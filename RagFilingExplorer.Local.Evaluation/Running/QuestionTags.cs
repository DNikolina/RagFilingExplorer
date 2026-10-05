using RagFilingExplorer.Local.Evaluation.Evaluators;
using RagFilingExplorer.Local.Evaluation.Grading;

namespace RagFilingExplorer.Local.Evaluation.Running;

/// <summary>
/// A question's tags in the report - shown on its case and filterable as chips ("only the calculations", "only the cash
/// flow questions"). "name:value" tags show their value on the case. The company and statement are the app's own routing
/// (<c>CompanyRegistry.ResolveFilings</c>, <c>QueryIntentResolver.ResolveStatementType</c>, as RagAnswerService calls
/// them), so a routing question shows where the app sent it, not where the answer is. Tags never affect a grade.
/// </summary>
internal static class QuestionTags
{
    public static List<string> For(ExpectedAnswer expected, IReadOnlyList<string> routedFilings, string? routedStatement)
    {
        List<string> tags = [$"kind:{expected.Kind}"];
        if (FigureSourceEvaluator.AsksForCalculation(expected))
        {
            tags.Add("calculation");
        }

        if (expected.Traps is { Count: > 0 })
        {
            tags.Add("has traps");
        }

        // "MSFT-10K-2026.html" -> MSFT; a question naming two companies (Q14) gets both, one naming none "none".
        tags.AddRange(routedFilings.Count > 0
            ? routedFilings.Select(f => $"company:{f.Split('-')[0]}")
            : ["company:none"]);
        tags.Add($"statement:{routedStatement ?? "none"}");
        return tags;
    }
}
