# Alert 警告对象

> 模块 6/9 · 来源：课程 P9

## 无返回值的 Alert

有三种类型的无返回值警告：

```pml
!!Alert.Error( 'You cannot do this!' )
!!Alert.Message( 'Saving your data now' )
!!Alert.Warning( 'Do not press this button again!' )
```

缺省情况下，警告窗口出现在光标附近；可用 X、Y 值指定其在屏幕上的位置——整个屏幕左上角是 `0,0`，右下角是 `1,1`：

```pml
!!Alert.Error( 'You cannot do this!' , 0.25, 0.1)
```

## 带返回值的警告对象（Confirm / Question / Input）

| 对象 | 返回值 |
| --- | --- |
| `!!Alert.Confirm(...)` | `'YES'` / `'NO'` |
| `!!Alert.Question(...)` | `'YES'` / `'NO'` / `'CANCEL'` |
| `!!Alert.Input(...)` | 字符串值 |

```pml
-- 确认
!Answer = !!Alert.Confirm( 'Are you sure!' )

-- 询问
!Answer = !!Alert.Question( 'OK to delete Site?' )

-- 输入：第一个值是输入提示，第二个值是缺省值
!Answer = !!Alert.Input( 'Enter Width of Floor','10' )
```

## 显示输出窗口

```pml
var !isshown form _CADCBTH display
if(!isshown eq 'Off') then
show _CADCREQ
endif
```

## 时钟

```pml
var !date clock date
var !time clock time
$p Begin report at $!date $!time
```
