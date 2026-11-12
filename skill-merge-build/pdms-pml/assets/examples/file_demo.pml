-- file_demo.pml —— FILE 对象读写（课程 P13）
-- 用法：$M /file_demo
-- ReadFile 自动打开/关闭文件，内容读到字符串数组；
-- WriteFile 三种模式：'WRITE' / 'OVERWRITE' / 'APPEND'

!Input = object FILE('%pdmsexe%abc.txt')
handle ANY
  !Lines = !Input.ReadFile()
endhandle
!ResultArray = ARRAY()
Do !Line VALUES !Lines
  !Column1 = !Line.Part(1)
  !ResultArray.Append(!Column1)
Enddo
!Output = object FILE('%pdmsexe%def.txt')
handle ANY
  !Output.WriteFile('WRITE', !ResultArray)
endhandle
$P File written: $!ResultArray.Size() lines.
