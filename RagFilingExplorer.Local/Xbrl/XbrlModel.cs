using AngleSharp.Dom;

namespace RagFilingExplorer.Local.Xbrl;

/// <summary>
/// One filing's inline XBRL, read by <see cref="InlineXbrlReader"/> (docs/Decision-Log.md, "XBRL
/// hybrid (v2)"). Contexts and units come from the hidden &lt;ix:header&gt;; facts from the ix: elements in the
/// page. The Structured strategy reads it for the filing profile, the structure labels and the company line;
/// CompanyRegistry for the company filter.
/// </summary>
internal sealed record XbrlDocument(
    IReadOnlyDictionary<string, XbrlContext> Contexts,
    IReadOnlyDictionary<string, XbrlUnit> Units,
    IReadOnlyList<XbrlFact> Facts)
{
    /// <summary>The first fact with this concept name (e.g. "dei:EntityRegistrantName"), or null.</summary>
    public XbrlFact? First(string concept) => Facts.FirstOrDefault(f => f.Concept == concept);
}

/// <summary>
/// An xbrli:context (XBRL 2.1 section 4.7): a period - an instant, or a start and end date - and, for a figure
/// that belongs to a segment, class or geography, its dimension members (Dimensions 1.0).
/// </summary>
internal sealed record XbrlContext(string Id, DateOnly? Instant, DateOnly? StartDate, DateOnly? EndDate, IReadOnlyList<XbrlDimension> Dimensions);

/// <summary>An explicit member ("us-gaap:StatementBusinessSegmentsAxis" = "msft:IntelligentCloudMember") or a
/// typed one, whose value is text ("2026-07-01") instead of a member name.</summary>
internal sealed record XbrlDimension(string Axis, string? Member, string? TypedValue);

/// <summary>An xbrli:unit: one or more measures ("iso4217:USD"), or a ratio ("iso4217:USD" per "xbrli:shares").</summary>
internal sealed record XbrlUnit(string Id, IReadOnlyList<string> Numerators, IReadOnlyList<string> Denominators)
{
    public override string ToString() => Denominators.Count == 0 ? string.Join("*", Numerators) : $"{string.Join("*", Numerators)}/{string.Join("*", Denominators)}";
}

/// <summary>
/// An ix:nonFraction (a number) or ix:nonNumeric (text, a date, a flag, or a whole note as a text block).
/// <see cref="Number"/> is the stored value - the displayed digits transformed by <see cref="Format"/>, times
/// 10^<see cref="Scale"/>, negated when <c>sign="-"</c> (the page shows "(4,743)" or "4,743"; the fact is
/// -4,743,000,000). <see cref="Text"/> is a text fact's value, joined across its continuations.
/// </summary>
internal sealed record XbrlFact(
    string? Id,
    string Concept,
    string ContextRef,
    string? UnitRef,
    bool IsNumeric,
    bool IsNil,
    string? Format,
    int Scale,
    string? Decimals,
    bool IsNegated,
    string DisplayText,
    decimal? Number,
    string? Text,
    bool IsTextBlock,
    IReadOnlyList<IElement> Elements);

/// <summary>A filer's extension taxonomy: its roles and, per role, the concepts it presents; plus labels.</summary>
internal sealed record XbrlTaxonomy(
    IReadOnlyList<XbrlRole> Roles,
    IReadOnlyDictionary<string, IReadOnlySet<string>> PresentedConcepts,
    IReadOnlyDictionary<string, string> Labels)
{
    /// <summary>The primary financial statements: Statement roles, parentheticals excluded.</summary>
    public IEnumerable<XbrlRole> PrimaryStatements => Roles.Where(r => r.Type == "Statement" && !r.Title.Contains("Parenthetical", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// A link:roleType. EDGAR requires its definition as "sort code - type - title", type one of Document,
/// Statement, Disclosure, Schedule - e.g. "9952155 - Statement - Consolidated Statements of Changes in
/// Stockholders' Equity" - which is what makes Type readable the same way for every filer.
/// </summary>
internal sealed record XbrlRole(string Uri, string SortCode, string Type, string Title);
