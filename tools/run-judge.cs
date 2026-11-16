#!/usr/bin/env -S dotnet --
#:include eval-common.cs

// The local judge, kept alongside the strict grade: Microsoft's Quality evaluators, judged by the app's chat model, over
// the baseline's cached answers - then their agreement with the strict grade (JudgeAgreementTests).
//
// One execution, stored in eval/v3-runs/ like any run; the answers come from the response cache (the same scenarios as
// the baseline), so only the judges ask the model. The agreement is written to eval/v3-runs/judge-<execution>.txt.
// Equivalence takes seconds a call; Groundedness is far slower, so screen it on a few questions first
// (--judges groundedness --only ...).
//
//     dotnet tools/run-judge.cs -- --only Q1,A10 --execution judge-smoke
//
// After the variance passes, in one detached process:
//     macOS, Linux:  nohup sh -c 'dotnet tools/run-variance.cs; dotnet tools/run-judge.cs' > /dev/null 2>&1 &
//     Windows:       Start-Process powershell -WindowStyle Minimized -ArgumentList '-NoProfile -Command "dotnet tools/run-variance.cs; dotnet tools/run-judge.cs"'
// Progress: eval/v3-runs/logs/<execution>.log; eval/v3-runs/logs/run-judge.log.
//
// --judges <list>       Comma-separated judges - equivalence, groundedness (default equivalence).
// --only <ids>          Comma-separated question ids (Evaluation:Only) - a smoke run first.
// --execution <name>    The execution name (default structured-hybrid-v3-judge-<judges>).

ScriptOptions options = new(args, ["--judges", "--only", "--execution"], []);
string judges = options.Value("--judges", "equivalence");
string only = options.Value("--only", "");
string execution = options.Value("--execution", $"structured-hybrid-v3-judge-{judges.Replace(',', '-')}");

EvalRun run = new("run-judge.log");
run.Log($"Start: judges '{judges}', only '{only}', execution {execution}");
run.RestrictTo(only);

if (run.Build("build-judge.log") != 0)
{
    run.Log("Build failed - see build-judge.log");

    return 1;
}

// Both graders: the agreement sets the judge against the strict grade, so the run needs both.
run.Override("Graders", "both");
run.Override("Judges", judges);
run.Override("Execution", execution);
run.Override("NoCache", "false");
run.Override("UnloadEachQuestion", "false");
run.Override("Sets", EvalRun.AllSets);

DateTime started = DateTime.Now;
int exit = run.RunEvaluation($"{execution}.log");
if (exit != 0)
{
    run.Log($"Judge run failed (exit {exit}) - see {execution}.log");

    return 1;
}

run.Log($"Judge run done in {(int)Math.Round((DateTime.Now - started).TotalMinutes)} min");

run.Override("JudgeExecution", execution);
if (run.Test("FullyQualifiedName~JudgeAgreementTests.Report_Execution", "agreement.log") != 0)
{
    run.Log("Agreement report failed - see agreement.log");

    return 1;
}

run.Log($"Done: eval/v3-runs/judge-{execution}.txt");

return 0;
