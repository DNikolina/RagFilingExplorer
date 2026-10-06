using RagFilingExplorer.Local.Evaluation.Grading;

namespace RagFilingExplorer.Local.Evaluation;

/// <summary>
/// tools/expected-answers.json is complete for what the evaluation reads - the checks a newly added question has to pass
/// (README, "The evaluation"). A gap here wouldn't fail a run; it would make the report quietly wrong.
/// </summary>
[TestFixture]
public class ExpectedAnswersTests
{
    private static readonly DirectoryInfo Repo = RepoPaths.FindRoot(AppContext.BaseDirectory);

    private static readonly IReadOnlyDictionary<string, ExpectedAnswer> Expected = ExpectedAnswer.LoadAll(Repo);

    [Test]
    public void EveryTrap_HasItsReason()
    {
        // The report names what each trap is; a trap added without one would show as a bare figure.
        List<string> missing = Expected.Values
            .SelectMany(e => (e.Traps ?? []).Where(t => e.TrapWhy?.ContainsKey(t) != true).Select(t => $"{e.Id} {t}"))
            .ToList();

        Assert.That(missing, Is.Empty);
    }

    [Test]
    public void EveryAnswerableQuestion_HasItsExpectedAnswerAndChunkMarker()
    {
        // Without chunk_expect an answerable question's rank reads "Not scored: a negative has no answer to find" and the
        // figure source never says where the expected figure was - silently.
        List<string> incomplete = Expected.Values
            .Where(e => e.Kind != "negative" && (e.Expect is not { Count: > 0 } || e.ChunkExpect is not { Count: > 0 }))
            .Select(e => e.Id)
            .ToList();

        Assert.That(incomplete, Is.Empty);
    }

    [Test]
    public void NoNegative_HasAChunkMarker()
    {
        // The rank reads a chunk_expect as "there's an answer to find" - a negative has none.
        Assert.That(Expected.Values.Where(e => e.Kind == "negative" && e.ChunkExpect is { Count: > 0 }).Select(e => e.Id), Is.Empty);
    }

    [Test]
    public void EveryQuestionInTheQuestionFiles_HasAnExpectedAnswer()
    {
        // The run looks each question up by its text; a missing one would stop it at that question.
        HashSet<string> known = Expected.Values.Select(e => e.Question).ToHashSet();
        List<string> missing = Running.EvaluationRunner.AllSets
            .SelectMany(s => File.ReadAllLines(Path.Combine(Repo.FullName, "tools", s.File)).Select(q => q.Trim()).Where(q => q.Length > 0))
            .Where(q => !known.Contains(q))
            .ToList();

        Assert.That(missing, Is.Empty);
    }

    [Test]
    public void BaselineGrades_SetWithoutAV2Baseline_IsEmptyNotAFailure()
    {
        // A set with no v2 baseline has nothing to compare with - the run's summary skips the comparison.
        Assert.That(EvaluationRunTests.BaselineGrades("SomeNewSet"), Is.Empty);
        Assert.That(EvaluationRunTests.BaselineGrades("AnswerSide"), Has.Count.EqualTo(27));
    }
}
