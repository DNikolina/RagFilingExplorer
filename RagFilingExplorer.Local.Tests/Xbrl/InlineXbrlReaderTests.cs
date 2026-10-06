using AngleSharp.Html.Parser;
using RagFilingExplorer.Local.Xbrl;

namespace RagFilingExplorer.Local.Tests.Xbrl;

/// <summary>The reader's rules on small fixtures shaped like the filings' own markup.</summary>
[TestFixture]
public class InlineXbrlReaderTests
{
    private const string Header =
        "<div style=\"display:none\"><ix:header><ix:resources>"
        + "<xbrli:context id=\"FY25\"><xbrli:entity><xbrli:identifier scheme=\"http://www.sec.gov/CIK\">1</xbrli:identifier></xbrli:entity>"
        + "<xbrli:period><xbrli:startDate>2024-06-01</xbrli:startDate><xbrli:endDate>2025-05-31</xbrli:endDate></xbrli:period></xbrli:context>"
        + "<xbrli:context id=\"Cloud\"><xbrli:entity><xbrli:identifier scheme=\"http://www.sec.gov/CIK\">1</xbrli:identifier>"
        + "<xbrli:segment><xbrldi:explicitMember dimension=\"us-gaap:StatementBusinessSegmentsAxis\">msft:IntelligentCloudMember</xbrldi:explicitMember></xbrli:segment>"
        + "</xbrli:entity><xbrli:period><xbrli:instant>2026-06-30</xbrli:instant></xbrli:period></xbrli:context>"
        + "<xbrli:unit id=\"usd\"><xbrli:measure>iso4217:USD</xbrli:measure></xbrli:unit>"
        + "<xbrli:unit id=\"usdPerShare\"><xbrli:divide><xbrli:unitNumerator><xbrli:measure>iso4217:USD</xbrli:measure></xbrli:unitNumerator>"
        + "<xbrli:unitDenominator><xbrli:measure>xbrli:shares</xbrli:measure></xbrli:unitDenominator></xbrli:divide></xbrli:unit>"
        + "</ix:resources></ix:header></div>";

    private static XbrlDocument Read(string body) =>
        InlineXbrlReader.Read(new HtmlParser().ParseDocument($"<html><body>{Header}{body}</body></html>"));

    [Test]
    public void Read_ContextsAndUnits_CarryPeriodDimensionAndRatio()
    {
        XbrlDocument x = Read(string.Empty);

        Assert.That(x.Contexts["FY25"].StartDate, Is.EqualTo(new DateOnly(2024, 6, 1)));
        Assert.That(x.Contexts["Cloud"].Instant, Is.EqualTo(new DateOnly(2026, 6, 30)));
        Assert.That(x.Contexts["Cloud"].Dimensions.Single().Member, Is.EqualTo("msft:IntelligentCloudMember"));
        Assert.That(x.Units["usdPerShare"].ToString(), Is.EqualTo("iso4217:USD/xbrli:shares"));
    }

    // ORCL's accumulated deficit reads "4,309" on the page; sign="-" makes the stored value negative, and scale 6
    // makes it millions. Parentheses around a figure are presentation only - dividends in the equity statement
    // show as "(4,743)" and are stored positive, with no sign attribute.
    [Test]
    public void Read_NumberFact_AppliesScaleAndSign()
    {
        XbrlDocument x = Read("<p>(<ix:nonFraction name=\"us-gaap:RetainedEarningsAccumulatedDeficit\" contextRef=\"FY25\" unitRef=\"usd\" "
            + "scale=\"6\" decimals=\"-6\" sign=\"-\" format=\"ixt:num-dot-decimal\">4,309</ix:nonFraction>)</p>");

        Assert.That(x.Facts.Single().Number, Is.EqualTo(-4_309_000_000m));
        Assert.That(x.Facts.Single().IsNegated, Is.True);
    }

    // MSFT's dei:DocumentPeriodEndDate wraps two other facts ("June 30" and "2026"); its own value is all of it.
    [Test]
    public void Read_NestedFacts_OuterValueIncludesTheInnerText()
    {
        XbrlDocument x = Read("<p><ix:nonNumeric name=\"dei:DocumentPeriodEndDate\" contextRef=\"FY25\" format=\"ixt:date-monthname-day-year-en\">"
            + "<ix:nonNumeric name=\"dei:CurrentFiscalYearEndDate\" contextRef=\"FY25\" format=\"ixt:date-monthname-day-en\">June 30</ix:nonNumeric>, "
            + "<ix:nonNumeric name=\"dei:DocumentFiscalYearFocus\" contextRef=\"FY25\">2026</ix:nonNumeric></ix:nonNumeric></p>");

        Assert.That(x.First("dei:DocumentPeriodEndDate")!.Text, Is.EqualTo("2026-06-30"));
        Assert.That(x.First("dei:CurrentFiscalYearEndDate")!.Text, Is.EqualTo("--06-30"));
        Assert.That(x.Facts, Has.Count.EqualTo(3));
    }

    // A text block (a whole note) continues elsewhere through ix:continuation; without following the chain only
    // the note's first piece would carry its topic. The value is the pieces concatenated in order with nothing
    // added (Inline XBRL 1.1) - as EDGAR extracts it (ORCL: "assessed.We have" across a piece boundary).
    [Test]
    public void Read_TextBlockWithContinuations_JoinsTheChainInOrder()
    {
        XbrlDocument x = Read("<div><ix:nonNumeric name=\"us-gaap:IncomeTaxDisclosureTextBlock\" contextRef=\"FY25\" escape=\"true\" continuedAt=\"c1\">"
            + "<p>INCOME TAXES</p></ix:nonNumeric></div><p>page footer</p>"
            + "<ix:continuation id=\"c1\" continuedAt=\"c2\"><p>Deferred taxes</p></ix:continuation><p>another footer</p>"
            + "<ix:continuation id=\"c2\"><p>Uncertain positions</p></ix:continuation>");

        XbrlFact note = x.Facts.Single();
        Assert.That(note.IsTextBlock, Is.True);
        Assert.That(note.Elements, Has.Count.EqualTo(3));
        Assert.That(note.Text, Is.EqualTo("INCOME TAXESDeferred taxesUncertain positions"));
    }

    [Test]
    public void Read_ContinuationThatIsMissing_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => Read(
            "<ix:nonNumeric name=\"us-gaap:IncomeTaxDisclosureTextBlock\" contextRef=\"FY25\" escape=\"true\" continuedAt=\"nowhere\">x</ix:nonNumeric>"));
    }

    [Test]
    public void Read_NilFact_HasNoValue()
    {
        XbrlDocument x = Read("<ix:nonFraction name=\"us-gaap:CommitmentsAndContingencies\" contextRef=\"FY25\" unitRef=\"usd\" xsi:nil=\"true\"></ix:nonFraction>");

        Assert.That(x.Facts.Single().IsNil, Is.True);
        Assert.That(x.Facts.Single().Number, Is.Null);
    }
}
