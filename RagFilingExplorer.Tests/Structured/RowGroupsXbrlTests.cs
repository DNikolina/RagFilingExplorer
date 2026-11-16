using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Microsoft.ML.Tokenizers;
using RagFilingExplorer.Core;
using RagFilingExplorer.Core.Chunking;
using RagFilingExplorer.Core.Structured;
using RagFilingExplorer.Core.Xbrl;

namespace RagFilingExplorer.Tests.Structured;

/// <summary>
/// The table rows' groups, read from layout alone (HtmlTableLinearizer: indents, totals, figures adding up), checked
/// against the filer's own presentation tree - XBRL as a check on the layout, never its input. A group's tagged rows
/// share a parent in the tree, so a row the layout puts in a group under another parent, or leaves out of the group
/// just before it though the tree puts it with that group's rows, is a disagreement. Groups the tree can't speak to
/// are skipped: dimension members (NFLX's designated / not designated hedges - one concept under two groups) and line
/// items listed flat under a [Line Items] node. Each disagreement it found was a layout miss, since fixed: rows after a
/// group's total, or a running total ("Net cash provided by operating activities"), filed under the group.
/// </summary>
[TestFixture]
public class RowGroupsXbrlTests
{
    private static readonly string[] Filings = ["MSFT-10K-2026.html", "NDAQ-10K-2025.html", "NFLX-10K-2025.html", "ORCL-10K-2026.html"];

    // Disagreements read against the pages, where the layout is right: a net line or a sibling that the filer's tree
    // hangs beside the group's rows. Anything new, or any of these gone, is a change to read.
    private static readonly Dictionary<string, string[]> Reviewed = new()
    {
        ["MSFT-10K-2026.html"] =
        [
            // A sibling of Ireland, at its indent; the tree lists the countries' lines flat.
            "outside \"Ireland:\" but presented with its rows under us-gaap:IncomeTaxExpenseBenefitContinuingOperationsIncomeTaxReconciliationAbstract: Other foreign jurisdictions (b)",
            // The statutory rate and every effect added up, not an effect; the tree lists it beside them.
            "outside \"Effect of:\" but presented with its rows under us-gaap:EffectiveIncomeTaxRateContinuingOperationsTaxRateReconciliationAbstract: Effective rate",
            // Printed after "Total deferred income tax assets" - the allowance and the net beside, not among, the assets.
            "outside \"Deferred Income Tax Assets\" but presented with its rows under us-gaap:DeferredTaxAssetsNetAbstract: Less valuation allowance",
            "outside \"Deferred Income Tax Assets\" but presented with its rows under us-gaap:DeferredTaxAssetsNetAbstract: Deferred income tax assets, net of valuation allowance",
        ],
        ["NDAQ-10K-2025.html"] =
        [
            // A revolving facility, not a senior unsecured note.
            "outside \"Long-term debt - senior unsecured notes:\" but presented with its rows under ndaq:ChangesInLongTermDebtObligationsAbstract: 2022 Revolving Credit Facility",
            // Revenues less expenses, not an expense.
            "outside \"Expenses:\" but presented with its rows under us-gaap:OperatingExpensesAbstract: Operating loss",
        ],
        ["NFLX-10K-2025.html"] =
        [
            // A sibling of Brazil, as MSFT's after Ireland.
            "outside \"Brazil\" but presented with its rows under nflx:EffectiveIncomeTaxRateReconciliationForeignTaxEffectsAmountAbstract: Other foreign jurisdictions",
        ],
        ["ORCL-10K-2026.html"] =
        [
            // Assets net of liabilities, not a liability line.
            "outside \"Deferred tax liabilities:\" but presented with its rows under us-gaap:ComponentsOfDeferredTaxLiabilitiesAbstract: Net deferred tax assets",
        ],
    };

    [TestCaseSource(nameof(Filings))]
    public void RowGroups_RealFiling_AgreeWithTheFilersPresentationTree(string filing)
    {
        FileInfo file = new(Path.Combine(RepoPaths.FindRoot(TestContext.CurrentContext.TestDirectory).FullName, "data", filing));
        IHtmlDocument document = new HtmlParser().ParseDocument(FilingFile.Read(file));
        XbrlTaxonomy taxonomy = TaxonomyReader.Read(TaxonomyReader.FindForFiling(document, file.Directory!)!);
        StructuredFiling read = new StructuredChunkingStrategy(TiktokenTokenizer.CreateForModel("gpt-4"), 500, 50).Read(document, taxonomy);

        List<string> disagreements = read.Sections.SelectMany(s => s.Blocks).OfType<TableBlock>()
            .SelectMany(t => Disagreements(t.Table, taxonomy.PresentationParents!))
            .ToList();

        Assert.That(disagreements, Is.EquivalentTo(Reviewed[filing]), string.Join("\n", disagreements));
    }

    private static IEnumerable<string> Disagreements(LinearizedTable table, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> parentsByRole)
    {
        List<(LinearizedRow Row, string Concept)> rows = table.Rows
            .Select(r => (Row: r, Concept: r.Values.Select(v => v.Concept).OfType<string>().GroupBy(c => c).MaxBy(g => g.Count())?.Key))
            .Where(r => r.Concept is not null)
            .Select(r => (r.Row, r.Concept!))
            .ToList();

        // The role presenting most of the table's concepts is the table's.
        IReadOnlyDictionary<string, string>? parents = parentsByRole.Values.MaxBy(p => rows.Count(r => p.ContainsKey(r.Concept)));
        if (parents is null || rows.Count(r => parents.ContainsKey(r.Concept)) < 2
            || rows.Where(r => r.Row.GroupLabel is not null).GroupBy(r => r.Concept).Any(g => g.Select(r => r.Row.GroupLabel).Distinct().Count() > 1))
        {
            yield break;
        }

        for (int start = 0; start < rows.Count;)
        {
            string? group = rows[start].Row.GroupLabel;
            int end = start;
            while (end < rows.Count && rows[end].Row.GroupLabel == group)
            {
                end++;
            }

            List<string> runParents = rows.Skip(start).Take(end - start).Select(r => parents.GetValueOrDefault(r.Concept)).OfType<string>().ToList();
            string? shared = runParents.Count >= 2 ? runParents.GroupBy(p => p).Where(g => g.Count() > runParents.Count / 2).Select(g => g.Key).FirstOrDefault() : null;
            // Under a [Line Items] node the tree is flat by the Dimensions convention - the groups are axis members
            // (MSFT's product / service cost of revenue, ORCL's restructuring plans) - so it can't place a row in one.
            // A row presented above the group's node is its total ("Current taxes" over the current tax lines), below it
            // a nested sub-section ("Effect of:" holding a tax credits node) - both in the group. And where rows before
            // the group share its node, the tree lists the group flat with them and can't place a row in or out of it.
            bool flat = shared is not null && rows.Take(start).Any(r => r.Row.GroupLabel is null && parents.GetValueOrDefault(r.Concept) == shared);
            if (group is not null && shared is not null && !shared.EndsWith("LineItems", StringComparison.Ordinal) && !flat)
            {
                foreach ((LinearizedRow row, string concept) in rows.Skip(start).Take(end - start))
                {
                    if (parents.GetValueOrDefault(concept) is { } parent && parent != shared && !row.Label.StartsWith("Total", StringComparison.OrdinalIgnoreCase)
                        && !IsAncestor(parents, parent, shared) && !IsAncestor(parents, shared, parent))
                    {
                        yield return $"in \"{group}\" but presented under {parent}, not {shared}: {row.Label}";
                    }
                }

                // The rows right after the group, outside it, until the next group. Not totals: filers list a grand
                // total beside the group's rows as often as above them, and the figures adding up already placed it.
                for (int next = end; next < rows.Count && rows[next].Row.GroupLabel is null; next++)
                {
                    if (parents.GetValueOrDefault(rows[next].Concept) == shared && !rows[next].Row.Label.StartsWith("Total", StringComparison.OrdinalIgnoreCase))
                    {
                        yield return $"outside \"{group}\" but presented with its rows under {shared}: {rows[next].Row.Label}";
                    }
                }
            }

            start = end;
        }
    }

    private static bool IsAncestor(IReadOnlyDictionary<string, string> parents, string ancestor, string concept)
    {
        for (string? up = parents.GetValueOrDefault(concept); up is not null; up = parents.GetValueOrDefault(up))
        {
            if (up == ancestor)
            {
                return true;
            }
        }

        return false;
    }
}
