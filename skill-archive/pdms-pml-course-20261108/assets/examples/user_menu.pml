-- user_menu.pml —— 自定义 USER 菜单文件（课程 P14）
-- 保存为无扩展名的 USER 文件，放到对应模块目录：
--   Design : ..\AVEVA\Pdms11.5\pdmsui\DES\GEN\USER
--   Draft  : ..\AVEVA\Pdms11.5\pdmsui\DRA\GEN\USER
--   Catview: ..\AVEVA\Catview11.3\CAT\GEN\USER
-- 调用：$M /%PDMSUI%/DES/GEN/USER BAR   （加菜单条）
--       $M /%PDMSUI%/DES/GEN/USER MENU  （加菜单项）

golabel /$1
Return

label /BAR
add |User|    _UserTools          -- 定义菜单条标题
return

label /MENU
menu _UserTools
add |Sort Pipe...|     |show !!pipeorder|    -- 定义菜单项
add sep
add |Add flow Arrow|   |$m /%pmllib%\toolkit\flowARROW.mac|
exit
Return
