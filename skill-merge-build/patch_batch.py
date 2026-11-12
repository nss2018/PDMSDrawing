# -*- coding: utf-8 -*-
# Adapt batch_draw.py for the 11-06 mainline tree (wizard.py lives in another repo).
import io
import os
import sys

P = r"D:\repos\drawingPDMS\tools\batch_draw.py"

EDITS = [
("docstring",
 '''"""batch_draw.py - unattended batch drawing driver for pdmsDrawing.''',
 '''"""batch_draw.py - unattended batch drawing driver for drawingPDMS (11-06 mainline).

Mainline copy: the C# engine lives in D:\\repos\\drawingPDMS, while the validated
one-sheet pipeline (wizard.py) still lives in D:\\repos\\pdmsDrawing\\bridge.
Point at it with --wizard-dir or PDMS_WIZARD_DIR; --dry-run needs no wizard.'''),

("imports",
 "import argparse\nimport json",
 "import argparse\nimport importlib.util\nimport json"),

("wizard loader",
 '''def wizard_runner(params, work_dir):
    """Run one real sheet. wizard.OUTDIR is scoped to this job only."""
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import wizard
    os.makedirs(work_dir, exist_ok=True)''',
 '''DEFAULT_WIZARD_DIRS = [
    os.environ.get("PDMS_WIZARD_DIR"),
    os.path.dirname(os.path.abspath(__file__)),
    os.path.join(os.path.dirname(os.path.abspath(__file__)), "bridge"),
    r"D:\\repos\\pdmsDrawing\\bridge",
]


def load_wizard(explicit=None):
    """Import wizard.py by path; its HTTP server is guarded by __main__, so import is safe."""
    candidates = ([explicit] if explicit else []) + [d for d in DEFAULT_WIZARD_DIRS if d]
    for d in candidates:
        path = os.path.join(d, "wizard.py")
        if not os.path.isfile(path):
            continue
        spec = importlib.util.spec_from_file_location("pdms_wizard", path)
        mod = importlib.util.module_from_spec(spec)
        sys.modules["pdms_wizard"] = mod
        spec.loader.exec_module(mod)
        return mod
    raise RuntimeError("wizard.py not found; pass --wizard-dir or set PDMS_WIZARD_DIR. Tried: " +
                       ", ".join(candidates))


def wizard_runner(params, work_dir, wizard_dir=None):
    """Run one real sheet. wizard.OUTDIR is scoped to this job only."""
    wizard = load_wizard(wizard_dir)
    os.makedirs(work_dir, exist_ok=True)'''),

("run_batch signature",
 '''def run_batch(cfg, out_dir, dry_run=False, resume=False, retry=0, only=None,
              fail_names=(), on_progress=None):''',
 '''def run_batch(cfg, out_dir, dry_run=False, resume=False, retry=0, only=None,
              fail_names=(), on_progress=None, wizard_dir=None):'''),

("runner call",
 "                result = wizard_runner(j[\"params\"], work_dir)",
 "                result = wizard_runner(j[\"params\"], work_dir, wizard_dir)"),

("argparse",
 '''    ap.add_argument("--only", default="", help="comma separated job names to run")
    a = ap.parse_args()''',
 '''    ap.add_argument("--only", default="", help="comma separated job names to run")
    ap.add_argument("--wizard-dir", default="", help="dir containing wizard.py (or set PDMS_WIZARD_DIR)")
    a = ap.parse_args()'''),

("main call",
 '''    state = run_batch(cfg, os.path.abspath(a.out), a.dry_run, a.resume, a.retry,
                      a.only or None, fails)''',
 '''    state = run_batch(cfg, os.path.abspath(a.out), a.dry_run, a.resume, a.retry,
                      a.only or None, fails, wizard_dir=a.wizard_dir or None)'''),
]

with io.open(P, "r", encoding="utf-8", newline="") as f:
    raw = f.read()
crlf = "\r\n" in raw
s = raw.replace("\r\n", "\n")
for label, old, new in EDITS:
    n = s.count(old)
    if n != 1:
        print("FAIL %-20s count=%d" % (label, n))
        sys.exit(1)
    s = s.replace(old, new)
    print("OK   %-20s" % label)
out = s.replace("\n", "\r\n") if crlf else s
with io.open(P, "w", encoding="utf-8", newline="") as f:
    f.write(out)
print("patched", P)
