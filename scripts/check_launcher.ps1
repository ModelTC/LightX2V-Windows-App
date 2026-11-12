param([Parameter(Mandatory=$true)][string]$Executable,[Parameter(Mandatory=$true)][string]$Report)
$ErrorActionPreference='Stop'
if(Test-Path $Report){throw 'Use an unused report directory.'}
New-Item -ItemType Directory $Report|Out-Null
$case=Join-Path $Report '中文 空格';New-Item -ItemType Directory (Join-Path $case 'app') -Force|Out-Null
Copy-Item $Executable (Join-Path $case 'LightX2V.exe')
$vswhere="${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$installation=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if(-not $installation){throw 'C++ build tools are required for the independent argument probe.'}
$source=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Tests\launcher_probe.cpp'))
$probe=Join-Path $case 'app\LightX2V.Windows.exe'
$command='call "'+$installation+'\Common7\Tools\VsDevCmd.bat" -arch=x64 -host_arch=x64 >nul && cl /nologo /W4 /WX /std:c++17 /EHsc /MT /O2 "'+$source+'" /Fo"'+$Report+'\probe.obj" /Fe"'+$probe+'" /link /SUBSYSTEM:WINDOWS shell32.lib'
& cmd.exe /d /c $command
if($LASTEXITCODE -ne 0){throw 'Argument probe compilation failed.'}
$actual=Join-Path $Report 'arguments.bin'
$start=[Diagnostics.ProcessStartInfo]::new((Join-Path $case 'LightX2V.exe'))
$start.UseShellExecute=$false;$start.WorkingDirectory=$env:TEMP;$start.EnvironmentVariables['LIGHTX2V_LAUNCHER_PROBE']=$actual
# This literal command line is an independent test vector for quotes, empty args and trailing slashes.
$start.Arguments='"中文 路径" "a\"b" "" "C:\folder with spaces\\"'
$p=[Diagnostics.Process]::Start($start)
if(-not $p.WaitForExit(10000)){throw 'Launcher stayed resident.'}
if($p.ExitCode -ne 0){throw 'Launcher failed.'}
$limit=(Get-Date).AddSeconds(10)
while(-not (Test-Path $actual) -and (Get-Date) -lt $limit){Start-Sleep -Milliseconds 100}
$value=[Text.Encoding]::Unicode.GetString([IO.File]::ReadAllBytes($actual)).Split([char]0)
$expected=@($probe,'中文 路径','a"b','','C:\folder with spaces\','')
if(($value|ConvertTo-Json -Compress) -cne ($expected|ConvertTo-Json -Compress)){throw 'Launcher changed child executable or argument values.'}
@{passed=$true;unicode=$true;spaces=$true;emptyArgument=$true;embeddedQuote=$true;trailingSlash=$true;launcherExited=$true}|ConvertTo-Json|Set-Content (Join-Path $Report 'launcher.json') -Encoding UTF8
