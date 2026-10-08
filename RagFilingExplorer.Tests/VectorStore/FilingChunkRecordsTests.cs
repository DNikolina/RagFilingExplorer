using RagFilingExplorer.Local.Chunking;
using RagFilingExplorer.Local.VectorStore;

namespace RagFilingExplorer.Tests.VectorStore;

[TestFixture]
public class FilingChunkRecordsTests
{
    private static FilingChunk Chunk(string filing, string content) => new(filing, "PART II > Item 8. Financial Statements", content, 10);

    // Keys used to start at 0, the vector store's "generate a key" value for an int key: SqliteVec stored
    // the first chunk (MSFT's cover page) under a generated key 1, which the real key-1 chunk then
    // overwrote. Every index silently lacked MSFT's name, address, state of incorporation and fiscal year end.
    [Test]
    public void Build_Keys_StartAtOneAndAreUnique()
    {
        List<FilingChunk> chunks = [Chunk("A.html", "cover page"), Chunk("A.html", "business"), Chunk("B.html", "cover page")];

        List<FilingChunkRecord> records = FilingChunkRecords.Build(chunks);

        Assert.That(records.Select(r => r.Key), Is.EqualTo(new[] { 1, 2, 3 }));
    }

    [Test]
    public void Build_StatementTitle_CarriesForwardUntilNotesBoundary()
    {
        List<FilingChunk> chunks =
        [
            Chunk("A.html", "Auditor's report."),
            Chunk("A.html", "CONSOLIDATED BALANCE SHEETS\n| Total assets | 100 |"),
            Chunk("A.html", "| Total liabilities | 60 |"),
            Chunk("A.html", "NOTES TO CONSOLIDATED FINANCIAL STATEMENTS\nNote 1 text."),
        ];

        List<FilingChunkRecord> records = FilingChunkRecords.Build(chunks);

        Assert.That(records.Select(r => r.StatementType), Is.EqualTo(new[] { "narrative", "balance_sheet", "balance_sheet", "narrative" }));
    }

    [Test]
    public void Build_NewFiling_ResetsStatementType()
    {
        List<FilingChunk> chunks = [Chunk("A.html", "CONSOLIDATED BALANCE SHEETS\n| Total assets | 100 |"), Chunk("B.html", "Cover page.")];

        List<FilingChunkRecord> records = FilingChunkRecords.Build(chunks);

        Assert.That(records[1].StatementType, Is.EqualTo("narrative"));
    }

    [Test]
    public void Build_Text_IsTheNomicDocumentPrefixedEmbeddingInput()
    {
        List<FilingChunkRecord> records = FilingChunkRecords.Build([Chunk("A.html", "Some prose.")]);

        Assert.That(records[0].Text, Does.StartWith("search_document: PART II > Item 8. Financial Statements"));
        Assert.That(records[0].Content, Is.EqualTo("Some prose."));
    }

    // The context line opens the embedding text only - what the model reads (Content) is unchanged.
    [Test]
    public void Build_EmbeddingContext_OpensTheEmbeddingTextOnly()
    {
        FilingChunk chunk = Chunk("A.html", "Some prose.") with { EmbeddingContext = "Oracle Corporation (ORCL), Form 10-K for fiscal year 2026." };

        FilingChunkRecord record = FilingChunkRecords.Build([chunk])[0];

        Assert.That(record.Text, Is.EqualTo(
            "search_document: Oracle Corporation (ORCL), Form 10-K for fiscal year 2026.\nPART II > Item 8. Financial Statements\n\nSome prose."));
        Assert.That(record.Content, Is.EqualTo("Some prose."));
    }
}
