# 对话框 Form 与控件

> 模块 7/9 · 来源：课程 P10–P13

## 最简单的对话框

```pml
setup form !!hello
Title 'Hello'
paragraph .Message text 'Hello world'
button .bye 'Goodbye' OK
exit
```

- `paragraph .Message`：文字控件 + 控件名。
- `button .bye`：按钮控件 + 控件名。
- `OK`：对话框控制内置属性，关闭对话框。

**存放位置与调用**：文件名与对象名一样，后缀是小写的 `pmlfrm`（`Pmllib/NewPml/forms/`）；把新对话框加进索引文件 `Pml.index`（`Pml Rehash All`）后：

```pml
Show !!hello          -- 显示对话框
Hide !!hello          -- 关闭对话框
```

## Callbacks（控件的响应操作）

控件的 `callback` 成员用于指定控件的响应操作，可以是：

```pml
!this.show.Callback   = 'show !!hello'                    -- 显示另一个对话框 form
!this.remove.Callback = 'remove all'                      -- 直接执行一条命令 command
!this.area.Callback   = '!area = !!area(!length,!width)'  -- 调用一个函数 function
!this.apply.Callback  = '!this.Apply()'                   -- 调用一个方法 method
```

## 缺省构造方法（Constructor method）

```pml
setup form !!hello
Title 'Hello'
paragraph .Message text 'Hello world'
text .input 'Enter text' width 10 is string    -- 字符串类型的编辑框
button .bye 'Goodbye' OK
exit
Define method .hello()                         -- 与 Form 同名、不带参数
!this.input.callback = '!this.doinput()'
endmethod
Define method .doinput()
!this.message.val = !this.input.val            -- !this = !!hello；val 表示 value
endmethod
```

缺省构造方法的特点：

- 与 Form 同名、不带参数；调用对话框时**自动执行**。
- 一般用于设置控件的缺省值和回调方法。
- 再次显示对话框时不再执行（定义内容已全部加载到内存中）。
- `Kill !!hello` 从内存中清除已加载的定义内容。
- 对话框的定义文件被修改后需要重新加载：`Pml reload form !!hello`。

## 对话框布局（Form Layout）

- 对话框有一个隐含的定位网格，原点在 Form 的**左上角**。
- 每个控件（Gadget）有四个隐含变量可以用于参考定位：`XMIN`、`XMAX`、`YMIN`、`YMAX`。

**自动定位（Auto-placement）**：

```pml
PATH Down        -- 下一个控件在前一个控件的下方
PATH up
PATH Right       -- 缺省
PATH Left
```

**相对前一个控件定位（Relative）**：

```pml
setup form !!hello
Title 'Hello'
paragraph .Message text 'Hello world'
text .input 'Enter text' at x0 ymax width 10 is string
button .bye 'Goodbye' at x0 ymax OK
exit
```

**相对指定控件定位**（用控件名限定坐标）：

```pml
button .bye 'Goodbye' At xmax.input ymax.input OK
```

## 控件定义（Gadget）

| 控件 | 写法 | 说明 |
| --- | --- | --- |
| 文本框 Paragraph | `paragraph .cename at x0 ymax text 'No name' width 20 height 1` | **at 必须在 text 前面** |
| 编辑框 Text | `text .input 'Enter text' at x0 ymax width 10 is string` | `is string` 声明输入类型 |
| 多选框 Toggle | `toggle .bran 'Branch'` | 状态通过 `.val` 成员判断 |
| 框架 Frame | `Frame .frame1 'Include' at x0 ymax+0.2` … `Exit` | Exit 结束框架内控件 |
| 按钮 Button | `button .ok 'OK' at x0 ymax OK` | 见下方三种内置属性 |
| 图标按钮 | `!icon = !!pml.getpathname('modmodeon16.png')`<br>`button .pick pixmap /$!icon at x0 ymax` | png 图像文件必须在 Pmllib 搜索路径内 |
| 列表框 List | `list .list 'Drawlist' at x0 ymax width 18 height 5` | 用数组赋值 |

框架示例：

```pml
Frame .frame1 'Include' at x0 ymax+0.2
toggle .bran  'Branch'
toggle .equi 'Equipment'
Exit
```

按钮三种内置属性：

```pml
button .ok 'OK' at x0 ymax OK               -- 关闭对话框，保留控件值
button .apply 'Apply' at x0 ymax Apply      -- 不关闭对话框
button .cancel 'Cancel' at x0 ymax Cancel   -- 关闭对话框，不保留控件值
```

按钮还可加 `tooltip`：

```pml
button .ce 'CE' at x0 ymax tooltip 'Select pipe'
```

## 设置控件初始值

在缺省构造方法里设置：

```pml
define method .addvolumn()
!this.bran.val = true          -- 多选框选中
!this.input.val = '1500'       -- 编辑框初始值
endmethod
```

## 完整实例（课程练习 Addvolumn）

新建方法，将当前元素的名字显示在文本框中：

```pml
define method .ce()
!this.cename.val = fullname
endmethod
```

在缺省构造方法中指定控件的响应操作：

```pml
define method .addvolumn()
…
!this.ce.callback = '!this.ce()'
endmethod
```

Apply 方法：调用控件的值，收集空间内的元素并 add：

```pml
define method .apply()
!name = !this.cename.val
!dist = !this.input.val
!elements = array()
if(!this.bran.val) then
var !elements append coll all bran Within Volume $!name $!dist
endif
if(!this.equi.val) then
var !elements append coll all equi Within Volume $!name $!dist
endif
var !names eval name for all from !elements
do !element value !elements
add $!element
enddo
endmethod
```

```pml
define method .addvolumn()
…
!this.apply.callback = '!this.apply()'       -- 不关闭对话框
endmethod
```

完整可运行版见 `assets/examples/addvolumn.pmlfrm`。

## 列表框应用

```pml
!this.list.dtext = !names          -- 列表框用数组赋值，dtext 表示 display text
!name = !this.list.selection()     -- 从列表框获取值

define method .doselection()
!name = !this.list.selection()
$!name
endmethod

define method .addvolumn()
…
!this.list.callback = '!this.doselection()'
endmethod
```

## 控件的内置方法（Method）和成员（Member）

```pml
!this.apply.Active = FALSE         -- 变灰一个控件
!this.input.SetFocus()             -- 聚焦控件
!this.input.seteditable(false)     -- 编辑框只读
!this.list.clear()                 -- 清除列表框的值
!this.apply.tag = ' Add '          -- 改变按钮上的文字
!this.apply.backgroud = 2          -- 改变按钮的背景颜色（课程原文拼写）
```

## View 控件（四种）

| 控件 | 用途 |
| --- | --- |
| Alpha view | 自定义输入输出显示窗口 |
| Plot view | 显示 Plot 图片 |
| Volume view | Design 显示窗口 |
| Area view | Draft 显示窗口 |

**Alpha Views**（自定义输入输出对话框）：

```pml
Setup Form !!alphaview
title ' Input & Output'
view .Input ALPHA hei 10 width 40
channel REQUESTS
channel COMMANDS
Exit
button .apply 'Apply' at x0 ymax Apply
button .Dismiss 'Cancel' at Xmax form-size Cancel
Exit
```

**Plot View**（显示 Plot 图片）：

```pml
setup form !!plotview
View .view1 plot width 41 hei 23
exit
define method .plotview()
!this.view1.borders = false
!this.view1.add('c:/ladder.plt')
endmethod
```

**Volume View**（Design 显示窗口 + 弹出菜单）：

```pml
setup form !!poptest
view .vol volume width 50 hei 9
exit
path down
button .press 'swap popup' call '!this.popswap()'
menu .popmenu
add 'hello' ' '
add 'world' ' '
exit
exit
define method .popswap()
!this.vol.popup = !this.popmenu
endmethod
```
