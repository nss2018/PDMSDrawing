# PDMS 操作连接器（pdms-bridge）

让智能体（WorkBuddy / 任意 MCP client）**直接驱动正在运行的 AVEVA PDMS 12.1.SP4**：
发 PML、查数据库、创建图纸（REGI/DRWG/SHEE/VIEW/DLLB）、列绘制清单。
跳过"手动重启 PDMS + 截图反馈"的循环。

> 2026-10-14 更新：server.js 的 PML 生成器已全部换成 12.1.SP4 TTY 实测通过的语法
> （证据 `D:\pdmsAgent\view_api\`）；PdmsBridge.cs 已并入 `drawingPDMS` 工程并构建、
> 部署到 PDMS 安装目录；文件轮询（传输 B）判定不可行（PML 无 sleep/wait），TCP 为唯一路径。
> 详见 `ROADMAP.md`。

## 架构

```
[ 智能体 (WorkBuddy / Accio 等) ]
        │  MCP (stdio, JSON-RPC 2.0)
        ▼
[ pdms-bridge-mcp  server.js ]   ← Connector（本机 Node，无依赖）
        │  TCP 127.0.0.1:18753（请求/响应各一行 JSON）
        ▼
[ PdmsBridge.cs （drawingPDMS 插件内监听器）]
        │  CreateCommand("$M <bridge_dir>\cmd_N.pml").RunInPdms()
        ▼
[ PDMS 12.1.SP4 进程 ]
        └─ $M 执行 CP+PML 混合宏（全链路 TTY 实测通过）
           结果 → D:\pdmsAgent\bridge\result.txt（openfile/writefile/closefile）
                  C# 轮询读到 → 删除 → 经 TCP 回传 → MCP 返回智能体
```

结果文件是**唯一稳定回读通道**：进程内 RunInPdms 的 inline 变量回读不可靠
（viewTest [3] 证据），而 PML 落盘经全链路验证。

## 组件

| 文件 | 作用 |
|---|---|
| `pdms-bridge-mcp/server.js` | MCP 服务端（无依赖 Node），暴露 5 个工具（PML 生成器用 TTY 已验证语法） |
| `pdms-bridge-mcp/package.json` | 元数据 |
| `PdmsBridge.cs` | PDMS 插件内 TCP 监听器（net35，`#if PDMS_BRIDGE` 门控；已并入 drawingPDMS 工程） |
| `AGENTBRIDGE.PML` | ~~文件轮询兜底宏~~ **不可行（存档）**：PML 无 sleep/wait，无法实现目录轮询 |

## 工具（MCP tools）

- `pdms_status` — 检查桥接是否在线（PING）
- `pdms_exec` — 在 PDMS 执行一段 PML / PDMS 命令（核心；可 CP+PML 混合，经 `$M` 宏下发）
- `pdms_query` — 求值 PML 表达式，结果经结果文件返回。例：`!!ce.type` / `!!ce.vsca` / `!!ce.dbType`
- `pdms_create_drawing` — 建图纸链 REGI→DRWG→SHEE→VIEW(+VSCA/VTYP/SIZE/XYPS/DIR)→DLLB/IDLI/TRLB，带回读验证
- `pdms_list_drawlist` — 列指定 VIEW 的成员（drawing-rooted 引用 `/DRWG/SHEET/VIEW`）

## 启用（TCP，唯一可行路径）

1. 编译插件：`D:\NSS\source\repos\radwingPDMS\drawingPDMS\`
   （Debug/Release 均已定义 `PDMS_BRIDGE`；Debug 构建产物直接部署到
   `D:\AVEVA\Plant\PDMS12.1.SP4\drawingPDMS.dll`，2026-10-14 已构建完成）。
2. 重启 PDMS 并进入 DRAFT 模块；插件加载时监听器自动启动，
   确认 `D:\pdmsAgent\bridge\bridge.log` 出现 "PdmsBridge started"。
3. 启动 MCP 服务端（默认即 TCP 传输）：
   ```
   node D:/pdmsAgent/bridge/pdms-bridge-mcp/server.js
   ```
4. 在 WorkBuddy 注册该 Connector（见下），智能体即可 `pdms_exec` 驱动 PDMS。

## 在 WorkBuddy 注册为 Connector

编辑 `~/.workbuddy/mcp.json`，加入：
```json
{
  "mcpServers": {
    "pdms-bridge": {
      "command": "node",
      "args": ["D:/pdmsAgent/bridge/pdms-bridge-mcp/server.js"]
    }
  }
}
```
注册后在对话里启用该 Connector，智能体就会获得上述 `pdms_*` 工具，直接操作 PDMS。

## 环境变量

- `PDMS_BRIDGE_HOST` / `PDMS_BRIDGE_PORT`：TCP 地址（默认 127.0.0.1:18753）
- `PDMS_BRIDGE_TIMEOUT`：MCP 侧超时（默认 20000ms）
- `PDMS_BRIDGE_DIR`：桥接目录（默认 `D:/pdmsAgent/bridge`）
- `PDMS_BRIDGE_RESULT`：PML 写结果的文件路径（默认 `<DIR>/result.txt`）
- `PDMS_BRIDGE_TRANSPORT` / `QUEUE` / `DONE`：文件轮询路径的历史遗留，该路径不可行，勿用

## 自测（无需 PDMS）
```
cd pdms-bridge-mcp && node test_harness.js
```
会起 mock PDMS 监听器并跑通 initialize/tools/list/tools/call 全链路。

## 下一步

见 `ROADMAP.md`：出图（首跑验证清单）/ 材料表 / CASER2（待规格）/ ISO 轴测图（资源清单）。
