using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;

namespace RagFilingExplorer.Local.Retrieval;

/// <summary>Scores how well each passage answers a question - higher is better; only the order means anything.</summary>
internal interface IRelevanceScorer
{
    string Name { get; }

    IReadOnlyList<float> Score(string question, IReadOnlyList<string> passages);
}

/// <summary>
/// v2 step 2b: a cross-encoder (ms-marco-MiniLM-L6-v2, run locally by ONNX Runtime) reading each (question, chunk) pair
/// whole, where the embedding and keyword searches only compared them. Chosen by a replay-only spike - docs/Decision-Log.md,
/// "Step 2b spike - measured": reranking each company's top 25 hybrid candidates, each chunk opened by its company line,
/// took the answers in the model's top 5 from 64 to 67 of 68 and lost none, for ~1-3 s of CPU per question.
/// Off by default since 2026-10-02 (Retrieval:Rerank): on the answer-side questions it pushed cash-flow statement rows
/// out for prose, and across every question set hybrid search alone puts as many answers in the model's context
/// (docs/Decision-Log.md, "A1-A27 with reranking off" onwards).
///
/// The model lives outside the repo (~91 MB, Apache 2.0) and is checked against its recorded SHA-256 before ONNX Runtime
/// reads it: the file was vetted once (provenance, operators, no external data - the same section), and a swapped or
/// corrupted file is refused rather than trusted.
/// </summary>
internal sealed class CrossEncoderReranker : IRelevanceScorer, IDisposable
{
    // The ms-marco MiniLM models' window (config.json's max_position_embeddings).
    private const int MaxLength = 512;

    private readonly InferenceSession session;
    private readonly BertPairEncoder encoder;

    private CrossEncoderReranker(string name, InferenceSession session, BertPairEncoder encoder)
    {
        Name = name;
        this.session = session;
        this.encoder = encoder;
    }

    public string Name { get; }

    /// <summary>
    /// Loads <c>onnx/model.onnx</c> and <c>vocab.txt</c> from <paramref name="modelDirectory"/> (environment variables
    /// expanded), after checking the model's SHA-256. Throws <see cref="FileNotFoundException"/> for a missing file and
    /// <see cref="InvalidOperationException"/> for a checksum mismatch - both before ONNX Runtime touches the file.
    /// </summary>
    public static CrossEncoderReranker Load(string modelDirectory, string expectedSha256)
    {
        string directory = Environment.ExpandEnvironmentVariables(modelDirectory);
        string modelPath = Path.Combine(directory, "onnx", "model.onnx");
        string vocabPath = Path.Combine(directory, "vocab.txt");
        foreach (string path in new[] { modelPath, vocabPath })
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"The reranker's {Path.GetFileName(path)} isn't at {path}.", path);
            }
        }

        string actualSha256;
        using (FileStream model = File.OpenRead(modelPath))
        {
            actualSha256 = Convert.ToHexStringLower(SHA256.HashData(model));
        }

        if (!actualSha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{modelPath} has SHA-256 {actualSha256}, not the {expectedSha256} recorded in Retrieval:RerankModelSha256 - refusing to load it.");
        }

        using FileStream vocab = File.OpenRead(vocabPath);
        BertPairEncoder encoder = BertPairEncoder.FromVocab(vocab, MaxLength);
        return new CrossEncoderReranker(Path.GetFileName(directory.TrimEnd('/', '\\')), new InferenceSession(modelPath), encoder);
    }

    /// <summary>One raw score (the model's logit) per passage, all pairs in one batch padded to its longest.</summary>
    public IReadOnlyList<float> Score(string question, IReadOnlyList<string> passages)
    {
        if (passages.Count == 0)
        {
            return [];
        }

        List<(long[] InputIds, long[] TokenTypeIds)> pairs = passages.Select(p => encoder.Encode(question, p)).ToList();
        int width = pairs.Max(p => p.InputIds.Length);
        long[] inputIds = new long[pairs.Count * width];
        long[] attentionMask = new long[pairs.Count * width];
        long[] tokenTypeIds = new long[pairs.Count * width];
        for (int row = 0; row < pairs.Count; row++)
        {
            pairs[row].InputIds.CopyTo(inputIds, row * width);
            pairs[row].TokenTypeIds.CopyTo(tokenTypeIds, row * width);
            Array.Fill(attentionMask, 1L, row * width, pairs[row].InputIds.Length); // padding ([PAD] = 0) stays masked out
        }

        long[] shape = [pairs.Count, width];
        using OrtValue inputIdsValue = OrtValue.CreateTensorValueFromMemory(inputIds, shape);
        using OrtValue attentionMaskValue = OrtValue.CreateTensorValueFromMemory(attentionMask, shape);
        using OrtValue tokenTypeIdsValue = OrtValue.CreateTensorValueFromMemory(tokenTypeIds, shape);
        Dictionary<string, OrtValue> inputs = new()
        {
            ["input_ids"] = inputIdsValue,
            ["attention_mask"] = attentionMaskValue,
            ["token_type_ids"] = tokenTypeIdsValue,
        };

        using RunOptions runOptions = new();
        using IDisposableReadOnlyCollection<OrtValue> outputs = session.Run(runOptions, inputs, session.OutputNames);
        return outputs[0].GetTensorDataAsSpan<float>().ToArray(); // logits, [batch, 1]
    }

    public void Dispose() => session.Dispose();
}
