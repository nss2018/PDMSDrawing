-- array_demo.pml —— 数组与 Collections 演示（课程 P8-P9）
-- 用法：$M /array_demo

-- 1) Split 建数组 + Append + values 遍历
!Str = 'Benz,Bmw,Audi'
!BestCar = !Str.Split(',')
!BestCar.Append('Lincoln')        -- 数组必须存在；不存在先 !a = array()
Do !Name Values !BestCar
  $P Array element is $!Name
Enddo

-- 2) 收集 CE 下所有 Branch 成员（不含 Tube），用计数器遍历
--    （空数组调 .Size() 会报 no member SIZE，实战坑2）
Var !PipeComps Coll All Branch Members For CE
!n = 0
Do !i Indices !PipeComps
  !n = !n + 1
Enddo
$P Collected $!n elements under CE.

-- 3) 限制条件收集：臂展大于 100 的 Elbow
Var !Elbows Coll All Elbow With (Abor gt 100) For CE
!n = 0
Do !i Indices !Elbows
  !n = !n + 1
Enddo
$P Found $!n big elbows.

-- 4) Eval 提取名字 + 排序
Var !Pipes Coll All Pipe For CE
Var !Names Eval Name For All From !Pipes
!Names.Sort().Invert()
Do !i Indices !Names
  !car = !Names[!i]               -- 先取到简单变量再插值（|...| 内数组索引插值会崩，实战坑6）
  $P $!car
Enddo
