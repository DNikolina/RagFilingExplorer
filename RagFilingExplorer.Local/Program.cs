using Microsoft.Extensions.VectorData;
using OllamaSharp;
using RagFilingExplorer.Local;
using RagFilingExplorer.Local.Chunking;
using RagFilingExplorer.Local.Retrieval;
using RagFilingExplorer.Local.VectorStore;
using System.Text;

// UTF-8 in and out. Without it, output redirected to a log is written in the console's OEM code page, so
// the filings' non-breaking spaces become a lone 0xFF byte - invalid UTF-8 for the tools that read the logs
// even inside an answer ("$9.1 billion", with a non-breaking space). Setting these changes
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

    // The wiring shared with the evaluation is AppComposition's; building the index stays here.
    FileInfo[] filings = AppComposition.FindFilings(repoRoot);
    CompanyRegistry companies = AppComposition.RegisterCompanies(filings);

    foreach (CompanyRegistration registration in companies.Registrations)
    {
        Console.WriteLine($"Company filter: {string.Join(" / ", registration.Names)} -> {registration.Filing}");
    }

    // --chunks-only: chunk every filing and write chunk-review/<strategy>/, nothing else - no Ollama, no
    // index. Reading the real chunk output is how chunking bugs are found; this makes that a one-minute loop
    // instead of a ten-minute re-embed.
    if (args.Contains("--chunks-only"))
    {
        List<FilingChunk> chunks = await IndexBuilder.ChunkFilingsAsync(settings, filings, reviewDirectoryPath);
        Console.WriteLine($"Total chunks across all filings: {chunks.Count} (--chunks-only: index not touched)");
        return;
    }

    (OllamaApiClient embeddingApiClient, OllamaApiClient chatApiClient) = AppComposition.CreateOllamaClients(settings.Ollama);
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

    VectorStoreCollection<int, FilingChunkRecord> collection = await AppComposition.OpenCollectionAsync(index, embeddingApiClient);

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

    using RagRuntime runtime = AppComposition.CreateRuntime(
        settings, index, collection, companies, chatApiClient, await AppComposition.OllamaChatModelAsync(settings, chatApiClient));

    Console.WriteLine(runtime.Reranker is null
        ? $"Search: {settings.Retrieval.Search}"
        : $"Search: {settings.Retrieval.Search}, reranked by {runtime.Reranker.Name} (each company's top {settings.Retrieval.RerankCandidates})");

    await InteractiveSession.RunAsync(runtime.AnswerService, verbose, settings.Retrieval);
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
