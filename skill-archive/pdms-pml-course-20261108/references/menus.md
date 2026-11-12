# 菜单定制

> 模块 9/9 · 来源：课程 P14–P15

## 菜单条（Bar）

以 Design 菜单为例，菜单条定义在 `..\AVEVA\Pdms11.5\pdmsui\DES\GEN\FSYSTEM`：

```pml
bar
add |Design|    _sysDes
add |Display|   _sysView
add |Query|     _sysQry
add |Settings|  _sysSet
add |Utilities| _sysUtil
```

调用自定义菜单条（`$M /%PDMSUI%/DES/GEN/USER BAR`），用 handle 包住避免重复添加报错：

```pml
$M /%PDMSUI%/DES/GEN/USER BAR
handle ANY
endhandle
add |Create|    _sysCrt
add |Modify|    _sysMod
```

## 菜单项（Menu）

```pml
menu _sysView
add |Graphical View|      '!!gphViews.addDesign()'
add |View Control...|     'show !!gphControl'
add |Plot View...|        form !!plotView
add sep
add |Browser...|          |EXPLORER SHOW|
add |Members...|          |CALLIB UFORMS MEMB _CDCMEMBER FREE|
add |Advanced Members...| |CALLIB UFORMS MEMB _CDCADVMEMBER FREE|
add |Drawlist...|         |CALLIB UFORMS MEMB _CDADDREM FREE|
add sep
add |Command Line...|     |show _cadcBth free|
```

- 菜单项动作可以是：函数调用（`'!!fn()'`）、显示对话框（`'show !!form'`）、`form` 命令、或 `|PDMS 命令|` 形式。
- `add sep` 添加分隔线。

## 自定义 USER 菜单

生成自定义菜单文件（文件名为 `USER`，无扩展名）：

| 模块 | 路径 |
| --- | --- |
| Design | `..\AVEVA\Pdms11.5\pdmsui\DES\GEN\USER` |
| Draft | `..\AVEVA\Pdms11.5\pdmsui\DRA\GEN\USER` |
| Catview | `..\AVEVA\Catview11.3\CAT\GEN\USER` |

**Design 示例**（`golabel /$1` 模式：传 `BAR` 加菜单条、传 `MENU` 加菜单项）：

```pml
golabel /$1
Return
label /BAR 
add |User|    _UserTools          -- 定义菜单条标题
return
label /MENU  
menu _UserTools
add |Sort Pipe...|     |show !!pipeorder|    -- 定义菜单项
exit
Return
```

调用：

```pml
$M /%PDMSUI%/DES/GEN/USER BAR
```

**Draft 示例**：

```pml
golabel /$1
Return
label /BAR 
add |User|    _UserTools
return
label /MENU  
menu _UserTools
add |Add flow Arrow|    |$m /%pmllib%\toolkit\flowARROW.mac|
exit
Return
```

**Catview 示例**：

```pml
golabel /$1
Return
label /BAR 
add |User|    _UserTools
return
label /MENU  
menu _UserTools
add |Copy Category...|        |show !!copycate|
exit
Return
```
