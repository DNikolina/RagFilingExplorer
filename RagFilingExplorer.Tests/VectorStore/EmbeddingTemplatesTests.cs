using RagFilingExplorer.Core;
using RagFilingExplorer.Core.VectorStore;

namespace RagFilingExplorer.Tests.VectorStore;

[TestFixture]
public class EmbeddingTemplatesTests
{
    private const string CompanyLine = "Oracle Corporation (ORCL), Form 10-K for fiscal year 2026.";

    // The text nomic-embed-text's index was built and is searched with - a change here silently changes every vector.
    [TestCase("nomic-embed-text")]
    [TestCase("nomic-embed-text:latest")]
    public void For_NomicEmbedText_KeepsItsPrefixes(string model)
    {
        EmbeddingTemplates nomic = EmbeddingTemplates.For(model);

        Assert.That(nomic.Query("What was revenue?"), Is.EqualTo("search_query: What was revenue?"));
        Assert.That(nomic.Document(CompanyLine, "Some prose."), Is.EqualTo($"search_document: {CompanyLine}\nSome prose."));
        Assert.That(nomic.Document(null, "Some prose."), Is.EqualTo("search_document: Some prose."));
    }

    // Google's cards for google/embeddinggemma-300m and google/embeddinggemma-2: "task: search result | query: ", and
    // "title: none | text: " without a title.
    [TestCase("embeddinggemma")]
    [TestCase("embeddinggemma-2:270m-bf16-text")]
    public void For_EmbeddingGemma_UsesItsCardsTemplates(string model)
    {
        EmbeddingTemplates gemma = EmbeddingTemplates.For(model);

        Assert.That(gemma.Query("What was revenue?"), Is.EqualTo("task: search result | query: What was revenue?"));
        Assert.That(gemma.Document(CompanyLine, "Some prose."), Is.EqualTo($"title: {CompanyLine} | text: Some prose."));
        Assert.That(gemma.Document(null, "Some prose."), Is.EqualTo("title: none | text: Some prose."));
    }

    // Voyage's prompts come from the request's input_type, so the text carries none - a prefix here would be doubled.
    [TestCase("voyage-4-large")]
    [TestCase("voyage-finance-2")]
    public void For_Voyage_SendsTheTextAsIs(string model)
    {
        EmbeddingTemplates voyage = EmbeddingTemplates.For(model);

        Assert.That(voyage.Query("What was revenue?"), Is.EqualTo("What was revenue?"));
        Assert.That(voyage.Document(CompanyLine, "Some prose."), Is.EqualTo($"{CompanyLine}\nSome prose."));
        Assert.That(voyage.Document(null, "Some prose."), Is.EqualTo("Some prose."));
    }

    // Each card's native size - the index's vector column is created with it.
    [TestCase("nomic-embed-text", "Ollama", 768)]
    [TestCase("embeddinggemma", "Ollama", 768)]
    [TestCase("embeddinggemma-2:270m-bf16-text", "Ollama", 768)]
    [TestCase("voyage-4-large", "Voyage", 1024)]
    [TestCase("voyage-finance-2", "Voyage", 1024)]
    public void For_EachModel_HasItsProviderAndCardsVectorSize(string model, string provider, int dimensions)
    {
        EmbeddingTemplates templates = EmbeddingTemplates.For(model);

        Assert.That(templates.Provider.ToString(), Is.EqualTo(provider), "an internal enum can't be a public test's parameter");
        Assert.That(templates.Dimensions, Is.EqualTo(dimensions));
    }

    // A Voyage model asked of Ollama would fail only as "not pulled"; refused naming both settings instead.
    [Test]
    public void EnsureServedBy_AnotherProvider_ThrowsNamingBoth()
    {
        StartupException ex = Assert.Throws<StartupException>(() => EmbeddingTemplates.For("voyage-4-large").EnsureServedBy(EmbeddingProvider.Ollama));

        Assert.That(ex.Message, Does.Contain("Embedding:Model").And.Contain("Providers:Embedding"));
        Assert.DoesNotThrow(() => EmbeddingTemplates.For("nomic-embed-text").EnsureServedBy(EmbeddingProvider.Ollama));
    }

    [Test]
    public void For_UnlistedModel_ThrowsNamingIt()
    {
        StartupException ex = Assert.Throws<StartupException>(() => EmbeddingTemplates.For("mxbai-embed-large"));

        Assert.That(ex.Message, Does.Contain("mxbai-embed-large").And.Contain("Embedding:Model"));
    }

    // nomic-embed-text's index keeps its name, so it isn't rebuilt; any other model gets its own, tag included.
    [TestCase("nomic-embed-text", "rag.structured.db")]
    [TestCase("nomic-embed-text:latest", "rag.structured.db")]
    [TestCase("embeddinggemma", "rag.structured.embeddinggemma.db")]
    [TestCase("embeddinggemma-2:270m-bf16-text", "rag.structured.embeddinggemma-2-270m-bf16-text.db")]
    [TestCase("voyage-finance-2", "rag.structured.voyage-finance-2.db")]
    public void IndexFilesFor_EachModel_HasItsOwnIndex(string model, string fileName)
    {
        IndexFiles index = IndexFiles.For(new DirectoryInfo(Path.GetTempPath()), "structured", EmbeddingTemplates.For(model), IndexFiles.UnnamedChunkBudget);

        Assert.That(index.DbFileName, Is.EqualTo(fileName));
    }

    // Another chunk budget is another index, kept beside the 500-token one; indexes built at 500 keep their names.
    [TestCase("nomic-embed-text", 500, "rag.structured.db")]
    [TestCase("nomic-embed-text", 1000, "rag.structured.1000tok.db")]
    [TestCase("voyage-finance-2", 500, "rag.structured.voyage-finance-2.db")]
    [TestCase("voyage-finance-2", 10000, "rag.structured.voyage-finance-2.10000tok.db")]
    public void IndexFilesFor_ChunkBudget_NamedUnlessTheUnnamedOne(string model, int maxTokensPerChunk, string fileName)
    {
        IndexFiles index = IndexFiles.For(new DirectoryInfo(Path.GetTempPath()), "structured", EmbeddingTemplates.For(model), maxTokensPerChunk);

        Assert.That(index.DbFileName, Is.EqualTo(fileName));
    }
}
