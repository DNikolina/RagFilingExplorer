<#
.SYNOPSIS
    The local judge, kept alongside the strict grade: Microsoft's Quality evaluators, judged by the app's chat model,
    over the v3 baseline's cached answers - then their agreement with the strict grade (JudgeAgreementTests).

.DESCRIPTION
    One execution, stored in eval/v3-runs/ like any run; the answers come from the response cache (the same scenarios
    as the baseline), so only the judges ask the model. The agreement is written to eval/v3-runs/judge-<execution>.txt.
    Equivalence takes seconds a call; Groundedness is far slower, so screen it on a few questions first
    (-Judges groundedness -Only ...). Builds once, then runs with --no-build.

    After the variance passes, in one detached process:
        Start-Process powershell -WindowStyle Minimized -ArgumentList '-NoProfile -ExecutionPolicy Bypass -Command "& .\tools\run-variance.ps1; & .\tools\run-judge.ps1"'
    Progress: eval/v3-runs/logs/<execution>.log; eval/v3-runs/logs/run-judge.log.

.PARAMETER Judges
    Comma-separated judges - equivalence, groundedness (default equivalence).
.PARAMETER Only
    Comma-separated question ids (Evaluation:Only) - a smoke run first, e.g. -Only Q1,A10 -Execution judge-smoke.
.PARAMETER Execution
    The execution name (default structured-hybrid-v3-judge-<judges>).
#>
param(
    [string]$Judges = "equivalence",
    [string]$Only = "",
    [string]$Execution = ""
)

$ErrorActionPreference = 'Continue'
. "$PSScriptRoot\eval-common.ps1"
$runLog = Join-Path $Logs 'run-judge.log'
if (-not $Execution) { $Execution = "structured-hybrid-v3-judge-$($Judges -replace ',', '-')" }

Write-RunLog $runLog "Start: judges '$Judges', only '$Only', execution $Execution"
if (-not $Only) { Assert-NoOnlyInSettings }
if ((Invoke-Dotnet "build `"$EvalProject`" --nologo -v quiet" 'build-judge.log') -ne 0) { Write-RunLog $runLog "Build failed - see build-judge.log"; exit 1 }

Clear-EvalOverrides
# Both graders: the agreement sets the judge against the strict grade, so the run needs both.
Set-EvalOverride Graders 'both'
Set-EvalOverride Judges $Judges
Set-EvalOverride Execution $Execution
Set-EvalOverride NoCache 'false'
Set-EvalOverride UnloadEachQuestion 'false'
Set-EvalOverride Sets $AllSets
if ($Only) { Set-EvalOverride Only $Only }
$started = Get-Date
$exit = Invoke-Dotnet "test `"$EvalProject`" --no-build --nologo --filter FullyQualifiedName~EvaluationRunTests --logger `"console;verbosity=detailed`"" "$Execution.log"
if ($exit -ne 0) { Write-RunLog $runLog "Judge run failed (exit $exit) - see $Execution.log"; exit 1 }
Write-RunLog $runLog "Judge run done in $([int]((Get-Date) - $started).TotalMinutes) min"

Set-EvalOverride JudgeExecution $Execution
if ((Invoke-Dotnet "test `"$EvalProject`" --no-build --nologo --filter FullyQualifiedName~JudgeAgreementTests.Report_Execution" 'agreement.log') -ne 0) { Write-RunLog $runLog "Agreement report failed - see agreement.log"; exit 1 }
Write-RunLog $runLog "Done: eval/v3-runs/judge-$Execution.txt"
