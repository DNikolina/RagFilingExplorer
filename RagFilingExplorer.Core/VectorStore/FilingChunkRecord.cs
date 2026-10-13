using Microsoft.Extensions.VectorData;

namespace RagFilingExplorer.Core.VectorStore;

/// <summary>
/// A stored, searchable filing chunk. <see cref="Text"/> is the embedding source only - per
/// Microsoft.Extensions.VectorData, a string property marked [VectorStoreVector] cannot be read back
/// after storage, so the actual chunk content is kept separately in <see cref="Content"/> for retrieval. Its schema is
/// <see cref="Definition"/> rather than attributes: the vector's size is the embedding model's, known only at runtime.
/// </summary>
internal sealed class FilingChunkRecord
{
    /// <summary>The schema, the vector sized for the embedding model. Property names are the stored column names - renaming
    /// one orphans every built index's column.</summary>
    public static VectorStoreCollectionDefinition Definition(int dimensions) => new()
    {
        Properties =
        [
            new VectorStoreKeyProperty(nameof(Key), typeof(int)),
            new VectorStoreDataProperty(nameof(SourceFiling), typeof(string)) { IsIndexed = true },
            new VectorStoreDataProperty(nameof(Heading), typeof(string)),
            new VectorStoreDataProperty(nameof(StatementType), typeof(string)) { IsIndexed = true },
            new VectorStoreDataProperty(nameof(Content), typeof(string)),
            new VectorStoreVectorProperty(nameof(Text), typeof(string), dimensions),
        ],
    };

    public int Key { get; set; }

    // Indexed: required for use in a VectorSearchOptions.Filter expression (e.g. filtering to one
    // company's filing before the vector comparison runs).
    public string SourceFiling { get; set; } = string.Empty;

    public string Heading { get; set; } = string.Empty;

    // Detected financial statement type (income_statement, balance_sheet, cash_flow_statement,
    // equity_statement, comprehensive_income, or "narrative" for everything else). Indexed: used
    // in a VectorSearchOptions.Filter alongside SourceFiling to narrow to the specific statement a
    // number-lookup question is asking about, not just the right company.
    public string StatementType { get; set; } = "narrative";

    public string Content { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;
}
