using Microsoft.Data.Sqlite;

namespace RagFilingExplorer.Local.VectorStore;

/// <summary>
/// The keyword half of hybrid search: an SQLite FTS5 index over each chunk's heading and content, ranked by FTS5's
/// built-in <c>bm25()</c>. It lives in the strategy's own rag.&lt;strategy&gt;.db as an external-content table over
/// the <c>chunks</c> table the vector store writes (SqliteVec keeps each record's data properties there, by property
/// name), so the text isn't stored twice and the index can always be rebuilt from what's there. SqliteVec has no
/// hybrid search of its own - neither does any MEVD SQLite or PostgreSQL connector (docs/Decision-Log.md, "Storage
/// stays SQLite") - so this is a plain query next to it, fused in <see cref="Retrieval.RankFusion"/>.
///
/// The Porter stemmer matches "securities" to "security", "expenses" to "expense". bm25's term statistics come from
/// every filing, and the company filter applies after ranking - the same as the vector side.
/// </summary>
internal sealed class KeywordIndex(string dbPath)
{
    private const string Table = "chunks_fts";

    private string ConnectionString => $"Data Source={dbPath}";

    /// <summary>
    /// Creates and fills the index if the database doesn't have it yet - a fresh build, or an index built without
    /// one. Cheap (about a second for ~1,000 chunks), and derived only from <c>chunks</c>: a rebuild deletes
    /// the whole database, so the two can't drift apart.
    /// </summary>
    public void EnsureCreated()
    {
        using SqliteConnection connection = new(ConnectionString);
        connection.Open();
        using SqliteCommand exists = connection.CreateCommand();
        exists.CommandText = $"SELECT count(*) FROM sqlite_master WHERE name = '{Table}'";
        if ((long)exists.ExecuteScalar()! > 0)
        {
            return;
        }

        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand create = connection.CreateCommand();
        create.CommandText = $"""
            CREATE VIRTUAL TABLE {Table} USING fts5(Heading, Content, content='chunks', content_rowid='Key', tokenize='porter unicode61');
            INSERT INTO {Table}({Table}) VALUES ('rebuild');
            """;
        create.ExecuteNonQuery();
        transaction.Commit();
    }

    /// <summary>
    /// The best-matching chunks for an FTS5 MATCH expression (see <see cref="Retrieval.KeywordQuery"/>), best first,
    /// optionally within one filing. Ties go to the lower key, so a run is repeatable.
    /// </summary>
    public List<FilingChunkRecord> Search(string matchExpression, string? filing, int top)
    {
        using SqliteConnection connection = new(ConnectionString);
        connection.Open();
        using SqliteCommand search = connection.CreateCommand();
        search.CommandText = $"""
            SELECT c.Key, c.SourceFiling, c.Heading, c.StatementType, c.Content
            FROM {Table} JOIN chunks c ON c.Key = {Table}.rowid
            WHERE {Table} MATCH $match AND ($filing IS NULL OR c.SourceFiling = $filing)
            ORDER BY bm25({Table}), c.Key
            LIMIT $top
            """;
        search.Parameters.AddWithValue("$match", matchExpression);
        search.Parameters.AddWithValue("$filing", (object?)filing ?? DBNull.Value);
        search.Parameters.AddWithValue("$top", top);

        List<FilingChunkRecord> results = new();
        using SqliteDataReader reader = search.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new FilingChunkRecord
            {
                Key = reader.GetInt32(0),
                SourceFiling = reader.GetString(1),
                Heading = reader.GetString(2),
                StatementType = reader.GetString(3),
                Content = reader.GetString(4),
            });
        }

        return results;
    }
}
