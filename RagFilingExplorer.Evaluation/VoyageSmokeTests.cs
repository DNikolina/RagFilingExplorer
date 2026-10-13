using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using RagFilingExplorer.Core.Chunking;
using RagFilingExplorer.Core.VectorStore;
using RagFilingExplorer.Voyage;

namespace RagFilingExplorer.Evaluation;

/// <summary>
/// Voyage's real API, once, small: startup's one-word check, two chunks of rag.structured.db embedded as documents in one
/// request, one question as a query - three requests, about a thousand tokens. Checks what the offline tests can't: the
/// request and response as Voyage really sends them, the vector size, and that the question lands nearer its answer than
/// an unrelated chunk (the input types the right way round). Billed against the key in user secrets or VOYAGE_API_KEY -
/// [Explicit], ask the user before running. Embedding__Model picks the model (voyage-finance-2 by default here):
///   dotnet test RagFilingExplorer.Evaluation --filter "FullyQualifiedName~VoyageSmokeTests"
/// </summary>
[TestFixture]
[Explicit("Calls Voyage's API with the user's key - billed.")]
public class VoyageSmokeTests
{
    private static readonly DirectoryInfo Repo = RepoPaths.FindRoot(AppContext.BaseDirectory);

    private const string Question = "What was Microsoft's total revenue in fiscal year 2026?";

    [Test]
    public async Task Embed_TwoChunksAndAQuestion_QuestionNearestItsAnswer()
    {
        string model = Environment.GetEnvironmentVariable("Embedding__Model") ?? "voyage-finance-2";
        EmbeddingTemplates templates = EmbeddingTemplates.For(model);
        templates.EnsureServedBy(Core.EmbeddingProvider.Voyage);

        // Microsoft's income statement (its revenue lines) and Netflix's properties - nothing about revenue.
        string answer = ChunkText("SourceFiling = 'MSFT-10K-2026.html' AND StatementType = 'income_statement' AND Content LIKE '%Total revenue%'");
        string unrelated = ChunkText("SourceFiling = 'NFLX-10K-2025.html' AND Heading LIKE '%Item 2. Properties%'");

        (IEmbeddingGenerator<string, Embedding<float>> documents, IEmbeddingGenerator<string, Embedding<float>> queries) =
            await VoyageEmbedding.CreateAsync(VoyageEmbedding.LoadKey(Path.Combine(Repo.FullName, "RagFilingExplorer.Cli")), templates);

        GeneratedEmbeddings<Embedding<float>> chunks = await documents.GenerateAsync([templates.Document(null, answer), templates.Document(null, unrelated)]);
        GeneratedEmbeddings<Embedding<float>> question = await queries.GenerateAsync([templates.Query(Question)]);

        double toAnswer = Cosine(question[0].Vector.Span, chunks[0].Vector.Span);
        double toUnrelated = Cosine(question[0].Vector.Span, chunks[1].Vector.Span);
        TestContext.Progress.WriteLine(
            $"{model} ({chunks[0].ModelId}): {chunks.Count} chunks, {chunks[0].Vector.Length} dimensions; tokens: chunks {chunks.Usage?.InputTokenCount}, "
            + $"question {question.Usage?.InputTokenCount}; cosine to the income statement {toAnswer:F4}, to Netflix's properties {toUnrelated:F4}");

        Assert.That(chunks, Has.Count.EqualTo(2));
        Assert.That(chunks.Concat(question).Select(e => e.Vector.Length), Is.All.EqualTo(templates.Dimensions));
        Assert.That(toAnswer, Is.GreaterThan(toUnrelated), "the question should land nearer its answer");
    }

    private static string ChunkText(string where)
    {
        using SqliteConnection connection = new($"Data Source={Path.Combine(Repo.FullName, "rag.structured.db")};Mode=ReadOnly");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT Heading, Content FROM chunks WHERE {where} ORDER BY Key LIMIT 1";
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.That(reader.Read(), Is.True, $"no chunk where {where}");
        return EmbeddingTextBuilder.Build(reader.GetString(0), reader.GetString(1));
    }

    private static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        return dot / Math.Sqrt(normA * normB);
    }
}
