using System.Globalization;
using System.Text;

namespace RagFilingExplorer.Local.Xbrl;

/// <summary>
/// A short, plain-sentence profile of the filing, built from its tagged cover facts (dei:), which
/// becomes one chunk headed "Cover Page". It answers identity questions - where the principal executive offices
/// are, who the auditor is, which fiscal year the report covers, the ticker - from one dense chunk, rather than
/// from a cover-page table or Item 2 prose, which rank poorly for such questions.
///
/// Only identity facts, deliberately - no share counts or public float: a profile naming the company and its
/// fiscal year could otherwise also rank for ordinary figure questions. The filing's own wording ("principal
/// executive offices", "independent registered public accounting firm"), not "headquarters". Checkbox facts are
/// left out: their meaning depends on which box carries the tag.
/// </summary>
internal static class FilingProfile
{
    public const string Heading = "Cover Page";

    public static string? Build(XbrlDocument xbrl)
    {
        string? name = CoverFacts.RegistrantName(xbrl);
        if (name is null)
        {
            return null;
        }

        StringBuilder text = new();
        string? periodEnd = xbrl.First("dei:DocumentPeriodEndDate")?.Text;
        string? fiscalYear = xbrl.First("dei:DocumentFiscalYearFocus")?.Text;
        string? formType = xbrl.First("dei:DocumentType")?.Text;
        text.Append($"{name} - annual report on Form {formType ?? "10-K"}");
        if (periodEnd is not null && DateOnly.TryParseExact(periodEnd, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly end))
        {
            text.Append($" for the fiscal year ended {end.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture)}");
        }

        text.Append(fiscalYear is null ? "." : $" (fiscal year {fiscalYear}).");

        List<(string Symbol, string? Exchange, string Title)> classes = CoverFacts.CommonStocks(xbrl);
        if (classes is [var single])
        {
            text.Append($" Common stock trading symbol: {single.Symbol}{OnExchange(single.Exchange)}.");
        }
        else if (classes.Count > 1)
        {
            // Several classes (Alphabet's GOOGL and GOOG): each symbol with the title that tells the classes apart.
            text.Append($" Common stock trading symbols: {string.Join("; ", classes.Select(c => $"{c.Symbol} ({c.Title}){OnExchange(c.Exchange)}"))}.");
        }

        string[] address = new[] { "dei:EntityAddressAddressLine1", "dei:EntityAddressAddressLine2", "dei:EntityAddressCityOrTown" }
            .Select(c => CoverFacts.Clean(xbrl.First(c)?.Text)).OfType<string>().ToArray();
        string? state = CoverFacts.Clean(xbrl.First("dei:EntityAddressStateOrProvince")?.Text);
        string? zip = CoverFacts.Clean(xbrl.First("dei:EntityAddressPostalZipCode")?.Text);
        if (address.Length > 0)
        {
            text.Append($" Address of principal executive offices: {string.Join(", ", address)}{(state is null ? "" : ", " + state)}{(zip is null ? "" : " " + zip)}.");
        }

        if (CoverFacts.Clean(xbrl.First("dei:EntityIncorporationStateCountryCode")?.Text) is { } incorporation)
        {
            text.Append($" State of incorporation: {incorporation}.");
        }

        if (CoverFacts.Clean(xbrl.First("dei:AuditorName")?.Text) is { } auditor)
        {
            string? location = CoverFacts.Clean(xbrl.First("dei:AuditorLocation")?.Text);
            string? firmId = CoverFacts.Clean(xbrl.First("dei:AuditorFirmId")?.Text);
            text.Append($" Independent registered public accounting firm (auditor): {auditor}{(location is null ? "" : ", " + location)}"
                + $"{(firmId is null ? "" : $" (PCAOB ID {firmId})")}.");
        }

        return text.ToString();
    }

    private static string OnExchange(string? exchange) => exchange is null ? "" : $", on the {exchange.Replace("The ", "")}";
}
