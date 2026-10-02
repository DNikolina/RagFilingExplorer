using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using RagFilingExplorer.Local.Chunking;
using RagFilingExplorer.Local.Xbrl;

namespace RagFilingExplorer.Local.Tests.Xbrl;

/// <summary>
/// Step 1b-i's verification, against the real filings in data/ (read-only, offline): every reference resolves,
/// every format code is known, every continuation chain completes, the taxonomy is found and declares the five
/// primary statements, and known figures come out exactly - including the ones v1 answered wrong (Q10's two
/// near-identical lines, T9's roll-forward row with no year).
/// </summary>
[TestFixture]
public class FilingXbrlTests
{
    private static readonly string[] Filings = ["MSFT-10K-2026.html", "NDAQ-10K-2025.html", "NFLX-10K-2025.html", "ORCL-10K-2026.html"];

    private readonly Dictionary<string, (IDocument Page, XbrlDocument Xbrl, XbrlTaxonomy Taxonomy)> _read = new();
    private DirectoryInfo _data = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _data = new DirectoryInfo(Path.Combine(RepoPaths.FindRoot(TestContext.CurrentContext.TestDirectory).FullName, "data"));
        foreach (string filing in Filings)
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(_data.FullName, filing));
            IDocument page = new HtmlParser().ParseDocument(MarkItDownConverter.DetectEncoding(bytes).GetString(bytes));
            FileInfo schema = TaxonomyReader.FindForFiling(page, _data) ?? throw new InvalidOperationException($"No taxonomy for {filing}");
            _read[filing] = (page, InlineXbrlReader.Read(page), TaxonomyReader.Read(schema));
        }
    }

    [TestCaseSource(nameof(Filings))]
    public void Read_EveryContextAndUnitReferenceResolves(string filing)
    {
        XbrlDocument x = _read[filing].Xbrl;

        Assert.That(x.Facts, Has.Count.GreaterThan(1_000));
        Assert.That(x.Facts.Where(f => !x.Contexts.ContainsKey(f.ContextRef)), Is.Empty);
        Assert.That(x.Facts.Where(f => f.IsNumeric && (f.UnitRef is null || !x.Units.ContainsKey(f.UnitRef))), Is.Empty);
        Assert.That(x.Facts.Where(f => !f.IsNil && f.IsNumeric && f.Number is null), Is.Empty);
    }

    // Counted when 1b-i was built; a change means the reader or the filing changed. Continuation chains and
    // format codes are checked by Read itself - it throws on a broken chain or an unknown code.
    [TestCase("MSFT-10K-2026.html", 1869, 131, 96, 43)]
    [TestCase("NDAQ-10K-2025.html", 1807, 120, 96, 104)]
    [TestCase("NFLX-10K-2025.html", 1468, 144, 70, 38)]
    [TestCase("ORCL-10K-2026.html", 2195, 106, 83, 33)]
    public void Read_FactCounts_AsInventoried(string filing, int facts, int negated, int textBlocks, int continued)
    {
        XbrlDocument x = _read[filing].Xbrl;

        Assert.That(x.Facts, Has.Count.EqualTo(facts));
        Assert.That(x.Facts.Count(f => f.IsNegated), Is.EqualTo(negated));
        Assert.That(x.Facts.Count(f => f.IsTextBlock), Is.EqualTo(textBlocks));
        Assert.That(x.Facts.Count(f => f.Elements.Count > 1), Is.EqualTo(continued));
    }

    // Main-set figures, stored in dollars: NFLX's thousands and the others' millions come from each fact's scale.
    // NDAQ's two comprehensive income lines - the Q10/V1 mix-up - are two different concepts. ORCL's dividends
    // declared carry their fiscal year in the context - T9's roll-forward row doesn't show one on the page.
    [TestCase("MSFT-10K-2026.html", "us-gaap:Assets", "2026-06-30", 758_376_000_000)]
    [TestCase("NFLX-10K-2025.html", "us-gaap:Assets", "2025-12-31", 55_596_993_000)]
    [TestCase("NDAQ-10K-2025.html", "us-gaap:ComprehensiveIncomeNetOfTaxIncludingPortionAttributableToNoncontrollingInterest", "2025-12-31", 2_113_000_000)]
    [TestCase("NDAQ-10K-2025.html", "us-gaap:ComprehensiveIncomeNetOfTax", "2025-12-31", 2_114_000_000)]
    [TestCase("NDAQ-10K-2025.html", "us-gaap:ComprehensiveIncomeNetOfTaxIncludingPortionAttributableToNoncontrollingInterest", "2024-12-31", 940_000_000)]
    [TestCase("ORCL-10K-2026.html", "us-gaap:DividendsCommonStockCash", "2025-05-31", 4_743_000_000)]
    [TestCase("ORCL-10K-2026.html", "us-gaap:RetainedEarningsAccumulatedDeficit", "2026-05-31", -4_309_000_000)]
    public void Read_KnownFigure_ForItsPeriod(string filing, string concept, string periodEnd, long expected)
    {
        XbrlDocument x = _read[filing].Xbrl;
        DateOnly end = DateOnly.Parse(periodEnd);

        List<decimal?> values = x.Facts
            .Where(f => f.Concept == concept && x.Contexts[f.ContextRef] is { Dimensions.Count: 0 } c && (c.EndDate ?? c.Instant) == end
                && (c.Instant is not null || c.StartDate!.Value.AddYears(1) > end))
            .Select(f => f.Number)
            .Distinct()
            .ToList();

        Assert.That(values, Is.EqualTo(new decimal?[] { (decimal)expected }));
    }

    [TestCase("MSFT-10K-2026.html", "MICROSOFT CORPORATION", "ONE MICROSOFT WAY", "2026-06-30", "DELOITTE & TOUCHE LLP")]
    [TestCase("NDAQ-10K-2025.html", "Nasdaq, Inc.", "151 W. 42nd Street,", "2025-12-31", "Ernst & Young LLP")]
    [TestCase("NFLX-10K-2025.html", "Netflix, Inc.", "121 Albright Way", "2025-12-31", "Ernst & Young LLP")]
    [TestCase("ORCL-10K-2026.html", "Oracle Corporation", "2300 Oracle Way", "2026-05-31", "Ernst & Young LLP")]
    public void Read_CoverFacts(string filing, string name, string address, string periodEnd, string auditor)
    {
        XbrlDocument x = _read[filing].Xbrl;

        Assert.That(x.First("dei:EntityRegistrantName")!.Text, Is.EqualTo(name));
        Assert.That(x.First("dei:EntityAddressAddressLine1")!.Text, Is.EqualTo(address));
        Assert.That(x.First("dei:DocumentPeriodEndDate")!.Text, Is.EqualTo(periodEnd));
        Assert.That(x.First("dei:AuditorName")!.Text, Is.EqualTo(auditor));
    }

    // Every filing declares exactly its five primary statements as Statement roles - including NDAQ's "Changes in
    // Stockholders' Equity", which v1's title patterns missed until the second review.
    [TestCaseSource(nameof(Filings))]
    public void Taxonomy_DeclaresFivePrimaryStatements_EachPresentingConcepts(string filing)
    {
        XbrlTaxonomy t = _read[filing].Taxonomy;

        Assert.That(t.PrimaryStatements.Count(), Is.EqualTo(5));
        Assert.That(t.PrimaryStatements.All(r => t.PresentedConcepts.GetValueOrDefault(r.Uri)?.Count > 0), Is.True);
    }

    [Test]
    public void Taxonomy_NdaqEquityStatement_IsAStatementRole()
    {
        Assert.That(_read["NDAQ-10K-2025.html"].Taxonomy.PrimaryStatements.Select(r => r.Title),
            Has.Some.Contains("Changes in Stockholders"));
    }

    [TestCase("MSFT-10K-2026.html", "msft:IntelligentCloudMember", "Intelligent Cloud [Member]")]
    [TestCase("NDAQ-10K-2025.html", "ndaq:CapitalAccessPlatformsMember", "Capital Access Platforms [Member]")]
    public void Taxonomy_LabelsCompanyMembers(string filing, string concept, string label)
    {
        Assert.That(_read[filing].Taxonomy.Labels[concept], Is.EqualTo(label));
    }

    // Step 1b-ii's profile, built from the cover facts. ORCL lists its preferred depositary shares ("ORCL PRD") first
    // and NDAQ four note issues besides its common stock: the symbol comes from the common stock's own context.
    // MSFT's all-capital cover values and NDAQ's "New York," (a comma inside the tag) are cleaned.
    [TestCase("MSFT-10K-2026.html", "Microsoft Corporation - annual report on Form 10-K for the fiscal year ended June 30, 2026 (fiscal year 2026).",
        "Common stock trading symbol: MSFT, on the Nasdaq.", "Address of principal executive offices: One Microsoft Way, Redmond, Washington 98052-6399.",
        "Independent registered public accounting firm (auditor): Deloitte & Touche LLP, Seattle, Washington (PCAOB ID 34).")]
    [TestCase("NDAQ-10K-2025.html", "Nasdaq, Inc. - annual report on Form 10-K for the fiscal year ended December 31, 2025 (fiscal year 2025).",
        "Common stock trading symbol: NDAQ, on the Nasdaq Stock Market.", "Address of principal executive offices: 151 W. 42nd Street, New York, New York 10036.",
        "Independent registered public accounting firm (auditor): Ernst & Young LLP, New York, New York (PCAOB ID 42).")]
    [TestCase("NFLX-10K-2025.html", "Netflix, Inc. - annual report on Form 10-K for the fiscal year ended December 31, 2025 (fiscal year 2025).",
        "Common stock trading symbol: NFLX, on the NASDAQ Global Select Market.", "Address of principal executive offices: 121 Albright Way, Los Gatos, California 95032.",
        "Independent registered public accounting firm (auditor): Ernst & Young LLP, San Jose, California (PCAOB ID 42).")]
    [TestCase("ORCL-10K-2026.html", "Oracle Corporation - annual report on Form 10-K for the fiscal year ended May 31, 2026 (fiscal year 2026).",
        "Common stock trading symbol: ORCL, on the New York Stock Exchange.", "Address of principal executive offices: 2300 Oracle Way, Austin, Texas 78741.",
        "Independent registered public accounting firm (auditor): Ernst & Young LLP, San Jose, California (PCAOB ID 42).")]
    public void FilingProfile_FromCoverFacts(string filing, string identity, string symbol, string address, string auditor)
    {
        string profile = FilingProfile.Build(_read[filing].Xbrl)!;

        Assert.That(profile, Does.StartWith(identity));
        Assert.That(profile, Does.Contain(symbol));
        Assert.That(profile, Does.Contain(address));
        Assert.That(profile, Does.EndWith(auditor));
    }

    // The SEC's cover-page codes map a displayed name to an EDGAR code ("Washington" -> "WA", "Nasdaq" -> "NASDAQ");
    // IxTransformations keeps the displayed text for these by design, so they're compared as not applicable.
    private static readonly HashSet<string> SecCodeConcepts =
        ["dei:EntityIncorporationStateCountryCode", "dei:EntityAddressStateOrProvince", "dei:SecurityExchangeName", "dei:EntityFilerCategory"];

    // EDGAR publishes each filing's facts extracted from its inline XBRL as plain XML (<name>_htm.xml) - the SEC's
    // own reading of the same tags. Every fact must match both ways, by concept, context and value (numbers as
    // numbers). HTML-valued facts (notes and policies, escape="true") and the SEC code concepts are left out.
    // This comparison found the fractional-year duration bug (MSFT's "2.3" is P2Y3M18D, not P2.3Y).
    // Runs for each filing whose extracted instance is in data/ - all four; a new filing without one is ignored, not failed.
    [TestCaseSource(nameof(Filings))]
    public void Read_MatchesEdgarsExtractedInstance(string filing)
    {
        XbrlDocument x = _read[filing].Xbrl;
        string ns = System.Xml.Linq.XDocument.Load(TaxonomyReader.FindForFiling(_read[filing].Page, _data)!.FullName).Root!.Attribute("targetNamespace")!.Value;
        FileInfo? instance = _data.GetFiles("*_htm.xml").FirstOrDefault(f => System.Xml.Linq.XDocument.Load(f.FullName).Root!
            .Attributes().Any(a => a.IsNamespaceDeclaration && a.Value == ns));
        if (instance is null)
        {
            Assert.Ignore($"No extracted instance for {filing} in data/.");
        }

        System.Xml.Linq.XElement root = System.Xml.Linq.XDocument.Load(instance.FullName).Root!;
        System.Xml.Linq.XName nil = System.Xml.Linq.XNamespace.Get("http://www.w3.org/2001/XMLSchema-instance") + "nil";
        HashSet<string> htmlValued = x.Facts.Where(f => f.IsTextBlock).Select(f => f.Concept).ToHashSet();
        bool Compared(string concept) => !htmlValued.Contains(concept) && !SecCodeConcepts.Contains(concept);

        HashSet<string> edgar = root.Elements()
            .Where(e => e.Attribute("contextRef") is not null && e.Attribute(nil)?.Value != "true")
            .Select(e => (Concept: $"{root.GetPrefixOfNamespace(e.Name.Namespace)}:{e.Name.LocalName}", Element: e))
            .Where(p => Compared(p.Concept))
            .Select(p => $"{p.Concept}|{p.Element.Attribute("contextRef")!.Value}|"
                + (p.Element.Attribute("unitRef") is not null ? Num(decimal.Parse(p.Element.Value, System.Globalization.CultureInfo.InvariantCulture)) : Normalize(p.Element.Value)))
            .ToHashSet();
        HashSet<string> ours = x.Facts
            .Where(f => !f.IsNil && Compared(f.Concept))
            .Select(f => $"{f.Concept}|{f.ContextRef}|{(f.IsNumeric ? Num(f.Number!.Value) : Normalize(f.Text!))}")
            .ToHashSet();

        Assert.That(ours.Count, Is.GreaterThan(1_000));
        Assert.That(ours.Except(edgar).Take(5), Is.Empty, "facts read differently from EDGAR");
        Assert.That(edgar.Except(ours).Take(5), Is.Empty, "EDGAR facts not read");

        static string Normalize(string s) => string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        static string Num(decimal d) => d.ToString("0.############################", System.Globalization.CultureInfo.InvariantCulture);
    }

    // NFLX's browser-saved copy lost its schemaRef; the taxonomy is found by the namespace the page declares.
    [Test]
    public void FindForFiling_NflxWithoutSchemaRef_IsFoundByNamespace()
    {
        Assert.That(TaxonomyReader.FindForFiling(_read["NFLX-10K-2025.html"].Page, _data)?.Name, Is.EqualTo("nflx-20251231.xsd"));
    }
}
