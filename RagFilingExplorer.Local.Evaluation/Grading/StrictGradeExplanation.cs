namespace RagFilingExplorer.Local.Evaluation.Grading;

/// <summary>
/// A strict grade in words, for the report: what the answer stated against what was expected, each trap with what it is
/// (<see cref="ExpectedAnswer.TrapWhy"/>) - "States 27,034 (dividends declared (equity statement), not paid (cash flow
/// statement)) instead of the expected 26,445 million." The grader's own note ("trap 27,034") stays as it is: it's held
/// to the Python grader by GraderParityTests. This only reads the grade; it never changes one.
/// </summary>
internal static class StrictGradeExplanation
{
    /// <summary>The explanation - for every grade, so the report's "Why this score?" is never empty.</summary>
    public static string? Explain(ExpectedAnswer expected, StrictGrade grade, string answer)
    {
        string expect = Expected(expected);
        HashSet<string> stated = StrictGrader.Numbers(answer).ToHashSet();
        string Named(IEnumerable<string> figures) => string.Join(", ", figures.Select(f => Why(expected, f) is { } why ? $"{f} ({why})" : f));
        List<string> traps = (expected.Traps ?? []).Where(stated.Contains).ToList();
        // The answer's own figures that are neither traps nor expected: a wrong sum (A27's 25.9), another line (A15's 16,719).
        List<string> others = Evaluators.FigureSourceEvaluator.Figures(answer, expected.Question)
            .Where(f => !traps.Contains(f) && !(expected.Expect ?? []).Contains(f)).ToList();
        List<string> conflicts = (expected.Conflicts ?? []).Where(c => stated.Contains(c) || answer.Contains(c, StringComparison.OrdinalIgnoreCase)).ToList();

        return grade.Status switch
        {
            "reliable" => grade.Note.Length > 0 ? $"States the expected {expect} ({grade.Note})." : $"States the expected {expect}.",
            "decline-ok" => expected.Kind == "routing"
                ? "Declines - a routing test, where the question's keywords point away from the answer, so a decline passes."
                : "Declines correctly: the filings don't contain this.",
            "no-unit" => $"States the expected {expect} without its unit.",
            "wrong-unit" => $"States the expected figure with the wrong unit - expected {expect}.",
            "declined" => $"Declines, though the filing states {expect}.",
            "malformed" => "Not an answer: " + grade.Note + ".",
            "check" when grade.Note.StartsWith("also states", StringComparison.Ordinal) || grade.Note.StartsWith("also names", StringComparison.Ordinal) =>
                $"States the expected {expect}, but also {Named(conflicts)} - a reader decides which it answers with.",
            "check" => $"Declines, but still states {Named(traps.Count > 0 ? traps : StrictGrader.StatedFigures(answer, expected.Question).Distinct())} - a reader decides.",
            "wrong" when expected.Kind == "negative" => "Gives an answer, but the filings don't contain this - a decline was expected.",
            "wrong" when grade.Note.StartsWith("only", StringComparison.Ordinal) => $"States only part of the expected {expect} ({grade.Note}).",
            "wrong" when others.Count > 0 && traps.Count > 0 => $"States {string.Join(", ", others)} instead of the expected {expect}; also states {Named(traps)}.",
            "wrong" when traps.Count > 0 => $"States {Named(traps)} instead of the expected {expect}.",
            "wrong" when others.Count > 0 => $"States {string.Join(", ", others)} instead of the expected {expect}.",
            "wrong" => $"Doesn't state the expected {expect}.",
            _ => grade.Note.Length > 0 ? grade.Note : grade.Status,
        };
    }

    private static string? Why(ExpectedAnswer expected, string figure) =>
        expected.TrapWhy is { } why && why.TryGetValue(figure, out string? reason) ? reason : null;

    /// <summary>The expected answer as the explanation names it: "26,445 million", "$3.64", "19%", "Austin".</summary>
    private static string Expected(ExpectedAnswer expected)
    {
        IEnumerable<string> values = (expected.Expect ?? []).Select(v => expected.Unit switch
        {
            "million" or "thousand" => $"{v} {expected.Unit}",
            "$" => $"${v}",
            "%" => $"{v}%",
            _ => v,
        });
        return string.Join(" and ", values);
    }
}
