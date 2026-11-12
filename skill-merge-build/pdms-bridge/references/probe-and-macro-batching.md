# 会话探活 / 端口验证 / 长宏防阻塞（合并自 pdms-bridge-probe）

> 来源：原技能 `pdms-bridge-probe`（2026-11-08 合并入 `pdms-bridge`，原目录已归档到 `D:\repos\pdmsDrawing\skill-archive\pdms-bridge-probe-20261108`）。本文保留全部工作流、Fallback、Pitfalls。触发「端口扫不出来 / Get-NetTCPConnection 空 / 长宏占死会话 / 结果文件只有 BOM」时读本文；连接器架构与工具清单读主 `SKILL.md`。

> 来源（现场）：Accio Work 侧 SWX/WPC 建库现场沉淀（2026-10-28 发布到 WorkBuddy 技能区）。

## Workflow

1. **端口存活验证（绕过系统命令异常）**：当 `Get-NetTCPConnection` 返回空或异常时，使用 Python `socket.connect_ex` 对已知桥接端口范围（如 18750-18760）进行批量扫描。识别出处于 `LISTENING` 状态的端口及其对应的进程 ID（PID），确认 `des.exe` (Design) 或 `dra.exe` (Paragon) 是否已正确加载桥接 DLL。

2. **会话身份与状态识别**：向识别出的活跃端口发送轻量级 Ping 指令（如 `{"kind":"ping"}`）或查询当前 CE 路径。根据返回结果区分 Design 会话（通常处理 /MDS, /AM07）与 Paragon 会话（通常处理目录结构），并确认会话是否处于空闲状态。

   > 与主 SKILL.md「多模块并存」章节配合：现方案端口按进程名确定性固定（des→18753 / par→18754 / dra→18755 / mon→18756 / iso→18757），并有 `<PDMS_BRIDGE_DIR>/instances/<module>_<pid>.json` 发现清单；**优先读发现清单选端口**，Socket 扫描只作为清单缺失或 `Get-NetTCPConnection` 异常时的兜底。

3. **宏执行防阻塞策略**：避免下发包含全量 `coll all` 或复杂循环的长宏。将任务拆分为多个短宏（如先枚举成员名，再按名点取属性），确保每个宏能在数秒内完成并触发 `closefile`。若必须执行长宏，需监控结果文件的大小变化，而非仅依赖 `RunInPdms` 的同步返回。

4. **结果文件回读与校验**：宏执行后，立即读取指定的结果文件（如 `.txt` 或 `.csv`）。校验文件是否包含预期的结束标记（如 `===EXP-END===`）及非空数据。若文件仅为 BOM 头或极小，判定为宏中途崩溃或被弹窗挂起。

## Suggestions

- 在桥接客户端集成 `dialog_watcher.py` 作为常驻看门狗，自动关闭 PDMS 弹出的模态错误框，防止会话因等待用户点击而假死（详见 `pdms-dialog` → `references/auto-close-watchdog.md`）。
- 对于需要获取大量成员属性的场景，优先使用 PML 的 `do indices` 配合 `CE` 跳转读取，而非一次性收集所有对象引用，以减少内存占用和超时风险。
- 部署新版本的桥接 DLL 前，必须确保所有 `des.exe` 进程已完全退出，否则文件锁定会导致部署失败且无明确报错。

## Fallback / Edge Cases

- **Step 1**：若 Socket 扫描发现端口开放但 Ping 无响应，可能是桥接 DLL 内部线程死锁。此时需尝试重启对应的 PDMS 模块，或检查 `bridge.log` 中的最后一条错误记录。也可能是**模态弹窗占死命令线程**（`pdms-dialog` 的 scan/dump/close 循环）。
- **Step 3**：若短宏拆分后仍出现 `RunInPdms failed`，检查宏中是否包含 `mess` 或绝对盘符路径，这些在 TTY 通道下可能被 `^^` 注入破坏。改用环境变量路径或纯 PML 逻辑。

## Pitfalls

- Step 1：严禁依赖 `Get-NetTCPConnection` 判断桥接状态，该命令在特定沙箱或权限下恒返回 0 条记录，导致误判桥接未启动。
- Step 3：不要在同一个 Design 会话中连续下发两个重型宏。第一个宏若未完全释放资源（如文件句柄或 CE 锁定），第二个宏会直接报 `RunInPdms failed`。这与 `pdms-pml` 坑 34「`$M` 同步独占会话」是同一根因。
- Step 4：结果文件若只有 3 字节（UTF-8 BOM），说明宏在第一条 `writefile` 之前就已崩溃。需检查宏首行的语法及 CE 初始位置是否正确；也可能是 `writefile` 到 `closefile` 才落盘，别急着判死（坑 34）。
- Step 3（止损线，2026-10-28 定案）：**"猜语法"式构法批次必须设止损**。CATR/DBL 引用含点目录名（`/PDMSPIPE.CATA-ANSI`）的 A~J 十种构法（全点分 / 去 CATA 前缀 / 点斜混合 / 段内斜杠 / 上下文 `CATA` 行 + 相对引用）系统性全弹，结案为 **DBL 文法结构性限制**——点号被当 UDA/UDET 属性引用符 → `(99,532) Undefined UDA or UDET name`。同类批次跑到 3~4 种仍全弹就停手定性，别再烧会话、弹窗清理与批次时间；判定"是文法限制还是写法问题"的判据是**同宏内无点名的引用是否畅通**（畅通即含点名病）。详见 `pdms-uda-udet-mdb` 与 `pdms-pml` 坑 35。
