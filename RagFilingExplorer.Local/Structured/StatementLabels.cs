using AngleSharp.Dom;
using RagFilingExplorer.Local.Xbrl;

namespace RagFilingExplorer.Local.Structured;

/// <summary>
/// Which table is which primary financial statement, from the filer's own taxonomy instead of v1's
/// title patterns (StatementTypeDetector). Every filing declares its primary statements as Statement roles
/// (parentheticals excluded - <see cref="XbrlTaxonomy.PrimaryStatements"/>); a small title rule names each role's
/// type, and a concept only that statement presents confirms it - checked on all four filings, where each check
/// concept is presented by exactly one primary role. A disagreement, a missing or doubled type, fails loudly.
///
/// A role then goes to the one table covering most of the concepts it presents. In the four filings the 20
/// primary statements are one table each, covering 50-86% of their role's concepts; the closest other table is
/// NFLX's segment table at 53% of the operations role (it repeats income-statement lines per segment) - above the
/// equity statements' 50%, so no fixed threshold separates them, but it loses its role to the statement itself
/// (79%). A statement split across two HTML tables would label only its larger half; none of the four has one.
/// </summary>
internal static class StatementLabels
{
    // Below this the best table is not taken to be the statement at all (the lowest real one covers 50%).
    private const double MinCoverage = 0.4;

    // Tried in order: a comprehensive income title also contains "INCOME", so it comes first.
    private static readonly (string Type, string[] TitleWords, string[] Concepts)[] Types =
    [
        ("comprehensive_income", ["COMPREHENSIVE"], ["us-gaap:ComprehensiveIncomeNetOfTax"]),
        ("cash_flow_statement", ["CASH FLOW"], ["us-gaap:NetCashProvidedByUsedInOperatingActivities"]),
        ("balance_sheet", ["BALANCE SHEET", "FINANCIAL POSITION", "FINANCIAL CONDITION"], ["us-gaap:Assets", "us-gaap:LiabilitiesAndStockholdersEquity"]),
        ("equity_statement", ["EQUITY"], ["us-gaap:StatementEquityComponentsAxis"]),
        ("income_statement", ["INCOME", "OPERATIONS", "EARNINGS"], ["us-gaap:EarningsPerShareBasic"]),
    ];

    /// <summary>Each primary Statement role's URI -> its statement type (one of the five), confirmed by its concepts.</summary>
    public static Dictionary<string, string> MapRoles(XbrlTaxonomy taxonomy)
    {
        Dictionary<string, string> types = new();
        foreach (XbrlRole role in taxonomy.PrimaryStatements)
        {
            string title = role.Title.ToUpperInvariant();
            (string Type, string[] TitleWords, string[] Concepts) match = Types.FirstOrDefault(t => t.TitleWords.Any(title.Contains));
            if (match.Type is null)
            {
                throw new InvalidOperationException($"Statement role '{role.Title}' matches no statement type by its title.");
            }

            IReadOnlySet<string> presented = taxonomy.PresentedConcepts.GetValueOrDefault(role.Uri) ?? new HashSet<string>();
            if (!match.Concepts.All(presented.Contains))
            {
                throw new InvalidOperationException($"Statement role '{role.Title}' reads as {match.Type} but doesn't present {string.Join(" + ", match.Concepts)}.");
            }

            types.Add(role.Uri, match.Type);
        }

        string[] missing = Types.Select(t => t.Type).Except(types.Values).ToArray();
        string[] doubled = types.Values.GroupBy(t => t).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
        if (missing.Length > 0 || doubled.Length > 0)
        {
            throw new InvalidOperationException($"Primary statements don't map one to one: missing [{string.Join(", ", missing)}], doubled [{string.Join(", ", doubled)}].");
        }

        return types;
    }

    /// <summary>The blocks with each primary statement's table labelled (a new <see cref="TableBlock"/>); the rest as they were.</summary>
    public static List<FilingBlock> Label(IReadOnlyList<FilingBlock> blocks, XbrlTaxonomy taxonomy)
    {
        Dictionary<string, string> roleTypes = MapRoles(taxonomy);
        List<(int Index, HashSet<string> Concepts)> tables = blocks
            .Select((b, i) => (Index: i, Concepts: b is TableBlock t ? TaggedConcepts(t) : new HashSet<string>()))
            .Where(t => t.Concepts.Count > 0)
            .ToList();

        Dictionary<int, string> labels = new();
        foreach ((string roleUri, string type) in roleTypes)
        {
            IReadOnlySet<string> presented = taxonomy.PresentedConcepts[roleUri];
            (int Index, double Coverage) best = tables
                .Select(t => (t.Index, Coverage: (double)t.Concepts.Count(presented.Contains) / presented.Count))
                .MaxBy(t => t.Coverage);
            if (best.Coverage < MinCoverage)
            {
                throw new InvalidOperationException($"No table covers the {type} role: best {best.Coverage:P0} of its {presented.Count} concepts.");
            }

            if (!labels.TryAdd(best.Index, type))
            {
                throw new InvalidOperationException($"One table is the best match for both {labels[best.Index]} and {type}.");
            }
        }

        return blocks.Select((b, i) => labels.TryGetValue(i, out string? type) ? ((TableBlock)b) with { StatementType = type } : b).ToList();
    }

    // The concepts of the numbers tagged in a table (ix:nonFraction names).
    private static HashSet<string> TaggedConcepts(TableBlock table) => table.Element.Descendants<IElement>()
        .Where(e => e.LocalName == "ix:nonfraction" && e.GetAttribute("name") is not null)
        .Select(e => e.GetAttribute("name")!)
        .ToHashSet();
}
