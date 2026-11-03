#!/usr/bin/env -S dotnet --
#:include eval-common.cs

// The variance measurement: the evaluation's questions asked afresh several times - no response cache - then the passes
// compared with a reference run question by question (VarianceComparisonTests).
//
// Each pass is one execution, <prefix>-<n> (or <prefix>-unload-<n> with --unload-each-question), stored in eval/v3-runs/
// like any evaluation run, with its summary-<execution>.txt. The comparison is written to eval/v3-runs/<prefix>.txt. Needs
// Ollama and the built index. Strict grade only: the judge needs cached answers (run-judge.cs).
//
//     dotnet tools/run-variance.cs -- --passes 1 --only Q1,A16 --prefix variance-smoke
//
// Start a full run detached, so it outlives the shell that starts it (a tool command stops long before a pass ends):
//     macOS, Linux:  nohup dotnet tools/run-variance.cs > /dev/null 2>&1 &
//     Windows:       Start-Process dotnet -WindowStyle Minimized -ArgumentList 'tools/run-variance.cs'
// Progress: eval/v3-runs/logs/<execution>.log (one line per question); eval/v3-runs/logs/run-variance.log (the passes).
// A smoke run's execution is stored like the rest - delete eval/v3-runs/results/<its name>/ afterwards, or it stays in
// report.html.
//
// --passes <n>              How many fresh passes (default 2).
// --sets <list>             Comma-separated sets - Main, HeldOut, AnswerSide (default: all three).
// --only <ids>              Comma-separated question ids (Evaluation:Only) - a smoke run first.
// --unload-each-question    Unload the chat model before every question (Evaluation:UnloadEachQuestion).
// --prefix <name>           Execution name prefix (default structured-hybrid-v3-variance).
// --baseline <execution>    The execution the passes are compared with (default structured-hybrid-v3-baseline).

ScriptOptions options = new(args, ["--passes", "--sets", "--only", "--prefix", "--baseline"], ["--unload-each-question"]);
int passes = options.IntValue("--passes", 2);
string sets = options.Value("--sets", EvalRun.AllSets);
string only = options.Value("--only", "");
bool unload = options.Switch("--unload-each-question");
string prefix = options.Value("--prefix", "structured-hybrid-v3-variance");
string baseline = options.Value("--baseline", "structured-hybrid-v3-baseline");
string executionPrefix = unload ? $"{prefix}-unload" : prefix;
if (passes < 1)
{
    EvalRun.Stop("--passes needs at least 1: the comparison needs a pass to compare.");
}

EvalRun run = new("run-variance.log");
run.Log($"Start: {passes} pass(es), sets '{sets}', only '{only}', unload {unload}, prefix {prefix}");
run.RestrictTo(only);
run.Override("Graders", "strict");
run.Override("NoCache", "true");
run.Override("UnloadEachQuestion", unload ? "true" : "false");
run.Override("Sets", sets);

if (run.Build("build.log") != 0)
{
    run.Log("Build failed - see build.log");

    return 1;
}

List<string> executions = [baseline];
for (int pass = 1; pass <= passes; pass++)
{
    string name = $"{executionPrefix}-{pass}";
    run.Override("Execution", name);
    run.Log($"Pass {pass} of {passes}: {name}");
    DateTime started = DateTime.Now;

    // The run test passes, or warns on grades that differ from v2's baseline (expected here) - a failure is a broken run.
    int exit = run.RunEvaluation($"{name}.log");
    if (exit != 0)
    {
        run.Log($"Pass {pass} failed (exit {exit}) - see {name}.log");

        return 1;
    }

    run.Log($"Pass {pass} done in {(int)Math.Round((DateTime.Now - started).TotalMinutes)} min");
    executions.Add(name);
}

run.Override("Compare", string.Join(',', executions));
run.Override("CompareName", executionPrefix);
if (run.Test("FullyQualifiedName~VarianceComparisonTests.Compare_Executions", "compare.log") != 0)
{
    run.Log("Comparison failed - see compare.log");

    return 1;
}

run.Log($"Done: eval/v3-runs/{executionPrefix}.txt");

return 0;
