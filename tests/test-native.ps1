param(
    [Parameter(Mandatory)][string]$ToolProject,
    [string]$Python = 'python',
    [ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$ResultsName = 'results',
    [string]$ProbeSource = 'NativeProbe.cs'
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskRuntime = Join-Path $taskRoot 'artifacts/routesuggest/native/runtime'
$taskProbe = Join-Path $taskRuntime 'mods/RouteSuggestPlusProbe'
& (Join-Path $taskRoot 'scripts/compile.ps1') -Source (Join-Path $PSScriptRoot $ProbeSource) -Output (Join-Path $taskProbe 'RouteSuggestPlusProbe.dll') -ExtraReferences (Join-Path $taskRoot 'dist/RouteSuggestPlus/RouteSuggestPlus.dll')
'{"id":"RouteSuggestPlusProbe","name":"RouteSuggestPlus isolated test","author":"规范有勇气","version":"0.1.0","min_game_version":"0.111.0","has_dll":true,"has_pck":false,"affects_gameplay":false,"dependencies":[{"id":"RouteSuggestPlus","min_version":"0.1.0"}]}' | Set-Content -LiteralPath (Join-Path $taskProbe 'RouteSuggestPlusProbe.json') -Encoding utf8
$taskData = Join-Path $taskRuntime 'data_sts2_windows_x86_64'
if (-not (Test-Path -LiteralPath $taskData)) {
    [void](New-Item -ItemType Junction -Path $taskData -Target 'C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64')
}
& $Python -X utf8 (Join-Path $PSScriptRoot 'test-native.py') $ToolProject $ResultsName
if ($LASTEXITCODE -ne 0) { throw "Native test failed; inspect artifacts/routesuggest/native/$ResultsName." }
