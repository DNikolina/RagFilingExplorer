using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using RagFilingExplorer.Local.Xbrl;

namespace RagFilingExplorer.Local.Structured;

/// <summary>A note's extent in the page - from <see cref="Start"/> through the end of <see cref="End"/> - and its topic.</summary>
internal sealed record NoteSpan(IElement Start, IElement End, string Topic);

/// <summary>
/// Where each note to the financial statements sits, and its name, from the filing's own tags rather
/// than its headings (ORCL's notes are unnumbered; a heading pattern would be one more filer convention). Every note
/// is tagged as a text block, and the filer's taxonomy presents that concept in a Disclosure role titled with the
/// note's name ("995567 - Disclosure - INCOME TAXES"). In all four filings the notes are exactly the outermost such
/// blocks, none overlapping.
///
/// Rules, each from the filings:
/// - Text blocks of the SEC's form taxonomies (dei cover, cyd Item 1C, ecd Item 9B) aren't notes, whatever their role.
/// - Roles for a note's parts - "(Tables)", "(Policies)", "(Details...)" - aren't note roles.
/// - A note spans its first piece to its last continuation, page headers and footers between them included.
/// - Except when another note starts in a gap between pieces: NDAQ tags two notes, "Revenue from Contracts with
///   Customers" and "Deferred Revenue", as one continued fact with four notes between them. Each run of pieces is
///   a note of its own, and the runs take the fact's roles in sort-code (= document) order; any other count of
///   runs and roles fails loudly.
/// </summary>
internal static partial class NoteTopics
{
    private static readonly string[] FormTaxonomies = ["dei:", "cyd:", "ecd:"];
    private static readonly HashSet<string> SmallWords = ["a", "an", "and", "as", "at", "by", "for", "from", "in", "of", "on", "or", "the", "to", "with"];

    [GeneratedRegex(@"\((Tables|Policies|Details.*)\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex PartRoleRegex();

    public static List<NoteSpan> Find(IDocument page, XbrlDocument xbrl, XbrlTaxonomy taxonomy)
    {
        List<IElement> all = page.All.ToList();
        Dictionary<IElement, int> position = new(all.Count);
        for (int i = 0; i < all.Count; i++)
        {
            position[all[i]] = i;
        }

        int EndOf(IElement e) => position[e.Descendants<IElement>().LastOrDefault() ?? e];

        List<XbrlRole> noteRoles = taxonomy.Roles.Where(r => r.Type == "Disclosure" && !PartRoleRegex().IsMatch(r.Title)).ToList();
        List<(List<IElement> Pieces, List<XbrlRole> Roles)> notes = xbrl.Facts
            .Where(f => f.IsTextBlock && !FormTaxonomies.Any(f.Concept.StartsWith))
            .Select(f => (Pieces: f.Elements.OrderBy(e => position[e]).ToList(),
                Roles: noteRoles.Where(r => taxonomy.PresentedConcepts.GetValueOrDefault(r.Uri)?.Contains(f.Concept) == true)
                    .OrderBy(r => r.SortCode.Length).ThenBy(r => r.SortCode, StringComparer.Ordinal).ToList()))
            .Where(n => n.Roles.Count > 0)
            .ToList();
        List<int> starts = notes.Select(n => position[n.Pieces[0]]).ToList();

        List<(int Start, int End, NoteSpan Span)> spans = new();
        foreach ((List<IElement> pieces, List<XbrlRole> roles) in notes)
        {
            // Runs of pieces with no other note starting in the gap between them.
            List<List<IElement>> runs = [[pieces[0]]];
            for (int i = 1; i < pieces.Count; i++)
            {
                int gapStart = EndOf(pieces[i - 1]);
                int gapEnd = position[pieces[i]];
                if (starts.Any(s => s > gapStart && s < gapEnd))
                {
                    runs.Add([]);
                }

                runs[^1].Add(pieces[i]);
            }

            if (roles.Count != 1 && roles.Count != runs.Count)
            {
                throw new InvalidOperationException($"Note text block '{pieces[0].GetAttribute("name")}' has {runs.Count} runs of pieces but {roles.Count} note roles.");
            }

            for (int r = 0; r < runs.Count; r++)
            {
                string topic = TopicOf(roles[roles.Count == 1 ? 0 : r].Title);
                spans.Add((position[runs[r][0]], EndOf(runs[r][^1]), new NoteSpan(runs[r][0], runs[r][^1], topic)));
            }
        }

        // Outermost only (a note-role block inside a note is part of it); partial overlaps would make a block's note
        // ambiguous, so they fail.
        List<(int Start, int End, NoteSpan Span)> outer = spans
            .Where(s => !spans.Any(o => o != s && o.Start <= s.Start && o.End >= s.End && (o.Start, o.End) != (s.Start, s.End)))
            .OrderBy(s => s.Start)
            .ToList();
        for (int i = 1; i < outer.Count; i++)
        {
            if (outer[i].Start <= outer[i - 1].End)
            {
                throw new InvalidOperationException($"Notes '{outer[i - 1].Span.Topic}' and '{outer[i].Span.Topic}' overlap.");
            }
        }

        return outer.Select(s => s.Span).ToList();
    }

    /// <summary>A role title as a heading: all-capital titles (MSFT, ORCL) in title case, like the others' (NDAQ, NFLX).</summary>
    internal static string TopicOf(string roleTitle)
    {
        string title = roleTitle.Trim();
        if (title.Any(char.IsLower))
        {
            return title;
        }

        string[] words = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(title.ToLowerInvariant()).Split(' ');
        return string.Join(' ', words.Select((w, i) => i > 0 && SmallWords.Contains(w.ToLowerInvariant()) ? w.ToLowerInvariant() : w));
    }
}
