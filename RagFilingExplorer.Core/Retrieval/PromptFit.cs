using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.ML.Tokenizers;
using RagFilingExplorer.Core.VectorStore;

namespace RagFilingExplorer.Core.Retrieval;

/// <summary>
/// Whether the largest prompt an index can produce fits a chat model's context window. Past the window Ollama doesn't
/// fail, it drops the oldest tokens - the system prompt and the top-ranked excerpts - and answers anyway, so a big-chunk
/// index (rag.structured.voyage-finance-2.10000tok.db) asked of llama at 4,096 would answer from a gutted prompt. Checked
/// at startup against the index's own chunks: the GenerationTopK largest, laid out as <see cref="RagAnswerService.BuildPrompt"/>
/// lays them out, plus the output budget the window must also hold.
/// </summary>
internal static class PromptFit
{
    // A fixed allowance for the question and the chat template's per-message tokens (questions here run 10-40
    // tokens). The prompt is counted with the chunker's tokenizer (cl100k); llama 3's 128K vocabulary takes in cl100k's
    // 100K, so it should count no more. Count each real prompt instead if a model with a costlier tokenizer is checked.
    internal const int QuestionAllowance = 100;

    /// <summary>The largest prompt, in tokens: the <paramref name="topK"/> excerpts that are longest once laid out, the
    /// system prompt, and <see cref="QuestionAllowance"/>.</summary>
    public static int LargestPromptTokens(Tokenizer tokenizer, IEnumerable<FilingChunkRecord> chunks, int topK)
    {
        IEnumerable<FilingChunkRecord> largest = chunks
            .OrderByDescending(c => tokenizer.CountTokens(RagAnswerService.BuildPrompt("", [c])[1].Text))
            .Take(topK);
        return RagAnswerService.BuildPrompt("", largest).Sum(m => tokenizer.CountTokens(m.Text)) + QuestionAllowance;
    }

    /// <summary>Refuses a window that can't hold <paramref name="promptTokens"/> plus the answer, naming the fixes.</summary>
    public static void EnsureFits(int promptTokens, int maxOutputTokens, int contextWindow, string indexName, int topK)
    {
        if (promptTokens + maxOutputTokens > contextWindow)
        {
            throw new StartupException(
                $"{indexName}'s largest prompt ({topK} excerpts) is ~{promptTokens:N0} tokens, plus {maxOutputTokens:N0} for the answer "
                + $"(Retrieval:MaxOutputTokens) - more than the chat model's window, Ollama:ContextLength {contextWindow:N0}. Past it "
                + "Ollama silently drops the start of the prompt. Raise Ollama:ContextLength (memory allowing), lower "
                + "Retrieval:GenerationTopK, or use an index with smaller chunks (Chunking:MaxTokensPerChunk).");
        }
    }

    /// <summary>Every chunk in the index - only the text the prompt holds (the vectors aren't read).</summary>
    public static List<FilingChunkRecord> ReadChunks(IndexFiles index)
    {
        using SqliteConnection connection = new($"Data Source={index.DbPath};Mode=ReadOnly");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT SourceFiling, Heading, Content FROM chunks";
        using SqliteDataReader reader = command.ExecuteReader();
        List<FilingChunkRecord> chunks = [];
        while (reader.Read())
        {
            chunks.Add(new FilingChunkRecord { SourceFiling = reader.GetString(0), Heading = reader.GetString(1), Content = reader.GetString(2) });
        }

        return chunks;
    }
}
