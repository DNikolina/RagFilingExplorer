using System.Reflection;
using Microsoft.Extensions.Configuration;
using RagFilingExplorer.Local.Evaluation.Judging;

namespace RagFilingExplorer.Local.Evaluation.Running;

/// <summary>
/// How an evaluation run is set up - <c>RagFilingExplorer.Local.Evaluation/evalsettings.json</c>, the evaluation's
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

    /// <summary>Which grader(s) a run uses: "strict" (the strict grade), "judge" (the model judges in
    /// <see cref="Judges"/>), or "both". Answer rank and figure source always run - they need no model.</summary>
    public required string Graders { get; set; }

    /// <summary>The model judges, comma-separated - equivalence, groundedness - used when <see cref="Graders"/> includes them.</summary>
    public required string Judges { get; set; }

    /// <summary>The execution name; empty: structured-hybrid-&lt;yyyyMMddTHHmm&gt;. The same name replaces that run.</summary>
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

    public bool UsesStrictGrade => Graders is "strict" or "both";

    public bool UsesJudges => Graders is "judge" or "both";

    /// <summary>The judges a run asks - none unless <see cref="Graders"/> includes them.</summary>
    public IReadOnlyList<string> JudgeNames => UsesJudges ? Lower(Judges) : [];

    public IReadOnlyList<string> SetNames => List(Sets);

    public IReadOnlyList<string> OnlyIds => List(Only);

    public IReadOnlyList<string> CompareExecutions => List(Compare);

    /// <summary>evalsettings.json in the evaluation project, with environment variables over it.</summary>
    public static EvaluationSettings Load(DirectoryInfo repoRoot) =>
        From(new ConfigurationBuilder()
            .SetBasePath(Path.Combine(repoRoot.FullName, "RagFilingExplorer.Local.Evaluation"))
            .AddJsonFile(FileName, optional: false)
            .AddEnvironmentVariables()
            .Build());

    /// <summary>The settings from a configuration - every key present, every value valid, or an error naming it.</summary>
    public static EvaluationSettings From(IConfiguration configuration)
    {
        IConfigurationSection section = configuration.GetSection(Section);
        string[] missing = typeof(EvaluationSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .Select(p => p.Name)
            .Where(name => section[name] is null)
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException($"{FileName} is missing required key(s): {string.Join(", ", missing.Select(m => $"{Section}:{m}"))}.");
        }

        EvaluationSettings settings = section.Get<EvaluationSettings>()
            ?? throw new InvalidOperationException($"{FileName} failed to bind its {Section} section.");
        settings.Graders = settings.Graders.Trim().ToLowerInvariant();
        settings.Validate();
        return settings;
    }

    private void Validate()
    {
        List<string> errors = new();
        if (Graders is not ("strict" or "judge" or "both"))
        {
            errors.Add($"Graders is \"{Graders}\" - one of strict, judge, both");
        }

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
