using System.Globalization;
using System.Text;

namespace RagFilingExplorer.Local.Xbrl;

/// <summary>
/// Step 1b-ii: a short, plain-sentence profile of the filing, built from its tagged cover facts (dei:), which
/// becomes one chunk headed "Cover Page". It answers identity questions - where the principal executive offices
/// are, who the auditor is, which fiscal year the report covers, the ticker - from one dense chunk, where v1
/// found them (or didn't) in a cover-page table or in Item 2 prose: NDAQ's address ranked 16-17th, and H11 was
/// right on Linearized only through the cover page's table (docs/Decision-Log.md, "structured-1c-early").
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

        if (CoverFacts.CommonStock(xbrl) is ({ } symbol, var exchange))
        {
            text.Append($" Common stock trading symbol: {symbol}{(exchange is null ? "" : $", on the {exchange.Replace("The ", "")}")}.");
        }

        string[] address = new[] { "dei:EntityAddressAddressLine1", "dei:EntityAddressAddressLine2", "dei:EntityAddressCityOrTown" }
            .Select(c => Clean(xbrl.First(c)?.Text)).Where(v => v is not null).Cast<string>().ToArray();
        string? state = Clean(xbrl.First("dei:EntityAddressStateOrProvince")?.Text);
        string? zip = Clean(xbrl.First("dei:EntityAddressPostalZipCode")?.Text);
        if (address.Length > 0)
        {
            text.Append($" Address of principal executive offices: {string.Join(", ", address)}{(state is null ? "" : ", " + state)}{(zip is null ? "" : " " + zip)}.");
        }

        if (Clean(xbrl.First("dei:EntityIncorporationStateCountryCode")?.Text) is { } incorporation)
        {
            text.Append($" State of incorporation: {incorporation}.");
        }

        if (Clean(xbrl.First("dei:AuditorName")?.Text) is { } auditor)
        {
            string? location = Clean(xbrl.First("dei:AuditorLocation")?.Text);
            string? firmId = Clean(xbrl.First("dei:AuditorFirmId")?.Text);
            text.Append($" Independent registered public accounting firm (auditor): {auditor}{(location is null ? "" : ", " + location)}"
                + $"{(firmId is null ? "" : $" (PCAOB ID {firmId})")}.");
        }

        return text.ToString();
    }

    private static string? Clean(string? value) => CoverFacts.Clean(value);
}
