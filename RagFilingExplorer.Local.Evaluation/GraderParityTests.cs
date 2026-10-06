using System.Text.Json;
using RagFilingExplorer.Local.Evaluation.Grading;

namespace RagFilingExplorer.Local.Evaluation;

/// <summary>
/// <see cref="StrictGrader"/> grades every graded answer in eval/ exactly as tools/grade_answers.py did -
/// status and note - offline, from the answers as logged. A grading is any eval/ JSON whose results carry an id, status,
/// note and answer (v1, every v2 step, both granite models). The Python
/// grade is "graded" where a reader later resolved it (H2, H10, T9...) - "status" then holds the reader's decision - and
/// "status" otherwise - the Python grader reproduces every grading that way.
/// </summary>
[TestFixture]
public class GraderParityTests
{
    private static readonly DirectoryInfo Repo = RepoPaths.FindRoot(AppContext.BaseDirectory);

    private sealed record Graded(string Id, string Answer, string Status, string Note);

    private static IReadOnlyList<Graded>? ReadGrading(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("results", out JsonElement results)
            || results.ValueKind != JsonValueKind.Array
            || results.GetArrayLength() == 0
            || !results.EnumerateArray().All(r => r.ValueKind == JsonValueKind.Object
                && new[] { "id", "status", "note", "answer" }.All(p => r.TryGetProperty(p, out _))))
        {
            return null;
        }

        return results.EnumerateArray().Select(r => new Graded(
            r.GetProperty("id").GetString()!,
            r.GetProperty("answer").GetString()!,
            r.TryGetProperty("graded", out JsonElement graded) ? graded.GetString()! : r.GetProperty("status").GetString()!,
            r.GetProperty("note").GetString()!)).ToList();
    }

    private static IEnumerable<string> GradedRuns() =>
        Directory.GetFiles(Path.Combine(Repo.FullName, "eval"), "*.json", SearchOption.AllDirectories)
            .Where(p => ReadGrading(p) is not null)
            .Select(p => Path.GetRelativePath(Repo.FullName, p).Replace('\\', '/'))
            .Order(StringComparer.Ordinal);

    [Test]
    public void GradedRuns_AreAllFound()
    {
        List<string> runs = GradedRuns().ToList();
        int answers = runs.Sum(r => ReadGrading(Path.Combine(Repo.FullName, r))!.Count);

        Assert.That(runs, Has.Count.GreaterThanOrEqualTo(34), "the 34 graded runs of 2026-10-02, and any since");
        Assert.That(answers, Is.GreaterThanOrEqualTo(1029));
    }

    [TestCaseSource(nameof(GradedRuns))]
    public void Grade_EveryAnswerInTheRun_MatchesThePythonGrader(string run)
    {
        IReadOnlyDictionary<string, ExpectedAnswer> expected = ExpectedAnswer.LoadAll(Repo);

        List<string> differences = ReadGrading(Path.Combine(Repo.FullName, run))!
            .Select(g => (g, mine: StrictGrader.Grade(expected[g.Id], g.Answer)))
            .Where(x => x.mine.Status != x.g.Status || x.mine.Note != x.g.Note)
            .Select(x => $"{x.g.Id}: Python {x.g.Status} '{x.g.Note}', .NET {x.mine.Status} '{x.mine.Note}'")
            .ToList();

        Assert.That(differences, Is.Empty);
    }

    // Python's round() rounds the double's exact value half to even; these are the cases Math.Round alone gets wrong or
    // where the midpoint is real.
    [TestCase(55.85, 1, "55.9")]   // stored as 55.850000000000001...
    [TestCase(2.675, 2, "2.67")]   // stored as 2.67499999999999982...
    [TestCase(0.125, 2, "0.12")]   // exact in binary: a true midpoint, to even
    [TestCase(0.375, 2, "0.38")]   // exact in binary: a true midpoint, to even
    [TestCase(3.074, 1, "3.1")]    // H4: 3,074 million as "$3.1 billion"
    [TestCase(55.663, 1, "55.7")]  // A14: MD&A's "$55.7 billion"
    public void PythonRound_MatchesPythonsRound(double value, int digits, string expected)
    {
        Assert.That(StrictGrader.PythonRound(value, digits), Is.EqualTo(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture)));
    }
}
