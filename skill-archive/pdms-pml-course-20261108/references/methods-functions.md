# 方法、表达式与函数

> 模块 3/9 · 来源：课程 P3–P5

## 方法 vs 函数

- **函数（Function）**：执行特定功能的子程序。
- **方法（Method）**：对象（变量）的函数。方法**不改变对象的类型和对象中的值**。
- 权威手册：Cadcentre Software Customisation Reference Manual。

## String 对象方法

| 方法 | 作用 |
| --- | --- |
| `Length()` | 字符串长度 |
| `Real()` `Position()` `Boolean()` | 类型转换 |
| `LowCase()` `UpCase()` | 大小写转换 |
| `After(str2)` `Before(str2)` `Substring(index)` | 两端截取 |
| `Substring(index,nchars)` | 中间截取 |
| `Part(nth)` `Part(nth,delim)` | 分隔符截取 |
| `Replace(str2,str3)` | 替换 |
| `Match(str2)` `MatchWild(str2)` | 匹配 |
| `Split()` | 分割（返回数组） |
| `Trim()` `Trim(option)` | 修剪 |

方法可**链式调用**：

```pml
!line = 'hello how are you'
!newline = !line.after('hello').trim().upcase()
q var !newline        -- <String> 'HOW ARE YOU'
```

## 表达式运算符

| 类别 | 运算符 |
| --- | --- |
| 算术 | `+ - * /` |
| 合并符 | `&` |
| 比较 | `EQ NE LT LE GT GE` |
| 布尔 | `NOT AND OR` |

- 表达式可以嵌套。
- 表达式**前后的类型必须一致**。

```pml
!X = 64
!Y = 32
!Z = !X + !Y          -- 96
!A = 'Hello '
!B = 'World'
!AB = !A + !B         -- 'Hello World'
!XY = !X & !Y         -- '6432'（& 是字符串合并）
```

## 标准数字函数

`SIN COS TAN ASIN ACOS ATAN` · `SQR POW ABS`（开方 / 乘方 / 绝对值）· `LOG ALOG`（对数 / 反对数）· `INT NINT`（取整 / 四舍五入取整）· `MATCH DMATCH`（匹配 / 中文字符匹配）· `MAX MIN`（最大值 / 最小值）

```pml
!s = 30 * sin(45)
!len = Length('abcdef')
!t = pow(20,2)
!sqr = sqr(4)
!m = match('abcdef','cd')
!f = (match(Funcof Zone,'Piping') gt 0)
```

## 标准字符串函数

`AFTER BEFORE` · `LOWCASE UPCASE` · `PART` · `REPLACE` · `STRING` · `SUBS DSUBS` · `TRIM`

```pml
AFTER('abcdef','cd')                 -- 'ef'
UPCASE('False')                      -- 'FALSE'
PART('/PIPE45/B9',-1,'/')            -- 'B9'（Part 默认字符串前后都有分隔符）
PART('aa bb cc',2)                   -- 'bb'
REPLACE('cat dog','cat','dog')       -- 'dog dog'
STRING(POS)                          -- 'W1000 N20000 U18000'
STRING(1,'D3')                       -- '1.000'
STRING(9*9 LT 100)                   -- 'TRUE'
SUBSTRING('abcdef',3,2)              -- 'cd'
TRIM('How now','LRM')                -- 'How now'
TRIM('10.3000','R','0')              -- '10.3'
!up = after(str(pos),'U')            -- str() 转字符串后再截取
```

在 Report 中设置过滤条件、在 List > Add > Selection 中定义选集时用，例如 `(match(str(Ori),'U') gt 0)`——Ori 需要先转换成字符串类型。

## 自定义函数

函数定义成**全局变量**；**文件名必须与函数名一样，后缀是小写的 `pmlfnc`**：

```pml
-- area.pmlfnc
define function !!Area( !Length is REAL, !Width is REAL ) is REAL
!Area = !Length * !Width
return !Area                      -- 函数有两个参数、一个返回值
Endfunction
```

存放位置（与 `.pmlfrm`、`.pmlobj` 并列）：

```
Pmllib/NewPml/
  functions/  area.pmlfnc
  forms/      xxx.pmlfrm
  objects/    xxx.pmlobj
```

把新函数加进索引文件 `Pml.index` 后即可调用并得到返回值：

```pml
Pml Rehash All
!MyArea = !!Area(6,4)
```

> 课程练习：新建函数计算圆的面积并测试。
