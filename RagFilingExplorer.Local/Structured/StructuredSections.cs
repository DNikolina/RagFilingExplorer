using RagFilingExplorer.Local.Chunking;

namespace RagFilingExplorer.Local.Structured;

/// <summary>
/// Groups a filing's blocks into sections under their "PART ... &gt; Item ..." heading, by the same line rules
/// v1's <see cref="SectionSplitter"/> applies to text (<see cref="SectionSplitter.HeadingTracker"/>): a paragraph's
/// heading and page-noise lines are dropped, a statement title starts a new section; a table is never read for
/// headings (a table of contents' "PART I" rows would otherwise take the Part heading - see SectionSplitter).
/// </summary>
internal static class StructuredSections
{
    public static List<StructuredSection> Split(IEnumerable<FilingBlock> blocks)
    {
        List<StructuredSection> sections = new();
        SectionSplitter.HeadingTracker headings = new();
        List<FilingBlock> current = new();
        List<string> lines = new(); // the kept lines of the paragraph being read

        void EndParagraph()
        {
            if (lines.Count > 0)
            {
                current.Add(new TextBlock(string.Join('\n', lines)));
                lines.Clear();
            }
        }

        void FlushSection()
        {
            EndParagraph();
            if (current.Count > 0)
            {
                sections.Add(new StructuredSection(headings.Heading, current.ToList()));
                current.Clear();
            }
        }

        foreach (FilingBlock block in blocks)
        {
            if (block is TextBlock text)
            {
                foreach (string line in text.Paragraph.Split('\n'))
                {
                    if (headings.Read(line, FlushSection))
                    {
                        lines.Add(line);
                    }
                }

                EndParagraph();
            }
            else
            {
                current.Add(block);
            }
        }

        FlushSection();
        return sections;
    }
}
