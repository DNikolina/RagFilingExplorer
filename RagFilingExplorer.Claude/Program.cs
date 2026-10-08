using Microsoft.Extensions.AI;
using RagFilingExplorer.Claude;
using RagFilingExplorer.Local;
using RagFilingExplorer.Local.Retrieval;

using IDisposable utf8Console = Utf8Console.Use();

// The local app's question loop over its existing index, with Claude answering. The index is built by the local app
// (dotnet run --project RagFilingExplorer.Local) - this one never builds it - and embeddings stay Ollama's, so Ollama
// still has to run.
try
{
    await RunAsync(args);
    return 0;
}
catch (StartupException ex)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"[startup] {ex.Message}");
    return 1;
}

static async Task RunAsync(string[] args)
{
    AppSettings settings = Load(() => AppSettings.Load(AppContext.BaseDirectory));
    ClaudeSettings claude = Load(() => ClaudeSettings.Load(AppContext.BaseDirectory));
    DirectoryInfo repoRoot = RepoPaths.FindRoot(AppContext.BaseDirectory);

    (IChatClient chat, ChatModelOptions options) = await ClaudeChat.CreateAsync(claude);
    Console.WriteLine($"Chat model: {claude.Model} (Claude API; effort {claude.LookupEffort} for lookups, {claude.SynthesisEffort} for synthesis)");

    using RagRuntime runtime = await AppComposition.OpenExistingIndexAsync(settings, repoRoot, chat, options);
    await InteractiveSession.RunAsync(runtime.AnswerService, args.Contains("--verbose"), settings.Retrieval);
}

static T Load<T>(Func<T> load)
{
    try
    {
        return load();
    }
    catch (InvalidOperationException ex)
    {
        throw new StartupException(ex.Message, ex);
    }
}
