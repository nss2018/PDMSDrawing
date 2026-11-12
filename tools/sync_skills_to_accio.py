"""Sync WorkBuddy user skills into ACCIO skill roots.

Rules (verified against ACCIO 0.34.4 client code):
  - Additive only. Never delete files that exist only in a target.
  - Never edit skills_config.json / skills.jsonc / remote_skills_cache.json.
    Those files are managed by the ACCIO client; hand edits create IDs that
    look configured but do not load at runtime.
  - Back up every target skill dir that will be overwritten BEFORE writing.
  - Hash verification after the copy; exit 1 on any mismatch.
  - Optional --retire moves the three retired duplicate skills into an
    archive folder. Nothing is ever deleted.

Usage:
  python sync_skills_to_accio.py --dry-run
  python sync_skills_to_accio.py
  python sync_skills_to_accio.py --retire
  python sync_skills_to_accio.py --targets "D:/skill" --only pdms-dialog,pdms-pml
"""
import argparse
import hashlib
import io
import json
import os
import shutil
import sys
import time
import zipfile

HOME = os.path.expanduser("~")
DEFAULT_SOURCE = os.path.join(HOME, ".workbuddy", "skills")
ACCOUNTS_ROOT = os.path.join(HOME, ".accio", "accounts")
DEFAULT_TARGETS = ["D:/skill"] + [
    os.path.join(ACCOUNTS_ROOT, a, "skills")
    for a in ("1796501114", "7100633559", "1794603983", "7099707696")
]
RETIRED = ("pdms-dialog-autoclose", "pdms-pml-course", "pdms-bridge-probe")
# ACCIO copies of these files contain extra field notes that do NOT exist in
# the WorkBuddy canonical copy. Never overwrite them: keep the target file and
# drop the canonical text beside it as SKILL.workbuddy.md.
PRESERVE = {
    "pdms-run-macro/SKILL.md",
    "pdms-spec-bolting-gasket/SKILL.md",
}
ARCHIVE = r"D:\repos\pdmsDrawing\skill-archive"
REPORT_DIR = r"D:\repos\pdmsDrawing\reports"
SKIP_DIRS = {"__pycache__", ".git", ".svn", "node_modules", ".venv", ".idea"}
SKIP_SUFFIX = (".pyc", ".pyo", ".tmp", ".bak", ".swp")


def digest(path):
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def snap(root):
    out = {}
    if not os.path.isdir(root):
        return out
    for dp, dns, fns in os.walk(root):
        dns[:] = [d for d in dns if d not in SKIP_DIRS]
        for fn in fns:
            if fn.startswith("._") or fn == ".DS_Store" or fn.endswith(SKIP_SUFFIX):
                continue
            full = os.path.join(dp, fn)
            rel = os.path.relpath(full, root).replace(os.sep, "/")
            out[rel] = digest(full)
    return out


def discover(source):
    skills = []
    if not os.path.isdir(source):
        raise SystemExit("source not found: %s" % source)
    for name in sorted(os.listdir(source)):
        d = os.path.join(source, name)
        if not os.path.isdir(d) or name.startswith("."):
            continue
        if not os.path.isfile(os.path.join(d, "SKILL.md")):
            continue
        skills.append((name, snap(d)))
    return skills


def tag_of(path):
    parts = path.replace("\\", "/").split("/")
    if parts[-2:-1] == ["accounts"] or "accounts" in parts:
        i = parts.index("accounts")
        if len(parts) > i + 1:
            return "account-" + parts[i + 1]
    return parts[-1].lower().replace(":", "")


def plan(skills, targets):
    rows = []
    for t in targets:
        tag = tag_of(t)
        for name, smap in skills:
            d = os.path.join(t, name)
            tmap = snap(d)
            new = [r for r in smap if r not in tmap]
            diff = [r for r in smap if r in tmap and tmap[r] != smap[r]]
            same = [r for r in smap if r in tmap and tmap[r] == smap[r]]
            extra = [r for r in tmap if r not in smap]
            preserved = ["%s/%s" % (name, r) for r in diff
                         if "%s/%s" % (name, r) in PRESERVE]
            diff = [r for r in diff if "%s/%s" % (name, r) not in PRESERVE]
            rows.append(dict(tag=tag, target=t, skill=name, exists=bool(tmap),
                             new=new, diff=diff, same=same, extra=extra,
                             preserved=preserved))
    return rows


def backup(rows, zip_path):
    touched = {}
    for r in rows:
        if r["exists"] and (r["new"] or r["diff"] or r["preserved"]):
            touched[(r["target"], r["skill"])] = True
    if not touched:
        return 0
    os.makedirs(os.path.dirname(zip_path), exist_ok=True)
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        for (t, skill) in sorted(touched):
            root = os.path.join(t, skill)
            tag = tag_of(t)
            for dp, dns, fns in os.walk(root):
                dns[:] = [d for d in dns if d not in SKIP_DIRS]
                for fn in sorted(fns):
                    full = os.path.join(dp, fn)
                    rel = os.path.relpath(full, root).replace(os.sep, "/")
                    z.write(full, "%s/%s/%s" % (tag, skill, rel))
    with zipfile.ZipFile(zip_path) as z:
        assert z.testzip() is None, "backup zip corrupted"
        return len(z.namelist())


def apply(rows, source, dry):
    stats = dict(new=0, diff=0, same=0, extra=0, preserved=0, files=0)
    if dry:
        for r in rows:
            stats["new"] += len(r["new"])
            stats["diff"] += len(r["diff"])
            stats["same"] += len(r["same"])
            stats["extra"] += len(r["extra"])
            stats["preserved"] += len(r["preserved"])
        return stats
    for r in rows:
        sroot = os.path.join(source, r["skill"])
        troot = os.path.join(r["target"], r["skill"])
        for prel in r["preserved"]:
            skill, fname = prel.split("/", 1)
            sp = os.path.join(source, skill, fname.replace("/", os.sep))
            cp = os.path.join(r["target"], skill, "SKILL.workbuddy.md")
            if not os.path.isfile(cp) or digest(cp) != digest(sp):
                os.makedirs(os.path.dirname(cp), exist_ok=True)
                shutil.copy2(sp, cp)
                stats["files"] += 1
            stats["preserved"] += 1
        for rel in r["new"] + r["diff"]:
            sp = os.path.join(sroot, rel.replace("/", os.sep))
            tp = os.path.join(troot, rel.replace("/", os.sep))
            parent = os.path.dirname(tp)
            if not os.path.isdir(parent):
                os.makedirs(parent, exist_ok=True)
            shutil.copy2(sp, tp)
            stats["files"] += 1
        stats["new"] += len(r["new"])
        stats["diff"] += len(r["diff"])
        stats["same"] += len(r["same"])
        stats["extra"] += len(r["extra"])
    return stats


def verify(rows):
    bad = []
    for r in rows:
        tmap = snap(os.path.join(r["target"], r["skill"]))
        for rel in r["same"] + r["new"] + r["diff"]:
            want = None
            for s in SOURCE_SKILLS:
                if s[0] == r["skill"]:
                    want = s[1].get(rel)
                    break
            if want is None or tmap.get(rel) != want:
                bad.append("%s/%s/%s" % (r["tag"], r["skill"], rel))
        for prel in r["preserved"]:
            skill, fname = prel.split("/", 1)
            want = None
            for s in SOURCE_SKILLS:
                if s[0] == skill:
                    want = s[1].get(fname)
                    break
            cp = os.path.join(r["target"], skill, "SKILL.workbuddy.md")
            orig = os.path.join(r["target"], skill, fname.replace("/", os.sep))
            if want is None or not os.path.isfile(orig) or not os.path.isfile(cp) or digest(cp) != want:
                bad.append("%s/%s (protected copy)" % (r["tag"], prel))
    return bad


def retire(targets, dry):
    moved = []
    stamp = time.strftime("%Y%m%d")
    dest_root = os.path.join(ARCHIVE, "accio-retired-" + stamp)
    for t in targets:
        tag = tag_of(t)
        for name in RETIRED:
            src = os.path.join(t, name)
            if not os.path.isdir(src):
                continue
            dst = os.path.join(dest_root, tag, name)
            if dry:
                moved.append(dict(tag=tag, name=name, src=src, dst=dst, action="WOULD-MOVE"))
                continue
            os.makedirs(os.path.dirname(dst), exist_ok=True)
            if os.path.exists(dst):
                dst = dst + "-" + str(int(time.time()))
            shutil.move(src, dst)
            moved.append(dict(tag=tag, name=name, src=src, dst=dst, action="MOVED"))
    return moved


def write_reports(rows, stats, bad, moved, zip_path, dry, targets, source):
    os.makedirs(REPORT_DIR, exist_ok=True)
    stamp = time.strftime("%Y%m%d")
    payload = dict(generated=time.strftime("%Y-%m-%d %H:%M:%S"), dry=dry,
                   source=source, targets=targets, stats=stats,
                   verify_failed=bad, retired=moved, backup=zip_path,
                   skills=sorted(set(r["skill"] for r in rows)),
                   per_target={}, overwritten=[], extras=[],
                   preserved=sorted(set(p for r in rows for p in r["preserved"])))
    for r in rows:
        t = payload["per_target"].setdefault(r["tag"], dict(target=r["target"],
                                                            new=0, diff=0, same=0, extra=0, skills=0))
        t["new"] += len(r["new"]); t["diff"] += len(r["diff"])
        t["same"] += len(r["same"]); t["extra"] += len(r["extra"])
        t["skills"] += 1
        if r["exists"] and (r["new"] or r["diff"]):
            payload["overwritten"].append("%s/%s" % (r["tag"], r["skill"]))
        if r["extra"]:
            payload["extras"].append("%s/%s (%d files)" % (r["tag"], r["skill"], len(r["extra"])))
    jp = os.path.join(REPORT_DIR, "skill-sync-accio-%s.json" % stamp)
    with io.open(jp, "w", encoding="utf-8", newline="\n") as f:
        f.write(json.dumps(payload, ensure_ascii=False, indent=2))
    rows_html = "".join(
        "<tr><td>%s</td><td>%d</td><td>%d</td><td>%d</td><td>%d</td><td>%d</td></tr>" % (
            k, v["skills"], v["new"], v["diff"], v["same"], v["extra"])
        for k, v in sorted(payload["per_target"].items()))
    html = """<!doctype html><html lang="zh-CN"><head><meta charset="utf-8">
<title>WorkBuddy → ACCIO 技能同步报告</title><style>
body{background:#f6f7f9;color:#20242b;font:14px/1.6 -apple-system,"Microsoft YaHei",sans-serif;margin:0;padding:32px}
.wrap{max-width:1000px;margin:auto;background:#fff;border:1px solid #e3e6ea;border-radius:10px;padding:28px 32px}
h1{font-size:22px;margin:0 0 6px}h2{font-size:16px;margin:26px 0 10px;border-left:3px solid #2f6fdb;padding-left:10px}
.sub{color:#6b7280;font-size:13px}table{border-collapse:collapse;width:100%;margin:10px 0}th,td{border:1px solid #e5e8ec;padding:7px 10px;text-align:left;font-size:13px}th{background:#f2f5f9}
code{background:#f1f3f6;padding:1px 5px;border-radius:4px;font-size:12px}
.ok{color:#16794a;font-weight:600}.warn{color:#b45309;font-weight:600}.bad{color:#b42318;font-weight:600}
ul{margin:8px 0;padding-left:22px}li{margin:3px 0}
</style></head><body><div class="wrap">
<h1>WorkBuddy → ACCIO 技能同步报告</h1>
<div class="sub">生成时间 __TIME__ ｜ 模式 __MODE__ ｜ 源 <code>__SRC__</code></div>
<h2>汇总</h2><p>技能 <b>__NSKILL__</b> 个 ｜ 目标 <b>__NTGT__</b> 个 ｜ 新增 <b>__NEW__</b> 文件 ｜ 覆盖 <b>__DIFF__</b> 文件 ｜ 已一致 <b>__SAME__</b> 文件 ｜ 目标独有保留 <b>__EXTRA__</b> 文件</p>
<p>一致性校验：<span class="__VCLS__">__VERIFY__</span> ｜ 覆盖前备份：<code>__ZIP__</code>（__ZIPN__ 文件）</p>
<h2>分目标明细</h2><table><tr><th>目标</th><th>技能数</th><th>新增</th><th>覆盖</th><th>已一致</th><th>独有保留</th></tr>__ROWS__</table>
<h2>规则与注意</h2><ul>
<li>增量不删：目标目录里多出的文件（ACCIO 侧扩展）全部保留，共 __EXTRA__ 个。</li>
<li>未改 <code>skills_config.json</code> / <code>skills.jsonc</code> / <code>remote_skills_cache.json</code>：ACCIO 客户端自托管这些文件，手改会产生不加载的假 ID。</li>
<li>保护升级：__PRESERVED_NOTE__。</li>
<li>旧副本 __RETIRED_NOTE__。</li>
<li>ACCIO 客户端正在运行时可能缓存技能清单，同步后建议重启 ACCIO（或新建会话）再确认。</li>
<li>不要在 ACCIO 侧再跑旧版 <code>sync_skills.py</code>（agent-core 仍是 10-28 老源），否则会把 account / D:\\skill 回滚。</li>
</ul></div></body></html>"""
    vcls = "ok" if not bad else "bad"
    vtxt = "PASS（源文件逐一对上）" if not bad else "FAIL " + ", ".join(bad[:5])
    html = (html.replace("__TIME__", payload["generated"])
                .replace("__MODE__", "DRY-RUN 预演" if dry else "实际执行")
                .replace("__SRC__", source)
                .replace("__NSKILL__", str(len(payload["skills"])))
                .replace("__NTGT__", str(len(targets)))
                .replace("__NEW__", str(stats["new"]))
                .replace("__DIFF__", str(stats["diff"]))
                .replace("__SAME__", str(stats["same"]))
                .replace("__EXTRA__", str(stats["extra"]))
                .replace("__VCLS__", vcls).replace("__VERIFY__", vtxt)
                .replace("__ZIP__", "DRY-RUN 未生成" if dry else zip_path)
                .replace("__ZIPN__", str(ZIP_COUNT))
                .replace("__ROWS__", rows_html)
                .replace("__PRESERVED_NOTE__", (
                    "%d 个 ACCIO 增强版文件未覆盖（%s），WorkBuddy canonical 版另存为 <code>SKILL.workbuddy.md</code>" % (
                        len(payload["preserved"]), ", ".join(payload["preserved"]))
                    if payload["preserved"] else "无"))
                .replace("__RETIRED_NOTE__", (
                    "本次归档 %d 个（可逆 mv，未删除）" % len(moved) if moved
                    else "脚本不会自动删除旧副本；<code>--retire</code> 为可选归档开关，用前请自行确认")))
    hp = os.path.join(REPORT_DIR, "skill-sync-accio-%s.html" % stamp)
    with io.open(hp, "w", encoding="utf-8", newline="\n") as f:
        f.write(html)
    return jp, hp


SOURCE_SKILLS = []
ZIP_COUNT = 0


def main():
    global SOURCE_SKILLS, ZIP_COUNT
    ap = argparse.ArgumentParser()
    ap.add_argument("--source", default=DEFAULT_SOURCE)
    ap.add_argument("--targets", default=None, help="comma separated target roots")
    ap.add_argument("--only", default=None, help="comma separated skill names")
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--retire", action="store_true", help="move retired duplicates to archive")
    ap.add_argument("--no-backup", action="store_true")
    ap.add_argument("--no-report", action="store_true")
    args = ap.parse_args()

    targets = [t.strip() for t in args.targets.split(",")] if args.targets else DEFAULT_TARGETS
    for t in targets:
        os.makedirs(t, exist_ok=True)
    SOURCE_SKILLS = discover(args.source)
    if args.only:
        keep = set(x.strip() for x in args.only.split(","))
        SOURCE_SKILLS = [s for s in SOURCE_SKILLS if s[0] in keep]
    rows = plan(SOURCE_SKILLS, targets)
    stamp = time.strftime("%Y%m%d")
    zip_path = os.path.join(ARCHIVE, "accio-presync-%s.zip" % stamp)
    if os.path.exists(zip_path):
        zip_path = os.path.join(ARCHIVE, "accio-presync-%s-%d.zip" % (stamp, int(time.time())))

    if not args.dry_run and not args.no_backup:
        ZIP_COUNT = backup(rows, zip_path)
        print("backup -> %s (%d files)" % (zip_path, ZIP_COUNT))
    stats = apply(rows, args.source, args.dry_run)
    bad = [] if args.dry_run else verify(rows)
    moved = retire(targets, args.dry_run or not args.retire) if args.retire else []

    print("sources   : %d skills from %s" % (len(SOURCE_SKILLS), args.source))
    print("targets   : %s" % ", ".join(tag_of(t) for t in targets))
    print("files     : new=%d overwrite=%d same=%d target-only=%d protected=%d written=%d" % (
        stats["new"], stats["diff"], stats["same"], stats["extra"],
        stats["preserved"], stats["files"]))
    if stats["preserved"]:
        print("protected : %s" % ", ".join(sorted(set(p for r in rows for p in r["preserved"]))))
    for r in rows:
        if r["exists"] and (r["new"] or r["diff"]):
            print("  refresh %-14s %-32s +%d ~%d" % (r["tag"], r["skill"], len(r["new"]), len(r["diff"])))
    if bad:
        print("VERIFY FAILED: %s" % ", ".join(bad[:8]))
    else:
        print("verify    : %s" % ("skipped (dry-run)" if args.dry_run else "PASS"))
    if moved:
        print("retired   : %d dirs" % len(moved))

    if not args.no_report:
        jp, hp = write_reports(rows, stats, bad, moved, zip_path, args.dry_run, targets, args.source)
        print("reports   : %s | %s" % (jp, hp))
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
