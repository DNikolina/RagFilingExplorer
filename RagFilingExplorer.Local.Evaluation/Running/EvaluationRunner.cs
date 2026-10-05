using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Reporting;
using Microsoft.Extensions.AI.Evaluation.Reporting.Formats.Html;
using Microsoft.Extensions.AI.Evaluation.Reporting.Storage;
using OllamaSharp;
using RagFilingExplorer.Local.Evaluation.Evaluators;
using RagFilingExplorer.Local.Evaluation.Grading;
using RagFilingExplorer.Local.Retrieval;

namespace RagFilingExplorer.Local.Evaluation.Running;

/// <summary>One question set: its file in tools/ and the name its scenarios are grouped under in the report.</summary>
internal sealed record QuestionSet(string Name, string File);

/// <summary>One question's outcome in a run. <see cref="FigureSource"/> is the figure-source status, <see cref="FigureTrace"/>
/// where each stated figure was found.</summary>
internal sealed record QuestionOutcome(
    string Set, string Id, StrictGrade Grade, int? Rank, string Answer, TimeSpan Elapsed, string FigureSource = "", string FigureTrace = "");

/// <summary>
/// A chat client whose inner client is switched per scenario: the app's answer service is built once, and each question
/// is answered through its own scenario's caching client (ScenarioRun.ChatConfiguration), so every response is cached
/// under its scenario.
/// </summary>
internal sealed class ScenarioChatClient : IChatClient
{
    public IChatClient? Inner { get; set; }

    private IChatClient Current => Inner ?? throw new InvalidOperationException("No scenario's chat client is set.");

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        Current.GetResponseAsync(messages, options, cancellationToken);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        Current.GetStreamingResponseAsync(messages, options, cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) => Current.GetService(serviceType, serviceKey);

    public void Dispose()
    {
    }
}

/// <summary>
/// v3 step 4: an evaluation run - the app as configured (AppComposition), every question of the given sets asked
/// in-process, each answer graded (<see cref="StrictFigureEvaluator"/>), its retrieval ranked
/// (<see cref="RetrievalRankEvaluator"/>) and its figures traced to the excerpts (<see cref="FigureSourceEvaluator"/>,
/// step 5), stored on disk as one scenario per question under one execution name, with
/// the model's responses cached, and an HTML report written from the stored results at the end. A null
/// <paramref name="cacheTimeToLive"/> asks the model afresh and caches nothing - a variance pass, which must neither replay
/// the cache (keyed by scenario, so shared by every execution) nor overwrite it. <paramref name="unloadBeforeEachQuestion"/>
/// unloads the chat model before every question, so none starts from a prompt prefix Ollama still holds - the variance
/// measurement's check on whether that reuse causes the drift.
/// </summary>
internal sealed class EvaluationRunner(
    DirectoryInfo repoRoot, string storageRoot, string executionName, TimeSpan? cacheTimeToLive, bool unloadBeforeEachQuestion = false)
{
    public static readonly IReadOnlyList<QuestionSet> AllSets =
    [
        new("Main", "manual-questions.txt"),
        new("HeldOut", "heldout-questions.txt"),
        new("AnswerSide", "answer-questions.txt"),
    ];

    /// <summary>Runs the sets (only the listed ids, when <paramref name="onlyIds"/> isn't empty) and writes report.html.</summary>
    public async Task<List<QuestionOutcome>> RunAsync(
        IReadOnlyList<QuestionSet> sets, IReadOnlySet<string> onlyIds, Action<string> progress, CancellationToken cancellationToken = default)
    {
        AppSettings settings = AppSettings.Load(Path.Combine(repoRoot.FullName, "RagFilingExplorer.Local"));
        IReadOnlyDictionary<string, ExpectedAnswer> expected = ExpectedAnswer.LoadAll(repoRoot);
        Dictionary<string, ExpectedAnswer> byQuestion = expected.Values.ToDictionary(e => e.Question);

        // The model's own client; the reporting configuration wraps it per scenario with a response cache.
        (_, OllamaSharp.OllamaApiClient chat) = AppComposition.CreateOllamaClients(settings.Ollama);
        ReportingConfiguration reporting = DiskBasedReportingConfiguration.Create(
            storageRootPath: storageRoot,
            evaluators:
            [
                new StrictFigureEvaluator(),
                new RetrievalRankEvaluator(settings.Retrieval.GenerationTopK),
                new FigureSourceEvaluator(settings.Retrieval.GenerationTopK),
            ],
            chatConfiguration: new ChatConfiguration(chat),
            enableResponseCaching: cacheTimeToLive is not null,
            timeToLiveForCacheEntries: cacheTimeToLive,
            executionName: executionName);

        ScenarioChatClient scenarioChat = new();
        using RagRuntime runtime = await AppComposition.OpenExistingIndexAsync(settings, repoRoot, scenarioChat);

        List<QuestionOutcome> outcomes = new();
        foreach (QuestionSet set in sets)
        {
            foreach (string question in File.ReadAllLines(Path.Combine(repoRoot.FullName, "tools", set.File)).Where(q => q.Trim().Length > 0))
            {
                ExpectedAnswer entry = byQuestion[question.Trim()];
                if (onlyIds.Count > 0 && !onlyIds.Contains(entry.Id))
                {
                    continue;
                }

                Stopwatch clock = Stopwatch.StartNew();
                await using ScenarioRun scenario = await reporting.CreateScenarioRunAsync($"{set.Name}.{entry.Id}", cancellationToken: cancellationToken);
                scenarioChat.Inner = scenario.ChatConfiguration!.ChatClient;
                if (unloadBeforeEachQuestion)
                {
                    await chat.RequestModelUnloadAsync(settings.Ollama.ChatModel, cancellationToken);
                }

                RagAnswer answer = await runtime.AnswerService.AskAsync(question, settings.Retrieval.VerboseSearchTopK, cancellationToken);
                StringBuilder text = new();
                await foreach (ChatResponseUpdate update in answer.AnswerStream.WithCancellation(cancellationToken))
                {
                    text.Append(update.Text);
                }

                string answerText = text.ToString().Trim();
                List<RetrievedExcerpt> chunks = answer.RetrievedChunks
                    .Select(r => new RetrievedExcerpt(r.Record.SourceFiling, r.Record.Heading, r.Record.StatementType, r.Record.Content))
                    .ToList();
                EvaluationResult result = await scenario.EvaluateAsync(
                    [new ChatMessage(ChatRole.User, question)],
                    new ChatResponse(new ChatMessage(ChatRole.Assistant, answerText)),
                    additionalContext: [new ExpectedAnswerContext(entry), new RetrievedChunksContext(chunks)],
                    cancellationToken);

                StrictGrade grade = new(
                    result.Get<StringMetric>(StrictFigureEvaluator.MetricName).Value!,
                    result.Get<StringMetric>(StrictFigureEvaluator.MetricName).Reason ?? "");
                double? rank = result.Get<NumericMetric>(RetrievalRankEvaluator.MetricName).Value;
                StringMetric source = result.Get<StringMetric>(FigureSourceEvaluator.MetricName);
                QuestionOutcome outcome = new(set.Name, entry.Id, grade, rank is null ? null : (int)rank, answerText, clock.Elapsed, source.Value!, source.Reason ?? "");
                outcomes.Add(outcome);
                progress($"{set.Name}.{entry.Id}: {grade.Status}{(grade.Note.Length > 0 ? $" ({grade.Note})" : "")}, rank {outcome.Rank?.ToString() ?? "-"}, "
                    + $"figures {outcome.FigureSource}, {clock.Elapsed.TotalSeconds:F0}s");
            }
        }

        scenarioChat.Inner = null;
        await WriteReportAsync(cancellationToken);
        return outcomes;
    }

    /// <summary>report-&lt;execution&gt;.html in the storage root, from this execution's stored results, and report.html from
    /// every execution in the store - the history.</summary>
    public async Task WriteReportAsync(CancellationToken cancellationToken = default)
    {
        await WriteReportAsync(storageRoot, Path.Combine(storageRoot, $"report-{executionName}.html"), executionName, cancellationToken);
        await WriteReportAsync(storageRoot, Path.Combine(storageRoot, "report.html"), executionName: null, cancellationToken);
    }

    /// <summary>An HTML report of the stored results - one execution's, or every execution's when <paramref name="executionName"/> is null.</summary>
    public static async Task WriteReportAsync(string storageRoot, string reportPath, string? executionName, CancellationToken cancellationToken = default)
    {
        DiskBasedResultStore store = new(storageRoot);
        List<ScenarioRunResult> results = new();
        await foreach (ScenarioRunResult result in store.ReadResultsAsync(executionName, cancellationToken: cancellationToken))
        {
            results.Add(result);
        }

        // The report lists executions in the order they first appear in its data, and opens on the first one. Oldest
        // first (user, 2026-10-02): each execution kept together, ordered by when it ran - so the report opens on the
        // oldest run; its comparison view still sets the newest against the one before it.
        Dictionary<string, DateTime> ranAt = results.GroupBy(r => r.ExecutionName).ToDictionary(g => g.Key, g => g.Min(r => r.CreationTime));
        List<ScenarioRunResult> ordered = results
            .OrderBy(r => ranAt[r.ExecutionName]).ThenBy(r => r.ExecutionName, StringComparer.Ordinal)
            .ThenBy(r => r.ScenarioName, StringComparer.Ordinal)
            .ToList();

        await new HtmlReportWriter(reportPath).WriteReportAsync(ordered, cancellationToken);
    }
}
