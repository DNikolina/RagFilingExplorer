using Microsoft.ML.Tokenizers;
using RagFilingExplorer.Core;
using RagFilingExplorer.Core.Retrieval;
using RagFilingExplorer.Core.VectorStore;

namespace RagFilingExplorer.Tests.Retrieval;

[TestFixture]
public class PromptFitTests
{
    private static Tokenizer tokenizer = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp() => tokenizer = TiktokenTokenizer.CreateForModel("gpt-4");

    private static FilingChunkRecord Chunk(int words) =>
        new() { SourceFiling = "MSFT-10K-2026.html", Heading = "PART II > Item 8", Content = string.Join(' ', Enumerable.Repeat("revenue", words)) };

    // The prompt counted is the one the model gets: the system prompt, the K longest excerpts laid out as the service lays
    // them out, and the question's allowance.
    [Test]
    public void LargestPromptTokens_TakesTheLongestExcerpts_CountedAsTheServiceLaysThemOut()
    {
        FilingChunkRecord[] chunks = [Chunk(10), Chunk(900), Chunk(50), Chunk(700)];

        int largest = PromptFit.LargestPromptTokens(tokenizer, chunks, topK: 2);

        int expected = RagAnswerService.BuildPrompt("", [chunks[1], chunks[3]]).Sum(m => tokenizer.CountTokens(m.Text)) + PromptFit.QuestionAllowance;
        Assert.That(largest, Is.EqualTo(expected));
        Assert.That(largest, Is.GreaterThan(1600), "both long excerpts counted");
    }

    [Test]
    public void EnsureFits_PromptAndAnswerWithinTheWindow_Passes()
    {
        Assert.DoesNotThrow(() => PromptFit.EnsureFits(promptTokens: 3200, maxOutputTokens: 768, contextWindow: 4096, "rag.structured.db", topK: 5));
    }

    // A 10,000-token index at llama's 4,096: Ollama would drop the start of the prompt and answer anyway.
    [Test]
    public void EnsureFits_PromptPastTheWindow_ThrowsNamingTheFixes()
    {
        StartupException ex = Assert.Throws<StartupException>(() =>
            PromptFit.EnsureFits(promptTokens: 9500, maxOutputTokens: 768, contextWindow: 4096, "rag.structured.voyage-finance-2.10000tok.db", topK: 5));

        Assert.That(ex.Message, Does.Contain("rag.structured.voyage-finance-2.10000tok.db").And.Contain("9,500")
            .And.Contain("Ollama:ContextLength").And.Contain("Retrieval:GenerationTopK").And.Contain("Chunking:MaxTokensPerChunk"));
    }
}
