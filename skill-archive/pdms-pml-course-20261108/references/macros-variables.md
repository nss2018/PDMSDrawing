# PML 基础、宏与变量

> 模块 1/8 · 来源：课程 P1–P3

## PML 是什么

- **PML**（Programmable Macro Language，可编程宏语言）：PDMS 内置宏语言。PML2 基于面向对象（Object Oriented）概念，支持用户自定义对象类型，与 PDMS 无缝连接，内置丰富的函数、方法及对象。
- **PML 构成**：
  - **Macros**：包含 PDMS 命令序列的 ASCII 文件，通过 `$M /FILENAME` 执行。
  - **PML 语言本体**：变量（Variables）、判断语句（If Construct）、循环（Do loops）、错误处理（Error Handling）、文件处理（Files and Directories）。
  - **窗体和菜单**：PDMS 大多数应用程序都由对话框（Forms）和菜单（Menus）驱动。

## 一个简单的宏

```
MYFILE                      -- 文件内容：PDMS 命令序列
NEW EQUIP /FRED
NEW BOX
XLEN 300 YLEN 400 ZLEN 600
NEW CYL DIA 400 HEI 600
CONN P1 TO P2 OF PREV
```

运行：`$M /MyFile`

> 实战：课程为简洁省略了 `END`（`NEW` 后紧跟另一个 `NEW` 时 PDMS 会自动结束上一个元素定义），真实项目宏里建议每个 `NEW…END` 显式配对，可读性和容错更好。

## 参数化宏

文件名后面的字符串是**参数**，最多 9 个、空格分隔：

```pml
-- MYFILE 内容
NEW EQUIP /$1
NEW BOX
XLEN $2 YLEN $3 ZLEN $4
NEW CYL DIA $3 HEI $4
CONN P1 TO P2 OF PREV
```

```sh
$M /MyFile NEWEQUIP 300 400 600
```

文本字符串作为单个参数输入，用 `$<` 和 `$>` 分隔符（它们之间的任何字符都被认为是一个参数）：

```sh
$M /MyFile $< PML TRAINING $> 55 66
```

## 变量是对象

- PML2 中变量是一个**对象（Object）**：每个对象有唯一的名字；每个对象都有一套关联函数，称为**方法（Methods）**；生成对象时必须指明**对象类型（Object type）**，不同类型对应不同方法。

**内置（Build-in）类型**：

| 类型 | 示例 |
|---|---|
| String（字符串） | `'Hello World'` |
| Real（实数，整数含在实数类型中） | `99` |
| Boolean（布尔） | `True` / `False` |
| Array（数组，可存任意类型） | `!arr[1] = 'x'` |

**系统定义（System-defined）类型**：PDMS 中的变量类型，如 Position、Reference。

**用户自定义（User-defined）类型**：见 `pdms-pml`（`.pmlobj`）。

## 生成、查询、删除变量

```pml
!MyString = 'Hello World'    -- 通过赋值声明变量类型
q var !MyString              -- 显示 <STRING> 'Hello World'

!Length = Real()             -- 直接声明变量类型（未设置）
q var !Length                -- 显示 <REAL> Unset

!MyString.Delete()           -- 用方法删除变量
```

## 命名规则

| 规则 | 说明 |
| --- | --- |
| `!x` | **局部**变量 |
| `!!x` | **全局**变量 |
| 长度 | 最长 16 个字符（不包括 `!` 和 `!!`） |
| 字符 | 可包含字母和数字；**不能以数字和点 `.` 开头** |
| 大小写 | **不敏感**（`!X` 与 `!x` 是同一个变量，实战坑1） |
| 建议 | 一个变量一个用途，起有意义的名字，书写时区分大小写便于阅读 |

## PDMS 属性类型（系统定义变量类型）

| 类型 | 典型属性 |
| --- | --- |
| String | Name, Description, Function |
| Real | Angle, Temperature |
| Boolean | Lock, Shop, Built |
| Array | Level |
| Reference | Spref, Catref, Lstube |
| Word（关键字） | Type, Purpose |
| Position | Position, Hposition |
| Orientation | Orientation |
| Direction | HDirection |

## 提取属性到变量

变量名 = 属性名：

```pml
!Name = Name
!Desc = Description
!Pspec = Pspec
!Temp = Temp
!Purp = Purp
!Ref = Ref          -- 参考类型
q var !Ref          -- 查询属性类型
```

## 用变量给 PDMS 属性赋值

`属性名 $变量名`——`$` 表示将变量的值转换成字符串（PDMS 命令行只接受字符串的值）：

| 属性类型 | 写法 | 注意 |
| --- | --- | --- |
| 名字 | `Name $!Name` | 第一个字符必须是 `/` |
| 字符串 | `Desc '$!Desc'` | 必须用单引号括起来 |
| 实数 | `Temp $!Temp` | — |
| 参考 | `Pspec $!Pspec` | 参考元素必须存在 |
| 关键字 | `Purp $!Purp` | 只取前四个字符，不用括号 |

**课程练习**：新建 Pipe，设置 `Desc 'My Description'` / `Temp 100` / `Pspec /A3B` / `Purp PIPI`；从 Pipe 提取 Desc/Temp/Pspec/Purp 四个属性；再新建一个 Pipe，属性与前一个完全一样。完整可运行版见 `assets/examples/vars_demo.pml`。
