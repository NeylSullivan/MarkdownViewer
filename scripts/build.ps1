param(
    [switch]$RunTests,
    [ValidateNotNullOrEmpty()]
    [string]$Version
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseVersion.ps1')
if ($PSBoundParameters.ContainsKey('Version')) { Assert-ReleaseVersion -Version $Version }
$projectRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$versionArgs = @()
if ($Version) { $versionArgs += "-p:Version=$Version" }
Push-Location $projectRoot
try {
    # Keep restore consistent with the Windows x64 target in packages.lock.json.
    dotnet build 'MarkdownViewer.slnx' -c Release -p:RuntimeIdentifiers=win-x64 -p:RestoreLockedMode=true -p:IncludeSourceRevisionInInformationalVersion=false @versionArgs --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if ($RunTests) {
        dotnet run --project 'tests\MarkdownViewer.SmokeTests' -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw 'Smoke tests failed.' }
    }
} finally { Pop-Location }
