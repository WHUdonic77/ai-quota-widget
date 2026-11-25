@echo off
setlocal
cd /d "%~dp0"
set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FW%\csc.exe" set "FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
set "ICON_OPT="
if exist "app.ico" set "ICON_OPT=/win32icon:app.ico"
set "META_SRC="
if exist "installer\AssemblyInfo.cs" set "META_SRC=installer\AssemblyInfo.cs"
if exist "AssemblyInfo.cs" set "META_SRC=AssemblyInfo.cs"
"%FW%\csc.exe" /nologo /codepage:65001 /target:winexe /optimize+ %ICON_OPT% /out:AIQuotaWidget.exe ^
  /r:"%FW%\WPF\PresentationFramework.dll" /r:"%FW%\WPF\PresentationCore.dll" /r:"%FW%\WPF\WindowsBase.dll" ^
  /r:"%FW%\System.Xaml.dll" /r:"%FW%\System.Web.Extensions.dll" /r:System.Core.dll ^
  AIQuotaWidget.cs %META_SRC%
if errorlevel 1 (echo BUILD FAILED & exit /b 1)
echo BUILD OK: %~dp0AIQuotaWidget.exe
