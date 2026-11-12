# 文件处理

> 模块 8/9 · 来源：课程 P13

## FILE 对象读写

读写文件需要使用 **FILE 对象**：

```pml
!Input = object FILE('%pdmsexe%abc.txt')
!Lines = !Input.ReadFile()          -- ReadFile 将文件内容写到字符串数组中
!ResultArray = ARRAY()              -- 声明新数组
do !Line VALUES !Lines
!Column1 = !Line.Part(1)
!ResultArray.Append(!Column1)
Enddo
!Output = object FILE('%pdmsexe%def.txt')
!Output.WriteFile('WRITE', !ResultArray)   -- WriteFile 将数组写到文件
```

- `ReadFile()` 方法可以**自动打开（Open）和关闭（Close）文件**。
- 写文件有三种模式：`'WRITE'`（写入）、`'OVERWRITE'`（覆盖）、`'APPEND'`（追加）。

## 路径环境变量

`%pdmsexe%`、`%pdmsuser%`、`%pmllib%` 等 PDMS 环境变量可直接用在 FILE 对象路径和 `$M` 路径中。

## 选择文件的标准函数 !!fileBrowser

函数定义：

```pml
define function 
!!fileBrowser(!directory is STRING, 
!seedFile is STRING, 
!title is STRING, 
!existFlag is BOOLEAN, 
!callback is STRING)
```

使用实例：

```pml
!!filebrowser('%pdmsuser%','*',
'Text File',true,
'!filename = !!filebrowser.file.name() 
q var !filename')
```

在对话框里嵌入文件选择（实例应用）：

```pml
setup form !!findfile
title 'Find file'
text .filename 'File name' at x1 ymax+0.3 width 35 is string
button .browser 'Brower' 
button .ok '  OK  ' at x1 ymax+0.3 OK
button .cancel 'Cancel' at x10 cancel
exit
define method .findfile()
!this.browser.callback =
|!!filebrowser('%pdmsuser%','*','', true,'!!findfile.filename.val
= !!filebrowser.file.name()')|
endmethod
```

> 实战：AI 驱动 PDMS 时（`pdms-bridge`），进程内变量回读不可靠，统一用命令式文件通道（`openfile write` → `writefile` 缓冲 → `closefile` 落盘）回读结果，赋值务必放在 `handle ANY` 块内，见 `pdms-pml` → 坑9。
