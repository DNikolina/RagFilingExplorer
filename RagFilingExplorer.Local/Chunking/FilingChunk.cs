namespace RagFilingExplorer.Local.Chunking;

/// <param name="StatementType">Set by a strategy that knows it (Structured, from the filer's taxonomy); null leaves
/// it to v1's title detection in FilingChunkRecords.</param>
/// <param name="EmbeddingContext">A line put before the chunk's text in its embedding text only - never in what the
/// model reads (Structured: the company and filing, from the cover facts); null for v1's strategies.</param>
internal sealed record FilingChunk(string SourceFiling, string Heading, string Content, int Tokens, string? StatementType = null, string? EmbeddingContext = null);
