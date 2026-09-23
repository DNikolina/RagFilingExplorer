using RagFilingExplorer.Local.VectorStore;

namespace RagFilingExplorer.Local.Tests.VectorStore;

[TestFixture]
public class IndexManifestTests
{
    private string _tempDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"IndexManifestTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_tempDirectory, recursive: true);

    private static IndexManifest Make(
        string embeddingModel = "nomic-embed-text", int maxTokens = 500, int overlap = 50, SortedDictionary<string, string>? hashes = null) => new()
    {
        EmbeddingModel = embeddingModel,
        TokenizerModel = "gpt-4",
        MaxTokensPerChunk = maxTokens,
        OverlapTokens = overlap,
        FilingHashes = hashes ?? new() { ["A.html"] = "aaa", ["B.html"] = "bbb" },
    };

    [Test]
    public void DescribeDifferences_Identical_ReturnsNothing()
    {
        Assert.That(Make().DescribeDifferences(Make()), Is.Empty);
    }

    // The worst stale-index case: query vectors from one embedding model compared against stored vectors
    // from another return noise with no error at all.
    [Test]
    public void DescribeDifferences_EmbeddingModelChanged_IsReported()
    {
        List<string> differences = Make().DescribeDifferences(Make(embeddingModel: "mxbai-embed-large"));

        Assert.That(differences, Has.Count.EqualTo(1));
        Assert.That(differences[0], Does.Contain("Ollama:EmbeddingModel").And.Contain("mxbai-embed-large"));
    }

    [Test]
    public void DescribeDifferences_ChunkSettingsChanged_ReportsEach()
    {
        List<string> differences = Make().DescribeDifferences(Make(maxTokens: 256, overlap: 0));

        Assert.That(differences, Has.Count.EqualTo(2));
        Assert.That(differences, Has.Some.Contain("Chunking:MaxTokensPerChunk"));
        Assert.That(differences, Has.Some.Contain("Chunking:OverlapTokens"));
    }

    [Test]
    public void DescribeDifferences_FilingAddedRemovedAndChanged_ReportsEach()
    {
        IndexManifest built = Make(hashes: new() { ["A.html"] = "aaa", ["B.html"] = "bbb" });
        IndexManifest current = Make(hashes: new() { ["A.html"] = "aaa-edited", ["C.html"] = "ccc" });

        List<string> differences = built.DescribeDifferences(current);

        Assert.That(differences, Has.Count.EqualTo(3));
        Assert.That(differences, Has.Some.Contain("C.html was added"));
        Assert.That(differences, Has.Some.Contain("B.html was removed"));
        Assert.That(differences, Has.Some.Contain("A.html changed"));
    }

    [Test]
    public void SaveThenTryLoad_RoundTripsWithNoDifferences()
    {
        string path = Path.Combine(_tempDirectory, "rag.db.manifest.json");
        IndexManifest original = Make();

        original.Save(path);
        IndexManifest? loaded = IndexManifest.TryLoad(path);

        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.DescribeDifferences(original), Is.Empty);
    }

    [Test]
    public void TryLoad_MissingFile_ReturnsNull()
    {
        Assert.That(IndexManifest.TryLoad(Path.Combine(_tempDirectory, "nope.json")), Is.Null);
    }

    [Test]
    public void TryLoad_CorruptFile_ReturnsNull()
    {
        string path = Path.Combine(_tempDirectory, "rag.db.manifest.json");
        File.WriteAllText(path, "{ not json");

        Assert.That(IndexManifest.TryLoad(path), Is.Null);
    }

    [Test]
    public void Create_HashesFileContents_SoAnEditIsDetected()
    {
        FileInfo filing = new(Path.Combine(_tempDirectory, "X.html"));
        File.WriteAllText(filing.FullName, "<p>original</p>");
        AppSettings settings = AppSettingsTests.LoadShippedSettings();

        IndexManifest before = IndexManifest.Create(settings, [filing]);
        File.WriteAllText(filing.FullName, "<p>edited</p>");
        IndexManifest after = IndexManifest.Create(settings, [filing]);

        Assert.That(before.FilingHashes["X.html"], Has.Length.EqualTo(64));
        Assert.That(before.DescribeDifferences(after), Is.EqualTo(new[] { "data/X.html changed since the index was built" }));
    }
}
