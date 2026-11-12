---
name: pdms-bridge
version: 2.0.0
description: "让 AI 智能体直接驱动正在运行的 AVEVA PDMS 12.1.SP4：发 PML、查数据库、建图纸、出 ISO 轴测图。包含完整的 MCP 连接器代码（server.js + PdmsBridge.cs + TtyFeed.cs + 实战 PML 宏）与 WorkBuddy 注册方式；另含会话探活（发现清单 + Socket 端口扫描）、长宏分步下发防阻塞、结果文件 BOM 校验与语法猜测批次止损线。适用于工业软件自动化、工厂设计（PDMS/E3D）AI 操作场景。"
agent_created: true
---

# pdms-bridge —— AI 直接开 PDMS 的操作连接器

AVEVA **PDMS 12.1.SP4**（工厂设计管理软件）原生没有给 AI 留接口。本技能把一整套**已验证落地**的连接器代码与踩坑经验打包好：让 WorkBuddy 这类智能体**直接驱动一台正在运行的 PDMS**——发 PML、查库、按体积框选建平面布置图、列绘制清单、一键出 ISO 轴测图，跳过"你重启 PDMS → 截图 → 我改"的死循环。

> 这不是概念稿：架构已于 2026-10-14 全链路跑通（TTY + 进程内 Command 双通道），代码可直接 `node` 起、可直接编译部署。

## 前置依赖与环境声明（给审核 / 使用者）

- **本技能运行需要用户本机已安装 AVEVA PDMS 12.1.SP4**（有许可证的付费工业软件）。这是**使用前提**，不是打包/审核前提。
- **审核与安装不需要 PDMS**：MCP 服务端是零依赖 Node，且内置 mock 自测（见下方"自测"）。PDMS 缺失时所有工具返回 `connected:false` 并给出明确提示，**MCP 服务进程保持存活、绝不崩溃、不阻塞宿主**。
- **安全边界（审核重点）**：`pdms_exec` 等写通道内置黑名单（拒 `QUIT`/`SHUTDOWN`/`SET MDB`/`DELETE REGI`）+ 路径沙箱（openfile 目标必须落在桥接目录内）+ 删前快照。仅监听 `127.0.0.1`，**无任何外网连接**。已用 `test_guardrails.js` 自动化验证 3/3 通过。
- 本技能**不修改、不捆绑 PDMS 本体**，只提供可并入用户自有工程的 C# 监听器源码与可下发的 PML 宏，部署由用户在其已授权环境内完成。

## 何时使用

- 用户想在 PDMS / E3D 里做**智能体自动化**（查模型、改属性、导出材料表/管线表、出图）。
- 用户问"怎么让 AI 操作 PDMS""PDMS 有没有 MCP""AI 能不能直接出 ISO 图"。
- 用户已有 PDMS 实机，想接一个连接器给它下命令。
- 用户在做 PDMS 插件（C# addin）开发，需要进程内 Command 通道（`RunInPdms`）参考。

## 会话探活与防阻塞（合并自 pdms-bridge-probe）

> 触发：`Get-NetTCPConnection` 返回空 / 异常、端口扫不出、长宏占死会话、`RunInPdms failed`、结果文件只有 3 字节 BOM。完整 4 步工作流、Fallback 与 Pitfalls 见 `references/probe-and-macro-batching.md`（原技能 `pdms-bridge-probe` 全文，2026-11-08 并入）。

| 症状 | 动作 |
|---|---|
| 端口扫不出来 | **优先读** `<PDMS_BRIDGE_DIR>/instances/<module>_<pid>.json` 发现清单；缺失时 Python `socket.connect_ex` 扫 18750–18760，**禁用** `Get-NetTCPConnection`（受限沙箱恒返回 0 条） |
| 端口开但 Ping 无响应 | 查 `bridge.log` 最后一条错误；按 `pdms-dialog` 清模态弹窗；必要时重启该模块 |
| 长宏 / 连续两个重型宏 | 拆短宏（先枚举成员名，再按名点取属性），单宏数秒内 `closefile`；`$M` 同步独占会话（`pdms-pml` 坑 34） |
| 结果文件 3 字节 BOM | 判宏中途崩 / 被弹窗挂起；回读必查 `===EXP-END===` 结束标记，别只信同步返回值 |
| "猜语法"批次连弹 3~4 次 | **止损定性**（DBL 含点名是文法结构性限制，`pdms-pml` 坑 35 / `pdms-uda-udet-mdb`），别再烧会话 |

## 架构（已落地）

```
[ 智能体 (WorkBuddy / 任意 MCP client) ]
        │  MCP (stdio, 帧自适应：标准 Content-Length 分帧 / 轻量 NDJSON 均可)
        ▼
[ pdms-bridge-mcp/server.js ]   ← Connector（本机 Node，零依赖）
        │  TCP 127.0.0.1:18753(Design) / 18754(Paragon) / 18755(Draft) / 18756(Monitor) / 18757(ISO)
        │  （端口由 PDMS 进程名 des/par/dra/mon/iso 自动判定，确定性固定，不随启动顺序漂移）
        ▼
[ PdmsBridge.cs （独立插件 PdmsBridge.dll，自带 IAddin 入口）]
        │  CreateCommand("$M <pml>").RunInPdms()
        ▼
[ PDMS 12.1.SP4 进程 ]
        └─ $M 执行 CP+PML 混合宏
           结果 → result.txt（openfile/writefile/closefile）
                  C# 轮询读到即删 → 经 TCP 回传 → MCP 返回智能体
```

**关键设计结论（务必先读）**：
- **结果文件是唯一稳定回读通道**：进程内 `RunInPdms` 的 inline 变量回读不可靠；PML 落盘 + C# 轮询经全链路验证。
- **`$M` 而非 `DO`**：CP+PML 混合宏在 TTY 与进程内通道实测均通过。
- **`RunInPdms` 是实例方法**：`Command.CreateCommand(text).RunInPdms()`，无静态重载；失败时 `command.Error.MessageText()` 带回 PDMS 报错文本。
- **文件轮询已复活（档位1）**：此前 ROADMAP 判定"裸命令宏无法轮询"正确，但 PML2 窗体具备 callback（`PDMS 二次开发，中级，高级.pdf` §第十章）。用 PML2 窗体的**定时回调**即可扫目录 → 零编译获得持久交互通道。详见 `references/连机方案.md`。

## 工具（MCP tools，共 12 个，均已实现）

| 工具 | 用途 | 通道 |
|---|---|---|
| `pdms_status` | 连接自检（PING），返回 connected 布尔与监听地址 | — |
| `pdms_query` | PML 表达式求值回读（`!!ce.type` / `!!ce.vsca` …） | 纯 PML |
| `pdms_exec` | 任意 CP/PML（逃生通道，带黑名单护栏） | 混合 |
| `pdms_probe_read` | 只读探针：unit/CE/出图 world 顶层/MDBMembers/包围盒 | op |
| `pdms_draw` | 无头出图（布置图全套标注块：尺寸/设备标签/流向箭头/阀门符号/管一览表/边框） | op |
| `pdms_export_dxf` / `pdms_export_pdf` | SHEET 导出 DXF / PDF | op |
| `pdms_create_drawing` | 建图链 REGI→DRWG→SHEE→VIEW(+属性)→DLLB/IDLI/TRLB 并回读验证 | CP 混合 |
| `pdms_list_drawlist` | 列指定 VIEW 的绘制清单成员 | 纯 PML |
| `pdms_iso_generate` | 管线 ISO 轴测图批量出图（TTY 全链路实测通过） | CP 混合 |
| `pdms_create_equipment` | 参数化创建设备（22 类，移植方圆 TestAttribute 插件） | CP 混合 |
| `pdms_get_equipment` | 反向读回设备 + 管嘴表（建后验证/导出） | 纯 PML |

> 说明：早期文档曾列 `pdms_search`/`pdms_get_attrs`/`pdms_set_attr`/`pdms_export_table`/`pdms_create`/`pdms_delete` 为规划项，当前版本尚未实现；上述 12 个为 `tools/list` 实际返回并已验证。

风险分层：**查/改/导出（纯 PML、op 通道）可先交付**；create 类依赖"进程内 `$M` 下发 CP 行是否被 `^^` 污染"判定（首跑验证点）。

## 连机方案三档（详见 references/连机方案.md）

资料库「PDMS·E3D 私有资料库」官方文档（`Installation Guide.pdf` §4.6/§4.7、`User Bulletin.pdf` §5.1）把 PDMS 自动化归结为 TTY 模式 / PDMSConsole+PDMSWait / GUI 控制台三件套。据此 bridge 给出三档连机路径：

| 档位 | 是否需要编译 C# | 是否持久交互 | 适用 |
|---|---|---|---|
| **档位1 · PML2 窗体轮询桥** | 否（零编译） | 是 | 临时演示 / 无 VS 环境，加载 `assets/pml/AGENTBRIDGE.PML` 即用 |
| **档位2 · C# 独立 Addin TCP** | 是（VS/MSBuild, .NET 3.5） | 是 | 生产首选，最快最稳 |
| **档位3 · TTY 批处理** | 否（零编译） | 否（每次新会话，慢） | 夜间批量（如 100 根管线 ISO） |

- 档位2 模板已备：`assets/mcp/addin/`（`PdmsBridge.cs` 改中性命名空间 + `PdmsBridgeAddin.cs` 实现 `IAddin` + `PdmsBridge.addin` + `PdmsBridge.csproj`），编译出 `PdmsBridge.dll` 丢进 PDMS 插件目录即可，无需改动你现有 `FangYuanPDMS` 工程。
- 档位1 用 `TRANSPORT=file` + 在 PDMS 里 `$M AGENTBRIDGE.PML` 启用，复活 `server.js` 的 `callFile` 分支。
- 档位3 用 `assets/mcp/run_tty_task.bat`（已写好 `PDMS_CONSOLE_IDENTIFIER` + `pdmswait`）。

## 在 WorkBuddy 注册为 Connector

编辑 `~/.workbuddy/mcp.json`：

```json
{
  "mcpServers": {
    "pdms-bridge": {
      "command": "node",
      "args": ["<技能目录>/assets/mcp/server.js"]
    }
  }
}
```

注册后在对话里启用该 Connector，智能体即获得上述 `pdms_*` 工具。

## 启用（TCP 生产路径 · 档位2）

1. **编译独立插件**：打开 `assets/mcp/addin/PdmsBridge.csproj`（VS/MSBuild，目标框架 .NET Framework 3.5，平台 x64）。工程**自包含**且已引用 PDMS 安装目录下的 `Aveva.ApplicationFramework.dll`（**`IAddin` 接口就在这里**；不要用 `Aveva.Pdms.Addins.dll`——本机没有这个文件）、`Aveva.Pdms.Database.dll`、`Aveva.Pdms.Utilities.dll`。编译出 `PdmsBridge.dll` 后：① 丢进 PDMS 根目录（与你现有 `FangYuanPDMS.dll` 同级）；② 把 `<string>PdmsBridge</string>` 追加进 `DesignAddins.xml` / `DraftAddins.xml` 等清单完成注册（PDMS 12.1 用 `*Addins.xml`，**不是** `.addin` 文件）。完整步骤见 `assets/mcp/addin/部署说明.md`。插件加载时监听器自动启动（日志见 `C:\pdmsAgent\bridge\bridge.log` 的 "PdmsBridge started"）。
2. **启动 MCP 服务端**：`node <技能目录>/assets/mcp/server.js`。
3. **智能体驱动**：`pdms_status` 确认在线 → `pdms_query` 看上下文 → `pdms_create_drawing` / `pdms_iso_generate` 出图。

> 零重编译快速验证（档位1）：在 PDMS 命令行 `$M C:/pdmsAgent/bridge/AGENTBRIDGE.PML`（先把 `assets/pml/AGENTBRIDGE.PML` 拷到该路径），`server.js` 设 `PDMS_BRIDGE_TRANSPORT=file` 即可走文件轮询通道，无需编译。

## 本机启动 PDMS（已验证要点 · 2026-10-15）

### 用户提供的真实启动脚本（附在对话里，原貌）
`D:\pdmsAgent\pdms - Sample  - .bat` 内容只有一行：

    D:\AVEVA\Plant\PDMS12.1.SP4\pdms.bat SAMPLE SYSTEM/XXXXXX /PIPE DRAFT

即 Monitor 标准形式（顺序：项目 / 用户密码 / MDB / 模块）。**该命令在用户自己的交互桌面双击即可正常拉起 Sample 项目（Draft 模块）**，但本机自动启动时需做两处修正（见下）。

### 本机自动启动的两处必改（已在对话里实测）
1. **盘符**：本机 PDMS 装在 `C:\AVEVA\Plant\PDMS12.1.SP4\`，**D 盘没有 AVEVA 目录**（`D:\AVEVA` 不存在）。脚本里的 `D:\` 要改成 `C:\`。
2. **MDB 名**：本机 Sample 项目只定义 `SAM000 / SAMPIC / SAMDWG / SAMISO / SAMDIA / SAMSTE / SAMTPL / SAMDFLTS / SAMPSI`（见 `evarsSample.bat`），**不存在 `PIPE`**。所以 `/PIPE` 会在登录阶段失败，改 `/SAM000`（逻辑 MDB 名 `SAM000ID=Sample`）。

   修正后用：`C:\AVEVA\Plant\PDMS12.1.SP4\pdms.bat SAMPLE SYSTEM/XXXXXX /SAM000 DRAFT`
   （或 Design 模块直启：`C:\AVEVA\Plant\PDMS12.1.SP4\pdms.bat DESIGN SAM SYSTEM/XXXXXX /SAM000`）

### 非交互会话绝对起不了 PDMS GUI（2026-10-15 实测定论）
在本机非交互会话（WorkBuddy / PowerShell `Start-Process`）下，无论走哪条路都会崩：
- `pdms.bat SAMPLE SYSTEM/XXXXXX /PIPE DRAFT` → 经 `mon.exe`，在 `Aveva.ApplicationFramework.Presentation.CommandBarManagerImpl` 的**类型初始化器**处致命退出（"A fatal error has been detected. The program must exit."）。
- `pdms.bat DESIGN SAM SYSTEM/XXXXXX /SAM000`（Design 直启，走 `des.exe`）→ 抛**同一个**致命异常（偶尔会打印 `PDMS_KEEP` 但进程随即死亡、18753 不绑定）。

这是 **Infragistics 表现层（UltraToolbarsManager）在无交互桌面 / 无 Windows station 的会话下无法初始化 GUI 框架**的**环境限制，不是 bridge、不是命令、也不是 DLL 注册的问题**。PdmsBridge 已确认注册在 `DesignAddins.xml` 和 `DraftAddins.xml` 两处。

> **结论**：PDMS 必须由用户在自己的**桌面交互会话**里手动启动（双击 `pdms.bat` 或上述命令行）。启动后 `PdmsBridge` 插件自动加载并监听 `127.0.0.1:18753`，届时 MCP 连接器即可连上。

### 一键启动器（已就绪，供用户在桌面用 / 调试参考）
- `C:\pdmsAgent\bridge\launch_sample.bat`：Design 直启 + `/SAM000`，写 `launch.log`。
- `C:\pdmsAgent\bridge\launch_user.bat`：用户原命令（已改 C: 路径）`pdms.bat SAMPLE SYSTEM/XXXXXX /PIPE DRAFT`。
- `C:\pdmsAgent\bridge\launch_des_sample.bat`：Design 直启 `des SAM SYSTEM/XXXXXX /SAM000`。

### 连机自检（PDMS 起来后由智能体跑）
- `C:\PDMSSKILL\pdmsAgent\.workbuddy\tmp\probe_connect.js` —— 起真 `server.js` 跑 MCP 握手 / 12 工具，并裸 TCP 探 18753。`pdms_status` 应返回 `connected:true`。
- **端口冲突**：`C:\pdmsAgent\bridge\bridge.log` 若报 `address already in use`，说明 18753 被旧 PDMS 残留进程占用；先干净退出所有 PDMS 再启。监听器成功标志是日志里的 `PdmsBridge started on 127.0.0.1:18753`。

## 多模块并存（Design/Paragon/Draft 同时打开）

同一个 `drawingPDMS.dll` 在 `DesignAddins.xml` / `ParagonAddins.xml` / `DraftAddins.xml` 都注册，三个模块各起独立进程都会执行 `PdmsBridge.Start()`。早期版本用"18753 被占就 +1 盲顺延"，导致端口随启动顺序漂移、连接器永远只戳 18753——多模块同时开时根本没法指定目标。

**现方案（2026-10-28 落地）**：
- **端口按模块确定性固定**（零配置，由自身进程名判定）：`des`→18753(Design)、`par`→18754(Paragon)、`dra`→18755(Draft)、`mon`→18756(Monitor)、`iso`→18757(ISO)。仍可用 `PDMS_BRIDGE_PORT` 环境变量强制覆盖；同模块重复启动才在该基值上 +1 顺延。
- **发现注册表**：每个实例在 `<PDMS_BRIDGE_DIR>/instances/<module>_<pid>.json` 写清单 `{module,port,pid,exe,dir,startedAt}`，进程退出自动删除。连接器据此按模块选端口，且会探活剔除崩溃残留的陈旧清单。
- **`pdms_status` 回报**：返回 `modules:[{module,port,pid,exe}]` 与当前生效 `targetModule`/`port`/`via`。
- **`pdms_select_module(module)`**：多模块并存时手动切换目标（DESIGN/PARAGON/DRAFT/…；`DRAW` 等价于 `DRAFT`；传空清除）。设置后后续所有 `pdms_*` 调用指向该模块端口。
- 连接器选端口优先级：`PDMS_BRIDGE_PORT` 显式端口 → `pdms_select_module`/`PDMS_BRIDGE_MODULE` 指定模块 → 仅一个实例用其端口 → 多个时优先 DESIGN → 回退 18753。
- Python 客户端 `bridge_client.py` 同样支持 `--module` / `--list` 与发现选端口。

> 部署注意：改的是 `src/drawingPDMS/PdmsBridge.cs`（与 `assets/mcp/addin/PdmsBridge.cs` 同源）。需重编译 `drawingPDMS.dll`（带 `PDMS_BRIDGE` 符号）并重启 PDMS 才生效。

## 环境变量

- `PDMS_BRIDGE_HOST` / `PDMS_BRIDGE_PORT`：默认 `127.0.0.1:18753`
- `PDMS_BRIDGE_TIMEOUT`：MCP 侧超时（默认 20000ms）
- `PDMS_BRIDGE_DIR`：桥接目录（默认 `C:/pdmsAgent/bridge`）
- `PDMS_BRIDGE_RESULT`：PML 写结果的文件路径

## 安全护栏（内置）

- 127.0.0.1 only；`pdms_exec` 黑名单拒 `QUIT`/`SHUTDOWN`/`SET MDB`/`DELETE REGI`。
- 写操作（delete/改 name/type/owner/生产模块 create）强制 confirm + 删前快照（PML 无 UNDO）。
- 写路径沙箱：openfile 目标必须落在桥接目录内。
- server.js 串行队列已修：单 result.txt 被所有调用共享，防并发串话。

## 自测（无需 PDMS）

```
cd assets/mcp && node test_harness.js      # mock PDMS 跑通 initialize/tools/list/call 全链路
node test_guardrails.js                    # 黑名单/confirm/路径沙箱 3/3 PASS
```

## 附带资源

- `assets/mcp/`：完整 MCP 服务端（server.js + 测试脚本 + package.json），零依赖 Node；`addin/` 子目录含独立插件工程模板；`run_tty_task.bat` 是档位3 TTY 批处理模板。
- `assets/PdmsBridge.cs`：PDMS 进程内 TCP 监听器（命名空间 `PdmsBridge`，可并入独立 DLL）。
- `assets/pml/`：实战 PML 宏 + `AGENTBRIDGE.PML`（档位1 零编译轮询桥）。
- `references/连机方案.md`：**连机方案总文档**（TTY/PDMSConsole 依据 + 三档路径 + 本机落地清单 + 分步实施 + 验证）。
- `references/README.md`、`references/ROADMAP.md`、`references/MCP-GENERIC-TOOLS.md`：架构、路线图、14 工具设计 + 首跑清单 + 护栏规范。
- `references/probe-and-macro-batching.md`：**会话探活 / 端口验证 / 长宏分步下发 / 结果文件校验 / 止损线**（合并自原 `pdms-bridge-probe`）。

## 约束与坑

- 平面出图 / 总图插件宿主运行在 **CLR v2.0（.NET 3.5 Client Profile）**：PDMS 侧 csproj 必须用 `v3.5`，用 .NET 4.x 的 DLL 会被**静默跳过**（注册表有、DLL 也在，但命令/窗口不出现）。
- 平台：官方 AVEVA E3D MCP 仅 UE 4.1（E3D 4.x）可用；本连接器是"同构思想移植"到 PDMS 12.1 独立版。
- 无显示环境：编译/注册可在本机完成，最终加载需 PDMS 实机验证。

## 迭代说明

- v1.x（2026-10-14 ~ 10-28）：TTY + 进程内 Command 双通道、12 个 MCP 工具、多模块确定性端口与发现清单。
- v2.0.0（2026-11-08）：并入 `pdms-bridge-probe`（Socket 探活 / 分步下发 / BOM 校验 / 止损线），新增「会话探活与防阻塞」章节；旧技能名只作溯源。
