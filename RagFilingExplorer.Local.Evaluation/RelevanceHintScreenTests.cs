using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using RagFilingExplorer.Local.Evaluation.Evaluators;
using RagFilingExplorer.Local.Evaluation.Grading;
using RagFilingExplorer.Local.Retrieval;

namespace RagFilingExplorer.Local.Evaluation;

/// <summary>
/// Screen: does telling the model what the excerpts' order means help? Each question is asked through
/// the app as usual, but with a chat client that records the exact messages and options instead of sending them; those
/// are then sent to Ollama twice - unchanged (must reproduce today's variance pass word for word) and with the hint - and
/// both answers graded. Plan, targets, controls and bar: Decision-Log, "Screen: tell the model what the excerpts' order
/// means". [Explicit]: Ollama and ~35-40 minutes; writes eval/relevance-hint-screen/results.txt.
/// </summary>
[TestFixture]
[Explicit("Needs Ollama and about 40 minutes.")]
public class RelevanceHintScreenTests
{
    private static readonly DirectoryInfo Repo = RepoPaths.FindRoot(AppContext.BaseDirectory);

    private static readonly string[] Targets = ["A10", "A14", "A15", "T6"];
    private static readonly string[] Controls = ["Q1", "Q4", "Q10", "Q24", "A1", "A2", "A9", "A13", "H6", "H16", "R1", "T7"];

    private const string Hint = "The excerpts are ordered by retrieval relevance - the first is the closest match to the question, "
        + "by how similar its text is to the question. Relevance is similarity, not proof that an excerpt holds the answer.";

    /// <summary>Records what the app would send and sends nothing.</summary>
    private sealed class RecordingChatClient : IChatClient
    {
        public List<ChatMessage>? Messages { get; private set; }

        public ChatOptions? Options { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Messages = messages.ToList();
            Options = options;
            return Empty();
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> Empty([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>The variant: the hint after the system prompt, each header with its rank - the app's user message rebuilt
    /// from the same excerpts in the same layout (RagAnswerService), so nothing else differs.</summary>
    private static List<ChatMessage> WithHint(string systemPrompt, IReadOnlyList<RetrievedExcerpt> excerpts, string question)
    {
        StringBuilder context = new();
        for (int i = 0; i < excerpts.Count; i++)
        {
            context.AppendLine($"--- Excerpt from {excerpts[i].SourceFiling}, section {excerpts[i].Heading} (relevance rank {i + 1} of {excerpts.Count}) ---");
            context.AppendLine(excerpts[i].Content);
            context.AppendLine();
        }

        return
        [
            new ChatMessage(ChatRole.System, systemPrompt.TrimEnd() + "\n\n" + Hint),
            new ChatMessage(ChatRole.User, $"Context excerpts:\n\n{context}\nQuestion: {question}"),
        ];
    }

    private static async Task<string> AskAsync(IChatClient chat, IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        StringBuilder text = new();
        await foreach (ChatResponseUpdate update in chat.GetStreamingResponseAsync(messages, options))
        {
            text.Append(update.Text);
        }

        return text.ToString().Trim();
    }

    private static string Graded(ExpectedAnswer entry, string answer, IReadOnlyList<RetrievedExcerpt> given)
    {
        StrictGrade grade = StrictGrader.Grade(entry, answer);
        string figures = string.Join("; ", FigureSourceEvaluator.Trace(answer, entry.Question, given)
            .Select(f => $"{f.Figure} from {(f.Sightings.Count == 0 ? "no excerpt" : string.Join(",", f.Sightings.Select(s => s.Position)))}"));
        return $"{grade.Status}{(grade.Note.Length > 0 ? $" ({grade.Note})" : "")}; figures: {(figures.Length > 0 ? figures : "-")}";
    }

    [Test]
    public async Task Screen_RelevanceHint_TargetsAndControls()
    {
        AppSettings settings = AppSettings.Load(Path.Combine(Repo.FullName, "RagFilingExplorer.Local"));
        IReadOnlyDictionary<string, ExpectedAnswer> expected = ExpectedAnswer.LoadAll(Repo);
        (_, OllamaSharp.OllamaApiClient chat) = AppComposition.CreateOllamaClients(settings.Ollama);
        RecordingChatClient recorder = new();
        using RagRuntime runtime = await AppComposition.OpenExistingIndexAsync(settings, Repo, recorder);
        string ollama = (await chat.GetVersionAsync()).ToString();

        StringBuilder report = new();
        report.AppendLine($"Relevance-hint screen, {DateTime.Now:yyyy-MM-dd HH:mm}, Ollama {ollama}, {settings.Ollama.ChatModel}");
        report.AppendLine($"Hint: {Hint}");
        report.AppendLine();
        int reproduced = 0;
        List<string> fixedTargets = new(), lostControls = new();

        foreach (string id in Targets.Concat(Controls))
        {
            ExpectedAnswer entry = expected[id];
            RagAnswer answer = await runtime.AnswerService.AskAsync(entry.Question, settings.Retrieval.VerboseSearchTopK);
            List<RetrievedExcerpt> given = answer.RetrievedChunks.Take(settings.Retrieval.GenerationTopK)
                .Select(r => new RetrievedExcerpt(r.Record.SourceFiling, r.Record.Heading, r.Record.StatementType, r.Record.Content))
                .ToList();
            List<ChatMessage> sent = recorder.Messages!;

            string current = await AskAsync(chat, sent, recorder.Options);
            string hinted = await AskAsync(chat, WithHint(sent[0].Text, given, entry.Question), recorder.Options);

            string variancePath = Path.Combine(Repo.FullName, "eval", "v3-runs", "results", "structured-hybrid-v3-variance-1",
                $"{Running.EvaluationRunner.AllSets.First(s => File.ReadAllLines(Path.Combine(Repo.FullName, "tools", s.File)).Any(q => q.Trim() == entry.Question)).Name}.{id}", "1.json");
            string varianceAnswer = System.Text.Json.JsonDocument.Parse(File.ReadAllText(variancePath)).RootElement
                .GetProperty("modelResponse").GetProperty("messages")[0].GetProperty("contents")[0].GetProperty("text").GetString()!.Trim();
            bool same = varianceAnswer == current;
            reproduced += same ? 1 : 0;

            bool before = StrictGrader.Grade(entry, current).Passed, after = StrictGrader.Grade(entry, hinted).Passed;
            if (Targets.Contains(id) && !before && after)
            {
                fixedTargets.Add(id);
            }

            if (Controls.Contains(id) && before && !after)
            {
                lostControls.Add(id);
            }

            report.AppendLine($"{id} ({(Targets.Contains(id) ? "target" : "control")}){(same ? "" : "  [current prompt did NOT reproduce the variance pass]")}");
            report.AppendLine($"  current: {Graded(entry, current, given)}");
            report.AppendLine($"           {current.ReplaceLineEndings(" ")}");
            report.AppendLine($"  hinted:  {Graded(entry, hinted, given)}");
            report.AppendLine($"           {hinted.ReplaceLineEndings(" ")}");
            TestContext.Progress.WriteLine($"{id}: current {StrictGrader.Grade(entry, current).Status}, hinted {StrictGrader.Grade(entry, hinted).Status}{(same ? "" : " (not reproduced)")}");
        }

        report.AppendLine();
        report.AppendLine($"Current prompt reproduced the variance pass word for word: {reproduced}/{Targets.Length + Controls.Length}");
        report.AppendLine($"Targets fixed: {fixedTargets.Count}/{Targets.Length} {string.Join(", ", fixedTargets)}");
        report.AppendLine($"Controls lost: {lostControls.Count}/{Controls.Length} {string.Join(", ", lostControls)}");
        report.AppendLine($"Bar (at least one target fixed, no control lost): {(fixedTargets.Count > 0 && lostControls.Count == 0 ? "MET - a full run is warranted" : "not met")}");

        string folder = Path.Combine(Repo.FullName, "eval", "relevance-hint-screen");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "results.txt"), report.ToString());
        TestContext.Progress.WriteLine(report.ToString());
    }
}
