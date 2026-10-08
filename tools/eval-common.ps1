<#
.SYNOPSIS
    Helpers shared by run-variance.ps1 and run-judge.ps1 - dot-source it: . "$PSScriptRoot\eval-common.ps1"
#>

$script:Repo = Split-Path $PSScriptRoot -Parent
$script:EvalProject = Join-Path $script:Repo 'RagFilingExplorer.Evaluation'
$script:Logs = Join-Path $script:Repo 'eval\v3-runs\logs'
New-Item -ItemType Directory -Force $script:Logs | Out-Null

# The three question sets, named explicitly: an empty override can't mean "all" (see Set-EvalOverride).
$script:AllSets = 'Main,HeldOut,AnswerSide'

# dotnet through cmd: Windows PowerShell 5.1's *> writes UTF-16 and turns stderr lines into errors; cmd's redirect keeps
# the bytes, and the exit code alone decides.
function Invoke-Dotnet([string]$arguments, [string]$logName) {
    cmd /c "dotnet $arguments > `"$(Join-Path $script:Logs $logName)`" 2>&1"
    return $LASTEXITCODE
}

function Write-RunLog([string]$runLog, [string]$message) {
    $line = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  $message"
    Add-Content -Path $runLog -Value $line -Encoding utf8
    Write-Host $line
}

# evalsettings.json holds the run's defaults; Evaluation__<key> variables override a key for this run. Start clean:
# the two scripts can run in one process, and one's overrides mustn't reach the other.
function Clear-EvalOverrides {
    Get-ChildItem Env: | Where-Object { $_.Name -like 'Evaluation__*' } | ForEach-Object { Remove-Item "Env:$($_.Name)" }
}

# Windows PowerShell deletes a variable assigned '' - so an empty value can't override evalsettings.json's, and the file's
# value would be used silently. Empty is refused here; callers pass an explicit value instead.
function Set-EvalOverride([string]$key, [string]$value) {
    if ([string]::IsNullOrEmpty($value)) { throw "Evaluation__$key can't be overridden with an empty value in Windows PowerShell." }
    Set-Item "Env:Evaluation__$key" $value
}

# Only has no explicit "every question" value, so a script that asks every question needs the file's Only to be empty.
function Assert-NoOnlyInSettings {
    $settings = Get-Content (Join-Path $script:EvalProject 'evalsettings.json') -Raw | ConvertFrom-Json
    if ($settings.Evaluation.Only) {
        throw "evalsettings.json sets Only to '$($settings.Evaluation.Only)' - empty it, or pass -Only, so this run asks what it says."
    }
}
