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
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;   // DllImport/CharSet（弹窗杀手 Win32 P/Invoke 必需）
using System.Text;
using System.Threading;
using Aveva.Pdms.Utilities.CommandLine;
using Aveva.Pdms.Shared;

namespace PdmsBridge
{
    public static class PdmsBridge
    {
        // 多实例支持（2026-10-18）：端口自动顺延，目录按端口隔离。
        private static int BoundPort = 0;
        private static string BridgeDir = null;
        private static string ResultFile = null;
        private static TcpListener _listener;
        private static volatile bool _running;
        // PDMS 主线程(UI)上下文：catinv 盘点必须在 PDMS 线程上执行 DB API，
        // 否则从监听线程直接调 DbElement.* 会拿到 UNKNOWN/空树（坑：线程上下文）。
        private static SynchronizationContext _uiCtx = null;

        public static void Start()
        {
            // 进入痕迹（2026-10-19）：先于一切逻辑落一行日志。
            // bridge.log 里只有此行没有 "started on" → Start 中途异常；
            // 连此行都没有 → 程序集没被加载 / Addin.Start 没被调用（DLL 缺失/注册失效）。
            try
            {
                string td = @"C:\pdmsAgent\bridge";
                if (!Directory.Exists(td)) Directory.CreateDirectory(td);
                File.AppendAllText(Path.Combine(td, "bridge.log"),
                    DateTime.Now + " PdmsBridge.Start() entered pid=" + System.Diagnostics.Process.GetCurrentProcess().Id + "\n");
            }
            catch { }

            if (_running) return;
            _running = true;
            // 捕获 PDMS 主线程同步上下文(在 Addin.Start 的 UI 线程上调用)。
            try { _uiCtx = SynchronizationContext.Current; } catch { _uiCtx = null; }

            int basePort = ParseInt(Environment.GetEnvironmentVariable("PDMS_BRIDGE_PORT"), 18753);
            string envDir = Environment.GetEnvironmentVariable("PDMS_BRIDGE_DIR");
            string envResult = Environment.GetEnvironmentVariable("PDMS_BRIDGE_RESULT");

            _listener = null;
            string lastErr = "";
            for (int attempt = 0; attempt <= 20; attempt++)
            {
                int p = basePort + attempt;
                string dir = envDir;
                if (p != 18753 && !string.IsNullOrEmpty(dir))
                    dir = dir + "_p" + p;
                if (string.IsNullOrEmpty(dir))
                    dir = (p == 18753) ? @"C:\pdmsAgent\bridge" : @"C:\pdmsAgent\bridge_p" + p;
                try
                {
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    TcpListener l = new TcpListener(IPAddress.Loopback, p);
                    l.Start();
                    _listener = l;
                    BoundPort = p;
                    BridgeDir = dir;
                    ResultFile = (string.IsNullOrEmpty(envResult) || p != 18753) ? Path.Combine(dir, "result.txt") : envResult;
                    break;
                }
                catch (Exception ex) { lastErr = ex.Message; }
            }
            if (_listener == null)
            {
                _running = false;
                try
                {
                    string fb = string.IsNullOrEmpty(envDir) ? @"C:\pdmsAgent\bridge" : envDir;
                    if (!Directory.Exists(fb)) Directory.CreateDirectory(fb);
                    File.AppendAllText(Path.Combine(fb, "bridge.log"),
                        DateTime.Now + " start failed (ports " + basePort + ".." + (basePort + 20) + "): " + lastErr + "\n");
                }
                catch { }
                return;
            }
            try
            {
                Thread t = new Thread(AcceptLoop) { IsBackground = true, Name = "PdmsBridge" };
                t.Start();
                File.AppendAllText(Path.Combine(BridgeDir, "bridge.log"),
                    DateTime.Now + " PdmsBridge started on " + IPAddress.Loopback + ":" + BoundPort
                    + " (base " + basePort + ") dir=" + BridgeDir + "\n");
                // 自动关闭本进程的 PDMS 错误弹窗（2026-10-19）：
                // PML 未捕获错误会弹模态框冻住宏线程（看似桥卡死，实为等人点 OK）。
                StartPopupKiller();
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
            try { _listener.Stop(); } catch { }
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
                    try
                    {
                        // 极简 JSON 解析（仅需 id / code 两个字段）
                        id = ParseInt(JsonField(req, "id"), 0);
                        code = JsonField(req, "code");
                    }
                    catch { }

                    // C# 目录库盘点通道（2026-10-18）：kind=catinv。
                    // 背景：Design 会话里 PML 对 coll 成员的所有取名写法全部失败
                    //（.name / (!X).name / .string() / VAR NAME / !!x.ref / $!X 插值），
                    // 且未捕获的 PML 错误会弹模态框冻住宏线程。故走 C# DB API：
                    // 不执行 PML、不改 CE、不弹框，直接返回整棵树文本。
                    if (JsonField(req, "kind") == "catinv")
                    {
                        string inv = "";
                        try
                        {
                            // 直接在监听线程调 DB API（绝对路径 /CATALOGUE 不依赖当前元素上下文）。
                            // 切勿用 _uiCtx.Send：会引发重入死锁（listen线程Send等UI线程，
                            // UI线程内DB API又向UI线程Send，互相死等）。
                            inv = RunCatInventory();
                        }
                        catch (Exception exi) { inv = "CATINV_ERR_" + exi.Message; }
                        byte[] ib = Encoding.UTF8.GetBytes(
                            "{\"id\":" + id + ",\"ok\":true,\"output\":" + JsonString(inv) + ",\"error\":\"\"}\n");
                        try { ns.Write(ib, 0, ib.Length); } catch { }
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
                        string pml = Path.Combine(BridgeDir, "cmd_" + id + ".pml");
                        // 关键修复: 用无 BOM 的 UTF-8 写 .pml。否则 System.Text.Encoding.UTF8
                        // 默认带 BOM(EF BB BF)，PML 在 BOM + 非 ASCII(中文注释) 下编码探测冲突、
                        // 宏解析中止 → RunInPdms failed / 空输出。无 BOM 时 ASCII 与中文均正常。
                        File.WriteAllText(pml, code ?? "", new System.Text.UTF8Encoding(false));
                        // 用 $M（PML 宏执行）而非 DO（CP 宏执行）下发：
                        // 2026-10-14 TTY 全链路验证过 $M 可执行 CP+PML 混合宏
                        // （NEW/END 建链、openfile/writefile/closefile 落盘均通过），
                        // 见 D:\pdmsAgent\view_api\diary 证据链。
                        // RunInPdms 是实例方法（无静态重载）：CreateCommand(...).RunInPdms()。
                        // 失败时 command.Error.MessageText() 带回 PDMS 报错文本（drawE3D.cs 先例）。
                        // 注意：不要 pml.Replace('/', '\\')——会把单引号字符串内的 /X* 变成 \X*，
                        // CP 层把 \X 当作转义序列导致语法错误（如 var !NC ARRAY(^^) 报错）。
                        // $M 命令接受正斜杠路径，直接拼接即可。
                        Command cmd = Command.CreateCommand("$M " + pml);
                        if (!cmd.RunInPdms())
                        {
                            ok = false;
                            try { error = "RunInPdms failed: " + cmd.Error.MessageText(); } catch { }
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
                    catch (Exception ex) { ok = false; error = ex.Message; }

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

        /* ---------------- C# 目录库盘点（catinv，不依赖 PML/不改 CE） ---------------- */

        private static int _catinvNodes = 0;

        // 从目录库根 /CATALOGUE 向下递归打印整棵树。
        // 每行：层级点.类型|名字。取名用 GetAsString(DbAttributeInstance.NAME)（drawE3D.cs 实战写法）。
        // 直接在监听线程调（绝对路径不依赖当前元素上下文；用 _uiCtx.Send 会重入死锁，禁）。
        public static string RunCatInventory()
        {
            StringBuilder sb = new StringBuilder();
            Aveva.Pdms.Database.DbElement start = null;
            // 起点：绝对路径 /CATALOGUE（cmd_930 证明确实存在），监听线程按路径解析，
            // 不依赖"当前元素"上下文（CurrentElement.Element 在监听线程返回 UNKNOWN 无效）。
            try
            {
                start = Aveva.Pdms.Database.DbElement.GetElement("/CATALOGUE");
                sb.AppendLine("CAT_NULL_" + (start == null || start.IsNull));
                sb.AppendLine("CAT_TYPE_" + safeType(start));
                sb.AppendLine("CAT_NAME_" + safeName(start));
            }
            catch (Exception ex) { sb.AppendLine("CAT_ERR_" + ex.Message); }

            if (start == null || start.IsNull)
            {
                // 退化：无参 GetElement()
                try
                {
                    start = Aveva.Pdms.Database.DbElement.GetElement();
                    sb.AppendLine("GE_TYPE_" + safeType(start));
                    sb.AppendLine("GE_NAME_" + safeName(start));
                }
                catch (Exception ex) { sb.AppendLine("GE_ERR_" + ex.Message); }
            }

            if (start != null && !start.IsNull)
            {
                _catinvNodes = 0;
                WalkCat(start, 0, sb);
            }
            return sb.ToString();
        }

        private static string safeType(Aveva.Pdms.Database.DbElement el)
        { try { return el.GetElementType().ToString(); } catch (Exception ex) { return "TYPEERR_" + ex.Message; } }
        private static string safeName(Aveva.Pdms.Database.DbElement el)
        { try { return el.GetAsString(Aveva.Pdms.Database.DbAttributeInstance.NAME); } catch (Exception ex) { return "NAMEERR_" + ex.Message; } }

        private static void WalkCat(Aveva.Pdms.Database.DbElement el, int depth, StringBuilder sb)
        {
            if (depth > 6 || _catinvNodes > 2000) return;
            try
            {
                foreach (object o in el.Members())
                {
                    _catinvNodes++;
                    Aveva.Pdms.Database.DbElement m;
                    try { m = (Aveva.Pdms.Database.DbElement)o; } catch { continue; }
                    string type = "?"; string name = "";
                    try { type = m.GetElementType().ToString(); } catch { }
                    try { name = m.GetAsString(Aveva.Pdms.Database.DbAttributeInstance.NAME); } catch { }
                    sb.AppendLine(new string('.', depth) + type + "|" + name);
                    WalkCat(m, depth + 1, sb);
                }
            }
            catch { }
        }

        /* ---------------- 自动关闭 PDMS 错误弹窗（2026-10-19） ----------------
         * 背景：PML 未捕获错误会弹模态框冻住宏线程（看似桥卡死，实为等人点 OK）。
         * 实战来源：Accio 会话（posted-Enter 关框不影响前台）+ vision_watcher/pdms_error_watcher.py。
         * 要点：
         *  - 后台线程轮询本进程的模态对话框（Win32 类 #32770）；
         *  - 标题白名单默认 "Error"，可用环境变量 PDMS_BRIDGE_AUTOKILL_TITLES 扩展（逗号分隔）；
         *  - 点 OK：EnumChildWindows 找文本为 OK/确定 的子窗口——【不限控件类】
         *    （PDMS 的 OK 按钮不是标准 Button 类），先 PostMessage(BM_CLICK) 不动光标；
         *  - 不奏效 → 对对话框 Post WM_KEYDOWN/UP VK_RETURN（posted-Enter，不影响前台）；
         *  - 仍不奏效 → 真实鼠标点击按钮中心（会短暂移动光标，最后手段）；
         *  - 只关本进程弹窗（GetWindowThreadProcessId == 自己 PID），绝不碰其他程序；
         *    Paragon/Design 各自的桥实例各管各的进程，互不越界；
         *  - 总开关：环境变量 PDMS_BRIDGE_AUTOKILL=0 关闭本功能。
         */

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lp);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr hWnd, EnumProc cb, IntPtr lp);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out WinRect r);
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wp, IntPtr lp);
        private delegate bool EnumProc(IntPtr hWnd, IntPtr lp);
        private struct WinRect { public int Left, Top, Right, Bottom; }
        private const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, BM_CLICK = 0x00F5, VK_RETURN = 0x0D;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;

        private static void StartPopupKiller()
        {
            try
            {
                if (Environment.GetEnvironmentVariable("PDMS_BRIDGE_AUTOKILL") == "0") return;
                Thread k = new Thread(PopupKillLoop) { IsBackground = true, Name = "PdmsBridgePopupKill" };
                k.Start();
            }
            catch { }
        }

        private static void PopupKillLoop()
        {
            uint myPid = 0;
            try { myPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id; } catch { return; }
            string titles = Environment.GetEnvironmentVariable("PDMS_BRIDGE_AUTOKILL_TITLES");
            if (string.IsNullOrEmpty(titles)) titles = "error";
            string[] want = titles.ToLower().Split(',');
            while (_running)
            {
                try { KillPopupsOnce(myPid, want); } catch { }
                Thread.Sleep(400);
            }
        }

        private static void KillPopupsOnce(uint myPid, string[] wantTitles)
        {
            EnumWindows(delegate(IntPtr h, IntPtr lp)
            {
                try
                {
                    if (!IsWindowVisible(h)) return true;
                    uint pid; GetWindowThreadProcessId(h, out pid);
                    if (pid != myPid) return true;               // 只关自己进程的弹窗
                    StringBuilder cn = new StringBuilder(256); GetClassName(h, cn, 256);
                    if (cn.ToString() != "#32770") return true;  // 只处理对话框类
                    StringBuilder tt = new StringBuilder(256); GetWindowText(h, tt, 256);
                    string title = tt.ToString().Trim();
                    bool match = false;
                    foreach (string w in wantTitles)
                    {
                        string s = w.Trim();
                        if (s.Length > 0 && title.ToLower() == s) { match = true; break; }
                    }
                    if (!match) return true;
                    DismissDialog(h, title);
                }
                catch { }
                return true;
            }, IntPtr.Zero);
        }

        private static void DismissDialog(IntPtr dlg, string title)
        {
            // 找 OK 按钮：不限控件类，只认子窗口文本（PDMS 的 OK 不是标准 Button）
            IntPtr okBtn = IntPtr.Zero;
            EnumChildWindows(dlg, delegate(IntPtr child, IntPtr lp)
            {
                try
                {
                    StringBuilder tt = new StringBuilder(256); GetWindowText(child, tt, 256);
                    string txt = tt.ToString().Trim().ToLower();
                    if (txt == "ok" || txt == "确定") { okBtn = child; return false; }
                }
                catch { }
                return true;
            }, IntPtr.Zero);

            string via = "";
            if (okBtn != IntPtr.Zero)
            {
                PostMessage(okBtn, BM_CLICK, IntPtr.Zero, IntPtr.Zero);   // 消息点击，不动光标
                via = "bmclick";
            }
            // 最多等 ~1s，期间依次升级：BM_CLICK → posted-Enter → 真实鼠标点击
            for (int i = 0; i < 4 && IsWindow(dlg) && IsWindowVisible(dlg); i++)
            {
                Thread.Sleep(250);
                if (!IsWindow(dlg) || !IsWindowVisible(dlg)) break;
                if (i == 1)
                {
                    PostMessage(dlg, WM_KEYDOWN, (IntPtr)VK_RETURN, IntPtr.Zero);  // posted-Enter 兜底
                    PostMessage(dlg, WM_KEYUP, (IntPtr)VK_RETURN, IntPtr.Zero);
                    via = (via.Length > 0 ? via + "+" : "") + "enter";
                }
                if (i == 3 && okBtn != IntPtr.Zero)
                {
                    WinRect r;
                    if (GetWindowRect(okBtn, out r) && r.Right > r.Left && r.Bottom > r.Top)
                    {
                        int x = (r.Left + r.Right) / 2, y = (r.Top + r.Bottom) / 2;
                        if (SetCursorPos(x, y))
                        {
                            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                            via = (via.Length > 0 ? via + "+" : "") + "mouse";
                        }
                    }
                }
            }
            try
            {
                File.AppendAllText(Path.Combine(BridgeDir, "bridge.log"),
                    DateTime.Now + " auto-kill popup title=" + title + " via=" + via
                    + (IsWindow(dlg) && IsWindowVisible(dlg) ? " (STILL OPEN)" : " closed") + "\n");
            }
            catch { }
        }

        /* ---------------- 极简 JSON 辅助（避免 System.Text.Json 依赖） ---------------- */
        private static string JsonField(string json, string key)
        {
            // 匹配 "key" : "value"  或  "key" : value
            int i = json.IndexOf('"' + key + '"');
            if (i < 0) return "";
            int colon = json.IndexOf(':', i + key.Length + 2);
            if (colon < 0) return "";
            int s = colon + 1;
            while (s < json.Length && (json[s] == ' ' || json[s] == '\t')) s++;
            if (s < json.Length && json[s] == '"')
            {
                // 字符串值：交给 ReadJsonString 做完整 JSON 反转义(旧逻辑把 \n/\\ 当字面量写入 .pml，导致 PML 语法全错)
                return ReadJsonString(json, s);
            }
            // 数值
            int en = s; while (en < json.Length && json[en] != ',' && json[en] != '}' && json[en] != '\n') en++;
            return json.Substring(s, en - s).Trim();
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
}
