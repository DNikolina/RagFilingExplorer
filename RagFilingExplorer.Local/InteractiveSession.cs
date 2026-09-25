using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using RagFilingExplorer.Local.Retrieval;
using RagFilingExplorer.Local.VectorStore;

namespace RagFilingExplorer.Local;

/// <summary>
/// The interactive question loop: reads a question, asks <see cref="RagAnswerService"/>, and prints the
/// filter line, the (verbose) ranked candidates, and the streamed answer.
/// </summary>
internal static class InteractiveSession
{
    public static async Task RunAsync(RagAnswerService ragAnswerService, bool verbose, RetrievalSettings retrieval)
    {
        Console.WriteLine();
        Console.WriteLine("=== Retrieval + answer generation ===");
        Console.WriteLine("Ask a question about the filings (blank line or 'exit' to quit).");
        if (!verbose)
        {
            Console.WriteLine("Run with --verbose to see the full ranked candidate list for each question.");
        }

        while (true)
        {
            Console.WriteLine();
            Console.Write("> ");
            string? question = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(question) || question.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            // Broad catch deliberately: this is a live call to an external service (Ollama), which can fail
            // in ways this app can't predict (a starved-reasoning response, a model rejecting an unsupported
            // option, a dropped connection, ...) - confirmed the hard way when an unhandled OllamaException
            // from mid-stream took down the entire interactive session over what should have been one bad
            // turn. One failed question should never end the session; report it and keep going.
            try
            {
                RagAnswer answer = await ragAnswerService.AskAsync(question, searchTopK: verbose ? retrieval.VerboseSearchTopK : retrieval.DefaultSearchTopK);
                string? filterLine = FormatMatchedFilter(answer);
                if (filterLine is not null)
                {
                    Console.WriteLine(filterLine);
                }

                if (answer.UsedReasoningEffort != ReasoningEffort.None)
                {
                    Console.WriteLine($"(reasoning: {answer.UsedReasoningEffort} - question matched RequiresSynthesis)");
                }

                if (answer.RetrievedChunks.Count == 0)
                {
                    Console.WriteLine("(no results)");
                    continue;
                }

                if (verbose)
                {
                    PrintRetrievedChunks(answer.RetrievedChunks);
                }

                await StreamAnswerAsync(answer.AnswerStream, verbose);
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine($"[error] {ex.Message}");
            }

            Console.WriteLine();
        }
    }

    /// <summary>
    /// The "(filtering to ..., statement type: ...)" line printed before each answer - null when the
    /// question resolved to no filter at all. The manual pass checks this line on every question, and
    /// tools/replay_recall.py parses it out of a --verbose run log, so its format is load-bearing.
    /// </summary>
    internal static string? FormatMatchedFilter(RagAnswer answer)
    {
        List<string> parts = new();
        if (answer.MatchedFilings.Count == 1)
        {
            parts.Add($"filtering to {answer.MatchedFilings[0]}");
        }
        else if (answer.MatchedFilings.Count > 1)
        {
            parts.Add($"searching {string.Join(" and ", answer.MatchedFilings)} separately");
        }

        if (answer.MatchedStatementType is not null)
        {
            parts.Add(parts.Count == 0 ? $"filtering to statement type: {answer.MatchedStatementType}" : $"statement type: {answer.MatchedStatementType}");
        }

        return parts.Count > 0 ? $"({string.Join(", ", parts)})" : null;
    }

    // Reasoning content (a reasoning model's "thinking", separate from its final answer - see
    // AppSettings.RetrievalSettings.ReasoningEffort) is only shown under --verbose, under its own header,
    // printed lazily so a plain lookup that never reasons looks exactly like it did before this existed.
    private static async Task StreamAnswerAsync(IAsyncEnumerable<ChatResponseUpdate> answerStream, bool verbose)
    {
        bool printedReasoningHeader = false;
        bool printedAnswerHeader = false;

        await foreach (ChatResponseUpdate update in answerStream)
        {
            if (verbose)
            {
                foreach (AIContent content in update.Contents)
                {
                    if (content is TextReasoningContent { Text.Length: > 0 } reasoning)
                    {
                        if (!printedReasoningHeader)
                        {
                            Console.WriteLine();
                            Console.WriteLine("--- Reasoning (verbose) ---");
                            printedReasoningHeader = true;
                        }

                        Console.Write(reasoning.Text);
                    }
                }
            }

            if (update.Text.Length > 0)
            {
                if (!printedAnswerHeader)
                {
                    Console.WriteLine();
                    Console.WriteLine("--- Answer ---");
                    printedAnswerHeader = true;
                }

                Console.Write(update.Text);
            }
        }
    }

    // --verbose: print the full ranked candidate list (compact) to see where the "right" chunk actually
    // lands - useful when diagnosing a retrieval miss, not needed for normal use.
    private static void PrintRetrievedChunks(IReadOnlyList<VectorSearchResult<FilingChunkRecord>> retrievedChunks)
    {
        Console.WriteLine();
        Console.WriteLine($"--- Retrieved chunks (top {retrievedChunks.Count}, compact, for diagnosis) ---");
        for (int i = 0; i < retrievedChunks.Count; i++)
        {
            FilingChunkRecord record = retrievedChunks[i].Record;
            string snippet = record.Content.Length > 90 ? record.Content[..90].ReplaceLineEndings(" ") : record.Content.ReplaceLineEndings(" ");
            Console.WriteLine($"[{i + 1}] score={retrievedChunks[i].Score:F4} | {record.SourceFiling} | {record.StatementType} | {record.Heading} | {snippet}");
        }
    }
}
