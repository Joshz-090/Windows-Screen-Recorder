@echo off
set "DOTNET_ROOT=%LOCALAPPDATA%\Microsoft\dotnet"
set "PATH=%DOTNET_ROOT%;%PATH%"

cd /d "%~dp0src\ScreenRecorder.App"

echo Starting Windows Screen Recorder Studio...
"%DOTNET_ROOT%\dotnet.exe" run -c Release
pause
