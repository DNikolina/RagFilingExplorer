using CommunityToolkit.VectorData.SqliteVec;
using Microsoft.Extensions.VectorData;
using OllamaSharp;
using RagFilingExplorer.Local;
using RagFilingExplorer.Local.Chunking;
using RagFilingExplorer.Local.Retrieval;
using RagFilingExplorer.Local.VectorStore;

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

    foreach (string problem in QueryIntentResolver.FindRegistrationProblems(filings.Select(f => f.Name)))
    {
        Console.WriteLine($"[warning] {problem}");
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
    // tweaks): if a question clearly names exactly one company (and/or points at one specific financial
    // statement), restrict the vector search accordingly before ranking runs. Directly targets the
    // cross-company/cross-statement contamination seen repeatedly in Step 7 testing (e.g. an MSFT-specific
    // question pulling in ORCL chunks, or a single filing's many similarly-shaped "Item 15" tables burying
    // the right one). See RagAnswerService/QueryIntentResolver for the actual resolution + search + prompt
    // + generation flow - extracted out of this loop so it can be unit-tested with mocked dependencies.
    RagAnswerService ragAnswerService = new(collection, chatApiClient, settings.Retrieval, chatModelSupportsThinking);

    await InteractiveSession.RunAsync(ragAnswerService, verbose, settings.Retrieval);
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
