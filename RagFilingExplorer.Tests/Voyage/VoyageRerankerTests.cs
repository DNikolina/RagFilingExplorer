using System.Net;
using System.Text;
using System.Text.Json;
using RagFilingExplorer.Voyage;

namespace RagFilingExplorer.Tests.Voyage;

[TestFixture]
public class VoyageRerankerTests
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

    // Voyage's response: best first, each result naming its candidate by index.
    private static HttpResponseMessage Ok(int totalTokens, params (int Index, float Score)[] results) => Json(HttpStatusCode.OK, new
    {
        @object = "list",
        data = results.Select(r => new { relevance_score = r.Score, index = r.Index }),
        model = "rerank-3",
        usage = new { total_tokens = totalTokens },
    });

    private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private static VoyageReranker Create(FakeVoyage voyage) =>
        new(new HttpClient(voyage) { BaseAddress = new Uri("https://api.voyageai.com/v1/") }, "rerank-3", (_, _) => Task.CompletedTask);

    // The request Voyage's reranker reference documents; no top_k, so every candidate comes back scored, and truncation
    // false so an over-long candidate fails instead of being cut.
    [Test]
    public async Task ScoreAsync_Candidates_PostsQueryDocumentsModelAndNoTruncation()
    {
        FakeVoyage voyage = new(Ok(9, (0, 0.5f), (1, 0.25f)));

        await Create(voyage).ScoreAsync("What was revenue?", ["Revenue 100", "Cost 40"]);

        Assert.That(voyage.Requests.Single().Uri!.AbsoluteUri, Is.EqualTo("https://api.voyageai.com/v1/rerank"));
        using JsonDocument body = JsonDocument.Parse(voyage.Requests.Single().Body);
        Assert.That(body.RootElement.GetProperty("query").GetString(), Is.EqualTo("What was revenue?"));
        Assert.That(body.RootElement.GetProperty("documents").EnumerateArray().Select(d => d.GetString()), Is.EqualTo(new[] { "Revenue 100", "Cost 40" }));
        Assert.That(body.RootElement.GetProperty("model").GetString(), Is.EqualTo("rerank-3"));
        Assert.That(body.RootElement.GetProperty("truncation").GetBoolean(), Is.False);
        Assert.That(body.RootElement.TryGetProperty("top_k", out _), Is.False);
    }

    // The answer service pairs score i with candidate i; Voyage sorts its results by score.
    [Test]
    public async Task ScoreAsync_ResultsBestFirst_ReturnsScoresInCandidateOrder()
    {
        FakeVoyage voyage = new(Ok(9, (2, 0.9f), (0, 0.5f), (1, 0.1f)));

        IReadOnlyList<float> scores = await Create(voyage).ScoreAsync("q", ["a", "b", "c"]);

        Assert.That(scores, Is.EqualTo(new[] { 0.5f, 0.1f, 0.9f }));
    }

    [TestCase(new[] { 0 }, "scored 1 of 2")]
    [TestCase(new[] { 0, 0 }, "index 0")]
    [TestCase(new[] { 0, 2 }, "index 2")]
    public void ScoreAsync_IndexMissingRepeatedOrOutOfRange_Throws(int[] indexes, string message)
    {
        FakeVoyage voyage = new(Ok(9, indexes.Select(i => (i, 0.5f)).ToArray()));

        InvalidOperationException ex = Assert.ThrowsAsync<InvalidOperationException>(() => Create(voyage).ScoreAsync("q", ["a", "b"]))!;

        Assert.That(ex.Message, Does.Contain(message));
    }

    [Test]
    public async Task ScoreAsync_TwoCalls_AddsUpTheTokensVoyageCounted()
    {
        FakeVoyage voyage = new(Ok(120, (0, 0.5f)), Ok(80, (0, 0.5f)));
        VoyageReranker reranker = Create(voyage);

        await reranker.ScoreAsync("q", ["a"]);
        await reranker.ScoreAsync("q", ["b"]);

        Assert.That(reranker.TotalTokens, Is.EqualTo(200));
    }

    [Test]
    public async Task ScoreAsync_NoCandidates_SendsNothing()
    {
        FakeVoyage voyage = new();

        Assert.That(await Create(voyage).ScoreAsync("q", []), Is.Empty);
        Assert.That(voyage.Requests, Is.Empty);
    }

    // The shared POST retries a rate limit for the reranker as for the embedder.
    [Test]
    public async Task ScoreAsync_RateLimitedThenOk_Retries()
    {
        FakeVoyage voyage = new(Json(HttpStatusCode.TooManyRequests, new { detail = "slow down" }), Ok(9, (0, 0.5f)));

        IReadOnlyList<float> scores = await Create(voyage).ScoreAsync("q", ["a"]);

        Assert.That(scores, Is.EqualTo(new[] { 0.5f }));
        Assert.That(voyage.Requests, Has.Count.EqualTo(2));
    }

    [Test]
    public void Name_NamesVoyageAndTheModel()
    {
        Assert.That(Create(new FakeVoyage()).Name, Is.EqualTo("Voyage rerank-3"));
    }
}
