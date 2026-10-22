param(
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2',
    [switch]$Install
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskSource = Join-Path $taskRoot 'mod'
$taskDist = Join-Path $taskRoot 'dist/RouteSuggestPlus'
& (Join-Path $taskRoot 'scripts/compile.ps1') -Source (Get-ChildItem -LiteralPath $taskSource -Filter '*.cs').FullName -Output (Join-Path $taskDist 'RouteSuggestPlus.dll') -GameDir $GameDir
Copy-Item -LiteralPath (Join-Path $taskSource 'RouteSuggestPlus.json') -Destination $taskDist -Force
Copy-Item -LiteralPath (Join-Path $taskRoot 'upstream/LICENSE') -Destination (Join-Path $taskDist 'LICENSE') -Force
foreach ($taskExtra in @('README.md', 'preview.png')) {
    $taskExtraPath = Join-Path $taskRoot $taskExtra
    if (Test-Path -LiteralPath $taskExtraPath) { Copy-Item -LiteralPath $taskExtraPath -Destination $taskDist -Force }
}
$taskVersion = (Get-Content -LiteralPath (Join-Path $taskDist 'RouteSuggestPlus.json') -Raw | ConvertFrom-Json).version
Compress-Archive -Path (Join-Path $taskDist '*') -DestinationPath (Join-Path $taskRoot "dist/RouteSuggestPlus-$taskVersion.zip") -Force
if ($Install) {
    $taskTarget = Join-Path $GameDir 'mods/RouteSuggestPlus'
    if (Test-Path -LiteralPath $taskTarget) {
        $taskBackup = Join-Path $taskRoot ('artifacts/routesuggest/backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
        [void](New-Item -ItemType Directory -Path (Split-Path -Parent $taskBackup) -Force)
        Copy-Item -LiteralPath $taskTarget -Destination $taskBackup -Recurse
    }
    [void](New-Item -ItemType Directory -Path $taskTarget -Force)
    foreach ($taskFile in @('RouteSuggestPlus.dll', 'RouteSuggestPlus.json', 'LICENSE', 'README.md', 'preview.png')) {
        $taskFilePath = Join-Path $taskDist $taskFile
        if (Test-Path -LiteralPath $taskFilePath) { Copy-Item -LiteralPath $taskFilePath -Destination (Join-Path $taskTarget $taskFile) -Force }
    }
    Write-Output "INSTALLED $taskTarget"
}
