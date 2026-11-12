param([string]$Dotnet='dotnet',[string]$Python='python',[Parameter(Mandatory=$true)][string]$Report)
$ErrorActionPreference='Stop'
if(Test-Path -LiteralPath $Report){throw 'Use a fresh regression report directory.'}
$Report=[IO.Path]::GetFullPath($Report)
New-Item -ItemType Directory $Report|Out-Null
Push-Location (Split-Path $PSScriptRoot)
try {
    & $Python prepare_setup.py
    if($LASTEXITCODE -ne 0){throw 'Setup pins failed.'}
    & $Python scripts/check_repository.py
    if($LASTEXITCODE -ne 0){throw 'Repository validation failed.'}
    & $Python -m unittest discover -s Tests -v
    if($LASTEXITCODE -ne 0){throw 'Python regressions failed.'}
    & $Dotnet publish SetupUiChecks/SetupUiChecks.csproj -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o "$Report\checks"
    if($LASTEXITCODE -ne 0){throw 'Regression build failed.'}
    $env:LIGHTX2V_QA_PYTHON=(Get-Command $Python).Source
    & "$Report\checks\SetupUiChecks.exe" "$Report\results" core
    if($LASTEXITCODE -ne 0){Get-Content "$Report\results\failure.txt" -ErrorAction SilentlyContinue;throw 'Windows regressions failed.'}
}
finally{Pop-Location}
