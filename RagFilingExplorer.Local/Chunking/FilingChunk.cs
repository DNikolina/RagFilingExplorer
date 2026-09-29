namespace RagFilingExplorer.Local.Chunking;

/// <param name="StatementType">Set by a strategy that knows it (Structured, from the filer's taxonomy); null leaves
/// it to v1's title detection in FilingChunkRecords.</param>
internal sealed record FilingChunk(string SourceFiling, string Heading, string Content, int Tokens, string? StatementType = null);
