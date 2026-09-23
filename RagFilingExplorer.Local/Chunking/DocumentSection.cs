namespace RagFilingExplorer.Local.Chunking;

/// <summary>A contiguous run of body text under a detected "PART ... &gt; Item ..." heading path.</summary>
internal sealed record DocumentSection(string Heading, string Body);
