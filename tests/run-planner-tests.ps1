param(
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts/routesuggest/PlannerTests.dll'
& (Join-Path $root 'scripts/compile.ps1') -Source @((Join-Path $root 'mod/Planner.cs'), (Join-Path $root 'mod/FlightPlanner.cs'), (Join-Path $root 'tests/PlannerTests.cs')) -Output $out -GameDir $GameDir | Out-Null
$bytes = [IO.File]::ReadAllBytes($out)
$asm = [Reflection.Assembly]::Load($bytes)
$result = $asm.GetType('PlannerTests').GetMethod('Run').Invoke($null, @())
$result
if ($result -notmatch 'TOTAL pass=\d+ fail=0\s*$') { throw 'Planner tests failed.' }
