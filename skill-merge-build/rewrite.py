# -*- coding: utf-8 -*-
# Merge pdms skills: rewrite SKILL.md bodies and fix cross-skill references.
# Usage: python rewrite.py staging | external
import io
import os
import sys

BUILD = r"D:\repos\pdmsDrawing\skill-merge-build"
SKILLS = r"C:\Users\Nie\.workbuddy\skills"

def E(rel, old, new, label, expect=1, base=BUILD):
    return (os.path.join(base, rel), old, new, label, expect)

STAGING = [
    # ---- pdms-dialog: frontmatter ----
    E("pdms-dialog/SKILL.md",
"""---
name: pdms-dialog
description: >-
  AVEVA PDMS / E3D 弹窗（模态 Error 框）扫描、读全文、自动关闭的纯 PowerShell 三件套，外加"桥接宏炸了"的完整诊断循环。
  只要出现以下任一情况就该用本技能：PDMS 桥接 pdms_exec 超时/挂起/没出结果文件、PML 宏中断后 PDMS 窗口里弹出 Error 框、
  "PDMS 卡住了/没反应/有个弹窗/怎么自己关弹窗"、RunInPdms 不返回、桥接 ECONNREFUSED 要判断死因、用户要求弹窗自动关闭/
  弹窗杀手/dialog watcher/ModalClose 类操作。核心循环：scan（枚举目标进程顶层窗口）→ dump（**先**读出弹窗里的 PML 错误文本，
  因为桥接会删结果文件，弹窗是错误文本唯一存留处）→ close（BM_CLICK 点 OK）→ 按 pdms-pml 坑清单修宏根因 → 重跑 → rescan 确认干净。
agent_created: true
---""",
"""---
name: pdms-dialog
version: 2.0.0
description: >-
  AVEVA PDMS / E3D 弹窗（模态 Error 框）一站式：①纯 PowerShell 三件套 scan/dump/close 交互式取证 + 精确打击；②常驻看门狗 dialog_watcher.py / ModalClose.exe 自动关弹窗；③"桥接宏炸了"的完整诊断循环。
  只要出现以下任一情况就该用本技能：PDMS 桥接 pdms_exec 超时/挂起/没出结果文件、PML 宏中断后 PDMS 窗口里弹出 Error 框、
  "PDMS 卡住了/没反应/有个弹窗/怎么自己关弹窗"、RunInPdms 不返回、桥接 ECONNREFUSED 要判断死因、用户要求弹窗自动关闭/
  弹窗杀手/dialog watcher/ModalClose/看门狗/watchdog 类操作。核心循环：scan（枚举目标进程顶层窗口）→ dump（**先**读出弹窗里的 PML 错误文本，
  因为桥接会删结果文件，弹窗是错误文本唯一存留处）→ close（BM_CLICK 点 OK）→ 按 pdms-pml 坑清单修宏根因 → 重跑 → rescan 确认干净。
agent_created: true
---""", "dialog frontmatter"),

    # ---- pdms-dialog: intro + routing table ----
    E("pdms-dialog/SKILL.md",
"""让智能体在 PDMS 自动化管线里**自己处理弹窗**：不用人肉切窗口点 OK。
来源：2026-10-27 现场验证（Design WPC / PDMS 12.1.SP4，本轮共关 4 个 Error 框并逐框读出 PML 错误文本）。
配套常驻方案见 `pdms-dialog-autoclose`（dialog_watcher.py / ModalClose.exe）；本技能是**交互式诊断 + 精确打击**版，
三者可并存：常驻看门狗管"白名单标题自动关"，本技能管"炸了要读错误文本再关"。""",
"""让智能体在 PDMS 自动化管线里**自己处理弹窗**：不用人肉切窗口点 OK。
来源：2026-10-27 现场验证（Design WPC / PDMS 12.1.SP4，本轮共关 4 个 Error 框并逐框读出 PML 错误文本）。

> **v2.0.0（2026-11-08）**：并入原 `pdms-dialog-autoclose`（常驻看门狗两实现），原技能目录已归档到 `D:\\repos\\pdmsDrawing\\skill-archive\\pdms-dialog-autoclose-20261108`。合并后一个技能覆盖「交互式取证」与「无人值守兜底」两半，**外部引用统一走 `pdms-dialog`**。

## 何时使用 · 两条路由（先分流再动手）

| 场景 | 走哪条 | 读什么 |
|---|---|---|
| 弹窗已经炸了，要**读出 PML 错误文本**、定位宏根因、精确关指定 HWND | 交互式诊断 | 本文「三件套」+「核心工作流」+ `references/bridge-macro-triage.md` |
| 长批 / 无人值守跑宏，要**提前挂常驻看门狗**自动点掉白名单弹窗 | 常驻兜底 | `references/auto-close-watchdog.md`（dialog_watcher.py / ModalClose.exe） |
| 端口扫不出来 / 长宏占死会话 / 结果文件只有 BOM | 桥接侧分诊 | `pdms-bridge` → `references/probe-and-macro-batching.md` |

**铁律不变**：先 dump 后 close（错误文本在关框前是唯一存留处）；常驻看门狗管"白名单标题自动关"，本文管"炸了要读错误文本再关"，两者可并存，长批跑建议先 `--ensure` 拉起。""", "dialog intro/routing"),

    # ---- pdms-dialog: resources + iteration + related ----
    E("pdms-dialog/SKILL.md",
"""## 关联技能

- `pdms-pml`：PML 语法 + 坑 10–15（弹窗的**根因**大多在宏体里）。
- `pdms-bridge`：MCP 连接器与 pdms_exec 通道细节。
- `pdms-dialog-autoclose`：常驻看门狗（dialog_watcher.py / ModalClose.exe）——长批跑时建议先 `--ensure` 拉起。""",
"""## 配套资源（标注合并来源）

| 文件 | 内容 | 来源 |
|---|---|---|
| `assets/scripts/pdms_dialog_{scan,dump,close}.ps1` | 纯 PowerShell 三件套（纯 ASCII） | 原 `pdms-dialog` |
| `references/bridge-macro-triage.md` | 桥接宏超时 / 崩溃 / 端口错位分诊 | 原 `pdms-dialog` |
| `references/auto-close-watchdog.md` | 常驻看门狗两实现 + 桥接集成 + 本机实体路径 | 原 `pdms-dialog-autoclose` |

## 迭代说明

- v1.x（2026-10-27）：交互式 scan/dump/close 三件套 + 诊断循环，8 条现场坑。
- v2.0.0（2026-11-08）：并入 `pdms-dialog-autoclose`，加两条路由与配套资源表；旧技能名只作溯源，不再作为独立技能引用。

## 关联技能

- `pdms-pml`：PML 语法 + 坑 10–15（弹窗的**根因**大多在宏体里）。
- `pdms-bridge`：MCP 连接器与 pdms_exec 通道细节；会话探活见 `references/probe-and-macro-batching.md`。
- `pdms-run-macro`：宏加载 / `%PMLLIB%` / `pml.index`。
- `pdms-uda-udet-mdb`：`(99,532)` 含点名诊断，弹窗防护同走本技能。""", "dialog tail"),

    # ---- pdms-bridge: frontmatter ----
    E("pdms-bridge/SKILL.md",
"""---
name: pdms-bridge
description: "让 AI 智能体直接驱动正在运行的 AVEVA PDMS 12.1.SP4：发 PML、查数据库、建图纸、出 ISO 轴测图。包含完整的 MCP 连接器代码（server.js + PdmsBridge.cs + TtyFeed.cs + 实战 PML 宏）与 WorkBuddy 注册方式。适用于工业软件自动化、工厂设计（PDMS/E3D）AI 操作场景。"
agent_created: true
---""",
"""---
name: pdms-bridge
version: 2.0.0
description: "让 AI 智能体直接驱动正在运行的 AVEVA PDMS 12.1.SP4：发 PML、查数据库、建图纸、出 ISO 轴测图。包含完整的 MCP 连接器代码（server.js + PdmsBridge.cs + TtyFeed.cs + 实战 PML 宏）与 WorkBuddy 注册方式；另含会话探活（发现清单 + Socket 端口扫描）、长宏分步下发防阻塞、结果文件 BOM 校验与语法猜测批次止损线。适用于工业软件自动化、工厂设计（PDMS/E3D）AI 操作场景。"
agent_created: true
---""", "bridge frontmatter"),

    # ---- pdms-bridge: new probe section ----
    E("pdms-bridge/SKILL.md",
"## 架构（已落地）",
"""## 会话探活与防阻塞（合并自 pdms-bridge-probe）

> 触发：`Get-NetTCPConnection` 返回空 / 异常、端口扫不出、长宏占死会话、`RunInPdms failed`、结果文件只有 3 字节 BOM。完整 4 步工作流、Fallback 与 Pitfalls 见 `references/probe-and-macro-batching.md`（原技能 `pdms-bridge-probe` 全文，2026-11-08 并入）。

| 症状 | 动作 |
|---|---|
| 端口扫不出来 | **优先读** `<PDMS_BRIDGE_DIR>/instances/<module>_<pid>.json` 发现清单；缺失时 Python `socket.connect_ex` 扫 18750–18760，**禁用** `Get-NetTCPConnection`（受限沙箱恒返回 0 条） |
| 端口开但 Ping 无响应 | 查 `bridge.log` 最后一条错误；按 `pdms-dialog` 清模态弹窗；必要时重启该模块 |
| 长宏 / 连续两个重型宏 | 拆短宏（先枚举成员名，再按名点取属性），单宏数秒内 `closefile`；`$M` 同步独占会话（`pdms-pml` 坑 34） |
| 结果文件 3 字节 BOM | 判宏中途崩 / 被弹窗挂起；回读必查 `===EXP-END===` 结束标记，别只信同步返回值 |
| "猜语法"批次连弹 3~4 次 | **止损定性**（DBL 含点名是文法结构性限制，`pdms-pml` 坑 35 / `pdms-uda-udet-mdb`），别再烧会话 |

## 架构（已落地）""", "bridge probe section"),

    # ---- pdms-bridge: resource table ----
    E("pdms-bridge/SKILL.md",
"- `references/README.md`、`references/ROADMAP.md`、`references/MCP-GENERIC-TOOLS.md`：架构、路线图、14 工具设计 + 首跑清单 + 护栏规范。",
"""- `references/README.md`、`references/ROADMAP.md`、`references/MCP-GENERIC-TOOLS.md`：架构、路线图、14 工具设计 + 首跑清单 + 护栏规范。
- `references/probe-and-macro-batching.md`：**会话探活 / 端口验证 / 长宏分步下发 / 结果文件校验 / 止损线**（合并自原 `pdms-bridge-probe`）。""", "bridge resource row"),

    # ---- pdms-bridge: iteration note ----
    E("pdms-bridge/SKILL.md",
"- 无显示环境：编译/注册可在本机完成，最终加载需 PDMS 实机验证。",
"""- 无显示环境：编译/注册可在本机完成，最终加载需 PDMS 实机验证。

## 迭代说明

- v1.x（2026-10-14 ~ 10-28）：TTY + 进程内 Command 双通道、12 个 MCP 工具、多模块确定性端口与发现清单。
- v2.0.0（2026-11-08）：并入 `pdms-bridge-probe`（Socket 探活 / 分步下发 / BOM 校验 / 止损线），新增「会话探活与防阻塞」章节；旧技能名只作溯源。""", "bridge iteration"),

    # ---- pdms-pml: version ----
    E("pdms-pml/SKILL.md", "version: 2.2.0", "version: 2.3.0", "pml version"),

    # ---- pdms-pml: v2.3.0 note ----
    E("pdms-pml/SKILL.md",
"> v2.2.0（2026-10-28）：",
"""> v2.3.0（2026-11-08）：清理旧副本 `pdms-pml-course`——它是 v2.1.0 合并**之前**的课程版，3 处 reference 差异（外链改写 / `.pmlobj` 说明）早已并入本技能，内容无缺失；原目录归档到 `D:\\repos\\pdmsDrawing\\skill-archive\\pdms-pml-course-20261108`，**外部引用统一走 `pdms-pml`**。
>
> v2.2.0（2026-10-28）：""", "pml v2.3 note"),

    # ---- pdms-pml: related skill ----
    E("pdms-pml/SKILL.md",
"`pdms-dialog-autoclose`（模态弹窗自动关，对应坑 27/33）",
"`pdms-dialog`（模态弹窗扫描/取证/自动关，对应坑 27/33）", "pml related"),

    # ---- pdms-pml gotchas: dead link ----
    E("pdms-pml/references/pml-gotchas.md",
"`pdms-dialog-autoclose`", "`pdms-dialog`（见 `references/auto-close-watchdog.md`）", "pml gotchas dead link"),
]

EXTERNAL = [
    E("pdms-expert/references/postmortems.md",
      "（`pdms-dialog-autoclose` 技能描述其实没写错",
      "（`pdms-dialog`（原 `pdms-dialog-autoclose`）技能描述其实没写错",
      "expert postmortems", base=SKILLS),

    E("pdms-expert/references/discipline.md",
      "走 `pdms-pml` / `pdms-pml-course` / 本正本",
      "走 `pdms-pml`（课程 + 避坑已合并，原 `pdms-pml-course` 已归档）/ 本正本",
      "expert discipline", base=SKILLS),

    E("pdms-run-macro/SKILL.md",
"""- `pdms-pml`（实战避坑全集：坑1–9，含 `handle ANY` 就近保护、结果文件回读）。
- `pdms-pml-course`（PML 标准语法课程：宏与变量、元素导航、控制流、文件处理）。""",
      "- `pdms-pml`（一站式：课程式标准语法 + 实战避坑全集；原 `pdms-pml-course` 已并入并归档，按 `references/` 模块路由查主题）。",
      "run-macro pml-course link", base=SKILLS),

    E("pdms-run-macro/SKILL.md",
      "- `pdms-dialog-autoclose`（PDMS 弹窗自动关闭：headless 跑宏时弹窗挂住命令线程的兜底方案）。",
      "- `pdms-dialog`（PDMS 弹窗扫描/取证/自动关闭：headless 跑宏时弹窗挂住命令线程的兜底方案，常驻看门狗见其 `references/auto-close-watchdog.md`）。",
      "run-macro dialog link", base=SKILLS),

    E("pdms-uda-udet-mdb/SKILL.md",
      "2. **OS 侧看门狗**：`pdms-dialog-autoclose` 技能的 `dialog_watcher.py` / `ModalClose.exe`",
      "2. **OS 侧看门狗**：`pdms-dialog` → `references/auto-close-watchdog.md` 的 `dialog_watcher.py` / `ModalClose.exe`",
      "uda watchdog link", base=SKILLS),

    E("pdms-uda-udet-mdb/SKILL.md",
      "- `pdms-dialog-autoclose`（弹窗自动关闭）",
      "- `pdms-dialog`（弹窗扫描 / 取证 / 自动关闭）",
      "uda related link", base=SKILLS),
]


def apply(edits, phase):
    for path, old, new, label, expect in edits:
        if not os.path.isfile(path):
            print("FAIL  %-32s missing: %s" % (label, path))
            sys.exit(1)
        with io.open(path, "r", encoding="utf-8", newline="") as f:
            raw = f.read()
        crlf = "\r\n" in raw
        s = raw.replace("\r\n", "\n")
        n = s.count(old)
        if expect == 1 and n != 1:
            print("FAIL  %-32s count=%d (want 1)" % (label, n))
            sys.exit(1)
        if expect < 0 and n < 1:
            print("FAIL  %-32s count=0" % label)
            sys.exit(1)
        out = s.replace(old, new)
        if crlf:
            out = out.replace("\n", "\r\n")
        with io.open(path, "w", encoding="utf-8", newline="") as f:
            f.write(out)
        print("OK    %-32s (%d hit)" % (label, n))
    print("phase %s done: %d edits" % (phase, len(edits)))


if __name__ == "__main__":
    phase = sys.argv[1] if len(sys.argv) > 1 else "staging"
    apply({"staging": STAGING, "external": EXTERNAL}[phase], phase)
