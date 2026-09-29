using Microsoft.ML.Tokenizers;
using RagFilingExplorer.Local.Chunking;

namespace RagFilingExplorer.Local.Structured;

/// <summary>
/// Packs a section's blocks into chunks by v1's rules (<see cref="TokenChunker.Pack"/>: paragraphs packed to the
/// budget with light overlap, a table atomic or split between rows with its caption and context repeated, a
/// short lead-in and a short footer attached to a table) - given typed blocks instead of text, so each chunk
/// knows which blocks, and so which tables and facts, it holds.
/// </summary>
internal static class StructuredChunker
{
    public static List<StructuredChunk> Chunk(StructuredSection section, Tokenizer tokenizer, int maxTokens, int overlapTokens)
    {
        List<ChunkerBlock> input = new();
        List<FilingBlock> source = new(); // input[i] came from source[i]
        foreach (FilingBlock block in section.Blocks)
        {
            if (block is TableBlock table)
            {
                input.Add(new ChunkerBlock(table.Rows.Text, table.Rows));
                source.Add(block);
                continue;
            }

            // A paragraph with a whitespace-only line is two packing blocks, as it was in the section text.
            foreach (string part in TokenChunker.SplitText(block.Text))
            {
                input.Add(new ChunkerBlock(part, null));
                source.Add(block);
            }
        }

        return TokenChunker.Pack(input, tokenizer, maxTokens, overlapTokens)
            .Select(c => new StructuredChunk(section.Heading, c.Content, c.Tokens,
                c.Blocks.Select(i => source[i]).Distinct(ReferenceEqualityComparer.Instance).Cast<FilingBlock>().ToList()))
            .ToList();
    }
}
