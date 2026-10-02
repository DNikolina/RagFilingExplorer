using CommunityToolkit.VectorData.SqliteVec;
using Microsoft.Extensions.VectorData;
using OllamaSharp;
using RagFilingExplorer.Local;
using RagFilingExplorer.Local.Chunking;
using RagFilingExplorer.Local.Retrieval;
using RagFilingExplorer.Local.VectorStore;
using System.Text;

// UTF-8 in and out. Without it, output redirected to a log is written in the console's OEM code page, so
// the filings' non-breaking spaces became a lone 0xFF byte - invalid UTF-8 for the tools that read the logs
// (38-41 lines per eval log, and once inside an answer: granite's "$9.1 billion"). Setting these changes
// the console's code pages for the whole terminal session, so the originals are restored on exit.
Encoding originalOutputEncoding = Console.OutputEncoding;
Encoding originalInputEncoding = Console.InputEncoding;
Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

// Startup problems the user can fix themselves (Ollama not running, a model not pulled, a stale or
// incomplete index, ...) are reported as one clear message instead of a stack trace.
try
{
    await RunAsync(args);
    return 0;
}
catch (StartupException ex)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"[startup] {ex.Message}");
    return 1;
}
finally
{
    Console.OutputEncoding = originalOutputEncoding;
    Console.InputEncoding = originalInputEncoding;
}

static async Task RunAsync(string[] args)
{
    AppSettings settings = LoadSettings();

    DirectoryInfo repoRoot = RepoPaths.FindRoot(AppContext.BaseDirectory);

    // Each chunking strategy has its own index and chunk dumps - see IndexFiles.
    string strategyName = ChunkingStrategies.FileName(settings.Chunking.Strategy);
    IndexFiles index = IndexFiles.For(repoRoot, strategyName);
    string reviewDirectoryPath = Path.Combine(repoRoot.FullName, "chunk-review", strategyName);
    bool verbose = args.Contains("--verbose");
    Console.WriteLine($"Chunking strategy: {settings.Chunking.Strategy} (index: {index.DbFileName})");

    DirectoryInfo dataDirectory = new(Path.Combine(repoRoot.FullName, "data"));
    FileInfo[] filings = dataDirectory.Exists ? dataDirectory.GetFiles("*.html").OrderBy(f => f.Name).ToArray() : [];
    if (filings.Length == 0)
    {
        throw new StartupException($"No *.html filings found in {dataDirectory.FullName}.");
    }

    // Each filing registers its company from its own tagged cover facts (name and ticker) - no hand-kept table
    // to forget when onboarding a filing (see CompanyRegistry).
    CompanyRegistry companies;
    try
    {
        companies = CompanyRegistry.FromFilings(filings);
    }
    catch (InvalidOperationException ex)
    {
        throw new StartupException(ex.Message, ex);
    }

    foreach (CompanyRegistration registration in companies.Registrations)
    {
        Console.WriteLine($"Company filter: {string.Join(" / ", registration.Names)} -> {registration.Filing}");
    }

    // --chunks-only: chunk every filing and write chunk-review/<strategy>/, nothing else - no Ollama, no
    // index. Reading the real chunk output is how nearly every chunking bug here was found; this makes that
    // a one-minute loop instead of a ten-minute re-embed.
    if (args.Contains("--chunks-only"))
    {
        List<FilingChunk> chunks = await IndexBuilder.ChunkFilingsAsync(settings, filings, reviewDirectoryPath);
        Console.WriteLine($"Total chunks across all filings: {chunks.Count} (--chunks-only: index not touched)");
        return;
    }

    OllamaApiClient embeddingApiClient = new(OllamaSetup.CreateHttpClient(settings.Ollama), settings.Ollama.EmbeddingModel);
    OllamaApiClient chatApiClient = new(OllamaSetup.CreateHttpClient(settings.Ollama), settings.Ollama.ChatModel);
    await OllamaSetup.EnsureReadyAsync(chatApiClient, settings.Ollama);

    if (args.Contains("--rebuild"))
    {
        index.Delete();
    }

    IndexManifest currentManifest = IndexManifest.Create(settings, filings);
    bool indexExists = index.Exists;
    if (indexExists)
    {
        index.EnsureCurrent(currentManifest);
    }
    else
    {
        // A manifest without its index (e.g. the .db was deleted by hand) must not vouch for the next one.
        index.Delete();
    }

    SqliteVectorStore vectorStore = new($"Data Source={index.DbPath}", new() { EmbeddingGenerator = embeddingApiClient });
    VectorStoreCollection<int, FilingChunkRecord> collection = vectorStore.GetCollection<int, FilingChunkRecord>("chunks");
    await collection.EnsureCollectionExistsAsync();

    if (indexExists)
    {
        Console.WriteLine($"Found an up-to-date {index.DbPath} - skipping chunking and embedding. Run with --rebuild to force a fresh build.");
    }
    else
    {
        try
        {
            await IndexBuilder.BuildAsync(collection, settings, filings, reviewDirectoryPath);
        }
        catch (Exception ex)
        {
            throw new StartupException(
                $"Building the index failed: {ex.Message}\nThe partial {index.DbFileName} has no manifest, so it won't be used - "
                + "fix the problem above and run again with --rebuild.", ex);
        }

        // Written last, only once every chunk is in: its presence is what marks the index as complete.
        currentManifest.Save(index.ManifestPath);
    }

    bool chatModelSupportsThinking = await OllamaSetup.ChatModelSupportsThinkingAsync(chatApiClient, settings.Ollama.ChatModel);

    // Metadata filtering (Microsoft's own retrieval-quality guidance ranks this above chunk-size/text
    // tweaks): a question that names a company is searched within that company's filing, and one that points
    // at a specific financial statement favours it - a hard filter under Vector search, a boost under Hybrid.
    // Directly targets the cross-company/cross-statement contamination seen repeatedly in Step 7 testing
    // (e.g. an MSFT-specific question pulling in ORCL chunks, or a single filing's many similarly-shaped
    // "Item 15" tables burying the right one). See RagAnswerService/QueryIntentResolver for the resolution +
    // search + prompt + generation flow, and InteractiveSession for the question loop - kept out of here so
    // they can be unit-tested with mocked dependencies.
    //
    // Hybrid search (Retrieval:Search) adds an FTS5 keyword index to the same database - created on first use, so an
    // index built before hybrid search existed needs no rebuild (see KeywordIndex.EnsureCreated).
    KeywordIndex? keywordIndex = null;
    if (settings.Retrieval.Search == SearchMode.Hybrid)
    {
        keywordIndex = new KeywordIndex(index.DbPath);
        keywordIndex.EnsureCreated();
    }

    // Reranking (Retrieval:Rerank, v2 step 2b): a local cross-encoder reorders each company's top hybrid candidates. Its
    // model is fetched separately and checked against its recorded SHA-256 before it's loaded - see CrossEncoderReranker.
    using CrossEncoderReranker? reranker = settings.Retrieval.Rerank ? LoadReranker(settings.Retrieval) : null;

    Console.WriteLine(reranker is null
        ? $"Search: {settings.Retrieval.Search}"
        : $"Search: {settings.Retrieval.Search}, reranked by {reranker.Name} (each company's top {settings.Retrieval.RerankCandidates})");
    RagAnswerService ragAnswerService = new(collection, chatApiClient, settings.Retrieval, chatModelSupportsThinking, companies, keywordIndex, reranker);

    await InteractiveSession.RunAsync(ragAnswerService, verbose, settings.Retrieval);
}

static CrossEncoderReranker LoadReranker(RetrievalSettings retrieval)
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

// appsettings.json holds the tunable knobs (model names, chunk size, timeouts, top-K) - see
// AppSettings.cs for what's deliberately NOT here (keyword lists, regexes - domain logic, not config),
// and for why presence of every key is checked explicitly.
static AppSettings LoadSettings()
{
    try
    {
        return AppSettings.Load(AppContext.BaseDirectory);
    }
    catch (InvalidOperationException ex)
    {
        throw new StartupException(ex.Message, ex);
    }
}
