using RagFilingExplorer.Local.Retrieval;

namespace RagFilingExplorer.Tests.Retrieval;

/// <summary>
/// The reranker's model is fetched separately (~91 MB, outside the repo), so scoring itself is measured by the eval runs,
/// not here; these pin what happens before ONNX Runtime touches a file - it must exist and match its recorded SHA-256.
/// </summary>
[TestFixture]
public class CrossEncoderRerankerTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"CrossEncoderRerankerTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, recursive: true);

    [Test]
    public void Load_MissingModel_ThrowsFileNotFound()
    {
        Assert.Throws<FileNotFoundException>(() => CrossEncoderReranker.Load(_directory, new string('0', 64)));
    }

    // A swapped or corrupted model is refused before ONNX Runtime parses it - the file was vetted once, by its hash.
    [Test]
    public void Load_ModelNotMatchingItsChecksum_IsRefusedBeforeLoading()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "onnx"));
        File.WriteAllText(Path.Combine(_directory, "onnx", "model.onnx"), "not the vetted model");
        File.WriteAllText(Path.Combine(_directory, "vocab.txt"), "[PAD]\n[UNK]\n[CLS]\n[SEP]\n[MASK]\n");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => CrossEncoderReranker.Load(_directory, new string('0', 64)))!;
        Assert.That(ex.Message, Does.Contain("refusing to load it"));
    }
}
