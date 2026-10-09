using RagFilingExplorer.Local;
using RagFilingExplorer.Local.VectorStore;

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

    [Test]
    public void For_UnlistedModel_ThrowsNamingIt()
    {
        StartupException ex = Assert.Throws<StartupException>(() => EmbeddingTemplates.For("mxbai-embed-large"));

        Assert.That(ex.Message, Does.Contain("mxbai-embed-large").And.Contain("Ollama:EmbeddingModel"));
    }

    // nomic-embed-text's index keeps its name, so it isn't rebuilt; any other model gets its own, tag included.
    [TestCase("nomic-embed-text", "rag.structured.db")]
    [TestCase("nomic-embed-text:latest", "rag.structured.db")]
    [TestCase("embeddinggemma", "rag.structured.embeddinggemma.db")]
    [TestCase("embeddinggemma-2:270m-bf16-text", "rag.structured.embeddinggemma-2-270m-bf16-text.db")]
    public void IndexFilesFor_EachModel_HasItsOwnIndex(string model, string fileName)
    {
        IndexFiles index = IndexFiles.For(new DirectoryInfo(Path.GetTempPath()), "structured", EmbeddingTemplates.For(model));

        Assert.That(index.DbFileName, Is.EqualTo(fileName));
    }
}
