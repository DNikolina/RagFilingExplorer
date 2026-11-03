using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Moq;
using RagFilingExplorer.Core;
using RagFilingExplorer.Core.VectorStore;

namespace RagFilingExplorer.Tests.VectorStore;

/// <summary>
/// The index build as the app runs it, on the smallest real filing and the shipped settings, with only the embedding
/// model faked. The manifest that marks a build complete is written by the app after this returns, so it isn't here.
/// </summary>
[TestFixture]
public class IndexBuilderTests
{
    private static readonly FileInfo Filing =
        new(Path.Combine(RepoPaths.FindRoot(AppContext.BaseDirectory).FullName, "data", "NFLX-10K-2025.html"));

    private string _tempDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"IndexBuilderTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        // Pooled connections keep the file open, and Windows won't delete an open file.
        SqliteConnection.ClearAllPools();
        Directory.Delete(_tempDirectory, recursive: true);
    }

    [Test]
    public async Task BuildAsync_RealFiling_StoresEveryChunkSearchablyAndWritesItsDump()
    {
        AppSettings settings = AppSettingsTests.LoadShippedSettings();
        EmbeddingTemplates templates = EmbeddingTemplates.For(settings.Embedding.Model);
        List<string> embedded = [];
        VectorStoreCollection<int, FilingChunkRecord> collection = await AppComposition.OpenCollectionAsync(
            new IndexFiles(Path.Combine(_tempDirectory, "rag.test.db")), Embedder(templates.Dimensions, embedded).Object, templates);

        await IndexBuilder.BuildAsync(collection, templates, settings, [Filing], _tempDirectory);

        List<FilingChunkRecord> stored = await collection.GetAsync(r => true, embedded.Count + 1).ToListAsync();
        int[] expectedKeys = [.. Enumerable.Range(1, embedded.Count)];
        Assert.That(stored.Select(r => r.Key).Order(), Is.EqualTo(expectedKeys));
        Assert.That(embedded[0], Does.Contain("Netflix"), "a chunk's embedding text opens with its company");

        // Each vector must reach its own chunk: the last chunk's text, embedded again, finds that chunk first.
        ReadOnlyMemory<float> lastChunk = Vector(embedded[^1], templates.Dimensions);
        List<VectorSearchResult<FilingChunkRecord>> found = await collection.SearchAsync(lastChunk, top: 1).ToListAsync();
        Assert.That(found.Single().Record.Key, Is.EqualTo(embedded.Count));

        string dump = await File.ReadAllTextAsync(Path.Combine(_tempDirectory, "NFLX-10K-2025.chunks.txt"));
        Assert.That(dump, Does.StartWith($"--- Chunk 1/{embedded.Count} |"));
    }

    // Upserts report no per-record outcome, so a record the store loses shows only in this count - and the app writes
    // the manifest that trusts the index once the build returns.
    [Test]
    public void BuildAsync_StoreHoldsFewerRecordsThanUpserted_Throws()
    {
        AppSettings settings = AppSettingsTests.LoadShippedSettings();
        Mock<VectorStoreCollection<int, FilingChunkRecord>> collection = new();
        collection
            .Setup(c => c.GetAsync(It.IsAny<Expression<Func<FilingChunkRecord, bool>>>(), It.IsAny<int>(), It.IsAny<FilteredRecordRetrievalOptions<FilingChunkRecord>?>(), It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerable.Empty<FilingChunkRecord>());

        InvalidOperationException ex = Assert.ThrowsAsync<InvalidOperationException>(() => IndexBuilder.BuildAsync(
            collection.Object, EmbeddingTemplates.For(settings.Embedding.Model), settings, [Filing], _tempDirectory))!;

        Assert.That(ex.Message, Does.Contain("holds 0 records"));
    }

    /// <summary>Stands in for the embedding model: the same text always gets the same vector, and different text a
    /// different one. <paramref name="embedded"/> collects each text, in order.</summary>
    private static Mock<IEmbeddingGenerator<string, Embedding<float>>> Embedder(int dimensions, List<string> embedded)
    {
        Mock<IEmbeddingGenerator<string, Embedding<float>>> embedder = new();
        embedder
            .Setup(e => e.GenerateAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<EmbeddingGenerationOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> values, EmbeddingGenerationOptions? _, CancellationToken _) =>
            {
                List<string> texts = [.. values];
                embedded.AddRange(texts);

                return new GeneratedEmbeddings<Embedding<float>>(texts.Select(text => new Embedding<float>(Vector(text, dimensions))));
            });

        return embedder;
    }

    // Seeded from the text's hash, not string.GetHashCode: that one differs between processes.
    private static float[] Vector(string text, int dimensions)
    {
        Random random = new(BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
        float[] vector = new float[dimensions];
        for (int i = 0; i < dimensions; i++)
        {
            vector[i] = random.NextSingle() - 0.5f;
        }

        return vector;
    }
}
