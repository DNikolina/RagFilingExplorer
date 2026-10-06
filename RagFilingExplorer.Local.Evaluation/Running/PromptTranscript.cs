using System.Text;
using Microsoft.Extensions.AI;
using RagFilingExplorer.Local.Evaluation.Evaluators;

namespace RagFilingExplorer.Local.Evaluation.Running;

/// <summary>
/// The conversation stored with each result - what the report's conversation view shows (it renders every message,
/// system ones included, as Markdown): the app's system prompt exactly as sent (<c>RagAnswer.Prompt</c>), the excerpts the
/// model was given, each headed as the app heads it with the filing's name linking to the filing in data/, and the
/// question. The app sends the excerpts and the question in one user message; here they're two, so the question stays
/// the last message - the only one Microsoft's evaluators read as the request (<c>TryGetUserRequest</c>: the last
/// message, if it's the user's), so a judge reads the question alone, not the excerpts.
/// </summary>
internal static class PromptTranscript
{
    // From eval/v3-runs/, where the reports are written, to data/ at the repo root.
    private const string FilingsFromReports = "../../data/";

    public static List<ChatMessage> Messages(IReadOnlyList<ChatMessage>? prompt, IReadOnlyList<RetrievedExcerpt> excerpts, string question)
    {
        List<ChatMessage> messages = new();
        if (prompt?.FirstOrDefault(m => m.Role == ChatRole.System) is { } system)
        {
            messages.Add(new ChatMessage(ChatRole.System, system.Text));
        }

        if (excerpts.Count > 0)
        {
            messages.Add(new ChatMessage(ChatRole.System, Excerpts(excerpts)));
        }

        messages.Add(new ChatMessage(ChatRole.User, question));
        return messages;
    }

    /// <summary>The excerpts as Markdown: the app's header per excerpt, the filing linked, the text in a code block - the
    /// report's Markdown would otherwise run a table's rows together.</summary>
    public static string Excerpts(IReadOnlyList<RetrievedExcerpt> excerpts)
    {
        StringBuilder text = new();
        text.AppendLine("Context excerpts the model was given - sent with the question in one message, shown apart here.");
        foreach (RetrievedExcerpt e in excerpts)
        {
            text.AppendLine();
            text.AppendLine($"--- Excerpt from [{e.SourceFiling}]({FilingsFromReports}{Uri.EscapeDataString(e.SourceFiling)}), section {e.Heading} ---");
            text.AppendLine("```");
            text.AppendLine(e.Content);
            text.AppendLine("```");
        }

        return text.ToString();
    }
}
