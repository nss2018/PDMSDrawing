---
name: pdms-dialog
version: 2.0.0
description: >-
  AVEVA PDMS / E3D 弹窗（模态 Error 框）一站式：①纯 PowerShell 三件套 scan/dump/close 交互式取证 + 精确打击；②常驻看门狗 dialog_watcher.py / ModalClose.exe 自动关弹窗；③"桥接宏炸了"的完整诊断循环。
  只要出现以下任一情况就该用本技能：PDMS 桥接 pdms_exec 超时/挂起/没出结果文件、PML 宏中断后 PDMS 窗口里弹出 Error 框、
  "PDMS 卡住了/没反应/有个弹窗/怎么自己关弹窗"、RunInPdms 不返回、桥接 ECONNREFUSED 要判断死因、用户要求弹窗自动关闭/
  弹窗杀手/dialog watcher/ModalClose/看门狗/watchdog 类操作。核心循环：scan（枚举目标进程顶层窗口）→ dump（**先**读出弹窗里的 PML 错误文本，
  因为桥接会删结果文件，弹窗是错误文本唯一存留处）→ close（BM_CLICK 点 OK）→ 按 pdms-pml 坑清单修宏根因 → 重跑 → rescan 确认干净。
agent_created: true
---

# PDMS 弹窗 扫描 / 读文 / 自动关闭（pdms-dialog）

让智能体在 PDMS 自动化管线里**自己处理弹窗**：不用人肉切窗口点 OK。
来源：2026-10-27 现场验证（Design WPC / PDMS 12.1.SP4，本轮共关 4 个 Error 框并逐框读出 PML 错误文本）。

> **v2.0.0（2026-11-08）**：并入原 `pdms-dialog-autoclose`（常驻看门狗两实现），原技能目录已归档到 `D:\repos\pdmsDrawing\skill-archive\pdms-dialog-autoclose-20261108`。合并后一个技能覆盖「交互式取证」与「无人值守兜底」两半，**外部引用统一走 `pdms-dialog`**。

## 何时使用 · 两条路由（先分流再动手）

| 场景 | 走哪条 | 读什么 |
|---|---|---|
| 弹窗已经炸了，要**读出 PML 错误文本**、定位宏根因、精确关指定 HWND | 交互式诊断 | 本文「三件套」+「核心工作流」+ `references/bridge-macro-triage.md` |
| 长批 / 无人值守跑宏，要**提前挂常驻看门狗**自动点掉白名单弹窗 | 常驻兜底 | `references/auto-close-watchdog.md`（dialog_watcher.py / ModalClose.exe） |
| 端口扫不出来 / 长宏占死会话 / 结果文件只有 BOM | 桥接侧分诊 | `pdms-bridge` → `references/probe-and-macro-batching.md` |

**铁律不变**：先 dump 后 close（错误文本在关框前是唯一存留处）；常驻看门狗管"白名单标题自动关"，本文管"炸了要读错误文本再关"，两者可并存，长批跑建议先 `--ensure` 拉起。

## 前置依赖与环境声明

- 仅 Windows + 一个运行中的 PDMS 进程。**脚本是纯 PowerShell 5.1 + Win32 P/Invoke，零第三方依赖**（不需 pywin32 / UIA / admin）。
- PDMS 未运行时脚本照常执行，只是枚举不到目标窗口（返回空）。不接触 PDMS 数据库、不改任何设计数据。
- 进程名：PDMS 12.1.SP4 = `des.exe`（`Get-Process des`）；E3D / 其他版本按实际进程名替换。
- **安全边界**：scan/dump 只读；close 只 `PostMessage(BM_CLICK)` 点击 OK/OKAY/YES/CLOSE 类按钮，不会关设计主窗口/视图，不会提交/回滚任何数据。

## 三件套（assets/scripts/，全部纯 ASCII —— 原因见坑 1）

| 脚本 | 用法 | 作用 |
|---|---|---|
| `pdms_dialog_scan.ps1` | `powershell -ExecutionPolicy Bypass -File pdms_dialog_scan.ps1 <pid1,pid2>` | 枚举指定进程**全部顶层窗口**（含不可见），输出 `PID= HWND= CLASS= VIS= TITLE=` |
| `pdms_dialog_dump.ps1` | `powershell -File pdms_dialog_dump.ps1 <HWND十进制>` | dump 对话框子控件：文本 + RichEdit 全文（WM_GETTEXT）——**PML 错误行就在这里** |
| `pdms_dialog_close.ps1` | `powershell -File pdms_dialog_close.ps1 <HWND十进制>` | 找 OK 类按钮（类名含 BUTTON）→ `PostMessage(BM_CLICK=0x00F5)` 点掉 |

取 PID：`Get-Process des | Select Id`。HWND 十进制直接从 scan 输出里抄。

## 核心工作流：桥接宏炸了的诊断循环

背景事实（决定流程顺序的根因）：
1. 桥接（drawingPDMS/PdmsBridge）**每条命令跑完会删除 result.txt**——宏中途炸掉，结果文件要么不存在要么空。
2. PML 未捕获错误会弹**模态 Error 框**，卡死 exec 线程：后续所有 `$M`/`pdms_exec` 全部排队挂死（现象=客户端超时）。
3. 因此**错误文本在弹窗关闭前是唯一可读的**。先 dump 后 close，顺序不能反。

```
1) 症状：pdms_exec 客户端超时 / 结果文件空 / 桥接日志无 "got result"
2) Get-Process des                      → 拿 PID（可能多个 Design 窗口，先分清哪个桥在哪个进程！）
3) pdms_dialog_scan.ps1 <pid>           → 找 VIS=True 且标题=Error/Warning 的框（不是主窗口）
4) pdms_dialog_dump.ps1 <hwnd>          → 读错误文本，例如 "(47,50) CP: Meaningless symbol(s) in command line"
5) pdms_dialog_close.ps1 <hwnd>         → 点 OK
6) 按 pdms-pml 技能的坑清单定位 PML 根因（错误文本里的行/词是关键线索）
7) 修宏 → 重发 → rescan 确认无新弹窗
8) 无弹窗但仍超时 → 见 references/bridge-macro-triage.md（exec 线程被大 coll 占住 / 进程已死 / 端口错位）
```

错误文本 → 根因速查（2026-10-27 实测映射，详见 pdms-pml 坑 10–15）：

| 弹窗文本 | 根因 |
|---|---|
| `CP: Meaningless symbol(s) in command line` | 宏里有 PML 不认的符号：`<` 比较符（用 `.lt()`）、`CE $!var`（用 `!!ce = $!var`）、`--` 注释行、writefile `\|...\|` 内空格 |
| `CP: Syntax error` | 自创了不存在的语句（如 `select zone for /MDS`；SELECT 的实证形式见 pdms-pml） |
| `Current element does not have an attribute ...` | 读的属性在该类型上不存在（应套 handle ANY 默认值模板） |

## 坑（逐条现场验证，必读）

1. **.ps1 必须纯 ASCII**。PS 5.1 按 ANSI(GBK) 解码 .ps1，且 `Add-Type` 内嵌 C# 若含中文字面量会因编码错配变成乱码字符串 → C# 语法错误（本轮实测：`确定`/`是` 变成 `纭畾`/`鏄?` 直接 CS0116）。PDMS 按钮就是 OK/Yes，不需要中文；确要中文标签时用码点拼（`[char]0x786E`）。
2. **PS 5.1 无 `[uint]` 别名** → 用 `[uint32]` / `[uint32[]]`（本轮实测 CS 侧 P/Invoke 签名 `out uint` 必须配 `[uint32]` 传参）。
3. **委托引用要留在变量里**：`$cb = [DlgScan+EnumWindowsProc]{ ... }` 再传入 EnumWindows，否则 GC 回收委托 → 回调悬空。
4. **PDMS 的 Error 框是 WinForms**（类名 `WindowsForms10.Window.8.app.*`），**不是** `#32770`。按 `#32770` 过滤会漏掉全部——必须枚举所有顶层窗口按**标题**判。错误正文在 RichEdit 子控件，用 `SendMessageW(0x000D/WM_GETTEXT)` 取（`WM_GETTEXTLENGTH=0x000E` 先量长度）。
5. **先 dump 后 close**（见工作流）。先点掉 = 证据蒸发。
6. **关弹窗 ≠ 恢复**。被中断的 PML 宏已作废，结果文件为空；必须修根因重跑。关完 rescan 一次，防连环弹。
7. **别指望 DLL 内置弹窗杀手**：drawingPDMS.dll 的常驻 watcher 只关**标题白名单**（默认 "Error"）且只在自己进程内，本轮实测对 WinForms Error 框**不生效**（框存活 10+ 分钟）。PS 三件套不挑弹窗类、不依赖 addin 版本，任何会话都可用。
8. **BM_CLICK 是权威通道**：`PostMessage(按钮, 0x00F5, 1, 0)`。坐标点击 / UIA 顶层 / WM_CLOSE 对 WinForms 模态框均不可靠（10/15 ModalClose 实测结论 + 本轮 4 框复证）。

## 资产与证据

- 三件套脚本：`assets/scripts/pdms_dialog_{scan,dump,close}.ps1`（本轮验证版原样归档）。
- 诊断记录（本机可查）：`C:\PDMSSKILL\pdmsAgent\bridge\mds_p4*.pml`（探测宏）、`C:\PDMSSKILL\pdmsAgent\bridge\bridge.log`（命令/结果日志）。
- 桥接侧分诊（超时 vs 崩溃 vs 端口错位、mini 活体宏、日志判读）：`references/bridge-macro-triage.md`。

## 配套资源（标注合并来源）

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
- `pdms-uda-udet-mdb`：`(99,532)` 含点名诊断，弹窗防护同走本技能。
