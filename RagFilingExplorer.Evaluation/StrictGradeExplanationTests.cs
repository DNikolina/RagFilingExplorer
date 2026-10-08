using RagFilingExplorer.Evaluation.Evaluators;
using RagFilingExplorer.Evaluation.Grading;

namespace RagFilingExplorer.Evaluation;

/// <summary>The strict grade in words and where the expected answer was - the report's explanations, offline.</summary>
[TestFixture]
public class StrictGradeExplanationTests
{
    private static readonly DirectoryInfo Repo = RepoPaths.FindRoot(AppContext.BaseDirectory);

    private static readonly IReadOnlyDictionary<string, ExpectedAnswer> Expected = ExpectedAnswer.LoadAll(Repo);

    private static string? Explain(string id, string answer) =>
        StrictGradeExplanation.Explain(Expected[id], StrictGrader.Grade(Expected[id], answer), answer);

    [Test]
    public void Explain_Trap_NamesWhatItIsAndTheExpectedFigure()
    {
        // A10, the v3 baseline's answer: the equity statement's dividends declared, not the cash flow statement's paid.
        string? text = Explain("A10", "Microsoft paid $27,034 million in common stock cash dividends in fiscal 2026 (Source: MSFT-10K-2026.html, PART II > Item 8).");

        Assert.That(text, Is.EqualTo("States 27,034 (dividends declared (equity statement), not paid (cash flow statement)) instead of the expected 26,445 million."));
    }

    [TestCase("A10", "Microsoft paid $26,445 million in dividends.", "States the expected 26,445 million.")]
    [TestCase("H4", "R&D rose by $3.1 billion.", "States the expected 3,074 million (rounded, in billions).")]
    [TestCase("A10", "The excerpts don't contain the dividends Microsoft paid in fiscal 2026.", "Declines, though the filing states 26,445 million.")]
    [TestCase("A10", "Microsoft paid 26,445 in dividends.", "States the expected 26,445 million without its unit.")]
    [TestCase("A2", "Nasdaq's basic earnings per share were $3.12, and diluted $3.09.", "States the expected $3.12, but also 3.09 (diluted) - a reader decides which it answers with.")]
    [TestCase("R1", "The excerpts don't contain Oracle's current deferred revenues.", "Declines - a routing test, where the question's keywords point away from the answer, so a decline passes.")]
    public void Explain_Grade_InWords(string id, string answer, string? explanation)
    {
        Assert.That(Explain(id, answer), Is.EqualTo(explanation));
    }

    [Test]
    public void Explain_AnsweredANegative_SaysADeclineWasExpected()
    {
        ExpectedAnswer negative = Expected.Values.First(e => e.Kind == "negative");

        Assert.That(Explain(negative.Id, "It was $1,234 million."), Does.StartWith("Gives an answer, but the filings don't contain this"));
    }

    private static readonly RetrievedExcerpt Equity = new("MSFT-10K-2026.html", "Item 8", "equity_statement",
        "Retained earnings > Common stock cash dividends — 2026: (27,034) | 2025: (24,677)");

    private static readonly RetrievedExcerpt CashFlow = new("MSFT-10K-2026.html", "Item 8", "cash_flow_statement",
        "Financing > Common stock cash dividends paid — 2026: (26,445) | 2025: (24,082)");

    [Test]
    public void ExpectedTrace_Misreading_TheLineTheAnswerWasOn()
    {
        string? trace = FigureSourceEvaluator.ExpectedTrace(Expected["A10"], "Microsoft paid $27,034 million.", [Equity, CashFlow]);

        Assert.That(trace, Is.EqualTo("Expected 26,445: excerpt 2 (cash_flow_statement, Item 8): \"Financing > Common stock cash dividends paid — 2026: (26,445) | 2025: (24,082)\""));
    }

    [Test]
    public void ExpectedTrace_ExpectedInNoExcerpt_ARetrievalMiss()
    {
        Assert.That(FigureSourceEvaluator.ExpectedTrace(Expected["A10"], "Microsoft paid $27,034 million.", [Equity]),
            Is.EqualTo("Expected 26,445: in none of the excerpts - retrieval missed it"));
    }

    private static readonly RetrievedExcerpt Income = new("ORCL-10K-2026.html", "Item 15", "income_statement",
        "Revenues > Total revenues — 2026: 67,357 | 2025: 57,399\nNet income — 2026: $17,087 | 2025: $12,443");

    [Test]
    public void ExpectedTrace_CalculationWithItsInputs_DerivedFromThem()
    {
        // A27: 25.4% is never printed; both inputs were in the excerpts - not a retrieval miss.
        Assert.That(FigureSourceEvaluator.ExpectedTrace(Expected["A27"], "It was 25.9%.", [Equity, Income]),
            Is.EqualTo("Expected 25.4: not printed as such - derived from 17,087 (excerpt 2), 67,357 (excerpt 2)"));
    }

    [Test]
    public void ExpectedTrace_CalculationWithoutItsInputs_ARetrievalMiss()
    {
        Assert.That(FigureSourceEvaluator.ExpectedTrace(Expected["A27"], "It was 25.9%.", [Equity]),
            Is.EqualTo("Expected 25.4: from 17,087 (in no excerpt), 67,357 (in no excerpt) - retrieval missed it"));
    }

    [Test]
    public void Explain_WrongResultWithItsInputs_NamesTheResultFirst()
    {
        // A27, the v3 baseline's answer: the stated result is 25.9, the inputs are traps only by being stated too.
        Assert.That(Explain("A27", "Oracle's net income as a percentage of its total revenues in fiscal 2026 was 25.9%. Net income was $17,087 million. Total revenues were $67,357 million."),
            Is.EqualTo("States 25.9 instead of the expected 25.4%; also states 17,087 (an input of the asked-for calculation, not its result), 67,357 (an input of the asked-for calculation, not its result)."));
    }

    [Test]
    public void Explain_AnotherFigure_NamesIt()
    {
        // A15, the v3 baseline's answer: the stockholders' equity note's total, not the cash flow statement's line.
        Assert.That(Explain("A15", "Microsoft spent $16,719 million in cash repurchasing its common stock in fiscal 2026."),
            Is.EqualTo("States 16,719 instead of the expected 22,271 million."));
    }

    [Test]
    public void ExpectedTrace_PassingAnswerOrNegative_Nothing()
    {
        ExpectedAnswer negative = Expected.Values.First(e => e.Kind == "negative");

        Assert.That(FigureSourceEvaluator.ExpectedTrace(Expected["A10"], "Microsoft paid $26,445 million.", [CashFlow]), Is.Null);
        Assert.That(FigureSourceEvaluator.ExpectedTrace(negative, "It was $1,234 million.", [CashFlow]), Is.Null);
    }
}
