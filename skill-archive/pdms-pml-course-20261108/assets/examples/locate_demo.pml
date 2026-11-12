-- locate_demo.pml —— 元素定位与导航：Q REF / Q NAME / CE 三种姿势
-- 用法：$M /locate_demo
-- 核心原则：名字会变，ref（=234/702）永不变 → 定位优先用 ref

-- 1) 先看当前元素是什么（Q REF / Q NAME 输出 CE 的标识）
Q REF
Q NAME

-- 2) 按 name 定位：/ 开头全路径直接敲
CE /SITE-1/ZONE-1/PIPE-1
Q NAME

-- 3) 按 ref 定位：= 形式。命令行输出的 ref 可直接回输
CE =234/702
Q REF

-- 4) 实战模式：coll 收集 → ref 回定位
--    （坑3：coll 成员没有 .type/.name，必须 CE 回去再读）
Var !W Coll All For CE
Do !i Indices !W
  !ref = !W[!i].string()      -- 形如 =234/702
  CE $!ref                    -- = 形式可直接 CE
  !t = !!ce.type
  $P $!ref is $!t
Enddo

-- 5) 列表导航：NEXT / PRE / SAME，到头会报 (2,113)，用 handle 兜底
Next
Handle (2,113)
  $P Last element.
EndHandle
