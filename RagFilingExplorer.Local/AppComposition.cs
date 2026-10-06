using CommunityToolkit.VectorData.SqliteVec;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using OllamaSharp;
using RagFilingExplorer.Local.Chunking;
using RagFilingExplorer.Local.Retrieval;
using RagFilingExplorer.Local.VectorStore;

namespace RagFilingExplorer.Local;

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

    /// <summary>One client for embeddings and one for chat, each with the configured timeout (see OllamaSetup).</summary>
    public static (OllamaApiClient Embedding, OllamaApiClient Chat) CreateOllamaClients(OllamaSettings ollama) =>
        (new OllamaApiClient(OllamaSetup.CreateHttpClient(ollama), ollama.EmbeddingModel),
         new OllamaApiClient(OllamaSetup.CreateHttpClient(ollama), ollama.ChatModel));

    /// <summary>The index's chunk collection, embedding with <paramref name="embedding"/>; created if it doesn't exist.</summary>
    public static async Task<VectorStoreCollection<int, FilingChunkRecord>> OpenCollectionAsync(IndexFiles index, OllamaApiClient embedding)
    {
        SqliteVectorStore vectorStore = new($"Data Source={index.DbPath}", new() { EmbeddingGenerator = embedding });
        VectorStoreCollection<int, FilingChunkRecord> collection = vectorStore.GetCollection<int, FilingChunkRecord>("chunks");
        await collection.EnsureCollectionExistsAsync();
        return collection;
    }

    /// <summary>
    /// The answer service over a built index. <paramref name="chatClient"/> answers the questions - the chat model's own
    /// client unless an evaluation passes one that caches responses; the thinking-capability check always asks Ollama.
    /// </summary>
    public static async Task<RagRuntime> CreateRuntimeAsync(
        AppSettings settings, IndexFiles index, VectorStoreCollection<int, FilingChunkRecord> collection, CompanyRegistry companies,
        OllamaApiClient chatApiClient, IChatClient? chatClient = null)
    {
        bool chatModelSupportsThinking = await OllamaSetup.ChatModelSupportsThinkingAsync(chatApiClient, settings.Ollama.ChatModel);

        // Metadata filtering (Microsoft's own retrieval-quality guidance ranks this above chunk-size/text
        // tweaks): a question that names a company is searched within that company's filing, and one that points
        // at a specific financial statement favours it - a hard filter under Vector search, a boost under Hybrid.
        // It targets cross-company and cross-statement contamination (an MSFT question pulling in ORCL chunks, or
        // a filing's many similarly-shaped tables burying the right one). See RagAnswerService/QueryIntentResolver
        // for the resolution + search + prompt + generation flow, and InteractiveSession for the question loop -
        // kept out of here so they can be unit-tested with mocked dependencies.
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
            collection, chatClient ?? chatApiClient, settings.Retrieval, chatModelSupportsThinking, companies, keywordIndex, reranker);
        return new RagRuntime(answerService, companies, reranker);
    }

    /// <summary>
    /// For an evaluation: the app as configured, over its existing index - never built here. A missing, incomplete or
    /// stale index is a <see cref="StartupException"/> naming the fix, as at the console's startup.
    /// </summary>
    public static async Task<RagRuntime> OpenExistingIndexAsync(AppSettings settings, DirectoryInfo repoRoot, IChatClient? chatClient = null)
    {
        FileInfo[] filings = FindFilings(repoRoot);
        CompanyRegistry companies = RegisterCompanies(filings);
        (OllamaApiClient embedding, OllamaApiClient chat) = CreateOllamaClients(settings.Ollama);
        await OllamaSetup.EnsureReadyAsync(chat, settings.Ollama);

        IndexFiles index = IndexFiles.For(repoRoot, ChunkingStrategies.FileName(settings.Chunking.Strategy));
        if (!index.Exists)
        {
            throw new StartupException($"{index.DbPath} doesn't exist - build it by running the app once (dotnet run --project RagFilingExplorer.Local).");
        }

        index.EnsureCurrent(IndexManifest.Create(settings, filings));
        VectorStoreCollection<int, FilingChunkRecord> collection = await OpenCollectionAsync(index, embedding);
        return await CreateRuntimeAsync(settings, index, collection, companies, chat, chatClient);
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
