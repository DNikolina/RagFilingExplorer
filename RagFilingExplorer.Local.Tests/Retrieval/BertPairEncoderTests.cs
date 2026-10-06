using System.Text;
using RagFilingExplorer.Local.Retrieval;

namespace RagFilingExplorer.Local.Tests.Retrieval;

/// <summary>
/// The pair encoder must tokenize as Hugging Face's tokenizers does - checked against it on every (question, chunk)
/// pair of both question files (docs/Decision-Log.md, "Step 2b - build"). These pin the differences
/// that check found in BertTokenizer's own basic tokenization, offline, with a vocabulary of just the words they use.
/// </summary>
[TestFixture]
public class BertPairEncoderTests
{
    private static readonly string[] Vocabulary =
    [
        "[PAD]", "[UNK]", "[CLS]", "[SEP]", "[MASK]",
        "united", "states", "securities", "what", "were", "revenues", "?", "a", "b",
    ];

    private static int Id(string token) => Array.IndexOf(Vocabulary, token);

    private static BertPairEncoder MakeEncoder(int maxLength = 512)
    {
        using MemoryStream vocab = new(Encoding.UTF8.GetBytes(string.Join('\n', Vocabulary) + '\n'));
        return BertPairEncoder.FromVocab(vocab, maxLength);
    }

    // MSFT's cover page: BertTokenizer dropped the line breaks and fused "STATES\n\nSECURITIES" into one word, which
    // WordPiece then split into fragments ("states", "##se", "##cu", ...).
    [Test]
    public void Normalize_LineBreaks_SeparateWords()
    {
        Assert.That(BertPairEncoder.Normalize("UNITED STATES\n\nSECURITIES"), Is.EqualTo("united states securities"));
    }

    // Every table row: BertTokenizer dropped "|" and "$", which Hugging Face keeps as words of their own (all ASCII symbols
    // count as punctuation for BERT).
    [Test]
    public void Normalize_PipeAndDollar_AreWordsOfTheirOwn()
    {
        Assert.That(BertPairEncoder.Normalize("2025: $3,011 | 2024: $3,6"), Is.EqualTo("2025 : $ 3 , 011 | 2024 : $ 3 , 6"));
    }

    [TestCase("Café", "cafe", TestName = "Normalize_Accent_IsStrippedAndLowercased")]
    [TestCase("a b", "a b", TestName = "Normalize_NonBreakingSpace_IsASpace")]
    [TestCase("it’s", "it ’ s", TestName = "Normalize_CurlyApostrophe_IsPunctuation")]
    [TestCase("x — y", "x — y", TestName = "Normalize_EmDash_IsPunctuation")]
    [TestCase("a​b", "ab", TestName = "Normalize_ZeroWidthSpace_IsDropped")]
    public void Normalize_Characters(string text, string expected)
    {
        Assert.That(BertPairEncoder.Normalize(text), Is.EqualTo(expected));
    }

    // MSFT's cover page check box: BertTokenizer dropped it; Hugging Face reads an unknown symbol as [UNK].
    [Test]
    public void Encode_UnknownSymbol_BecomesUnk()
    {
        (long[] ids, _) = MakeEncoder().Encode("a", "a ☒ b");

        Assert.That(ids, Is.EqualTo(new long[] { Id("[CLS]"), Id("a"), Id("[SEP]"), Id("a"), Id("[UNK]"), Id("b"), Id("[SEP]") }));
    }

    [Test]
    public void Encode_Pair_IsClsQuestionSepPassageSep_WithTypeIdsZeroThenOne()
    {
        (long[] ids, long[] types) = MakeEncoder().Encode("What were revenues?", "a b");

        Assert.That(ids, Is.EqualTo(new long[]
        {
            Id("[CLS]"), Id("what"), Id("were"), Id("revenues"), Id("?"), Id("[SEP]"), Id("a"), Id("b"), Id("[SEP]"),
        }));
        Assert.That(types, Is.EqualTo(new long[] { 0, 0, 0, 0, 0, 0, 1, 1, 1 }));
    }

    // 30-40% of chunks exceed the 512-token window with their company line (the spike); only the passage's end is cut.
    [Test]
    public void Encode_PassageLongerThanWindow_KeepsTheQuestion_CutsThePassageFromItsEnd()
    {
        (long[] ids, long[] types) = MakeEncoder(maxLength: 8).Encode("a", "b a b a b a");

        Assert.That(ids, Is.EqualTo(new long[] { Id("[CLS]"), Id("a"), Id("[SEP]"), Id("b"), Id("a"), Id("b"), Id("a"), Id("[SEP]") }));
        Assert.That(types, Has.Length.EqualTo(8));
    }
}
