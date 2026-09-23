namespace RagFilingExplorer.Local.Chunking;

internal sealed record FilingChunk(string SourceFiling, string Heading, string Content, int Tokens);
