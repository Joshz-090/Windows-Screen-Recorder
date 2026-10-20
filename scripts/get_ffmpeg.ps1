$binDir = "d:\projects\Bili\coding\Windows Screen Recorder\engine\bin"
if (-not (Test-Path $binDir)) {
    New-Item -ItemType Directory -Force -Path $binDir | Out-Null
}

$zipPath = Join-Path $binDir "ffmpeg.zip"
$ffmpegExe = Join-Path $binDir "ffmpeg.exe"

if (-not (Test-Path $ffmpegExe)) {
    Write-Host "Downloading ffmpeg release essentials..."
    $url = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"
    Invoke-WebRequest -Uri $url -OutFile $zipPath
    Write-Host "Extracting archive..."
    Expand-Archive -Path $zipPath -DestinationPath $binDir -Force
    $found = Get-ChildItem -Path $binDir -Recurse -Filter "ffmpeg.exe" | Where-Object { $_.FullName -ne $ffmpegExe } | Select-Object -First 1
    if ($found) {
        Copy-Item -Path $found.FullName -Destination $ffmpegExe -Force
        Write-Host "Copied ffmpeg.exe to $ffmpegExe"
    }
    Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
}

Write-Host "FFmpeg check:"
& $ffmpegExe -version | Select-Object -First 2
