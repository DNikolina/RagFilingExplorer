using RagFilingExplorer.Local.Xbrl;

namespace RagFilingExplorer.Local.Tests.Xbrl;

/// <summary>The format codes the four filings use (step 1b-i), each with a displayed form found in them.</summary>
[TestFixture]
public class IxTransformationsTests
{
    [TestCase(null, "13.70", 13.70)]
    [TestCase("ixt:num-dot-decimal", "7,425,545,491", 7425545491.0)]
    [TestCase("ixt:num-dot-decimal", "3.6", 3.6)]
    [TestCase("ixt:fixed-zero", "—", 0)]
    [TestCase("ixt:fixed-zero", "No", 0)]
    [TestCase("ixt-sec:numwordsen", "three", 3)]
    [TestCase("ixt-sec:numwordsen", "No", 0)]
    public void ToNumber_KnownFormat_GivesTheStoredValue(string? format, string shown, decimal expected)
    {
        Assert.That(IxTransformations.ToNumber(format, shown), Is.EqualTo(expected));
    }

    [TestCase("ixt:date-monthname-day-year-en", "December 11, 2025", "2025-12-11")]
    [TestCase("ixt:date-year-month-day", "2027-06-01", "2027-06-01")]
    [TestCase("ixt:date-monthname-year-en", "September 2026", "2026-09")]
    [TestCase("ixt:date-monthname-day-en", "June 30", "--06-30")]
    [TestCase("ixt-sec:duryear", "15", "P15Y")]
    [TestCase("ixt-sec:duryear", "2.3", "P2Y3M18D")]
    [TestCase("ixt-sec:durday", "268", "P268D")]
    [TestCase("ixt-sec:durwordsen", "six years", "P6Y")]
    [TestCase("ixt-sec:boolballotbox", "☒", "true")]
    [TestCase("ixt-sec:boolballotbox", "☐", "false")]
    [TestCase("ixt:fixed-true", "We maintain a systematic process", "true")]
    public void ToText_KnownFormat_GivesTheCanonicalValue(string format, string shown, string expected)
    {
        Assert.That(IxTransformations.ToText(format, shown), Is.EqualTo(expected));
    }

    // NDAQ: "one- year", hyphenated across a line break - the first code the fail-loudly rule caught.
    [Test]
    public void ToText_DurationInWords_ReadsAHyphenAsASpace()
    {
        Assert.That(IxTransformations.ToText("ixt-sec:durwordsen", "one- year"), Is.EqualTo("P1Y"));
    }

    // A guessed conversion would be a quietly wrong number; an unknown code must stop the build instead.
    [Test]
    public void ToNumber_UnknownFormat_Throws()
    {
        Assert.Throws<NotSupportedException>(() => IxTransformations.ToNumber("ixt:num-comma-decimal", "1.234,5"));
        Assert.Throws<NotSupportedException>(() => IxTransformations.ToText("ixt:date-day-month-year", "30/06/2026"));
    }
}
