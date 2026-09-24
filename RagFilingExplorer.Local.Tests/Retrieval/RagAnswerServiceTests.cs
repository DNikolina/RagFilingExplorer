using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Moq;
using RagFilingExplorer.Local.Retrieval;
using RagFilingExplorer.Local.VectorStore;

namespace RagFilingExplorer.Local.Tests.Retrieval;

/// <summary>
/// This is where Moq is actually exercised: VectorStoreCollection&lt;TKey,TRecord&gt; and IChatClient
/// are RagAnswerService's two real dependencies, mocked here so the retrieve+generate orchestration
/// (filter construction, the citation-formatted prompt, streaming the answer back) is verified without
/// a live Ollama instance or a populated vector store.
/// </summary>
[TestFixture]
public class RagAnswerServiceTests
{
    private const int SearchTopK = 5;

    // Fixture values for the service's tunables - RagAnswerService itself has no defaults (production
    // always passes appsettings.json's RetrievalSettings), so each test states only what it cares about.
    private static RagAnswerService CreateService(
        Mock<VectorStoreCollection<int, FilingChunkRecord>> collection,
        Mock<IChatClient> chatClient,
        ReasoningEffort reasoningEffort = ReasoningEffort.None,
        int maxOutputTokens = 2048,
        bool chatModelSupportsThinking = true)
    {
        RetrievalSettings retrieval = new()
        {
            DefaultSearchTopK = SearchTopK,
            VerboseSearchTopK = 25,
            GenerationTopK = 5,
            ChatTemperature = 0.2f,
            ReasoningEffort = reasoningEffort,
            MaxOutputTokens = maxOutputTokens,
        };

        return new RagAnswerService(collection.Object, chatClient.Object, retrieval, chatModelSupportsThinking);
    }

    private static async IAsyncEnumerable<VectorSearchResult<FilingChunkRecord>> AsAsync(IEnumerable<VectorSearchResult<FilingChunkRecord>> items)
    {
        foreach (VectorSearchResult<FilingChunkRecord> item in items)
        {
            yield return item;
        }

        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> AsAsync(IEnumerable<ChatResponseUpdate> items)
    {
        foreach (ChatResponseUpdate item in items)
        {
            yield return item;
        }

        await Task.CompletedTask;
    }

    private static VectorSearchResult<FilingChunkRecord> MakeResult(string sourceFiling, string heading, string content, string statementType = "narrative")
    {
        FilingChunkRecord record = new()
        {
            Key = 0,
            SourceFiling = sourceFiling,
            Heading = heading,
            StatementType = statementType,
            Content = content,
        };

        return new VectorSearchResult<FilingChunkRecord>(record, 0.5);
    }

    private static (Mock<VectorStoreCollection<int, FilingChunkRecord>> Collection, Mock<IChatClient> ChatClient) MakeMocks(
        IEnumerable<VectorSearchResult<FilingChunkRecord>>? searchResults = null,
        Action<VectorSearchOptions<FilingChunkRecord>>? onSearch = null,
        Action<IEnumerable<ChatMessage>>? onChat = null,
        Action<ChatOptions?>? onChatOptions = null)
    {
        Mock<VectorStoreCollection<int, FilingChunkRecord>> collection = new();
        collection
            .Setup(c => c.SearchAsync<string>(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<VectorSearchOptions<FilingChunkRecord>>(), It.IsAny<CancellationToken>()))
            .Callback<string, int, VectorSearchOptions<FilingChunkRecord>, CancellationToken>((_, _, options, _) => onSearch?.Invoke(options))
            .Returns(AsAsync(searchResults ?? Enumerable.Empty<VectorSearchResult<FilingChunkRecord>>()));

        Mock<IChatClient> chatClient = new();
        chatClient
            .Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((messages, options, _) =>
            {
                onChat?.Invoke(messages);
                onChatOptions?.Invoke(options);
            })
            .Returns(AsAsync([new ChatResponseUpdate(ChatRole.Assistant, "The answer.")]));

        return (collection, chatClient);
    }

    [Test]
    public async Task AskAsync_QuestionNamingOneCompany_PassesNonNullFilterToSearch()
    {
        VectorSearchOptions<FilingChunkRecord>? captured = null;
        (Mock<VectorStoreCollection<int, FilingChunkRecord>> collection, Mock<IChatClient> chatClient) = MakeMocks(onSearch: o => captured = o);

        RagAnswerService service = CreateService(collection, chatClient);
        RagAnswer answer = await service.AskAsync("What was Microsoft's revenue?", SearchTopK);

        Assert.That(answer.MatchedFilings, Is.EqualTo(new[] { "MSFT-10K-2026.html" }));
        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.Filter, Is.Not.Null, "a question naming one company must produce a search filter");
    }

    [Test]
    public async Task AskAsync_QuestionNamingNothing_PassesNoFilterToSearch()
    {
        VectorSearchOptions<FilingChunkRecord>? captured = null;
        (Mock<VectorStoreCollection<int, FilingChunkRecord>> collection, Mock<IChatClient> chatClient) = MakeMocks(onSearch: o => captured = o);

        RagAnswerService service = CreateService(collection, chatClient);
        RagAnswer answer = await service.AskAsync("What does the company do?", SearchTopK);

        Assert.That(answer.MatchedFilings, Is.Empty);
        Assert.That(answer.MatchedStatementType, Is.Null);
        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.Filter, Is.Null, "an unfiltered question must not restrict the search");
    }

    // A comparison question used to run one unfiltered search with 5 slots shared across every filing,
    // and "Compare Microsoft's and Oracle's total revenue" lost ORCL's revenue chunk to rank 10. It now
    // runs one filtered search per named company and interleaves them by rank. The mock applies each
    // search's real filter expression to a record pool, so this also checks the filters themselves.
    [Test]
    public async Task AskAsync_QuestionNamingTwoCompanies_SearchesEachSeparatelyAndInterleaves()
    {
        VectorSearchResult<FilingChunkRecord>[] pool =
        [
            MakeResult("MSFT-10K-2026.html", "H", "m1", "income_statement"),
            MakeResult("MSFT-10K-2026.html", "H", "m2", "income_statement"),
            MakeResult("MSFT-10K-2026.html", "H", "m3", "income_statement"),
            MakeResult("MSFT-10K-2026.html", "H", "m-balance", "balance_sheet"),
            MakeResult("ORCL-10K-2026.html", "H", "o1", "income_statement"),
            MakeResult("ORCL-10K-2026.html", "H", "o2", "income_statement"),
            MakeResult("ORCL-10K-2026.html", "H", "o3", "income_statement"),
            MakeResult("NDAQ-10K-2025.html", "H", "n1", "income_statement"),
        ];
        List<int> requestedTops = new();
        (Mock<VectorStoreCollection<int, FilingChunkRecord>> collection, Mock<IChatClient> chatClient) = MakeMocks();
        collection
            .Setup(c => c.SearchAsync<string>(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<VectorSearchOptions<FilingChunkRecord>>(), It.IsAny<CancellationToken>()))
            .Returns((string _, int top, VectorSearchOptions<FilingChunkRecord> options, CancellationToken _) =>
            {
                requestedTops.Add(top);
                Func<FilingChunkRecord, bool> filter = options.Filter!.Compile();
                return AsAsync(pool.Where(r => filter(r.Record)).Take(top));
            });

        RagAnswerService service = CreateService(collection, chatClient);
        RagAnswer answer = await service.AskAsync("Compare Microsoft's and Oracle's total revenue.", SearchTopK);

        Assert.That(answer.MatchedFilings, Is.EqualTo(new[] { "MSFT-10K-2026.html", "ORCL-10K-2026.html" }));
        Assert.That(answer.MatchedStatementType, Is.EqualTo("income_statement"));
        Assert.That(requestedTops, Is.EqualTo(new[] { 3, 3 }), "one search per company, ceil(5 / 2) each");
        Assert.That(answer.RetrievedChunks.Select(r => r.Record.Content), Is.EqualTo(new[] { "m1", "o1", "m2", "o2", "m3" }));
    }

    [Test]
    public async Task AskAsync_ReturnsExactlyWhatSearchReturned()
    {
        VectorSearchResult<FilingChunkRecord>[] results =
        [
            MakeResult("MSFT-10K-2026.html", "PART II > Item 8. Financial Statements", "Total revenues $100"),
        ];
        (Mock<VectorStoreCollection<int, FilingChunkRecord>> collection, Mock<IChatClient> chatClient) = MakeMocks(searchResults: results);

        RagAnswerService service = CreateService(collection, chatClient);
        RagAnswer answer = await service.AskAsync("What was total revenue?", SearchTopK);

        Assert.That(answer.RetrievedChunks, Has.Count.EqualTo(1));
        Assert.That(answer.RetrievedChunks[0].Record.Content, Is.EqualTo("Total revenues $100"));
    }

    [Test]
    public async Task AskAsync_AnswerStreamYieldsTheChatClientsText()
    {
        (Mock<VectorStoreCollection<int, FilingChunkRecord>> collection, Mock<IChatClient> chatClient) = MakeMocks();

        RagAnswerService service = CreateService(collection, chatClient);
        RagAnswer answer = await service.AskAsync("What was total revenue?", SearchTopK);

        List<string> chunks = new();
        await foreach (ChatResponseUpdate update in answer.AnswerStream)
        {
            chunks.Add(update.Text);
        }

        Assert.That(string.Concat(chunks), Is.EqualTo("The answer."));
    }

    [Test]
    public async Task AskAsync_PromptIncludesCitationContextAndTheQuestion()
    {
        VectorSearchResult<FilingChunkRecord>[] results =
        [
            MakeResult("MSFT-10K-2026.html", "PART II > Item 8. Financial Statements", "Total revenues $331,839 million"),
        ];
        List<ChatMessage>? capturedMessages = null;
        (Mock<VectorStoreCollection<int, FilingChunkRecord>> collection, Mock<IChatClient> chatClient) =
            MakeMocks(searchResults: results, onChat: m => capturedMessages = m.ToList());

        RagAnswerService service = CreateService(collection, chatClient);
        await service.AskAsync("What was Microsoft's total revenue for fiscal year 2026?", SearchTopK);

        Assert.That(capturedMessages, Is.Not.Null);
        string userMessage = capturedMessages!.Single(m => m.Role == ChatRole.User).Text;
        Assert.That(userMessage, Does.Contain("Source: MSFT-10K-2026.html | Section: PART II > Item 8. Financial Statements"));
        Assert.That(userMessage, Does.Contain("Total revenues $331,839 million"));
        Assert.That(userMessage, Does.Contain("What was Microsoft's total revenue for fiscal year 2026?"));
    }

    // Baseline case: with ReasoningEffort configured as None and a non-synthesis question, ChatOptions.Reasoning
    // must still be explicitly set to Effort.None (not left null) - Microsoft.Extensions.AI's ChatOptions.
    // Reasoning maps through OllamaSharp to Ollama's think field, and leaving it unset lets a reasoning
    // model default to thinking on its own. See the tests below for the fuller routing/gating story
    // (RequiresSynthesis, chatModelSupportsThinking) this default composes with.
    [Test]
    public async Task AskAsync_NoReasoningConfigured_SetsReasoningEffortNoneOnChatOptions()
    {
        ChatOptions? captured = null;
        (Mock<VectorStoreCollection<int, FilingChunkRecord>> collection, Mock<IChatClient> chatClient) =
            MakeMocks(onChatOptions: o => captured = o);

        RagAnswerService service = CreateService(collection, chatClient);
        await service.AskAsync("What was total revenue?", SearchTopK);

        Assert.That(captured?.Reasoning?.Effort, Is.EqualTo(ReasoningEffort.None));
    }

    // Reasoning is only worth its cost on questions that actually need multi-step synthesis (see
    // QueryIntentResolver.RequiresSynthesis) - a plain lookup always gets Effort.None regardless of the
    // configured reasoningEffort, so it's never wasted on a task that doesn't need it.
    [Test]
    public async Task AskAsync_SynthesisQuestion_UsesConfiguredReasoningEffort()
    {
        ChatOptions? captured = null;
        (Mock<VectorStoreCollection<int, FilingChunkRecord>> collection, Mock<IChatClient> chatClient) =
            MakeMocks(onChatOptions: o => captured = o);

        RagAnswerService service = CreateService(collection, chatClient, reasoningEffort: ReasoningEffort.High);
        RagAnswer answer = await service.AskAsync("Compare Microsoft's and Oracle's revenue.", SearchTopK);

        Assert.That(captured?.Reasoning?.Effort, Is.EqualTo(ReasoningEffort.High));
        Assert.That(answer.UsedReasoningEffort, Is.EqualTo(ReasoningEffort.High));
    }

    [Test]
    public async Task AskAsync_NonSynthesisQuestion_IgnoresConfiguredReasoningEffort()
    {
        ChatOptions? captured = null;
        (Mock<VectorStoreCollection<int, FilingChunkRecord>> collection, Mock<IChatClient> chatClient) =
            MakeMocks(onChatOptions: o => captured = o);

        RagAnswerService service = CreateService(collection, chatClient, reasoningEffort: ReasoningEffort.High);
        RagAnswer answer = await service.AskAsync("What was total revenue?", SearchTopK);

        Assert.That(captured?.Reasoning?.Effort, Is.EqualTo(ReasoningEffort.None), "a plain lookup must not pay for reasoning");
        Assert.That(answer.UsedReasoningEffort, Is.EqualTo(ReasoningEffort.None));
    }

    // Regression coverage for a second real bug, found immediately after the first live test of the
    // routing above: Ollama doesn't quietly ignore a "think" request for a model that can't reason - it
    // throws a hard OllamaException ("<model> does not support thinking"), which crashed the whole app
    // the first time a synthesis question tried to route llama3.1:8b to Effort.Medium. chatModelSupportsThinking
    // (set from Ollama's own /api/show capabilities in Program.cs, not assumed) must gate the routing too.
    [Test]
    public async Task AskAsync_SynthesisQuestion_ButModelDoesNotSupportThinking_UsesNone()
    {
        ChatOptions? captured = null;
        (Mock<VectorStoreCollection<int, FilingChunkRecord>> collection, Mock<IChatClient> chatClient) =
            MakeMocks(onChatOptions: o => captured = o);

        RagAnswerService service = CreateService(collection, chatClient, reasoningEffort: ReasoningEffort.High, chatModelSupportsThinking: false);
        RagAnswer answer = await service.AskAsync("Compare Microsoft's and Oracle's revenue.", SearchTopK);

        Assert.That(captured?.Reasoning?.Effort, Is.EqualTo(ReasoningEffort.None), "must never request thinking from a model that can't do it");
        Assert.That(answer.UsedReasoningEffort, Is.EqualTo(ReasoningEffort.None));
    }

    [Test]
    public async Task AskAsync_MaxOutputTokens_IsPassedThroughToChatOptions()
    {
        ChatOptions? captured = null;
        (Mock<VectorStoreCollection<int, FilingChunkRecord>> collection, Mock<IChatClient> chatClient) =
            MakeMocks(onChatOptions: o => captured = o);

        RagAnswerService service = CreateService(collection, chatClient, maxOutputTokens: 4096);
        await service.AskAsync("What was total revenue?", SearchTopK);

        Assert.That(captured?.MaxOutputTokens, Is.EqualTo(4096));
    }

    // Regression coverage for the actual failure mode this session found: a reasoning model can hit its
    // output-token ceiling entirely while "thinking" and never produce real answer text. Ollama still
    // reports this as a normal completion (FinishReason.Length), so nothing upstream throws by default -
    // the caller would just see an empty answer with no explanation. RagAnswerService now wraps the
    // stream to detect exactly this (no real text + FinishReason.Length) and fail loudly instead.
    [Test]
    public void AskAsync_StarvedResponse_ThrowsWhenNoRealTextAndFinishReasonIsLength()
    {
        (Mock<VectorStoreCollection<int, FilingChunkRecord>> collection, Mock<IChatClient> chatClient) = MakeMocks();
        chatClient
            .Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns(AsAsync(
            [
                new ChatResponseUpdate(ChatRole.Assistant, "") { Contents = [new TextReasoningContent("Thinking a lot...")] },
                new ChatResponseUpdate(ChatRole.Assistant, "") { FinishReason = ChatFinishReason.Length },
            ]));

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            RagAnswerService service = CreateService(collection, chatClient);
            RagAnswer answer = await service.AskAsync("What was total revenue?", SearchTopK);
            await foreach (ChatResponseUpdate _ in answer.AnswerStream)
            {
            }
        });
    }

    [Test]
    public async Task AskAsync_NormalCompletion_DoesNotThrowEvenWithFinishReasonLength()
    {
        // A response that hit the length cap but still produced real text is a truncated-but-real
        // answer, not a starved one - it must be shown, not treated as an error.
        (Mock<VectorStoreCollection<int, FilingChunkRecord>> collection, Mock<IChatClient> chatClient) = MakeMocks();
        chatClient
            .Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns(AsAsync(
            [
                new ChatResponseUpdate(ChatRole.Assistant, "The answer.") { FinishReason = ChatFinishReason.Length },
            ]));

        RagAnswerService service = CreateService(collection, chatClient);
        RagAnswer answer = await service.AskAsync("What was total revenue?", SearchTopK);

        List<string> chunks = new();
        await foreach (ChatResponseUpdate update in answer.AnswerStream)
        {
            chunks.Add(update.Text);
        }

        Assert.That(string.Concat(chunks), Is.EqualTo("The answer."));
    }
}
