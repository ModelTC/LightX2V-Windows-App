param([Parameter(Mandatory=$true)][string]$Executable,[Parameter(Mandatory=$true)][string]$Report,[int]$ExistingPid=0,[switch]$KeepOpen)
$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Path $Report -Force | Out-Null
Remove-Item Env:DOTNET_ROOT -ErrorAction SilentlyContinue
Remove-Item Env:DOTNET_ROOT_X64 -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.Drawing
Add-Type -Path "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\WPF\UIAutomationTypes.dll"
Add-Type -Path "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\WPF\UIAutomationClient.dll"
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class AppCapture {
 [StructLayout(LayoutKind.Sequential)] public struct Rect {public int Left,Top,Right,Bottom;}
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd,out Rect rect);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd,IntPtr dc,uint flags);
}
'@
$p=if($ExistingPid -gt 0){Get-Process -Id $ExistingPid}else{$start=[System.Diagnostics.ProcessStartInfo]::new($Executable);$start.UseShellExecute=$false;[System.Diagnostics.Process]::Start($start)}
try {
 $limit=(Get-Date).AddSeconds(60);$ready=$false;$names=@()
 while((Get-Date) -lt $limit){
  $p.Refresh();if($p.HasExited){throw "Packaged app exited: $($p.ExitCode)"}
  if($p.MainWindowHandle -ne 0){
   $window=[System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
   $condition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Text)
   $names=@($window.FindAll([System.Windows.Automation.TreeScope]::Descendants,$condition) | ForEach-Object {$_.Current.Name})
   $plus=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'AddImages'))
   $ready=$null -ne $plus -and $plus.Current.IsEnabled
   if($ready){break}
  }
  Start-Sleep -Milliseconds 250
 }
 if(-not $ready){throw 'Published app did not connect to the native backend.'}
 if(@($names | Where-Object {$_ -like '*Access to the path*' -or $_ -like '*环境检查失败*'}).Count -gt 0){throw 'Published app reported a settings or environment error.'}
 [AppCapture]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
 Start-Sleep -Milliseconds 500
 $rect=[AppCapture+Rect]::new();[AppCapture]::GetWindowRect($p.MainWindowHandle,[ref]$rect) | Out-Null
 $bitmap=[System.Drawing.Bitmap]::new($rect.Right-$rect.Left,$rect.Bottom-$rect.Top)
 $graphics=[System.Drawing.Graphics]::FromImage($bitmap)
 try{$dc=$graphics.GetHdc();try{[AppCapture]::PrintWindow($p.MainWindowHandle,$dc,2) | Out-Null}finally{$graphics.ReleaseHdc($dc)};$bitmap.Save((Join-Path $Report 'published-app.png'),[System.Drawing.Imaging.ImageFormat]::Png)}finally{$graphics.Dispose();$bitmap.Dispose()}
 @{ready=$ready;pid=$p.Id;executable=$Executable;noDotnetRoot=($null -eq $env:DOTNET_ROOT);uiText=$names;time=(Get-Date -Format o)} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $Report 'packaged-smoke.json') -Encoding UTF8
}finally{
 if(-not $KeepOpen -and -not $p.HasExited){$p.CloseMainWindow() | Out-Null;if(-not $p.WaitForExit(15000)){$p.Kill()}}
}
