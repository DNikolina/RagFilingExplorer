<#
.SYNOPSIS
    The variance measurement (v3): the evaluation's questions asked afresh several times - no response cache - then the
    passes compared with a reference run question by question (VarianceComparisonTests).

.DESCRIPTION
    Each pass is one execution, <Prefix>-<n> (or <Prefix>-unload-<n> with -UnloadEachQuestion), stored in eval/v3-runs/
    like any evaluation run, with its summary-<execution>.txt. The comparison is written to eval/v3-runs/<Prefix>.txt.
    Needs Ollama and the built index; about two hours per full pass on a CPU-only machine. Builds once, then runs with
    --no-build, so editing code while it runs doesn't change what's measured. Strict grade only: the judge needs cached
    answers (run-judge.ps1).

    Started detached so it outlives the shell that starts it (a tool command stops long before a full pass ends):
        Start-Process powershell -WindowStyle Minimized -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File tools\run-variance.ps1'
    Progress: eval/v3-runs/logs/<execution>.log (one line per question); eval/v3-runs/logs/run-variance.log (the passes).
    A smoke run's execution is stored like the rest - delete eval/v3-runs/results/<its name>/ afterwards, or it stays in
    report.html.

.PARAMETER Passes
    How many fresh passes (default 2).
.PARAMETER Sets
    Comma-separated sets - Main, HeldOut, AnswerSide (default: all three, 102 questions).
.PARAMETER Only
    Comma-separated question ids (Evaluation:Only) - a smoke run first, e.g. -Passes 1 -Only Q1,A16 -Prefix variance-smoke.
.PARAMETER UnloadEachQuestion
    Unload the chat model before every question (Evaluation:UnloadEachQuestion).
.PARAMETER Prefix
    Execution name prefix (default structured-hybrid-v3-variance).
.PARAMETER Baseline
    The execution the passes are compared with (default structured-hybrid-v3-baseline).
#>
param(
    [int]$Passes = 2,
    [string]$Sets = "",
    [string]$Only = "",
    [switch]$UnloadEachQuestion,
    [string]$Prefix = "structured-hybrid-v3-variance",
    [string]$Baseline = "structured-hybrid-v3-baseline"
)

$ErrorActionPreference = 'Continue'
. "$PSScriptRoot\eval-common.ps1"
$runLog = Join-Path $Logs 'run-variance.log'

Write-RunLog $runLog "Start: $Passes pass(es), sets '$(if ($Sets) { $Sets } else { 'all' })', only '$Only', unload $($UnloadEachQuestion.IsPresent), prefix $Prefix"
if (-not $Only) { Assert-NoOnlyInSettings }
if ((Invoke-Dotnet "build `"$EvalProject`" --nologo -v quiet" 'build.log') -ne 0) { Write-RunLog $runLog "Build failed - see build.log"; exit 1 }

Clear-EvalOverrides
$executions = @($Baseline)
for ($i = 1; $i -le $Passes; $i++) {
    $name = if ($UnloadEachQuestion) { "$Prefix-unload-$i" } else { "$Prefix-$i" }
    Set-EvalOverride Execution $name
    Set-EvalOverride Graders 'strict'
    Set-EvalOverride NoCache 'true'
    Set-EvalOverride UnloadEachQuestion $(if ($UnloadEachQuestion) { 'true' } else { 'false' })
    Set-EvalOverride Sets $(if ($Sets) { $Sets } else { $AllSets })
    if ($Only) { Set-EvalOverride Only $Only }
    Write-RunLog $runLog "Pass $i of $Passes`: $name"
    $started = Get-Date
    # The run test passes, or warns on grades that differ from v2's baseline (expected here) - a failure is a broken run.
    $exit = Invoke-Dotnet "test `"$EvalProject`" --no-build --nologo --filter FullyQualifiedName~EvaluationRunTests --logger `"console;verbosity=detailed`"" "$name.log"
    if ($exit -ne 0) { Write-RunLog $runLog "Pass $i failed (exit $exit) - see $name.log"; exit 1 }
    Write-RunLog $runLog "Pass $i done in $([int]((Get-Date) - $started).TotalMinutes) min"
    $executions += $name
}

$compareName = if ($UnloadEachQuestion) { "$Prefix-unload" } else { $Prefix }
Set-EvalOverride Compare ($executions -join ',')
Set-EvalOverride CompareName $compareName
if ((Invoke-Dotnet "test `"$EvalProject`" --no-build --nologo --filter FullyQualifiedName~VarianceComparisonTests.Compare_Executions" 'compare.log') -ne 0) { Write-RunLog $runLog "Comparison failed - see compare.log"; exit 1 }
Write-RunLog $runLog "Done: eval/v3-runs/$compareName.txt"
