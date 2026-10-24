$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet restore DevOpsClient.sln --configfile NuGet.Config
    if ($LASTEXITCODE -ne 0) { throw 'Package restore failed.' }
    dotnet build DevOpsClient.sln -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    dotnet test DevOpsClient.sln -c Release --no-build --no-restore --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Structured tests failed.' }
    # Preserve connection settings edited beside a previously published executable.
    $publishedSettingsPath = Join-Path $PSScriptRoot 'artifacts/Orbit/appsettings.json'
    $existingSettings = if (Test-Path -LiteralPath $publishedSettingsPath) { [System.IO.File]::ReadAllBytes($publishedSettingsPath) } else { $null }
    try {
        dotnet publish src/DevOpsClient -c Release --no-restore -o artifacts/Orbit
        $publishExitCode = $LASTEXITCODE
    } finally {
        if ($null -ne $existingSettings) { [System.IO.File]::WriteAllBytes($publishedSettingsPath, $existingSettings) }
    }
    $global:LASTEXITCODE = $publishExitCode
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Write-Host "Ready: $PSScriptRoot\artifacts\Orbit\DevOpsClient.exe"
} finally { Pop-Location }

