using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using RagFilingExplorer.Local.Evaluation.Evaluators;
using RagFilingExplorer.Local.Evaluation.Running;

namespace RagFilingExplorer.Local.Evaluation;

/// <summary>The conversation stored with each result for the report, offline.</summary>
[TestFixture]
public class PromptTranscriptTests
{
    private const string Question = "How much cash did Microsoft pay in common stock dividends in fiscal 2026?";

    private static readonly RetrievedExcerpt CashFlow = new("MSFT-10K-2026.html", "PART II > Item 8", "cash_flow_statement",
        "CASH FLOWS STATEMENTS\nFinancing > Common stock cash dividends paid — 2026: (26,445) | 2025: (24,082)");

    private static readonly List<ChatMessage> Prompt =
    [
        new(ChatRole.System, "You answer questions about SEC 10-K filings."),
        new(ChatRole.User, "Context excerpts:\n\n...\nQuestion: " + Question),
    ];

    [Test]
    public void Messages_SystemPromptExcerptsThenTheQuestionLast()
    {
        List<ChatMessage> messages = PromptTranscript.Messages(Prompt, [CashFlow], Question);

        Assert.That(messages.Select(m => m.Role), Is.EqualTo(new[] { ChatRole.System, ChatRole.System, ChatRole.User }));
        Assert.That(messages[0].Text, Is.EqualTo("You answer questions about SEC 10-K filings."), "the system prompt exactly as sent");
        Assert.That(messages[2].Text, Is.EqualTo(Question));
    }

    [Test]
    public void Messages_TheQuestionIsWhatTheEvaluatorsRead()
    {
        // Microsoft's evaluators take the last message, if it's the user's, as the request - the judge must read only the
        // question.
        Assert.That(PromptTranscript.Messages(Prompt, [CashFlow], Question).TryGetUserRequest(out ChatMessage? request), Is.True);
        Assert.That(request!.Text, Is.EqualTo(Question));
    }

    [Test]
    public void Excerpts_HeaderLinksTheFilingAndTheTextStaysInACodeBlock()
    {
        string text = PromptTranscript.Excerpts([CashFlow]);

        Assert.That(text, Does.Contain("--- Excerpt from [MSFT-10K-2026.html](../../data/MSFT-10K-2026.html), section PART II > Item 8 ---"));
        Assert.That(text, Does.Contain("```\nCASH FLOWS STATEMENTS\nFinancing > Common stock cash dividends paid — 2026: (26,445) | 2025: (24,082)\n```")
            .Or.Contain("```\r\nCASH FLOWS STATEMENTS\nFinancing > Common stock cash dividends paid — 2026: (26,445) | 2025: (24,082)\r\n```"));
    }

    [Test]
    public void Messages_NoPrompt_ExcerptsAndQuestionOnly()
    {
        Assert.That(PromptTranscript.Messages(null, [CashFlow], Question).Select(m => m.Role), Is.EqualTo(new[] { ChatRole.System, ChatRole.User }));
    }
}
