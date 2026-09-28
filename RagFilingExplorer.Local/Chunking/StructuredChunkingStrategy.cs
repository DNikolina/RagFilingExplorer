using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Microsoft.ML.Tokenizers;

namespace RagFilingExplorer.Local.Chunking;

/// <summary>
/// v2's strategy, built up in measured steps (docs/Decision-Log.md, "XBRL hybrid (v2)"). Step 1a: the filing
/// is read as a DOM and converted by <see cref="HtmlTextConverter"/> instead of the markitdown CLI; tables are
/// linearized exactly as in the Linearized strategy (its <see cref="LinearizedChunkingStrategy.LinearizeTables"/>,
/// called, not copied). So 1a differs from Linearized in one thing only - who converts the prose - and is
/// measured against it. Sections, chunking and statement-type tagging are the shared v1 code, unchanged.
/// </summary>
internal sealed class StructuredChunkingStrategy(Tokenizer tokenizer, int maxTokensPerChunk, int overlapTokens) : IChunkingStrategy
{
    public async Task<ChunkedFiling> ChunkAsync(FileInfo filing)
    {
        byte[] bytes = await File.ReadAllBytesAsync(filing.FullName);
        string html = MarkItDownConverter.DetectEncoding(bytes).GetString(bytes);
        string text = ConvertToText(html);
        List<DocumentSection> sections = SectionSplitter.Split(text);

        List<FilingChunk> chunks = new();
        foreach (DocumentSection section in sections)
        {
            foreach ((string content, int tokens) in TokenChunker.Chunk(section.Body, tokenizer, maxTokensPerChunk, overlapTokens))
            {
                chunks.Add(new FilingChunk(filing.Name, section.Heading, content, tokens));
            }
        }

        return new ChunkedFiling(sections, chunks);
    }

    /// <summary>
    /// Decoded filing HTML to the section/chunk input text. The hidden inline-XBRL header is removed as a DOM
    /// element here rather than by regex - it's the part of the filing v2's later steps will read.
    /// </summary>
    internal static string ConvertToText(string html)
    {
        HtmlParser parser = new();
        IHtmlDocument document = parser.ParseDocument(html);
        foreach (AngleSharp.Dom.IElement header in document.QuerySelectorAll("*").Where(e => e.LocalName == "ix:header").ToList())
        {
            header.Remove();
        }

        string linearized = LinearizedChunkingStrategy.LinearizeTables(document.DocumentElement.OuterHtml);
        return HtmlTextConverter.Convert(parser.ParseDocument(linearized));
    }
}
