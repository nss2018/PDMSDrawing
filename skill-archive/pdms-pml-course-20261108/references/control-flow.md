# 控制流与错误处理

> 模块 4/9 · 来源：课程 P6–P8

## 注释

```pml
--This is a new-style PML comment      -- 单行注释
$* The following lines calculate the new angle
!Z = !X + !Y   $* We are assuming both !X and !Y are REAL   -- 行尾注释
$(                                      -- 段落注释
skip if (!X EQ !Y)
…
$)
```

## 打印与退出

```pml
$P This text will be output to the screen.
$P This is an example of a much longer message $
that will be output to the screen        -- 行尾 $ 表示换行继续

if ( count EQ 0 ) then
return                                  -- 退出程序
endif
```

## If 条件判断

判断表达式中**必须是布尔值**；`ELSEIF`、`ELSE` 都是可选项：

```pml
!Type = Type
!OwnType = Type of Owner
IF (!Type eq 'BRAN') THEN
$P CE is Branch.
ELSEIF (!OwnType eq 'BRAN') THEN
$P CE is Branch member.
ELSE
$P CE is $!Type, Pls select Branch.
ENDIF
```

**常用判断例程**：

```pml
!x = real()
-- 判断变量值是否存在：函数写法 / 方法写法
If(Unset(!x)) then …          If(!x.Unset()) then …
If(Set(!x)) then …            If(!x.Set()) then …

-- 判断布尔值：字符串先转布尔
!MyString = 'True'
If(!MyString.Boolean()) then
…
endif

-- 判断元素是否有名字：Name 只有名字，FullName 是全路径名
!Name = Name
!FullName = FullName
If(!Name EQ !FullName) then
$p Named.
Else
$p No name.
endif
```

## Do 循环

```pml
!Total = 0
Do !x From 1 To 100 By 1
!Total = !Total + !x
Enddo
```

**中断循环（Break）**：

```pml
!Total = 0
Do !x From 1 To 100
!Total = !Total + !x
If(!Total gt 500) then
Break                    -- 或 Break If(!Total gt 500)
Endif
Enddo
```

**跳过循环（Skip）**（用 skip 跳过奇数）：

```pml
Do !x From 1 To 100
If(Int(!x / 2) NE (!x / 2)) then
Skip                     -- 或 Skip If(Int(!x / 2) NE (!x / 2))
Endif
!Total = !Total + !x
Enddo
```

## Label / GoLabel 跳转

用 `golabel` 可以跳转到标记行，不限制前后顺序：

```pml
Label /Start
…
GoLabel /Start
```

- Label 名最长 16 个字符（不包括 `/`）。
- **不允许跳转到 Do 循环中**，下例非法：

```pml
golabel /illegal
!Total = 0
do !x from 1 to 5
!Total = !Total + !x
label /illegal          -- 非法：跳入循环内部
enddo
```

## 错误提示与错误处理

测试程序时可能出现的错误形如 `(2,113) List exhausted`：**2 表示错误出现在 PDMS 中的哪个模块，113 是错误代码**。出现错误通常有三种结果：弹出一个警告框需用户确认 / 输出一个错误信息 / 从当前运行的程序中退出。

**处理特定错误码，使程序继续执行**：

```pml
Next
Handle (2,113)
$p Last element.
EndHandle
$P OK
```

**处理任何可能的错误**：

```pml
Next
Handle Any
EndHandle
$P OK
```

> 实战（坑8）：`handle ANY` 只保护**紧邻前一条语句**，不是块级作用域。每条可能失败的语句（属性读、CE、isodraw）前都要单独包 `handle ANY … endhandle`，或在 handle 体内用 `break`。详见 `pdms-pml` → `references/pml-gotchas.md`。
