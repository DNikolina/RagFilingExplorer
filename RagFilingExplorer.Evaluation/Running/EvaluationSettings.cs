using Microsoft.Extensions.Configuration;
using RagFilingExplorer.Evaluation.Judging;

namespace RagFilingExplorer.Evaluation.Running;

/// <summary>
/// How an evaluation run is set up - <c>RagFilingExplorer.Evaluation/evalsettings.json</c>, the evaluation's
/// counterpart of the app's appsettings.json - one configuration style for both. The file holds the defaults; an environment variable overrides one key for one run with
/// the same name - <c>Evaluation__NoCache=true</c> - which is how tools/run-variance.ps1 and run-judge.ps1 set a pass.
/// Every key is required and checked at load, as AppSettings does: no default hides in code.
///
/// Lists are comma-separated strings, not JSON arrays: an environment variable overrides an array item by item
/// (<c>Evaluation__Judges__0</c>), so a two-item list in the file overridden with one item would quietly keep the second.
/// </summary>
internal sealed class EvaluationSettings
{
    public const string FileName = "evalsettings.json";
    public const string Section = "Evaluation";

    /// <summary>
    /// Which chat model answers: Local (the app's Ollama model, appsettings.json) or Claude (RagFilingExplorer.Claude's
    /// claudesettings.json; the key from user secrets or ANTHROPIC_API_KEY, every answer billed). Embeddings, the index and retrieval are
    /// Local's either way. The response cache is keyed by the model (the library adds the chat client's provider and
    /// model id), so one model's cached answers never replay for another.
    /// </summary>
    public required ChatModel ChatModel { get; set; }

    /// <summary>Which grader(s) a run uses: strict (the strict grade), judge (the model judges in <see cref="Judges"/>),
    /// or both. Answer rank and figure source always run - they need no model. Bound as an enum, ignoring case, so a
    /// typo fails at load.</summary>
    public required Graders Graders { get; set; }

    /// <summary>The model judges, comma-separated - equivalence, groundedness - used when <see cref="Graders"/> includes them.</summary>
    public required string Judges { get; set; }

    /// <summary>The execution name; empty: structured-hybrid-&lt;yyyyMMddTHHmm&gt;. Reusing a name overwrites the questions
    /// asked again and keeps the rest of that run (the summary lists them) - use a new name to keep runs apart.</summary>
    public required string Execution { get; set; }

    /// <summary>Question sets, comma-separated - Main, HeldOut, AnswerSide; empty: all three.</summary>
    public required string Sets { get; set; }

    /// <summary>Question ids, comma-separated; empty: every question of the sets. A smoke run first.</summary>
    public required string Only { get; set; }

    /// <summary>Ask the model afresh and cache nothing - a variance pass.</summary>
    public required bool NoCache { get; set; }

    /// <summary>Unload the chat model before every question (with <see cref="NoCache"/>).</summary>
    public required bool UnloadEachQuestion { get; set; }

    /// <summary>How long a cached response is kept, in days.</summary>
    public required int CacheTimeToLiveDays { get; set; }

    /// <summary>Executions the variance comparison reads, comma-separated (VarianceComparisonTests).</summary>
    public required string Compare { get; set; }

    /// <summary>The variance comparison's file name, without .txt.</summary>
    public required string CompareName { get; set; }

    /// <summary>The run the judge agreement report reads (JudgeAgreementTests).</summary>
    public required string JudgeExecution { get; set; }

    public bool UsesStrictGrade => Graders is Graders.Strict or Graders.Both;

    public bool UsesJudges => Graders is Graders.Judge or Graders.Both;

    /// <summary>The judges a run asks - none unless <see cref="Graders"/> includes them.</summary>
    public IReadOnlyList<string> JudgeNames => UsesJudges ? Lower(Judges) : [];

    public IReadOnlyList<string> SetNames => List(Sets);

    public IReadOnlyList<string> OnlyIds => List(Only);

    public IReadOnlyList<string> CompareExecutions => List(Compare);

    /// <summary>evalsettings.json in the evaluation project, with environment variables over it.</summary>
    public static EvaluationSettings Load(DirectoryInfo repoRoot) =>
        From(new ConfigurationBuilder()
            .SetBasePath(Path.Combine(repoRoot.FullName, "RagFilingExplorer.Evaluation"))
            .AddJsonFile(FileName, optional: false)
            .AddEnvironmentVariables()
            .Build());

    /// <summary>The settings from a configuration - every key present, every value valid, or an error naming it.</summary>
    public static EvaluationSettings From(IConfiguration configuration)
    {
        AppSettings.EnsureKeysPresent(configuration, typeof(EvaluationSettings), Section, FileName);
        EvaluationSettings settings = configuration.GetSection(Section).Get<EvaluationSettings>()
            ?? throw new InvalidOperationException($"{FileName} failed to bind its {Section} section.");
        settings.Validate();
        return settings;
    }

    private void Validate()
    {
        List<string> errors = [];
        List<string> unknownJudges = Lower(Judges).Where(j => !JudgeSetup.Known.Contains(j)).ToList();
        if (unknownJudges.Count > 0)
        {
            errors.Add($"Judges names {string.Join(", ", unknownJudges)} - known: {string.Join(", ", JudgeSetup.Known)}");
        }

        if (UsesJudges && Lower(Judges).Count == 0)
        {
            errors.Add($"Graders is \"{Graders}\" but Judges is empty");
        }

        List<string> unknownSets = SetNames.Where(s => EvaluationRunner.AllSets.All(a => !a.Name.Equals(s, StringComparison.OrdinalIgnoreCase))).ToList();
        if (unknownSets.Count > 0)
        {
            errors.Add($"Sets names {string.Join(", ", unknownSets)} - the sets are {string.Join(", ", EvaluationRunner.AllSets.Select(s => s.Name))}");
        }

        if (UnloadEachQuestion && !NoCache)
        {
            errors.Add("UnloadEachQuestion without NoCache would unload the model for cached answers");
        }

        // The judge runs with a larger context window than the app, and Ollama reloads the model when it changes - with
        // fresh answers that's two reloads per question, so the pass no longer measures the app as it runs.
        if (UsesJudges && NoCache)
        {
            errors.Add($"Graders \"{Graders}\" with NoCache would reload the model around every answer - judge cached answers instead");
        }

        // The judge is the local model with Ollama's context option, and unloading is Ollama's - neither applies to
        // Claude, and a judge there would bill a second model call per answer.
        if (ChatModel == ChatModel.Claude && UsesJudges)
        {
            errors.Add($"Graders \"{Graders}\" with ChatModel Claude - the judge is the local model's; grade Claude's answers strictly");
        }

        if (ChatModel == ChatModel.Claude && UnloadEachQuestion)
        {
            errors.Add("UnloadEachQuestion with ChatModel Claude - only an Ollama model is unloaded");
        }

        if (CacheTimeToLiveDays <= 0)
        {
            errors.Add("CacheTimeToLiveDays must be positive");
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"{FileName}: {string.Join("; ", errors)}.");
        }
    }

    private static List<string> List(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static List<string> Lower(string value) => List(value).Select(v => v.ToLowerInvariant()).ToList();
}

/// <summary>The values <see cref="EvaluationSettings.Graders"/> accepts.</summary>
internal enum Graders
{
    Strict,
    Judge,
    Both,
}

/// <summary>The values <see cref="EvaluationSettings.ChatModel"/> accepts.</summary>
internal enum ChatModel
{
    Local,
    Claude,
}
