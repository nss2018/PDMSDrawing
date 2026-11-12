#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""batch_draw.py - unattended batch drawing driver for pdmsDrawing.

WT-DRAW benchmark feature: "batch drawing / unattended mode" with a real
deliverable per sheet and a machine-readable run report.

It reuses the existing one-sheet pipeline (wizard.pipeline: replace_symbols ->
SAVEWORK -> export_pdf -> mono -> preview). Each job gets its OWN work dir by
temporarily pointing wizard.OUTDIR at it, so PDFs of different sheets never
overwrite each other (the single-sheet pipeline writes a fixed preview.pdf).

Usage:
  python batch_draw.py jobs.json --out OUTDIR
  python batch_draw.py jobs.json --out OUTDIR --dry-run --fail 02-设备布置图
  python batch_draw.py jobs.json --out OUTDIR --resume --retry 1
  python batch_draw.py jobs.json --out OUTDIR --only "01-管道布置图,03-管口方位图"

No third-party packages are required. wizard.py is imported only in real mode.
All comments are ASCII on purpose (project rule: no CJK in script comments).
"""
import argparse
import json
import os
import re
import shutil
import sys
import time
from datetime import datetime

DEFAULT_OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "batch_out")


def now_str():
    return datetime.now().strftime("%Y-%m-%d %H:%M:%S")


def safe_id(name, index):
    base = re.sub(r"[\\/:*?\"<>|\r\n\t]+", "_", str(name)).strip(" ._") or ("job%02d" % index)
    return "%02d_%s" % (index, base[:80])


def load_config(path):
    with open(path, "r", encoding="utf-8-sig") as f:
        cfg = json.load(f)
    if not isinstance(cfg, dict) or not isinstance(cfg.get("jobs"), list):
        raise ValueError("jobs.json must be an object with a 'jobs' array")
    return cfg


def build_jobs(cfg, only=None):
    defaults = dict(cfg.get("defaults") or {})
    wanted = None
    if only:
        wanted = set(x.strip() for x in only.split(",") if x.strip())
    jobs = []
    for i, raw in enumerate(cfg["jobs"], 1):
        if not isinstance(raw, dict):
            raise ValueError("job #%d is not an object" % i)
        name = str(raw.get("name") or ("job%02d" % i))
        if wanted and name not in wanted:
            continue
        params = dict(defaults)
        params.update(dict(raw.get("params") or {}))
        jobs.append({"id": safe_id(name, i), "name": name, "params": params})
    return jobs


def wizard_runner(params, work_dir):
    """Run one real sheet. wizard.OUTDIR is scoped to this job only."""
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import wizard
    os.makedirs(work_dir, exist_ok=True)
    old_out = wizard.OUTDIR
    try:
        wizard.OUTDIR = work_dir
        wizard.pipeline(dict(params))
        pipe = wizard.PIPE
        pdf = None
        cand = os.path.join(work_dir, "preview.pdf")
        if pipe.get("ok") and os.path.exists(cand):
            pdf = cand
        return {
            "ok": bool(pipe.get("ok")),
            "log": list(pipe.get("log") or []),
            "pdf": pdf,
            "error": None if pipe.get("ok") else "pipeline failed, see job log",
        }
    except Exception as e:
        return {"ok": False, "log": [], "pdf": None, "error": repr(e)}
    finally:
        wizard.OUTDIR = old_out


def dry_runner(params, work_dir, name, fail_names):
    """Offline runner for validating the batch driver without PDMS."""
    time.sleep(0.05)
    if name in fail_names:
        return {"ok": False, "log": ["[dry] simulated failure for " + name],
                "pdf": None, "error": "simulated failure"}
    return {"ok": True, "log": ["[dry] simulated pipeline for " + name,
                                "[dry] params=" + json.dumps(params, ensure_ascii=False)],
            "pdf": None, "error": None}


def archive_result(job, result, out_dir, work_dir):
    pdf = result.get("pdf")
    archived = None
    if pdf and os.path.exists(pdf):
        pdf_dir = os.path.join(out_dir, "pdf")
        os.makedirs(pdf_dir, exist_ok=True)
        archived = os.path.join(pdf_dir, job["id"] + ".pdf")
        shutil.copy2(pdf, archived)
    log_dir = os.path.join(out_dir, "logs")
    os.makedirs(log_dir, exist_ok=True)
    log_path = os.path.join(log_dir, job["id"] + ".log")
    with open(log_path, "w", encoding="utf-8") as f:
        f.write("JOB %s  %s\n" % (job.get("name") or job.get("id") or "-", now_str()))
        for line in result.get("log") or []:
            f.write(str(line) + "\n")
        if result.get("error"):
            f.write("ERROR " + str(result["error"]) + "\n")
    return {"pdf": archived, "log": log_path}


def read_state(out_dir):
    path = os.path.join(out_dir, "state.json")
    if not os.path.exists(path):
        return {}
    try:
        with open(path, "r", encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return {}


def write_state(out_dir, state):
    os.makedirs(out_dir, exist_ok=True)
    tmp = os.path.join(out_dir, "state.json.tmp")
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(state, f, ensure_ascii=False, indent=2)
    os.replace(tmp, os.path.join(out_dir, "state.json"))


def write_report(out_dir, cfg, state, jobs):
    rows = []
    for j in jobs:
        r = (state.get("jobs") or {}).get(j["id"], {})
        rows.append((j["name"], r.get("status", "-"), r.get("attempts", 0),
                     r.get("seconds", "-"), r.get("pdf") or "-", r.get("error") or "-"))
    ok = len([r for r in rows if r[1] in ("ok", "dry-ok")])
    failed = len([r for r in rows if r[1] == "failed"])
    skipped = len([r for r in rows if r[1] == "skipped"])

    md = []
    md.append("# 批量出图汇总报告")
    md.append("")
    md.append("- 批次：%s" % (cfg.get("name") or "未命名批次"))
    md.append("- 生成时间：%s" % now_str())
    md.append("- 图纸：%d 张（成功 %d / 失败 %d / 跳过 %d）" % (len(rows), ok, failed, skipped))
    std = (cfg.get("defaults") or {}).get("standard")
    md.append("- 出图规范：%s" % (std or "project（项目默认）"))
    md.append("")
    md.append("| 图纸 | 状态 | 尝试 | 耗时(s) | PDF | 错误 |")
    md.append("| --- | --- | --- | --- | --- | --- |")
    for r in rows:
        md.append("| %s | %s | %s | %s | %s | %s |" % tuple(str(x) for x in r))
    md.append("")
    if failed:
        md.append("## 失败图纸（--resume --retry 1 可续跑）")
        md.append("")
        for r in rows:
            if r[1] == "failed":
                md.append("- %s：%s" % (r[0], r[5]))
        md.append("")
    md.append("> 结果文件：state.json（断点续跑状态）、report.json（机器可读）、pdf/、logs/。")
    md.append("")
    with open(os.path.join(out_dir, "report.md"), "w", encoding="utf-8") as f:
        f.write("\n".join(md))
    with open(os.path.join(out_dir, "report.json"), "w", encoding="utf-8") as f:
        json.dump({"generated": now_str(), "batch": cfg.get("name"),
                   "total": len(rows), "ok": ok, "failed": failed, "skipped": skipped,
                   "jobs": [{"name": r[0], "status": r[1], "attempts": r[2],
                             "seconds": r[3], "pdf": r[4], "error": r[5]} for r in rows]},
                  f, ensure_ascii=False, indent=2)


def run_batch(cfg, out_dir, dry_run=False, resume=False, retry=0, only=None,
              fail_names=(), on_progress=None):
    jobs = build_jobs(cfg, only)
    os.makedirs(out_dir, exist_ok=True)
    state = read_state(out_dir)
    state.setdefault("jobs", {})
    state["updated"] = now_str()
    state.setdefault("started", now_str())

    def progress(msg):
        line = "[batch] " + str(msg)
        print(line)
        if on_progress:
            try:
                on_progress(line + "\n")
            except Exception:
                pass

    progress("start: %d sheets, dry_run=%s resume=%s retry=%s" %
             (len(jobs), dry_run, resume, retry))
    for j in jobs:
        prev = state["jobs"].get(j["id"], {})
        if resume and prev.get("status") in ("ok", "dry-ok"):
            state["jobs"][j["id"]] = prev
            progress("skip (already ok): " + j["name"])
            write_state(out_dir, state)
            continue
        attempts = int(prev.get("attempts") or 0)
        max_rounds = retry + 1 if prev.get("status") == "failed" else 1
        result = None
        t0 = time.time()
        for rnd in range(max_rounds):
            attempts += 1
            progress("run %s (attempt %d)" % (j["name"], attempts))
            work_dir = os.path.join(out_dir, "work", j["id"])
            if dry_run:
                result = dry_runner(j["params"], work_dir, j["name"], fail_names)
            else:
                result = wizard_runner(j["params"], work_dir)
            if result.get("ok"):
                break
            progress("failed %s: %s" % (j["name"], result.get("error")))
        art = archive_result(j, result or {}, out_dir,
                             os.path.join(out_dir, "work", j["id"]))
        state["jobs"][j["id"]] = {
            "name": j["name"],
            "status": ("dry-ok" if dry_run else "ok") if result and result.get("ok") else "failed",
            "attempts": attempts,
            "seconds": round(time.time() - t0, 2),
            "pdf": art["pdf"], "log": art["log"],
            "error": None if result and result.get("ok") else (result or {}).get("error"),
        }
        write_state(out_dir, state)
        progress("done %s -> %s" % (j["name"], state["jobs"][j["id"]]["status"]))
    write_report(out_dir, cfg, state, jobs)
    progress("report: " + os.path.join(out_dir, "report.md"))
    return state


def main():
    ap = argparse.ArgumentParser(description="Unattended batch drawing for pdmsDrawing")
    ap.add_argument("jobs", help="JSON sheet list")
    ap.add_argument("--out", default=DEFAULT_OUT, help="report/state/PDF output dir")
    ap.add_argument("--dry-run", action="store_true", help="no PDMS, simulate the pipeline")
    ap.add_argument("--fail", default="", help="dry-run: comma separated job names to fail")
    ap.add_argument("--resume", action="store_true", help="skip jobs already ok in state.json")
    ap.add_argument("--retry", type=int, default=0, help="extra attempts for previously failed jobs")
    ap.add_argument("--only", default="", help="comma separated job names to run")
    a = ap.parse_args()
    cfg = load_config(a.jobs)
    fails = set(x.strip() for x in a.fail.split(",") if x.strip())
    state = run_batch(cfg, os.path.abspath(a.out), a.dry_run, a.resume, a.retry,
                      a.only or None, fails)
    jobs = state.get("jobs") or {}
    bad = [k for k, v in jobs.items() if v.get("status") == "failed"]
    print("BATCH RESULT: %d jobs, %d failed" % (len(jobs), len(bad)))
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
