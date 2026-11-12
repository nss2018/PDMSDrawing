# -*- coding: utf-8 -*-
"""Scan all active skill roots for stale retired skill names in .md files."""
import os
import io
import sys

OLD = ("pdms-dialog-autoclose", "pdms-pml-course", "pdms-bridge-probe")

ROOTS = [
    r"C:\Users\Nie\.workbuddy\skills",
    r"D:\skill",
    r"C:\Users\Nie\.accio\accounts\1796501114\skills",
    r"C:\Users\Nie\.accio\accounts\7100633559\skills",
    r"C:\Users\Nie\.accio\accounts\1794603983\skills",
    r"C:\Users\Nie\.accio\accounts\7099707696\skills",
]


def tag(root):
    if ".workbuddy" in root:
        return "WB"
    if root.upper().startswith("D:"):
        return "D-skill"
    return "acc-" + root.split(os.sep)[-2]


def main():
    total_files = 0
    total_hits = 0
    by_root = {}
    for root in ROOTS:
        if not os.path.isdir(root):
            print("MISSING ROOT", root)
            continue
        entries = []
        for dp, dns, fns in os.walk(root):
            dns[:] = [d for d in dns if d not in ("__pycache__", ".git")]
            for fn in sorted(fns):
                if not fn.lower().endswith(".md"):
                    continue
                full = os.path.join(dp, fn)
                try:
                    lines = io.open(full, encoding="utf-8", errors="replace").read().splitlines()
                except Exception as e:
                    print("READ ERR", full, e)
                    continue
                hits = []
                for i, line in enumerate(lines, 1):
                    for name in OLD:
                        if name in line:
                            hits.append((i, name, line.strip()))
                if hits:
                    rel = os.path.relpath(full, root).replace(os.sep, "/")
                    entries.append((rel, hits, full))
        by_root[root] = entries
        total_files += len(entries)
        total_hits += sum(len(e[1]) for e in entries)
        print("=" * 78)
        print("ROOT %-10s %s   files=%d hits=%d" % (
            tag(root), root, len(entries), sum(len(e[1]) for e in entries)))
        for rel, hits, full in entries:
            print("  - %s  (%d)" % (rel, len(hits)))
            for i, name, line in hits:
                print("      L%-4d [%-24s] %s" % (i, name, line[:150]))
    print("=" * 78)
    print("TOTAL files=%d hits=%d" % (total_files, total_hits))


if __name__ == "__main__":
    main()
