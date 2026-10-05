using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using RagFilingExplorer.Local.Evaluation.Evaluators;
using RagFilingExplorer.Local.Evaluation.Grading;

namespace RagFilingExplorer.Local.Evaluation;

/// <summary>The figure-source trace and its evaluator (v3 step 5), offline.</summary>
[TestFixture]
public class FigureSourceEvaluatorTests
{
    private const string Filing = "MSFT-10K-2026.html";

    // A10's case, the misreading step 5 exists to show: the answer's $27,034M is the equity statement's dividends
    // declared, not the cash flow statement's dividends paid (26,445).
    private static readonly RetrievedExcerpt CashFlow = new(Filing, "PART II > Item 8 > Cash Flows Statements", "cash_flow_statement",
        "CASH FLOWS STATEMENTS\n(In millions)\nFinancing > Common stock repurchased — 2026: (18,420) | 2025: (18,420)\n"
        + "Financing > Common stock cash dividends paid — 2026: (26,445) | 2025: (24,082) | 2024: (21,771)");

    private static readonly RetrievedExcerpt Equity = new(Filing, "PART II > Item 8 > Stockholders' Equity Statements", "equity_statement",
        "STOCKHOLDERS' EQUITY STATEMENTS\nRetained earnings > Common stock cash dividends — 2026: (27,034) | 2025: (24,689)");

    private static readonly RetrievedExcerpt Narrative = new(Filing, "PART II > Item 7 > Cash Flows", "narrative",
        "Cash used in financing decreased, mainly due to $18,420 million of common stock repurchased.");

    private static readonly ExpectedAnswer A10 = new(
        "A10", "How much cash did Microsoft pay in common stock dividends in fiscal 2026?", "figure",
        Expect: ["26,445"], Unit: "million", ChunkExpect: ["(26,445)"]);

    private static readonly ExpectedAnswer A26 = new(
        "A26", "By how much did Microsoft's net income increase from fiscal 2025 to fiscal 2026?", "figure",
        Expect: ["31,917"], Unit: "million", Exact: true, ChunkExpect: ["133,749", "101,832"]);

    private static async Task<StringMetric> SourceAsync(ExpectedAnswer expected, string answer, IReadOnlyList<RetrievedExcerpt> excerpts, int generationTopK = 5)
    {
        EvaluationResult result = await new FigureSourceEvaluator(generationTopK).EvaluateAsync(
            [new ChatMessage(ChatRole.User, expected.Question)],
            new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)),
            additionalContext: [new ExpectedAnswerContext(expected), new RetrievedChunksContext(excerpts)]);
        return result.Get<StringMetric>(FigureSourceEvaluator.MetricName);
    }

    [Test]
    public void Trace_FigureInTheEquityStatement_GivesItsPositionStatementTypeAndLine()
    {
        List<FigureSource> sources = FigureSourceEvaluator.Trace(
            "Microsoft paid $27,034 million in common stock dividends in fiscal 2026 (Source: MSFT-10K-2026.html, Item 8).",
            A10.Question, [CashFlow, Equity]);

        Assert.That(sources, Has.Count.EqualTo(1));
        Assert.That(sources[0].Figure, Is.EqualTo("27,034"));
        FigureSighting sighting = sources[0].Sightings.Single();
        Assert.That(sighting.Position, Is.EqualTo(2));
        Assert.That(sighting.Excerpt.StatementType, Is.EqualTo("equity_statement"));
        Assert.That(sighting.Line, Does.StartWith("Retained earnings > Common stock cash dividends"));
    }

    [Test]
    public void Trace_FigureInTwoExcerpts_ListsBoth()
    {
        List<FigureSource> sources = FigureSourceEvaluator.Trace("Microsoft repurchased $18,420 million of common stock.", "q", [CashFlow, Equity, Narrative]);

        Assert.That(sources.Single().Sightings.Select(s => s.Position), Is.EqualTo(new[] { 1, 3 }));
        Assert.That(sources.Single().Sightings[0].MoreLines, Is.Zero, "the line holds 18,420 twice, but it's one line");
    }

    [Test]
    public void Trace_YearsQuestionNumbersAndCitations_AreNotFigures()
    {
        List<FigureSource> sources = FigureSourceEvaluator.Trace(
            "In fiscal 2026, dividends paid were $26,445 million (Source: MSFT-10K-2026.html, Item 8).", A10.Question, [CashFlow]);

        Assert.That(sources.Select(s => s.Figure), Is.EqualTo(new[] { "26,445" }));
    }

    [Test]
    public void Trace_DayOfADate_IsNotAFigure()
    {
        // Q2, Q19 and 32 more v3-baseline answers: "as of December 31, 2025" made 31 a figure, found in nearly every excerpt.
        List<FigureSource> sources = FigureSourceEvaluator.Trace("As of June 30, 2026, dividends paid were $26,445 million.", "q", [CashFlow]);

        Assert.That(sources.Select(s => s.Figure), Is.EqualTo(new[] { "26,445" }));
    }

    [Test]
    public void Trace_SameFigureTwice_TracedOnce()
    {
        List<FigureSource> sources = FigureSourceEvaluator.Trace("$26,445 million - that is, 26,445 million dollars.", A10.Question, [CashFlow]);

        Assert.That(sources, Has.Count.EqualTo(1));
    }

    [Test]
    public void Window_LongLine_CentredOnTheFigure()
    {
        // H13: a 400-character row whose figure came after the first 160 characters - the trace showed the line without it.
        string line = new string('a', 200) + " $5,208,710 " + new string('b', 200);

        string window = FigureSourceEvaluator.Window(line, "5,208,710");

        Assert.That(window, Does.Contain("$5,208,710"));
        Assert.That(window, Does.StartWith("...").And.EndWith("..."));
        Assert.That(FigureSourceEvaluator.Window("Net income — 2026: $17,087", "17,087"), Is.EqualTo("Net income — 2026: $17,087"));
    }

    [TestCase(new[] { "12,443", "17,087" }, new[] { "29,530" }, true)]    // A21: a sum of two inputs
    [TestCase(new[] { "35,562", "32,488" }, new[] { "3,074" }, true)]     // H4: a difference
    [TestCase(new[] { "(26,445)" }, new[] { "26,445" }, false)]           // A10: the figure is its own marker
    [TestCase(new[] { "$20,935" }, new[] { "20,935" }, false)]            // A6: the marker carries a "$"
    [TestCase(new[] { "10,149,273" }, new[] { "24,784,938" }, false)]     // V3: one marker, not its inputs
    [TestCase(new[] { "223,000" }, new[] { "223,000", "121,000" }, false)] // Q11: a fact, marked by one of its figures
    public void AsksForCalculation_TwoOrMoreInputsWithoutTheExpectedFigure(string[] chunkExpect, string[] expect, bool calculation)
    {
        Assert.That(FigureSourceEvaluator.AsksForCalculation(new("X", "q", "figure", Expect: expect, ChunkExpect: chunkExpect)), Is.EqualTo(calculation));
    }

    [Test]
    public async Task EvaluateAsync_EveryFigureInAnExcerpt_IsTraced()
    {
        StringMetric metric = await SourceAsync(A10, "Microsoft paid $27,034 million in dividends.", [CashFlow, Equity]);

        Assert.That(metric.Value, Is.EqualTo("traced"));
        Assert.That(metric.Metadata!["Stated 27,034"], Does.StartWith("excerpt 2 (equity_statement, PART II > Item 8 > Stockholders' Equity Statements)"));
        Assert.That(metric.Metadata!["Expected 26,445"], Does.StartWith("excerpt 1 (cash_flow_statement"));
        Assert.That(metric.Reason, Is.EqualTo(FigureSourceEvaluator.Description), "the report shows the reason as \"What this measures?\"");
        Assert.That(metric.Interpretation!.Reason, Is.EqualTo(
            "A misreading - every figure is in an excerpt: 27,034 from excerpt 2 (equity_statement); the expected 26,445 was in excerpt 1 (cash_flow_statement)."));
        Assert.That(metric.Interpretation!.Failed, Is.False);
        Assert.That(FigureSourceEvaluator.TraceText(metric), Does.StartWith("Stated 27,034: excerpt 2"));
    }

    [Test]
    public async Task EvaluateAsync_FigureInNoExcerpt_IsUntracedAndFails()
    {
        StringMetric metric = await SourceAsync(A10, "Microsoft paid $25,000 million in dividends.", [CashFlow, Equity]);

        Assert.That(metric.Value, Is.EqualTo("untraced"));
        Assert.That(metric.Metadata!["Stated 25,000"], Is.EqualTo("in none of the excerpts"));
        Assert.That(metric.Interpretation!.Reason, Does.StartWith("25,000 in none of the excerpts the model was given"));
        Assert.That(metric.Interpretation!.Failed, Is.True);
    }

    [Test]
    public async Task EvaluateAsync_AskedForCalculation_IsCalculatedNotFailed()
    {
        RetrievedExcerpt income = new(Filing, "PART II > Item 8 > Income Statements", "income_statement", "Net income — 2026: $133,749 | 2025: $101,832");

        StringMetric metric = await SourceAsync(A26, "Net income rose by $31,917 million, from $101,832 million to $133,749 million.", [income]);

        Assert.That(metric.Value, Is.EqualTo("calculated"));
        Assert.That(metric.Metadata!["Stated 101,832"], Does.StartWith("excerpt 1 (income_statement"));
        Assert.That(metric.Interpretation!.Reason, Does.StartWith("31,917 in no excerpt - the question asks for a calculation"));
        Assert.That(metric.Interpretation!.Failed, Is.False);
    }

    [Test]
    public async Task EvaluateAsync_Decline_NoFigureStated()
    {
        StringMetric metric = await SourceAsync(A10, "The excerpts don't contain the dividends Microsoft paid in fiscal 2026.", [CashFlow]);

        Assert.That(metric.Value, Is.EqualTo("no figure stated"));
        Assert.That(metric.Interpretation!.Failed, Is.False);
    }

    [Test]
    public async Task EvaluateAsync_FigureOnlyBeyondGenerationTopK_IsUntraced()
    {
        // The rank list runs to VerboseSearchTopK; the model reads only the first GenerationTopK.
        StringMetric metric = await SourceAsync(A10, "Microsoft paid $27,034 million in dividends.", [CashFlow, Equity], generationTopK: 1);

        Assert.That(metric.Value, Is.EqualTo("untraced"));
    }
}
