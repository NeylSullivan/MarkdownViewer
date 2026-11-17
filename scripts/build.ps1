param([switch]$RunTests)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
Push-Location $projectRoot
try {
    dotnet build 'MarkdownViewer.slnx' -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if ($RunTests) {
        dotnet run --project 'tests\MarkdownViewer.SmokeTests' -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw 'Smoke tests failed.' }
    }
} finally { Pop-Location }
