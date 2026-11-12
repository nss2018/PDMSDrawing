-- control_demo.pml —— If/Do/Break/Skip/Handle 综合演示（课程 P6-P8）
-- 用法：$M /control_demo（在 BRAN 或其成员上运行效果最佳）

-- 1) If / Elseif / Else（判断表达式必须是布尔值）
!Type = Type
!OwnType = Type of Owner
IF (!Type eq 'BRAN') THEN
  $P CE is Branch.
ELSEIF (!OwnType eq 'BRAN') THEN
  $P CE is Branch member.
ELSE
  $P CE is $!Type, Pls select Branch.
ENDIF

-- 2) 判断变量是否设置（函数写法 / 方法写法等价）
!x = real()
if (!x.Unset()) then
  $P x is unset.
endif
if (Unset(!x)) then
  $P x is still unset.
endif

-- 3) Do 计数循环 + Break If
!Total = 0
Do !i From 1 To 100 By 1
  !Total = !Total + !i
  Break If (!Total gt 500)
Enddo
$P Total = $!Total

-- 4) Skip If 跳过奇数，只累加偶数
!Total = 0
Do !i From 1 To 100
  Skip If (Int(!i / 2) NE (!i / 2))
  !Total = !Total + !i
Enddo
$P Sum of evens = $!Total

-- 5) Handle 特定错误码：Next 到列表末尾时报 (2,113) List exhausted
Next
Handle (2,113)
  $P Last element.
EndHandle
$P OK

-- 6) Handle Any 兜底任何错误
Next
Handle Any
EndHandle
$P OK
