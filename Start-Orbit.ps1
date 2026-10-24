$ErrorActionPreference = 'Stop'

# One command for the normal desktop workflow: restore, build, test, publish, and run.
$publishScript = Join-Path $PSScriptRoot 'Publish-Orbit.ps1'
$executable = Join-Path $PSScriptRoot 'artifacts/Orbit/DevOpsClient.exe'

if (-not (Test-Path -LiteralPath $publishScript)) {
    throw "Publish script not found: $publishScript"
}

& $publishScript
if ($LASTEXITCODE -ne 0) {
    throw 'Orbit publish failed.'
}

if (-not (Test-Path -LiteralPath $executable)) {
    throw "Published executable not found: $executable"
}

Write-Host "Starting $executable"
$process = Start-Process -FilePath $executable -WorkingDirectory (Split-Path -Parent $executable) -PassThru -Wait
if ($process.ExitCode -ne 0) {
    throw "Orbit exited with code $($process.ExitCode)."
}
