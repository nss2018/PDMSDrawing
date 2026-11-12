// PdmsBridge.cs — PDMS 侧连接器监听器（drawingPDMS 插件内）
//
// 作用：在 PDMS 进程内开一个本地 TCP 监听器(127.0.0.1:18753)，接收 MCP 连接器
//       (pdms-bridge-mcp) 发来的 PML/命令，在 PDMS 线程上执行，并把结果回传。
//       这是"智能体直接操作 PDMS"的落地端。
//
// 集成方式（门控，不影响现有构建）：
//   1) 把本文件拷到 drawingPDMS 工程目录，csproj 里加 <Compile Include="PdmsBridge.cs" />。
//   2) 在 drawAddin.cs 的 IAddin.Start() 开头（或结尾）加：
//         #if PDMS_BRIDGE
//             drawingPDMS.PdmsBridge.Start();
//         #endif
//   3) 工程里定义编译符号 PDMS_BRIDGE（csproj 加
//         <DefineConstants>$(DefineConstants);PDMS_BRIDGE</DefineConstants>
//      或命令行 -p:DefineConstants=PDMS_BRIDGE）。
//   4) 重建并部署 DLL，重启 PDMS，加载插件后监听器自动启动。
//
// 注意：
//   - 必须 .NET Framework 3.5 Client Profile；勿用 async/await、System.Text.Json、
//     HttpClient 等 3.5 不支持的 API（本文件全部规避）。
//   - Command.RunInPdms 是否同步执行取决于 PDMS 版本；本文件通过"轮询结果文件"
//     兼容异步情形，故 PML 里请把结果写到 PDMS_BRIDGE_RESULT 指定的文件。
//   - AGENTBRIDGE.PML（文件轮询兜底）不可行：PML 无 sleep/wait，无法实现
//     目录轮询（2026-10-14 评估，见 D:\pdmsAgent\bridge\ROADMAP.md）。

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Diagnostics;
using Aveva.Pdms.Utilities.CommandLine;

namespace drawingPDMS
{
    public static class PdmsBridge
    {
        // 2026-10-28 重构：多模块并存支持。Design/Paragon/Draft 各起独立进程都加载本 addin。
        // 端口按模块确定性固定(DESIGN=18753/PARAGON=18754/DRAFT=18755/...)，由自身进程名判定；
        // 仅当同模块重复启动时才 +1 顺延。每个实例写发现清单 instances/<module>_<pid>.json，
        // 连接器据此按模块选端口，解决"多模块同时打开时识别不到正确端口"。
        private const int MaxPortTry = 20;
        // internal：DialogWatcher 等同程序集类共用 bridge 目录（2026-10-19 CS0103 修复）
        internal static string BridgeDir =
            Environment.GetEnvironmentVariable("PDMS_BRIDGE_DIR") ?? @"D:\pdmsAgent\bridge";
        private static string ResultFile =
            Environment.GetEnvironmentVariable("PDMS_BRIDGE_RESULT") ?? Path.Combine(BridgeDir, "result.txt");
        internal static int BoundPort = 18753;   // 实际绑定端口（Start 内按模块确定性端口赋值）
        private static TcpListener _listener;
        private static volatile bool _running;
        // 多模块支持（2026-10-28）：端口按模块确定性固定，并写发现清单供连接器按模块选端口。
        private static string ModuleName = "UNKNOWN";   // DESIGN/PARAGON/DRAFT/MONITOR/ISO，由自身进程名判定
        private static int ModulePort = 18753;          // 该模块确定性默认端口
        private static string ManifestPath = null;      // 本实例发现清单路径 (<BridgeDir>/instances/<module>_<pid>.json)
        // kind:"op" 的 UI 线程通道（2026-10-14 用户拍板路线 A：桥接直调 C# 出图链）：
        //   1) 插件控件注册的 Control（RegisterUi，drawE3D 构造时调）——Invoke 与窗口同线程，最稳；
        //   2) Start()/RegisterUi 时捕获的 SynchronizationContext（WinForms UI 线程）——Post+Wait；
        //   3) 都不可用 —— inline 执行（bridge.log 有告警；首跑 probe_read 只读 op 负责验证跨线程 DB API 安全性）。
        private static System.Windows.Forms.Control _uiControl;
        private static SynchronizationContext _uiContext;

        /// <summary>插件 UI 控件构造时注册（在 PDMS UI 线程上），供 kind:"op" 操作派发到 UI 线程。</summary>
        public static void RegisterUi(System.Windows.Forms.Control c)
        {
            _uiControl = c;
            if (_uiContext == null) _uiContext = SynchronizationContext.Current;
            Log("RegisterUi: control=" + (c != null ? c.GetType().Name : "null")
                + " handleCreated=" + (c != null && c.IsHandleCreated)
                + " syncContext=" + (_uiContext != null ? _uiContext.GetType().Name : "null"));
        }

        public static void Start()
        {
            if (_running) return;
            _running = true;
            DetectModule();   // 先判定模块与确定性端口
            // 进程退出时兜底清理本实例发现清单（PDMS 正常关闭即便 Stop 未触发也删）
            try { AppDomain.CurrentDomain.ProcessExit += (s, e) => RemoveManifest(); } catch { }
            string lastErr = "";
            // 端口基值：环境变量 PDMS_BRIDGE_PORT 优先，否则用模块确定性端口。
            int basePort = ParseInt(Environment.GetEnvironmentVariable("PDMS_BRIDGE_PORT"), ModulePort);
            string envResult = Environment.GetEnvironmentVariable("PDMS_BRIDGE_RESULT");
            // BridgeDir 已是 baseDir（env PDMS_BRIDGE_DIR 或 D:\pdmsAgent\bridge）。
            // 多模块共用同一目录：结果文件/清单/日志都落在 baseDir；TCP 端口本身已区分实例，
            // 不再用 _pN 子目录（否则非 Design 模块的 result.txt 与连接器 RESULT_FILE 错位→超时）。
            bool bound = false;
            for (int attempt = 0; attempt <= MaxPortTry && !bound; attempt++)
            {
                int p = basePort + attempt;
                try
                {
                    if (!Directory.Exists(BridgeDir)) Directory.CreateDirectory(BridgeDir);
                    _listener = new TcpListener(IPAddress.Loopback, p);
                    _listener.Start();
                    bound = true;
                    BoundPort = p;
                    ResultFile = string.IsNullOrEmpty(envResult) ? Path.Combine(BridgeDir, "result.txt") : envResult;
                }
                catch (Exception exB) { lastErr = exB.Message; try { _listener = null; } catch { } }
            }
            if (!bound)
            {
                _running = false;
                try { File.AppendAllText(Path.Combine(BridgeDir, "bridge.log"),
                    DateTime.Now + " start failed (module " + ModuleName + " ports " + basePort + ".." + (basePort + MaxPortTry) + "): " + lastErr + "\n"); } catch { }
                return;
            }
            try
            {
                // 捕获 UI 线程 SynchronizationContext（Start 由 addin 在 UI 线程调；
                // drawE3D 构造时 RegisterUi 会再补一次，取先到者）
                if (_uiContext == null) _uiContext = SynchronizationContext.Current;
                Thread t = new Thread(AcceptLoop) { IsBackground = true, Name = "PdmsBridge" };
                t.Start();
                // 日志：写到 bridge 目录，方便排查
                File.AppendAllText(Path.Combine(BridgeDir, "bridge.log"),
                    DateTime.Now + " PdmsBridge started on " + IPAddress.Loopback + ":" + BoundPort
                    + " (module " + ModuleName + ", base " + basePort + ") dir=" + BridgeDir
                    + " syncContext=" + (_uiContext != null ? _uiContext.GetType().Name : "null") + "\n");
                WriteManifest();                  // 多模块发现清单（连接器据此按模块选端口）
                // 模态弹窗自动关闭看门狗（防 headless 流水线被错误框堵死）
                DialogWatcher.EnsureStarted();
            }
            catch (Exception ex)
            {
                _running = false;
                try { File.AppendAllText(Path.Combine(BridgeDir, "bridge.log"), DateTime.Now + " start failed: " + ex.Message + "\n"); } catch { }
            }
        }

        public static void Stop()
        {
            _running = false;
            RemoveManifest();
            try { _listener.Stop(); } catch { }
        }

        /* ---------------- 模块判定 + 多模块发现清单（2026-10-28） ----------------
         * 同一个 drawingPDMS.dll 在 Design/Paragon/Draft 三个 Addins.xml 里都注册，
         * 三个模块各起一个独立进程都会执行 Start()。通过自身进程名(des/par/dra/mon/iso)
         * 判定模块，分配确定性端口，并写 <BridgeDir>/instances/<module>_<pid>.json，
         * 连接器据此按模块选端口——根治"Design/Paragon/Draw 同时打开时识别不到正确端口"。
         */
        private static void DetectModule()
        {
            ModuleName = "UNKNOWN";
            ModulePort = 18753;
            string envMod = Environment.GetEnvironmentVariable("PDMS_BRIDGE_MODULE");
            if (!string.IsNullOrEmpty(envMod))
            {
                envMod = envMod.Trim().ToUpperInvariant();
                if (envMod == "DRAW") envMod = "DRAFT";   // DRAW 即 Draft 出图模块
                ModuleName = envMod;
            }
            else
            {
                string exe = "";
                try { exe = (System.Diagnostics.Process.GetCurrentProcess().MainModule != null)
                    ? System.Diagnostics.Process.GetCurrentProcess().MainModule.ModuleName.ToLowerInvariant() : ""; }
                catch { }
                if (exe.Contains("des")) ModuleName = "DESIGN";
                else if (exe.Contains("par")) ModuleName = "PARAGON";
                else if (exe.Contains("dra")) ModuleName = "DRAFT";
                else if (exe.Contains("mon")) ModuleName = "MONITOR";
                else if (exe.Contains("iso")) ModuleName = "ISO";
                else if (exe.Contains("pdms")) ModuleName = "PDMS";
                else ModuleName = "UNKNOWN";
            }
            switch (ModuleName)
            {
                case "DESIGN":  ModulePort = 18753; break;
                case "PARAGON": ModulePort = 18754; break;
                case "DRAFT":   ModulePort = 18755; break;
                case "MONITOR": ModulePort = 18756; break;
                case "ISO":     ModulePort = 18757; break;
                default:        ModulePort = 18753; break;
            }
        }

        private static void WriteManifest()
        {
            try
            {
                string instDir = Path.Combine(BridgeDir, "instances");
                if (!Directory.Exists(instDir)) Directory.CreateDirectory(instDir);
                int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                ManifestPath = Path.Combine(instDir, ModuleName.ToLower() + "_" + pid + ".json");
                string exe = "";
                try { exe = System.Diagnostics.Process.GetCurrentProcess().MainModule.ModuleName; } catch { }
                string json = "{\"module\":" + JsonString(ModuleName)
                    + ",\"port\":" + BoundPort
                    + ",\"pid\":" + pid
                    + ",\"exe\":" + JsonString(exe)
                    + ",\"dir\":" + JsonString(BridgeDir)
                    + ",\"startedAt\":" + JsonString(DateTime.Now.ToString("o")) + "}\n";
                File.WriteAllText(ManifestPath, json);
                File.AppendAllText(Path.Combine(BridgeDir, "bridge.log"),
                    DateTime.Now + " manifest " + ManifestPath + " (module=" + ModuleName + ")\n");
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(Path.Combine(BridgeDir, "bridge.log"), DateTime.Now + " manifest err: " + ex.Message + "\n"); } catch { }
            }
        }

        private static void RemoveManifest()
        {
            try { if (ManifestPath != null && File.Exists(ManifestPath)) File.Delete(ManifestPath); } catch { }
        }

        private static void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client = null;
                try { client = _listener.AcceptTcpClient(); }
                catch { if (!_running) break; continue; }
                ThreadPool.QueueUserWorkItem(HandleClient, client);
            }
        }

        private static void HandleClient(object state)
        {
            TcpClient client = (TcpClient)state;
            try
            {
                using (client)
                using (NetworkStream ns = client.GetStream())
                {
                    // 读一行 JSON 请求： {"id":N,"code":"...","kind":"exec"}
                    StringBuilder sb = new StringBuilder();
                    byte[] buf = new byte[4096];
                    int have;
                    string req = null;
                    while ((have = ns.Read(buf, 0, buf.Length)) > 0)
                    {
                        sb.Append(Encoding.UTF8.GetString(buf, 0, have));
                        int nl = sb.ToString().IndexOf('\n');
                        if (nl >= 0) { req = sb.ToString().Substring(0, nl).Trim(); break; }
                    }
                    if (string.IsNullOrEmpty(req)) return;

                    int id = 0; string code = "";
                    string kind = "";
                    try
                    {
                        // 极简 JSON 解析（id / code / kind / op / params）
                        id = ParseInt(JsonField(req, "id"), 0);
                        code = JsonField(req, "code");
                        kind = JsonField(req, "kind");
                    }
                    catch { }

                    // kind:"op" —— 命名 C# 操作（HeadlessDrawing：窗口出图链的无头移植），
                    // 在 PDMS UI 线程执行（RunOnUi），不走 $M PML 通道。
                    // 2026-10-15：整段加 try/catch 回包——旧版这里抛异常会静默断连（0 字节），
                    // 排查只能靠 bridge.log（ParseFlatParams 负长度 Substring 事故）。
                    if ("op".Equals(kind, StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            string opName = "";
                            string paramsJson = "";
                            try { opName = JsonField(req, "op"); paramsJson = JsonObject(req, "params"); } catch { }
                            Dictionary<string, string> prm = ParseFlatParams(paramsJson);
                            string opOut = "";
                            string opErr = null;
                            Action work = delegate
                            {
                                try
                                {
                                    if ("probe_read".Equals(opName)) { HeadlessDrawing.OpResult r = HeadlessDrawing.ProbeRead(); opOut = r.Output; if (!r.Ok) opErr = r.Error; }
                                    else if ("create_drawing".Equals(opName)) { HeadlessDrawing.OpResult r = HeadlessDrawing.CreateDrawing(prm); opOut = r.Output; if (!r.Ok) opErr = r.Error; }
                                    else if ("export_dxf".Equals(opName)) { HeadlessDrawing.OpResult r = HeadlessDrawing.ExportDxf(prm); opOut = r.Output; if (!r.Ok) opErr = r.Error; }
                                    else if ("export_pdf".Equals(opName)) { HeadlessDrawing.OpResult r = HeadlessDrawing.ExportPdf(prm); opOut = r.Output; if (!r.Ok) opErr = r.Error; }
                                     else if ("delete_element".Equals(opName)) { HeadlessDrawing.OpResult r = HeadlessDrawing.DeleteElement(prm); opOut = r.Output; if (!r.Ok) opErr = r.Error; }
                                     // 2026-10-16 新增：CE 读写（悬空 CE 修复）+ 视图刷新（C# 封装 UpdateModelView）
                                     else if ("set_ce".Equals(opName)) { HeadlessDrawing.OpResult r = HeadlessDrawing.SetCe(prm); opOut = r.Output; if (!r.Ok) opErr = r.Error; }
                                      // 2026-10-20 (21:5x)：读 CE 引用——Web 向导 Add(CE) 按钮（用户选中元素一键填引用）
                                      else if ("get_ce".Equals(opName)) { HeadlessDrawing.OpResult r = HeadlessDrawing.GetCe(prm); opOut = r.Output; if (!r.Ok) opErr = r.Error; }
                                      // 2026-10-20 (22:2x)：配管材料表（整合 PipeGen MaterialReport，输出 .xls）
                                      else if ("material_report".Equals(opName)) { HeadlessDrawing.OpResult r = HeadlessDrawing.MaterialReportOp(prm); opOut = r.Output; if (!r.Ok) opErr = r.Error; }
                                      else if ("update_view".Equals(opName)) { HeadlessDrawing.OpResult r = HeadlessDrawing.UpdateView(prm); opOut = r.Output; if (!r.Ok) opErr = r.Error; }
                                      // 2026-10-16 新增：HG20519 标准符号图块库（SYTM）建库 + 已出视图符号原位替换
                                      else if ("create_sym_blocks".Equals(opName)) { HeadlessDrawing.OpResult r = HeadlessDrawing.CreateSymBlocks(prm); opOut = r.Output; if (!r.Ok) opErr = r.Error; }
                                      else if ("replace_symbols".Equals(opName)) { HeadlessDrawing.OpResult r = HeadlessDrawing.ReplaceSymbols(prm); opOut = r.Output; if (!r.Ok) opErr = r.Error; }
                                      // 2026-10-17 (8r)：规则集源头方案——枚举规则集库找现成单线模板 + 视图切换 RRSF
                                      else if ("list_rulesets".Equals(opName)) { HeadlessDrawing.OpResult r = HeadlessDrawing.ListRulesets(prm); opOut = r.Output; if (!r.Ok) opErr = r.Error; }
                                      else if ("set_rrsf".Equals(opName)) { HeadlessDrawing.OpResult r = HeadlessDrawing.SetRrsf(prm); opOut = r.Output; if (!r.Ok) opErr = r.Error; }
                                      else if ("set_styl".Equals(opName)) { HeadlessDrawing.OpResult r = HeadlessDrawing.SetStyl(prm); opOut = r.Output; if (!r.Ok) opErr = r.Error; }
                                      // 2026-10-17 (8s)：MAS 只读 → 视图本地规则(局部 RRUL)+自建空白样式，源头单线
                                      else if ("make_local_rule".Equals(opName)) { HeadlessDrawing.OpResult r = HeadlessDrawing.MakeLocalRule(prm); opOut = r.Output; if (!r.Ok) opErr = r.Error; }
                                      // 2026-10-17 (8t)：通用命令流——OMIT ALL BRANCH 由解析器处理编码
                                      else if ("exec_cmds".Equals(opName)) { HeadlessDrawing.OpResult r = HeadlessDrawing.ExecCmds(prm); opOut = r.Output; if (!r.Ok) opErr = r.Error; }
                                      // 2026-10-17 (8u)：DESSYM 2D 符号层诊断/清除
                                      else if ("sym_purge".Equals(opName)) { HeadlessDrawing.OpResult r = HeadlessDrawing.SymPurge(prm); opOut = r.Output; if (!r.Ok) opErr = r.Error; }
                                      // 2026-10-17 (8v)：反射全量属性 dump
                                      else if ("dump_attrs".Equals(opName)) { HeadlessDrawing.OpResult r = HeadlessDrawing.DumpAttrs(prm); opOut = r.Output; if (!r.Ok) opErr = r.Error; }
                                      else opErr = "unknown op: " + opName;
                                 }
                                 catch (Exception exOp)
                                 {
                                     opErr = "[" + exOp.GetType().Name + "] " + exOp.Message;
                                     // 2026-10-16：落 StackTrace（此前只回 Message，无法定位真实抛出点）
                                     Log("op " + opName + " 异常: " + exOp.GetType().FullName + " " + exOp.Message + "\n" + exOp.StackTrace);
                                 }
                            };
                            RunOnUi(work);
                            string respOp = "{\"id\":" + id + ",\"ok\":" + (opErr == null ? "true" : "false")
                                          + ",\"output\":" + JsonString(opOut) + ",\"error\":" + JsonString(opErr ?? "") + "}\n";
                            byte[] outbOp = Encoding.UTF8.GetBytes(respOp);
                            ns.Write(outbOp, 0, outbOp.Length);
                            Log("op " + opName + " ok=" + (opErr == null) + " out=" + opOut.Length + " err=" + (opErr ?? ""));
                        }
                        catch (Exception exBranch)
                        {
                            Log("op branch err: " + exBranch.GetType().Name + " " + exBranch.Message);
                            try
                            {
                                string respErr = "{\"id\":" + id + ",\"ok\":false,\"output\":\"\","
                                              + "\"error\":" + JsonString("op branch: " + exBranch.Message) + "}\n";
                                byte[] outbErr = Encoding.UTF8.GetBytes(respErr);
                                ns.Write(outbErr, 0, outbErr.Length);
                            }
                            catch { }
                        }
                        return;
                    }

                    string output = "";
                    string error = null;
                    bool ok = true;
                    try
                    {
                        // 写到临时 .pml，再用 DO 在 PDMS 线程执行
                        if (!Directory.Exists(BridgeDir)) Directory.CreateDirectory(BridgeDir);
                        if (File.Exists(ResultFile)) { try { File.Delete(ResultFile); } catch { } }
                        // 2026-10-16 根治（致命堵死实证）：原实现每笔都写同名 cmd_<id>.pml（bridge_call 每笔新起
                        // server.js → id 恒为 1 → 永远是 cmd_1.pml）。PDMS 执行 $M 宏是「边执行边读文件」，
                        // 上一笔未跑完（如卡在错误弹窗）时同名覆盖 → 宏引擎重读到旧内容/读位错乱 → 重复执行旧宏
                        // （实证 13:28:07：cmd_1.pml 被新宏覆盖的同一秒，旧宏 diag18.txt 被截断重跑 →
                        //  NEW RRST 撞名 → (41,12) Name already used 弹窗 → 宏线程卡死 → 后续 $M 全堵 → fatal）。
                        // 改为每笔唯一文件名（id + ticks），PDMS 永不读到被覆盖过的文件。
                        string pml = Path.Combine(BridgeDir, "cmd_" + id + "_" + DateTime.Now.Ticks + ".pml");
                        // 关键修复: 用无 BOM 的 UTF-8 写 .pml。否则 System.Text.Encoding.UTF8
                        // 默认带 BOM(EF BB BF)，PML 在 BOM + 非 ASCII(中文注释) 下编码探测冲突、
                        // 宏解析中止 → RunInPdms failed / 空输出。无 BOM 时 ASCII 与中文均正常。
                        // 2026-10-16 加固（实证）：$M 宏执行期间 PDMS 持有 cmd_<id>.pml 句柄，上一笔宏未跑完时
                        // 直接 WriteAllText 抛 IOException「对象当前正在其他地方使用」→ 500ms×120 重试等锁释放。
                        bool staged = false;
                        Exception lastIo = null;
                        for (int attempt = 0; attempt < 120 && !staged; attempt++)
                        {
                            try { File.WriteAllText(pml, code ?? "", new System.Text.UTF8Encoding(false)); staged = true; }
                            catch (IOException ioEx) { lastIo = ioEx; Thread.Sleep(500); }
                        }
                        if (!staged)
                            throw new Exception("暂存 " + pml + " 失败（文件被占用）: " + (lastIo != null ? lastIo.Message : "unknown"));
                        // 用 $M（PML 宏执行）而非 DO（CP 宏执行）下发：
                        // 2026-10-14 TTY 全链路验证过 $M 可执行 CP+PML 混合宏
                        // （NEW/END 建链、openfile/writefile/closefile 落盘均通过），
                        // 见 D:\pdmsAgent\view_api\diary 证据链。
                        // RunInPdms 是实例方法（无静态重载）：CreateCommand(...).RunInPdms()。
                        // 失败时 command.Error.MessageText() 带回 PDMS 报错文本（drawE3D.cs 先例）。
                        // 2026-10-16 加固（用户纪律「先判断对象是不是空，不是空再继续执行」，NRE 实证）：
                        //   CreateCommand / RunInPdms / cmd.Error 逐级判空；异常落 type+message+StackTrace
                        //   到 bridge.log（此前只在客户端看到 ex.Message，无法定位 NRE 真实抛出点）。
                        //   注意：RunInPdms 抛异常 ≠ 宏没下发（2026-10-16 实证：13:19 抛 NRE，宏 13:26 仍执行成功）
                        //   → 异常后仍继续轮询结果文件，由调用方按结果文件判定，勿盲目重发（cmd_1.pml 会被锁）。
                        // 2026-10-16 根治（GDI+ 跨线程 fatal，14:06:20 bridge.log 实证）：exec 路径原在【线程池线程】
                        // 直接调 CreateCommand/RunInPdms。命令触发 DB Claim → Draft/Design Explorer 树更新
                        // （EndTreeUpdate → RestoreScrolledRegion → BringIntoView → UltraTreeNode.get_ImageSizeResolved
                        //  → System.Drawing.Image.get_Width）在【线程池线程】上访问 UI 线程创建的节点图标 GDI+ 对象
                        //  → InvalidOperationException「对象当前正在其他地方使用」→ 此前冒泡为
                        //  "A fatal error has been detected. The program must exit."。
                        //  只有会更新 Explorer 树的命令（CE 切到图框/视图、UPDATE all 等）才触发；只读/表示库命令不触发。
                        //  修复：用 RunOnUi 把 CreateCommand/RunInPdms 切到【UI 线程】执行（与 op 路径一致）→
                        //  树更新即在 UI 线程 → GDI+ OK。结果文件轮询仍留桥接线程（纯文件 I/O，不阻塞 UI）。
                        Command cmd = null;
                        Action dispatchCmd = delegate
                        {
                            try { cmd = Command.CreateCommand("$M " + pml.Replace('/', '\\')); }
                            catch (Exception exCc)
                            {
                                ok = false;
                                error = "CreateCommand 抛异常: " + exCc.GetType().Name + " " + exCc.Message;
                                Log("exec CreateCommand 异常: " + exCc.GetType().FullName + " " + exCc.Message + "\n" + exCc.StackTrace);
                            }
                        };
                        RunOnUi(dispatchCmd);
                        if (cmd == null)
                        {
                            if (ok) { ok = false; error = "CreateCommand 返回 null（命令未下发）"; }
                        }
                        else
                        {
                            bool ran = true;
                            Action runCmd = delegate
                            {
                                try { ran = cmd.RunInPdms(); }
                                catch (Exception exRun)
                                {
                                    ran = true; // 异常不代表未下发，交给结果文件判定
                                    error = "RunInPdms 抛异常: " + exRun.GetType().Name + " " + exRun.Message;
                                    Log("exec RunInPdms 异常: " + exRun.GetType().FullName + " " + exRun.Message + "\n" + exRun.StackTrace);
                                }
                                if (!ran)
                                {
                                    ok = false;
                                    // cmd.Error 判空后再取文本（原实现直接解引用，Error 为 null 时自身 NRE）
                                    try
                                    {
                                        if (cmd.Error != null) error = "RunInPdms failed: " + cmd.Error.MessageText();
                                        else error = "RunInPdms failed（Error 为 null，无报错文本）";
                                    }
                                    catch (Exception exErr) { error = "RunInPdms failed（读 Error 异常: " + exErr.Message + "）"; }
                                }
                            };
                            RunOnUi(runCmd);
                        }
                        // 轮询结果文件（兼容 RunInPdms 异步）。
                        // MCP 侧是在 C# 读到文件后才回包，故读到必须立即删，
                        // 否则下一次调用会先读到上一笔的残留结果。
                        // 修复：用 FileShare.ReadWrite 读取，并在文件被锁(IOException)时重试，
                        // 而非像旧逻辑那样在 ReadAllText 抛异常后直接中止整笔处理(曾导致 "文件正由另一进程使用")。
                        for (int i = 0; i < 200; i++) // 最多 ~20s
                        {
                            if (File.Exists(ResultFile))
                            {
                                try
                                {
                                    using (FileStream fs = new FileStream(ResultFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                                    using (StreamReader sr = new StreamReader(fs, Encoding.UTF8))
                                    {
                                        output = sr.ReadToEnd();
                                    }
                                    try { File.Delete(ResultFile); } catch { }
                                    try { File.AppendAllText(Path.Combine(BridgeDir, "bridge.log"),
                                        DateTime.Now + " cmd_" + id + " got result " + output.Length + " chars\n"); } catch { }
                                    break;
                                }
                                catch (IOException)
                                {
                                    // 结果文件仍被 PDMS 持有句柄(宏尚未 closefile / 异步写入中)，等下一轮重试
                                }
                            }
                            Thread.Sleep(100);
                        }
                    }
                    catch (Exception ex)
                    {
                        ok = false;
                        error = ex.Message;
                        // 2026-10-16：落 StackTrace，根治「只看到 ex.Message 无法定位 NRE 抛出点」的排查盲区
                        Log("exec 异常: " + ex.GetType().FullName + " " + ex.Message + "\n" + ex.StackTrace);
                    }

                    string resp = "{\"id\":" + id + ",\"ok\":" + (ok ? "true" : "false") +
                                  ",\"output\":" + JsonString(output) + ",\"error\":" + JsonString(error ?? "") + "}\n";
                    byte[] outb = Encoding.UTF8.GetBytes(resp);
                    ns.Write(outb, 0, outb.Length);
                }
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(Path.Combine(BridgeDir, "bridge.log"), DateTime.Now + " handle err: " + ex.Message + "\n"); } catch { }
            }
        }

        /* ---------------- kind:"op" 执行辅助（HeadlessDrawing 在 UI 线程跑） ---------------- */

        /// <summary>把 op 工作派发到 PDMS UI 线程执行（与窗口按钮同线程；三级回退见字段注释）。</summary>
        private static void RunOnUi(Action work)
        {
            // 1) 注册控件的 Invoke（句柄已创建时最稳）
            if (_uiControl != null && _uiControl.IsHandleCreated)
            {
                try { _uiControl.Invoke(work); return; }
                catch (Exception exInv) { Log("UI Invoke 失败(" + exInv.Message + ")，回退 SynchronizationContext/inline"); }
            }
            // 2) SynchronizationContext（UI 线程捕获）Post + 等待（600s 上限）
            if (_uiContext != null)
            {
                ManualResetEvent mre = new ManualResetEvent(false);
                _uiContext.Post(delegate { try { work(); } finally { mre.Set(); } }, null);
                if (!mre.WaitOne(600000)) Log("UI 线程 600s 未返回（op 可能未完成，查 PDMS 是否挂起）");
                return;
            }
            // 3) inline（带告警；首跑 probe_read 只读 op 负责验证跨线程 DB API 安全性）
            Log("无 UI 线程通道，op 在桥接线程 inline 执行（注意：DB API 跨线程未验证）");
            work();
        }

        private static void Log(string msg)
        {
            try { File.AppendAllText(Path.Combine(BridgeDir, "bridge.log"), DateTime.Now + " " + msg + "\n"); } catch { }
        }

        // 取 JSON 对象字段的原始 {..} 子串（简单处理字符串/转义内的花括号）
        private static string JsonObject(string json, string key)
        {
            int i = json.IndexOf('"' + key + '"');
            if (i < 0) return "";
            int colon = json.IndexOf(':', i + key.Length + 2);
            if (colon < 0) return "";
            int s = colon + 1;
            while (s < json.Length && (json[s] == ' ' || json[s] == '\t' || json[s] == '\n' || json[s] == '\r')) s++;
            if (s >= json.Length || json[s] != '{') return "";
            int depth = 0; bool inStr = false; bool esc = false;
            for (int j = s; j < json.Length; j++)
            {
                char c = json[j];
                if (inStr)
                {
                    if (esc) esc = false;
                    else if (c == '\\') esc = true;
                    else if (c == '"') inStr = false;
                    continue;
                }
                if (c == '"') inStr = true;
                else if (c == '{') depth++;
                else if (c == '}') { depth--; if (depth == 0) return json.Substring(s, j - s + 1); }
            }
            return "";
        }

        // 扁平 JSON 对象 → Dictionary<string,string>（op 参数无嵌套；数值/布尔值一律存为字符串）
        private static Dictionary<string, string> ParseFlatParams(string obj)
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(obj) || obj.Length < 2) return d;
            int i = 1, n = obj.Length;
            while (i < n)
            {
                while (i < n && (obj[i] == ' ' || obj[i] == '\t' || obj[i] == '\n' || obj[i] == '\r' || obj[i] == ',')) i++;
                if (i >= n || obj[i] == '}') break;
                if (obj[i] != '"') { i++; continue; }
                int ks = i + 1;
                i++; // 跳过开引号（2026-10-15 修复：漏掉这步导致 i-ks=-1 → ArgumentOutOfRangeException）
                while (i < n && obj[i] != '"') i++;
                string key = obj.Substring(ks, i - ks);
                i++;
                while (i < n && (obj[i] == ' ' || obj[i] == '\t')) i++;
                if (i >= n || obj[i] != ':') break;
                i++;
                while (i < n && (obj[i] == ' ' || obj[i] == '\t')) i++;
                string val;
                if (i < n && obj[i] == '"')
                {
                    val = ReadJsonString(obj, i);
                    i++;
                    while (i < n)
                    {
                        if (obj[i] == '\\') { i += 2; continue; }
                        if (obj[i] == '"') { i++; break; }
                        i++;
                    }
                }
                else
                {
                    int s2 = i;
                    while (i < n && obj[i] != ',' && obj[i] != '}') i++;
                    val = obj.Substring(s2, i - s2).Trim();
                }
                d[key] = val;
            }
            return d;
        }

        /* ---------------- 极简 JSON 辅助（避免 System.Text.Json 依赖） ---------------- */
        private static string JsonField(string json, string key)
        {
            // 匹配 "key" : "value"  或  "key" : value
            // 2026-10-15 修复：只认"后面跟冒号的"才是 key。旧版 IndexOf 会把 "kind":"op" 的
            // 值 "op" 当成 key "op" 的命中位置（靠偏移巧合才对），payload 一变就取错字段。
            string pat = "\"" + key + "\"";
            int from = 0;
            int n = json.Length;
            while (from < n)
            {
                int i = json.IndexOf(pat, from, StringComparison.Ordinal);
                if (i < 0) return "";
                int c = i + pat.Length;
                while (c < n && (json[c] == ' ' || json[c] == '\t')) c++;
                if (c < n && json[c] == ':')
                {
                    int s = c + 1;
                    while (s < n && (json[s] == ' ' || json[s] == '\t')) s++;
                    if (s < n && json[s] == '"')
                    {
                        // 字符串值：交给 ReadJsonString 做完整 JSON 反转义(旧逻辑把 \n/\\ 当字面量写入 .pml，导致 PML 语法全错)
                        return ReadJsonString(json, s);
                    }
                    // 数值
                    int en = s; while (en < n && json[en] != ',' && json[en] != '}' && json[en] != '\n') en++;
                    return json.Substring(s, en - s).Trim();
                }
                from = i + 1; // 命中处不是 key（是某个值），继续找下一个
            }
            return "";
        }

        // 解析 JSON 字符串字面量(从开引号位置 startIndex 开始)，正确处理转义序列，返回 C# 字符串。
        // 支持 \", \\, \/, \n, \r, \t, \b, \f, \uXXXX。
        private static string ReadJsonString(string json, int startIndex)
        {
            if (startIndex >= json.Length || json[startIndex] != '"') return "";
            StringBuilder b = new StringBuilder();
            int i = startIndex + 1;
            while (i < json.Length)
            {
                char c = json[i];
                if (c == '"') break; // 字符串结束
                if (c == '\\')
                {
                    i++;
                    if (i >= json.Length) break;
                    char e = json[i];
                    switch (e)
                    {
                        case '"': b.Append('"'); break;
                        case '\\': b.Append('\\'); break;
                        case '/': b.Append('/'); break;
                        case 'n': b.Append('\n'); break;
                        case 'r': b.Append('\r'); break;
                        case 't': b.Append('\t'); break;
                        case 'b': b.Append('\b'); break;
                        case 'f': b.Append('\f'); break;
                        case 'u':
                            if (i + 4 < json.Length)
                            {
                                string hex = json.Substring(i + 1, 4);
                                int cp;
                                if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out cp))
                                    b.Append((char)cp);
                                i += 4;
                            }
                            break;
                        default: b.Append(e); break;
                    }
                    i++;
                }
                else { b.Append(c); i++; }
            }
            return b.ToString();
        }

        private static int ParseInt(string s, int def)
        {
            int v; return int.TryParse(s, out v) ? v : def;
        }

        private static string JsonString(string s)
        {
            if (s == null) return "\"\"";
            StringBuilder b = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"') b.Append("\\\"");
                else if (c == '\\') b.Append("\\\\");
                else if (c == '\n') b.Append("\\n");
                else if (c == '\r') b.Append("\\r");
                else if (c < 32) b.Append("\\u" + ((int)c).ToString("x4"));
                else b.Append(c);
            }
            b.Append("\"");
            return b.ToString();
        }
    }

    /* ---------------- DialogWatcher（2026-10-19：PDMS 模态弹窗自动关闭看门狗） ----------------
     * 背景：headless 出图链最怕 Draft 弹模态错误框（实证：LTER 弹「Current element does not
     * have an attribute LTER」等人点 OK，整条流水线堵死 35 分钟）。本看门狗后台线程常驻，
     * 1 秒一轮扫本进程的可见 WS_POPUP 窗口：有 OK 类子控件即视为模态框，自动关闭。
     * 关闭三段式（对应 Accio 实证方案）：
     *   1) posted Enter（PostMessage VK_RETURN，不打扰前台焦点）；
     *   2) 1.5s 后仍在 → 真实鼠标点击按钮矩形中心（PDMS 的 OK 不是标准 Button 类，
     *      子控件查找不限定类名，BM_CLICK 对自绘控件无效故用真点击）；
     *   3) 仍不在 → WM_CLOSE 兜底。
     * 日志：bridge\dialog_watcher_cs.log。由 PdmsBridge.Start() 拉起，进程内随 PDMS 常驻。 */
    public static class DialogWatcher
    {
        private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr hWnd, EnumProc cb, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageW(IntPtr hWnd, int msg, IntPtr wParam, StringBuilder lParam);
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

        private const int WM_GETTEXT = 0x000D, WM_GETTEXTLENGTH = 0x000E;
        private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_CLOSE = 0x0010;
        private const int VK_RETURN = 0x0D;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
        private const int GWL_STYLE = -16;
        private const int WS_POPUP = unchecked((int)0x80000000);
        private static readonly string[] OkLabels = new string[] { "OK", "Ok", "ok", "Yes", "YES", "确定", "确认" };
        private static volatile bool _started;

        public static void EnsureStarted()
        {
            if (_started) return;
            _started = true;
            Thread t = new Thread(Loop) { IsBackground = true, Name = "PdmsDialogWatcher" };
            t.Start();
        }

        private static void Log(string s)
        {
            try { File.AppendAllText(Path.Combine(PdmsBridge.BridgeDir, "dialog_watcher_cs.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + s + "\n"); } catch { }
        }

        private static string WndText(IntPtr h)
        {
            try
            {
                IntPtr len = SendMessageW(h, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero);
                StringBuilder sb = new StringBuilder((int)len + 2);
                SendMessageW(h, WM_GETTEXT, (IntPtr)((int)len + 1), sb);
                return sb.ToString();
            }
            catch { return ""; }
        }

        private static IntPtr FindOkChild(IntPtr hdlg)
        {
            IntPtr hit = IntPtr.Zero;
            EnumChildWindows(hdlg, delegate(IntPtr h, IntPtr l)
            {
                string t = WndText(h).Trim();
                foreach (string ok in OkLabels)
                {
                    if (t == ok) { hit = h; return false; }
                }
                return true;
            }, IntPtr.Zero);
            return hit;
        }

        private static void Loop()
        {
            uint myPid = (uint)Process.GetCurrentProcess().Id;
            Log("watcher started pid=" + myPid);
            while (true)
            {
                try
                {
                    EnumWindows(delegate(IntPtr h, IntPtr l)
                    {
                        if (!IsWindowVisible(h)) return true;
                        uint pid; GetWindowThreadProcessId(h, out pid);
                        if (pid != myPid) return true;
                        if ((GetWindowLongW(h, GWL_STYLE) & WS_POPUP) == 0) return true; // 只扫弹窗，跳过主框
                        IntPtr okBtn = FindOkChild(h);
                        if (okBtn == IntPtr.Zero) return true;
                        string title = WndText(h);
                        CloseDialog(h, okBtn, title);
                        return true;
                    }, IntPtr.Zero);
                }
                catch (Exception ex) { Log("scan error: " + ex.Message); }
                Thread.Sleep(1000);
            }
        }

        private static void CloseDialog(IntPtr hdlg, IntPtr okBtn, string title)
        {
            // 1) posted Enter：不抢前台焦点
            PostMessageW(hdlg, WM_KEYDOWN, (IntPtr)VK_RETURN, IntPtr.Zero);
            PostMessageW(hdlg, WM_KEYUP, (IntPtr)VK_RETURN, IntPtr.Zero);
            Thread.Sleep(1500);
            if (!IsWindow(hdlg) || !IsWindowVisible(hdlg)) { Log("auto-closed dlg title=" + title + " via=enter"); return; }
            // 2) 真实鼠标点击按钮中心（自绘控件兜底）
            RECT r;
            if (GetWindowRect(okBtn, out r))
            {
                int x = (r.Left + r.Right) / 2, y = (r.Top + r.Bottom) / 2;
                SetCursorPos(x, y);
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(1500);
                if (!IsWindow(hdlg) || !IsWindowVisible(hdlg)) { Log("auto-closed dlg title=" + title + " via=click"); return; }
            }
            // 3) WM_CLOSE 兜底
            PostMessageW(hdlg, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            Log("auto-closed dlg title=" + title + " via=wmclose");
        }
    }
}
