using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Reporting;
using Microsoft.Extensions.AI.Evaluation.Reporting.Storage;
using RagFilingExplorer.Local.Evaluation.Running;

namespace RagFilingExplorer.Local.Evaluation;

/// <summary>Which questions a run asks, and what an earlier run under the same name leaves behind - offline: the
/// question files and expected answers in tools/, and a store in a temp directory.</summary>
[TestFixture]
public class QuestionSelectionTests
{
    private static readonly DirectoryInfo Repo = RepoPaths.FindRoot(AppContext.BaseDirectory);

    [Test]
    public void SelectQuestions_NoIds_EveryQuestionOfTheSets()
    {
        List<SelectedQuestion> questions = EvaluationRunner.SelectQuestions(Repo, EvaluationRunner.AllSets, new HashSet<string>());

        Assert.That(questions, Has.Count.EqualTo(102));
        Assert.That(questions[0].Set.Name, Is.EqualTo("Main"));
    }

    [Test]
    public void SelectQuestions_Ids_OnlyThoseInTheSetsOrder()
    {
        List<SelectedQuestion> questions = EvaluationRunner.SelectQuestions(Repo, EvaluationRunner.AllSets, new HashSet<string> { "A16", "Q1" });

        Assert.That(questions.Select(q => $"{q.Set.Name}.{q.Entry.Id}"), Is.EqualTo(new[] { "Main.Q1", "AnswerSide.A16" }));
    }

    // A mistyped id (A61 for A16) must stop the run, not make a smoke run that asks nothing and passes.
    [Test]
    public void SelectQuestions_UnknownId_FailsNamingIt()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            EvaluationRunner.SelectQuestions(Repo, EvaluationRunner.AllSets, new HashSet<string> { "Q1", "A61" }))!;

        Assert.That(error.Message, Does.Contain("A61").And.Not.Contain("Q1,"));
    }

    // A run under a stored run's name only overwrites the questions it asks; the rest stay under that name, so the run
    // lists them instead of letting them pass silently as part of it.
    [Test]
    public async Task EarlierScenariosAsync_StoredButNotAsked_Listed()
    {
        string storage = Path.Combine(Path.GetTempPath(), $"QuestionSelectionTests-{Guid.NewGuid():N}");
        try
        {
            DiskBasedResultStore store = new(storage);
            ChatMessage[] messages = [new ChatMessage(ChatRole.User, "q")];
            ChatResponse response = new(new ChatMessage(ChatRole.Assistant, "a"));
            await store.WriteResultsAsync(new[] { "Main.Q1", "Main.Q2", "AnswerSide.A16" }
                .Select(s => new ScenarioRunResult(s, "1", "full", DateTime.UtcNow, messages, response, new EvaluationResult()))
                .ToList());

            List<string> kept = await EvaluationRunner.EarlierScenariosAsync(storage, "full", new HashSet<string> { "Main.Q1" });
            List<string> none = await EvaluationRunner.EarlierScenariosAsync(storage, "new-name", new HashSet<string> { "Main.Q1" });

            Assert.That(kept, Is.EqualTo(new[] { "AnswerSide.A16", "Main.Q2" }));
            Assert.That(none, Is.Empty);
        }
        finally
        {
            Directory.Delete(storage, recursive: true);
        }
    }

    // An id that exists, but in a set the run doesn't include, would also ask nothing.
    [Test]
    public void SelectQuestions_IdOutsideTheSetsRun_Fails()
    {
        List<QuestionSet> mainOnly = EvaluationRunner.AllSets.Where(s => s.Name == "Main").ToList();

        Assert.That(() => EvaluationRunner.SelectQuestions(Repo, mainOnly, new HashSet<string> { "A16" }),
            Throws.InvalidOperationException.With.Message.Contains("A16").And.Message.Contains("(Main)"));
    }
}
