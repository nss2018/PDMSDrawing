# pdmsDrawing 项目长期记忆（2026-11-09 精简至 3000 字内）

## 工程与构建
- **主工程 `D:\repos\drawingPDMS`**（C# 出图引擎唯一改动落点）；本工作区 `D:\repos\pdmsDrawing` = 沉淀版 + Python 桥接/向导/批量编排。主工程 csproj Debug 直出 `C:\AVEVA\Plant\PDMS12.1.SP4\`（生成即部署）；MSBuild/csc 被拦，只用 dotnet build。csproj 逐项 `<Compile Include>`，新增 .cs 必须手动登记。
- **段号铁律**：加 `(1xX)` 前先 grep 占用检查；已用到 15l（主工程 headlessDrawing.cs）。
- 部署判定只信 **DLL 哈希 + UTF-16 段号串**（MSBuild Copy 保留 obj 时间戳，mtime 不可靠）；部署后必须重启所有 PDMS 模块。

## 取证与日志（证据优先）
- 返回 ok ≠ 成功，timeout ≠ 失败；只信日志/DLL 二进制/产物。日志：`D:\pdmsAgent\bridge\op_create_drawing.log`、`op_replace_symbols.log`；`C:\AVEVA\...\last_outdir.txt` 指产物目录。
- 判定铁律：先看出图日志有没有本轮段号，再谈部署；D:\studio/C:\studio 不必同步（引擎在 C:\AVEVA 安装目录）。
- D 盘 = aigo U 盘（FAT32、卷损坏 dirty bit），会掉线；**不在 U 盘上编译/写项目**；脚本注释禁中文。
- 桥取证定案：PML 通道（^^ 污染、timeout）不可靠；沙箱内 socket 连 18753 被网络隔离拒；可用 = MCP pdms_* 工具或 DXF 离线解析（`~/.workbuddy/scripts/dxf_probe_*.py` 可复用）。
- **DXF 取证口径**：尺寸链数字 = DIMENSION 实体（组码 11/21 文本中点，兜底 10/20），按 y 聚类再算 x 间距；GTFDOT = LDIMENSION 测点引出线（图框外虚线即它，14y 定性，9f/14q 管不到）。
- ⚠️ 日志「引出N」≠ 尺寸链被引出：测点通道坏时 N 个可能全来自轴网 DPOINT。

## 桥端口与身份
- 分族定稿：绘图桥族 18753–18762（MCP 18753 / paragon 18754）；支架桥族 18763–18783（launch bat 18763、SWX(Para) 18764、WPC(Draw) 18765、WPC(Design) 18766、SAM 18767）。验身份发 `op=support_hello`。
- Addin 注册必须写裸程序集名；连错模块先 pdms_status 再 select_module。

## 出图约定
- **15o（11-12）**：GLABEL **LSHAPE BENT 转角引线三种布置图统一无条件恢复**（(10r) 10-25 曾对设备/埋板图退役=错）；namePos：布置图默认 TR、管道图默认 C(15e)。段号用到 15o。
- 标注：45° 斜引线优先、右上>左上；实际画法都在 headlessDrawing.cs（`CreateAnnotation` 是死入口）。设备定位用 Origin 世界坐标。5 图层分设（Dim/Text 建 VIEW 下，Outline/Frame/Table 建 SHEET 下）。
- 管道布置图缺省：leadDim=auto 只引真挤点、annotate 不勾仍画设备名/符号/管线表（14o）；附件不进尺寸链（14g `Graphic.IsDimFitting`，dimFittings=1 恢复）。

## 标注优化时间线要点（详见 reports/*.html）
- **14d/f**：annotationPlanner AABB 打分避让 + 几何禁区 + PlaceEscort 强制 45° 引出。
- **14g/14k**：附件类型黑名单；管线点按视图中心分象限归边（getPipeCollect 曾无条件进每条边链）；四边都试排取最近落位。
- **14o**：底链底线 = 标题栏顶+10mm；GLABEL 去 " POS EL" 后缀。
- **14u/14v**：管线号菱形贴线（labOnPipe）；字宽判拥挤 + PlaceStagger 错落引出（textPad=1.5、leadMax=80）。
- **14p→14q**：单线点划根治终解 = 图元级 `ATT.NLSTYL="GTSOLID"`（线型敲死在图元上，改样式无用；数字串 8001/8004 写入从未成功）。
- **14w**：硬否决→罚分打分（labScoreMax=32）+ 两级归属兜底；「修改没生效」多为代码跑了但全回退。
- **14x 三铁律**：①贴线候选必须校验本页真实可见段（防幽灵菱形，无可见段 PIPE 整条不画序号）；②9f `dpts.Count<3` 短路放链定位/DTAN/DTOF 之后；③测点 DPPT(DDNM) 与 DPOINT(POS/DPOS) 混挂，只读 DDNM 会全链空转。
- **14y**：贴线锚点必须投影吸附到真实可见段（SnapToVisible14y）；图框外虚线=LDIMENSION 原生引出线。
- **14z 测点读点铁律**：DPPT 不走 `GetElement(DDNM)→WorldPosOf`（对 BRANCH 四通道全抛 186/186）；**正解 = 直接读 `ATT.DDNMX/DDNMY`**（drawE3DTool.cs:353 实证），量纲 ddnmSpace=auto（ddnmWorldMin=2000），一维为 0 用链 DPOS 补。
- **15a（11-09）**：取证发现 9m 挂 253 个设备测点全被 isEq9 保护 → RIGHT 链 69 段挤爆；引出同值 200×7 刷屏；Stagger 命中 12/76。修复：①设备测点限密（eqGapMin=12/eqPerChain=40，挂链源头）；②同值去重（leadDup=1，leadSeen14a）；③径向回退仍撞→PlaceEscort 强制 45° 救回，仍撞才硬画。日志 `(15a) 引出修整 = 同值去重N Escort救回N 硬画N 设备点限密：拒挂N`。部署 DLL SHA256 00f4d714…。
- ⚠️ LDIMENSION 数字是 PDMS 原生生成，规划器只管自绘标注；减数字只能减测点，不能靠避让。
- **15f（11-10）**：贴线全空转根因 = `ParseLimitsString` 实返 `[Wfrom,Nfrom,Ufrom,Wto,Nto,Uto]`，13n 按 `[xmin,xmax,...]` 取索引 → 页框 xmin>xmax 倒置 → 281 BRANCH 全剔 → pendStrokes=0 → 贴线命中 0/93、93 个菱形全排图框边+长引线。修 pageBox 索引 + `labNoLead=1/18mm`（旧环禁远跳、超长引线不画）。DLL SHA256 79fecb2f…。⚠️ headlessDrawing.cs:1850 与 drawE3D.cs:521 同款误用，被 WORPOS 盒覆盖未暴露，别照抄。
- ⚠️ 图例入库：本机 `/MDS/ABA/TAGS/*`、`/TT/Isometric_ADP/*` SYTM 多缺失 → 原生视图无图例，现全靠 replace_symbols 自绘 9200 条线；补库+配表示规则可让 PDMS 自动出图例，但无合法 CATREF 的裸元件仍需代码兜底，改项目库须测试库先做+SAVEWORK。
- **15g（11-10）A 方案**：官方层级 = `DEPT>REGI>LIBY>SYLB>SYTM`（LALB 承载 TXTM），`SLABEL/SYMB.TMRF` 指向 SYTM 渲染；10-17 三次建库失败真因 = 把 LALB/SYTM 挂 DRAFT 的 DEPT 下。代码已实现「出图自动建 13 SYTM + SLABEL 引用 + 全链路直接画线兜底」，`symLib=0` 一键回退；DLL SHA 44b42675…。⚠️ LIBY 世界 SetAttribute(NAME) 会抛错（14p 实证）→ 按内容/顺序幂等复用；SLABEL 合法宿主是 LAYER 不是 NOTE；`Xyscale` 命令未实证别进批。**实机库对象待 PDMS 重启首轮出图才落地，验收看 `(15g)` 日志+截图**。B 链路 = `DRSYLK(DrTmpref+Criteria)→GPART(Tmpref,DrSSRef)→SPCO(Prtref,Tmpref)→EQUI.Spref→RRUL(SymbolFlag=true)→UPDATE ALL→DSLAYE>DESSYM`；硬限制：方向不跟随 EQUI、DESSYM 不可智能标注（用 RULL 后置）、裸元件永远代码兜底；PRJ/MAS 拒写需自建可写规则库。详见 `reports/15g-symlib-A-and-B-route-20261110.html`。
- ⚠️ 沉淀版 headlessDrawing.cs 只到 14g，缺 14i/14k/14m/14n/14o/14p，同步前先补基座。
- **15h（11-12 定稿 15l 修正）设备填充细密 45° 斜线**：自绘设备轮廓（(10b)，equipOutline13r=1 才有）内画 45° 斜线族 `x−y=c`（相位锚形心），**纸面间距固定 4mm（原 15i 观感）**；参数 `eqHatch=1/hatchSp=4`（纸面 mm，ANSI31×hatchScale×VSCA 换算路线已退役——画线坐标是纸面 mm，间距禁再套 VSCA）。矩形/圆裁剪辅助 `DrawHatchAnsi31Rect/Circle`（headlessDrawing.cs）。管道布置图 (13r) 默认无自绘轮廓→填充自动跳过，传 equipOutline=1 恢复。日志 `(15h) 设备填充(细密45°) = 线N 台N 跳N 间距…`。(15i) 整块已删不留痕迹。DLL SHA256 adb0e907…（11-12 08:50 部署）。
- **15l（11-12）框内黑粗线清理**：用户拍板「只留 15h 白细 ANSI31 + 中心线/轴网改细实线」。①**(15i) 小方块填充整块删除不留开关**（用户「不要留痕迹」；其 NLCOLO 3 线经 relayer 压成背景层灰=黑粗线真身，与 15h 双填叠加）；②10c1 设备中心线 NLSTYL 8004→GTSOLID（色保持红）；③DrawGridSeg GTDOT→GTSOLID（覆盖 10q 点线旧约定）；④SHEET 陈旧 NOTE 清场名单追加「Small Blocks Hatch」防旧图残留。取证口径：DXF 探针分层（背景层=灰/黄、轮廓层=白/自绘）+ PDF 笔宽 0.539/0.694 分档判粗细（`~/.workbuddy/scripts/*_15i*.py` 可复用）。DLL SHA256 fd2b1373。

## 其它
- pdms 技能 14 个 canonical（pdms-dialog/pdms-bridge/pdms-pml v2.3.0），旧版归档 skill-archive；ACCIO 同步唯一入口 `tools/sync_skills_to_accio.py`。
