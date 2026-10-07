using RagFilingExplorer.Local.Chunking;

namespace RagFilingExplorer.Local.VectorStore;

/// <summary>
/// Turns chunks (every filing's, in document order) into the records stored in the index.
///
/// nomic-embed-text expects task-specific prefixes for good retrieval matching: "search_document: "
/// on stored text, "search_query: " on the query text at search time. EmbeddingTextBuilder enriches
/// table chunks with their row labels as plain text before that prefix, since sparse tables otherwise
/// embed poorly - see its doc comment. A chunk with an embedding context (Structured: "Oracle
/// Corporation (ORCL), Form 10-K for fiscal year 2026.") has that line first. Content stays exactly as
/// chunked - only Text (the embedding input) changes.
///
/// StatementType is tracked in document order: once a statement-title line (e.g. "CONSOLIDATED
/// STATEMENTS OF OPERATIONS") is seen, that type carries forward to subsequent chunks until a new title
/// line appears, the "Notes to Financial Statements" boundary resets it to narrative (see
/// StatementTypeDetector.IsNotesToFinancialStatementsBoundary - without this, whichever statement was
/// detected last leaks across every Note for the rest of the filing), or the filing changes - the same
/// "carry the nearest marker forward" pattern already used for row-group labels in TokenChunker. A chunk whose
/// strategy already set its type (Structured: from the filer's taxonomy, StatementLabels) keeps that type.
/// </summary>
internal static class FilingChunkRecords
{
    public static List<FilingChunkRecord> Build(IReadOnlyList<FilingChunk> allChunks)
    {
        List<FilingChunkRecord> records = new();
        string? currentStatementType = null;
        string? previousFiling = null;

        for (int i = 0; i < allChunks.Count; i++)
        {
            FilingChunk chunk = allChunks[i];

            if (chunk.SourceFiling != previousFiling)
            {
                currentStatementType = null;
                previousFiling = chunk.SourceFiling;
            }

            foreach (string line in chunk.Content.Split('\n'))
            {
                if (StatementTypeDetector.IsNotesToFinancialStatementsBoundary(line))
                {
                    currentStatementType = null;
                    continue;
                }

                string? detected = StatementTypeDetector.Detect(line);
                if (detected is not null)
                {
                    currentStatementType = detected;
                }
            }

            records.Add(new FilingChunkRecord
            {
                // Keys start at 1: an int key of 0 is the vector store's "generate a key" value. SqliteVec
                // stores a key-0 chunk under a generated key 1, which the real key-1 chunk then overwrites -
                // the index silently loses the filing's first chunk.
                Key = i + 1,
                SourceFiling = chunk.SourceFiling,
                Heading = chunk.Heading,
                StatementType = chunk.StatementType ?? currentStatementType ?? "narrative",
                Content = chunk.Content,
                Text = $"search_document: {(chunk.EmbeddingContext is null ? "" : chunk.EmbeddingContext + "\n")}"
                    + EmbeddingTextBuilder.Build(chunk.Heading, chunk.Content),
            });
        }

        return records;
    }
}
