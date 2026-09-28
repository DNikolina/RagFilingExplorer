using System.Globalization;
using System.Text;
using AngleSharp.Dom;

namespace RagFilingExplorer.Local.Xbrl;

/// <summary>
/// Reads a filing's inline XBRL from its parsed page (step 1b-i): the contexts and units declared in the hidden
/// &lt;ix:header&gt; (XBRL 2.1 contexts and units, Dimensions 1.0 members) and every fact in the page (Inline XBRL
/// 1.1 ix:nonFraction / ix:nonNumeric), including those in ix:hidden. Works from the DOM, not patterns: facts
/// nest (MSFT's dei:DocumentPeriodEndDate wraps the "June 30" and "2026" facts - its value is "June 30, 2026"),
/// and a text block continues elsewhere through ix:continuation. Must run before anything removes the header.
///
/// Element and attribute names are matched lowercased: the HTML parser lowercases them ("ix:nonfraction",
/// "contextref"), and one filing here (NFLX, a browser-saved copy) isn't XHTML, so an XML reader isn't an option.
/// </summary>
internal static class InlineXbrlReader
{
    public static XbrlDocument Read(IDocument document)
    {
        IElement header = document.All.FirstOrDefault(e => e.LocalName == "ix:header")
            ?? throw new InvalidOperationException("No <ix:header> - not an inline XBRL filing.");

        Dictionary<string, XbrlContext> contexts = header.Descendants<IElement>()
            .Where(e => e.LocalName == "xbrli:context")
            .Select(ReadContext)
            .ToDictionary(c => c.Id);
        Dictionary<string, XbrlUnit> units = header.Descendants<IElement>()
            .Where(e => e.LocalName == "xbrli:unit")
            .Select(ReadUnit)
            .ToDictionary(u => u.Id);

        // Continuation targets by id: a fact's continuedat names the first; each may name the next.
        Dictionary<string, IElement> continuations = document.All
            .Where(e => e.LocalName == "ix:continuation" && e.Id is not null)
            .ToDictionary(e => e.Id!);

        List<XbrlFact> facts = document.All
            .Where(e => e.LocalName is "ix:nonfraction" or "ix:nonnumeric")
            .Select(e => ReadFact(e, continuations))
            .ToList();

        return new XbrlDocument(contexts, units, facts);
    }

    private static XbrlContext ReadContext(IElement context)
    {
        DateOnly? Date(string name) => context.Descendants<IElement>().FirstOrDefault(e => e.LocalName == name) is { } d
            ? DateOnly.ParseExact(d.TextContent.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : null;

        List<XbrlDimension> dimensions = context.Descendants<IElement>()
            .Where(e => e.LocalName is "xbrldi:explicitmember" or "xbrldi:typedmember")
            .Select(e => e.LocalName == "xbrldi:explicitmember"
                ? new XbrlDimension(Required(e, "dimension"), e.TextContent.Trim(), null)
                : new XbrlDimension(Required(e, "dimension"), null, e.TextContent.Trim()))
            .ToList();

        return new XbrlContext(Required(context, "id"), Date("xbrli:instant"), Date("xbrli:startdate"), Date("xbrli:enddate"), dimensions);
    }

    private static XbrlUnit ReadUnit(IElement unit)
    {
        List<string> Measures(IElement? scope) => scope is null ? [] :
            scope.Descendants<IElement>().Where(e => e.LocalName == "xbrli:measure").Select(e => e.TextContent.Trim()).ToList();

        IElement? divide = unit.Descendants<IElement>().FirstOrDefault(e => e.LocalName == "xbrli:divide");
        return divide is null
            ? new XbrlUnit(Required(unit, "id"), Measures(unit), [])
            : new XbrlUnit(Required(unit, "id"),
                Measures(divide.Descendants<IElement>().FirstOrDefault(e => e.LocalName == "xbrli:unitnumerator")),
                Measures(divide.Descendants<IElement>().FirstOrDefault(e => e.LocalName == "xbrli:unitdenominator")));
    }

    private static XbrlFact ReadFact(IElement element, Dictionary<string, IElement> continuations)
    {
        bool isNumeric = element.LocalName == "ix:nonfraction";
        bool isNil = element.GetAttribute("xsi:nil") is "true";
        string? format = element.GetAttribute("format");
        int scale = int.TryParse(element.GetAttribute("scale"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int s) ? s : 0;
        bool negated = element.GetAttribute("sign") == "-";
        string concept = Required(element, "name");

        // The fact's own element, then each continuation in chain order; a chain that loops or names a missing
        // id is an error, not a shorter text.
        List<IElement> elements = [element];
        HashSet<string> seen = new();
        for (string? next = element.GetAttribute("continuedat"); next is not null;)
        {
            if (!seen.Add(next) || !continuations.TryGetValue(next, out IElement? part))
            {
                throw new InvalidOperationException($"Fact {element.Id ?? concept}: continuation '{next}' is missing or loops.");
            }

            elements.Add(part);
            next = part.GetAttribute("continuedat");
        }

        // A continued fact's value is its pieces concatenated in order with nothing added (Inline XBRL 1.1, 4.1.1) - as EDGAR
        // extracts it: ORCL's CODM description reads "assessed.We have" where one piece ends and the next begins.
        string displayText = Collapse(string.Concat(elements.Select(RawText)));
        decimal? number = null;
        string? text = null;
        if (!isNil)
        {
            if (isNumeric)
            {
                decimal magnitude = IxTransformations.ToNumber(format, displayText) * Pow10(scale);
                number = negated ? -magnitude : magnitude;
            }
            else
            {
                text = IxTransformations.ToText(format, displayText);
            }
        }

        return new XbrlFact(element.Id, concept, Required(element, "contextref"), element.GetAttribute("unitref"),
            isNumeric, isNil, format, scale, element.GetAttribute("decimals"), negated, displayText, number, text,
            element.GetAttribute("escape") == "true", elements);
    }

    // The fact's text as displayed: all nested text (nested facts included - they're part of the value), minus
    // ix:exclude content (Inline XBRL 1.1, 4.1.3), whitespace as in the source - see Collapse.
    private static string RawText(IElement element)
    {
        StringBuilder text = new();
        foreach (IText node in element.Descendants<IText>())
        {
            if (!InsideExclude(node, element))
            {
                text.Append(node.Data);
            }
        }

        return text.ToString();
    }

    // Whitespace collapsed only after the pieces are joined: each piece keeps its own edges, so "five" + " years" across
    // a continuation is "five years", while "assessed." + "We" stays "assessed.We" (ORCL) - both as EDGAR has them.
    private static string Collapse(string text)
    {
        return string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool InsideExclude(IText node, IElement fact)
    {
        for (IElement? e = node.ParentElement; e is not null && e != fact; e = e.ParentElement)
        {
            if (e.LocalName == "ix:exclude")
            {
                return true;
            }
        }

        return false;
    }

    private static decimal Pow10(int scale)
    {
        decimal factor = 1m;
        for (int i = 0; i < Math.Abs(scale); i++)
        {
            factor *= 10m;
        }

        return scale >= 0 ? factor : 1m / factor;
    }

    private static string Required(IElement element, string attribute) =>
        element.GetAttribute(attribute) ?? throw new InvalidOperationException($"<{element.LocalName}> without '{attribute}'.");
}
