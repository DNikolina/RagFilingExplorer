using CommunityToolkit.VectorData.SqliteVec;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using RagFilingExplorer.Core.Chunking;
using RagFilingExplorer.Core.Retrieval;
using RagFilingExplorer.Core.VectorStore;

namespace RagFilingExplorer.Core;

/// <summary>
/// The answer service and what it was built with. Dispose it to release the reranker's model, if one is loaded.
/// </summary>
internal sealed class RagRuntime(RagAnswerService answerService, CompanyRegistry companies, CrossEncoderReranker? reranker) : IDisposable
{
    public RagAnswerService AnswerService { get; } = answerService;

    public CompanyRegistry Companies { get; } = companies;

    public CrossEncoderReranker? Reranker { get; } = reranker;

    public void Dispose() => Reranker?.Dispose();
}

/// <summary>
/// How the app is put together, in one place, for the console (Program.cs) and the evaluation - so an evaluation
/// measures the app exactly as it answers questions, not a copy of its wiring. Building an index stays in Program.cs:
/// <see cref="OpenExistingIndexAsync"/> only opens one that's already built and current.
/// </summary>
internal static class AppComposition
{
    /// <summary>The filings in data/, by name; none is a startup error.</summary>
    public static FileInfo[] FindFilings(DirectoryInfo repoRoot)
    {
        DirectoryInfo dataDirectory = new(Path.Combine(repoRoot.FullName, "data"));
        FileInfo[] filings = dataDirectory.Exists ? dataDirectory.GetFiles("*.html").OrderBy(f => f.Name).ToArray() : [];
        return filings.Length > 0 ? filings : throw new StartupException($"No *.html filings found in {dataDirectory.FullName}.");
    }

    /// <summary>
    /// Each filing registers its company from its own tagged cover facts (name and ticker) - no hand-kept table to forget
    /// when onboarding a filing (see CompanyRegistry).
    /// </summary>
    public static CompanyRegistry RegisterCompanies(FileInfo[] filings)
    {
        try
        {
            return CompanyRegistry.FromFilings(filings);
        }
        catch (InvalidOperationException ex)
        {
            throw new StartupException(ex.Message, ex);
        }
    }

    /// <summary>The index's chunk collection, embedding with <paramref name="embedding"/> (the model <paramref name="templates"/>
    /// describes); created if it doesn't exist.</summary>
    public static async Task<VectorStoreCollection<int, FilingChunkRecord>> OpenCollectionAsync(
        IndexFiles index, IEmbeddingGenerator<string, Embedding<float>> embedding, EmbeddingTemplates templates)
    {
        SqliteVectorStore vectorStore = new($"Data Source={index.DbPath}", new() { EmbeddingGenerator = embedding });
        VectorStoreCollection<int, FilingChunkRecord> collection =
            vectorStore.GetCollection<int, FilingChunkRecord>("chunks", FilingChunkRecord.Definition(templates.Dimensions));
        await collection.EnsureCollectionExistsAsync();
        return collection;
    }

    /// <summary>The answer service over a built index, answering with <paramref name="chatClient"/> as <paramref name="chatModel"/> describes.</summary>
    public static RagRuntime CreateRuntime(
        AppSettings settings, IndexFiles index, VectorStoreCollection<int, FilingChunkRecord> collection, CompanyRegistry companies,
        IChatClient chatClient, ChatModelOptions chatModel)
    {
        // Company and statement-type filtering, search, prompt and generation are RagAnswerService's (see
        // QueryIntentResolver for why the filters exist); the question loop is InteractiveSession's.
        //
        // Hybrid search (Retrieval:Search) adds an FTS5 keyword index to the same database - created on first use, so an
        // index built without one needs no rebuild (see KeywordIndex.EnsureCreated).
        KeywordIndex? keywordIndex = null;
        if (settings.Retrieval.Search == SearchMode.Hybrid)
        {
            keywordIndex = new KeywordIndex(index.DbPath);
            keywordIndex.EnsureCreated();
        }

        // Reranking (Retrieval:Rerank): a local cross-encoder reorders each company's top hybrid candidates. Its
        // model is fetched separately and checked against its recorded SHA-256 before it's loaded - see CrossEncoderReranker.
        CrossEncoderReranker? reranker = settings.Retrieval.Rerank ? LoadReranker(settings.Retrieval) : null;

        RagAnswerService answerService = new(
            collection, chatClient, settings.Retrieval, chatModel, companies, EmbeddingTemplates.For(settings.Embedding.Model), keywordIndex, reranker);
        return new RagRuntime(answerService, companies, reranker);
    }

    /// <summary>
    /// The app as configured, over its existing index - never built here: for an evaluation. A missing,
    /// incomplete or stale index is a <see cref="StartupException"/> naming the fix, as at the console's startup. The
    /// caller brings the providers - <paramref name="embedding"/> must be the model the index was built with.
    /// </summary>
    public static async Task<RagRuntime> OpenExistingIndexAsync(
        AppSettings settings, DirectoryInfo repoRoot, IEmbeddingGenerator<string, Embedding<float>> embedding,
        IChatClient chatClient, ChatModelOptions chatModel)
    {
        FileInfo[] filings = FindFilings(repoRoot);
        CompanyRegistry companies = RegisterCompanies(filings);

        EmbeddingTemplates templates = EmbeddingTemplates.For(settings.Embedding.Model);
        IndexFiles index = IndexFiles.For(repoRoot, ChunkingStrategies.FileName(settings.Chunking.Strategy), templates);
        if (!index.Exists)
        {
            throw new StartupException($"{index.DbPath} doesn't exist - build it by running the app once (dotnet run --project RagFilingExplorer.Cli).");
        }

        index.EnsureCurrent(IndexManifest.Create(settings, filings));
        VectorStoreCollection<int, FilingChunkRecord> collection = await OpenCollectionAsync(index, embedding, templates);
        return CreateRuntime(settings, index, collection, companies, chatClient, chatModel);
    }

    private static CrossEncoderReranker LoadReranker(RetrievalSettings retrieval)
    {
        if (retrieval.Search != SearchMode.Hybrid)
        {
            throw new StartupException("Retrieval:Rerank needs Retrieval:Search = Hybrid - reranking was measured on hybrid candidates only (docs/Decision-Log.md, \"Step 2b spike - measured\").");
        }

        try
        {
            return CrossEncoderReranker.Load(retrieval.RerankModelDirectory, retrieval.RerankModelSha256);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException)
        {
            throw new StartupException($"{ex.Message} Fetch the model as docs/Decision-Log.md, \"Step 2b resumed\" records, or set Retrieval:Rerank to false.", ex);
        }
    }
}
