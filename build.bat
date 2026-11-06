@echo off
rem Compila o VoltCore Optimizer usando o compilador C# que ja vem no Windows (.NET Framework 4.x).
rem Nao precisa instalar Visual Studio nem .NET SDK.
setlocal
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set WPF=%FW%\WPF
if not exist "%FW%\csc.exe" (
  echo [ERRO] csc.exe nao encontrado em %FW%
  exit /b 1
)
if not exist dist mkdir dist
"%FW%\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 ^
  /out:dist\VoltCore.exe ^
  /win32manifest:src\app.manifest ^
  /win32icon:assets\icon.ico ^
  /resource:src\MainWindow.xaml,MainWindow.xaml ^
  /r:"%WPF%\PresentationFramework.dll" /r:"%WPF%\PresentationCore.dll" /r:"%WPF%\WindowsBase.dll" ^
  /r:System.Xaml.dll /r:System.Management.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll ^
  src\*.cs
if errorlevel 1 (
  echo.
  echo [ERRO] Falha na compilacao.
  exit /b 1
)
echo.
echo OK: dist\VoltCore.exe
