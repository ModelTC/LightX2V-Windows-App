param([Parameter(Mandatory=$true)][string]$Destination)
$ErrorActionPreference='Stop'
$Destination=[IO.Path]::GetFullPath($Destination)
$vswhere="${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$installation=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if(-not $installation){throw 'Visual Studio C++ x64 build tools are required to compile the native launcher.'}
$tools=Join-Path $installation 'Common7\Tools\VsDevCmd.bat'
$launcher=Join-Path $PSScriptRoot '..\Launcher'
$intermediate=Join-Path $Destination 'launcher-obj'
New-Item -ItemType Directory -Force $intermediate|Out-Null
try {
    $command='call "'+$tools+'" -arch=x64 -host_arch=x64 >nul && cd /d "'+$launcher+'" && rc /nologo /fo "'+$intermediate+'\launcher.res" launcher.rc && cl /nologo /W4 /WX /std:c++17 /EHsc /MT /O2 /utf-8 launcher.cpp "'+$intermediate+'\launcher.res" /Fo"'+$intermediate+'\launcher.obj" /Fe"'+$Destination+'\LightX2V.exe" /link /SUBSYSTEM:WINDOWS /DYNAMICBASE /NXCOMPAT shell32.lib user32.lib'
    & cmd.exe /d /c $command
    if($LASTEXITCODE -ne 0){throw 'Native launcher compilation failed.'}
} finally {Remove-Item -LiteralPath $intermediate -Recurse -Force}
