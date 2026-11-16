using RagFilingExplorer.Core.VectorStore;

namespace RagFilingExplorer.Tests.VectorStore;

/// <summary>
/// Whether an index on disk is trusted, and what --rebuild removes. The index's name is EmbeddingTemplatesTests', the
/// manifest comparison IndexManifestTests'; here the database is a placeholder file, never opened.
/// </summary>
[TestFixture]
public class IndexFilesTests
{
    private string _tempDirectory = null!;
    private IndexFiles _index = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"IndexFilesTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        _index = new IndexFiles(Path.Combine(_tempDirectory, "rag.structured.db"));
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_tempDirectory, recursive: true);

    private static IndexManifest Manifest(int maxTokens = 500, string filingHash = "aaa") => new()
    {
        EmbeddingModel = "nomic-embed-text",
        EmbeddingDimensions = 768,
        ChunkingStrategy = "Structured",
        TokenizerModel = "gpt-4",
        MaxTokensPerChunk = maxTokens,
        OverlapTokens = 50,
        FilingHashes = new() { ["A.html"] = filingHash },
    };

    // The manifest is written only once a build has finished: a database without one is a build that was interrupted,
    // holding some of the chunks and answering as if it held them all.
    [Test]
    public void EnsureCurrent_NoManifest_ThrowsNamingRebuild()
    {
        File.WriteAllText(_index.DbPath, "placeholder");

        StartupException ex = Assert.Throws<StartupException>(() => _index.EnsureCurrent(Manifest()))!;

        Assert.That(ex.Message, Does.Contain("rag.structured.db.manifest.json").And.Contain("has no build manifest").And.Contain("--rebuild"));
    }

    // A changed setting or filing must stop startup, each difference listed - otherwise the old index is used as if current.
    [Test]
    public void EnsureCurrent_ManifestDiffersFromTheCurrentOne_ThrowsListingEachDifference()
    {
        Manifest().Save(_index.ManifestPath);

        StartupException ex = Assert.Throws<StartupException>(() => _index.EnsureCurrent(Manifest(maxTokens: 1000, filingHash: "edited")))!;

        Assert.That(ex.Message, Does.Contain("is out of date").And.Contain("Chunking:MaxTokensPerChunk").And.Contain("A.html changed").And.Contain("--rebuild"));
    }

    [Test]
    public void EnsureCurrent_ManifestMatchesTheCurrentOne_Passes()
    {
        Manifest().Save(_index.ManifestPath);

        Assert.DoesNotThrow(() => _index.EnsureCurrent(Manifest()));
    }

    // SQLite's side files go too: they belong to the database being removed, not to the one built in its place.
    [Test]
    public void Delete_IndexWithItsSideFilesAndManifest_RemovesThemAndNothingElse()
    {
        string other = Path.Combine(_tempDirectory, "rag.structured.voyage-finance-2.db");
        foreach (string path in new[] { _index.DbPath, _index.DbPath + "-shm", _index.DbPath + "-wal", _index.ManifestPath, other })
        {
            File.WriteAllText(path, "placeholder");
        }

        Assert.That(_index.Exists, Is.True);

        _index.Delete();

        Assert.That(_index.Exists, Is.False);
        Assert.That(Directory.GetFiles(_tempDirectory), Is.EqualTo(new[] { other }), "another model's index is left alone");
    }

    // --rebuild on a first run, or after an interrupted build that left no manifest.
    [Test]
    public void Delete_SomeOrAllFilesAbsent_DoesNotFail()
    {
        Assert.DoesNotThrow(() => _index.Delete());

        File.WriteAllText(_index.DbPath, "placeholder");

        Assert.DoesNotThrow(() => _index.Delete());
        Assert.That(Directory.GetFiles(_tempDirectory), Is.Empty);
    }

    // The dumps of another chunk budget sit beside the 500-token ones, named as the index is.
    [TestCase(500, "structured")]
    [TestCase(1000, "structured.1000tok")]
    public void ReviewDirectory_ChunkBudget_NamedUnlessTheUnnamedOne(int maxTokensPerChunk, string folder)
    {
        DirectoryInfo root = new(_tempDirectory);

        Assert.That(IndexFiles.ReviewDirectory(root, "structured", maxTokensPerChunk), Is.EqualTo(Path.Combine(root.FullName, "chunk-review", folder)));
    }
}
