"""Run the published CLI, compare real Mania snapshots with golden, and exercise MC/MR/Taiko."""
from pathlib import Path
import json
import subprocess
import sys

root = Path(__file__).resolve().parents[2]
exe = Path(sys.argv[1]).resolve()
output = root / "out/aot-check/cli-verified"
output.mkdir(parents=True, exist_ok=True)

def run(*args):
    return subprocess.run([str(exe), *map(str, args)], cwd=root, check=True, capture_output=True, encoding="utf-8-sig").stdout

def compare(expected, actual):
    if isinstance(expected, dict):
        for key, value in expected.items():
            if key != "fixture":
                compare(value, actual[key])
    elif isinstance(expected, list):
        assert len(expected) == len(actual)
        for a, b in zip(expected, actual):
            compare(a, b)
    elif isinstance(expected, float):
        assert abs(expected - actual) < 1e-10, (expected, actual)
    else:
        assert expected == actual, (expected, actual)

print(run("help"))
for path in sorted((root / "fixtures").iterdir()):
    if path.suffix in {".osu", ".mc"}:
        run("dump-map", path)
    elif path.suffix in {".osr", ".mr"}:
        run("dump-replay", path)
print(run("benchmark", root / "fixtures", output))
for name, prefix in {"CarnationLN": "- ck", "KuitDoinPineNut": "Kuit - Doin",
                     "RaijinTempest": "Kuiiiiteeee", "YuugureMarathon": "Kuit - Various"}.items():
    expected = json.loads((root / "tests/golden" / f"{name}.json").read_text())
    actual = json.loads(next(output.glob(prefix + "*.json")).read_text())
    compare(expected, actual)
    print("PASS native CLI vs golden:", name)
for name, map_ext, replay_ext, notes in [("aot-minimal", ".mc", ".mr", 2), ("aot-taiko", ".osu", ".osr", 4)]:
    run("dump-judge", root / "fixtures" / (name + map_ext), root / "fixtures" / (name + replay_ext), "--out", output / f"{name}.json")
    actual = json.loads((output / f"{name}.json").read_text())
    assert len(actual["notes"]) == notes
    print("PASS native CLI synthetic format:", name)
