$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$url = 'https://desktop.docker.com/win/main/amd64/Docker%20Desktop%20Installer.exe'
$out = Join-Path $env:USERPROFILE 'Downloads\Docker Desktop Installer.exe'

Write-Host "Downloading Docker Desktop Installer..."
Write-Host "URL:  $url"
Write-Host "To:   $out"
Write-Host ""

Invoke-WebRequest -Uri $url -OutFile $out

$file = Get-Item $out
Write-Host ""
Write-Host "Done."
Write-Host ("File:  {0}" -f $file.Name)
Write-Host ("Size:  {0:N2} MB" -f ($file.Length / 1MB))
Write-Host ("Path:  {0}" -f $file.FullName)
Write-Host ""
Write-Host "Run it by double-click (admin rights required)."
