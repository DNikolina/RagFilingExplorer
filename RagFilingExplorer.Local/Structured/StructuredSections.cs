using RagFilingExplorer.Local.Chunking;

namespace RagFilingExplorer.Local.Structured;

/// <summary>
/// Groups a filing's blocks into sections under their "PART ... &gt; Item ..." heading, by the same line rules
/// v1's <see cref="SectionSplitter"/> applies to text (<see cref="SectionSplitter.HeadingTracker"/>): a paragraph's
/// heading and page-noise lines are dropped, a statement title starts a new section; a table is never read for
/// headings (a table of contents' "PART I" rows would otherwise take the Part heading - see SectionSplitter).
/// A note to the financial statements is a section of its own, its topic the heading's last part
/// ("PART II &gt; Item 8. ... &gt; Income Taxes") - so no chunk spans two notes, and a chunk from the middle of a
/// note still says which note it is.
/// </summary>
internal static class StructuredSections
{
    public static List<StructuredSection> Split(IEnumerable<FilingBlock> blocks)
    {
        List<StructuredSection> sections = [];
        SectionSplitter.HeadingTracker headings = new();
        List<FilingBlock> current = [];
        string? currentTopic = null;
        List<string> lines = []; // the kept lines of the paragraph being read

        void EndParagraph()
        {
            if (lines.Count > 0)
            {
                current.Add(new TextBlock(string.Join('\n', lines)) { Topic = currentTopic });
                lines.Clear();
            }
        }

        void FlushSection()
        {
            EndParagraph();
            if (current.Count > 0)
            {
                string heading = currentTopic is null ? headings.Heading : $"{headings.Heading} > {currentTopic}";
                sections.Add(new StructuredSection(heading, current.ToList()));
                current.Clear();
            }
        }

        foreach (FilingBlock block in blocks)
        {
            if (block.Topic != currentTopic)
            {
                FlushSection();
                currentTopic = block.Topic;
            }

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
                // A statement table starts its own section when the title rules didn't - v1's title patterns miss some
                // filers' titles ("Statements of Financial Position"), and two statements sharing a section could share
                // a chunk, which can't be labelled. The text after the previous table (this statement's title and
                // units) moves with it.
                if (block is TableBlock { StatementType: not null } && current.Any(b => b is TableBlock { StatementType: not null }))
                {
                    EndParagraph();
                    int afterLastTable = current.FindLastIndex(b => b is TableBlock) + 1;
                    List<FilingBlock> lead = current.Skip(afterLastTable).ToList();
                    current.RemoveRange(afterLastTable, lead.Count);
                    FlushSection();
                    current.AddRange(lead);
                }

                current.Add(block);
            }
        }

        FlushSection();
        return sections;
    }
}
