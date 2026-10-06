using RagFilingExplorer.Local.Chunking;
using RagFilingExplorer.Local.Structured;

namespace RagFilingExplorer.Local.Tests.Chunking;

/// <summary>
/// The Structured strategy as a whole. Its parts - the page reader, sections, chunking - are tested under
/// Structured/. No test compares its chunks with the committed chunk-review/structured/ dumps: that was checked
/// once, when the block model reproduced all 948 word for word (docs/Decision-Log.md, "block model"), and a
/// changed dump shows in git status after a --chunks-only run.
/// </summary>
[TestFixture]
public class StructuredStrategyTests
{
    [Test]
    public void ChunkingStrategies_CreatesTheStructuredStrategy()
    {
        ChunkingSettings settings = new() { Strategy = ChunkingStrategyKind.Structured, TokenizerModel = "gpt-4", MaxTokensPerChunk = 500, OverlapTokens = 50 };

        Assert.That(ChunkingStrategies.Create(settings), Is.InstanceOf<StructuredChunkingStrategy>());
        Assert.That(ChunkingStrategies.FileName(ChunkingStrategyKind.Structured), Is.EqualTo("structured"));
    }

    // The four real filings, offline: every chunk names the blocks it came from, and every table block is in some
    // chunk - the property the structure labels are read through.
    [TestCase("MSFT-10K-2026.html")]
    [TestCase("NDAQ-10K-2025.html")]
    [TestCase("NFLX-10K-2025.html")]
    [TestCase("ORCL-10K-2026.html")]
    public async Task ReadAsync_RealFiling_EveryChunkKnowsItsBlocksAndEveryTableIsChunked(string filing)
    {
        FileInfo file = new(Path.Combine(RepoPaths.FindRoot(TestContext.CurrentContext.TestDirectory).FullName, "data", filing));
        StructuredChunkingStrategy strategy = new(Microsoft.ML.Tokenizers.TiktokenTokenizer.CreateForModel("gpt-4"), 500, 50);

        StructuredFiling read = await strategy.ReadAsync(file);

        Assert.That(read.Chunks, Is.Not.Empty);
        Assert.That(read.Chunks.Where(c => c.Blocks.Count == 0), Is.Empty);
        HashSet<FilingBlock> chunked = read.Chunks.SelectMany(c => c.Blocks).ToHashSet(ReferenceEqualityComparer.Instance as IEqualityComparer<FilingBlock>);
        Assert.That(read.Sections.SelectMany(s => s.Blocks).OfType<TableBlock>().Where(t => !chunked.Contains(t)), Is.Empty);
    }
}
