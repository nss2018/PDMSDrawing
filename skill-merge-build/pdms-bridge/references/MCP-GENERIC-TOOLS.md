# PDMS 通用 MCP 工具集设计 —— 借鉴 AVEVA 官方 E3D MCP（剑维，UE 4.1）

日期 2026-10-14 ｜ 状态：待首跑实测 ｜ 目标：PDMS 12.1.SP4 独立版
生产路径 = 进程内 Command 通道（PdmsBridge.dll RunInPdms），与 ROADMAP.md 一致。

## 1. 借鉴对象与架构同构性

剑维（AVEVA 中国）官方随 **AVEVA Unified Engineering 4.1** 发布 E3D MCP，能力基线（公开口径）：

- 在 E3D 内开启 MCP Server，提供**通用工具**（不是场景化工具海）
- 第三方 AI 经**标准协议 + 安全连接**接入（WorkBuddy 只是演示客户端）
- 能力 = **数据库增删改查 + 软件操作自动化**
- 官方工具清单未公开（视频号文章已删，docs.aveva.com 暂无条目）
- 佐证：AVEVA 社区 2026-07 帖（community.aveva.com/aveva-engineering/pdms-e3d-forum/f/aveva-everything3d/100958）官方确认 E3D MCP 在路线图

**我们的架构已同构，无需重做**，只补工具集 + 护栏：

| 官方概念 | 我们的实现 | 状态 |
|---|---|---|
| E3D 内 MCP Server | PdmsBridge.dll（PDMS 进程内监听 TCP 127.0.0.1:18753，RunInPdms） | 已落地 |
| 标准协议端点 | server.js（MCP stdio JSON-RPC） | 已落地 |
| 通用工具 | 现有 8 工具（status/exec/query/create_drawing/list_drawlist/iso_generate/**create_equipment/get_equipment**） | 本文扩展到 14 |
| 数据库增删改查 | search/get_attrs/set_attr/create/delete（新增） | 待首跑 |
| 软件操作自动化 | pdms_exec 保留为兜底逃生通道 + 护栏 | 增强 |
| 安全连接 | 127.0.0.1 + 黑名单 + confirm + 写路径沙箱 + 串行队列 | 本文补齐 |

## 2. 设计原则

1. **通用原语优先**：≤12 个工具；场景（材料表/管线表/出图）由 AI 用原语组合，不单独建工具（官方同款思路）。
2. **结果文件是唯一回读通道**（已验证）：所有新工具复用 `pmlResultOpen()` 模板（write/overwrite 双开 + handle ANY，缓冲至 closefile）。
3. **未验证语法必须标注 `[未验证]` 并进首跑清单**（§7）；写操作先在草稿库/测试模块跑，生产库只读。
4. **持久化边界**：DRAFT 会话元素可能落在 PADD 会话级存储（已验证）；演示与首跑一律用草稿 REGI 或用户指定测试模块。
5. **服务端串行**：现有 server.js 对 RESULT_FILE 无并发保护（见 §4.6 修复）。

## 3. 工具清单（现有 5 + 新增 6 = 11）

| # | 工具 | 用途 | PML 性质 | 状态 |
|---|---|---|---|---|
| 1 | pdms_status | 连接自检 | — | 已有 |
| 2 | pdms_exec | 任意 CP/PML（逃生通道） | 混合 | 已有 + 护栏 |
| 3 | pdms_query | 表达式求值回读 | 纯 PML | 已有 |
| 4 | pdms_create_drawing | 出图链高层封装 | CP 混合 | 已有 |
| 5 | pdms_list_drawlist | 图元清单 | 纯 PML | 已有 |
| 6 | **pdms_search** | 模块内搜元素（类型/属性过滤） | 纯 PML | 新增 |
| 7 | **pdms_get_attrs** | 读元素属性值 | 纯 PML | 新增 |
| 8 | **pdms_set_attr** | 改元素属性值（带回读） | 纯 PML | 新增 |
| 9 | **pdms_export_table** | **属性表导出 CSV→xlsx（材料表/管线表）** | 纯 PML | 新增 |
| 10 | **pdms_create** | 通用新建元素 | CP 混合 | 新增 |
| 11 | **pdms_delete** | 删除元素（confirm + 快照） | CP 混合 | 新增 |
| 12 | **pdms_iso_generate** | **管线 ISO 批量出图**：pipes 数组 → 逐管线 CE→ISODRAFTMODE→options→DETAIL→isodraw 回读→EXIT，返回每管线 status/plots/plotfile；optionsFile 可切 DXF（`File AND DXF`） | CP 混合 | **2026-10-14 新增，PML 与 TTY 验证链 v10 逐行同构（离线自测 test_iso_tool.js PASS，待桥接首跑）** |

风险分层（关键结论）：**#6–9 全是纯 PML**，不依赖 ROADMAP §2-3 的"进程内 $M 下 CP 行是否被 `^^` 污染"判定；#10–11 依赖该判定。→ **用户的三项诉求（操作模型/设置属性/导出表格）里，查/改/导出三个低风险项可以先交付**。

### 3.1 pdms_search
args: `ref`（模块，如 `/PROJ/SYS/1`）、`type`（可选，如 VALVE）、`limit`（默认 200）
返回：元素引用串列表（AI 可直接接力 get_attrs/set_attr/delete）。

```
pmlResultOpen()
!N1 = 0
!TR1 = false
do !E1 element /PROJ/SYS/1          -- [未验证·首跑#7] 模块遍历
  !T1 = !E1.type                     -- [未验证·首跑#7] 句柄 .type
  if (!T1 = 'VALVE') then            -- type 省略 → 去掉该过滤；[未验证] 字符串=比较
    if (!N1 < 200) then              -- PML do 无 break：到 limit 只停写入
      !S1 = !E1.string()
      writefile $!F1 |ROW$!S1|
      !N1 = !N1 + 1
    else
      !TR1 = true
    endif
  endif
enddo
writefile $!F1 |SEARCH_END rows_$!N1|
if (!TR1) then
  writefile $!F1 |TRUNCATED|
endif
closefile $!F1
```

### 3.2 pdms_get_attrs
args: `ref`（元素全引用）、`attrs`（空格分隔小写属性名，缺省 = name/type/dia/wall/spec/desc）

```
pmlResultOpen()
!E1 = /PROJ/SYS/1/PIPE/P100         -- [未验证·首跑#6] 元素句柄赋值
!S1 = !E1.string()
writefile $!F1 |ELEM$!S1|
!A1 = 0
handle ANY                           -- 缺属性保护：默认值 + handle ANY（已验证模式）
!A1 = !E1.dia
endhandle
writefile $!F1 |ATTR dia_$!A1|
-- …每属性两行（默认值赋值 + handle ANY 读 + 写行）…
writefile $!F1 |GET_OK|
closefile $!F1
```

### 3.3 pdms_set_attr
args: `ref`、`attr`、`value`（字符串自动加单引号）、`confirm`（改 `name`（改名）/`type`/`owner` 类危险属性时强制 true）

```
pmlResultOpen()
!E1 = /PROJ/SYS/1/VALVE/V-101
!E1.desc = 'DN50 闸阀 介质:水'       -- [未验证·首跑#8] 属性写
!R1 = 0
handle ANY
!R1 = !E1.desc
endhandle
writefile $!F1 |SET_OK desc_$!R1|    -- 回读确认
closefile $!F1
```

### 3.4 pdms_export_table（核心新增：材料表 / 管线表 / 任意属性表）
args: `ref`（模块）、`types`（可选，逗号分隔，默认 PIPE）、`attrs`（可选列，默认 name/type/dia/wall/spec）、`limit`（默认 500）、`format`（`csv`|`xlsx` 默认 xlsx）、`outPath`（可选；缺省 = `D:/pdmsAgent/bridge/export/<模块名>_<时间戳>.xlsx`）

PML 侧（纯 PML，只产出 CSV 文本到结果文件）：

```
pmlResultOpen()
writefile $!F1 |TAG,TYPE,DIA,WALL,SPEC|      -- CSV 表头（attrs 决定列）
!N1 = 0
!TR1 = false
do !E1 element /PROJ/SYS/1                    -- [未验证·首跑#7]
  !T1 = !E1.type
  if (!T1 = 'PIPE') then                      -- types 多值 → 多个 if
    if (!N1 < 500) then
      !S1 = !E1.name
      !A1 = !E1.dia
      !B1 = 0
      handle ANY
      !B1 = !E1.wall
      endhandle
      !C1 = 0
      handle ANY
      !C1 = !E1.spec
      endhandle
      writefile $!F1 |$!S1,$!T1,$!A1,$!B1,$!C1|
      !N1 = !N1 + 1
    else
      !TR1 = true
    endif
  endif
enddo
writefile $!F1 |===EXPORT_END rows_$!N1===|
if (!TR1) then
  writefile $!F1 |TRUNCATED|
endif
closefile $!F1
```

Node 侧（server.js）：解析结果文件 CSV → JSON rows 回给 AI；`format=xlsx` 时直接用 **exceljs**（ISO-PLAN 已定 Node exceljs 出表）写 xlsx 到 outPath，返回 `{rows, truncated, xlsxPath}`。
**全程不依赖 PDMS RPT 模块 / 报表模板** —— ROADMAP §3"形式待确认"问题就此解决：CSV→xlsx 为一选。
管线表（ISO-PLAN P5）= 本工具特例（types=PIPE/BRANCH + 管线表列），无需另建工具。

### 3.5 pdms_create（通用新建）
args: `ref`（父上下文，模块或草稿 REGI）、`type`、`name`、`attrs`（可选 key value 对）、`confirm`（生产模块强制 true）

```
pmlResultOpen()
CE /PROJ/SYS/1                            -- [首跑#6a] 模块级 CE（CE /REGI 形式已验证）
NEW VALVE /V-TEST                          -- NEW+属性续行+END 形式已验证（出图链）
DESC '自检元素'
END
!E1 = /PROJ/SYS/1/VALVE/V-TEST             -- 回读（[未验证] 句柄赋值）
!S1 = !E1.string()
writefile $!F1 |CREATE_OK$!S1|
closefile $!F1
```
首跑类型候选：选一个"可建可删、无外部引用依赖"的类型（VALVE/FRAM 等，看项目实际）；出图链（REGI/DRWG/SHEE/VIEW）已验证，可直接作为本工具的首个回归用例。
**依赖 ROADMAP §2-3 判定**：NEW/CE 是 CP 行；若进程内 $M 与 TTY 强制输入同被 `^^` 污染 → 本工具需改纯 PML 等价路径（待首跑定，见 §7 风险注）。

### 3.6 pdms_delete
args: `ref`、`confirm`（**必填 true**，回显元素名）

```
pmlResultOpen()
!E1 = /PROJ/SYS/1/VALVE/V-TEST
!S1 = !E1.string()
writefile $!F1 |SNAPSHOT$!S1|              -- 伪撤销：删前快照（PML 无 UNDO）
CE /PROJ/SYS/1/VALVE/V-TEST
DELETE VALVE                                -- CE+DELETE <TYPE> 形式已验证
!F2 = false
CE /PROJ/SYS/1/VALVE/V-TEST
handle ANY
!F2 = true
endhandle
if (!F2.not()) then                         -- 否定陷阱：用 .not()（已验证）
  writefile $!F1 |DELETE_OK|
else
  writefile $!F1 |DELETE_FAIL still exists|
endif
closefile $!F1
```

## 4. 安全护栏（对标"安全连接/受控自动化"）

1. **网络**：127.0.0.1 only（现状保持）。
2. **pdms_exec 黑名单**（逐行检查，命中即拒 + 说明）：
   - `QUIT` / `SHUTDOWN`（退出 PDMS）
   - `SET MDB`（切库，危险；需要时走用户明确指令 + confirm）
   - `DELETE REGI`（注册表级删除）
   - `openfile` 目标路径必须落在 `D:/pdmsAgent/bridge/` 或 `D:/pdmsAgent/bridge/export/` 内（写路径沙箱）
3. **confirm 强制**：pdms_delete 全部；pdms_set_attr 改 name/type/owner；pdms_create 在生产模块。
4. **无 UNDO 对策**：删/改前自动快照（内置于 PML 模板，见 3.6/3.3）。
5. **写操作不重试**：PML 无幂等保证 → create 前先 `CE + handle ANY` 查重；exec 失败原样返回报错文本（已有）。
6. **串行队列（修现有缺陷）**：单 RESULT_FILE 被所有调用共享，MCP 客户端可并发调用（WorkBuddy 多 Agent 场景）→ 现在会串话。server.js 加全局串行链：
   ```js
   let serial = Promise.resolve();
   function runSerial(fn) { const p = serial.then(fn); serial = p.catch(() => {}); return p; }
   // tools/call 内：const out = await runSerial(() => dispatchTool(name, args));
   ```

## 5. WorkBuddy 接入与"三件套"演示（对标剑维视频）

接入：按 WorkBuddy MCP 配置文档添加 stdio 类型 MCP server —— command=`node`，args=`["D:\\pdmsAgent\\bridge\\pdms-bridge-mcp\\server.js"]`；`tools/list` 应看到 12 个工具。前提：PDMS GUI 已启动、drawingPDMS 已加载、`pdms_status` 返回 connected=true。（Accio Work 等任意 MCP 客户端同理可接。）

**对标剑维"说出那几根管道自动出 ISO"的核心工具 = pdms_iso_generate（#12，已落地）**：
对 WorkBuddy 说 **"把 /2001、/2003、/2005 出 ISO 图"** → 自动调
`pdms_iso_generate({pipes:["/2001","/2003","/2005"]})`（要 DXF 时追加 `optionsFile: "<dxf选项>"`）
→ 桥接在 PDMS 进程内逐管线 CE→ISODRAFTMODE→options→DETAIL→isodraw 回读→EXIT
→ 返回每管线 `{status, plots, plotfile}`。**零 UDA/服务器/XML，选项用自带 Standard iso**，与旧 E3D Design 端 button2 同构。

三件套演示脚本（对应剑维视频三项能力，首跑未过前用草稿库/测试模块数据）：

1. **操作模型（查）**："列出 /<PROJ>/<SYS>/<MOD> 模块下所有阀门" → pdms_search → 表格呈现
2. **设置属性（改）**："把阀门 V-101 的说明改成'DN50 闸阀 介质:水'" → pdms_set_attr → 回读确认
3. **导出表格**："把该模块管道材料表导出 Excel（编号/类型/直径/壁厚/规格）" → pdms_export_table → xlsx 落盘 + 呈现文件
4. **管线 ISO 出图（核心）**："把 /2001 和 /2003 出 ISO 图" → pdms_iso_generate → 每管线 status/plotfile 回读，DXF/PLT 落 %PDMSUSER%

## 6. 首跑实测清单（接 ROADMAP §2 的 1–5，全部 PASS 后按序）

| # | 验证项 | 通过判据 |
|---|---|---|
| 6 | 元素句柄：`!E1 = /REG/MOD/TYPE/NAME` + `!E1.string()` | 回读引用串正确 |
| 6a | 模块级 `CE /PROJ/SYS/MOD` | 无报错，`!!ce` 可回读 |
| 7 | 模块遍历 `do !E1 element /REG/MOD` + `!E1.type` + 字符串 `=` 比较 | 遍历计数与 GUI 列表一致 |
| 8 | 属性写 `!E1.<attr> = value` + 回读 | 回读 = 写入值（测试模块） |
| 9 | export_table 首跑：模块管道表 ≤500 行 | CSV 行数 = rows_ 计数，TRUNCATED 标志正确，xlsx 可打开 |
| 10 | CRUD 闭环（依赖 §2-3 判定）：草稿库 NEW 无害类型 → 回读 → DELETE → 确认消失 | 四步全 PASS |

**可执行**：`pdms-bridge-mcp\first_run_check.js`（Phase A 只读默认可直接跑；`--write` 开 B3 建图 + B10 CRUD 闭环；#8 属性写需 `--set-ref` 指定安全目标；`--module` 改成实际设计模块；证据自动落盘 `bridge/first_run_<ts>.log`）。

**最大风险注**：#10 及 pdms_create/delete 依赖 ROADMAP §2-3（进程内 $M 下发 CP 行是否被 `^^` 污染）。若被污染 → 纯 PML 侧（#6–9 全部）不受影响可先交付；#10–11 需找纯 PML 等价构造/删除路径（或评估升级 UE 直接用官方 MCP）。

## 7. 实施顺序

| 阶段 | 内容 | 依赖 | 风险 |
|---|---|---|---|
| P0 | server.js 串行队列 + pdms_exec 黑名单/confirm/路径沙箱 | 无（纯 JS） | 无 ｜ **已完成 2026-10-14**（`pdms-bridge-mcp\test_guardrails.js` 3/3 PASS） |
| P1 | pdms_export_table（含 exceljs 出 xlsx） | 首跑 #7、#9 | 低（纯 PML） |
| P2 | pdms_search / get_attrs / set_attr | 首跑 #6–#8 | 低（纯 PML） |
| P3 | pdms_create / pdms_delete 通用化 + 材料表/管线表特例脚本 | ROADMAP §2-3 + 首跑 #10 | 中（CP 依赖） |
| P4 | WorkBuddy 接入 + 三件套演示 + README | P1/P2 完成 | 无 |

不变项：ROADMAP §2 出图（保留 pdms_create_drawing 高层工具，已验证）、§5 ISO（ISO-PLAN.md 路线）、§4 CASER2（待用户资料）。

## 8. 边界与备注

- 平台：官方 MCP 仅 UE 4.1（E3D Design 4.x）可用，PDMS 12.1 独立版不可直接套用，本方案为"同构思想移植"。
- 官方工具清单未公开；若后续拿到 UE 4.1 的 MCP 工具文档，对照 §3 补差（尤其表格导出格式与"软件操作自动化"的具体工具）。
- 持久化：写操作结果在草稿库可能仅会话级可见（PADD 边界已验证）；生产写入需真实工程库 + GUI 提交验证后才算落盘。
