using System.Text;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Reporting;
using Microsoft.Extensions.AI.Evaluation.Reporting.Storage;
using RagFilingExplorer.Local.Evaluation.Evaluators;

namespace RagFilingExplorer.Local.Evaluation.Running;

/// <summary>One stored answer of one execution: what the variance comparison reads back from the result store.</summary>
internal sealed record StoredAnswer(string Execution, string Scenario, string Question, string Answer, string Grade, string FigureSource)
{
    /// <summary>The figures the answer states, as the figure trace reads them (a date's day isn't one), sorted - equal for
    /// two answers that differ only in wording.</summary>
    public IReadOnlyList<string> Figures { get; } = FigureSourceEvaluator.Figures(Answer, Question).Order(StringComparer.Ordinal).ToList();

    /// <summary>The answer as compared: a curly apostrophe read as a straight one - runs logged in the console's code page
    /// have only straight ones, the model's copy of a heading like "Management’s" turned into "'".</summary>
    public string ComparedText { get; } = Answer.Replace('’', '\'');
}

/// <summary>How one question's answers differ across the compared executions - the worst of any pair.</summary>
internal enum AnswerVariation
{
    /// <summary>Word for word the same answer in every execution.</summary>
    Identical,

    /// <summary>Different text, the same figures and grade.</summary>
    Wording,

    /// <summary>A figure added or dropped, the same grade.</summary>
    Figures,

    /// <summary>A different grade.</summary>
    Grade,
}

/// <summary>
/// The variance measurement: the same questions asked afresh in several executions - identical prompts,
/// since retrieval is deterministic - compared question by question. Answers are read from the result store, so any
/// executions can be compared: the cached baseline and NoCache passes.
/// </summary>
internal static class VarianceComparison
{
    /// <summary>Every stored answer of the given executions (the latest iteration of each scenario).</summary>
    public static async Task<List<StoredAnswer>> LoadAsync(string storageRoot, IReadOnlyList<string> executions, CancellationToken cancellationToken = default)
    {
        DiskBasedResultStore store = new(storageRoot);
        List<StoredAnswer> answers = new();
        foreach (string execution in executions)
        {
            Dictionary<string, ScenarioRunResult> latest = new();
            await foreach (ScenarioRunResult result in store.ReadResultsAsync(execution, cancellationToken: cancellationToken))
            {
                if (!latest.TryGetValue(result.ScenarioName, out ScenarioRunResult? seen) || result.CreationTime > seen.CreationTime)
                {
                    latest[result.ScenarioName] = result;
                }
            }

            if (latest.Count == 0)
            {
                throw new InvalidOperationException($"No stored results for execution {execution} under {storageRoot}.");
            }

            foreach (ScenarioRunResult result in latest.Values)
            {
                EvaluationResult evaluation = result.EvaluationResult;
                answers.Add(new StoredAnswer(
                    execution,
                    result.ScenarioName,
                    result.Messages.Last().Text,
                    result.ModelResponse.Text.Trim(),
                    // A run with Graders: judge has no strict grade - "-", so wording and figures still compare.
                    evaluation.Metrics.TryGetValue(StrictFigureEvaluator.MetricName, out EvaluationMetric? strict) ? ((StringMetric)strict).Value ?? "" : "-",
                    evaluation.Metrics.TryGetValue(FigureSourceEvaluator.MetricName, out EvaluationMetric? source) ? ((StringMetric)source).Value ?? "" : ""));
            }
        }

        return answers;
    }

    /// <summary>How a question's answers vary: <see cref="AnswerVariation.Grade"/> if any two grades differ, else
    /// <see cref="AnswerVariation.Figures"/> if any two figure lists do, else wording, else identical.</summary>
    public static AnswerVariation Classify(IReadOnlyList<StoredAnswer> answers)
    {
        if (answers.Select(a => a.Grade).Distinct().Count() > 1)
        {
            return AnswerVariation.Grade;
        }

        if (answers.Select(a => string.Join("|", a.Figures)).Distinct().Count() > 1)
        {
            return AnswerVariation.Figures;
        }

        return answers.Select(a => a.ComparedText).Distinct(StringComparer.Ordinal).Count() > 1 ? AnswerVariation.Wording : AnswerVariation.Identical;
    }

    /// <summary>The comparison as text: counts per set, each pair of executions, and every question that varies with its
    /// answers. Only questions every execution answered are compared.</summary>
    public static string Report(IReadOnlyList<string> executions, IReadOnlyList<StoredAnswer> answers)
    {
        List<List<StoredAnswer>> questions = answers
            .GroupBy(a => a.Scenario)
            .Select(g => executions.Select(e => g.FirstOrDefault(a => a.Execution == e)).ToList())
            .Where(row => row.All(a => a is not null))
            .Select(row => row.Select(a => a!).ToList())
            .OrderBy(row => row[0].Scenario, StringComparer.Ordinal)
            .ToList();

        StringBuilder text = new();
        text.AppendLine($"Variance across {executions.Count} executions: {string.Join(", ", executions)}");
        text.AppendLine($"{questions.Count} questions answered in every execution.");
        text.AppendLine();

        string Counts(IEnumerable<List<StoredAnswer>> rows)
        {
            List<AnswerVariation> classes = rows.Select(Classify).ToList();
            return string.Join("  ", Enum.GetValues<AnswerVariation>().Select(v => $"{v.ToString().ToLowerInvariant()} {classes.Count(c => c == v)}"));
        }

        text.AppendLine($"All: {Counts(questions)}");
        foreach (IGrouping<string, List<StoredAnswer>> set in questions.GroupBy(row => row[0].Scenario.Split('.')[0]))
        {
            text.AppendLine($"{set.Key}: {Counts(set)}");
        }

        text.AppendLine();
        text.AppendLine("Pairs (same text / same grade):");
        for (int i = 0; i < executions.Count; i++)
        {
            for (int j = i + 1; j < executions.Count; j++)
            {
                int sameText = questions.Count(row => row[i].ComparedText == row[j].ComparedText);
                int sameGrade = questions.Count(row => row[i].Grade == row[j].Grade);
                text.AppendLine($"  {executions[i]} vs {executions[j]}: {sameText}/{questions.Count} / {sameGrade}/{questions.Count}");
            }
        }

        foreach (AnswerVariation variation in new[] { AnswerVariation.Grade, AnswerVariation.Figures, AnswerVariation.Wording })
        {
            List<List<StoredAnswer>> rows = questions.Where(row => Classify(row) == variation).ToList();
            if (rows.Count == 0)
            {
                continue;
            }

            text.AppendLine();
            text.AppendLine($"{variation} ({rows.Count}):");
            foreach (List<StoredAnswer> row in rows)
            {
                text.AppendLine($"  {row[0].Scenario}");
                foreach (StoredAnswer a in row)
                {
                    text.AppendLine($"    [{a.Execution}] {a.Grade}, figures {(a.Figures.Count > 0 ? string.Join(" ", a.Figures) : "-")}"
                        + $" ({(a.FigureSource.Length > 0 ? a.FigureSource : "no figure source")}): {a.Answer.ReplaceLineEndings(" ")}");
                }
            }
        }

        return text.ToString();
    }
}
