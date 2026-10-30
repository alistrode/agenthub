$ErrorActionPreference = 'Stop'
$Product = 'AgentHub'
$Port = 5178
$BaseUrl = 'http://127.0.0.1:' + $Port + '/'
$health = $null
try { $health = Invoke-RestMethod -Uri ($BaseUrl + 'api/health') -TimeoutSec 2 } catch { }
if ($health.product -ne $Product) {
  $exe = Join-Path $PSScriptRoot (Join-Path 'publish' ($Product + '.exe'))
  if (-not (Test-Path -LiteralPath $exe)) { throw ($Product + ' executable not found.') }
  $logDir = Join-Path (Join-Path $env:LOCALAPPDATA $Product) 'logs'
  New-Item -ItemType Directory -Force -Path $logDir | Out-Null
  $process = Start-Process -FilePath $exe -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $logDir 'server.log') -RedirectStandardError (Join-Path $logDir 'error.log')
  for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 250
    try { $health = Invoke-RestMethod -Uri ($BaseUrl + 'api/health') -TimeoutSec 2; if ($health.product -eq $Product) { break } } catch { }
    if ($process.HasExited) { throw ($Product + ' failed to start. See ' + $logDir) }
  }
}
if ($health.product -ne $Product) { throw ($Product + ' did not start on port ' + $Port) }
Start-Process -FilePath $BaseUrl

