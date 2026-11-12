# 智能体 + PDMS 路线图（2026-10-14）

目标：智能体直接驱动 AVEVA PDMS 12.1.SP4 完成出图任务，逐步扩展到材料表、CASER2、ISO 轴测图。
生产路径 = 进程内 Command 通道（DLL 插件 RunInPdms）；TTY/键盘注入仅作研究，不用于生产。

## 1. 架构（已落地，2026-10-14）

```
智能体 → MCP(stdio) → server.js(Node,无依赖)
      → TCP 127.0.0.1:18753 → PdmsBridge(drawingPDMS 插件, #if PDMS_BRIDGE)
      → CreateCommand("$M <pml>").RunInPdms() → PML 宏(CP+PML 混合)
      → result.txt(openfile/writefile/closefile) → C# 轮询读到即删 → 回传
```

关键设计：
- **结果文件是唯一稳定回读通道**：进程内 RunInPdms inline 变量回读不可靠（viewTest [3] 证据），
  PML 落盘 + C# 轮询经 TTY 全链路验证。
- **$M 而非 DO**：CP+PML 混合宏 TTY 实测全通过。注意：TTY 强制输入路径（LOGIN:166）会给 CP 行
  加 `^^` 前缀、只能跑纯 PML；进程内 Command 通道是否同样受限**首跑需确认**（§2 第 3 步即验证点）。
- **RunInPdms 是实例方法**：`Command.CreateCommand(text).RunInPdms()`，无静态重载；
  失败时 `command.Error.MessageText()` 带回 PDMS 报错文本。
- C# 读到 result.txt 后立即删除，否则下一次调用会读到上一笔残留。

## 2. 出图（平面图/总图）— 集成完成，待首跑验证

已验证（TTY 全链路，证据 `D:\pdmsAgent\view_api\`）：
- 建链语法：`CE /*` → `NEW REGI/DRWG/SHEE/VIEW`(+属性续行 VSCA/VTYP/SIZE/XYPS/DIR，无等号) → `END` 与 NEW 一一对应
- 回读：`!!ce.type/.vsca/.vtyp/.name/.dbType`
- 引用格式：drawing-rooted `/DRWG/SHEET/VIEW`（registry-rooted → (2,109)）
- 文件 I/O：`openfile write`+handle ANY→`overwrite`，writefile 缓冲到 closefile
- 删除：`DELETE <TYPE>`；切库：`SET MDB <db>`

首跑清单（用户重启 PDMS 后，按序）：
1. `pdms_status` → 结果文件出现 `PING_OK`（监听器通）
2. `pdms_query` `!!ce.type` → 确认 GUI 会话当前模块（预期 MON 或 DRAFT）
3. `pdms_create_drawing`（最小参数，drawing=AGT001）→ 核对结果：
   - `VIEW_TYPE_VIEW` / `VIEW_VSCA_0.2EX-01` / `VIEW_VTYPE_Universal HLine`
   - **`VIEW_DB_`**：若是 PADD → GUI 会话的 DRAFT 也落在会话级伪存储，需 `SET MDB` 切真实
     DRA 库（SAMPLE 工程默认无 DRA，需建库或指向已有库）；若是真实库名 → 做一次性
     二进制扫描验证持久化
   - 本步骤同时验证"进程内 $M 下发 CP 行是否会被 ^^ 污染"（若失败且报错含 `^^` 即命中，
     对策：把 CP 建链拆成纯 PML 包装或走 DO 对比测试）
4. `pdms_list_drawlist` `/AGT001/S1/V1` → MEMBERS_n
5. GUI 目视确认图纸出现（截图留证）

**可执行首跑脚本（2026-10-14 增，项 1–5 + MCP-GENERIC-TOOLS.md §6 项 6–10 全自动化）**：
`node D:\pdmsAgent\bridge\pdms-bridge-mcp\first_run_check.js`
- 默认 Phase A 只读：A1 桥接在线 / A2 当前上下文 / A7 模块遍历 / A6 句柄回读 / A8r 属性读 / A9 导表首跑 / A5 人工目视提示
- `--write`：+ B3 建图（含 `^^` 污染判定）/ B10 CRUD 闭环（NEW→回读→DELETE→确认消失，自清理）
- `--set-ref <ref> --set-attr <a> --set-value <v>`：B8 属性写（必须用户指定安全目标）
- 参数：`--module`（默认 /SAMPLE/SYSTEM/1，改成实际设计模块）、`--crud-type`（默认 FRAM）、`--limit`（默认 50）
- 证据自动落盘 `bridge/first_run_<ts>.log`（每步入参+原始输出+PASS/FAIL/WARN/SKIP）
- 2026-10-14 冒烟：PDMS 未启动时 A1 快速失败（ECONNREFUSED 18753）→ 跳过全链，按预期

 未验证项（默认关闭，勿默认开启）：
 - `ADD ALL <types> WITHIN W.. N.. U.. TO W.. N.. U..`（体积框选入绘）— 基础链通过后再开 `addAll`，
   先在一次性 REGI 里单独验证
 - 导出（DWG/PDF）— 需查 DRAW 的 EXPORT/PRINT 命令或 E3D 侧 API

### 2.1 op 通道（2026-10-14 用户拍板路线 A：桥接直调 C#，取代 §2 PML 路径为出图主路径）

窗口生产链（drawE3D.cs button2_Click，进程内验证可出图）已移植为无头：
- 新增 `drawingPDMS/headlessDrawing.cs`：`create_drawing`（button2_Click 逐行 1:1 镜像，含管道布置图
  全套标注块 ~800 行 + 管口方位图块；UI 依赖全部换参数：box/elements/drawingType/scaleDen/backs/rrsf/hrsf）
  / `probe_read`（只读探针）/ `export_dxf` / `export_pdf`（button11 逐行同款 LIEXEC/PLOT）
- `PdmsBridge` 协议扩展 `kind:"op"`（`exec`/$M 通道原样保留）：op 派发到 **PDMS UI 线程**执行
  （三级回退：注册控件 Control.Invoke → SynchronizationContext.Post+Wait(600s) → inline+告警；
  drawE3D 构造函数 RegisterUi 注册控件与上下文）
- server.js 新增 4 工具：`pdms_probe_read` / `pdms_draw` / `pdms_export_dxf` / `pdms_export_pdf`
  （op 超时默认 10 分钟；elements 数组 → 空格分隔串）
- 编译验证：MSBuild Debug 零错误（2026-10-14 晚，输出 bin\verify 避占用的 PDMS 目录 DLL；
  正式部署需用户关闭 PDMS 后覆盖 `D:\AVEVA\Plant\PDMS12.1.SP4\drawingPDMS.dll` 并重启）

出图首跑顺序（PML 版 `pdms_create_drawing` 降为备用通道）：
1. 用户关闭 PDMS → 部署新 DLL → 重开同一 DRAW 会话（须挂设计库）
2. `pdms_probe_read`（只读 op：验证 UI 线程派发 + 跨线程 DB API 安全；列出设计 world 顶层 + 包围盒选目标）
3. `pdms_draw`（start="/*"，box 或 elements 取 probe 结果，drawingType=管道布置图，annotate=true）
   → 输出含 SHEET/VIEW 名 + ACTUAL_LIMITS + UPDATE all 结果
4. GUI 目视确认（Draft Explorer 树 + 图纸显示）
5. `pdms_export_dxf` / `pdms_export_pdf`（默认输出 D:\pdmsAgent\bridge\out\）

已知风险：标注块符号库引用（/TT/Isometric_ADP/Arrow1、/MDS/ABA/TAGS/...）依赖项目主库；
会话缺这些符号元素时标注步报错形态 = 窗口按钮点击同样报错（模型投影部分保留，日志定位失败点）。

## 3. 材料表 — 待确认形式

候选路径：
- **RPT 模块（Report）**：官方 pmllib 有 `rptcrtquick.pmlfnc`（快速建报表）先例；
  培训材料 TM-1803 Reporting（`D:\pdmsAgent\E3D 培训教材\`）
- **模型查询**：管材/法兰等材料信息在 DESI/PIPE 类库中，PML 聚合后写 Excel/CSV
- **图纸注释**：材料表作为注释块挂到图纸上

待用户确认：产出形式（Excel / PDF 报表 / 图纸注释块）、统计范围（管材？法兰？设备？钢结构？）、
是否有现成模板。
**2026-10-14 决策**：一选 = PML 聚合 → CSV → Node(exceljs) 出 xlsx，以 `pdms_export_table` 通用工具实现
（不依赖 RPT 模块，管线表同为特例）；RPT 路径降为备选。详见 `MCP-GENERIC-TOOLS.md` §3.4。

## 4. CASER2 — 阻塞，待用户资料

"CASER2" 在 AVEVA 安装目录全量 grep 零命中——非 AVEVA 标准名称，判定为客户定制报表/交付物。
需要用户补充：
- 是什么报表（设备清单？管道汇总？特殊工况计算？）
- 样例或模板文件（Excel/Word/PDF）
- 输入范围（哪些模块/哪些元素）

## 5. ISO 轴测图 — 方案已定，见 `D:\pdmsAgent\ISO-PLAN.md`

范围按用户指示收窄为**管线 → ISO DWG + 管线表**（不做多功能）。
- 旧资产盘点完成：`D:\newRepos\ISO出图Design端（11-27）`（Design 端 CNPE.ISO，E3D addin）+
  `D:\newRepos\CNPE.ISO.CAD`（CAD 端 AutoCAD 插件：mat-ISO 材料表回写/PDF/图框图签）
- 业务规则在手：站点分类 `:CNPEspco`(T/W/I→GY/SG/YK)、角度象限 `:ISOD_dAngle/:ISOD_AAngle`、
  长度/直径/itemCode、Isodraft 日志校验（"will not be detailed"+DXF 存在）、ISO 登记表 9 列 Excel、(mat) 侧车
- 分阶段 P0(盘点✅)→P1(bridge 首跑,等用户)→P2(GUI 手动跑通 PDMS Isodraft 录命令)→P3(生成自动化)
  →P4(角度规则移植,视模板)→P5(管线表 Excel)→P6(DWG,复用 CAD 端)
- 辅助库 FangYuanData.dll 已反编译存档：`D:\pdmsAgent\decompiled\FangYuanData\`
- PDMS 侧文档资源：ISOUG/ISORM CHM、TM-1873、ISO定制.pdf、IntelligentDraw.rar

## 6. 文件轮询传输（AGENTBRIDGE.PML）— 结论：不可行

PML 无 sleep/wait 语义，无法实现"轮询 queue 目录 → 执行 → 写 done"循环；
进程内 Command 通道（TCP 路径）是唯一可行路径。AGENTBRIDGE.PML 保留存档。
（server.js 的 callFile 分支仍在代码中，`PDMS_BRIDGE_TRANSPORT=file` 不再可用。）

## 7. 证据与参考

- TTY 验证证据链：`D:\pdmsAgent\view_api\`（cvt_tty_*.pml + run_*.ps1 + 屏幕/日志证据）
- API 侧测试：`drawingPDMS\viewTest.cs`（PASS 14 / FAIL 6，含 inline 回读失败证据）
- 日记：`agent-core\diary\2026-10-14.md` §2（TTY 全链路终判）
- 构建：MSBuild 18 BuildTools；Debug → `D:\AVEVA\Plant\PDMS12.1.SP4\drawingPDMS.dll`
  （2026-10-14 11:49，114176B，桥接代码已验证入包）
- 参考解决方案：`D:\pdmsAgent\FangYuanPDMS\`（Draft/Pipe/Support .cs）
- 样例建图语法源：`D:\AVEVA\Plant\PDMS12.1.SP4\PDMSUI\dra\data\sample.dat` / `clib\fcreate`

## 8. 借鉴官方 E3D MCP（2026-10-14，用户定调"针对 PDMS 借鉴他们的"）

剑维（AVEVA 中国）随 UE 4.1 发布官方 E3D MCP：E3D 内 MCP Server + 通用工具 + 数据库增删改查 +
软件操作自动化；WorkBuddy 仅为演示客户端（标准 MCP 协议，任意客户端可接），工具清单未公开。
PDMS 版借鉴设计（11 工具 + 安全护栏 + 首跑清单 #6–#10 + 实施顺序 P0–P4）：**`D:\pdmsAgent\bridge\MCP-GENERIC-TOOLS.md`**。
要点：
- search/get_attrs/set_attr/export_table 为纯 PML，低风险可先交付（对应用户三诉求：查模型/改属性/导表格）。
- create/delete 依赖 §2 步骤 3 判定（进程内 $M 下发 CP 行是否被 `^^` 污染）——整个 CRUD 路径最大风险点；
  若命中，纯 PML 侧不受影响，CRUD 需找纯 PML 等价路径。
- server.js 需先修并发缺陷（单 result.txt 无串行保护）+ pdms_exec 加护栏黑名单。

## 9. 参数化设备（设备部）— 智能体重构（2026-10-14，代码逻辑移植，不碰 vendor DLL）

来源：方圆 TestAttribute 插件（`D:\newRepos\TestAttribute - 12.1.sp4`：BuildOne.cs 23 构造器、TowerImpl、Attribute.cs UI 默认值、EquiTool 管嘴表、General FUNC 保存）。
用户定调：只把**代码逻辑**移植进智能体；vendor DLL 不编译、不部署（FangYuan.DesignAddin 本就未注册，无需注册）。

交付物：
- Skill `pdms/parametric-equipment`（agent-core/skills/）：SKILL.md（工作流/管嘴表约定/陷阱）+ references/equipment-catalog.md（22 类参数/几何公式/FUNC/默认值，含源码行号）+ references/cp-syntax.md（CP 语法证据 + ORI→ORIA 求解器 + 首跑 E1–E7）。
- server.js 新增 2 工具（现 8 个）：`pdms_create_equipment`（CP 生成：NEW EQUI + 嵌套基本体 + FUNC + 管嘴；查重 EQUIP_EXISTS；成员回读；dryRun 审计；noCatr / func:"" 降级）/ `pdms_get_equipment`（反向参数化 + 管嘴表聚合 SufFaceDis/EleNozz/NozzAngle，对应 vendor Callback/getNozzInfo）。
- ORI 串 → ORIA 三元组求解器（E→N→U 组合，dat 6 组映射复现，|和| 最小标准形；欧拉表示不唯一已处理）+ 自检 `node server.js --selftest-equip`（22 类 PASS）+ 冒烟 `test_equipment_smoke.js`（16/16 PASS；离线用例走假端口 9，**绝不触达真实桥**——文件头已立铁律）。
- 未移植：three_box_base（0/30/150° 圆弧定位 + 动态径向 ORI，v1 需单独设计角度参数化）。

事故记录（进程内 $M 首次真跑 CP，2026-10-14 18:3x）：
- 冒烟测试误发一条非 dryRun 泵创建（P-1，/SAMPLE/SYSTEM/1，noCatr）到真实桥（当时在线）。随后 `pdms_query !!ce.type` 在 CE 空时于 closefile 前中止宏（回读赋值在 handle 外）→ 结果文件永不落盘 → 桥等待卡滞（其后 PING/query 全超时）。
- 修复：全部回读赋值移入 handle ANY 块内（query/create/get 三处）；冒烟改 dryRun + 假端口。
- 18:23 用户重启 PDMS（des 20492 + PDMSConsole 10004），旧监听进程消失；18:53 时 18753 未监听 = drawingPDMS addin Start() 未执行（需进入 DESIGN 模块）→ **P-1 是否存在未核实**（若泵宏跑到 FUNC 行，类未定义即中止，可能残留空 EQUI 壳）→ 桥恢复后 get_equipment 核实，存在则 DELETE。

首跑顺序（桥恢复后）：
1. PDMS 进 DESIGN 模块（设备部模型）→ `pdms_status` PING。
2. `pdms_get_equipment /SAMPLE/SYSTEM/1/EQUIP/P-1` 核实/清理事故残留。
3. `pdms_create_equipment` dryRun=true 审计 CP → 正式下发（先小设备，如 flat_top_tank）→ GUI 目视。
4. E1–E7 逐项判定（cp-syntax.md §5）；`^^` 污染判定与 §2 步骤 3 同一闸——若命中，设备全链路停摆，需纯 PML 等价路径（研究项，同 create/delete）。
5. 首跑通过后并入 first_run_check.js Phase C（TODO）。
