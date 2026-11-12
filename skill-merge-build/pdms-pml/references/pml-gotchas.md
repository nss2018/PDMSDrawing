# PML 实战踩坑清单（逐条验证）

> 来源：`ISO-PLAN.md` §6（TTY 全链路验证，2026-10-14）、`bridge/` 桥接宏、`iso/iso_gen.pml`。
> 每一条都在真实 PDMS 12.1.SP4 环境踩实，不是推测。

---

## 坑 1：变量名大小写不敏感 → 类型冲突
- **现象**：`!n1` 先当 REAL 用、后又赋 STRING，报 `incompatible types`。
- **根因**：PML 变量名**大小写不敏感**，`!n1` 与 `!N1` 是同一个变量。
- **修复**：全程小写 + 有意义的命名；不要靠大小写区分两个变量。

## 坑 2：空 PML 数组调 `.size()` 报 "no member SIZE"
- **现象**：对空数组成員调 `.size()`，整宏中断。
- **修复**：用计数器代替；或先判非空再调。

## 坑 3：`coll` 结果成员无 `.type` / `.name`
- **现象**：`coll all for CE` 收集到的成员调 `.type`/`.name` 报无此成员。
- **正确做法**：
  - `do !i indices !W` + `!W[!i].string()`（返回值带 `=` 前缀，如 `=PIPE/P100`）；
  - 取类型/名字需 `CE <该引用>`（`=` 形式可直接 CE）后读 `!!ce.type` / `!!ce.name`。

## 坑 4：`coll <TYPE> for CE` 不可用（按类型收集）
- **现象**：`coll PIPE for CE` 返回的是 CE 自身，不是子管线。
- **修复**：只能 `coll all for CE` + 逐条 `!!ce.type` 判型过滤（见 `iso_gen.pml` 的 `if (!T1 eq 'PIPE')`）。

## 坑 5：类型代码是缩写
- **注意**：类型代码不是全名：`BRAN`（非 BRANCH）、`PIPE`、`SITE`、`ZONE`、`EQUI`、`NOZZ`、`WORL`、`PTWL`、`TUBI`。
- **判型时用缩写**，否则 `!!ce.type` 永远匹配不上。

## 坑 6：`|...|` 字符串内 `$!arr[!i]` 数组索引插值会崩
- **现象**：在 `|...|` 字符串里写 `$!arr[!i]` 做数组索引插值，整宏中断。
- **修复**：先把数组元素取到简单变量 `!s = !arr[!i]`，再插值 `|...$!s...|`。

## 坑 7：宏内 CP 行的盘符路径 / `mess` 会被 `^^` 注入破坏
- **现象**：TTY 强制输入路径（LOGIN:166）会给 CP 行加 `^^` 前缀，导致带盘符路径的 `openfile` / `mess` 命令失败，且只能跑纯 PML。
- **修复**：宏里**不写盘符路径、不用 `mess`**；用 `openfile /D:\...`、`$M "%ENV%/..."` 形式正常。进程内 Command 通道（RunInPdms）用 `$M` 路径形式通常安全，但首跑需确认是否同样被 `^^` 污染。

## 坑 8：`handle ANY` 只保护紧邻前一条语句
- **现象**：以为 `handle ANY … endhandle` 是块级捕获，结果中间语句抛错仍中断。
- **修复**：每条可能失败的语句（属性读、CE、isodraw 状态读）前都单独 `handle ANY … endhandle`；可在 handle 体内用 `break` 跳出循环。

## 坑 9：结果文件是唯一稳定回读通道
- **现象**：进程内 `RunInPdms` 的 inline 变量回读不可靠；`isodraw status` 等系统变量不落盘就读不到。
- **修复**：统一用
  ```
  openfile <file> write !F1
  handle ANY
    openfile <file> overwrite !F1
  endhandle
  ... writefile $!F1 |...|
  closefile $!F1
  ```
  C#/Node 侧轮询读后删除。**所有回读赋值必须放在 `handle ANY` 块内**——漏在外会导致结果文件永不落盘，桥接卡滞（实测事故：CE 空时 `closefile` 前宏中止 → 桥等待超时）。

---

## 事故记录（进程内 `$M` 首次真跑）
某次桥接在线时，冒烟测试误发一条非 dryRun 的泵创建到真实桥；随后 `pdms_query !!ce.type` 在 CE 空时于 `closefile` 前中止宏（回读赋值在 handle 外）→ 结果文件永不落盘 → 桥等待卡滞（其后 PING/query 全超时）。修复：全部回读赋值移入 handle ANY 块内；冒烟改 dryRun + 假端口。

---

## 坑 10：`collect ... for WORLD` 直接杀死宏（电解槽建模 2026-10-17 实测）
- **现象**：宏里写 `var !x collect all (BOX) for WORLD`，宏**无任何报错地静默终止**，后面的语句全不执行、结果文件也不落盘。
- **修复**：collect 范围收敛到具体元素——`collect all (BOX) for /设备位号` 或 `for /ZONE名`。世界级统计要在外层先 `collect all (EQUI) for /ZONE` 再逐设备收集。

## 坑 11：`$!arr.Size` 数组属性插值报 "Module not defined"（实测）
- **现象**：`writefile $!F1 |COUNT $!arr.Size|` 报 `Module not defined in file`，宏中断；`var !x eval ... for all from !空数组` 报 `SELECT PML array is not defined`。
- **修复**：先把属性赋给标量 `!n = !arr.Size`，再插值 `|COUNT $!n|`；eval 前确认数组非空。

## 坑 12：`Name /xxx already used` = 元素其实建成功了（实测）
- **现象**：`NEW EQUIPMENT /位号` 报 `(41,12) Name /xxx already used.`，极易误判为失败而重试，结果建出重复设备或误删好设备。
- **根因**：这是"重名冲突"，意味着库里**已经有**这个名字的元素——通常就是上一次（看似超时/失败的）下发实际建成功的。
- **修复**：见此报错先 `CE /xxx` + 查成员数确认现状，再决定改名复用还是删除；配合桥接 1~5 分钟延迟语义（见 pdms-equip-modeling 技能 §3），不要按报错盲目重建。

## 坑 13：设备 ORI 会整体扭转本地坐标系（实测）
- **现象**：设备设 `ORI Y is W and Z is U` 后，宏内所有方向词（N45U 等）在世界坐标里全部错位 90°，管子朝向、排布方向全错；且从模型截图上极难一眼看出原因。
- **修复**：设备**不设 ORI**（默认本地 E/N/U = 世界 E/N/U）；局部朝向需求写在**原语的 ORI** 上（如竖盘法兰 `ORI Y is N and Z is W`）。确需旋转用 `ROTATE ABOUT U BY 90 THRO CE`。

## 坑 14：空数组 `do !i indices` / `.size()` 会致命崩溃，且 `handle ANY` 包不住
- **现象**：对空数组（或收集返回空）做 `do !i indices !E` 或直接 `.size()`，整宏**无报错地静默终止**（结果文件永不落盘），`handle ANY` 也救不回来。
- **根因**：PML 对空/未定义数组的下标与 size 操作是致命错误，不在 `handle ANY` 捕获范围内。
- **修复**：迭代前先用 `!ARR[1]` 取元素引用 + **哨兵串**判定非空（不要 `Set()`/`var !X` 无初始化，会被当成未定义）：
  ```pml
  var !OK = 'N'
  handle ANY
    !T = !ARR[1]
    !OK = 'Y'
  endhandle
  if (!OK eq 'Y') then
    do !i indices !ARR
      ...
    enddo
  endif
  ```
  实测：`var !E ARRAY()` 后 `do !i indices !E` 必崩；`!E.size()` 在 handle 内也崩。

## 坑 15：链式 `!ARR[!i].name` 致命崩溃 —— 必须拆两行
- **现象**：`!NM = !ARR[!i].name` 或 `(!ARR[!i]).name` 直接报变量/类型错并中止宏。
- **修复**：先取元素引用再读属性：
  ```pml
  !X = !ARR[!i]
  !NM = !X.name
  ```
  已验证：在 Paragon 里 `!X = !CATS[!i]` 后 `!NM = !X.name` 稳定可用（见桥接目录 `C:\pdmsAgent\bridge_p18754\cmd_2349.pml` 范本）。

## 坑 16：`coll all <TYPE>`（无 `for`/`from`）是语法错误
- **现象**：`var !B coll all CATEG`（想全库收集）整宏在**加载期**就失败——连 `openfile` 都不执行，结果文件压根不创建。
- **修复**：必须带作用域：`coll all CATEG for ce` / `coll all CATA for $!C1`（元素变量）/ `coll all ... from !coll`。参考 `PMLLIB/paragon/objects/catgetelement.pmlobj`：`var !coll coll all SCOM for ce`。

## 坑 17：复用被中断宏遗留的打开文件句柄会锁死文件
- **现象**：某宏 `openfile ... !F1` 后因致命错（坑14/15）没跑到 `closefile`，句柄 `!F1` 一直挂着；后续宏再 `openfile ... !F1` 直接失败，整宏 0 输出。
- **修复**：每个宏用**全新句柄号**（!F20、!F21…）和**全新文件名**；出错后想复用先 `closefile $!Fx`，或直接换号。切勿跨宏复用同一 `!F1`/`result.txt`。

## 坑 18：一次致命 PML 错误会令 PDMS 命令处理器停摆
- **现象**：某次宏里踩到坑14/15 致命错后，之后**所有**宏都返回空、不执行（连 `writefile |HELLO|` 这种最简宏都不落盘），桥接持续 0 字符。
- **根因**：致命错让 PDMS 命令处理器卡死/弹出未关的错误框，后续 `RunInPdms` 全部空回。
- **修复**：用户侧**关掉 Paragon 里可能的报错框 → 重启 Paragon(SWX) 模块 → 必要时重启桥接**，会话恢复后再跑。自动化前务必先用最简宏探活（如只写一行 `|HELLO|`）。

## 坑 19：`CE /*` 后 `ce` 进入异常态，`coll ... for ce` 会抛致命错
- **现象**：`CE /*` 能导航到 world（能写标记），但之后 `ce.type` / `coll all X for ce` 抛错；在 world 下 `coll all CATEG for ce` 返回空 → 踩坑14 崩。
- **修复**：盘点类宏**不要依赖 `CE /*`**。直接用桥接注入时的当前 `ce`，或显式 `CE /具体路径`；世界级统计改 `coll all X for ce` 收敛到具体设备/区（见坑10）。

## 坑 20：`!!ce.type` / `!!ce.namn` / `!!ce.dbType` 在 `$M` 宏**顶层**是致命错
- **现象**：宏顶层写 `!t = !!ce.type` / `!!ce.namn` / `!!ce.dbType`（用来读当前元素），整宏在 `openfile` 之后、第一个 `writefile` 之前就中止——结果文件只有 BOM，`handle ANY` 也救不回来（不是普通运行时错，是顶层全局变量访问的致命错）。
- **反例**：`min_ce.pml`（`!!ce.type/namn/dbType` 三条）实测必崩；`nav_probe.pml` 删掉这些后正常。
- **修复**：**不要**在 `$M` 宏顶层读 `!!ce`。想知道当前元素/库，改用 `coll all <TYPE> for ce`（它会隐式用 CE 而不碰 `!!ce` 变量）。AVEVA `catgetelement.pmlobj` 里的 `!!ce.type` 是写在 **object 方法体内**的，裸宏不能直接抄。

## 坑 21：`object COLLECTION()` / `object DBREF()` 构造在 `$M` 宏**顶层**也是致命错
- **现象**：想照搬 AVEVA `catgetelement.pmlobj` 的全局收集写法（`!col = object COLLECTION()` / `!col.scope(object DBREF())`），在裸 `$M` 宏里整宏在第一个 `writefile` 前中止（仅 BOM）。
- **根因**：这些 `object` 构造/方法调用属于 **PML 对象方法环境**，只能在 `define object / define method` 体内用，不能放在独立宏顶层。
- **修复**：裸宏只用 `coll all <TYPE> [for ce | for $!E]` 命令式收集；需要 `object COLLECTION()` 的强能力时，先 `define object` 包一层再调用（或让 PDMS 原生菜单宏去调）。

## 坑 22：目录库层级与元件类型（AVEVA `catgetelement.pmlobj` 权威确认）
- **正确层级**：`CATEG`（类别组）→ `CATE`（具体类别）→ `SCOM`/`JOIN`/`SPRF`/`SFIT`（元件）。
- **`CATA` 是数据库类型名**（代码 `!element.dbType neq 'CATA'`），**不是** collect 得到的元件类型；元件要收 `SCOM/JOIN/SPRF/SFIT`，且必须挂在 `CATE` 下面，不是挂在 `CATEG` 组下面直接收 `CATA`。
- **类别类型**还有 `STCA`/`SECT`/`STSE`（AVEVA 代码 `categoryTypes`）。
- **安全收集写法**（已验证）：
  ```pml
  var !G coll all CATEG for ce        -- 类别组
  var !C coll all CATE for $!G        -- 类别
  var !S coll all SCOM for $!C        -- 元件（JOIN/SPRF/SFIT 同理）
  ```
  读元件名用 `!nm = (!X).namn`（AVEVA 同时用 `.name` 与 `.namn`），**先 `!X=!ARR[!i]` 再读**（坑15）。

## 坑 23：本工程 Paragon(SWX) 会话的 CE 漂移 + 世界级空
- **现象**：桥接 18754 连的是 Paragon(SWX)。用户重置后默认 CE 下 `coll all CATEG for ce` = 1（正常）；但 `CE /*`/`CE /` 都到不了目录库——`CE /*` 到的 world 下 `coll all CATEG/CATE/ZONE/EQUI for ce` **全 0**（`nav_probe.pml` 实测），`CE /` 直接报错。
- **结论**：目录库 CATEG 不在 world 直接子级，且 `CE /*` 在本会话把 CE 带进空 world，**纯靠导航定位不可靠**；`coll all <TYPE>`（无 `for`）搜的是当前 MDB，本会话当前 MDB 也不是目录库 → 也 0。
- **修复**：盘点宏**只用 `for ce` 吃默认 CE**，绝不写 `CE /*`/`CE /`/`DATABASE` 之类导航；CE 一旦漂走（任何致命错或导航后）只能**用户重置 Paragon 会话**恢复默认 CE，再重跑。已就绪模版：`inv_v3.pml`（`coll all CATEG for ce`→CATE→SCOM/JOIN/SPRF/SFIT，全程 `empty()` 守卫、`(!X).namn` 读名）。

## 坑 24：MCP `pdms_exec` 的 20s 硬超时 + 读名字可用写法（2026-10-18 实测）
- **MCP 超时陷阱**：`pdms_exec` 工具的 `timeout` 参数**不生效**，连接器固定 20s 硬超时；PDMS 实际要更久（首跑常 40~90s）才回写结果文件 → 误报 `timeout`，但 `bridge.log` 里后来会出现 `got result`。**唯一可信完成信号 = `bridge.log` 的 `got result` 行 / 结果文件落盘**，不是 MCP 返回值。
- **正道**：走本地 `run_long.js`（桥目录自带），读结果文件：`node run_long.js <宏.pml> <结果.out> <id> <wait_ms>`，`wait_ms` 给 90000。`run_long.js` 等满 `wait_ms` 后读结果文件，不靠 MCP。
- **读名字可用写法（本会话实测）**：
  - ✅ `!X = !ARR[!i]` 后 `!NM = !X.name`（坑15）稳定可用；
  - ✅ `!!X.ref`（镜像 Design 桥 `!!ce.ref`）可拿全引用名；
  - ❌ `(!X).namn` / `(!X).type` / `(!X).string()` / `$!X` 本会话 Paragon 里**实测返回空**（handle 吞错后无输出），不要依赖；
  - ❌ 顶层 `!!ce.type/namn` 致命（坑20），但循环内用 `!!X.ref`（X 为收集到的元素变量）OK。
- **CE 漂移后别硬试**：复位前 `coll all CATEG for ce` 可能返 0/空，踩坑14 崩；先让用户重置 Paragon 会话恢复默认 CE 再跑盘点。

## 坑 25：【反转】"BOM-only / 输出文件没建" ≠ 链路死——多半是自己宏语法错，先用已知 good 模版定基线（2026-10-18 21:xx 实锤反转）
- **误判实录（已推翻）**：曾据 "JSON 回显 `OUT(1)` 仅 BOM + `bridge.log` 无 `got result` + `ping2.txt` 仅 3 字节 BOM + `inv_final.txt` 根本没建" 误判成 "PDMS 卡模态、Design+Paragon 双桥同时哑火、无编程恢复口子"。**该结论错误。**
- **真相 = 全是我自己宏的语法 bug**：
  - `probe_inv_final.pml`：顶层多写了 `var !G array()`…`var !F array()`，后面又 `var !G coll all CATEG for ce` → **重复声明**，编译期整宏被拒，连 `openfile` 都没跑（所以 `inv_final.txt` 根本没建）。
  - `ping2.pml` / Design ping：~~`$?` 非法~~ **【坑26 更正】`$?` 在 writefile 管道里不报错、被原样写出**（ping2.txt 迟到的内容就是字面 `PONG_MOD_$?`）；当时 BOM-only 的真因是**未捕获错误弹模态框等人点**（坑27），不是 `$?` 语法错。
- **判别铁律（最重要）**：看到 "BOM only / 输出文件没建 / `OUT(1)`" **先别判链路死**，立刻跑一个**历史上真出过结果的已知 good 模版**定基线——本工程用 `inv_v5b.pml`（= `inv_v5.pml` 原样复制、仅换输出文件名避开句柄锁）：
  - `inv_v5b` 能回计数 → **PDMS 和桥都活着**，是当前宏的语法问题，回去修宏；
  - `inv_v5b` 也 BOM/没文件 → 才可能是会话/桥问题，再找用户。
  - 本次 `inv_v5b` 实测**正常回 `CATEG_COUNT`**，直接证明 PDMS 活着、推翻"卡模态"。
- **写宏正确式（照搬 inv_v5b，别自作聪明）**：`var !X coll all <TYPE> for <ce|$!ref>`（单次声明）+ `handle ANY / var !X ARRAY() / endhandle` 兜底 + `.size()` + `do !i indices !X` + 拆两行 `!E = !X[!i]`；**绝不在 coll 声明之外再写顶层 `var !X array()`**（重复声明=编译错）；读名用 `!E.name`（无括号，坑24）与 `!!E.ref` 双取，**勿用 `(!E).name`**（本会话返空）、**勿用 `$?`**。
- **另两个与语法无关的真实小状况（都需用户操作）**：① **CE 漂移**：`coll all CATEG for ce` 现返回 0（17:06 时=1）→ 用户重置 Paragon 恢复默认 CE；② **18754 桥偶发掉线**（`ECONNREFUSED`），PdmsBridge 插件 "start 只允许一次"，端口掉了只能**重启 Paragon 会话**重绑。
- **仍成立的小结论**：桥（C#+TtyFeed）纯文本转发，grep 全部 `.cs` 无 `Escape/SendKeys/PostMessage/cancel`——**没有编程口子**代替用户在 PDMS 界面操作；诊断器 `ping_port.js <端口> <pml> <out> <id> <wait_ms>` 可任意端口探活。

## 附：Paragon 目录库盘点已验证安全模版（最新版，取代 inv_v3.pml）
- 通道：`C:\pdmsAgent\bridge_p18754\`（端口 18754，仅 Paragon，Design 的 18753 不碰）。
- **当前正确模版**：`probe_inv_final.pml` —— `coll all CATEG for ce` → `coll all CATE for $!g` → `coll all SCOM/JOIN/SPRF/SFIT for $!c`；空数组用**哨兵 `!OK` 守卫**（坑14，勿裸 `!ARR.empty()`/`.size`）；元件名用 `!X.name` 与 `!!X.ref` **双兜底**（坑24/15，**勿用 `(!X).namn`**，本会话实测返空）；**不含任何 `CE /*`/`CE /` 导航**（坑23）。
- **前提**：PDMS 无模态弹窗、Paragon 默认 CE（用户重置后）才跑得动；卡模态时本模版也不动（坑25）。
- 取数：`node run_long.js probe_inv_final.pml inv_final.txt <id> 90000` 读 `inv_final.txt`（坑24，勿用 `send_generic.js` 40s 短超时）。

## 坑 26：writefile 插值里禁止内联方法调用 `$!X.size()` → "Object does not have a member SIZE"（2026-10-18 21:45 用户贴回报错实锤）
- ❌ `writefile $!F9 |ZONE_$!GZ.size()|` → `(2,759) Object does not have a member SIZE`，致命错。
- ✅ 先赋值再插值：`!nz = !GZ.size()` → `writefile $!F9 |ZONE_$!nz|`（inv_v5 模版一直是对的，别内联）。
- `$?` 更正：writefile 管道里**不报错、原样写出**（坑25 的相关说法以此为准）。

## 坑 27：【核心机制】未捕获的 PML 错误会弹【模态错误框】冻住宏线程——所有"卡死/延迟 N 分钟"的真凶（2026-10-18 实锤）
- 现象：宏停在出错行、文件只有 BOM、看似"链路死/延迟 7 分钟"；实际是 **PDMS 弹了模态错误框等人点确定**（用户 21:52 点掉后两个积压宏同秒完成；用户贴回的报错文本就是那个框）。
- 推论：① 冒险语句各自包 `handle ANY` = **不弹框、静默继续**（walk2/walk3 全 handle 化后秒回零弹窗）；② 裸奔的危险语句 = 弹框 + 宏线程冻结；③ 宏没跑完前桥不回 `got result`。
- 判别顺序修正：BOM-only → ①先让用户看 PDMS 窗口有无错误框 → ②已知 good 模版定基线（坑25）→ ③才怀疑会话/桥。

## 坑 28：Design 桥(18753)能直接看到 Paragon 目录库；但 coll 成员取名在 Design 会话全灭；C# catinv 通道已备好（2026-10-18 21:5x）
- **重大发现**：18753 侧 `coll all ZONE/EQUI/CATEG for ce` → `ZONE_0 EQUI_0 CATEG_1`——**Paragon 目录库对 Design 桥可见**！18754 死了也能盘点结构（walk2 模版：CATEG→CATE→SCOM/JOIN/SPRF/SFIT 计数，实测 1/1/1/0/0/0，零 CE 导航秒回）。
- **但 Design 会话里 coll 成员取名全灭**（全 handle 化静默失败）：`.name`、`(!X).name`、`.string()`、`VAR !V NAME`、`VAR !V NAME OF $!X`、`!!X.ref`、`!X.Ref`、`$!X` 直接插值——一个都不出。名字只能：① 重启 Paragon 后走 18754 用 `!X.name`（cmd_2349 时代验证过）；② C# 通道。
- **C# catinv 通道（已写进桥源码，待用户编译）**：请求 `{"id":N,"kind":"catinv"}` → `RunCatInventory()`：`DbElement.GetElement()`(无参=当前元素) → `.Owner` 上溯到根 → `Members()` 递归（限深6/限2000节点），每行输出 `层级.类型|名字`（`GetElementType().ToString()` + `GetAsString(DbAttributeInstance.NAME)`）。不走 PML、不改 CE、无弹框。源码 `skills/pdms-bridge/assets/PdmsBridge.cs`（csproj 需补引 Aveva.Pdms.Shared.dll 时才用 CE，当前实现不需要）；客户端 `C:\pdmsAgent\bridge_p18754\catinv.js`，用法 `node catinv.js 18754 <id>`。
- **桥内部机制（读源码实锤）**：`Start()` 里 `if(_running) return`——监听线程死后同进程重调 start 无效，**只能重启会话重载 addin 重绑端口**；桥 output 只来自结果文件（命令行 Q 输出不回传）。

## 附2：Design 桥走库防弹窗模版（walk2/walk3，2026-10-18 已验证秒回）
- 文件：`C:\pdmsAgent\bridge\walk2.pml`（结构计数）/ `walk3.pml`（取名尝试，Design 侧全灭）。
- 要点：全新文件名（不 overwrite）；除 coll 声明+ARRAY() 兜底外**所有冒险语句各自包 handle ANY**；先 `!n = !X.size()` 赋值再插值（坑26）；只用 `for ce` 与 `for $!ref`，零 CE 导航。
- 取数：`node ping_port.js 18753 C:/pdmsAgent/bridge/walk2.pml C:/pdmsAgent/bridge/walk2.txt <id> 30000`（ping_port.js 在 bridge_p18754 目录）。

## 坑 29：`handle ANY ... endhandle` 在 `$M` 宏里【整块被跳过】（2026-10-18 23:0x 实锤，walk7 铁证）
- 现象：walk7 里 `A_BEFORE`/`C_AFTER`/`E_END` 都落盘，唯独 handle ANY 块里的字面量 `writefile $!F9 |B_INSIDE|` **完全没执行**——块被静默跳过，不报错、不弹框。
- 推论：此前所有“PML 取名 8 连败”全是假象——换名语句全包在 handle 里，压根没跑过。**别再用 handle ANY 当“防弹窗”手段**：要么裸奔（出错弹框、用户关），要么走 C#。
- 正确防错：只用 `for ce` / `for $!ref` 拿已知存在的元素 + `ARRAY()` 兜底判空，不碰 handle。

## 坑 30：Design 模块里 PML 对 `coll` 元素取 `.name` 直接报 “Object does not have a member NAME”（2026-10-18 23:0x 实锤 walk9）
- `!s1 = !x.name`（!x 来自 `do !x values of !CG`）→ `(2,759) Object does not have a member NAME`，致命错。
- 同一会话里试过且全失败（静默或致命）：`.name`、`(!X).name`、`.string()`、`VAR !V NAME`、`VAR !V NAME OF $!X`、`!!X.ref`、`.Ref`、`$!X` 直接插值、`!CG[1]` 下标访问。
- **结论**：Design 会话（18753）的 PML 取名彻底不可用。**取名/盘点只走 C# DB API**（见坑 31）。不要再在 Design 侧用 PML 取元件名。

## 坑 31：C# catinv 必须在 PDMS 主线程跑 DB API —— 否则 `START_TYPE_UNKNOWN`/空树（2026-10-18 修正）
- 原 catinv 直接从监听线程(ThreadPool)调 `DbElement.GetElement()/Members()` → 无 PDMS 线程上下文 → 输出 `START_TYPE_UNKNOWN`/`ROOT_TYPE_UNKNOWN`。
- 修正（已写进 `skills/pdms-bridge/assets/PdmsBridge.cs`）：
  1. `Start()` 里 `_uiCtx = SynchronizationContext.Current;`（Addin.Start 在 UI 线程上调用，捕获到 WindowsFormsSynchronizationContext）。
  2. catinv 分支 `_uiCtx.Send(d => { inv = RunCatInventory(); }, null)` 把盘点派发到 PDMS 主线程（同步等返回）。
  3. 起点用 `Aveva.Pdms.Shared.CurrentElement.Element`（=PML 的 `ce`，walk6 证 `for ce` 能取到 CATEG_1）；退化再用 `DbElement.GetElement()` 无参；再兜底 `DbElement.GetElement("/CATALOGUE")`。
     - **⚠️ CS0234 实锤（2026-10-19 用户编译报错）**：`Aveva.Pdms.Shared` 里**没有 `CE` 类型**！drawE3D.cs 的 `CE` 是 `using CE = Aveva.Pdms.Shared.CurrentElement;` 的**别名**，真实类名是 `CurrentElement`。写 `Aveva.Pdms.Shared.CE.Element` → `CS0234: 命名空间"Aveva.Pdms.Shared"中不存在类型或命名空间名"CE"`。正确：`Aveva.Pdms.Shared.CurrentElement.Element`（`P:Aveva.Pdms.Shared.CurrentElement.Element` 文档实证）。
  4. 取名 `el.GetAsString(Aveva.Pdms.Database.DbAttributeInstance.NAME)`（C# 里 Name 不是属性，是属性方法——对应坑30）。
- **编译注意**：① `el.DbRef` 在 12.1.SP4 **不存在**（文档零命中）→ 编译失败，已删除；② 需 `using Aveva.Pdms.Shared;` 且 csproj 引 `Aveva.Pdms.Shared.dll`（assets/mcp/addin/PdmsBridge.csproj 49-51 已加）；③ C# 编译器在沙箱被禁用，编译只能用户在 VS/msbuild 完成；④ Debug 输出路径 8 层 `..\` = `C:\AVEVA\Plant\PDMS12.1.SP4\`（即编即部署）；⑤ **编译源是 addin 子目录那份**，改完要同步（assets/PdmsBridge.cs → assets/mcp/addin/PdmsBridge.cs）；⑥ csproj 必须 `<PlatformTarget>x64</PlatformTarget>`（否则报 MSIL 与 `mscorlib.dll` AMD64 不匹配的警告/运行失败）；⑦ 编译前把所有 API 对照 `drawE3D.cs` 实战写法核一遍（它用的别名 `CE`/`ATT`/`DbType` 都别直接当类型名用）。
- 客户端 `C:\pdmsAgent\bridge_p18754\catinv.js`，用法 `node catinv.js 18753 <id>`，等待 15s。
- **⚠️ `_uiCtx.Send` 是死路，已废弃（2026-10-19 实锤重入死锁）**：改成 `_uiCtx.Send` 派发到 UI 线程后**必然死锁**——监听线程 Send 同步等 UI 线程，而 UI 线程执行 `RunCatInventory` 时内部 DB API 又向 UI 线程 Send，重入互相死等，catinv.js 15s 超时也无返回。PDMS 的 `Command.RunInPdms` 之所以不崩，正是因为它本就在 UI 线程派发、不重入。**正确做法见坑32：catinv 直接在监听线程调 DB API，起点用绝对路径 `/CATALOGUE`。**

## 坑 32：catinv 最终可用方案 —— 监听线程直调 DB API + `/CATALOGUE` 绝对路径起点（2026-10-19 定稿）
- **为什么不用 Send**：见坑31 末尾。`_uiCtx.Send` 重入死锁，永远别用。
- **为什么不用 `CurrentElement.Element` 当起点**：监听线程没有"当前元素"上下文，无参 `GetElement()`/`CurrentElement.Element` 在该线程返回 UNKNOWN 类型无效元素（第一次 catinv 输出 `START_TYPE_UNKNOWN` 即此因）。
- **正确做法（已写入 `PdmsBridge.cs` + `assets/mcp/addin/PdmsBridge.cs`）**：
  1. 直接在监听线程调 `RunCatInventory()`（不 Send、不 Post）；
  2. 起点 `Aveva.Pdms.Database.DbElement.GetElement("/CATALOGUE")`（绝对路径，cmd_930 证明确实存在；不依赖当前元素上下文，监听线程按路径解析即可）；
  3. 退化 `GetElement()` 无参；
  4. `WalkCat` 递归 `Members()`（限深6/限2000），每行 `层级.类型|名字`，取名 `GetAsString(DbAttributeInstance.NAME)`；
  5. 所有取值用 `try/catch` 安全包裹，异常信息落地不吞。
- 客户端 `C:\pdmsAgent\bridge_p18754\catinv.js`，`node catinv.js 18753 <id>`（15s 超时）。编译/部署/重启见坑31。
- **已验证**：18753 Design 桥能直接看到 Paragon 目录库（walk2/walk6 结构计数 1/1/1/0/0/0）；此 C# 方案绕开 Design 模块 PML 取名死（坑30），从 DB 层直接取名字。

## 坑 33：`(2,761) STRING=REAL` —— 变量首次赋值即定型，数值属性别用字符串初始化（2026-10-28 实锤，SWX 建库）
- **现象链**：结果文件只剩 3 字节 UTF-8 BOM → PDMS 弹模态框 `Error (2,761) Cannot assign variable to result - incompatible types (STRING=REAL)` → 命令处理器停等 OK（坑 27 那个机制）→ 之后所有 `$M` 全堵（桥只回空消息 `RunInPdms failed: `，后期连结果文件都不生成）。一次踩掉四条探针、白跑十几轮。
- **根因**（课程《PML基础》模块1「通过赋值声明变量类型」+ 模块3「表达式前后类型必须一致」）：变量**第一次赋值就定型**，之后换类型即 2761。最经典的自杀写法是"防御性初始化"：

```pml
!b1 = 'ERR'          -- 定型 STRING
!b1 = !!ce.bore      -- BORE 是 REAL → (2,761) STRING=REAL，宏当场断
```

- **正确写法**：数值属性用数值变量接（`!b1 = 0.0` → `!b1 = !!ce.bore`，兜底 `!b1 = -1.0`）；要文本形态用 `STRING(!!ce.bore)`；`DATE` 属性（`crdate`）可以直接进 STRING 变量（实测 `11:19 20 May 2013`），REAL 不行；计数器一律 `!c1 = 0` 起、标志位 `!g1 = false` 起、字符串才用 `'E'` 起——**一个变量一生只服务一种类型**。
- **与坑 27/29 合流**：这个错**弹模态框且 `handle ANY` 挡不住**（语句后明明跟了 handle 照样弹），而坑 29 又证 handle 块在 `$M` 里可能被整块跳过 → 结论一致：**防御只能写在写法层面**，OS 侧兜底用 `pdms-dialog`（见 `references/auto-close-watchdog.md`）（`dialog_watcher.py` / `python C:\PDMSSKILL\close_pdms_popup.py <des_pid>`）。
- **误判留案**（方法论）：本坑早期被误记成「`.split()`/`.length()` 等字符串方法杀宏」——**错的**，`.length()` 那次是 `!l1='ERR'` 后接 REAL 才炸。**判元凶必须按报错正文，别按"这条宏里唯一新鲜的构造"下结论。**

## 坑 34：`$M` 同步独占会话（宏没跑完第二笔必堵）；认端口别用 `Get-NetTCPConnection`（2026-10-28 实锤）
- `RunInPdms` **同步等宏跑完**（实测 114s 才回包，之后客户端还要轮询 result.txt ~20s）。宏跑完前**别往同一会话发第二笔**；第二笔的表现有两种：`{"ok":false,"error":"RunInPdms failed: "}`（空原因）**或连 openfile 都不执行（结果文件根本不生成）**——后者是更强的"会话被占"信号。客户端 socket 超时要给足。
- **`writefile` 缓冲到 `closefile` 才落盘**：宏跑几分钟期间结果文件恒 3 字节，**别据此判"崩了"**；判存活看 `bridge.log` 或下一条请求能否进。
- **认端口**：`netstat -ano | findstr :1875` 或 python `socket.connect_ex`。**PowerShell `Get-NetTCPConnection -State Listen` 在受限沙箱里恒返回 0 条**（连系统自身监听都不返回），会把"桥好好的"误判成"桥没起"，白等一轮没下发探针。
- 多开 des.exe（如 Design + Paragon）时同一进程可能出现多个监听口（base 冲突后自增），连 `bridge.log` 里记了 `started` 的那个都可能不是当前活口；有 `bridge_ports.txt`（PID|PORT|时间）台账就查台账。
- **制度提醒**：同一台机器上 Design / Paragon 两个会话常同时开，**不得把另一个会话当残留进程杀掉**；批量宏前先确认自己连的是哪个口。

## 坑 35（DBL）：点号 `.` 被解析成 UDA/UDET 属性引用符 —— 含点元素名在 DBL 里无解（2026-10-28 定案，A~J 十种构法全灭）
- **现象**：`(99,532) Undefined UDA or UDET name. An MDB with suitable definitions must be selected`（2026-10-23 首报同款）。迷惑点：**同一条宏里无点名的引用全部畅通**（`/XS-CATA` 系 355 条、等级侧 `/AM07.GASK.S15`），看着像"语法时好时坏"。
- **根因**：DBL 点分路径在**第一个 `.`** 切段，后半截一律当 **UDA/UDET 属性名**解析。元素名本身含点（典型 CATA `/PDMSPIPE.CATA-ANSI`）时：`PDMSPIPE` 当元素名、`CATA-ANSI` 当属性名 → 查无此属性 → 99,532。
- **已证伪**：A~J 十种构法系统性全灭（全点分 / 去 CATA 前缀 / 点斜混合 / 段内斜杠 / 上下文 `CATA` 行 + 相对引用 等）→ **DBL 文法结构性限制，不是写法能绕的**，不要再发第 K 种构法批次。
- **判据**：报错指向"属性名未定义"而不是"元素找不到"，且同宏内无点名引用全通 → 直接定性含点名病。
- **边界**：命令行/宏逐级导航可达（`CATA /PDMSPIPE.CATA-ANSI` → `CE /GASKET-PLASTIC` → `CE /ABGEAA3`），但只把 CE 停到位，**不等于**该路径能当 DBL 里 CATR/DETR/SPCOMPONENT 的引用值。
- **根治**：① 建库阶段禁止目录元素名含点（`SITE.CATA` 复合命名是隐患源头）；② 已有点名 → 导航式改名去点或建无点别名（库内存量引用按名解析不受影响）；③ 需要写引用时改走 PML 属性通道——⚠️ 裸宏写 `!!ce.catref` 曾把 des.exe 打成 AccessViolation，必须 `define object` 包裹（未验证，勿当已通）。
- 深度版见同目录技能 **`pdms-uda-udet-mdb`**（同错号两类根因：点分引用 vs 当前 MDB 缺 UDET/UDA）与 `assets/dbl_check.pml`（列出当前 CE 下所有含点名字元素）。

## 坑 36：Paragon(18753) 桥 `$M` 宏里 handle ANY 块【整块跳过】+ 变量跨宏存活 —— 探针纪律（2026-11-03 实锤）
- **坑 29 在 Paragon 桥同样成立**：p3 探针里所有 `handle ANY` 守卫全被跳过，`!!ce.type`/`!x.name` 明明能读却全回 `'NA'` 假象；删掉守卫同语句秒通。**18753 上写探针：一律无守卫、单步化、错了让弹窗炸出来**（模态框用 pdms_vision_popups.py 关）。
- **变量跨宏存活 + 首赋值定型（坑33 的会话级放大）**：同一 PDMS 会话里所有 `$M` 宏共享变量池。`!mm` 在上一个宏定型为 ARRAY 后，下一个宏 `!mm = !MM[!i]` 直接 `(2,761) ARRAY=STRING`，跟本宏写法无关——用户连贴两次同错才定位。**铁律：每个探针全新变量名（带轮次前缀如 !z9）**；复用必须先 `.delete()` 再赋值（**delete 后可换类型**，p5 验证：`!v=0` → 循环内 `!v.delete()` → `!v=!ca[!i]` 吃混合 REAL/STRING 数组全绿）。
- **coll all for ce 是递归收集（self + 全部后代）**：对 CATA 世界收集直接吐 14MB（MDS-Standard-Supports 全库 1108 只 GMSE 全出来）。层级收集先想清楚，别对高层用 all。
- **dbref 寻址全程可用，默认 CE 漂了无所谓**：`CE =15512/xxxx`、`coll all for $!ref`、`!!ce.owner` → `CE $!ow` 全部验证通过；`ce /name` 导航成功即漂（坑23），但只要拿到 dbref 就再也不依赖默认 CE。
- **Paragon 取名可读**（本会话实测 `!!ce.type`/`!!ce.name` 无守卫直读全通，与坑30 Design 侧全灭相反）。
- **成员枚举免 .dbref()**：coll 成员直接 `$!member` 插值进 `CE $!member`（元素/字符串两种形态都渲染成 `=db/num`），p10 全绿。

## 坑 37：`(2,761)` 家族第二型 `ARRAY=STRING` 与 REAL→STRING 正解（2026-11-03 实锤）
- **现象**：`!mm = !MM[!i]` 报 `ARRAY=STRING`——不是本行错，是 `!mm` 在**之前某个宏**里已定型 ARRAY（跨宏残留，见坑36）。`REAL=STRING`（坑33）与 `ARRAY=STRING` 同号不同型，**报错正文里的 "已有类型=新类型" 是唯一的判型证据**。
- **混合数组循环**：PDMS 属性数组（CPAR 等）可 REAL/STRING 混存（课程确认 Array 可存任意类型）。循环变量跨元素换型必炸 → **delete() 重定型法**（上）或每元素插值直落文件不经过中间变量。
- **REAL→STRING 正解**：`STRING()` 老函数在表达式里被解析成方法调用报 `(46,58) Method STRING could not be found`；**正解 `.String()` 方法**（nozzCheck.pmlfrm 实战验证：`!x.String().replace('mm','')`）或 `$!` 插值直落 writefile（插值即隐式转串）。
- **openfile 路径**：`/C:\dir\file.txt`（反斜杠+盘符）验证可用；路径写法各异时先跑 3 行 ping 探针定通道，再二分语法。

## 坑 38：PML 实战语法补充（nozzCheck.pmlfrm 2026-11-03 精读，AVEVA.PDMS.PMLNetTest 真实生产 form）
- **REAL↔STRING**：`.real()` / `.String()` / `.nint()`（就近取整）；字符串清洗 `.replace('mm','')`；拼接用 `&`（`'$!a' &'*'& '$!b'`）。
- **几何对象**：`object line(!pos1,!pos2)` → `.length()/.direction()/.ExtendEnd(!dis)/.EndPosition.wrt(Owner)`；`object Direction('N')` → `.angle(!dir)`；`object arc()` 圆弧定位 `AnglePosition(!deg)`；位置分量直改 `!pos.up = !x`、`!pos.east = !y`。
- **属性链式求值**：`var !x desc of Owner of catref of $!name`（一条 var 串到祖先属性）；`!ref.prop.apar` dbref 链式取 UDA；`pos of $!name wrt /*`。
- **form 骨架**：`setup form !!name` + `member .x is array/dbref` + `define method .m(!a is String)` + gadget 回调 `call |!this.m()|`；list 用 `setHeadings()/setRows()/rtext/.val/.dtext`（dtext[行] 可存数组做列拆分）；popup 菜单 `!this.list.popup = !this.menu`；`.activeTask(|MODAngle|)` + `show !!$!task` 做子窗路由。
- **方法体内可混发 PDMS 命令**：裸 `nozz`（类型导航）、`ori p1 is $!dir`、`pos $!pos`、`by u $!dis`。
- **特定错误码 handle**：`handle (2,785)` / `handle (2,751)` 比 handle ANY 精准；`label /gogogo` + 跳转做流程控制。
- **.pmlfrm 落 PMLLIB 的文件里中文注释无害**（nozzCheck 通篇中文注释正常跑）——坑外话：禁中文指的是 TTY 通道下发的宏。

## 坑 39：GMSE 原语属性正本在 `PDMSUI\cat\dflts\gmse-*`——SBOX 用 PXLE/PYLE/PZLE，不是 PHEI/PLEN/PWID（2026-11-03 实锤）
- **权威正本**：`C:\AVEVA\Plant\PDMS12.1.SP4\PDMSUI\cat\dflts\gmse-sbox`（及 gmse-scyl / gmse-sext / gmse-nsbo …）逐行列全各类型可写属性，建几何宏前先看这个，**不许凭记忆编属性名**。
- **SBOX**：只有 PX/PY/PZ（=盒心坐标）+ PXLE/PYLE/PZLE（=全长）+ CLFL/TUFL 旗标。**没有 PHEI/PLEN/PWID**（那是 SEXT/SCYL 的）——编了 Paragon 不报错但盒体零尺寸，派生即 fatal。
- **盒心语义铁证**：BT02 正版 SBOX `PX=0 PXLE=(TWICE PARAM 8)`——若 PX 是角、PXLE 是边长则盒体整体偏到 +X 单侧，只有"中心+全长"才对称成立；PZ=(-0.5*(RPRO PLOD+PARA[7]))+PZLE=PARAM 7 也正好是"贴管底的吊板"。
- **SCYL**：只有 PAXI(默认Y)/PDIS/PHEI/PDIA，**没有 PX/PY/PZ**——圆柱只能沿轴位移，不能横向偏置；横向偏置的柱体用 SEXT（PAAX/PBAX 转轴+顶点多边形）或干脆 SBOX 方柱。
- **SEXT**：PHEI + PX/PY/PZ + PAAX(默认Y)/PBAX(默认Z)；SLOOP/SVERTEX 顶点用 PX/PY（截面内坐标），正多边形顶点显式逐条写 `PX=( COS ( 30 ) * 0.5 * ATTRIB PARA[n] )`（AC10 原版句式，COS/SIN 直接可用）。
- **官方建库宏模板**：`PDMSUI\des\data\utmpls.upgrade`——`NEW GMSE /名` → `NEW SBOX` → 属性 → `END` → … → `END` 的块结构；**每个 NEW 块必须 END 收口**（否则 CE 落在新原语上，下一个 new 嵌套进上一个）；**成员原语一律无名**（正版 AC10/BT02 成员 NAME==自身 ref）。
- **表达式句式**：括号式 `( ATTRIB PARA[n ] )`（公式）、平式 `PARAM n`/`TWICE PARAM 8`（字面/倍数）——两种都验证过，别造第三种。
- **GMRE 切换**：CE=SCOM 后 `gmre =dbref`（与 m7g 验证过的 `dtre =dbref` 同款命令式赋值）。
- **建模通道取证**：bc.py 只能 exec；db_probe 等 kind=op 通道 params 必须是**嵌套 JSON 对象**（不是字符串），客户端 C:/PDMSSKILL/tmp/dbop.py。

## 坑 40：`=` 与 `/` 两种引用永不混写（2026-11-03 实锤，两次宏冻死同一根因）
- **按名**：`/MDS-AC`（斜杠开头，无等号）；**按库号**：`=15512/4415`（等号开头，无斜杠）。二选一。
- `CE =/MDS-AC` 这种 `=名` 杂交写法**无效**：PML 不报语法错，而是**弹模态 Error 框冻死宏线程**——表现为桥 exec_cmds 超时、result.txt 只有 BOM 无内容。
- db_probe 输出 `REF =15512/4415 ... NAME /MDS-AC` 两种并列展示，取用时别把 NAME 的斜杠抄进 REF 字段。
- **正解**：PML 里 CE 导航一律用 dbref（`CE =15512/4415`），ref 从 db_probe 的 REF 字段原样抄。

## 坑 41：GMSE 公式里的 `PARAM n` / `TWICE PARAM n` 不是命令行语法（2026-11-03 实锤，(47,15) 弹窗）
- **公式语言 ≠ 命令窗口语言**。库里存的 `PZLE=(TWICE PARAM 8)` / `PHEI=(PARAM 6)` 是 **GMSE 求值上下文**里的公式文本；**在 Command Window 直接敲 `PARAM 11` 无效** → 编译错 (47,15) → 弹模态框冻死线程。我照抄公式文本硬敲就是"自创语法"。
- **命令行唯一的正解（M10 已验证落库+派生通过）**：小写属性名 + 空格 + **括号表达式**，无等号：
  `pxle ( ATTRIB PARA[4] )` / `pzle ( ATTRIB PARA[2] + 2 * ATTRIB PARA[9] )`
  即：`PARAM n` → `ATTRIB PARA[n]`；`TWICE PARAM 8` → `2 * ATTRIB PARA[8]`。
- 反例（我自己编的，全部炸）：`PXLE=( TWICE PARAM 8 )`（等号+TWICE）、`pdia ( PARAM 11 )`（PARAM）、set_attr op 用 DB API 写表达式串（把 Paragon 干崩）。
- **`var !!UA1 !!UA1` 不是我的手写**：来自 `PdmsKit.cs:843 PmlVariableString(name)` 的回读包装，调用链 `SetAttrByCommand → DistanceIsMetric() → PmlVariableString("UA1")`（PdmsKit.cs:623 / 1173）。它报 `(2,427) Message System not initialized` = 会话已经进了错乱态（前面弹窗把消息系统搞死了），不是语法错，但说明 **set_attr 之路必须彻底放弃**。
- **写 GMSE 表达式属性的唯一通道**：纯 PML 宏（bc.py → $M），结构 new + 属性逐条命令式赋值；不要用 DB-API（SetAttribute(表达式串) 会挂死/崩会话）。

## 坑 42：`para` 命令不认 `ATT` token → `(41,232)` fatal（2026-11-03 实锤，SPCO 又崩两次）
- **现象**：宏里含 `para 80 ATT 89 115 16 100 10 70 25 15 26 25` → 弹 `Error (41,232)` → 随后 "A fatal error has been detected" 会话直接死。结果文件只留 BOM（错在第一条 writefile 之前）。
- **病根**：BT 族（BT02/BT03）的 PARA 里带 `ATT` 这个**非数字 token**，`para <值列表>` 命令逐项按数字解析，遇 `ATT` 报 (41,232)。AC 族 PARA（`80 89 89 80 ...`）全数字，`para` 能吃，所以 m6 当年设 AC10L 没炸。
- **根治**：**建 SPCO 时不要手写 `para` 行**。SPCO 一旦 `catr` 指向 SCON，**PARA 会自动从 SCON 继承**（含 `ATT` token 和 12 槽），无需也不可用 `para` 命令回写。建法 = `new spco` + `catr/ptre/gmre/dtre` 四引用 + `savework` 即可（m5 原样）。
- **诊断法**：写"分步打点宏"——每步写一个新标记文件（b01..b08），最后存在哪个标记就知道死在哪一步。本例 8/8 全绿证明去掉 `para` 后四引用+savework 全通。
- **派生验证的可逆招**：拿一个测试点（如 `/R80-U118`），`spre =<新SPCO ref>` + `savework` 看 GVOL，验完 `spre =<原SPCO ref>` 切回。实测切 `/BT03-3` GVOL=174387.6mm³、PARA 自动同步 PSIZE=12；切回 GVOL=180045、PSIZE=11，干净可逆。
