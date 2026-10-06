using RagFilingExplorer.Local.Chunking;
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
        string embeddingModel = "nomic-embed-text", int maxTokens = 500, int overlap = 50, SortedDictionary<string, string>? hashes = null,
        string strategy = "Markdown") => new()
    {
        EmbeddingModel = embeddingModel,
        ChunkingStrategy = strategy,
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

    // Each strategy has its own index file, but one renamed or copied by hand must not answer silently
    // from the other strategy's chunks.
    [Test]
    public void DescribeDifferences_ChunkingStrategyChanged_IsReported()
    {
        List<string> differences = Make().DescribeDifferences(Make(strategy: "Linearized"));

        Assert.That(differences, Has.Count.EqualTo(1));
        Assert.That(differences[0], Does.Contain("Chunking:Strategy").And.Contain("Linearized"));
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

    // The Structured strategy reads the filer's taxonomy too: an edited .xsd or linkbase changes statement types and
    // note topics, so it must make the index stale like an edited filing.
    [Test]
    public void Create_StructuredTaxonomyEdited_IsDetected()
    {
        FileInfo filing = new(Path.Combine(_tempDirectory, "X.html"));
        File.WriteAllText(filing.FullName, "<p>filing</p>");
        File.WriteAllText(Path.Combine(_tempDirectory, "x-20261231.xsd"), "<schema/>");
        File.WriteAllText(Path.Combine(_tempDirectory, "x-20261231_pre.xml"), "<linkbase/>");
        AppSettings settings = AppSettingsTests.LoadShippedSettings();

        IndexManifest before = IndexManifest.Create(settings, [filing]);
        File.WriteAllText(Path.Combine(_tempDirectory, "x-20261231_pre.xml"), "<linkbase>edited</linkbase>");
        IndexManifest after = IndexManifest.Create(settings, [filing]);

        Assert.That(before.FilingHashes.Keys, Is.EqualTo(new[] { "X.html", "x-20261231.xsd", "x-20261231_pre.xml" }));
        Assert.That(before.DescribeDifferences(after), Is.EqualTo(new[] { "data/x-20261231_pre.xml changed since the index was built" }));
    }

    // EDGAR's extracted instance is a test oracle the app never reads; downloading it must not make the index stale.
    // v1's strategies don't read the taxonomy at all.
    [Test]
    public void InputFiles_ExtractedInstanceAndOtherStrategies_LeftOut()
    {
        FileInfo filing = new(Path.Combine(_tempDirectory, "X.html"));
        File.WriteAllText(filing.FullName, "<p>filing</p>");
        File.WriteAllText(Path.Combine(_tempDirectory, "x-20261231.xsd"), "<schema/>");
        File.WriteAllText(Path.Combine(_tempDirectory, "x-20261231_htm.xml"), "<xbrl/>");

        Assert.That(IndexManifest.InputFiles(ChunkingStrategyKind.Structured, [filing]).Select(f => f.Name),
            Is.EqualTo(new[] { "X.html", "x-20261231.xsd" }));
        Assert.That(IndexManifest.InputFiles(ChunkingStrategyKind.Markdown, [filing]).Select(f => f.Name),
            Is.EqualTo(new[] { "X.html" }));
    }
}
