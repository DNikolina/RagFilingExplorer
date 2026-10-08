using Microsoft.Extensions.VectorData;
using RagFilingExplorer.Local.Chunking;

namespace RagFilingExplorer.Local.VectorStore;

/// <summary>
/// Chunks every filing (writing its chunk-review dump along the way) and, for a build, embeds and upserts
/// the resulting records - skipped entirely when the strategy's index already exists and is current.
/// </summary>
internal static class IndexBuilder
{
    public static async Task BuildAsync(
        VectorStoreCollection<int, FilingChunkRecord> collection, AppSettings settings, FileInfo[] filings, string reviewDirectoryPath)
    {
        List<FilingChunk> allChunks = await ChunkFilingsAsync(settings, filings, reviewDirectoryPath);

        Console.WriteLine("=== Vector storage ===");
        Console.WriteLine($"Total chunks across all filings: {allChunks.Count}");

        List<FilingChunkRecord> records = FilingChunkRecords.Build(allChunks);
        await UpsertRecordsAsync(collection, records, settings.VectorStore.UpsertBatchSize);
    }

    public static async Task<List<FilingChunk>> ChunkFilingsAsync(AppSettings settings, FileInfo[] filings, string reviewDirectoryPath)
    {
        DirectoryInfo reviewDirectory = new(reviewDirectoryPath);
        reviewDirectory.Create();

        IChunkingStrategy strategy = ChunkingStrategies.Create(settings.Chunking);

        List<FilingChunk> allChunks = [];
        foreach (FileInfo filing in filings)
        {
            allChunks.AddRange(await IngestFilingAsync(filing, strategy, reviewDirectory));
        }

        return allChunks;
    }

    // Converts one filing to chunks, prints its section/chunk stats, and writes its chunk-review dump.
    private static async Task<List<FilingChunk>> IngestFilingAsync(FileInfo filing, IChunkingStrategy strategy, DirectoryInfo reviewDirectory)
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

    private static async Task WriteChunkReviewFileAsync(string path, List<FilingChunk> chunks)
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

    private static async Task UpsertRecordsAsync(VectorStoreCollection<int, FilingChunkRecord> collection, List<FilingChunkRecord> records, int batchSize)
    {
        Console.WriteLine("Embedding and upserting all chunks (this calls Ollama for every chunk - may take a few minutes)...");

        // batchSize must stay 1 on SqliteVec 1.0.1-preview - see VectorStoreSettings.UpsertBatchSize. It costs little:
        // embedding each chunk, not the upsert, is what takes the time.
        DateTime start = DateTime.UtcNow;
        for (int i = 0; i < records.Count; i += batchSize)
        {
            List<FilingChunkRecord> batch = records.Skip(i).Take(batchSize).ToList();
            await collection.UpsertAsync(batch);
            Console.WriteLine($"  {Math.Min(i + batchSize, records.Count)}/{records.Count} upserted ({(DateTime.UtcNow - start).TotalSeconds:F0}s elapsed)");
        }

        Console.WriteLine($"Upserted {records.Count} records in {(DateTime.UtcNow - start).TotalSeconds:F0}s.");

        // Upserts report no per-record outcome, so a record lost in the store is otherwise silent (a key-0
        // record overwritten by key 1 is one way - see FilingChunkRecords).
        // Thrown before the manifest is written, so a short index is never trusted.
        int stored = await collection.GetAsync(r => true, records.Count + 1).CountAsync();

        if (stored != records.Count)
        {
            throw new InvalidOperationException($"the index holds {stored} records after upserting {records.Count}.");
        }
    }
}
