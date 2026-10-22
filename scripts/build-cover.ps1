param(
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2'
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskRuntime = Join-Path $taskRoot 'artifacts/cover-builder'
$taskDist = Join-Path $taskRoot 'dist/RouteSuggestPlus'
[void](New-Item -ItemType Directory -Path $taskRuntime, $taskDist -Force)
# Run only a tiny asset project; no game PCK, autoloads, Steam or player saves.
$taskEngine = Join-Path $taskRuntime 'SlayTheSpire2.exe'
$taskInstalledEngine = Get-Item -LiteralPath (Join-Path $GameDir 'SlayTheSpire2.exe')
if (-not (Test-Path -LiteralPath $taskEngine) -or (Get-Item -LiteralPath $taskEngine).LastWriteTimeUtc -ne $taskInstalledEngine.LastWriteTimeUtc -or (Get-Item -LiteralPath $taskEngine).Length -ne $taskInstalledEngine.Length) {
    Copy-Item -LiteralPath $taskInstalledEngine.FullName -Destination $taskEngine
}
@'
config_version=5
[application]
config/name="RouteSuggestPlus Cover Builder"
config/use_custom_user_dir=true
config/custom_user_dir_name="RouteSuggestPlus-CoverBuilder"
[rendering]
renderer/rendering_method="gl_compatibility"
'@ | Set-Content -LiteralPath (Join-Path $taskRuntime 'project.godot') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'build-cover.gd') -Destination $taskRuntime
[void](New-Item -ItemType Directory -Path (Join-Path $taskRuntime '.godot') -Force)
'list=Array[Dictionary]([])' | Set-Content -LiteralPath (Join-Path $taskRuntime '.godot/global_script_class_cache.cfg') -Encoding utf8
$taskStart = [Diagnostics.ProcessStartInfo]::new($taskEngine)
$taskStart.UseShellExecute = $false
$taskStart.CreateNoWindow = $true
$taskStart.RedirectStandardOutput = $true
$taskStart.RedirectStandardError = $true
foreach ($taskArg in @('--headless', '--path', $taskRuntime, '--script', 'res://build-cover.gd', '--', (Join-Path $taskRoot 'preview.png'), (Join-Path $taskDist 'RouteSuggestPlus.pck'))) {
    $taskStart.ArgumentList.Add($taskArg)
}
$taskProcess = [Diagnostics.Process]::Start($taskStart)
try {
    $taskStdout = $taskProcess.StandardOutput.ReadToEndAsync()
    $taskStderr = $taskProcess.StandardError.ReadToEndAsync()
    if (-not $taskProcess.WaitForExit(60000)) { $taskProcess.Kill($true); throw 'Cover builder timed out.' }
    $taskOutput = $taskStdout.GetAwaiter().GetResult()
    $taskErrors = $taskStderr.GetAwaiter().GetResult()
    $taskOutput
    if ($taskErrors) { Write-Warning $taskErrors }
    if ($taskProcess.ExitCode -ne 0 -or $taskOutput -notmatch 'COVER_PASS') { throw 'Cover pack build or resource-load verification failed.' }
} finally { $taskProcess.Dispose() }
