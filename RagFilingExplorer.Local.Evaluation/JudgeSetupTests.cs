using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation.Quality;
using OllamaSharp.Models;
using RagFilingExplorer.Local.Evaluation.Evaluators;
using RagFilingExplorer.Local.Evaluation.Grading;
using RagFilingExplorer.Local.Evaluation.Judging;

namespace RagFilingExplorer.Local.Evaluation;

/// <summary>The judge spike's setup (v3 step 6), offline: ground truth, grounding text, evaluators, the judge's context window.</summary>
[TestFixture]
public class JudgeSetupTests
{
    private static readonly DirectoryInfo Repo = RepoPaths.FindRoot(AppContext.BaseDirectory);

    [TestCase("A10", "$26,445 million")]
    [TestCase("A17", "$13,463,971 thousand")]
    [TestCase("A2", "$3.12")]
    [TestCase("H2", "19%")]
    [TestCase("A23", "1.23")]
    [TestCase("Q12", "Austin")]
    [TestCase("Q14", "$331,839 million and $67,357 million")]
    [TestCase("Q11", "223,000, 121,000 and 102,000")]
    public void GroundTruth_ExpectedAnswer_StatedWithItsUnit(string id, string groundTruth)
    {
        Assert.That(JudgeSetup.GroundTruth(ExpectedAnswer.LoadAll(Repo)[id]), Is.EqualTo(groundTruth));
    }

    [Test]
    public void GroundTruth_Negative_IsADecline()
    {
        ExpectedAnswer negative = ExpectedAnswer.LoadAll(Repo).Values.First(e => e.Kind == "negative");

        Assert.That(JudgeSetup.GroundTruth(negative), Does.StartWith("The filings don't contain this information"));
    }

    [Test]
    public void GroundingText_Excerpts_LaidOutAsThePromptShowsThem()
    {
        string text = JudgeSetup.GroundingText(
        [
            new RetrievedExcerpt("MSFT-10K-2026.html", "PART II > Item 8", "cash_flow_statement", "Dividends paid — 2026: (26,445)"),
            new RetrievedExcerpt("MSFT-10K-2026.html", "PART II > Item 7", "narrative", "Text."),
        ]);

        Assert.That(text, Is.EqualTo("--- Excerpt from MSFT-10K-2026.html, section PART II > Item 8 ---\nDividends paid — 2026: (26,445)\n\n"
            + "--- Excerpt from MSFT-10K-2026.html, section PART II > Item 7 ---\nText."));
    }

    [Test]
    public void Contexts_OnlyTheExcerptsTheModelWasGiven()
    {
        List<RetrievedExcerpt> excerpts = Enumerable.Range(1, 8).Select(i => new RetrievedExcerpt("F.html", $"S{i}", "narrative", $"chunk {i}")).ToList();

        GroundednessEvaluatorContext grounding = JudgeSetup.Contexts(ExpectedAnswer.LoadAll(Repo)["A10"], excerpts, 5)
            .OfType<GroundednessEvaluatorContext>().Single();

        Assert.That(grounding.GroundingContext, Does.Contain("chunk 5").And.Not.Contain("chunk 6"));
    }

    [Test]
    public void Evaluators_KnownNames_TheirEvaluators()
    {
        Assert.That(JudgeSetup.Evaluators([JudgeSetup.Equivalence]).Single(), Is.InstanceOf<EquivalenceEvaluator>());
        Assert.That(JudgeSetup.Evaluators([]), Is.Empty);
        Assert.Throws<ArgumentException>(() => JudgeSetup.Evaluators(["relevance"]));
    }

    private const string Unread = "Failed to parse numeric score for 'Equivalence' from the following text:\r\n";

    // The step 6 smoke test's two replies, word for word (A10, Q1).
    [TestCase(Unread + "The predicted answer is mostly similar to the correct answer, but with a slight difference in the amount. Therefore, the Equivalence score is 4.", 4)]
    [TestCase(Unread + "The predicted answer is mostly similar to the correct answer, as it provides the same numerical value and mentions the same date. However, it also includes additional information that is not present in the correct answer, such as the source of the data. Therefore, I would rate the Equivalence metric as 4 stars.", 4)]
    [TestCase(Unread + "Equivalence score: 2", 2)]
    [TestCase(Unread + "I'd give it 5/5.", 5)]
    [TestCase(Unread + "Rather than 5 stars, the score is 3.", 3)]   // the last phrase wins
    [TestCase(Unread + "The answers differ.", null)]
    [TestCase("The request timed out.", null)]                        // not an unread reply
    [TestCase(null, null)]
    public void RecoverScore_WrittenScore_TheLastScorePhrase(string? error, int? score)
    {
        Assert.That(JudgeSetup.RecoverScore(error), Is.EqualTo(score));
    }

    [Test]
    public async Task LibraryFailsAsync_EveryScore_TheLibrarysOwnVerdict()
    {
        // Pinned from the library (10.10.0): scores below 4 fail. A library upgrade that changes the rule fails this test
        // - a recovered score always gets whatever verdict the library gives the same score.
        bool[] equivalence = await Task.WhenAll(Enumerable.Range(1, 5).Select(s => JudgeSetup.LibraryFailsAsync("Equivalence", s)));
        bool[] groundedness = await Task.WhenAll(Enumerable.Range(1, 5).Select(s => JudgeSetup.LibraryFailsAsync("Groundedness", s)));

        Assert.That(equivalence, Is.EqualTo(new[] { true, true, true, false, false }));
        Assert.That(groundedness, Is.EqualTo(new[] { true, true, true, false, false }));
    }

    private sealed class CapturingChatClient : IChatClient
    {
        public List<ChatOptions?> Options { get; } = new();

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Options.Add(options);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "4")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    [Test]
    public async Task JudgeContextChatClient_RaisesTheContextOnlyWhileJudging()
    {
        CapturingChatClient inner = new();
        JudgeContextChatClient client = new(inner);
        ChatOptions appOptions = new() { Temperature = 0 };

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "q")], appOptions);
        client.JudgeContext = true;
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "q")], appOptions);

        Assert.That(inner.Options[0], Is.SameAs(appOptions), "the app's request goes through untouched");
        Assert.That(inner.Options[1]!.AdditionalProperties![OllamaOption.NumCtx.Name], Is.EqualTo(JudgeContextChatClient.JudgeContextTokens));
        Assert.That(inner.Options[1]!.Temperature, Is.EqualTo(0f), "the judge's own options are kept");
        Assert.That(appOptions.AdditionalProperties, Is.Null, "the caller's options aren't changed");
    }
}
