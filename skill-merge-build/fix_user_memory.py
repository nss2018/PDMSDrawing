# -*- coding: utf-8 -*-
# Fix shell-escaped paths in the appended user-level memory note.
import io

P = r"C:\Users\Nie\.workbuddy\MEMORY.md"
MARK = "## PDMS 工程与技能集定位"

NOTE = r"""## PDMS 工程与技能集定位（2026-11-08 聂工拍板，防 U 盘丢失）
- 最新 C# 出图主工程 = D:\repos\drawingPDMS（11-06 版，含 14a 标注避让/管口方位图/DrawingWizard）；D:\repos\pdmsDrawing = 10-28 沉淀版 + Python 桥接/向导/批量编排（wizard.py 在此）。改动前先认工程。
- 主工程 Debug 编译默认直出 C:\AVEVA\Plant\PDMS12.1.SP4\（生成即部署），只验证请用 -p:OutputPath=...\build_check\ 覆盖。
- ~/.workbuddy/skills/ 的 pdms 系列已从 17 个去重为 14 个：pdms-dialog / pdms-bridge / pdms-pml 为 canonical，旧副本归档在 D:\repos\pdmsDrawing\skill-archive\。
"""

with io.open(P, "r", encoding="utf-8") as f:
    s = f.read()
idx = s.find(MARK)
if idx < 0:
    raise SystemExit("marker not found")
head = s[:idx].rstrip() + "\n\n"
with io.open(P, "w", encoding="utf-8", newline="") as f:
    f.write(head + NOTE)
with io.open(P, "r", encoding="utf-8") as f:
    out = f.read()
assert "D:\\repos\\drawingPDMS" in out
assert "\r" not in out.split(MARK)[1]
assert "\b" not in out.split(MARK)[1]
print("fixed. tail:")
print(out[-420:])
