<#
.SYNOPSIS
    The variance measurement (v3): the evaluation's questions asked afresh several times - no response cache - then the
    passes compared with the v3 baseline question by question (VarianceComparisonTests).

.DESCRIPTION
    Each pass is one execution, <Prefix>-<n> (or <Prefix>-unload-<n> with -UnloadEachQuestion), stored in eval/v3-runs/
    like any evaluation run, with its summary-<execution>.txt. The comparison is written to eval/v3-runs/<Prefix>.txt.
    Needs Ollama and the built index; about two hours per full pass on this machine. Builds once, then runs with
    --no-build, so editing code while it runs doesn't change what's measured.

    Started detached so it outlives the shell that starts it (a tool command stops at two hours):
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
.PARAMETER Graders
    strict, judge or both (Evaluation:Graders); empty: evalsettings.json's.
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
    [string]$Graders = "",
    [string]$Prefix = "structured-hybrid-v3-variance",
    [string]$Baseline = "structured-hybrid-v3-baseline"
)

$ErrorActionPreference = 'Continue'
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'RagFilingExplorer.Local.Evaluation'
$logs = Join-Path $repo 'eval\v3-runs\logs'
New-Item -ItemType Directory -Force $logs | Out-Null
$runLog = Join-Path $logs 'run-variance.log'

# dotnet through cmd: Windows PowerShell 5.1's *> writes UTF-16 and turns stderr lines into errors; cmd's redirect keeps
# the bytes, and the exit code alone decides.
function Invoke-Dotnet([string]$arguments, [string]$logName) {
    cmd /c "dotnet $arguments > `"$(Join-Path $logs $logName)`" 2>&1"
    return $LASTEXITCODE
}

function Log([string]$message) {
    $line = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  $message"
    Add-Content -Path $runLog -Value $line -Encoding utf8
    Write-Host $line
}

Log "Start: $Passes pass(es), sets '$(if ($Sets) { $Sets } else { 'all' })', only '$Only', unload $($UnloadEachQuestion.IsPresent), prefix $Prefix"
if ((Invoke-Dotnet "build `"$project`" --nologo -v quiet" 'build.log') -ne 0) { Log "Build failed - see build.log"; exit 1 }

# evalsettings.json holds the run's defaults; these Evaluation__<key> variables override a key for this run. Start
# clean: run-variance.ps1 and run-judge.ps1 can run in one process, and one's overrides mustn't reach the other.
Get-ChildItem Env: | Where-Object { $_.Name -like 'Evaluation__*' } | ForEach-Object { Remove-Item "Env:$($_.Name)" }
$executions = @($Baseline)
for ($i = 1; $i -le $Passes; $i++) {
    $name = if ($UnloadEachQuestion) { "$Prefix-unload-$i" } else { "$Prefix-$i" }
    $env:Evaluation__Execution = $name
    $env:Evaluation__NoCache = 'true'
    $env:Evaluation__UnloadEachQuestion = if ($UnloadEachQuestion) { 'true' } else { 'false' }
    $env:Evaluation__Sets = $Sets
    $env:Evaluation__Only = $Only
    $env:Evaluation__Graders = $Graders
    Log "Pass $i of $Passes`: $name"
    $started = Get-Date
    # The run test passes, or warns on grades that differ from v2's baseline (expected here) - a failure is a broken run.
    $exit = Invoke-Dotnet "test `"$project`" --no-build --nologo --filter FullyQualifiedName~EvaluationRunTests --logger `"console;verbosity=detailed`"" "$name.log"
    if ($exit -ne 0) { Log "Pass $i failed (exit $exit) - see $name.log"; exit 1 }
    Log "Pass $i done in $([int]((Get-Date) - $started).TotalMinutes) min"
    $executions += $name
}

$env:Evaluation__Compare = $executions -join ','
$env:Evaluation__CompareName = if ($UnloadEachQuestion) { "$Prefix-unload" } else { $Prefix }
if ((Invoke-Dotnet "test `"$project`" --no-build --nologo --filter FullyQualifiedName~VarianceComparisonTests.Compare_Executions" 'compare.log') -ne 0) { Log "Comparison failed - see compare.log"; exit 1 }
Log "Done: eval/v3-runs/$($env:Evaluation__CompareName).txt"
