@echo off
rem ============================================================================
rem run_tty_task.bat — 档位3 零编译 TTY 批处理（一次性/夜间批量用，如 ISO 百根管线）
rem 用法：run_tty_task.bat <PROJECT> <USER>/<PASS> <MDB> <macro.pml>
rem   例：run_tty_task.bat SAM SYSTEM/XXXXXX STRUC C:\pdmsAgent\bridge\queue\iso_batch.pml
rem 依据：Installation Guide §4.6.1(TTY) + §4.7(PDMSConsole/PDMSWait/PDMS_CONSOLE_IDENTIFIER)
rem 注意：每次都启动一次 PDMS（慢），且是新会话（不保留上次的 CE/状态），仅适合批处理。
rem ============================================================================
setlocal
if "%~4"=="" (
  echo 用法: %~nx0 ^<PROJECT^> ^<USER^>/^<PASS^> ^<MDB^> ^<macro.pml^>
  exit /b 1
)

set PDMS_CONSOLE_IDENTIFIER=%RANDOM%
set PDMS_INSTALL=C:\AVEVA\Plant\PDMS12.1.SP4

rem TTY = 不启动 GUI；宏里把结果写到 C:\pdmsAgent\bridge\result.txt
call "%PDMS_INSTALL%\pdms.bat" TTY %1 %2 /%3 $M/%4

rem 等 PDMS 真正结束（多会话靠上面的 IDENTIFIER 区分）
call "%PDMS_INSTALL%\pdmswait"

echo TTY task done.
endlocal
