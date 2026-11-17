param([string]$ExecutablePath)
$ErrorActionPreference = 'Stop'
if (-not $ExecutablePath) {
    $besideScript = Join-Path $PSScriptRoot 'MarkdownViewer.exe'
    $ExecutablePath = if (Test-Path -LiteralPath $besideScript) { $besideScript } else { Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\MarkdownViewer-win-x64-framework-dependent\MarkdownViewer.exe' }
}
$ExecutablePath = (Resolve-Path -LiteralPath $ExecutablePath).Path
if ([IO.Path]::GetFileName($ExecutablePath) -ne 'MarkdownViewer.exe') { throw 'Select MarkdownViewer.exe.' }
$classRoot = 'HKCU:\Software\Classes'
$progId = 'MarkdownViewer.Document'
$documentKey = Join-Path $classRoot $progId
New-Item -Path $documentKey -Force | Out-Null
Set-Item -LiteralPath $documentKey -Value 'Markdown document'
$commandKey = Join-Path $documentKey 'shell\open\command'
New-Item -Path $commandKey -Force | Out-Null
Set-Item -LiteralPath $commandKey -Value ('"' + $ExecutablePath + '" "%1"')
$iconKey = Join-Path $documentKey 'DefaultIcon'
New-Item -Path $iconKey -Force | Out-Null
Set-Item -LiteralPath $iconKey -Value ('"' + $ExecutablePath + '",0')
foreach ($extension in @('.md','.markdown','.mdown','.mkd')) {
    $openWith = Join-Path $classRoot ($extension + '\OpenWithProgids')
    New-Item -Path $openWith -Force | Out-Null
    New-ItemProperty -LiteralPath $openWith -Name $progId -Value '' -PropertyType String -Force | Out-Null
}
Write-Output 'Markdown Viewer registered for this user. Choose it in Windows Open with to set the default application.'
