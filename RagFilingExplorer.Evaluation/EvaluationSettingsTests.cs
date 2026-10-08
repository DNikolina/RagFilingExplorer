using Microsoft.Extensions.Configuration;
using RagFilingExplorer.Evaluation.Running;

namespace RagFilingExplorer.Evaluation;

/// <summary>evalsettings.json and its validation, offline - the shipped file, and in-memory overrides standing in for the
/// environment variables a run script sets.</summary>
[TestFixture]
public class EvaluationSettingsTests
{
    private static readonly DirectoryInfo Repo = RepoPaths.FindRoot(AppContext.BaseDirectory);

    /// <summary>The shipped file with <paramref name="overrides"/> over it ("Evaluation:Graders" = "both"), as
    /// Evaluation__Graders=both would.</summary>
    private static EvaluationSettings With(params (string Key, string? Value)[] overrides) =>
        EvaluationSettings.From(new ConfigurationBuilder()
            .SetBasePath(Path.Combine(Repo.FullName, "RagFilingExplorer.Evaluation"))
            .AddJsonFile(EvaluationSettings.FileName)
            .AddInMemoryCollection(overrides.Select(o => new KeyValuePair<string, string?>($"Evaluation:{o.Key}", o.Value)))
            .Build());

    [Test]
    public void Shipped_Defaults_StrictGradeOnAllQuestionsFromTheCache()
    {
        // The defaults are what a plain run does - Load reads the same file, with this process's environment over it.
        EvaluationSettings settings = With();

        Assert.That(settings.ChatModel, Is.EqualTo(ChatModel.Local));
        Assert.That(settings.UsesLocalJudge, Is.True, "the judge is the answering model unless a Claude model is named");
        Assert.That(settings.Graders, Is.EqualTo(Grader.Strict));
        Assert.That(settings.UsesStrictGrade, Is.True);
        Assert.That(settings.JudgeNames, Is.Empty, "judges are listed, but only asked when Graders includes them");
        Assert.That(settings.Judges, Is.EqualTo("equivalence"));
        Assert.That(settings.SetNames, Is.Empty);
        Assert.That(settings.OnlyIds, Is.Empty);
        Assert.That(settings.NoCache, Is.False);
        Assert.That(settings.CacheTimeToLiveDays, Is.EqualTo(365));
    }

    [TestCase("strict", true, false)]
    [TestCase("judge", false, true)]
    [TestCase("both", true, true)]
    [TestCase("Both", true, true)]
    public void Graders_EachChoice_TheGradersItRuns(string graders, bool strict, bool judges)
    {
        EvaluationSettings settings = With(("Graders", graders));

        Assert.That(settings.UsesStrictGrade, Is.EqualTo(strict));
        Assert.That(settings.JudgeNames.Count > 0, Is.EqualTo(judges));
    }

    [Test]
    public void Overrides_ListsAndFlags_ReadAsWritten()
    {
        EvaluationSettings settings = With(("Only", "Q1, A16"), ("Sets", "Main,AnswerSide"), ("CacheTimeToLiveDays", "30"), ("Graders", "both"), ("Judges", "Equivalence,groundedness"));

        Assert.That(settings.OnlyIds, Is.EqualTo(new[] { "Q1", "A16" }));
        Assert.That(settings.SetNames, Is.EqualTo(new[] { "Main", "AnswerSide" }));
        Assert.That(settings.CacheTimeToLiveDays, Is.EqualTo(30));
        Assert.That(settings.JudgeNames, Is.EqualTo(new[] { "equivalence", "groundedness" }));
    }

    [TestCase("Graders", "judges", "Evaluation:Graders")]
    [TestCase("Judges", "relevance", "Judges names relevance")]
    [TestCase("Sets", "Main,Extra", "Sets names Extra")]
    [TestCase("CacheTimeToLiveDays", "0", "CacheTimeToLiveDays must be positive")]
    public void Validate_BadValue_NamedInTheError(string key, string value, string message)
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => With((key, value)))!;

        Assert.That(error.Message, Does.Contain(message));
    }

    [Test]
    public void Validate_JudgeWithNoJudges_Fails()
    {
        Assert.That(() => With(("Graders", "judge"), ("Judges", "")), Throws.InvalidOperationException.With.Message.Contains("Judges is empty"));
    }

    // The judge's larger context window makes Ollama reload the model; with fresh answers that happens around every
    // question, and a variance pass would measure reloads instead of the app.
    [TestCase("judge")]
    [TestCase("both")]
    public void Validate_JudgeWithNoCache_Fails(string graders)
    {
        Assert.That(() => With(("Graders", graders), ("NoCache", "true")), Throws.InvalidOperationException.With.Message.Contains("judge cached answers"));
    }

    [TestCase("judge")]
    [TestCase("both")]
    public void Validate_ClaudeAnsweringWithTheLocalJudge_Fails(string graders)
    {
        Assert.That(() => With(("ChatModel", "Claude"), ("Graders", graders)), Throws.InvalidOperationException.With.Message.Contains("set JudgeModel to a Claude model"));
    }

    // A Claude judge has its own client: any answers can be judged, fresh ones too - no Ollama reload to avoid.
    [TestCase("Local", "false")]
    [TestCase("Claude", "false")]
    [TestCase("Local", "true")]
    public void Validate_ClaudeJudge_AllowedWithEitherAnsweringModel(string chatModel, string noCache)
    {
        EvaluationSettings settings = With(("ChatModel", chatModel), ("Graders", "both"), ("JudgeModel", "claude-sonnet-5-5"), ("NoCache", noCache));

        Assert.That(settings.UsesLocalJudge, Is.False);
        Assert.That(settings.JudgeNames, Is.EqualTo(new[] { "equivalence" }));
    }

    [Test]
    public void Validate_ClaudeJudgeWithEffortNone_Fails()
    {
        Assert.That(() => With(("Graders", "judge"), ("JudgeModel", "claude-sonnet-5-5"), ("JudgeEffort", "None")),
            Throws.InvalidOperationException.With.Message.Contains("JudgeEffort can't be None"));
    }

    [Test]
    public void Validate_ClaudeWithUnload_Fails()
    {
        Assert.That(() => With(("ChatModel", "Claude"), ("NoCache", "true"), ("UnloadEachQuestion", "true")),
            Throws.InvalidOperationException.With.Message.Contains("only an Ollama model is unloaded"));
    }

    [Test]
    public void Validate_ClaudeStrictFresh_Allowed()
    {
        Assert.That(With(("ChatModel", "claude"), ("NoCache", "true")).ChatModel, Is.EqualTo(ChatModel.Claude));
    }

    [Test]
    public void Validate_UnloadWithoutNoCache_Fails()
    {
        Assert.That(() => With(("UnloadEachQuestion", "true")), Throws.InvalidOperationException.With.Message.Contains("without NoCache"));
    }

    [Test]
    public void From_MissingKey_NamedInTheError()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>("Evaluation:Graders", "strict")])
            .Build();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => EvaluationSettings.From(configuration))!;

        Assert.That(error.Message, Does.Contain("Evaluation:Judges").And.Contain("Evaluation:NoCache"));
    }
}
