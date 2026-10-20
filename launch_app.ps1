$dotnetDir = "$env:LOCALAPPDATA\Microsoft\dotnet"
$env:DOTNET_ROOT = $dotnetDir
$env:Path = "$dotnetDir;$env:Path"

Set-Location "$PSScriptRoot\src\ScreenRecorder.App"

Write-Host "Launching Windows Screen Recorder Studio..." -ForegroundColor Cyan
& "$dotnetDir\dotnet.exe" run -c Release
