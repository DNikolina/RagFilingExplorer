using System.Xml.Linq;
using AngleSharp.Dom;

namespace RagFilingExplorer.Local.Xbrl;

/// <summary>
/// Reads a filer's extension taxonomy from data/: the role list and, per role, the concepts its
/// presentation linkbase presents, plus each concept's standard label. Handles both layouts found across the four
/// filings - linkbases embedded in the .xsd (MSFT, ORCL - DFIN's packaging) or separate _pre/_lab/_cal/_def.xml
/// files named by the .xsd's link:linkbaseRef (NDAQ, NFLX). An XML parser, not patterns: attribute order differs
/// between the layouts and titles carry entities ("Stockholders&#8217; Equity").
/// </summary>
internal static class TaxonomyReader
{
    private static readonly XNamespace Link = "http://www.xbrl.org/2003/linkbase";
    private static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";
    private const string StandardLabelRole = "http://www.xbrl.org/2003/role/label";

    /// <summary>
    /// The taxonomy schema in <paramref name="dataDirectory"/> whose targetNamespace is one the filing's page
    /// declares (xmlns:nflx="http://www.netflix.com/20251231") - by namespace rather than the page's schemaRef,
    /// which NFLX's browser-saved copy no longer has. Null when none matches.
    /// </summary>
    public static FileInfo? FindForFiling(IDocument filing, DirectoryInfo dataDirectory)
    {
        HashSet<string> declared = filing.DocumentElement.Attributes
            .Where(a => a.Name.StartsWith("xmlns:", StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Value)
            .ToHashSet();
        return dataDirectory.GetFiles("*.xsd")
            .FirstOrDefault(f => XDocument.Load(f.FullName).Root?.Attribute("targetNamespace")?.Value is { } ns && declared.Contains(ns));
    }

    public static XbrlTaxonomy Read(FileInfo schema)
    {
        XDocument xsd = XDocument.Load(schema.FullName);
        List<XDocument> linkbases = [xsd];
        foreach (XElement reference in xsd.Descendants(Link + "linkbaseRef"))
        {
            string href = reference.Attribute(XLink + "href")?.Value
                ?? throw new InvalidOperationException($"{schema.Name}: linkbaseRef without href.");
            FileInfo file = new(Path.Combine(schema.DirectoryName!, href));
            if (!file.Exists)
            {
                throw new FileNotFoundException($"{schema.Name} references {href}, which isn't in {schema.DirectoryName}.", file.FullName);
            }

            linkbases.Add(XDocument.Load(file.FullName));
        }

        List<XbrlRole> roles = xsd.Descendants(Link + "roleType").Select(ReadRole).ToList();

        Dictionary<string, HashSet<string>> presented = [];
        foreach (XElement link in linkbases.SelectMany(d => d.Descendants(Link + "presentationLink")))
        {
            string role = link.Attribute(XLink + "role")!.Value;
            if (!presented.TryGetValue(role, out HashSet<string>? concepts))
            {
                presented[role] = concepts = [];
            }

            concepts.UnionWith(link.Elements(Link + "loc").Select(l => ConceptOf(l.Attribute(XLink + "href")!.Value)));
        }

        Dictionary<string, string> labels = [];
        foreach (XElement link in linkbases.SelectMany(d => d.Descendants(Link + "labelLink")))
        {
            Dictionary<string, string> locConcept = link.Elements(Link + "loc")
                .GroupBy(l => l.Attribute(XLink + "label")!.Value)
                .ToDictionary(g => g.Key, g => ConceptOf(g.First().Attribute(XLink + "href")!.Value));
            ILookup<string, XElement> labelsByKey = link.Elements(Link + "label").ToLookup(l => l.Attribute(XLink + "label")!.Value);
            foreach (XElement arc in link.Elements(Link + "labelArc"))
            {
                if (!locConcept.TryGetValue(arc.Attribute(XLink + "from")!.Value, out string? concept))
                {
                    continue;
                }

                XElement? standard = labelsByKey[arc.Attribute(XLink + "to")!.Value]
                    .FirstOrDefault(l => (l.Attribute(XLink + "role")?.Value ?? StandardLabelRole) == StandardLabelRole);
                if (standard is not null)
                {
                    labels.TryAdd(concept, standard.Value.Trim());
                }
            }
        }

        return new XbrlTaxonomy(roles, presented.ToDictionary(p => p.Key, p => (IReadOnlySet<string>)p.Value), labels);
    }

    // "9952155 - Statement - Consolidated Statements of Changes in Stockholders' Equity"
    private static XbrlRole ReadRole(XElement roleType)
    {
        string uri = roleType.Attribute("roleURI")!.Value;
        string definition = roleType.Element(Link + "definition")?.Value.Trim() ?? string.Empty;
        string[] parts = definition.Split(" - ", 3);
        return parts.Length == 3
            ? new XbrlRole(uri, parts[0].Trim(), parts[1].Trim(), parts[2].Trim())
            : new XbrlRole(uri, string.Empty, "(unknown)", definition);
    }

    // A locator's href ends in the concept's element id - "us-gaap-2025.xsd#us-gaap_Assets",
    // "msft-20260630.xsd#msft_IntelligentCloudMember" - which by taxonomy convention is prefix_Name. The prefixes
    // here contain '-' but never '_', so the first '_' is the separator: "us-gaap:Assets", matching fact names.
    private static string ConceptOf(string href)
    {
        string id = href[(href.IndexOf('#') + 1)..];
        int separator = id.IndexOf('_');
        return separator < 0 ? id : id[..separator] + ":" + id[(separator + 1)..];
    }
}
