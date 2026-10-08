using RagFilingExplorer.Local.Retrieval;

namespace RagFilingExplorer.Tests.Retrieval;

[TestFixture]
public class KeywordQueryTests
{
    private static readonly string[] CompanyNames = ["Microsoft", "MSFT", "Oracle", "ORCL"];

    // R1: the keyword route sends "deferred revenues" to the income statement, and a hard filter kept its balance-sheet
    // answer out of reach. The keyword search sees the words themselves, with the company, the year and the function
    // words gone.
    [TestCase("What were Oracle's current deferred revenues as of May 31, 2026?", "\"current\" OR \"deferred\" OR \"revenues\" OR \"may\" OR \"31\"")]
    // T5: the verbatim phrase is there as words - "U.S." becomes "u" and a dropped "s".
    [TestCase("What was the recorded basis of Microsoft's U.S. government securities as of June 30, 2026?",
        "\"recorded\" OR \"basis\" OR \"u\" OR \"government\" OR \"securities\" OR \"june\" OR \"30\"")]
    [TestCase("What was Microsoft's Intelligent Cloud segment revenue?", "\"intelligent\" OR \"cloud\" OR \"segment\" OR \"revenue\"")]
    public void Build_Question_KeepsOnlyItsContentWords(string question, string expected)
    {
        Assert.That(KeywordQuery.Build(question, CompanyNames), Is.EqualTo(expected));
    }

    [Test]
    public void Build_RepeatedWord_AppearsOnce()
    {
        Assert.That(KeywordQuery.Build("Revenue and revenue growth", CompanyNames), Is.EqualTo("\"revenue\" OR \"growth\""));
    }

    // Every table has a year column, so a year would favour chunks for holding many of them, not for answering.
    [Test]
    public void Build_FourDigitYear_IsDropped_OtherNumbersKept()
    {
        Assert.That(KeywordQuery.Build("Payments due in fiscal 2028 under Note 12", CompanyNames), Is.EqualTo("\"payments\" OR \"due\" OR \"under\" OR \"note\" OR \"12\""));
    }

    [Test]
    public void Build_NothingButStopWordsAndNames_IsNull()
    {
        Assert.That(KeywordQuery.Build("What was Oracle's total for the year?", CompanyNames), Is.Null);
    }

    // Quoting is the only escaping FTS5 needs, which holds because words are letters and digits only: a quote, an
    // operator or a column filter in the question can't reach the MATCH expression.
    [Test]
    public void Build_FtsSyntaxInQuestion_IsReducedToQuotedWords()
    {
        Assert.That(KeywordQuery.Build("heading: \"cash\" NEAR(flow*)", CompanyNames),
            Is.EqualTo("\"heading\" OR \"cash\" OR \"near\" OR \"flow\""));
    }
}
