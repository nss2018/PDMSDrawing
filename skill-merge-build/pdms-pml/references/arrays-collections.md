# 数组与 Collections

> 模块 5/9 · 来源：课程 P8–P9

## 数组（Array）

**从字符串创建数组**：

```pml
!Str = 'Benz,Bmw,Audi'
!BestCar = !Str.Split(',')
q var !BestCar
-- <ARRAY>
-- [1]  <STRING> 'Benz'
-- [2]  <STRING> 'Bmw'
-- [3]  <STRING> 'Audi'
```

**逐个元素添加 / 数组追加**：

```pml
!BestCar[5] = 'Cadillac'        -- 索引赋值
!BestCar.Append('Lincoln')      -- 数组必须存在，否则提前声明 !a = array()
```

## 数组对象方法

| 方法 | 作用 |
| --- | --- |
| `Size()` | 数组元素数量 |
| `Append(value)` | 追加元素 |
| `AppendArray(Array)` | 追加数组 |
| `Clear()` | 删除全部元素 |
| `!MyArray[N].Delete()` | 删除单个元素 |
| `Delete()` | 删除数组 |
| `Compress()` | 压缩数组 |
| `Sort()` | 排序 |
| `Invert()` | 倒序 |
| `Find(value)` `FindFirst(value)` | 搜索 |
| `Unique()` | 合并重复项 |

> 实战（坑2）：**空数组调 `.Size()` 报 "no member SIZE"**，整宏中断 → 用计数器代替，或先判非空。

## 数组循环

**赋值循环（do values）**——将数组元素逐个赋给变量：

```pml
do !Name values !BestCar
$p Array element is $!Name
Enddo
```

**索引循环（do indices）**——将索引号逐个赋给变量：

```pml
do !n indices !BestCar
!Car = !BestCar[!n]
$p Array element $!n is $!Car
Enddo
```

## Collections（PDMS 中生成数组）

```pml
Var !PipeComps Collect all Branch members For CE
```

- 生成的 `!PipeComps` 是数组，保存的是元素的**参考号**。
- 不使用 `For CE` 则是从整个 MDB 中提取。
- `COLL` 是 `COLLECT` 的缩写，两种写法等价（项目里惯用 `COLL`）。

**类型用法示例**：

| 写法 | 含义 |
| --- | --- |
| `ALL` | 所有元素 |
| `ALL Pipe` | 所有的 Pipe |
| `BRANCH MEMBERS` | 所有管件，不包括 Tube |
| `ALL BRANCH MEMBERS` | 所有管件，包括 Tube |
| `ITEMS OF EQUI /D1201` | 设备中的所有基本体 |

**限制条件**（With 后面是一个表达式）：

```pml
Var !Elbows Coll All Elbow With (Abor gt 100) for CE
```

**限制区域**：

```pml
Var !Elbows Coll All Elbow Within W0N0U0 to W2000N2000U2000
Var !Elbows Coll All Elbow Exclusive Within Volume /D1201 1500
-- Exclusive 表示完全包含在空间内的元素才被选中
```

**追加**：

```pml
Var !Elbows Append Coll all Bend for CE
```

## 求值（Evaluate）——从参考中提取属性值

```pml
Var !Pipes Coll all Pipe for ce
Var !Names Eval name for all from !Pipes
!Names.sort().invert()
Do !n indices !Names
reorder $!Names[!n] before $!n
EndDo
```

> 实战（坑3/4/6）：
> - `coll` 收集到的成员**没有** `.type` / `.name`，要 `CE <该引用>`（`=` 形式可 CE）后读 `!!ce.type` / `!!ce.name`。
> - `coll PIPE for CE` 这类按类型收集返回的是 CE 自身，只能 `coll all for CE` + 逐条 `!!ce.type` 判型过滤；类型代码是缩写（`BRAN`、`EQUI`、`NOZZ` 等）。
> - `|...|` 字符串内 `$!arr[!n]` 数组索引插值会崩 → 先取到简单变量（`!s = !arr[!n]`）再插值。
