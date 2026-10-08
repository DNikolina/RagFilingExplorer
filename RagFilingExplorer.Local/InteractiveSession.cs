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

            // Broad catch deliberately: this is a live call to an external service (the chat model), which can fail
            // in ways this app can't predict (a starved-reasoning response, a refusal, a model rejecting an unsupported
            // option, a dropped connection, ...), and an exception from mid-stream would otherwise end the
            // whole session. One failed question should never end the session; report it and keep going.
            try
            {
                RagAnswer answer = await ragAnswerService.AskAsync(question, searchTopK: verbose ? retrieval.VerboseSearchTopK : retrieval.DefaultSearchTopK);
                string? filterLine = FormatMatchedFilter(answer);
                if (filterLine is not null)
                {
                    Console.WriteLine(filterLine);
                }

                if (verbose && answer.Search == SearchMode.Hybrid)
                {
                    Console.WriteLine(FormatKeywords(answer));
                }

                if (answer.UsedReasoningEffort != ReasoningEffort.None)
                {
                    Console.WriteLine($"(reasoning: {answer.UsedReasoningEffort})");
                }

                if (answer.RetrievedChunks.Count == 0)
                {
                    Console.WriteLine("(no results)");
                    continue;
                }

                if (verbose)
                {
                    PrintRetrievedChunks(answer);
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
    /// tools/replay_recall.py parses it out of a --verbose run log, so its format is load-bearing. Under hybrid
    /// search the statement type boosts instead of filtering, and the line says so ("boosting statement type: ...").
    /// </summary>
    internal static string? FormatMatchedFilter(RagAnswer answer)
    {
        List<string> parts = [];
        if (answer.MatchedFilings.Count == 1)
        {
            parts.Add($"filtering to {answer.MatchedFilings[0]}");
        }
        else if (answer.MatchedFilings.Count > 1)
        {
            parts.Add($"searching {string.Join(" and ", answer.MatchedFilings)} separately");
        }

        if (answer.MatchedStatementType is not null && answer.Search == SearchMode.Hybrid)
        {
            parts.Add($"boosting statement type: {answer.MatchedStatementType}");
        }
        else if (answer.MatchedStatementType is not null)
        {
            parts.Add(parts.Count == 0 ? $"filtering to statement type: {answer.MatchedStatementType}" : $"statement type: {answer.MatchedStatementType}");
        }

        return parts.Count > 0 ? $"({string.Join(", ", parts)})" : null;
    }

    /// <summary>
    /// The verbose "(keywords: ...)" line under hybrid search - the FTS5 query the keyword search ran. tools/replay_recall.py
    /// reads it back from the log (as it does the filter line) to replay the keyword search, and its presence is how
    /// the replay knows a run was hybrid.
    /// </summary>
    internal static string FormatKeywords(RagAnswer answer) => $"(keywords: {answer.KeywordQuery ?? "none"})";

    // Reasoning content (a reasoning model's "thinking", separate from its final answer - see
    // AppSettings.RetrievalSettings.ReasoningEffort) is only shown under --verbose, under its own header,
    // printed lazily so a plain lookup that never reasons shows no reasoning header at all.
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
    private static void PrintRetrievedChunks(RagAnswer answer)
    {
        IReadOnlyList<VectorSearchResult<FilingChunkRecord>> retrievedChunks = answer.RetrievedChunks;
        Console.WriteLine();
        Console.WriteLine($"--- Retrieved chunks (top {retrievedChunks.Count}, compact, for diagnosis) ---");
        for (int i = 0; i < retrievedChunks.Count; i++)
        {
            FilingChunkRecord record = retrievedChunks[i].Record;
            string snippet = record.Content[..Math.Min(90, record.Content.Length)].ReplaceLineEndings(" ");
            Console.WriteLine($"[{i + 1}] {FormatScore(retrievedChunks[i], answer.Reranked)} | {record.SourceFiling} | {record.StatementType} | {record.Heading} | {snippet}");
        }
    }

    /// <summary>
    /// A retrieved chunk's scores in the verbose list: "score=0.0325" (fused, or a distance under vector search), and when
    /// reranked, " rerank=3.86 hybrid=#3" - the cross-encoder's score and the rank its company's hybrid search gave it.
    /// "score=" stays the fused score, so the chunk marked hybrid=#1 is still the one tools/replay_recall.py's top score
    /// can be checked against.
    /// </summary>
    internal static string FormatScore(VectorSearchResult<FilingChunkRecord> result, IReadOnlyDictionary<int, RerankedChunk>? reranked) =>
        reranked is not null && reranked.TryGetValue(result.Record.Key, out RerankedChunk? r)
            ? $"score={result.Score:F4} rerank={r.Score:F2} hybrid=#{r.HybridRank}"
            : $"score={result.Score:F4}";
}
