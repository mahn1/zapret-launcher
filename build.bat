@echo off
rem Build ZapretLauncher.exe with the csc.exe bundled in .NET Framework 4.x (no SDK needed)
setlocal
cd /d "%~dp0"
set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FW%\csc.exe" set "FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
if not exist dist mkdir dist
"%FW%\csc.exe" /nologo /target:winexe /optimize+ /utf8output /codepage:65001 ^
  /out:dist\ZapretLauncher.exe /win32manifest:src\app.manifest /win32icon:src\app.ico ^
  /resource:src\MainWindow.xaml,ZapretLauncher.MainWindow.xaml ^
  /r:"%FW%\WPF\PresentationFramework.dll" /r:"%FW%\WPF\PresentationCore.dll" /r:"%FW%\WPF\WindowsBase.dll" ^
  /r:System.Xaml.dll /r:System.ServiceProcess.dll /r:System.Web.Extensions.dll ^
  /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  src\*.cs
if errorlevel 1 exit /b 1
echo OK: dist\ZapretLauncher.exe
