using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace RagFilingExplorer.Local.Evaluation.Grading;

/// <summary>A grade: <see cref="Status"/> is one of reliable, decline-ok, no-unit, wrong-unit, declined, wrong, malformed,
/// check; <see cref="Note"/> says why (empty when there's nothing to add).</summary>
internal sealed record StrictGrade(string Status, string Note)
{
    /// <summary>Reliable answers and correct declines - what the project's scores count.</summary>
    public bool Passed => Status is "reliable" or "decline-ok";
}

/// <summary>
/// tools/grade_answers.py's rules, ported line for line (v3 step 2) - the manual pass's strict grading
/// (docs/Manual-Test-Questions.md, "Grading"):
///   Correct  - the expected figure is stated (an alternative line's figure counts only if the answer names that line,
///              e.g. Q10's "attributable to Nasdaq")
///   Complete - its unit is stated: "million"/"thousand" after the number (or anywhere in the answer when the number
///              itself carries none), "$" before a per-share figure, "%" after a rate. A different unit attached to the
///              number is wrong-unit: the digits are right but the figure is off by 1,000x.
///   Reliable - both. A decline is correct for negatives, passes for routing tests, and is "declined" otherwise.
/// The period ("for the year ended ...") is not checked: the manual grading didn't enforce it either. "check" marks an
/// answer only a reader can settle - the expected figure next to a lookalike, or a decline that still states figures.
///
/// The Python grader is the specification: GraderParityTests holds this port to its grade - status and note - on every
/// graded answer in eval/. A rule changes there first, then here, until the Python tools are retired.
/// </summary>
internal static partial class StrictGrader
{
    // Python's re and .NET's Regex agree on these patterns: \d, \w and \b are Unicode-aware in both, and $ (without
    // Multiline) matches at the end or before a final newline in both.
    [GeneratedRegex(@"\d{1,3}(?:,\d{3})+(?:\.\d+)?|\d+(?:\.\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"\G\)?\s*(million|thousand|billion)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ScaleAfterRegex();

    // A curly apostrophe reaches the log since the app writes UTF-8 (2026-10-01); before, it arrived as "'".
    [GeneratedRegex(@"(?:don['’]t|do not|does not|doesn['’]t) (?:contain|include|provide|mention|state|specify)"
        + @"|not (?:explicitly |directly |specifically )?"
        + @"(?:stated|provided|available|included|mentioned|found|specified|reported|disclosed)"
        + @"|no (?:information|data|mention)|unable to|cannot (?:find|determine|answer)|there is no",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeclineRegex();

    // The prompt-v1 failure: the units rule came out as the whole answer ("The unit for the figures is not stated.").
    [GeneratedRegex(@"^\W*the units?\b[^.]{0,40}\bnot stated\W*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnitOnlyRegex();

    [GeneratedRegex(@"\G\)?\s*(%|percent)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PercentAfterRegex();

    [GeneratedRegex(@"\$\s*\(?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex DollarBeforeRegex();

    [GeneratedRegex(@"\G\s*billion", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BillionAfterRegex();

    [GeneratedRegex(@"\(Source:[^)]*\)|[A-Z]{4}-10K-\d{4}\.html|\b(?:Item|Note|PART)\s+\w+", RegexOptions.CultureInvariant)]
    private static partial Regex CitationTextRegex();

    [GeneratedRegex(@"^(19|20)\d\d$", RegexOptions.CultureInvariant)]
    private static partial Regex YearRegex();

    private sealed record Token(string Text, int Start, int End);

    /// <summary>grade() - the status and note for one answer.</summary>
    public static StrictGrade Grade(ExpectedAnswer entry, string answer)
    {
        IReadOnlyList<string> expect = entry.Expect ?? [];
        if (UnitOnlyRegex().IsMatch(answer))
        {
            return new("malformed", "the units rule as the whole answer");
        }

        bool declines = DeclineRegex().IsMatch(answer) || answer is "(no results)" or "(no answer)";
        List<string> figures = StatedFigures(answer, entry.Question);

        // A decline that still states a figure - T9's "$0 ... as there is no mention", T8's "not stated, but the year's
        // average was $85.47" - is neither a clean decline nor a clean answer: a reader decides.
        bool declined = declines && figures.Count == 0;
        if (entry.Kind == "negative")
        {
            if (declined)
            {
                return new("decline-ok", "");
            }

            return declines ? new("check", "declines but states figures") : new("wrong", "answered a negative");
        }

        if (entry.Kind == "fact")
        {
            string lower = answer.ToLowerInvariant();
            List<string> missing = expect.Where(e => !lower.Contains(e.ToLowerInvariant()) && !HasNumber(answer, e)).ToList();
            if (missing.Count == 0)
            {
                List<string> named = (entry.Conflicts ?? []).Where(c => lower.Contains(c.ToLowerInvariant())).ToList();
                return named.Count > 0 ? new("check", $"also names {string.Join(", ", named)}") : new("reliable", "");
            }

            if (declined)
            {
                return new("declined", "");
            }

            return declines ? new("check", "declines but states figures") : new("wrong", $"missing {string.Join(", ", missing)}");
        }

        string? unit = entry.Unit;
        List<string> found = expect.Where(e => HasNumber(answer, e)).ToList();
        foreach (AcceptedAlternative alt in entry.Accept ?? [])
        {
            if (found.Count == 0 && HasNumber(answer, alt.Value) && answer.ToLowerInvariant().Contains(alt.IfLabel.ToLowerInvariant()))
            {
                found = [alt.Value];
                expect = [alt.Value];
            }
        }

        // exact: an MD&A-rounding trap or asked-for arithmetic, where "$55.7 billion" is the wrong answer, not a rounding of it.
        if (expect.Count > 0 && found.Count == 0 && !entry.Exact && expect.All(e => RoundedBillions(answer, e, unit)))
        {
            return new("reliable", "rounded, in billions");
        }

        if (found.Count == expect.Count && expect.Count > 0)
        {
            List<string> units = expect.Select(e => UnitStatus(answer, e, unit)).ToList();
            List<string> conflict = (entry.Conflicts ?? []).Where(c => !expect.Contains(c) && HasNumber(answer, c)).ToList();
            if (conflict.Count > 0)
            {
                return new("check", $"also states {string.Join(", ", conflict)}");
            }

            if (units.Contains("wrong"))
            {
                return new("wrong-unit", $"expected {unit}");
            }

            if (units.Contains("missing"))
            {
                return new("no-unit", $"expected {unit}");
            }

            return new("reliable", "");
        }

        if (entry.Kind == "routing" && declined)
        {
            return new("decline-ok", "routing test");
        }

        if (declined && found.Count == 0)
        {
            return new("declined", "");
        }

        List<string> traps = (entry.Traps ?? []).Where(t => HasNumber(answer, t)).ToList();
        if (declines && found.Count == 0)
        {
            return new("check", "declines but states " + string.Join(", ", traps.Count > 0 ? traps : figures));
        }

        if (found.Count > 0)
        {
            return new("wrong", $"only {string.Join(", ", found)} of {string.Join(", ", expect)}");
        }

        return new("wrong", traps.Count > 0 ? $"trap {string.Join(", ", traps)}" : "expected figure absent");
    }

    // tokens()
    private static List<Token> Tokens(string text) =>
        NumberRegex().Matches(text).Select(m => new Token(m.Value, m.Index, m.Index + m.Length)).ToList();

    /// <summary>The numbers in <paramref name="text"/> as the grader reads them ("26,445" from "(26,445)").</summary>
    internal static IEnumerable<string> Numbers(string text) => Tokens(text).Select(t => t.Text);

    /// <summary>Where <paramref name="value"/> first occurs in <paramref name="text"/> as a number the grader reads, or -1.</summary>
    internal static int IndexOfNumber(string text, string value) => Tokens(text).FirstOrDefault(t => t.Text == value)?.Start ?? -1;

    // has_number()
    private static bool HasNumber(string text, string value) => Tokens(text).Any(t => t.Text == value);

    // The answer from `start` on, at most `length` characters - Python's answer[start:start + length].
    private static string Slice(string text, int start, int length) =>
        start >= text.Length ? string.Empty : text.Substring(start, Math.Min(length, text.Length - start));

    /// <summary>rounded_billions() - true if the answer states <paramref name="value"/> (in millions) in billions, rounded
    /// to the digits it gives: the filing's own wording is often "$3.1 billion" for 3,074 million (H4).</summary>
    private static bool RoundedBillions(string answer, string value, string? unit)
    {
        if (unit != "million")
        {
            return false;
        }

        double expected = double.Parse(value.Replace(",", ""), CultureInfo.InvariantCulture) / 1000;
        foreach (Token t in Tokens(answer))
        {
            if (BillionAfterRegex().IsMatch(Slice(answer, t.End, 10)))
            {
                int decimals = t.Text.Contains('.') ? t.Text.Split('.')[1].Length : 0;
                if (decimals > 0 && PythonRound(expected, decimals) == decimal.Parse(t.Text.Replace(",", ""), CultureInfo.InvariantCulture))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>unit_status() - "ok", "missing" or "wrong" for the first occurrence of <paramref name="value"/> that
    /// carries a unit, else the answer as a whole.</summary>
    private static string UnitStatus(string answer, string value, string? unit)
    {
        if (unit is null)
        {
            return "ok";
        }

        foreach (Token hit in Tokens(answer).Where(t => t.Text == value))
        {
            if (unit == "%")
            {
                if (PercentAfterRegex().IsMatch(Slice(answer, hit.End, 10)))
                {
                    return "ok";
                }
            }
            else if (unit == "$")
            {
                int from = Math.Max(0, hit.Start - 3);
                if (DollarBeforeRegex().IsMatch(answer[from..hit.Start]))
                {
                    return "ok";
                }
            }
            else
            {
                Match m = ScaleAfterRegex().Match(Slice(answer, hit.End, 12));
                if (m.Success)
                {
                    return m.Groups[1].Value.ToLowerInvariant() == unit ? "ok" : "wrong";
                }
            }
        }

        if (unit is "million" or "thousand"
            && Regex.IsMatch(answer, $@"\b{unit}s?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "ok";
        }

        return "missing";
    }

    /// <summary>stated_figures() - numbers the answer states as figures: not years, not numbers from the question, not
    /// citation text. Also the figures <see cref="Evaluators.FigureSourceEvaluator"/> looks for in the excerpts.</summary>
    internal static List<string> StatedFigures(string answer, string question)
    {
        string text = CitationTextRegex().Replace(answer, " ");
        HashSet<string> asked = Tokens(question).Select(t => t.Text).ToHashSet();
        return Tokens(text).Select(t => t.Text).Where(t => !YearRegex().IsMatch(t) && !asked.Contains(t)).ToList();
    }

    /// <summary>
    /// Python's round(x, digits) for a positive double: the double's exact binary value rounded half to even, as CPython
    /// does it. Math.Round scales by a power of ten first and can disagree at a midpoint: 55.85 is stored as
    /// 55.850000000000001421..., which Python rounds to 55.9.
    /// </summary>
    internal static decimal PythonRound(double x, int digits)
    {
        long bits = BitConverter.DoubleToInt64Bits(x);
        int exponent = (int)((bits >> 52) & 0x7FF);
        long fraction = bits & 0xFFFFFFFFFFFFFL;
        BigInteger mantissa = exponent == 0 ? fraction : fraction | (1L << 52);
        int power = (exponent == 0 ? 1 : exponent) - 1075; // x = mantissa * 2^power, exactly

        BigInteger scaledNumerator = mantissa * BigInteger.Pow(10, digits);
        BigInteger quotient;
        if (power >= 0)
        {
            quotient = scaledNumerator << power;
        }
        else
        {
            BigInteger denominator = BigInteger.One << -power;
            quotient = BigInteger.DivRem(scaledNumerator, denominator, out BigInteger remainder);
            int half = (remainder * 2).CompareTo(denominator);
            if (half > 0 || (half == 0 && !quotient.IsEven))
            {
                quotient += 1;
            }
        }

        return (decimal)quotient / (decimal)Math.Pow(10, digits);
    }
}
