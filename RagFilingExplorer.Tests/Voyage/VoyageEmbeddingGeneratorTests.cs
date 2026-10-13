using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using RagFilingExplorer.Voyage;

namespace RagFilingExplorer.Tests.Voyage;

[TestFixture]
public class VoyageEmbeddingGeneratorTests
{
    // Voyage's endpoint, answered from a queue; every request body kept.
    private sealed class FakeVoyage(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = new(responses);

        public List<(Uri? Uri, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri, await request.Content!.ReadAsStringAsync(cancellationToken)));
            return responses.Dequeue();
        }
    }

    private static HttpResponseMessage Ok(params (int Index, float[] Vector)[] data) => Json(HttpStatusCode.OK, new
    {
        @object = "list",
        data = data.Select(d => new { @object = "embedding", embedding = d.Vector, index = d.Index }),
        model = "voyage-4-large",
        usage = new { total_tokens = 7 },
    });

    private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private static (VoyageEmbeddingGenerator Generator, List<TimeSpan> Delays) Create(FakeVoyage voyage, int dimensions = 2)
    {
        List<TimeSpan> delays = [];
        HttpClient http = new(voyage) { BaseAddress = new Uri("https://api.voyageai.com/v1/") };
        return (new VoyageEmbeddingGenerator(http, "voyage-4-large", "document", dimensions, (d, _) => { delays.Add(d); return Task.CompletedTask; }), delays);
    }

    // The request Voyage's API reference documents; truncation false so an over-long chunk fails instead of being cut.
    [Test]
    public async Task GenerateAsync_Inputs_PostsModelInputTypeAndNoTruncation()
    {
        FakeVoyage voyage = new(Ok((0, [1, 2]), (1, [3, 4])));

        await Create(voyage).Generator.GenerateAsync(["first", "second"]);

        Assert.That(voyage.Requests, Has.Count.EqualTo(1));
        Assert.That(voyage.Requests[0].Uri, Is.EqualTo(new Uri("https://api.voyageai.com/v1/embeddings")));
        using JsonDocument body = JsonDocument.Parse(voyage.Requests[0].Body);
        Assert.That(body.RootElement.GetProperty("input").EnumerateArray().Select(e => e.GetString()), Is.EqualTo(new[] { "first", "second" }));
        Assert.That(body.RootElement.GetProperty("model").GetString(), Is.EqualTo("voyage-4-large"));
        Assert.That(body.RootElement.GetProperty("input_type").GetString(), Is.EqualTo("document"));
        Assert.That(body.RootElement.GetProperty("truncation").GetBoolean(), Is.False);
    }

    // Each embedding carries its input's position; the list's own order isn't promised.
    [Test]
    public async Task GenerateAsync_DataOutOfOrder_ReturnsInInputOrder()
    {
        GeneratedEmbeddings<Embedding<float>> embeddings = await Create(new FakeVoyage(Ok((1, [3, 4]), (0, [1, 2])))).Generator
            .GenerateAsync(["first", "second"]);

        Assert.That(embeddings.Select(e => e.Vector.ToArray()), Is.EqualTo(new[] { new float[] { 1, 2 }, [3, 4] }));
        Assert.That(embeddings.Usage?.InputTokenCount, Is.EqualTo(7));
    }

    [Test]
    public async Task GenerateAsync_RateLimitedThenOk_RetriesWithBackOff()
    {
        FakeVoyage voyage = new(
            Json(HttpStatusCode.TooManyRequests, new { detail = "slow down" }),
            Json(HttpStatusCode.ServiceUnavailable, new { detail = "busy" }),
            Ok((0, [1, 2])));
        (VoyageEmbeddingGenerator generator, List<TimeSpan> delays) = Create(voyage);

        await generator.GenerateAsync(["first"]);

        Assert.That(voyage.Requests, Has.Count.EqualTo(3));
        Assert.That(delays, Is.EqualTo(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) }));
    }

    [Test]
    public void GenerateAsync_RateLimitedEveryTime_GivesUpAfterMaxAttempts()
    {
        FakeVoyage voyage = new(Enumerable.Range(0, VoyageEmbeddingGenerator.MaxAttempts)
            .Select(_ => Json(HttpStatusCode.TooManyRequests, new { detail = "slow down" })).ToArray());
        (VoyageEmbeddingGenerator generator, List<TimeSpan> delays) = Create(voyage);

        HttpRequestException ex = Assert.ThrowsAsync<HttpRequestException>(() => generator.GenerateAsync(["first"]));

        Assert.That(ex.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
        Assert.That(ex.Message, Does.Contain("slow down").And.Contain($"after {VoyageEmbeddingGenerator.MaxAttempts} attempts"));
        Assert.That(delays, Has.Count.EqualTo(VoyageEmbeddingGenerator.MaxAttempts - 1));
    }

    // A malformed request (an unknown model, a text over the context with truncation off) won't succeed on a retry.
    [Test]
    public void GenerateAsync_BadRequest_ThrowsItsDetailWithoutRetrying()
    {
        FakeVoyage voyage = new(Json(HttpStatusCode.BadRequest, new { detail = "Model voyage-5 is not supported." }));

        HttpRequestException ex = Assert.ThrowsAsync<HttpRequestException>(() => Create(voyage).Generator.GenerateAsync(["first"]));

        Assert.That(ex.Message, Does.Contain("400").And.Contain("Model voyage-5 is not supported."));
        Assert.That(voyage.Requests, Has.Count.EqualTo(1));
    }

    // The index's vector column has the card's size; a vector of another size must fail here, naming the model.
    [Test]
    public void GenerateAsync_VectorOfAnotherSize_Throws()
    {
        FakeVoyage voyage = new(Ok((0, [1, 2, 3])));

        InvalidOperationException ex = Assert.ThrowsAsync<InvalidOperationException>(() => Create(voyage, dimensions: 2).Generator.GenerateAsync(["first"]));

        Assert.That(ex.Message, Does.Contain("3-dimension").And.Contain("2"));
    }

    [Test]
    public void BackOff_DoublesUpToAMinute()
    {
        Assert.That(Enumerable.Range(1, 8).Select(a => VoyageEmbeddingGenerator.BackOff(a).TotalSeconds), Is.EqualTo(new double[] { 1, 2, 4, 8, 16, 32, 60, 60 }));
    }
}
