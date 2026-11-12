param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [Parameter(Mandatory = $true)][string]$Report
)
$ErrorActionPreference = 'Stop'
if ((Test-Path -LiteralPath $Report) -and @(Get-ChildItem -LiteralPath $Report -Force).Count -gt 0) {
    throw 'Use a new or empty report directory.'
}
New-Item -ItemType Directory -Path $Report -Force | Out-Null
Add-Type -Path "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\WPF\UIAutomationTypes.dll"
Add-Type -Path "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\WPF\UIAutomationClient.dll"

$start = [System.Diagnostics.ProcessStartInfo]::new((Get-Item -LiteralPath $Executable).FullName)
$start.UseShellExecute = $false
$start.EnvironmentVariables.Remove('DOTNET_ROOT')
$start.EnvironmentVariables.Remove('DOTNET_ROOT_X64')
$launched = [System.Diagnostics.Process]::Start($start)
$process = $launched
$child = Join-Path (Split-Path $start.FileName) 'app\LightX2V.Windows.exe'
$usesLauncher = (Split-Path $start.FileName -Leaf) -eq 'LightX2V.exe'
if ($usesLauncher) { $process = $null }
try {
    $limit = (Get-Date).AddSeconds(60)
    $mode = ''
    while ((Get-Date) -lt $limit) {
        if ($usesLauncher -and $null -eq $process) {
            $candidate = Get-CimInstance Win32_Process -Filter "Name='LightX2V.Windows.exe'" | Where-Object { $_.ExecutablePath -eq $child -and $_.CreationDate -ge $launched.StartTime.AddSeconds(-1) } | Select-Object -First 1
            if ($null -ne $candidate) { $process = Get-Process -Id $candidate.ProcessId }
            else { Start-Sleep -Milliseconds 250; continue }
        }
        $process.Refresh()
        if ($process.HasExited) { throw "Packaged app exited during startup with code $($process.ExitCode)." }
        if ($process.MainWindowHandle -ne 0) {
            $window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
            $scope = [System.Windows.Automation.TreeScope]::Descendants
            $id = [System.Windows.Automation.AutomationElement]::AutomationIdProperty
            $setup = $window.FindFirst($scope, [System.Windows.Automation.PropertyCondition]::new($id, 'SetupContinue'))
            $workspace = $window.FindFirst($scope, [System.Windows.Automation.PropertyCondition]::new($id, 'AddImages'))
            if ($null -ne $setup) { $mode = 'setup'; break }
            if ($null -ne $workspace -and $workspace.Current.IsEnabled) { $mode = 'workspace'; break }
        }
        Start-Sleep -Milliseconds 250
    }
    if ($mode -eq '') { throw 'Packaged app did not open a usable setup or workspace window.' }
    @{
        uiReady = $true
        mode = $mode
        backendValidated = $false
        executable = $start.FileName
        pid = $process.Id
        checkedAt = (Get-Date -Format o)
    } | ConvertTo-Json | Set-Content (Join-Path $Report 'package-startup.json') -Encoding UTF8
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        $process.CloseMainWindow() | Out-Null
        if (-not $process.WaitForExit(15000)) { $process.Kill() }
    }
    if ($null -ne $process) { $process.Dispose() }
    if ($usesLauncher) { $launched.Dispose() }
}
