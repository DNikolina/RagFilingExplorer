using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using OllamaSharp;
using RagFilingExplorer.Claude;
using RagFilingExplorer.Core;
using RagFilingExplorer.Core.Chunking;
using RagFilingExplorer.Core.Retrieval;
using RagFilingExplorer.Core.VectorStore;
using RagFilingExplorer.Ollama;
using RagFilingExplorer.Voyage;

using IDisposable utf8Console = Utf8Console.Use();

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
    AppSettings settings = Load(() => AppSettings.Load(AppContext.BaseDirectory));
    // Claude's settings are read only when it answers, and set the excerpts it reads (Claude:GenerationTopK).
    ClaudeSettings? claude = settings.Providers.Chat == ChatProvider.Claude
        ? Load(() =>
        {
            ClaudeSettings loaded = ClaudeSettings.Load(AppContext.BaseDirectory);
            loaded.ApplyTo(settings.Retrieval);
            return loaded;
        })
        : null;

    DirectoryInfo repoRoot = RepoPaths.FindRoot(AppContext.BaseDirectory);

    // Each chunking strategy and chunk budget has its own index and chunk dumps (chunk-review/<strategy>[.<n>tok]/), and
    // each embedding model its own index - see IndexFiles.
    string strategyName = ChunkingStrategies.FileName(settings.Chunking.Strategy);
    EmbeddingTemplates embeddingTemplates = EmbeddingTemplates.For(settings.Embedding.Model);
    embeddingTemplates.EnsureServedBy(settings.Providers.Embedding);
    IndexFiles index = IndexFiles.For(repoRoot, strategyName, embeddingTemplates, settings.Chunking.MaxTokensPerChunk);
    string reviewDirectoryPath = IndexFiles.ReviewDirectory(repoRoot, strategyName, settings.Chunking.MaxTokensPerChunk);
    bool verbose = args.Contains("--verbose");
    Console.WriteLine($"Chunking strategy: {settings.Chunking.Strategy} (index: {index.DbFileName})");

    // The wiring shared with the evaluation is AppComposition's; building the index stays here.
    FileInfo[] filings = AppComposition.FindFilings(repoRoot);
    CompanyRegistry companies = AppComposition.RegisterCompanies(filings);

    foreach (CompanyRegistration registration in companies.Registrations)
    {
        Console.WriteLine($"Company filter: {string.Join(" / ", registration.Names)} -> {registration.Filing}");
    }

    // --chunks-only: chunk every filing and write chunk-review/<strategy>[.<n>tok]/, nothing else - no Ollama, no
    // index. Reading the real chunk output is how chunking bugs are found; this makes that a one-minute loop
    // instead of a ten-minute re-embed.
    if (args.Contains("--chunks-only"))
    {
        List<FilingChunk> chunks = await IndexBuilder.ChunkFilingsAsync(settings, filings, reviewDirectoryPath);
        Console.WriteLine($"Total chunks across all filings: {chunks.Count} (--chunks-only: index not touched)");
        return;
    }

    // A stale index stops the app here, before any provider is checked - Voyage's checks are billed. With --rebuild it's
    // replaced anyway; it's deleted only after the providers pass, so a provider that's down doesn't cost the index.
    bool rebuild = args.Contains("--rebuild");
    IndexManifest currentManifest = IndexManifest.Create(settings, filings);
    if (!rebuild && index.Exists)
    {
        index.EnsureCurrent(currentManifest);
    }

    // Each provider is checked before any build: Ollama for only the models it serves here (none with Voyage and Claude),
    // Claude's key and model against its Models API (no tokens billed), Voyage's with one billed query embedding or
    // rerank - so a missing one fails now, not after a ten-minute embed.
    bool ollamaEmbeds = settings.Providers.Embedding == EmbeddingProvider.Ollama;
    (OllamaApiClient ollamaEmbedding, OllamaApiClient ollamaChat) = OllamaSetup.CreateClients(settings);
    await OllamaSetup.EnsureServesAsync(ollamaChat, settings, embeds: ollamaEmbeds, chats: claude is null);

    // Voyage embeds a chunk and a question differently (its input_type), so the index is built with one generator and
    // searched with the other; Ollama's model embeds both alike, its templates making the difference.
    (IEmbeddingGenerator<string, Embedding<float>> documentEmbedding, IEmbeddingGenerator<string, Embedding<float>> queryEmbedding) = ollamaEmbeds
        ? (OllamaSetup.EmbeddingGenerator(ollamaEmbedding), OllamaSetup.EmbeddingGenerator(ollamaEmbedding))
        : await VoyageEmbedding.CreateAsync(Load(() => VoyageApi.LoadKey(AppContext.BaseDirectory)), embeddingTemplates);
    Console.WriteLine($"Embedding model: {settings.Embedding.Model} ({settings.Providers.Embedding}, {embeddingTemplates.Dimensions} dimensions)");

    // Voyage's reranker is checked here with the other providers (one billed call); the local one loads with the runtime.
    VoyageReranker? voyageReranker = settings.Retrieval.Rerank && settings.Providers.Rerank == RerankProvider.Voyage
        ? await VoyageReranker.CreateAsync(Load(() => VoyageApi.LoadKey(AppContext.BaseDirectory)), Load(() => VoyageSettings.Load(AppContext.BaseDirectory)).RerankModel)
        : null;

    (IChatClient chat, ChatModelOptions chatModel) = claude is null
        ? (OllamaSetup.ChatClient(ollamaChat, settings.Ollama), await OllamaSetup.ReadChatModelAsync(settings, ollamaChat))
        : await ClaudeChat.CreateAsync(claude);
    Console.WriteLine(claude is null
        ? $"Chat model: {settings.Ollama.ChatModel} (Ollama)"
        : $"Chat model: {claude.Model} (Claude API; effort {claude.LookupEffort} for lookups, {claude.SynthesisEffort} for synthesis; reads {claude.GenerationTopK} excerpts)");

    if (rebuild)
    {
        index.Delete();
    }

    if (index.Exists)
    {
        Console.WriteLine($"Found an up-to-date {index.DbPath} - skipping chunking and embedding. Run with --rebuild to force a fresh build.");
    }
    else
    {
        // A manifest without its index (e.g. the .db was deleted by hand) must not vouch for the next one.
        index.Delete();
        try
        {
            await IndexBuilder.BuildAsync(
                await AppComposition.OpenCollectionAsync(index, documentEmbedding, embeddingTemplates), embeddingTemplates, settings, filings, reviewDirectoryPath);
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

    VectorStoreCollection<int, FilingChunkRecord> collection = await AppComposition.OpenCollectionAsync(index, queryEmbedding, embeddingTemplates);
    using RagRuntime runtime = AppComposition.CreateRuntime(settings, index, collection, embeddingTemplates, companies, chat, chatModel, voyageReranker);

    Console.WriteLine(runtime.Reranker is null
        ? $"Search: {settings.Retrieval.Search}"
        : $"Search: {settings.Retrieval.Search}, reranked by {runtime.Reranker.Name} (each company's top {settings.Retrieval.RerankCandidates})");

    await InteractiveSession.RunAsync(runtime.AnswerService, verbose, settings.Retrieval);
}

// appsettings.json holds the tunable knobs (providers, model names, chunk size, timeouts, top-K) - see
// AppSettings.cs for what's deliberately NOT here (keyword lists, regexes - domain logic, not config),
// and for why presence of every key is checked explicitly.
static T Load<T>(Func<T> load)
{
    try
    {
        return load();
    }
    catch (InvalidOperationException ex)
    {
        throw new StartupException(ex.Message, ex);
    }
}
