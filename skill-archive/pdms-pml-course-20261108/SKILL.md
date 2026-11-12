---
name: pdms-pml-course
version: 1.1.0
description: |
  PML（PDMS Macro Language）课程式系统教程与主题速查。按 AVEVA 中国《PML基础》培训教材完整走一遍 PML 语法：宏与参数化、变量与对象类型、元素定位与导航（Q REF / Q NAME / CE）、PDMS 属性读写、字符串/数字方法与标准函数、表达式、If/Do/Label 控制流、Handle 错误处理、数组与 Collections、Alert、对话框 Form 与控件、FILE 文件处理、菜单定制。写/读/调试 .pml / .pmlfnc / .pmlfrm 宏前，按主题查阅对应模块。
agent_created: true
---

# pdms-pml-course

PML 是 AVEVA PDMS / E3D 内置的宏语言。本技能把 AVEVA 中国官方培训教材《PML基础》（王元，`B-PML介绍.pdf`，15 页）整理成**课程式模块教程**：一个主题一个 reference，写宏时按需查、抄示例改，不需要一次读完。

与 `pdms-pml`（实战避坑）互补：本技能讲**标准写法**（课程原文），`pdms-pml` 讲**哪里会炸**（真实项目踩坑）。两个配合用。

## 何时使用

- 系统学习 PML，或按主题查标准语法（变量怎么声明、集合怎么收、对话框怎么写）。
- 写 `.pml` 宏、`.pmlfnc` 函数、`.pmlfrm` 对话框、USER 菜单文件之前。
- 用户问某条 PML 语法"标准写法是什么"、"课程里怎么讲的"。
- 拿不准语法时先查本技能，再对照 `pdms-pml` 的坑清单。

## 开始前

- 本技能是**纯语法资料库**：无运行时代码、无网络、无 PDMS 依赖，离线即可查阅。示例宏要在真实 PDMS / E3D 里执行才有效果。
- 语法以课程原文为准；标注「实战」的条目来自真实项目验证，与课程冲突时以实战为准。
- 写宏通用避坑（变量大小写、handle 就近保护、结果文件回读等）见 `pdms-pml` 技能的 `references/pml-gotchas.md`。

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

## 写宏工作流

1. **明确任务**：建元素？查库？弹窗交互？批量改属性？
2. **查路由表**读对应模块，抄最接近的示例。
3. **过硬约束**（下方清单），从 `assets/examples/` 挑模板改。
4. **命名规则**：宏文件名随意；函数/对话框文件名必须与对象名一致（`.pmlfnc` / `.pmlfrm`）。
5. **加载验证**：`$M /文件名` 执行；新函数/对话框先 `Pml Rehash All`。
6. **出错**：按 `(模块,错误码)` 定位；通用坑查 `pdms-pml`。

## 硬约束（写之前过一遍）

1. 变量名**大小写不敏感**：`!n1` 与 `!N1` 是同一个变量 → 全程小写。
2. 定位元素**优先用 ref**：名字会变，ref（`=234/702`）永不变；跨会话/跨宏定位一律 ref。
3. 判断表达式**必须是布尔值**：字符串先 `.Boolean()`。
4. 字符串属性赋值**必须单引号**：`Desc '$!Desc'`；Name 赋值**首字符必须 /**：`Name $!Name`。
5. 关键字（Word）属性**只取前 4 字符**，不加括号：`Purp $!Purp`。
6. `handle ANY` **只保护紧邻前一条语句**（就近原则）→ 每条可能失败的语句单独包。
7. Label 名 ≤16 字符，**不允许跳转进 Do 循环**。
8. 数组 `Append` 前数组必须已存在（否则先 `!a = array()`）。
9. `coll` 收集的成员**没有** `.type`/`.name`，要 `CE` 回去再读（实战坑3/4）。
10. 新函数/对话框加进 `Pml.index`：`Pml Rehash All`。
11. 改了对话题义文件后：`Pml reload form !!name` 或 `Kill !!name`。

## 如何迭代本技能

- **加新主题**：在 `references/` 新增一个模块文件 → 在本文「模块路由」表加一行 → 有配套示例就放 `assets/examples/`。
- **改现有主题**：直接改对应 reference；frontmatter `version` 升 patch（内容修正）或 minor（新增模块）。
- **发现课程与实战冲突**：在 reference 里用「实战」标注，并同步给 `pdms-pml` 的 gotchas。
- **来源**：AVEVA 中国《PML基础》培训教材（`B-PML介绍.pdf`）+ 本项目 `bridge/`、`iso/` 真实宏验证。

## 配套资源

`assets/examples/` 里是可抄改的课程示例：

| 文件 | 内容 |
| --- | --- |
| `hello_param.pml` | 参数化宏：建 EQUIP + BOX + CYL + 连接（课程 P2） |
| `vars_demo.pml` | 变量提取/赋值 Pipe 属性（课程 P3 练习） |
| `locate_demo.pml` | 元素定位：Q REF / Q NAME / CE 按 name·ref 定位 + coll 回定位（用户实战 + 坑3） |
| `control_demo.pml` | If/Do/Break/Skip/Handle 综合（课程 P6–P8） |
| `array_demo.pml` | Split/Append/遍历 + COLL 收集 + Eval（课程 P8–P9） |
| `area.pmlfnc` | 自定义函数：矩形面积（课程 P5） |
| `hello.pmlfrm` | 最简对话框（课程 P10） |
| `addvolumn.pmlfrm` | 组合控件对话框 + Apply 收集（课程 P11–P12 练习） |
| `file_demo.pml` | FILE 对象读写（课程 P13） |
| `user_menu.pml` | 自定义 USER 菜单文件（课程 P14） |

关联技能：`pdms-pml`（实战避坑）、`pdms-bridge`（AI 驱动 PDMS）、`pdms-iso`（ISO 出图）。
