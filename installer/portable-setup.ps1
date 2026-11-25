param(
    [switch]$Uninstall,
    [switch]$Silent
)
$ErrorActionPreference = 'Stop'

$AppId = 'AIQuotaWidget'
$DisplayName = 'AI 额度桌面小组件'
$ShortcutFile = 'AI 额度小组件.lnk'
$RunReg = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$UninstReg = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AIQuotaWidget'
$InstallDir = Join-Path $env:LOCALAPPDATA $AppId
$SrcDir = $PSScriptRoot

function Stop-RunningWidget {
    Get-Process -Name 'AIQuotaWidget' -ErrorAction SilentlyContinue | ForEach-Object {
        try {
            $null = $_.CloseMainWindow()
            if (-not $_.WaitForExit(600)) { $_.Kill() }
        } catch {}
    }
}

if ($Uninstall) {
    Write-Host "==> 正在关闭运行中的 AI 额度桌面小组件..." -ForegroundColor Cyan
    Stop-RunningWidget
    Start-Sleep -Milliseconds 300

    Remove-ItemProperty -Path $RunReg -Name $AppId -ErrorAction SilentlyContinue
    if (Test-Path $UninstReg) { Remove-Item -Path $UninstReg -Recurse -Force -ErrorAction SilentlyContinue }

    $deskLnk = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) $ShortcutFile
    $startLnk = Join-Path ([Environment]::GetFolderPath('Programs')) $ShortcutFile
    if (Test-Path $deskLnk) { Remove-Item $deskLnk -Force -ErrorAction SilentlyContinue }
    if (Test-Path $startLnk) { Remove-Item $startLnk -Force -ErrorAction SilentlyContinue }

    if (Test-Path $InstallDir) {
        Remove-Item -Path $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    Write-Host "✓ 已成功卸载 AI 额度桌面小组件！" -ForegroundColor Green
    if (-not $Silent) { Start-Sleep -Seconds 2 }
    exit 0
}

Write-Host "==> 正在安装 AI 额度桌面小组件 到: $InstallDir" -ForegroundColor Cyan
Stop-RunningWidget
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

# 复制程序源码与资源（保留已有 config.json / antigravity_quota.json）
$copyFiles = @('AIQuotaWidget.cs', 'AssemblyInfo.cs', 'app.ico', 'build.cmd', 'run.ps1', 'start-widget.vbs', 'install.cmd', 'uninstall.cmd', 'portable-setup.ps1', 'LICENSE')
foreach ($f in $copyFiles) {
    $sp = Join-Path $SrcDir $f
    if (Test-Path $sp) {
        Copy-Item $sp (Join-Path $InstallDir $f) -Force
    }
}

$targetCfg = Join-Path $InstallDir 'config.json'
$srcCfg = Join-Path $SrcDir 'config.json'
if ((-not (Test-Path $targetCfg)) -and (Test-Path $srcCfg)) {
    Copy-Item $srcCfg $targetCfg -Force
}

# 使用目标电脑系统自带 .NET Framework 4.x 本地编译（消除外来 exe 的 SmartScreen 拦截）
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path (Join-Path $fw 'csc.exe'))) {
    $fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$csc = Join-Path $fw 'csc.exe'
$targetExe = Join-Path $InstallDir 'AIQuotaWidget.exe'

if (Test-Path $csc) {
    Write-Host "==> 正在使用本机 .NET Framework 编译原生可执行文件..." -ForegroundColor Cyan
    Push-Location $InstallDir
    try {
        $cscArgs = @(
            '/nologo', '/codepage:65001', '/target:winexe', '/optimize+',
            '/out:AIQuotaWidget.exe',
            "/r:$fw\WPF\PresentationFramework.dll",
            "/r:$fw\WPF\PresentationCore.dll",
            "/r:$fw\WPF\WindowsBase.dll",
            "/r:$fw\System.Xaml.dll",
            "/r:$fw\System.Web.Extensions.dll",
            '/r:System.Core.dll'
        )
        if (Test-Path 'app.ico') { $cscArgs += '/win32icon:app.ico' }
        $cscArgs += 'AIQuotaWidget.cs'
        if (Test-Path 'AssemblyInfo.cs') { $cscArgs += 'AssemblyInfo.cs' }
        & $csc @cscArgs
    } finally {
        Pop-Location
    }
}

if ((-not (Test-Path $targetExe)) -and (Test-Path (Join-Path $SrcDir 'AIQuotaWidget.exe'))) {
    Copy-Item (Join-Path $SrcDir 'AIQuotaWidget.exe') $targetExe -Force
}

# 创建桌面与开始菜单快捷方式
$wsh = New-Object -ComObject WScript.Shell
foreach ($lnkDir in @([Environment]::GetFolderPath('DesktopDirectory'), [Environment]::GetFolderPath('Programs'))) {
    $lnk = $wsh.CreateShortcut((Join-Path $lnkDir $ShortcutFile))
    $lnk.TargetPath = $targetExe
    $lnk.WorkingDirectory = $InstallDir
    if (Test-Path (Join-Path $InstallDir 'app.ico')) {
        $lnk.IconLocation = "$(Join-Path $InstallDir 'app.ico'),0"
    }
    $lnk.Description = 'AI 额度桌面小组件 (Codex + Antigravity)'
    $lnk.Save()
}

# 注册开机自启与系统卸载项
New-Item -Path $RunReg -Force | Out-Null
Set-ItemProperty -Path $RunReg -Name $AppId -Value "`"$targetExe`""

New-Item -Path $UninstReg -Force | Out-Null
Set-ItemProperty -Path $UninstReg -Name 'DisplayName' -Value $DisplayName
Set-ItemProperty -Path $UninstReg -Name 'DisplayVersion' -Value '1.0.0'
Set-ItemProperty -Path $UninstReg -Name 'Publisher' -Value 'AIQuota'
Set-ItemProperty -Path $UninstReg -Name 'DisplayIcon' -Value (Join-Path $InstallDir 'app.ico')
Set-ItemProperty -Path $UninstReg -Name 'InstallLocation' -Value $InstallDir
Set-ItemProperty -Path $UninstReg -Name 'UninstallString' -Value "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$InstallDir\portable-setup.ps1`" -Uninstall"
Set-ItemProperty -Path $UninstReg -Name 'NoModify' -Value 1 -Type DWord
Set-ItemProperty -Path $UninstReg -Name 'NoRepair' -Value 1 -Type DWord

if (-not $Silent) {
    Start-Process -FilePath $targetExe -WorkingDirectory $InstallDir
}
Write-Host "✓ 安装完成！小组件已在屏幕顶部启动。" -ForegroundColor Green
if (-not $Silent) { Start-Sleep -Seconds 2 }
