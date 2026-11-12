# 常驻看门狗：自动关闭 PDMS 弹窗（合并自 pdms-dialog-autoclose）

> 来源：原技能 `pdms-dialog-autoclose`（2026-11-08 合并入 `pdms-dialog`，原目录已归档到 `D:\repos\pdmsDrawing\skill-archive\pdms-dialog-autoclose-20261108`）。本文保留其全部实现细节，触发「自动关弹窗 / 看门狗 / dialog_watcher / ModalClose」时读本文；触发「弹窗炸了要读错误文本、诊断宏根因」时读主 `SKILL.md`。

「自己关 PDMS 弹窗」= 让一个后台进程 watch PDMS 的模态对话框，出现就自动点掉，避免人工干预、避免自动化管线被卡住。

> 来源：本项目 `bridge/dialog_watcher.py`、`bridge/ModalClose.cs`、`bridge/bridge_client.py`（`ensure_dialog_watcher`）、`bridge/pdms-bridge-mcp/server.js` 的弹窗风险注释。全部现场验证，不推测。

## 何时需要

- headless / 桥接批量跑宏（如 `pdms-bridge` 经 TTY / 进程内 Command 通道下发 `$M`）。
- PDMS 弹出**模态错误框**（`(41,12)` 等）会让命令处理器停等 OK → 后续所有 `$M` 全部堵死，关光弹窗前勿再下发。
- 现象：结果缺 `===EXP-END===`（宏中途中止，可能 CP 报错弹窗挂住线程）→ 先用 `EnumWins` 找 `PROC=des/dra` + `VIS=True` 的框并关掉。
- **根因预防优先**：宏里用 `handle ANY` 静默处理错误 + 结果文件回读（`pdms-pml` 坑8/9、`pml_util.js` 的「无弹窗、无挂线程、无 fatal 连锁」模式），能从根因避免弹窗；本技能的 OS 侧看门狗是**兜底**。

## 实现一（推荐常驻）：`dialog_watcher.py`

- **原理**：轮询 PDMS 进程（`design.exe`/`dra.exe`/`mon.exe`/`pdms.exe`/`pdms*`）的顶层**可见** `WS_POPUP` 窗口（跳过主框架与 `Program Manager`/`Windows Input Experience`）；子控件文本命中 OK 类标签即判定为可关弹窗。
- **关闭三级递进**（对自定义控件也安全）：
  1. `PostMessageW` 发 `Enter` 到对话框（默认按钮，无前台抢占）；
  2. 仍活着 → 真实鼠标 `mouse_event` 点击 OK 按钮矩形中心；
  3. 仍活着 → `WM_CLOSE` 兜底。
- **命中标签**：`OK/ok/Ok/confirm/CONFIRM/yes/YES/Yes` + 中文 `确定`/`确认`（中文用码点存，文件保持 ASCII）。
- **依赖**：纯 `ctypes` + 标准库，**无需 pywin32**，Python 3 即可。
- **单实例**：`dialog_watcher.pid` 锁；日志 `dialog_watcher.log`。
- **启动**：
  ```sh
  # 常驻（阻塞运行）
  python D:/pdmsAgent/bridge/dialog_watcher.py
  # 幂等拉起：已运行则跳过，否则 detached 后台启动（桥接用这个）
  python D:/pdmsAgent/bridge/dialog_watcher.py --ensure
  ```
- **停止**：结束进程即可（会自动删 pidfile）。
- **本机实体路径（2026-10-28 复核）**：`D:\repos\pdmsDrawing\bridge\dialog_watcher.py`；`ModalClose.cs` + `ModalClose.exe` 在 `D:\repos\bridge\`。C 盘可用的是 `pdms_vision_popups.py`、`close_pdms_popup.py`、`vision_watcher\pdms_error_watcher.py`（需 pywinauto）、`vision_watcher\pdms_dialog_agent.py` + `launch_agent.py`。

## 实现二（专用）：`ModalClose.exe`（`ModalClose.cs`）

- **用途**：关**指定进程**所有标题 `== "Error"` 的模态对话框（默认 `dra.exe`，可传参换进程名）。
- **原理**：`EnumWindows(pid)` → `title=="Error"` → `EnumChildWindows` → 类 `WindowsForms10.BUTTON` → `SendMessage(BM_CLICK)`。
- **验证结论（2026-10-15）**：坐标点击 / UIA 顶层均不可靠，`BM_CLICK` 为权威通道。按钮优先级 `OK`/`确定`/`确认`，兜底第一个按钮；最多 10 轮、每轮 600ms。
- **用法**：
  ```sh
  ModalClose.exe            # 关 dra.exe 的 Error 框
  ModalClose.exe mon        # 关 mon.exe 的 Error 框
  ```

## 桥接集成

- `bridge_client.py` 的 `ensure_dialog_watcher()`：每次发命令前 `Popen([..., dialog_watcher.py, "--ensure"], CREATE_NO_WINDOW)` 拉起看门狗 → 桥接自动化天然带弹窗兜底。
- `server.js` 在宏执行前后把「弹窗挂住」列为风险，结果缺 `===EXP-END===` 时提示先 `EnumWins` 找 `PROC=des/dra` 框关掉。

## 注意事项

- 看门狗**只关带 OK/确定/确认 的 `WS_POPUP` 弹窗**，不会误关设计主窗口 / 视图。
- 依赖 PDMS 进程名命中（`is_pdms_pid`）；若自定义进程名不含 `pdms` 需改白名单。
- 看门狗是兜底，**第一道防线是宏侧 `handle ANY` 静默 + 结果文件回读**（不弹窗、不挂线程）；两者配合最稳。
- `dialog_watcher.py` 与 `ModalClose.exe` 可并存：前者守通用弹窗，后者专清 `Error` 框。
- **与交互式三件套的关系**：主 `SKILL.md` 的 scan/dump/close 是「炸了要读错误文本再关」的精确打击版；本文件是「白名单标题自动关」的常驻版。两者可并存，长批跑前先 `--ensure` 拉起看门狗，出错时仍按主 SKILL.md 的 **先 dump 后 close** 顺序取证。

## 关联技能

- `pdms-bridge`（AI 驱动 PDMS：TTY / 进程内 Command 通道下发宏，弹窗挂住即卡死）。
- `pdms-run-macro`（宏文件加载到窗口执行、不报错指南）。
- `pdms-pml`（实战避坑：坑8 `handle ANY` 就近保护、坑9 结果文件回读——根因避免弹窗）。
