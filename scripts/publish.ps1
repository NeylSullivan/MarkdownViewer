param(
    [ValidateNotNullOrEmpty()]
    [string]$Version
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseVersion.ps1')
if ($PSBoundParameters.ContainsKey('Version')) { Assert-ReleaseVersion -Version $Version }
$projectRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$name = 'MarkdownViewer-win-x64-framework-dependent'
$versionArgs = @()
if ($Version) {
    $name = "MarkdownViewer-$Version-win-x64-framework-dependent"
    $versionArgs += "-p:Version=$Version"
}
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts'))
$output = [IO.Path]::GetFullPath((Join-Path $artifactsRoot $name))
$archive = Join-Path $artifactsRoot "$name.zip"
if (-not $output.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Publish output must be inside the repository artifacts directory.'
}
Push-Location $projectRoot
try {
    if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
    dotnet publish 'src\MarkdownViewer\MarkdownViewer.csproj' -c Release -r win-x64 --self-contained false -p:RestoreLockedMode=true -p:IncludeSourceRevisionInInformationalVersion=false -p:DebugType=None -p:DebugSymbols=false @versionArgs -o $output --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $output -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Destination $output -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses') -Destination $output -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Register-FileAssociation.ps1') -Destination $output -Force
    Compress-Archive -Path (Join-Path $output '*') -DestinationPath $archive -Force
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$archive.sha256", "$hash  $name.zip`n", [Text.Encoding]::ASCII)
    Write-Output (Join-Path $output 'MarkdownViewer.exe')
} finally { Pop-Location }
