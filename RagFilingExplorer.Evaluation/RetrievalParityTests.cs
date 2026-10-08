using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using RagFilingExplorer.Evaluation.Evaluators;
using RagFilingExplorer.Evaluation.Grading;
using RagFilingExplorer.Local.Retrieval;

namespace RagFilingExplorer.Evaluation;

/// <summary>
/// The app, asked in-process with its shipped settings (AppComposition - the console's own wiring),
/// ranks every question's answer exactly where tools/replay_recall.py ranked it from the v2 baseline logs - the replay's
/// output is committed in eval/v3-retrieval-parity/. Also the fused score of the first chunk, to the replay's four
/// decimals. Needs Ollama (question embeddings) and the built rag.structured.db; the chat model is never asked - its
/// client throws if anything does. [Explicit]: runs only when asked for, e.g.
///   dotnet test RagFilingExplorer.Evaluation --filter "FullyQualifiedName~RetrievalParityTests"
/// </summary>
[TestFixture]
[Explicit("Needs Ollama and the built index.")]
public class RetrievalParityTests
{
    private static readonly DirectoryInfo Repo = RepoPaths.FindRoot(AppContext.BaseDirectory);

    // " A10 rank=   3  in-top-5=yes  filter=...  top1=0.0328", or "A12 (negative test, not scored)  filter=...  top1=..."
    private static readonly Regex ReplayLine = new(@"^\s*([A-Z]\d+) (?:rank=\s*(>?\d+)|\(negative test, not scored\)).*top1=(\S+)\s*$");

    private sealed class NoChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The retrieval parity check must not ask the chat model.");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new InvalidOperationException("The retrieval parity check must not ask the chat model.");
#pragma warning disable CS0162 // an async iterator needs a yield
            yield break;
#pragma warning restore CS0162
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    [TestCase("manual-questions.txt", "replay-main.txt")]
    [TestCase("heldout-questions.txt", "replay-heldout.txt")]
    [TestCase("answer-questions.txt", "replay-answer.txt")]
    public async Task AskAsync_EveryQuestion_RanksTheAnswerWhereTheReplayDid(string questionsFile, string replayFile)
    {
        AppSettings settings = AppSettings.Load(Path.Combine(Repo.FullName, "RagFilingExplorer.Local"));
        IReadOnlyDictionary<string, ExpectedAnswer> expected = ExpectedAnswer.LoadAll(Repo);
        Dictionary<string, ExpectedAnswer> byQuestion = expected.Values.ToDictionary(e => e.Question);
        string[] questions = File.ReadAllLines(Path.Combine(Repo.FullName, "tools", questionsFile)).Where(q => q.Trim().Length > 0).ToArray();
        List<Match> replay = File.ReadAllLines(Path.Combine(Repo.FullName, "eval", "v3-retrieval-parity", replayFile))
            .Select(l => ReplayLine.Match(l)).Where(m => m.Success).ToList();
        Assert.That(replay, Has.Count.EqualTo(questions.Length), "one replay line per question");

        using RagRuntime runtime = await AppComposition.OpenExistingIndexAsync(settings, Repo, new NoChatClient());
        List<string> differences = [];
        for (int i = 0; i < questions.Length; i++)
        {
            // Line i of the questions file is line i of the replay. (The replay labels main-set lines by position - its
            // "Q14" is line 14, which is Q17 in expected-answers.json's ids - so the lines are matched, not the labels.)
            ExpectedAnswer entry = byQuestion[questions[i].Trim()];

            RagAnswer answer = await runtime.AnswerService.AskAsync(questions[i], settings.Retrieval.VerboseSearchTopK);
            List<string> chunks = answer.RetrievedChunks.Select(r => r.Record.Content).ToList();

            string replayTop = replay[i].Groups[3].Value;
            string mineTop = answer.RetrievedChunks.Count == 0 ? "-" : (answer.RetrievedChunks[0].Score ?? 0).ToString("F4", CultureInfo.InvariantCulture);
            if (mineTop != replayTop)
            {
                differences.Add($"{entry.Id}: top score replay {replayTop}, app {mineTop}");
            }

            if (entry.ChunkExpect is not { Count: > 0 })
            {
                continue;
            }

            // The replay ranks within each company's top 25; the app's 25 are the same list, cut at 25 overall.
            string replayRank = replay[i].Groups[2].Value;
            int? expectedRank = replayRank.StartsWith('>') || int.Parse(replayRank, CultureInfo.InvariantCulture) > chunks.Count
                ? null
                : int.Parse(replayRank, CultureInfo.InvariantCulture);
            int? rank = RetrievalRankEvaluator.RankOf(chunks, entry.ChunkExpect);
            if (rank != expectedRank)
            {
                differences.Add($"{entry.Id}: rank replay {replayRank}, app {rank?.ToString(CultureInfo.InvariantCulture) ?? "none"}");
            }
        }

        Assert.That(differences, Is.Empty);
    }
}
