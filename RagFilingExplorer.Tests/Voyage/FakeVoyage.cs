namespace RagFilingExplorer.Tests.Voyage;

/// <summary>Voyage's endpoint, answered from a queue; every request body kept.</summary>
internal sealed class FakeVoyage(params HttpResponseMessage[] responses) : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> responses = new(responses);

    public List<(Uri? Uri, string Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add((request.RequestUri, await request.Content!.ReadAsStringAsync(cancellationToken)));
        return responses.Dequeue();
    }
}
