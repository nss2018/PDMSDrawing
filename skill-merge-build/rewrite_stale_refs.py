# -*- coding: utf-8 -*-
"""Rewrite stale retired skill names in all active skill roots.

Rules are exact-substring replacements keyed by file relative path.
Nothing else in the files is touched (line endings are preserved byte-wise).
Usage:
    python rewrite_stale_refs.py --dry-run
    python rewrite_stale_refs.py --apply
"""
import io
import os
import sys
import zipfile
import time

OLD = ("pdms-dialog-autoclose", "pdms-pml-course", "pdms-bridge-probe")

ROOTS = [
    r"C:\Users\Nie\.workbuddy\skills",
    r"D:\skill",
    r"C:\Users\Nie\.accio\accounts\1796501114\skills",
    r"C:\Users\Nie\.accio\accounts\7100633559\skills",
    r"C:\Users\Nie\.accio\accounts\1794603983\skills",
    r"C:\Users\Nie\.accio\accounts\7099707696\skills",
]

BACKUP = r"D:\repos\pdmsDrawing\skill-archive\stale-refs-presync-20261108.zip"

# (relative path, exact old substring, new substring)
RULES = [
    # ---- pdms-bridge canonical ----
    ("pdms-bridge/SKILL.md",
     "## 会话探活与防阻塞（合并自 pdms-bridge-probe）",
     "## 会话探活与防阻塞（合并自原「探活版」，已归档）"),
    ("pdms-bridge/SKILL.md",
     "（原技能 `pdms-bridge-probe` 全文，2026-11-08 并入）",
     "（原「探活版」技能全文，2026-11-08 并入并归档）"),
    ("pdms-bridge/SKILL.md",
     "（合并自原 `pdms-bridge-probe`）",
     "（合并自原「探活版」，已归档）"),
    ("pdms-bridge/SKILL.md",
     "- v2.0.0（2026-11-08）：并入 `pdms-bridge-probe`（Socket 探活 / 分步下发 / BOM 校验 / 止损线），新增「会话探活与防阻塞」章节；旧技能名只作溯源。",
     "- v2.0.0（2026-11-08）：并入原「探活版」技能（Socket 探活 / 分步下发 / BOM 校验 / 止损线），新增「会话探活与防阻塞」章节；旧技能已归档，外部引用统一走 `pdms-bridge`。"),

    # ---- pdms-bridge reference ----
    ("pdms-bridge/references/probe-and-macro-batching.md",
     "# 会话探活 / 端口验证 / 长宏防阻塞（合并自 pdms-bridge-probe）",
     "# 会话探活 / 端口验证 / 长宏防阻塞（合并自原「探活版」，已归档）"),
    ("pdms-bridge/references/probe-and-macro-batching.md",
     "> 来源：原技能 `pdms-bridge-probe`（2026-11-08 合并入 `pdms-bridge`，原目录已归档到 `D:\\repos\\pdmsDrawing\\skill-archive\\pdms-bridge-probe-20261108`）。",
     "> 来源：原「探活版」技能（2026-11-08 合并入 `pdms-bridge`；原目录归档在 `D:\\repos\\pdmsDrawing\\skill-archive`，2026-11-08）。"),

    # ---- pdms-dialog canonical ----
    ("pdms-dialog/SKILL.md",
     "> **v2.0.0（2026-11-08）**：并入原 `pdms-dialog-autoclose`（常驻看门狗两实现），原技能目录已归档到 `D:\\repos\\pdmsDrawing\\skill-archive\\pdms-dialog-autoclose-20261108`。",
     "> **v2.0.0（2026-11-08）**：并入原「看门狗版」技能（常驻看门狗两实现），原目录归档在 `D:\\repos\\pdmsDrawing\\skill-archive`（2026-11-08）。"),
    ("pdms-dialog/SKILL.md",
     "| 原 `pdms-dialog-autoclose` |",
     "| 原「看门狗版」（已归档） |"),
    ("pdms-dialog/SKILL.md",
     "- v2.0.0（2026-11-08）：并入 `pdms-dialog-autoclose`，加两条路由与配套资源表；旧技能名只作溯源，不再作为独立技能引用。",
     "- v2.0.0（2026-11-08）：并入原「看门狗版」技能，加两条路由与配套资源表；旧技能已归档，外部引用统一走 `pdms-dialog`。"),

    # ---- pdms-dialog reference ----
    ("pdms-dialog/references/auto-close-watchdog.md",
     "# 常驻看门狗：自动关闭 PDMS 弹窗（合并自 pdms-dialog-autoclose）",
     "# 常驻看门狗：自动关闭 PDMS 弹窗（合并自原「看门狗版」，已归档）"),
    ("pdms-dialog/references/auto-close-watchdog.md",
     "> 来源：原技能 `pdms-dialog-autoclose`（2026-11-08 合并入 `pdms-dialog`，原目录已归档到 `D:\\repos\\pdmsDrawing\\skill-archive\\pdms-dialog-autoclose-20261108`）。",
     "> 来源：原「看门狗版」技能（2026-11-08 合并入 `pdms-dialog`；原目录归档在 `D:\\repos\\pdmsDrawing\\skill-archive`，2026-11-08）。"),

    # ---- pdms-expert ----
    ("pdms-expert/references/discipline.md",
     "（课程 + 避坑已合并，原 `pdms-pml-course` 已归档）",
     "（课程 + 避坑已合并，原课程版已归档）"),
    ("pdms-expert/references/postmortems.md",
     "（`pdms-dialog`（原 `pdms-dialog-autoclose`）技能描述其实没写错",
     "（`pdms-dialog`（含常驻看门狗章节）技能描述其实没写错"),

    # ---- pdms-pml canonical ----
    ("pdms-pml/SKILL.md",
     "清理旧副本 `pdms-pml-course`——",
     "清理历史残留副本（原课程版）——"),
    ("pdms-pml/SKILL.md",
     "原目录归档到 `D:\\repos\\pdmsDrawing\\skill-archive\\pdms-pml-course-20261108`",
     "原目录归档在 `D:\\repos\\pdmsDrawing\\skill-archive`（2026-11-08）"),
    ("pdms-pml/SKILL.md",
     "由原 `pdms-pml`（实战避坑）与 `pdms-pml-course`（课程式教程）合并而成",
     "由原 `pdms-pml`（实战避坑）与原课程版（课程式教程）合并而成"),

    # ---- pdms-run-macro (WorkBuddy canonical version, 4 roots) ----
    ("pdms-run-macro/SKILL.md",
     "；原 `pdms-pml-course` 已并入并归档，按",
     "；原课程版已并入并归档，按"),
    ("pdms-run-macro/SKILL.workbuddy.md",
     "；原 `pdms-pml-course` 已并入并归档，按",
     "；原课程版已并入并归档，按"),

    # ---- pdms-run-macro (ACCIO enhanced version, 2 roots) ----
    ("pdms-run-macro/SKILL.md",
     "- `pdms-pml`（实战避坑全集：坑1–9，含 `handle ANY` 就近保护、结果文件回读）。",
     "- `pdms-pml`（一站式：课程式标准语法 + 实战避坑全集；坑1–9，含 `handle ANY` 就近保护、结果文件回读）。"),
    ("pdms-run-macro/SKILL.md",
     "- `pdms-pml-course`（PML 标准语法课程：宏与变量、元素导航、控制流、文件处理）。",
     "- `pdms-pml/references/`（课程式标准语法：`macros-variables` / `element-navigation` / `control-flow` / `files`，按主题路由）。"),
    ("pdms-run-macro/SKILL.md",
     "- `pdms-dialog-autoclose`（PDMS 弹窗自动关闭：headless 跑宏时弹窗挂住命令线程的兜底方案）。",
     "- `pdms-dialog`（PDMS 弹窗一站式：交互式取证 + 常驻看门狗 `references/auto-close-watchdog.md`；headless 跑宏时弹窗挂住命令线程的兜底方案）。"),

    # ---- skill-merge-pack methodology ----
    ("skill-merge-pack/SKILL.md",
     "## 变体：一侧其实是「历史残留副本」（2026-11-08 pdms-pml-course 实战）",
     "## 变体：一侧其实是「历史残留副本」（2026-11-08 三技能整合实战）"),

    # ---- ACCIO-only skills (2 roots) ----
    ("pdms-bridge-identifier-lint/SKILL.md",
     "真正原因是 `pdms-pml-course` 硬约束 6",
     "真正原因是 `pdms-pml` 课程版硬约束 6"),
    ("pdms-bridge-identifier-lint/SKILL.md",
     "（`pdms-pml-course` references/element-navigation.md + assets/examples/locate_demo.pml 原文）：",
     "（`pdms-pml/references/element-navigation.md` + `pdms-pml/assets/examples/locate_demo.pml` 原文）："),
    ("pdms-bridge-port-binding/SKILL.md",
     "（见 `pdms-dialog-autoclose`）",
     "（见 `pdms-dialog` 的常驻看门狗章节 `references/auto-close-watchdog.md`）"),
]


def read(path):
    with io.open(path, "r", encoding="utf-8", newline="") as f:
        return f.read()


def write(path, text):
    with io.open(path, "w", encoding="utf-8", newline="") as f:
        f.write(text)


def all_md(root):
    out = []
    for dp, dns, fns in os.walk(root):
        dns[:] = [d for d in dns if d not in ("__pycache__", ".git")]
        for fn in fns:
            if fn.lower().endswith(".md"):
                out.append(os.path.join(dp, fn))
    return out


def main():
    apply = "--apply" in sys.argv
    dry = "--dry-run" in sys.argv
    if not (apply or dry):
        print("usage: rewrite_stale_refs.py --dry-run | --apply")
        return 2

    # Pre-flight: every rule must hit at least one root.
    changes = []   # (root, fullpath, oldtext, newtext, nrules)
    missing = []
    for root in ROOTS:
        if not os.path.isdir(root):
            print("MISSING ROOT", root)
            continue
        for rel, old, new in RULES:
            full = os.path.join(root, rel.replace("/", os.sep))
            if os.path.isfile(full):
                text = read(full)
                if old in text:
                    # Multiple rules may target the same file: chain them.
                    existing = next((c for c in changes if c[0] == root and c[1] == full), None)
                    if existing is None:
                        changes.append([root, full, text, text, []])
                        existing = changes[-1]
                    n = existing[3].count(old)
                    existing[3] = existing[3].replace(old, new)
                    existing[4].append((rel, old, new, n))
                else:
                    missing.append((root, rel, old[:40]))

    hit_rules = len(set((rel, old) for _r, _f, _o, _n, rs in changes for rel, old, _n2, _c in rs))
    print("rules total=%d  hit=%d  no-op-in-file=%d" % (len(RULES), hit_rules, len(missing)))
    print("files to change = %d" % len(changes))
    for root, full, before, after, rs in changes:
        tag = ("WB" if ".workbuddy" in root else
               "D-skill" if root.upper().startswith("D:") else
               "acc-" + root.split(os.sep)[-2])
        print("  %-18s %-56s %d rule(s)" % (
            tag, os.path.relpath(full, root).replace(os.sep, "/"), len(rs)))

    # Guard: no stale name may survive in the rewritten content.
    leftovers = []
    for root, full, before, after, rs in changes:
        for name in OLD:
            if name in after:
                leftovers.append((full, name, after.count(name)))
    print("leftover stale names in rewritten files = %d" % len(leftovers))
    for full, name, n in leftovers[:10]:
        print("   !!", full, name, n)

    if dry:
        print("DRY RUN - no file written")
        return 0
    if leftovers:
        print("ABORT: stale names would remain")
        return 1

    # Snapshot originals (only files that will be written).
    os.makedirs(os.path.dirname(BACKUP), exist_ok=True)
    with zipfile.ZipFile(BACKUP, "w", zipfile.ZIP_DEFLATED) as z:
        for root, full, before, after, rs in changes:
            tag = ("WB" if ".workbuddy" in root else
                   "D-skill" if root.upper().startswith("D:") else
                   "acc-" + root.split(os.sep)[-2])
            arc = "%s/%s" % (tag, os.path.relpath(full, root).replace(os.sep, "/"))
            z.writestr(arc, before.encode("utf-8"))
    print("snapshot:", BACKUP)

    for root, full, before, after, rs in changes:
        if before != after:
            write(full, after)
    print("written files = %d" % sum(1 for c in changes if c[2] != c[3]))

    # Full verification across all roots (any .md, not just touched ones).
    bad = []
    for root in ROOTS:
        if not os.path.isdir(root):
            continue
        for full in all_md(root):
            text = read(full)
            for name in OLD:
                if name in text:
                    bad.append((root, os.path.relpath(full, root), name, text.count(name)))
    print("VERIFY stale names remaining in any .md = %d" % len(bad))
    for b in bad[:20]:
        print("   !!", b)
    return 0 if not bad else 1


if __name__ == "__main__":
    sys.exit(main())
