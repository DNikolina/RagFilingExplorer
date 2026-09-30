using Microsoft.Data.Sqlite;
using RagFilingExplorer.Local.VectorStore;

namespace RagFilingExplorer.Local.Tests.VectorStore;

/// <summary>
/// Against a real SQLite file (FTS5 ships in the bundled SQLite), holding a <c>chunks</c> table shaped as SqliteVec
/// writes it for <see cref="FilingChunkRecord"/> - the table the keyword index is built over.
/// </summary>
[TestFixture]
public class KeywordIndexTests
{
    private string _tempDirectory = null!;
    private string _dbPath = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"KeywordIndexTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        _dbPath = Path.Combine(_tempDirectory, "rag.test.db");
        Execute("""CREATE TABLE "chunks" ("Key" INTEGER PRIMARY KEY, "SourceFiling" TEXT NOT NULL, "Heading" TEXT NOT NULL, "StatementType" TEXT NOT NULL, "Content" TEXT NOT NULL)""");
    }

    [TearDown]
    public void TearDown()
    {
        // Pooled connections keep the file open, and Windows won't delete an open file.
        SqliteConnection.ClearAllPools();
        Directory.Delete(_tempDirectory, recursive: true);
    }

    private void Execute(string sql)
    {
        using SqliteConnection connection = new($"Data Source={_dbPath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private void AddChunk(int key, string filing, string content, string heading = "PART II > Item 8", string statementType = "narrative") =>
        Execute($"INSERT INTO chunks VALUES ({key}, '{filing}', '{heading}', '{statementType}', '{content}')");

    [Test]
    public void Search_MoreOfTheQuerysWords_RanksHigher()
    {
        AddChunk(1, "MSFT.html", "Revenue grew in every segment.");
        AddChunk(2, "MSFT.html", "Intelligent Cloud > Revenue - 2026: $137,791");
        AddChunk(3, "MSFT.html", "Leases are recorded at cost.");
        KeywordIndex index = new(_dbPath);
        index.EnsureCreated();

        List<FilingChunkRecord> results = index.Search("\"intelligent\" OR \"cloud\" OR \"revenue\"", filing: null, top: 10);

        Assert.That(results.Select(r => r.Key), Is.EqualTo(new[] { 2, 1 }), "chunk 3 holds none of the words");
        Assert.That(results[0].Content, Is.EqualTo("Intelligent Cloud > Revenue - 2026: $137,791"), "records come back whole");
    }

    [Test]
    public void Search_Filing_KeepsOnlyThatFilingsChunks()
    {
        AddChunk(1, "MSFT.html", "Deferred revenue");
        AddChunk(2, "ORCL.html", "Deferred revenues, current");
        KeywordIndex index = new(_dbPath);
        index.EnsureCreated();

        Assert.That(index.Search("\"deferred\"", "ORCL.html", 10).Select(r => r.Key), Is.EqualTo(new[] { 2 }));
    }

    // T5 asks for "U.S. government securities"; the Porter stemmer lets a plural in the question match a singular in
    // the filing and back.
    [Test]
    public void Search_PluralInQuery_MatchesSingularInChunk()
    {
        AddChunk(1, "MSFT.html", "U.S. government security holdings");
        KeywordIndex index = new(_dbPath);
        index.EnsureCreated();

        Assert.That(index.Search("\"securities\"", null, 10).Select(r => r.Key), Is.EqualTo(new[] { 1 }));
    }

    [Test]
    public void Search_HeadingWords_AreSearchedToo()
    {
        AddChunk(1, "NDAQ.html", "Balance at December 31", heading: "Goodwill and Acquired Intangible Assets");
        KeywordIndex index = new(_dbPath);
        index.EnsureCreated();

        Assert.That(index.Search("\"goodwill\"", null, 10).Select(r => r.Key), Is.EqualTo(new[] { 1 }));
    }

    [Test]
    public void Search_Top_LimitsTheResults()
    {
        for (int key = 1; key <= 5; key++)
        {
            AddChunk(key, "MSFT.html", "Revenue");
        }

        KeywordIndex index = new(_dbPath);
        index.EnsureCreated();

        Assert.That(index.Search("\"revenue\"", null, 3), Has.Count.EqualTo(3));
    }

    // Startup calls this on every hybrid run; the second call must neither fail nor index anything twice.
    [Test]
    public void EnsureCreated_CalledTwice_IndexesEachChunkOnce()
    {
        AddChunk(1, "MSFT.html", "Revenue");
        KeywordIndex index = new(_dbPath);
        index.EnsureCreated();
        index.EnsureCreated();

        Assert.That(index.Search("\"revenue\"", null, 10), Has.Count.EqualTo(1));
    }
}
