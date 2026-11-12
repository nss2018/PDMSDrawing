! ============================================================================
! AGENTBRIDGE.PML — 档位1 零编译持久交互桥（PML2 窗体定时轮询）
!
! 用途：在不重编译任何 C# 插件的前提下，让 pdms-bridge（server.js, TRANSPORT=file）
!       通过"文件队列"与运行中的 PDMS 通信，获得持久交互通道。
!
! 原理：PDMS 二次开发手册 §第十章 确认 PML2 窗体(Forms)具备 callback。
!       用窗体的"定时回调(Timed Callback)"每 ~200ms 扫 queue/ 目录：
!         收到 queue/<id>.req  → 把内容写成 .pml 并 $M 执行 →
!         读取 RESULT_FILE(C:\pdmsAgent\bridge\result.txt) →
!         写入 done/<id>.res(OK/ERR + 正文) → 删 req。
!       这与 TCP 通道共用同一 result.txt 约定，server.js 的 callFile 分支直接可用。
!
! 启用：在 PDMS 命令窗输入  $M C:/pdmsAgent/bridge/AGENTBRIDGE.PML
!       （或在登录宏里加一行调用 AgentBridgeForm.start()）
!
! ⚠️ 三处 API 请按本机 PML 版本核对（已标注 [VERIFY]）：
!   1) 定时回调方法名：本稿用 addTimedCallback(秒, 方法名, 重复?)；
!      若本机为 setTimedCallback / 或窗体 timedCallback 事件，请改这一处。
!   2) 目录枚举：本稿用 directory 对象 .files('*.req')；
!      若本机无 directory 类，改用 !!dir 捕获 或 q files。
!   3) 文件读写：本稿用 file 对象的 open/readLine/writeLine/close；
!      若本机 API 不同（如 fopen/fgetline），请对应替换。
! ============================================================================

! ---- 配置（与 server.js 默认一致，可用环境变量改） ----
!q evars PDMS_BRIDGE_DIR            ! 仅示意；PML 侧直接用字面值更稳
define string !BRIDGE_DIR = 'C:/pdmsAgent/bridge/'
define string !QUEUE_DIR  = !BRIDGE_DIR & 'queue/'
define string !DONE_DIR   = !BRIDGE_DIR & 'done/'
define string !RESULT_F   = !BRIDGE_DIR & 'result.txt'

! ---- 窗体 + 定时回调 ----
setup form !!AgentBridgeForm
  [this.formTitle = |AgentBridge (pdms-bridge)|

  define method .start()
    !this.run()
    !this.addTimedCallback(0.2, |!this.poll()|, true)   ! [VERIFY] 定时回调方法名
    q text |AgentBridge started: polling | & !QUEUE_DIR

  define method .poll()
    !dir = new directory(!QUEUE_DIR)                       ! [VERIFY] 目录枚举
    !reqs = !dir.files('*.req')
    do !i from 1 to (!reqs.length())
      !name = !reqs[!i]
      !reqPath = !QUEUE_DIR & !name
      !donePath = !DONE_DIR & replace(!name, '.req', '.res')

      !code = !this.readFile(!reqPath)
      if (!code.length() > 0)
        !this.deleteIfExists(!RESULT_F)
        !pmlPath = !QUEUE_DIR & replace(!name, '.req', '.pml')
        !this.writeFile(!pmlPath, !code)
        !err = ''
        try
          !M ' ' & !pmlPath        ! 等同 $M：在 PDMS 线程执行 PML（可含 !! CP 行）
        catch (!e)
          !err = !e
        endtry
        !out = ''
        if (!this.fileExists(!RESULT_F)) !out = !this.readFile(!RESULT_F)
        if (!err = '')
          !this.writeFile(!donePath, 'OK' & (char 10) & !out)
        else
          !this.writeFile(!donePath, 'ERR' & (char 10) & !err)
        !this.deleteIfExists(!pmlPath)
      endif
      !this.deleteIfExists(!reqPath)
    enddo

  define method .fileExists(!p)
    !ok = false
    try
      !f = new file
      !f.open(!p, 'r')            ! [VERIFY] 文件读 API
      !f.close()
      !ok = true
    catch (!x)
      !ok = false
    endtry
    return !ok

  define method .readFile(!p)
    !s = ''
    try
      !f = new file
      !f.open(!p, 'r')
      do
        !line = !f.readLine()
        !s = !s & !line & (char 10)
      while (!f.endOfFile() = false)
      !f.close()
    catch (!x)
      !s = ''
    endtry
    return !s

  define method .writeFile(!p, !content)
    try
      !f = new file
      !f.open(!p, 'w')
      !f.writeLine(!content)       ! [VERIFY] 文件写 API
      !f.close()
    catch (!x)
      q text |AgentBridge write failed: | & !x
    endtry

  define method .deleteIfExists(!p)
    if (!this.fileExists(!p))
      try
        !f = new file
        !f.delete(!p)             ! [VERIFY] 删除 API
      catch (!x)
      endtry
    endif

! ---- 自启动 ----
!this.start()
