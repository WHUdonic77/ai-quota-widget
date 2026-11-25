# Build AIQuotaWidget redistributable installer & portable archive
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$instSrc = Join-Path $root 'installer'
$distDir = Join-Path $root 'dist'
$stageRoot = Join-Path $env:TEMP ("ai-quota-pack-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))

$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path (Join-Path $fw 'csc.exe'))) {
    $fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$csc = Join-Path $fw 'csc.exe'
if (-not (Test-Path $csc)) {
    throw "Cannot find .NET Framework 4.x csc.exe at $csc"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$utf8Bom = New-Object System.Text.UTF8Encoding($true)
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

try {
    New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $distDir -Force | Out-Null

    # 1. Generate multi-resolution app.ico (reuse existing app.ico if already present or IconGen is blocked)
    Write-Host "[1/4] Preparing app.ico..." -ForegroundColor Cyan
    $icoPath = Join-Path $root 'app.ico'
    if (-not (Test-Path $icoPath)) {
        $iconGenExe = Join-Path $stageRoot 'IconGen.exe'
        & $csc /nologo /optimize+ "/r:$fw\System.Drawing.dll" "/out:$iconGenExe" (Join-Path $instSrc 'IconGen.cs')
        if ($LASTEXITCODE -ne 0) { throw "IconGen compilation failed" }
        & $iconGenExe $icoPath
        if ($LASTEXITCODE -ne 0) { throw "IconGen execution failed" }
    }

    # 2. Compile main AIQuotaWidget.exe (into staging first, and copy to root if unlocked)
    Write-Host "[2/4] Compiling AIQuotaWidget.exe..." -ForegroundColor Cyan
    $builtExe = Join-Path $stageRoot 'AIQuotaWidget.exe'
    & $csc /nologo /codepage:65001 /target:winexe /optimize+ `
        "/win32icon:$icoPath" `
        "/out:$builtExe" `
        "/r:$fw\WPF\PresentationFramework.dll" `
        "/r:$fw\WPF\PresentationCore.dll" `
        "/r:$fw\WPF\WindowsBase.dll" `
        "/r:$fw\System.Xaml.dll" `
        "/r:$fw\System.Web.Extensions.dll" `
        /r:System.Core.dll `
        (Join-Path $root 'AIQuotaWidget.cs') `
        (Join-Path $instSrc 'AssemblyInfo.cs')
    if ($LASTEXITCODE -ne 0) { throw "AIQuotaWidget.exe compilation failed" }

    try { Copy-Item $builtExe (Join-Path $root 'AIQuotaWidget.exe') -Force } catch {}

    # Prepare clean machine-independent default config.json
    $cleanCfgJson = @"
{
  "refreshSeconds": 60,
  "topmost": true,
  "autoCollapse": true,
  "showCapsLock": true,
  "showCodex": true,
  "showAntigravity": true,
  "codexHome": "",
  "agBrainDir": "",
  "prices": {
    "default": [
      1.25,
      0.125,
      10
    ],
    "gpt-5-mini": [
      0.25,
      0.025,
      2
    ]
  },
  "providers": []
}
"@

    # 3. Build single-file GUI installer: dist\AIQuotaWidget-Setup.exe
    Write-Host "[3/4] Building single-file GUI installer AIQuotaWidget-Setup.exe..." -ForegroundColor Cyan
    $payloadDir = Join-Path $stageRoot 'payload'
    New-Item -ItemType Directory -Path $payloadDir -Force | Out-Null
    Copy-Item $builtExe (Join-Path $payloadDir 'AIQuotaWidget.exe') -Force
    Copy-Item (Join-Path $root 'AIQuotaWidget.cs') (Join-Path $payloadDir 'AIQuotaWidget.cs') -Force
    Copy-Item (Join-Path $instSrc 'AssemblyInfo.cs') (Join-Path $payloadDir 'AssemblyInfo.cs') -Force
    Copy-Item $icoPath (Join-Path $payloadDir 'app.ico') -Force
    Copy-Item (Join-Path $root 'build.cmd') (Join-Path $payloadDir 'build.cmd') -Force
    Copy-Item (Join-Path $root 'run.ps1') (Join-Path $payloadDir 'run.ps1') -Force
    Copy-Item (Join-Path $root 'start-widget.vbs') (Join-Path $payloadDir 'start-widget.vbs') -Force
    Copy-Item (Join-Path $root 'LICENSE') (Join-Path $payloadDir 'LICENSE') -Force
    [IO.File]::WriteAllText((Join-Path $payloadDir 'config.default.json'), $cleanCfgJson, $utf8NoBom)

    $payloadZip = Join-Path $stageRoot 'payload.zip'
    [System.IO.Compression.ZipFile]::CreateFromDirectory($payloadDir, $payloadZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

    $setupExe = Join-Path $distDir 'AIQuotaWidget-Setup.exe'
    & $csc /nologo /codepage:65001 /target:winexe /optimize+ `
        "/win32icon:$icoPath" `
        "/resource:$payloadZip,AIQuota.Setup.payload.zip" `
        "/out:$setupExe" `
        "/r:$fw\WPF\PresentationFramework.dll" `
        "/r:$fw\WPF\PresentationCore.dll" `
        "/r:$fw\WPF\WindowsBase.dll" `
        "/r:$fw\System.Xaml.dll" `
        "/r:$fw\System.IO.Compression.dll" `
        "/r:$fw\System.IO.Compression.FileSystem.dll" `
        "/r:$fw\System.Windows.Forms.dll" `
        /r:System.Core.dll `
        (Join-Path $instSrc 'Setup.cs')
    if ($LASTEXITCODE -ne 0) { throw "AIQuotaWidget-Setup.exe compilation failed" }

    # 4. Build green portable & script-installable zip: dist\AIQuotaWidget-Portable.zip
    Write-Host "[4/4] Building portable package AIQuotaWidget-Portable.zip..." -ForegroundColor Cyan
    $portStage = Join-Path $stageRoot 'portable_root'
    $portAppDir = Join-Path $portStage 'AIQuotaWidget'
    New-Item -ItemType Directory -Path $portAppDir -Force | Out-Null

    Copy-Item $builtExe (Join-Path $portAppDir 'AIQuotaWidget.exe') -Force
    Copy-Item (Join-Path $root 'AIQuotaWidget.cs') (Join-Path $portAppDir 'AIQuotaWidget.cs') -Force
    Copy-Item (Join-Path $instSrc 'AssemblyInfo.cs') (Join-Path $portAppDir 'AssemblyInfo.cs') -Force
    Copy-Item $icoPath (Join-Path $portAppDir 'app.ico') -Force
    Copy-Item (Join-Path $root 'build.cmd') (Join-Path $portAppDir 'build.cmd') -Force
    Copy-Item (Join-Path $root 'run.ps1') (Join-Path $portAppDir 'run.ps1') -Force
    Copy-Item (Join-Path $root 'start-widget.vbs') (Join-Path $portAppDir 'start-widget.vbs') -Force
    Copy-Item (Join-Path $root 'LICENSE') (Join-Path $portAppDir 'LICENSE') -Force
    [IO.File]::WriteAllText((Join-Path $portAppDir 'config.json'), $cleanCfgJson, $utf8NoBom)

    # Re-write portable-setup.ps1 with UTF-8 BOM so PowerShell 5.1 parses Chinese strings on any locale
    $psContent = [IO.File]::ReadAllText((Join-Path $instSrc 'portable-setup.ps1'), [Text.Encoding]::UTF8)
    [IO.File]::WriteAllText((Join-Path $portAppDir 'portable-setup.ps1'), $psContent, $utf8Bom)

    $installCmd = "@echo off`r`npowershell.exe -NoProfile -ExecutionPolicy Bypass -File `"%~dp0portable-setup.ps1`"`r`n"
    $uninstallCmd = "@echo off`r`npowershell.exe -NoProfile -ExecutionPolicy Bypass -File `"%~dp0portable-setup.ps1`" -Uninstall`r`n"
    [IO.File]::WriteAllText((Join-Path $portAppDir 'install.cmd'), $installCmd, [Text.Encoding]::ASCII)
    [IO.File]::WriteAllText((Join-Path $portAppDir 'uninstall.cmd'), $uninstallCmd, [Text.Encoding]::ASCII)

    $readme = @"
AI 额度桌面小组件 (Codex + Antigravity + 可扩展其他 AI 应用) —— 便携/安装包说明
==============================================================================

运行环境：Windows 10 / Windows 11（基于系统内置 .NET Framework 4.x，无需安装任何第三方依赖，无需管理员权限）

【方式一：免安装绿色运行】
直接双击运行本目录下的 AIQuotaWidget.exe 即可在屏幕顶部显示悬浮条。
（若部分电脑开启了严格 SmartScreen 拦截外来 exe，可双击 install.cmd 调用本机内置编译器原地生成并安装，零拦截。）

【方式二：一键安装到本机系统】
双击运行 install.cmd：
  1. 自动部署至 %LocalAppData%\AIQuotaWidget 并用本机 .NET 原地编译
  2. 自动创建桌面与开始菜单快捷方式
  3. 自动开启开机自启，并写入系统「设置 -> 应用 -> 已安装的应用」
  4. 如需卸载，双击 uninstall.cmd 或前往 Windows「设置 -> 应用」卸载即可。

【常用操作】
  - 左键点击悬浮条图标：展开 / 收起对应 AI 应用的实时用量与额度面板
  - 按住黑色悬浮条拖动：自由调整屏幕位置（自动记忆）
  - 右键点击悬浮条：
      · 管理 AI 应用（添加其他应用额度）…：添加 Claude Code / Gemini CLI（本机日志）、DeepSeek / OpenRouter / Kimi / 硅基流动 / 自定义余额 API、或任意应用手动额度记录；支持勾选显示/隐藏与调整顺序
      · 切换开机自启、置顶、大小写指示器、校准额度或退出

【可选：迁移旧电脑的个人配置与校准数据】
安装包默认使用干净的自适应配置（在新显示器上自动顶部居中）。
如需迁移旧电脑上的额度校准记录与自定义应用列表，将旧电脑目录中的 config.json 与 antigravity_quota.json 覆盖到新电脑安装目录即可。
"@
    [IO.File]::WriteAllText((Join-Path $portAppDir 'README.txt'), $readme, $utf8Bom)

    $portableZip = Join-Path $distDir 'AIQuotaWidget-Portable.zip'
    if (Test-Path $portableZip) { Remove-Item $portableZip -Force }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($portStage, $portableZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

    Write-Host ""
    Write-Host "Build Succeeded!" -ForegroundColor Green
    Get-ChildItem $distDir | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize
} finally {
    if (Test-Path $stageRoot) {
        Remove-Item $stageRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
