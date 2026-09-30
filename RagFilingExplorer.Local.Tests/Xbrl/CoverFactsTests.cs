using RagFilingExplorer.Local.Retrieval;
using RagFilingExplorer.Local.Xbrl;

namespace RagFilingExplorer.Local.Tests.Xbrl;

/// <summary>
/// Which registered securities are common equity: a filer can list several classes (Alphabet's GOOGL and GOOG) next
/// to notes and preferred stock, each security in its own context pairing title, symbol and exchange. The four real
/// filings have one class each (CompanyRegistryTests and the profile chunks in chunk-review/structured/ hold them).
/// </summary>
[TestFixture]
public class CoverFactsTests
{
    private static XbrlFact Fact(string concept, string context, string text) =>
        new(null, concept, context, null, false, false, null, 0, null, false, text, null, text, false, []);

    private static IEnumerable<XbrlFact> Security(string context, string title, string symbol, string exchange) =>
    [
        Fact("dei:Security12bTitle", context, title),
        Fact("dei:TradingSymbol", context, symbol),
        Fact("dei:SecurityExchangeName", context, exchange),
    ];

    // Alphabet's cover, reduced: two common classes, a note issue, a preferred stock listed first.
    private static readonly XbrlDocument TwoClasses = new(
        new Dictionary<string, XbrlContext>(),
        new Dictionary<string, XbrlUnit>(),
        [
            Fact("dei:EntityRegistrantName", "d", "Alphabet Inc."),
            .. Security("p", "Depositary Shares, each representing a 1/20th interest in a share of Series D Preferred Stock", "ABC PRD", "NASDAQ"),
            .. Security("a", "Class A Common Stock, $0.001 par value", "GOOGL", "NASDAQ"),
            .. Security("c", "Class C Capital Stock, $0.001 par value", "GOOG", "NASDAQ"),
            .. Security("b", "Class B Common Stock, $0.001 par value", "GOOGB", "NASDAQ"),
            .. Security("n", "1.750% Senior Notes due 2029", "GOOG29", "NASDAQ"),
        ]);

    [Test]
    public void CommonStocks_SeveralClasses_EachCommonClassInCoverOrder_NotNotesOrPreferred()
    {
        Assert.That(CoverFacts.CommonStocks(TwoClasses).Select(c => c.Symbol), Is.EqualTo(new[] { "GOOGL", "GOOGB" }));
    }

    [TestCase("Common Shares, no par value")]
    [TestCase("Ordinary Shares, nominal value $0.01")]
    [TestCase("American Depositary Shares, each representing one ordinary share")]
    public void CommonStocks_OtherNamesForCommonEquity_Count(string title)
    {
        XbrlDocument cover = new(new Dictionary<string, XbrlContext>(), new Dictionary<string, XbrlUnit>(), [.. Security("x", title, "XYZ", "NYSE")]);

        Assert.That(CoverFacts.CommonStocks(cover).Select(c => c.Symbol), Is.EqualTo(new[] { "XYZ" }));
    }

    [Test]
    public void Register_SeveralCommonClasses_EveryTickerRoutesToTheFiling()
    {
        CompanyRegistry registry = new([CompanyRegistry.Register("GOOGL-10K-2026.html", TwoClasses)]);

        Assert.That(registry.Registrations.Single().Names, Is.EqualTo(new[] { "Alphabet", "GOOGL", "GOOGB" }));
        Assert.That(registry.ResolveFilings("What was GOOGB's share count?"), Is.EqualTo(new[] { "GOOGL-10K-2026.html" }));
        Assert.That(registry.ResolveFilings("What is the coupon on GOOG29?"), Is.Empty);
    }

    [Test]
    public void EmbeddingContext_NameTickersFormAndFiscalYear()
    {
        XbrlDocument cover = TwoClasses with
        {
            Facts = [.. TwoClasses.Facts, Fact("dei:DocumentType", "d", "10-K"), Fact("dei:DocumentFiscalYearFocus", "d", "2026")],
        };

        Assert.That(CoverFacts.EmbeddingContext(cover), Is.EqualTo("Alphabet Inc. (GOOGL, GOOGB), Form 10-K for fiscal year 2026."));
    }

    // Step 1d was measured with these four lines typed by hand (the "co" variant); what ships must be exactly them.
    [TestCase("MSFT-10K-2026.html", "Microsoft Corporation (MSFT), Form 10-K for fiscal year 2026.")]
    [TestCase("NDAQ-10K-2025.html", "Nasdaq, Inc. (NDAQ), Form 10-K for fiscal year 2025.")]
    [TestCase("NFLX-10K-2025.html", "Netflix, Inc. (NFLX), Form 10-K for fiscal year 2025.")]
    [TestCase("ORCL-10K-2026.html", "Oracle Corporation (ORCL), Form 10-K for fiscal year 2026.")]
    public void EmbeddingContext_RealFiling_IsTheLineStep1dWasMeasuredWith(string filing, string expected)
    {
        string path = Path.Combine(RepoPaths.FindRoot(TestContext.CurrentContext.TestDirectory).FullName, "data", filing);
        byte[] bytes = File.ReadAllBytes(path);
        XbrlDocument xbrl = InlineXbrlReader.Read(new AngleSharp.Html.Parser.HtmlParser().ParseDocument(
            RagFilingExplorer.Local.Chunking.MarkItDownConverter.DetectEncoding(bytes).GetString(bytes)));

        Assert.That(CoverFacts.EmbeddingContext(xbrl), Is.EqualTo(expected));
    }

    // Five-letter tickers came out "Googl": the all-capitals-to-title-case cleaning meant for "MICROSOFT CORPORATION"
    // reached the symbol. The four filings' tickers have at most four letters, so nothing showed it.
    [Test]
    public void FilingProfile_SeveralCommonClasses_ListsEachWithItsTitle_TickersAsTagged()
    {
        Assert.That(FilingProfile.Build(TwoClasses), Does.Contain(
            "Common stock trading symbols: GOOGL (Class A Common Stock, $0.001 par value), on the Nasdaq; GOOGB (Class B Common Stock, $0.001 par value), on the Nasdaq."));
    }
}
