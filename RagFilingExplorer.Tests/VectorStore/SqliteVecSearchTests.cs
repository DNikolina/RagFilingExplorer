using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Moq;
using RagFilingExplorer.Core;
using RagFilingExplorer.Core.VectorStore;

namespace RagFilingExplorer.Tests.VectorStore;

/// <summary>
/// Through the real SqliteVec store, opened by <see cref="AppComposition.OpenCollectionAsync"/>, in a temp file. Every
/// other test fakes the vector store, so this is the only one that loads the native vec0 library - the part of the app
/// built per OS.
/// </summary>
[TestFixture]
public class SqliteVecSearchTests
{
    private string _tempDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"SqliteVecSearchTests-{Guid.NewGuid():N}");
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
    public async Task SearchAsync_TwoStoredChunks_ReturnsTheNearestFirst()
    {
        EmbeddingTemplates templates = EmbeddingTemplates.For("nomic-embed-text");
        Dictionary<string, float[]> vectors = new()
        {
            ["near"] = Vector(templates.Dimensions, first: 1f, second: 0f),
            ["far"] = Vector(templates.Dimensions, first: 0f, second: 1f),
        };
        Mock<IEmbeddingGenerator<string, Embedding<float>>> embeddings = new();
        embeddings
            .Setup(e => e.GenerateAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<EmbeddingGenerationOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> values, EmbeddingGenerationOptions? _, CancellationToken _) =>
                new GeneratedEmbeddings<Embedding<float>>(values.Select(value => new Embedding<float>(vectors[value]))));
        VectorStoreCollection<int, FilingChunkRecord> collection = await AppComposition.OpenCollectionAsync(
            new IndexFiles(Path.Combine(_tempDirectory, "rag.test.db")), embeddings.Object, templates);

        // One at a time: multi-record upserts throw on this SqliteVec version (VectorStoreSettings.UpsertBatchSize).
        // The far chunk goes in first, so insertion order can't pass the test.
        await collection.UpsertAsync(new FilingChunkRecord { Key = 1, Text = "far", Content = "the far chunk" });
        await collection.UpsertAsync(new FilingChunkRecord { Key = 2, Text = "near", Content = "the near chunk" });

        List<VectorSearchResult<FilingChunkRecord>> results = await collection
            .SearchAsync(new ReadOnlyMemory<float>(Vector(templates.Dimensions, first: 0.9f, second: 0.1f)), top: 2)
            .ToListAsync();

        Assert.That(results.Select(result => result.Record.Content), Is.EqualTo(new[] { "the near chunk", "the far chunk" }));
    }

    private static float[] Vector(int size, float first, float second)
    {
        float[] vector = new float[size];
        vector[0] = first;
        vector[1] = second;

        return vector;
    }
}
