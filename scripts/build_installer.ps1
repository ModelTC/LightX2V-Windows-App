param([Parameter(Mandatory)][string]$Archive)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$output = Join-Path (Split-Path $Archive) 'LightX2V-Setup.exe'
& "$framework\csc.exe" /nologo /codepage:65001 /target:winexe /platform:x64 /optimize+ "/out:$output" `
    "/win32icon:$root\LightX2V.Windows\Resources\LightX2V.ico" `
    "/resource:$Archive,package.zip" "/resource:$root\Installer\Setup.xaml,Setup.xaml" `
    "/resource:$root\docs\assets\AppIcon.png,AppIcon.png" `
    "/reference:$framework\WPF\PresentationFramework.dll" "/reference:$framework\WPF\PresentationCore.dll" `
    "/reference:$framework\WPF\WindowsBase.dll" /reference:System.Xaml.dll /reference:System.IO.Compression.dll `
    /reference:System.Windows.Forms.dll "$root\Installer\Setup.cs"
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$binary = [Reflection.Assembly]::LoadFile($output)
$stream = $binary.GetManifestResourceStream('package.zip')
try {
    $hash = (Get-FileHash -InputStream $stream -Algorithm SHA256).Hash
    if ($hash -ne (Get-FileHash -LiteralPath $Archive).Hash) { throw 'Installer payload differs from verified archive.' }
} finally { $stream.Dispose() }
