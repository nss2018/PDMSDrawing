-- hello_param.pml —— 参数化宏：建 EQUIP + BOX + CYL + 连接（课程 P2）
-- 用法：$M /hello_param NEWEQUIP 300 400 600
--   $1=元素名  $2=XLEN  $3=YLEN(兼作 DIA)  $4=ZLEN(兼作 HEI)
-- 课程原文省略了 END（NEW 后紧跟另一个 NEW 会自动结束上一个元素定义）；
-- 真实项目建议显式 NEW...END 配对，如下所示。
NEW EQUIP /$1
NEW BOX
  XLEN $2 YLEN $3 ZLEN $4
END
NEW CYL DIA $3 HEI $4
END
CONN P1 TO P2 OF PREV
