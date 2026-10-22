"""Run RouteSuggestPlus alone in a separate game runtime and save directory (Steam off)."""
import importlib.util
import json
import os
from pathlib import Path
import shutil
import sys
import re

root = Path(__file__).resolve().parents[1]
source = Path(sys.argv[1]) / 'research/animated_cards/run_game_probe.py'
spec = importlib.util.spec_from_file_location('existing_probe', source)
probe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(probe)
probe.RUNTIME = root / 'artifacts/routesuggest/native/runtime'
result_name = sys.argv[2] if len(sys.argv) > 2 else 'results'
assert re.fullmatch(r'[a-zA-Z0-9_-]+', result_name), 'Invalid result directory name'
probe.OUTPUT = root / 'artifacts/routesuggest/native' / result_name
probe.USERDIR = 'CelestialAnimationGameProbe-RouteSuggestPlus'
probe.RUNTIME.mkdir(parents=True, exist_ok=True)
probe.OUTPUT.mkdir(parents=True, exist_ok=True)
for item in probe.GAME.iterdir():
    if item.is_file() and (item.suffix.lower() in {'.dll', '.exe'} or item.name == 'release_info.json'):
        probe.copy_file(item, probe.RUNTIME / item.name)
target = probe.RUNTIME / 'mods/RouteSuggestPlus'
if target.exists():
    shutil.rmtree(target)
shutil.copytree(root / 'dist/RouteSuggestPlus', target)
allowed_mods = {'RouteSuggestPlus', 'RouteSuggestPlusProbe'}
assert {item.name for item in (probe.RUNTIME / 'mods').iterdir()} <= allowed_mods
pack, original, schema = probe.prepare_pack()
env = dict(os.environ)
env['CELESTIAL_GAME_PREFLIGHT_OUTPUT'] = str(probe.OUTPUT / 'preflight_report.json')
env['CELESTIAL_GAME_PREFLIGHT_USERDIR'] = probe.USERDIR
base = [str(probe.RUNTIME / 'SlayTheSpire2.exe'), '--path', str(probe.RUNTIME), '--main-pack', str(pack), '--force-steam=off']
preflight = probe.OUTPUT / 'preflight_report.json'
if preflight.exists():
    preflight.unlink()
probe.launch(base + ['--headless', '--audio-driver', 'Dummy', '--nomods', '--script', 'res://celestial_probe/preflight.gd', '--log-file', str(probe.OUTPUT / 'preflight.log')], env, 'preflight', 45)
report = json.loads(preflight.read_text())
assert report['status'] == 'passed' and all(report['checks'].values()), report
probe.seed_settings(report, schema)
env['ROUTEPLUS_PROBE_OUTPUT'] = str(probe.OUTPUT)
result = probe.OUTPUT / 'native-report.json'
if result.exists():
    result.unlink()
probe.launch(base + ['--windowed', '--resolution', '1600x1000', '--rendering-method', 'gl_compatibility', '--log-file', str(probe.OUTPUT / 'native.log')], env, 'native', 240)
report = json.loads(result.read_text())
print(json.dumps(report, ensure_ascii=False, indent=2))
assert report['status'] == 'passed', report
