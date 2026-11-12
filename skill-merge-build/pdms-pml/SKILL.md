---
name: pdms-pml
version: 2.3.0
description: "AVEVA PDMS / E3D 的 PML（PDMS Macro Language）一站式技能：课程式系统教程 + 实战避坑速查。教程部分按 AVEVA 中国《PML基础》教材覆盖全部主题——宏与参数化、变量与对象类型、元素定位导航（Q REF / Q NAME / CE）、PDMS 属性读写、字符串/数字方法与标准函数、表达式、If/Do/Label 控制流、Handle 错误处理、数组与 Collections、Alert、对话框 Form 与控件、FILE 文件处理、菜单定制。避坑部分为真实项目（PDMS 12.1.SP4）逐条验证的 35 个坑——变量大小写、空数组 .size()、coll 收集、collect for WORLD 杀宏、类型缩写、字符串插值崩溃、数组属性插值、Name already used 语义、设备 ORI 坐标扭转、TTY ^^ 注入、handle 就近保护、结果文件回读、模态错误框冻线程、(2,761) 变量首次赋值即定型、$M 同步独占会话与认端口、DBL 点号被解析成 UDA/UDET 属性引用符（含点元素名在 DBL 里无解，A~J 十种构法全灭）等。适用于系统学习 PML、按主题查标准写法、写/读/调试 .pml / .pmlfnc / .pmlfrm / USER 菜单宏、排查 incompatible types 等诡异报错、让 AI 生成 PML 宏。"
agent_created: true
---

# pdms-pml — PML 系统教程 + 实战避坑

> v2.3.0（2026-11-08）：清理旧副本 `pdms-pml-course`——它是 v2.1.0 合并**之前**的课程版，3 处 reference 差异（外链改写 / `.pmlobj` 说明）早已并入本技能，内容无缺失；原目录归档到 `D:\repos\pdmsDrawing\skill-archive\pdms-pml-course-20261108`，**外部引用统一走 `pdms-pml`**。
>
> v2.2.0（2026-10-28）：并入 Accio 侧 SWX 建库现场的最新三条实锤——坑 33 `(2,761) STRING=REAL` 变量首次赋值即定型、坑 34 `$M` 同步独占会话与认端口、坑 35 **DBL 点号被解析成 UDA/UDET 属性引用符（含点元素名在 DBL 里无解，A~J 十种构法全灭）**。坑清单从 13 条扩到 35 条（`references/pml-gotchas.md` 为完整台账）。
>
> v2.1.0：由原 `pdms-pml`（实战避坑）与 `pdms-pml-course`（课程式教程）合并而成，一个技能覆盖两半：
> - **标准写法**：课程原文，一个主题一个 reference，写宏时按需查、抄示例改；
> - **哪里会炸**：真实项目逐条验证的坑清单，动手前必过。
>
> 来源：AVEVA 中国《PML基础》培训教材（`B-PML介绍.pdf`）+ 本项目 `bridge/`、`iso/` 真实宏（PDMS 12.1.SP4）验证。标注「实战」的条目与课程冲突时，以实战为准。

## 何时使用

- 系统学习 PML，或按主题查标准语法（变量怎么声明、集合怎么收、对话框怎么写）。
- 写 / 调试 `.pml` / `.pmlfnc` 函数 / `.pmlfrm` 对话框 / USER 菜单文件之前。
- 用户问某条 PML 语法"标准写法是什么"、"课程里怎么讲的"。
- 用户让 AI 生成 PML 代码（出图、建元素、查属性）。
- PML 报 `incompatible types`、宏中断、`no member SIZE`、`coll` 收集不到等诡异错误。
- 用户问 `$M` 和 `DO`、`CE`、`NEW`、属性读写的写法。

## 前置依赖与环境声明

- 本技能是**纯语法资料库 + 可复用代码示例**：无运行时代码、无网络连接、无宿主依赖，**离线即可使用**，审核/安装无需 AVEVA PDMS。
- 示例宏要在真实 PDMS / E3D 里**执行**才出效果，但阅读、检索、让 AI 据此生成 PML 完全不需要 PDMS。执行依赖由最终用户在其已授权环境内提供。
- 语法以课程原文为准；「实战」标注条目来自真实项目验证。

## 模块路由（按需读，不要一次全读）

| 我要… | 读 |
| --- | --- |
| PML 是什么、写/运行宏、参数化宏 | [macros-variables](references/macros-variables.md) |
| 变量声明/命名/删除、PDMS 属性读写 | [macros-variables](references/macros-variables.md) |
| 定位/导航到某个元素：Q REF、Q NAME、CE、NEXT/PRE | [element-navigation](references/element-navigation.md) |
| 字符串/数组/类型转换方法、表达式、标准函数、自定义函数 | [methods-functions](references/methods-functions.md) |
| 注释、$P、If/Elseif、Do/Break/Skip、Label/GoLabel、Handle | [control-flow](references/control-flow.md) |
| 数组、Collections 收集、Eval 求值 | [arrays-collections](references/arrays-collections.md) |
| Alert 错误/消息/确认/输入、clock、form display | [alerts](references/alerts.md) |
| 对话框 Form、控件、定位、Callback、构造方法、View 控件 | [forms](references/forms.md) |
| FILE 对象读写、fileBrowser | [files](references/files.md) |
| 菜单条 Bar、菜单项 Menu、USER 菜单文件 | [menus](references/menus.md) |
| **写宏前必过**：35 个实战坑（逐条证据 + 修复） | [pml-gotchas](references/pml-gotchas.md) |
| 速查：变量 / CE / 集合 / 控制流 / NEW…END / 文件 I/O / ISO 命令 / 类型缩写 | [pml-syntax](references/pml-syntax.md) |

## 硬约束（写之前过一遍）

1. 变量名**大小写不敏感**：`!n1` 与 `!N1` 是同一个变量 → 全程小写。
2. 定位元素**优先用 ref**：名字会变，ref（`=234/702`）永不变；跨会话/跨宏定位一律 ref。
3. 判断表达式**必须是布尔值**：字符串先 `.Boolean()`。
4. 字符串属性赋值**必须单引号**：`Desc '$!Desc'`；Name 赋值**首字符必须 /**：`Name $!Name`。
5. 关键字（Word）属性**只取前 4 字符**，不加括号：`Purp $!Purp`。
6. `handle ANY` **只保护紧邻前一条语句**（就近原则）→ 每条可能失败的语句单独包。
7. Label 名 ≤16 字符，**不允许跳转进 Do 循环**。
8. 数组 `Append` 前数组必须已存在（否则先 `!a = array()`）。
9. `coll` 收集的成员**没有** `.type`/`.name`，要 `CE` 回去再读；`coll <TYPE> for CE` 不可用，只能 `coll all for CE` + 逐条判型过滤。
10. 类型代码用**缩写**：`BRAN`（非 BRANCH）、`PIPE`、`EQUI`、`NOZZ`、`TUBI` 等。
11. `|...|` 字符串内**不要**直接插值数组索引 `$!arr[!i]`（会崩）→ 先取到简单变量再插值。
12. 建元素用 `NEW…END`，属性续行**无等号**，END 与 NEW 一一对应。
13. 新函数/对话框加进 `Pml.index`：`Pml Rehash All`；改了对话题义文件后：`Pml reload form !!name` 或 `Kill !!name`。
14. **变量首次赋值即定型**（坑 33）：`!b = 'ERR'` 之后再 `!b = !!ce.bore`（REAL）→ `(2,761) incompatible types`，宏断 + 弹模态框。数值用 `0.0` 起、计数用 `0` 起、标志用 `false` 起，一个变量一生一种类型。
15. **DBL 里的点号是 UDA/UDET 属性引用符**（坑 35）：元素名含点（`/PDMSPIPE.CATA-ANSI`）就不能用点分形式引用它，报 `(99,532)`；A~J 十种写法全灭，**唯一正解是名字去点或改用 `CE` 导航**。
16. **`$M` 同步独占会话**（坑 34）：宏跑完前第二笔必堵（空 `RunInPdms failed:` 或结果文件不生成）；`writefile` 到 `closefile` 才落盘，别拿"文件 3 字节"判死；认端口用 `netstat -ano`，`Get-NetTCPConnection` 在受限沙箱恒返回 0 条。

## 写宏工作流

1. **明确任务**：建元素？查库？弹窗交互？批量改属性？
2. **查路由表**读对应模块，抄最接近的示例。
3. **过硬约束**（上方清单），从 `assets/examples/` 挑模板改。
4. **命名规则**：宏文件名随意；函数/对话框文件名必须与对象名一致（`.pmlfnc` / `.pmlfrm`）。
5. **加载验证**：`$M /文件名` 执行；新函数/对话框先 `Pml Rehash All`。
6. **出错**：按 `(模块,错误码)` 定位；通用坑查 `references/pml-gotchas.md`。

## PML 基础速查

| 语法 | 说明 | 示例 |
|---|---|---|
| `!x = 值` | 变量赋值（**大小写不敏感**，见坑 1） | `!n1 = 3.14` |
| `$!x` | 变量插值进字符串/命令 | `CE $!P1NAME` |
| `!!ce.type` | 读当前元素（CE）属性（双感叹号 = 表达式求值） | `!!ce.name` |
| `CE /A/B/C` | 把元素设为当前元素（支持 `=` 前缀引用也可 CE） | `CE /*` 回到 world 顶层 |
| `NEW TYPE /NAME` … `END` | 新建元素，属性续行（**无等号**），END 与 NEW 一一对应 | 见下 |
| `handle ANY` … `endhandle` | 异常捕获（**只保护紧邻前一条语句**） | 见坑 8 |
| `$M "<路径>"` | 加载并执行一个 PML 宏文件（推荐，进程内/通道通用） | `$M %PMLLIB%/Common_Tools/iso_diag.pmlmac` |
| `var !x isodraw status` | 读 ISO 出图状态等系统变量 | `var !S isodraw status` |

**建图/建元素模板（已验证语法）**：

```pml
CE /AGTREG_1
handle ANY
  NEW REGI /AGTREG_1      -- 已存在则跳过
endif
NEW DRWG /AGTDRAW_1
NEW SHEE /AGTDRAW_1/S1
NEW VIEW /AGTDRAW_1/S1/V1
  VSCA  0.02
  VTYP  Universal
  SIZE  1189 841
  XYPS  X500 Y400
  DIR   D
END
END
NEW LIBY /AGTDRAW_1/LIBY
NEW DLLB /AGTDRAW_1/DRAWLIST
NEW IDLI /AGTDRAW_1/DRAWLIST/DRWG
END
END
```

## 实战踩坑清单（台账 35 条；本表列常用 13 条 + 最新 3 条）

完整证据与修复见 [references/pml-gotchas.md](references/pml-gotchas.md)，要点：

| # | 坑 | 一句话修复 |
|---|---|---|
| 1 | 变量名大小写不敏感 → 类型冲突 | 全程小写 + 有意义命名 |
| 2 | 空数组调 `.size()` 报 "no member SIZE" | 用计数器代替，或先判非空 |
| 3 | `coll` 成员无 `.type` / `.name` | `!W[!i].string()` + `CE <引用>` 后读 `!!ce.type` |
| 4 | `coll <TYPE> for CE` 不可用 | 只能 `coll all for CE` + 逐条判型过滤 |
| 5 | 类型代码是缩写 | 判型用 `BRAN` / `PIPE` / `EQUI` … |
| 6 | `\|...\|` 内 `$!arr[!i]` 索引插值会崩 | 先取到简单变量再插值 |
| 7 | 宏内 CP 行盘符路径 / `mess` 被 `^^` 注入破坏 | 宏里不写盘符路径、不用 `mess`；用 `$M "%ENV%/..."` 形式 |
| 8 | `handle ANY` 只保护紧邻前一条语句 | 每条可能失败的语句单独包 |
| 9 | 结果文件是唯一稳定回读通道 | `openfile write` + `writefile` + `closefile`；回读赋值全放 handle 块内 |
| 10 | `collect ... for WORLD` 静默杀死宏 | collect 收敛到具体设备/ZONE，逐层收集 |
| 11 | `$!arr.Size` 插值报 "Module not defined" | 先 `!n = !arr.Size` 赋标量再 `$!n` |
| 12 | `Name /xxx already used` = 其实建成功了 | 先 `CE` 探测确认现状，别按失败重建 |
| 13 | 设备 ORI 扭转本地坐标系 → 方向词全错位 | 设备不设 ORI，朝向写在原语上；旋转用 `ROTATE ABOUT U BY 90 THRO CE` |
| 33 | `(2,761) STRING=REAL`：变量首次赋值即定型（10-28 实锤） | 数值属性用 `!b = 0.0` 接或 `STRING(...)`，别用 `'ERR'` 初始化 |
| 34 | `$M` 同步独占会话；`Get-NetTCPConnection` 恒返回 0 条 | 宏跑完前别发第二笔；认端口用 `netstat -ano` / `socket.connect_ex` |
| 35（DBL） | **点号被解析成 UDA/UDET 属性引用符**：含点元素名在 DBL 无解（A~J 全灭） | 名字去点 / 改用 `CE` 导航；详见坑 35 + 技能 `pdms-uda-udet-mdb` |

> 坑 **14–32** 是 10-17~10-19 那轮电解槽建模与桥接的实锤（collect for WORLD 杀宏、`$!arr.Size` 插值、空数组 `do indices` 致命、链式 `.name` 崩、遗留文件句柄锁死、**模态错误框冻住命令线程**、`handle ANY` 块在 `$M` 里被整块跳过、Design 侧 PML 取名全灭 → 取名只走 C# DB API、catinv 定稿方案…），按需直接查 [pml-gotchas](references/pml-gotchas.md)。

## 结果文件回读模板（复制即用）

```pml
openfile /D:/pdmsAgent/bridge/result.txt write !F1
handle ANY
  openfile /D:/pdmsAgent/bridge/result.txt overwrite !F1
endhandle
!T1 = 'UNK'
handle ANY
  !T1 = !!ce.type
endhandle
writefile $!F1 |TYPE_$!T1|
closefile $!F1
```

## 给 AI 生成 PML 的约束

让智能体写 PML 时，强制套用：全程小写变量、**每种变量用同类型初始化到底（数值 `0.0` / 计数 `0` / 标志 `false` / 字符串 `'E'`）**、每条可能失败语句包 `handle ANY`（但别指望它兜弹窗，坑 27/29/33）、回读必落结果文件、数组别在 `|...|` 里插值、类型用缩写、建元素用 `NEW…END` 续行无等号、**引用元素优先 ref / `CE` 导航，含点名字一律不走点分引用（坑 35）**、一条宏只做一件事且跑完再发下一条（坑 34）。详见 `references/pml-gotchas.md`。

## 配套资源

`assets/examples/`（13 个可抄改示例）：

| 文件 | 内容 | 来源 |
| --- | --- | --- |
| `hello_param.pml` | 参数化宏：建 EQUIP + BOX + CYL + 连接（课程 P2） | 课程 |
| `vars_demo.pml` | 变量提取/赋值 Pipe 属性（课程 P3 练习） | 课程 |
| `locate_demo.pml` | 元素定位：Q REF / Q NAME / CE 按 name·ref 定位 + coll 回定位（实战坑3） | 课程+实战 |
| `control_demo.pml` | If/Do/Break/Skip/Handle 综合（课程 P6–P8） | 课程 |
| `array_demo.pml` | Split/Append/遍历 + COLL 收集 + Eval（课程 P8–P9） | 课程 |
| `area.pmlfnc` | 自定义函数：矩形面积（课程 P5） | 课程 |
| `hello.pmlfrm` | 最简对话框（课程 P10） | 课程 |
| `addvolumn.pmlfrm` | 组合控件对话框 + Apply 收集（课程 P11–P12 练习） | 课程 |
| `file_demo.pml` | FILE 对象读写（课程 P13） | 课程 |
| `user_menu.pml` | 自定义 USER 菜单文件（课程 P14） | 课程 |
| `cmd_1.pml` | PING 回读 | 实战 |
| `m_create.pml` | 建图链 + 回读 | 实战 |
| `iso_gen.pml` | ISO 出图完整宏：world 枚举 + 首条 PIPE 出图 | 实战 |

`references/`（11 个模块）：课程 9 个主题模块（macros-variables / element-navigation / methods-functions / control-flow / arrays-collections / alerts / forms / files / menus）+ `pml-gotchas.md`（**35 条坑**逐条证据与修复，唯一完整台账）+ `pml-syntax.md`（完整语法速查）。

## 如何迭代本技能

- **加新主题**：在 `references/` 新增一个模块文件 → 在本文「模块路由」表加一行 → 有配套示例就放 `assets/examples/`。
- **改现有主题**：直接改对应 reference；frontmatter `version` 升 patch（内容修正）或 minor（新增模块）。
- **发现课程与实战冲突**：在 reference 里用「实战」标注，并同步更新 `pml-gotchas.md`。
- **来源**：AVEVA 中国《PML基础》培训教材（`B-PML介绍.pdf`）+ 本项目 `bridge/`、`iso/` 真实宏验证。

## 关联技能

`pdms-bridge`（AI 驱动 PDMS）、`pdms-iso`（ISO 出图）、`pdms-uda-udet-mdb`（`(99,532) Undefined UDA or UDET` 两类根因 + 含点名诊断脚本，对应坑 35）、`pdms-dialog`（模态弹窗扫描/取证/自动关，对应坑 27/33）、`pdms-run-macro`（宏加载 / `%PMLLIB%` / `pml.index`）、`pdms-database`（库与属性层）。
