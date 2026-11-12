-- vars_demo.pml —— 变量提取/赋值 Pipe 属性（课程 P3 练习）
-- 用法：$M /vars_demo
-- 1) 新建 Pipe，设置四个不同类型的属性
NEW PIPE
  Desc 'My Description'      -- 字符串属性：单引号
  Temp 100                   -- 实数属性
  Pspec /A3B                 -- 参考属性：元素必须存在
  Purp PIPI                  -- 关键字属性：取前 4 字符，不加括号
END

-- 2) 从 Pipe 提取属性到变量
!desc = Desc
!temp = Temp
!pspec = Pspec
!purp = Purp
$P Desc=$!desc Temp=$!temp Pspec=$!pspec Purp=$!purp

-- 3) 用变量给新 Pipe 赋同样的属性（$ 把变量值转成字符串）
NEW PIPE
  Desc '$!desc'              -- 字符串必须单引号
  Temp $!temp                -- 实数直接插值
  Pspec $!pspec              -- 参考元素必须存在
  Purp $!purp                -- 关键字只取前 4 字符
END
$P Second pipe created with same attributes.
