using Microsoft.Extensions.VectorData;

namespace RagFilingExplorer.Local.VectorStore;

/// <summary>
/// A stored, searchable filing chunk. <see cref="Text"/> is the embedding source only - per
/// Microsoft.Extensions.VectorData, a string property marked [VectorStoreVector] cannot be read back
/// after storage, so the actual chunk content is kept separately in <see cref="Content"/> for retrieval.
/// </summary>
internal sealed class FilingChunkRecord
{
    [VectorStoreKey]
    public int Key { get; set; }

    // IsIndexed: required for use in a VectorSearchOptions.Filter expression (e.g. filtering to one
    // company's filing before the vector comparison runs).
    [VectorStoreData(IsIndexed = true)]
    public string SourceFiling { get; set; } = string.Empty;

    [VectorStoreData]
    public string Heading { get; set; } = string.Empty;

    // Detected financial statement type (income_statement, balance_sheet, cash_flow_statement,
    // equity_statement, comprehensive_income, or "narrative" for everything else). IsIndexed: used
    // in a VectorSearchOptions.Filter alongside SourceFiling to narrow to the specific statement a
    // number-lookup question is asking about, not just the right company.
    [VectorStoreData(IsIndexed = true)]
    public string StatementType { get; set; } = "narrative";

    [VectorStoreData]
    public string Content { get; set; } = string.Empty;

    [VectorStoreVector(dimensions: 768)]
    public string Text { get; set; } = string.Empty;
}
