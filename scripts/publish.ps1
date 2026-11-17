$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$name = 'MarkdownViewer-win-x64-framework-dependent'
$output = Join-Path $projectRoot ('artifacts\' + $name)
Push-Location $projectRoot
try {
    dotnet publish 'src\MarkdownViewer\MarkdownViewer.csproj' -c Release -r win-x64 --self-contained false -p:DebugType=None -p:DebugSymbols=false -o $output --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $output -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Destination $output -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses') -Destination $output -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Register-FileAssociation.ps1') -Destination $output -Force
    Compress-Archive -Path (Join-Path $output '*') -DestinationPath (Join-Path $projectRoot ('artifacts\' + $name + '.zip')) -Force
    Write-Output (Join-Path $output 'MarkdownViewer.exe')
} finally { Pop-Location }
