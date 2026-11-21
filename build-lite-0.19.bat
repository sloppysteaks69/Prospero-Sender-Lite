@echo off
rem Builds prospero-sender-lite-0.19.exe with the C# compiler that ships in Windows
rem (no Visual Studio or .NET SDK needed), then runs its self-test. Double-click it.
setlocal
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "OUT=prospero-sender-lite-0.19.exe"
set "LOG=build-log-0.19.txt"
set "TEST=selftest-0.19.txt"
if not exist "%CSC%" (
  echo The Windows C# compiler was not found at %CSC% > "%LOG%"
  type "%LOG%"
  pause
  exit /b 1
)
if exist "%OUT%" del "%OUT%"
if exist "%TEST%" del "%TEST%"
echo Building %OUT% ...
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /warn:3 /out:"%OUT%" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:Microsoft.VisualBasic.dll /resource:"mux_receiver-85.elf",mux_receiver-85.elf /resource:"slipstream.pkg",slipstream.pkg src\*.cs > "%LOG%" 2>&1
if errorlevel 1 (
  echo BUILD FAILED >> "%LOG%"
  type "%LOG%"
  echo.
  echo The errors above are saved in %~dp0%LOG%
  pause
  exit /b 1
)
echo BUILD OK >> "%LOG%"
echo Built. Running the self-test (about 10 seconds) ...
start /wait "" "%OUT%" --selftest "%~dp0%TEST%"
type "%TEST%"
echo.
echo Start the app with %~dp0%OUT%
pause
