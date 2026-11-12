# PML 语法速查（PDMS Macro Language）

> 面向"写得出、调得通"的实战语法，不是语言规范全集。结合 `assets/examples/` 里的真实宏看更直观。

## 变量与类型
```
!x = 10            -- 整数
!y = 3.14          -- 实数
!s = 'hello'       -- 字符串（单引号）
!b = true          -- 布尔
!n1 = !n2 + 1      -- 运算；注意大小写不敏感（见 gotchas 坑1）
```
- 变量名大小写不敏感：`!X` 与 `!x` 同一变量。
- 字符串插值：`|固定文本 $!var 更多|`（数组索引别在 `|...|` 里插值，见坑6）。

## 当前元素 CE 与属性读
```
CE /*                 -- 回到 world 顶层
CE /SITE/ZONE/PIPE/P1 -- 设当前元素（引用可带 = 前缀）
!!ce.type             -- 读类型（缩写，见坑5）
!!ce.name
!!ce.vsca             -- 读具体属性
var !st isodraw status  -- 读系统/ISO 状态变量
```

## 集合与遍历
```
var !W coll all for CE          -- 收集 CE 下所有成员（无 .type/.name，见坑3/4）
do !i indices !W                -- 遍历索引
  !ref = !W[!i].string()        -- 返回值带 = 前缀
  CE $!ref                       -- 再 CE 才能读类型/名字
  !t = !!ce.type
enddo

do !e element /PROJ/SYS/1        -- 模块遍历（首跑待验证语法）
  !t = !e.type
enddo

do !k from 1 to 3                -- 计数循环（无 break，到 limit 停写）
  ...
enddo
```

## 条件与控制
```
if (!t eq 'PIPE') then
  ...
else
  ...
endif
!b = !b.not()                   -- 布尔取反（坑：用 .not() 而非 not !b）
handle ANY
  ...
endhandle                       -- 就近异常捕获（坑8）
```

## 新建元素（NEW…END 续行，无等号）
```
NEW REGI /R1
NEW DRWG /R1/D1
NEW SHEE /R1/D1/S1
NEW VIEW /R1/D1/S1/V1
  VSCA  0.02          -- 属性续行，无 = 号
  VTYP  Universal
  SIZE  1189 841
  XYPS  X500 Y400
  DIR   D
END
END
NEW LIBY /R1/LIBY
NEW DLLB /R1/DRAWLIST
NEW IDLI /R1/DRAWLIST/DRWG
END
END
```

## 文件 I/O（结果回读通道）
```
openfile /D:/path/result.txt write !F1
handle ANY
  openfile /D:/path/result.txt overwrite !F1
endhandle
writefile $!F1 |一行内容|
closefile $!F1
```
- `write` 首次开；若已存在用 `overwrite` 分支（handle ANY 兜底）。
- `writefile` 缓冲，到 `closefile` 才落盘。

## 宏加载与命令下发
```
$M "%PMLLIB%/Common_Tools/iso_diag.pmlmac"   -- 加载并执行宏文件（推荐）
DO <路径>                                     -- 另一种加载（TTY 下会被 ^^ 污染，见坑7）
ISODRAFTMODE                                   -- 进入 ISO 出图模式
options default                                -- 载入默认选项
$M "<选项文件>"                                -- 载入自定义选项
DETAIL PIPE /2001                              -- 对管线出 ISO
isodraw status                                 -- 读状态
EXIT                                           -- 退出 ISO 模式
QUIT                                           -- 退出会话（pdms_exec 黑名单，勿经桥下发）
```

## 类型缩写对照
`BRAN`(BRANCH) · `PIPE` · `SITE` · `ZONE` · `EQUI` · `NOZZ` · `WORL`(WORLD) · `PTWL` · `TUBI`
