param([string]$Dotnet = 'dotnet', [string]$Destination = '', [string]$Python = 'python')
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    & $Python -X utf8 "$PSScriptRoot\prepare_setup.py"
    if ($LASTEXITCODE -ne 0) { throw 'Pinned setup payload verification failed.' }
    & $Python -X utf8 "$PSScriptRoot\scripts\check_repository.py"
    if ($LASTEXITCODE -ne 0) { throw 'Repository validation failed.' }
    & $Python -X utf8 "$PSScriptRoot\prepare_runtime.py"
    if ($LASTEXITCODE -ne 0) { throw 'Prompt enhancement runtime preparation failed.' }

    $version = ([xml](Get-Content "$PSScriptRoot\LightX2V.Windows\LightX2V.Windows.csproj" -Raw)).SelectSingleNode('/Project/PropertyGroup/Version').InnerText
    if ([string]::IsNullOrWhiteSpace($Destination)) { $Destination = "$PSScriptRoot\dist\LightX2V-Windows-$version" }
    $Destination = [IO.Path]::GetFullPath($Destination)
    if ((Test-Path -LiteralPath $Destination) -and @(Get-ChildItem -LiteralPath $Destination -Force).Count -gt 0) {
        throw 'Use a new or empty output directory to avoid packaging stale files.'
    }
    & $Dotnet publish "$PSScriptRoot\LightX2V.Windows\LightX2V.Windows.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o $Destination
    if ($LASTEXITCODE -ne 0) { throw 'Windows app publish failed.' }

    $dotnetDirectory = Split-Path (Get-Command $Dotnet).Source
    foreach ($name in @('LICENSE.txt', 'ThirdPartyNotices.txt')) {
        $licensePath = Join-Path $dotnetDirectory $name
        if (-not (Test-Path -LiteralPath $licensePath)) { throw "Missing .NET license: $licensePath" }
        Copy-Item -LiteralPath $licensePath -Destination (Join-Path $Destination $name)
    }
    & $Python -X utf8 "$PSScriptRoot\scripts\package_app.py" --app $Destination
    if ($LASTEXITCODE -ne 0) { throw 'Portable package verification failed.' }
}
finally { Pop-Location }
