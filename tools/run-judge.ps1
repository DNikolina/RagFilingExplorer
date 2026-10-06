<#
.SYNOPSIS
    The local judge (v3 step 6, kept alongside the strict grade): Microsoft's Quality evaluators, judged by the app's
    chat model, over the v3 baseline's cached answers - then their agreement with the strict grade (JudgeAgreementTests).

.DESCRIPTION
    One execution, stored in eval/v3-runs/ like any run; the answers come from the response cache (the same scenarios
    as the baseline), so only the judges ask the model. The agreement is written to eval/v3-runs/judge-<execution>.txt.
    Equivalence takes ~8 s a call, ~15 min for all 102 questions; Groundedness ~100 s a call, so screen it on a few
    questions first (-Judges groundedness -Only ...). Builds once, then runs with --no-build.

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

# evalsettings.json holds the run's defaults; these Evaluation__<key> variables override a key for this run. Start
# clean: run-variance.ps1 and run-judge.ps1 can run in one process, and one's overrides mustn't reach the other.
Get-ChildItem Env: | Where-Object { $_.Name -like 'Evaluation__*' } | ForEach-Object { Remove-Item "Env:$($_.Name)" }
# Both graders: the agreement sets the judge against the strict grade, so the run needs both.
$env:Evaluation__Graders = 'both'
$env:Evaluation__Judges = $Judges
$env:Evaluation__Execution = $Execution
$env:Evaluation__Only = $Only
$env:Evaluation__NoCache = 'false'
$env:Evaluation__UnloadEachQuestion = 'false'
$env:Evaluation__Sets = ''
$started = Get-Date
$exit = Invoke-Dotnet "test `"$project`" --no-build --nologo --filter FullyQualifiedName~EvaluationRunTests --logger `"console;verbosity=detailed`"" "$Execution.log"
if ($exit -ne 0) { Log "Judge run failed (exit $exit) - see $Execution.log"; exit 1 }
Log "Judge run done in $([int]((Get-Date) - $started).TotalMinutes) min"

$env:Evaluation__JudgeExecution = $Execution
if ((Invoke-Dotnet "test `"$project`" --no-build --nologo --filter FullyQualifiedName~JudgeAgreementTests.Report_Execution" 'agreement.log') -ne 0) { Log "Agreement report failed - see agreement.log"; exit 1 }
Log "Done: eval/v3-runs/judge-$Execution.txt"
