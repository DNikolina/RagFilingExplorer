<#
.SYNOPSIS
    The local-judge spike (v3 step 6): Microsoft's Quality evaluators, judged by the app's chat model, over the v3
    baseline's cached answers - then their agreement with the strict grade (JudgeAgreementTests).

.DESCRIPTION
    One execution, stored in eval/v3-runs/ like any run; the answers come from the response cache (the same scenarios
    as the baseline), so only the judges ask the model. The agreement is written to eval/v3-runs/judge-<execution>.txt.
    Phase A is Equivalence (~25 s a question, ~45 min for all 102); Groundedness (~2 min a question) only after A
    (user, 2026-10-05). Builds once, then runs with --no-build.

    After the variance passes, in one detached process:
        Start-Process powershell -WindowStyle Minimized -ArgumentList '-NoProfile -ExecutionPolicy Bypass -Command "& .\tools\run-variance.ps1; & .\tools\run-judge.ps1"'
    Progress: eval/v3-runs/logs/<execution>.log; eval/v3-runs/logs/run-judge.log.

.PARAMETER Judges
    Comma-separated judges - equivalence, groundedness (default equivalence: phase A).
.PARAMETER Only
    Comma-separated question ids (EVAL_ONLY) - a smoke run first, e.g. -Only Q1,A10 -Execution judge-smoke.
.PARAMETER Execution
    The execution name (default structured-hybrid-v3-judge-<judges>).
#>
param(
    [string]$Judges = "equivalence",
    [string]$Only = "",
    [string]$Execution = ""
)

$ErrorActionPreference = 'Continue'
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'RagFilingExplorer.Local.Evaluation'
$logs = Join-Path $repo 'eval\v3-runs\logs'
New-Item -ItemType Directory -Force $logs | Out-Null
$runLog = Join-Path $logs 'run-judge.log'
if (-not $Execution) { $Execution = "structured-hybrid-v3-judge-$($Judges -replace ',', '-')" }

# dotnet through cmd: Windows PowerShell 5.1's *> writes UTF-16 and turns stderr lines into errors (see run-variance.ps1).
function Invoke-Dotnet([string]$arguments, [string]$logName) {
    cmd /c "dotnet $arguments > `"$(Join-Path $logs $logName)`" 2>&1"
    return $LASTEXITCODE
}

function Log([string]$message) {
    $line = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  $message"
    Add-Content -Path $runLog -Value $line -Encoding utf8
    Write-Host $line
}

Log "Start: judges '$Judges', only '$Only', execution $Execution"
if ((Invoke-Dotnet "build `"$project`" --nologo -v quiet" 'build-judge.log') -ne 0) { Log "Build failed - see build-judge.log"; exit 1 }

$env:EVAL_EXECUTION = $Execution
$env:EVAL_JUDGE = $Judges
$env:EVAL_ONLY = $Only
$env:EVAL_NO_CACHE = ''
$env:EVAL_UNLOAD = ''
$env:EVAL_SETS = ''
$started = Get-Date
$exit = Invoke-Dotnet "test `"$project`" --no-build --nologo --filter FullyQualifiedName~EvaluationRunTests --logger `"console;verbosity=detailed`"" "$Execution.log"
if ($exit -ne 0) { Log "Judge run failed (exit $exit) - see $Execution.log"; exit 1 }
Log "Judge run done in $([int]((Get-Date) - $started).TotalMinutes) min"

$env:EVAL_JUDGE_EXECUTION = $Execution
if ((Invoke-Dotnet "test `"$project`" --no-build --nologo --filter FullyQualifiedName~JudgeAgreementTests.Report_Execution" 'agreement.log') -ne 0) { Log "Agreement report failed - see agreement.log"; exit 1 }
Log "Done: eval/v3-runs/judge-$Execution.txt"
