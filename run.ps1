# Compile AIQuotaWidget.cs in memory and run it (no unsigned exe needed)
param([string[]]$WidgetArgs = @())
$dir = $PSScriptRoot
$fw  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path $fw)) { $fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319' }
$refs = @(
    "$fw\WPF\PresentationFramework.dll", "$fw\WPF\PresentationCore.dll", "$fw\WPF\WindowsBase.dll",
    "$fw\System.Xaml.dll", "$fw\System.Web.Extensions.dll", 'System.Core.dll', 'System.dll'
)
$src = [IO.File]::ReadAllText((Join-Path $dir 'AIQuotaWidget.cs'), [Text.Encoding]::UTF8)
Add-Type -TypeDefinition $src -ReferencedAssemblies $refs -IgnoreWarnings -WarningAction SilentlyContinue
$vbs = Join-Path $dir 'start-widget.vbs'
$launch = '"' + (Join-Path $env:WINDIR 'System32\wscript.exe') + '" "' + $vbs + '"'
[void][AIQuota.Program]::Main([string[]](@('--dir', $dir, '--launch', $launch) + $WidgetArgs))
