$ErrorActionPreference = 'SilentlyContinue'
$conns = Get-NetTCPConnection -LocalPort 5173 -ErrorAction SilentlyContinue
if (-not $conns) {
  Write-Host 'No process listening on :5173'
  exit 0
}
$pids = $conns | Select-Object -ExpandProperty OwningProcess -Unique
foreach ($processId in $pids) {
  try {
    Stop-Process -Id $processId -Force
    Write-Host "killed PID $processId"
  } catch {
    Write-Host "could not kill PID $processId : $($_.Exception.Message)"
  }
}
