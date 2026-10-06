using System.Globalization;
using System.Text;
using Microsoft.ML.Tokenizers;

namespace RagFilingExplorer.Local.Retrieval;

/// <summary>
/// Turns a (question, passage) pair into a BERT cross-encoder's inputs - <c>[CLS] question [SEP] passage [SEP]</c>, token
/// type 0 then 1, the passage cut to what fits in <c>maxLength</c> - exactly as Hugging Face's <c>tokenizers</c> does for
/// the ms-marco cross-encoders, which is how the reranking spike scored them (tools/rerank_spike.py).
///
/// <see cref="BertTokenizer"/>'s own basic tokenization differs from Hugging Face's on this project's text: it drops line
/// breaks (fusing the words either side - "STATES\n\nSECURITIES" became "states" "##se" "##cu"...), ASCII symbols such
/// as <c>|</c> and <c>$</c> (every table row's separators), and unknown symbols such as the cover page's "☒" (Hugging Face:
/// <c>[UNK]</c>) - on this project's questions and chunks, every pair. So <see cref="Normalize"/> does Hugging Face's
/// BertNormalizer and BertPreTokenizer here, and BertTokenizer only runs WordPiece; the parity check against Python's
/// <c>tokenizers</c> is in docs/Decision-Log.md, "Step 2b - build".
/// </summary>
internal sealed class BertPairEncoder(BertTokenizer tokenizer, int maxLength)
{
    /// <summary>From a model's vocab.txt; basic tokenization off - <see cref="Normalize"/> does it.</summary>
    public static BertPairEncoder FromVocab(Stream vocab, int maxLength) => new(
        BertTokenizer.Create(vocab, new BertOptions { ApplyBasicTokenization = false, LowerCaseBeforeTokenization = false, RemoveNonSpacingMarks = false }),
        maxLength);

    /// <summary>The pair's input ids and token type ids. Only the passage is cut ("only_second"), from its end.</summary>
    public (long[] InputIds, long[] TokenTypeIds) Encode(string question, string passage)
    {
        IReadOnlyList<int> questionIds = tokenizer.EncodeToIds(Normalize(question), addSpecialTokens: false);
        IReadOnlyList<int> passageIds = tokenizer.EncodeToIds(Normalize(passage), addSpecialTokens: false);
        List<int> kept = passageIds.Take(Math.Max(0, maxLength - 3 - questionIds.Count)).ToList(); // 3: [CLS] and two [SEP]

        return (
            tokenizer.BuildInputsWithSpecialTokens(questionIds, kept).Select(id => (long)id).ToArray(),
            tokenizer.CreateTokenTypeIdsFromSequences(questionIds, kept).Select(id => (long)id).ToArray());
    }

    /// <summary>
    /// Hugging Face's BertNormalizer (clean_text, handle_chinese_chars, strip_accents, lowercase - the ms-marco models' own
    /// settings) and BertPreTokenizer (split on whitespace, every punctuation character its own word), returned as words
    /// joined by single spaces for WordPiece.
    /// </summary>
    internal static string Normalize(string text)
    {
        StringBuilder cleaned = new(text.Length);
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (rune.Value is 0 or 0xFFFD || IsControl(rune))
            {
                continue;
            }

            if (rune.Value is '\t' or '\n' or '\r' || Rune.IsWhiteSpace(rune))
            {
                cleaned.Append(' ');
            }
            else if (IsCjk(rune.Value))
            {
                cleaned.Append(' ').Append(rune.ToString()).Append(' ');
            }
            else
            {
                cleaned.Append(rune.ToString());
            }
        }

        // strip_accents follows lowercase when unset, as it is for these models: decompose, drop the combining marks.
        StringBuilder words = new(cleaned.Length);
        foreach (Rune rune in cleaned.ToString().Normalize(NormalizationForm.FormD).EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            Rune lower = Rune.ToLowerInvariant(rune);
            if (IsPunctuation(lower))
            {
                words.Append(' ').Append(lower.ToString()).Append(' ');
            }
            else
            {
                words.Append(lower.ToString());
            }
        }

        return string.Join(' ', words.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    // Hugging Face: a control character is "other" (Cc, Cf, Cs, Co, Cn) - except tab, line feed and carriage return,
    // which count as whitespace.
    private static bool IsControl(Rune rune) => rune.Value is not ('\t' or '\n' or '\r') && Rune.GetUnicodeCategory(rune)
        is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned;

    // Hugging Face's BERT punctuation: all ASCII non-alphanumeric printables (so "$", "|", "+" count), plus Unicode's P*.
    private static bool IsPunctuation(Rune rune) =>
        rune.Value is (>= 33 and <= 47) or (>= 58 and <= 64) or (>= 91 and <= 96) or (>= 123 and <= 126) || Rune.IsPunctuation(rune);

    // The CJK Unified Ideographs blocks BERT tokenizes one character at a time.
    private static bool IsCjk(int c) => c is (>= 0x4E00 and <= 0x9FFF) or (>= 0x3400 and <= 0x4DBF) or (>= 0x20000 and <= 0x2A6DF)
        or (>= 0x2A700 and <= 0x2B73F) or (>= 0x2B740 and <= 0x2B81F) or (>= 0x2B920 and <= 0x2CEAF) or (>= 0xF900 and <= 0xFAFF)
        or (>= 0x2F800 and <= 0x2FA1F);
}
