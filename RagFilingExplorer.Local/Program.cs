using CommunityToolkit.VectorData.SqliteVec;
using Microsoft.Extensions.AI;
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

    // Each chunking strategy has its own index and chunk dumps, so switching Chunking:Strategy back and
    // forth never forces a re-embed - see IChunkingStrategy.
    string strategyName = ChunkingStrategies.FileName(settings.Chunking.Strategy);
    string dbPath = Path.Combine(repoRoot.FullName, $"rag.{strategyName}.db");
    string manifestPath = dbPath + ".manifest.json";
    bool verbose = args.Contains("--verbose");
    Console.WriteLine($"Chunking strategy: {settings.Chunking.Strategy} (index: {Path.GetFileName(dbPath)})");

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

    OllamaApiClient embeddingApiClient = new(CreateOllamaHttpClient(settings.Ollama), settings.Ollama.EmbeddingModel);
    OllamaApiClient chatApiClient = new(CreateOllamaHttpClient(settings.Ollama), settings.Ollama.ChatModel);
    await EnsureOllamaReadyAsync(chatApiClient, settings.Ollama);

    if (args.Contains("--rebuild"))
    {
        DeleteIndexFiles(dbPath, manifestPath);
    }

    IndexManifest currentManifest = IndexManifest.Create(settings, filings);
    bool indexExists = File.Exists(dbPath);
    if (indexExists)
    {
        EnsureIndexIsCurrent(dbPath, manifestPath, currentManifest);
    }
    else
    {
        // A manifest without its index (e.g. the .db was deleted by hand) must not vouch for the next one.
        DeleteIndexFiles(dbPath, manifestPath);
    }

    SqliteVectorStore vectorStore = new($"Data Source={dbPath}", new() { EmbeddingGenerator = embeddingApiClient });
    VectorStoreCollection<int, FilingChunkRecord> collection = vectorStore.GetCollection<int, FilingChunkRecord>("chunks");
    await collection.EnsureCollectionExistsAsync();

    if (indexExists)
    {
        Console.WriteLine($"Found an up-to-date {dbPath} - skipping chunking and embedding. Run with --rebuild to force a fresh build.");
    }
    else
    {
        try
        {
            await BuildIndexAsync(collection, settings, filings, Path.Combine(repoRoot.FullName, "chunk-review", strategyName));
        }
        catch (Exception ex)
        {
            throw new StartupException(
                $"Building the index failed: {ex.Message}\nThe partial {Path.GetFileName(dbPath)} has no manifest, so it won't be used - "
                + "fix the problem above and run again with --rebuild.", ex);
        }

        // Written last, only once every chunk is in: its presence is what marks the index as complete.
        currentManifest.Save(manifestPath);
    }

    // Ollama doesn't quietly ignore a "think" request for a model that can't reason - it throws a hard
    // OllamaException ("<model> does not support thinking"), confirmed directly when routing tried to send
    // one to llama3.1:8b and crashed the whole app on the first synthesis question. Checked once here via
    // Ollama's own /api/show capabilities list, rather than assumed, so RagAnswerService only ever engages
    // reasoning for a model that genuinely supports it.
    bool chatModelSupportsThinking = await ChatModelSupportsThinkingAsync(chatApiClient, settings.Ollama.ChatModel);

    // Metadata filtering (Microsoft's own retrieval-quality guidance ranks this above chunk-size/text
    // tweaks): if a question clearly names exactly one company (and/or points at one specific financial
    // statement), restrict the vector search accordingly before ranking runs. Directly targets the
    // cross-company/cross-statement contamination seen repeatedly in Step 7 testing (e.g. an MSFT-specific
    // question pulling in ORCL chunks, or a single filing's many similarly-shaped "Item 15" tables burying
    // the right one). See RagAnswerService/QueryIntentResolver for the actual resolution + search + prompt
    // + generation flow - extracted out of this loop so it can be unit-tested with mocked dependencies.
    RagAnswerService ragAnswerService = new(collection, chatApiClient, settings.Retrieval, chatModelSupportsThinking);

    await RunInteractiveLoopAsync(ragAnswerService, verbose, settings.Retrieval);
}

// ===== Setup helpers =====

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

// Fails fast, before any chunking, with the exact fix - otherwise a cloner without Ollama running (or
// without a model pulled) gets a raw HttpRequestException stack trace, and during a first build only
// after every filing has already been converted.
static async Task EnsureOllamaReadyAsync(OllamaApiClient client, OllamaSettings ollama)
{
    List<string> installed;
    try
    {
        installed = (await client.ListLocalModelsAsync()).Select(m => m.Name).ToList();
    }
    catch (HttpRequestException ex)
    {
        throw new StartupException(
            $"Could not reach Ollama at {ollama.BaseUrl} ({ex.Message}). Is it installed and running? "
            + "Start it with 'ollama serve' or the Ollama app.", ex);
    }

    // Ollama reports an untagged pull ("nomic-embed-text") as "nomic-embed-text:latest".
    string[] missing = new[] { ollama.EmbeddingModel, ollama.ChatModel }
        .Where(model => !installed.Any(name => name == model || name == $"{model}:latest"))
        .Distinct()
        .ToArray();

    if (missing.Length > 0)
    {
        throw new StartupException(
            $"Ollama is running but doesn't have these model(s) pulled: {string.Join(", ", missing)}. Run: "
            + string.Join(" && ", missing.Select(m => $"ollama pull {m}")));
    }
}

// An existing index is only trusted if it has a manifest (written only after a build finishes) that
// matches the current settings and filings - see IndexManifest for the two failure modes this closes.
static void EnsureIndexIsCurrent(string dbPath, string manifestPath, IndexManifest currentManifest)
{
    IndexManifest? builtManifest = IndexManifest.TryLoad(manifestPath);
    if (builtManifest is null)
    {
        throw new StartupException(
            $"{dbPath} exists but has no build manifest ({Path.GetFileName(manifestPath)}), so it can't be "
            + "trusted as complete - an earlier build was interrupted or failed, or it predates manifests. "
            + "Run with --rebuild.");
    }

    List<string> differences = builtManifest.DescribeDifferences(currentManifest);
    if (differences.Count > 0)
    {
        throw new StartupException(
            $"{dbPath} is out of date:\n"
            + string.Concat(differences.Select(d => $"  - {d}\n"))
            + "Run with --rebuild to rebuild it (this re-embeds every chunk and takes several minutes).");
    }
}

// --rebuild forces a fresh chunk+embed cycle - the only way to pick up a chunking/embedding *code*
// change (settings and filing changes are detected automatically via the manifest).
static void DeleteIndexFiles(string dbPath, string manifestPath)
{
    foreach (string path in new[] { dbPath, dbPath + "-shm", dbPath + "-wal", manifestPath })
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}

// Default HttpClient.Timeout (100s) isn't enough for CPU-only Ollama inference - a batch upsert of
// even a few dozen chunks, or a single llama3.1:8b generation over several chunks of context, can
// easily exceed it.
static HttpClient CreateOllamaHttpClient(OllamaSettings ollama) =>
    new() { BaseAddress = new Uri(ollama.BaseUrl), Timeout = TimeSpan.FromMinutes(ollama.TimeoutMinutes) };

static async Task<bool> ChatModelSupportsThinkingAsync(OllamaApiClient client, string model)
{
    OllamaSharp.Models.ShowModelResponse info = await client.ShowModelAsync(model);
    return info.Capabilities?.Contains("thinking") ?? false;
}

// ===== Index building (chunking + embedding), skipped when the strategy's index already exists =====

static async Task BuildIndexAsync(
    VectorStoreCollection<int, FilingChunkRecord> collection, AppSettings settings, FileInfo[] filings, string reviewDirectoryPath)
{
    DirectoryInfo reviewDirectory = new(reviewDirectoryPath);
    reviewDirectory.Create();

    IChunkingStrategy strategy = ChunkingStrategies.Create(settings.Chunking);

    List<FilingChunk> allChunks = new();
    foreach (FileInfo filing in filings)
    {
        allChunks.AddRange(await IngestFilingAsync(filing, strategy, reviewDirectory));
    }

    Console.WriteLine("=== Vector storage ===");
    Console.WriteLine($"Total chunks across all filings: {allChunks.Count}");

    List<FilingChunkRecord> records = BuildRecords(allChunks);
    await UpsertRecordsAsync(collection, records, settings.VectorStore.UpsertBatchSize);
}

// Converts one filing to chunks, prints its section/chunk stats, and writes its chunk-review dump.
static async Task<List<FilingChunk>> IngestFilingAsync(FileInfo filing, IChunkingStrategy strategy, DirectoryInfo reviewDirectory)
{
    Console.WriteLine($"=== {filing.Name} ===");

    (List<DocumentSection> sections, List<FilingChunk> chunks) = await strategy.ChunkAsync(filing);

    int[] tokenCounts = chunks.Select(c => c.Tokens).ToArray();
    Console.WriteLine($"Sections detected: {sections.Count}");
    Console.WriteLine($"Chunks: {chunks.Count}");
    Console.WriteLine($"Tokens per chunk - min: {tokenCounts.Min()}, max: {tokenCounts.Max()}, avg: {tokenCounts.Average():F0}");
    Console.WriteLine();
    Console.WriteLine("Section outline detected:");
    foreach (string heading in sections.Select(s => s.Heading))
    {
        Console.WriteLine($"  - {heading}");
    }

    Console.WriteLine();

    string reviewFilePath = Path.Combine(reviewDirectory.FullName, $"{Path.GetFileNameWithoutExtension(filing.Name)}.chunks.txt");
    await WriteChunkReviewFileAsync(reviewFilePath, chunks);
    Console.WriteLine($"Full chunk dump written to: {reviewFilePath}");
    Console.WriteLine();

    return chunks;
}

static async Task WriteChunkReviewFileAsync(string path, List<FilingChunk> chunks)
{
    await using StreamWriter writer = new(path);
    for (int i = 0; i < chunks.Count; i++)
    {
        FilingChunk chunk = chunks[i];
        await writer.WriteLineAsync($"--- Chunk {i + 1}/{chunks.Count} | heading: {chunk.Heading} | tokens: {chunk.Tokens} ---");
        await writer.WriteLineAsync(chunk.Content);
        await writer.WriteLineAsync();
    }
}

// nomic-embed-text expects task-specific prefixes for good retrieval matching: "search_document: "
// on stored text, "search_query: " on the query text at search time. EmbeddingTextBuilder enriches
// table chunks with their row labels as plain text before that prefix, since sparse tables otherwise
// embed poorly - see its doc comment. Content stays exactly as chunked - only Text (the embedding
// input) changes.
//
// StatementType is tracked in document order (chunks appear filing-by-filing, in original order):
// once a statement-title line (e.g. "CONSOLIDATED STATEMENTS OF OPERATIONS") is seen, that type
// carries forward to subsequent chunks until a new title line appears, the "Notes to Financial
// Statements" boundary resets it to narrative (see StatementTypeDetector.IsNotesToFinancialStatementsBoundary
// - without this, whichever statement was detected last leaks across every Note for the rest of the
// filing), or the filing changes - the same "carry the nearest marker forward" pattern already used
// for row-group labels in TokenChunker.
static List<FilingChunkRecord> BuildRecords(List<FilingChunk> allChunks)
{
    List<FilingChunkRecord> records = new();
    string? currentStatementType = null;
    string? previousFiling = null;

    for (int i = 0; i < allChunks.Count; i++)
    {
        FilingChunk chunk = allChunks[i];

        if (chunk.SourceFiling != previousFiling)
        {
            currentStatementType = null;
            previousFiling = chunk.SourceFiling;
        }

        foreach (string line in chunk.Content.Split('\n'))
        {
            if (StatementTypeDetector.IsNotesToFinancialStatementsBoundary(line))
            {
                currentStatementType = null;
                continue;
            }

            string? detected = StatementTypeDetector.Detect(line);
            if (detected is not null)
            {
                currentStatementType = detected;
            }
        }

        records.Add(new FilingChunkRecord
        {
            Key = i,
            SourceFiling = chunk.SourceFiling,
            Heading = chunk.Heading,
            StatementType = currentStatementType ?? "narrative",
            Content = chunk.Content,
            Text = $"search_document: {EmbeddingTextBuilder.Build(chunk.Heading, chunk.Content)}",
        });
    }

    return records;
}

static async Task UpsertRecordsAsync(VectorStoreCollection<int, FilingChunkRecord> collection, List<FilingChunkRecord> records, int batchSize)
{
    Console.WriteLine("Embedding and upserting all chunks (this calls Ollama for every chunk - may take a few minutes)...");

    // Default (appsettings.json's VectorStore.UpsertBatchSize) must stay 1: SqliteVec 1.0.1-preview
    // throws "UNIQUE constraint failed on vec_chunks primary key" on any multi-record UpsertAsync batch
    // against a fresh vec0 virtual table (confirmed - crashes on the very first batch of all-new keys,
    // so it's not a real duplicate-key issue in our data). Its delete-then-insert upsert workaround
    // (vec0 has no native UPSERT) appears to only be exercised correctly for single-record batches. In
    // practice this isn't much slower than batching: ~5 records/sec either way on this hardware.
    DateTime start = DateTime.UtcNow;
    for (int i = 0; i < records.Count; i += batchSize)
    {
        List<FilingChunkRecord> batch = records.Skip(i).Take(batchSize).ToList();
        await collection.UpsertAsync(batch);
        Console.WriteLine($"  {Math.Min(i + batchSize, records.Count)}/{records.Count} upserted ({(DateTime.UtcNow - start).TotalSeconds:F0}s elapsed)");
    }

    Console.WriteLine($"Upserted {records.Count} records in {(DateTime.UtcNow - start).TotalSeconds:F0}s.");
}

// ===== Interactive retrieval + answer generation =====

static async Task RunInteractiveLoopAsync(RagAnswerService ragAnswerService, bool verbose, RetrievalSettings retrieval)
{
    Console.WriteLine();
    Console.WriteLine("=== Retrieval + answer generation ===");
    Console.WriteLine("Ask a question about the filings (blank line or 'exit' to quit).");
    if (!verbose)
    {
        Console.WriteLine("Run with --verbose to see the full ranked candidate list for each question.");
    }

    while (true)
    {
        Console.WriteLine();
        Console.Write("> ");
        string? question = Console.ReadLine();

        if (string.IsNullOrWhiteSpace(question) || question.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase))
        {
            break;
        }

        // Broad catch deliberately: this is a live call to an external service (Ollama), which can fail
        // in ways this app can't predict (a starved-reasoning response, a model rejecting an unsupported
        // option, a dropped connection, ...) - confirmed the hard way when an unhandled OllamaException
        // from mid-stream took down the entire interactive session over what should have been one bad
        // turn. One failed question should never end the session; report it and keep going.
        try
        {
            RagAnswer answer = await ragAnswerService.AskAsync(question, searchTopK: verbose ? retrieval.VerboseSearchTopK : retrieval.DefaultSearchTopK);
            PrintMatchedFilter(answer);

            if (answer.UsedReasoningEffort != ReasoningEffort.None)
            {
                Console.WriteLine($"(reasoning: {answer.UsedReasoningEffort} - question matched RequiresSynthesis)");
            }

            if (answer.RetrievedChunks.Count == 0)
            {
                Console.WriteLine("(no results)");
                continue;
            }

            if (verbose)
            {
                PrintRetrievedChunks(answer.RetrievedChunks);
            }

            await StreamAnswerAsync(answer.AnswerStream, verbose);
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"[error] {ex.Message}");
        }

        Console.WriteLine();
    }
}

// Reasoning content (a reasoning model's "thinking", separate from its final answer - see
// AppSettings.RetrievalSettings.ReasoningEffort) is only shown under --verbose, under its own header,
// printed lazily so a plain lookup that never reasons looks exactly like it did before this existed.
static async Task StreamAnswerAsync(IAsyncEnumerable<ChatResponseUpdate> answerStream, bool verbose)
{
    bool printedReasoningHeader = false;
    bool printedAnswerHeader = false;

    await foreach (ChatResponseUpdate update in answerStream)
    {
        if (verbose)
        {
            foreach (AIContent content in update.Contents)
            {
                if (content is TextReasoningContent { Text.Length: > 0 } reasoning)
                {
                    if (!printedReasoningHeader)
                    {
                        Console.WriteLine();
                        Console.WriteLine("--- Reasoning (verbose) ---");
                        printedReasoningHeader = true;
                    }

                    Console.Write(reasoning.Text);
                }
            }
        }

        if (update.Text.Length > 0)
        {
            if (!printedAnswerHeader)
            {
                Console.WriteLine();
                Console.WriteLine("--- Answer ---");
                printedAnswerHeader = true;
            }

            Console.Write(update.Text);
        }
    }
}

static void PrintMatchedFilter(RagAnswer answer)
{
    List<string> parts = new();
    if (answer.MatchedFilings.Count == 1)
    {
        parts.Add($"filtering to {answer.MatchedFilings[0]}");
    }
    else if (answer.MatchedFilings.Count > 1)
    {
        parts.Add($"searching {string.Join(" and ", answer.MatchedFilings)} separately");
    }

    if (answer.MatchedStatementType is not null)
    {
        parts.Add(parts.Count == 0 ? $"filtering to statement type: {answer.MatchedStatementType}" : $"statement type: {answer.MatchedStatementType}");
    }

    if (parts.Count > 0)
    {
        Console.WriteLine($"({string.Join(", ", parts)})");
    }
}

// --verbose: print the full ranked candidate list (compact) to see where the "right" chunk actually
// lands - useful when diagnosing a retrieval miss, not needed for normal use.
static void PrintRetrievedChunks(IReadOnlyList<VectorSearchResult<FilingChunkRecord>> retrievedChunks)
{
    Console.WriteLine();
    Console.WriteLine($"--- Retrieved chunks (top {retrievedChunks.Count}, compact, for diagnosis) ---");
    for (int i = 0; i < retrievedChunks.Count; i++)
    {
        FilingChunkRecord record = retrievedChunks[i].Record;
        string snippet = record.Content.Length > 90 ? record.Content[..90].ReplaceLineEndings(" ") : record.Content.ReplaceLineEndings(" ");
        Console.WriteLine($"[{i + 1}] score={retrievedChunks[i].Score:F4} | {record.SourceFiling} | {record.StatementType} | {record.Heading} | {snippet}");
    }
}
