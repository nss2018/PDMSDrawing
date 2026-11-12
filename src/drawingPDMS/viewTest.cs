using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Aveva.Pdms.Database;
using Aveva.PDMS.Database.Filters;
using Aveva.Pdms.Geometry;
using Aveva.Pdms.Shared;
using Aveva.Pdms.Utilities.CommandLine;
using Aveva.Pdms.Graphics;
using CE = Aveva.Pdms.Shared.CurrentElement;
using ATT = Aveva.Pdms.Database.DbAttributeInstance;

namespace drawingPDMS
{
    /// <summary>
    /// 视图 API 实机测试（程序内调用，窗口"视图API测试"页签触发）。
    ///
    /// 目的：验证本项目使用的底层视图调用接口在已部署 PDMS 12.1.SP4 运行时全部可用。
    /// 静态对账（反编译 AVEVA 依赖 DLL）见 D:\pdmsAgent\view_api\view-api-verification.md。
    ///
    /// 测试链：
    ///   [1] 模块探测 —— gph3ddesign1（Design 3D 视图会话）是否可用
    ///   [2] 几何令牌 —— Direction/Position 字符串令牌与坐标运算
    ///   [3] PML 变量 —— RunInPdms + GetPMLVariableReal/String 回读
    ///   [4] DB 视图链 —— SHEET→VIEW 创建、属性写入(XYPS/SIZE/THPO/DIR/VSCA/VTYP)、读回、
    ///                     IDLIST/ADDENTRY、UpdateModelView、UPDATE all（测试树保活，Run 结尾统一清理）
    ///   [5] DrawList  —— DrawListManager.Instance / CurrentDrawList / getViewDrawList
    ///   [6] AID 标注 + SheetPosition —— 直接调用原厂 FangYuan.PDMS.Draft.SheetPosition /
    ///                     FangYuan.PDMS.Aid（俯视图 ADEG 0/90/180/270 矩阵 + AID 五型命令冒烟 + aid clear）
    ///   [7] 管道程序化创建/连接 —— 直接调用原厂 FangYuan.PDMS.Pipe/Paragon/Utilities：
    ///                     只读段（unit/mode/规格发现/CoCo 表/收集工具）+ 写段（PipeCreateAndUpdate→
    ///                     PipeNamed→BranchCreate→FittingCreateForward×2→ConnectLeave→Slope，
    ///                     测试管 Run 结尾统一清理）
    ///
    /// 兼容 net35（v141 编译器）：不用字符串插值/异步。
    /// </summary>
    public static class ViewApiTest
    {
        private static int _pass;
        private static int _fail;
        private static readonly StringBuilder _log = new StringBuilder();

        private static Action<string> _onLine;

        // [4] 测试树保活至 [6] AID 标注测试（复用同一 SHEET/VIEW）；Run 结尾统一清理
        private static List<DbElement> _created = new List<DbElement>();
        private static DbElement _testView;

        // 看门狗：超过 75 秒无进度时把卡住的步骤/最后输出写文件（UI 冻结时依然有效）
        private static Timer _watchdog;
        private static DateTime _lastProgress;
        private static string _currentStep = "";
        private static string _lastLine = "";
        private static bool _wdFired;

        private static void WatchdogTick(object state)
        {
            try
            {
                if (!_wdFired && (DateTime.Now - _lastProgress).TotalSeconds > 75)
                {
                    _wdFired = true;
                    double stall = (DateTime.Now - _lastProgress).TotalSeconds;
                    File.AppendAllText(@"D:\pdmsAgent\view_test_watchdog.log",
                        "WATCHDOG " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                        + " 停滞 " + (int)stall + "s  step=" + _currentStep
                        + "  lastLine=" + _lastLine.Replace("\r", " ").Replace("\n", " ") + "\n---\n");
                }
            }
            catch { }
        }

        private static void Emit(string line)
        {
            _lastProgress = DateTime.Now;
            _wdFired = false;
            _lastLine = line;
            _log.AppendLine(line);
            if (_onLine != null) _onLine(line + Environment.NewLine);
        }

        private static void Step(string name)
        {
            _currentStep = name;
            Emit("---- " + name + " ----");
        }

        private static void Ok(string msg)
        {
            _pass++;
            Emit("  [PASS] " + msg);
        }

        private static void Bad(string msg)
        {
            _fail++;
            Emit("  [FAIL] " + msg);
        }

        private static void Info(string msg)
        {
            Emit("  [INFO] " + msg);
        }

        private static bool Try(bool ok, string passMsg, string failMsg)
        {
            if (ok) Ok(passMsg);
            else Bad(failMsg);
            return ok;
        }

        public static int PassCount { get { return _pass; } }
        public static int FailCount { get { return _fail; } }

        private const string LogDir = @"D:\pdmsAgent\view_api";

        public static string Run(Action<string> onLine)
        {
            _pass = 0;
            _fail = 0;
            _log.Length = 0;
            _onLine = onLine;
            _created = new List<DbElement>();
            _testView = null;
            _lastProgress = DateTime.Now;
            _currentStep = "init";
            _lastLine = "";
            _wdFired = false;
            // 完整日志落盘（UI 文本框会截断长行；智能体事后直接读文件）
            string logFile = LogDir + @"\viewtest_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log";
            _watchdog = new Timer(WatchdogTick, null, 5000, 5000);
            try
            {
                Emit("=== 视图 API 实机测试  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===");
                Emit("日志落盘: " + logFile);
                Emit("");

                bool isDesign = ProbeModule();
                TestGeometryTokens();
                TestDrawTree(isDesign);
                TestModelSelection(isDesign);
                TestPmlVariables();
                TestViewDbChain(isDesign);
                TestDrawList(isDesign);
                TestAidAnnotations(isDesign);
                TestPipeTooling();

                Emit("");
                Emit("=== 结果: PASS " + _pass + " / FAIL " + _fail + " ===");
            }
            finally
            {
                if (_watchdog != null) { _watchdog.Dispose(); _watchdog = null; }
                // [4] 测试树统一清理（保活给 [6] AID 标注用）
                try { CleanupChain(_created); } catch { }
                try
                {
                    if (!Directory.Exists(LogDir)) Directory.CreateDirectory(LogDir);
                    File.WriteAllText(logFile, _log.ToString(), Encoding.UTF8);
                }
                catch { }
            }
            return _log.ToString();
        }

        // [1] 模块探测：gph3ddesign1 只存在于 Design 图形会话。
        //     这直接回答 "DRAW 模块能否打开 Design 视图" —— 运行时给出证据。
        private static bool ProbeModule()
        {
            Step("[1] 模块探测 (gph3ddesign1)");
                // 2026-10-15：先纯 DB 解析判定——DRAW 会话里下面的 PML 命令必然失败、
                // 每次必弹红框报错（用户："每次都是一样的错"）。纯 DB 判定结论相同且无弹窗。
                DbElement gph = null;
                try { gph = DbElement.GetElement("gph3ddesign1"); } catch { }
                if (gph == null || gph.IsNull)
                {
                    Bad("当前会话无 gph3ddesign1（纯DB解析，无弹窗）—— DRAW 模块无法打开 Design 3D 视图（跨图形会话）");
                    Info("DRAW 模块可驱动的是图纸 2D 视图图形（DRA 命令行 / DrawList / SHEET-VIEW 属性）");
                    return false;
                }
                string dir = "";
                string pmlErr = "";
                try
                {
                    // PML 变量名规则：≤4 字符、不含下划线（!VT_DIR 会被解析成 !VT 报错）
                    // 19:33 实证：赋值形式 !VTD = ... 执行成功但 GetPMLVariable 读回未初始化值
                    // → 改用 AVEVA 官方形式 var !!NAME <expr>（E3D 原厂 Utilities 同款，见 [3] 注释）
                    Command cmd = Command.CreateCommand("var !!VTD gph3ddesign1.view.direction");
                    if (cmd.RunInPdms())
                        dir = cmd.GetPMLVariableString("VTD");
                    else
                        pmlErr = CmdErr(cmd);
                }
                catch (Exception ex)
                {
                    Info("探测异常: " + ex.Message);
                }
                if (!string.IsNullOrEmpty(dir))
                {
                    Ok("Design 模块: gph3ddesign1.view.direction = " + dir);
                    Info("此处可驱动 Design 3D 视图（MoveView/Clip/Enhance，FangYuan.PDMS.Design 系列）");
                    return true;
                }
                Bad("gph3ddesign1 可解析但读 direction 失败"
                   + (pmlErr != "" ? "  PML报错: " + pmlErr : ""));
            return false;
        }

        // [2] 几何令牌 API（D3 数学层为原生互操作，逐调用设检查点定位卡死点）
        private static void TestGeometryTokens()
        {
            Step("[2] 几何令牌 (Direction / Position)");
            try
            {
                Info("2.1 Direction.Create() 无参冒烟（D3 层首触）");
                Direction d0 = Direction.Create();
                Info("2.1 完成: " + d0);

                Info("2.2 Direction.Create(\"D\") / Create(\"U\")（生产同款令牌）");
                Direction dD = Direction.Create("D");
                Direction dU = Direction.Create("U");
                Try(dD != null && dD.ToString() == "D", "Create(\"D\") = " + dD, "Create(\"D\") = " + dD);
                Try(dU != null && dU.ToString() == "U", "Create(\"U\") = " + dU, "Create(\"U\") = " + dU);

                Info("2.3 Angle(D,U)");
                double ang = dD.Angle(dU);
                // D=下(0,0,-1) / U=上(0,0,1) 反向 → 180° 才是正确值（19:04 实测 180，旧版期望 90 是错的）
                Try(Math.Abs(ang - 180.0) < 0.1, "Angle(D,U) ≈ 180° (实际 " + ang + "°)", "Angle(D,U) = " + ang);

                Info("2.4 基础令牌 E/w + 复合令牌 n 45 w");
                Direction dE = Direction.Create("E");
                Direction dW = Direction.Create("w");
                Try(dE != null && dW != null, "Create(\"E\") = " + dE + " / Create(\"w\") = " + dW, "基础令牌 E/w 返回 null");
                Direction dNW = Direction.Create("n 45 w");
                if (dNW == null)
                {
                    Info("2.4 复合令牌 \"n 45 w\" 本会话返回 null（D3 解析不接受此格式），基础令牌不受影响");
                }
                else
                {
                    Info("2.5 Angle(n45w, w) + RotateAbout");
                    double angW = dNW.Angle(dW);
                    Try(Math.Abs(angW - 45.0) < 1.0,
                        "Angle(n45w, w) ≈ 45° (实际 " + angW + "°)", "夹角 = " + angW);
                    dNW.RotateAbout(Direction.Create("up"), 90);   // 原地修改（返回 void）
                    Info("2.5 RotateAbout(up,90) 原地修改: n 45 w -> " + dNW);
                }
            }
            catch (Exception ex) { Bad("Direction 令牌: " + ex.Message); }

            try
            {
                Info("2.6 Position.Create(100,200,300)");
                Position p1 = Position.Create(100, 200, 300);
                Try(p1 != null && p1.X == 100 && p1.Y == 200 && p1.Z == 300,
                    "Position.Create(100,200,300) 分量正确", "P = " + p1);

                Info("2.7 Distance / MidPoint");
                Position p2 = Position.Create(100, 200, 350);
                double dist = p1.Distance(p2);
                Try(Math.Abs(dist - 50) < 0.01, "Distance(p1,p2) = 50", "dist = " + dist);
                Position pm = p1.MidPoint(p2);
                Try(Math.Abs(pm.Z - 325) < 0.01, "MidPoint.Z = 325", "Z = " + pm.Z);

                Info("2.8 MoveBy 原地修改");
                p1.MoveBy(Direction.Create("up"), 50);   // 原地修改（返回 void）
                Try(Math.Abs(p1.Z - 350) < 0.01, "MoveBy(U,50) 原地修改: Z 300 -> 350", "Z = " + p1.Z);
            }
            catch (Exception ex) { Bad("Position 运算: " + ex.Message); }

            try
            {
                Info("2.9 Position.Create(\"w 1 n 2 u 3\")（位置字符串）");
                Position pStr = null;
                try { pStr = Position.Create("w 1 n 2 u 3"); } catch { }
                if (pStr == null)
                {
                    Info("2.9 位置字符串本会话返回 null（D3 解析不接受此格式），坐标构造路径不受影响");
                }
                else
                {
                    // 19:51 实证：X=-1 Y=2 Z=3 —— 非单位问题，是方向符号：
                    //   PDMS X 轴东向(E)为正，W = -X（2.6 的 Create(100,200,300) 佐证 E/N/U=+X/+Y/+Z）
                    //   手册佐证（PML .pdf 附录 A）：A.10 内部值恒为数据库单位（距离=mm）；图 A.1 W 与 E 反向
                    //   期望: X=-1, Y=2, Z=3（项目单位 MM）
                    double ex = pStr.X, ey = pStr.Y, ez = pStr.Z;
                    bool ok = Math.Abs(ex + 1) < 1e-6 && Math.Abs(ey - 2) < 1e-6 && Math.Abs(ez - 3) < 1e-6;
                    string comp = "X=" + ex + " Y=" + ey + " Z=" + ez;
                    Try(ok, "位置字符串解析正确（W=-X）: " + comp,
                        "P = " + pStr + " " + comp + "（期望 X=-1 Y=2 Z=3）");
                }
            }
            catch (Exception ex)
            {
                Info("Position.Create(string) 异常: " + ex.Message);
            }
        }

        // [2b] 设计树 + 出图树遍历（双路：WORLD 下 SITE/DEPT 及直挂 REGISTRY → DRWG → SHEET → VIEW）
        //      生产插件模式：设计树选中元素 → WORLD 下 REGISTRY 生成 SHEET → SHEET 页生成图纸
        private static void TestDrawTree(bool isDesign)
        {
            Step("[2b] 设计树 + 出图树遍历 (纯DB只读, 大库下可能需要数十秒)");
            try
            {
                DbElement world = DbElement.GetElement("/*");
                if (world == null || world.IsNull) { Bad("无法取得 /* WORLD"); return; }

                int nSite = 0, nDept = 0, nRegiDirect = 0, nDrwg = 0, nSheet = 0, nView = 0;
                bool stop = false;

                // 1) WORLD 顶层分类
                DbElement[] tops = world.Members();
                List<DbElement> regis = new List<DbElement>();
                foreach (DbElement top in tops)
                {
                    if (top == null || top.IsNull || top.IsDeleted) continue;
                    DbElementType t = top.GetElementType();
                    if (t == DbElementTypeInstance.SITE) nSite++;
                    else if (t == DbElementTypeInstance.DEPT) nDept++;
                    else if (t == DbElementTypeInstance.REGISTRY)
                    {
                        nRegiDirect++;
                        regis.Add(top);
                        continue;
                    }
                    // 2) SITE / DEPT 下的 REGISTRY
                    if (t == DbElementTypeInstance.SITE || t == DbElementTypeInstance.DEPT)
                    {
                        DbElement[] rs = top.Members(DbElementTypeInstance.REGISTRY);
                        foreach (DbElement r in rs)
                            if (r != null && !r.IsNull) regis.Add(r);
                    }
                }

                // 3) REGISTRY → DRWG → SHEET → VIEW（出图树主体）
                foreach (DbElement regi in regis)
                {
                    if (stop) break;
                    DbElement[] drwgs = regi.Members(DbElementTypeInstance.DRWG);
                    foreach (DbElement drwg in drwgs)
                    {
                        if (stop || drwg == null || drwg.IsNull) continue;
                        nDrwg++;
                        DbElement[] sheets = drwg.Members(DbElementTypeInstance.SHEET);
                        foreach (DbElement sheet in sheets)
                        {
                            if (sheet == null || sheet.IsNull) continue;
                            nSheet++;
                            DbElement[] views = sheet.Members(DbElementTypeInstance.VIEW);
                            if (views != null) nView += views.Length;
                            if (nSheet >= 500) { stop = true; break; }
                        }
                    }
                }
                Ok("WORLD 顶层: SITE " + nSite + " / DEPT " + nDept + " / REGISTRY(直挂) " + nRegiDirect
                   + "  |  出图树: REGISTRY 合计 " + regis.Count + " / DRWG " + nDrwg
                   + " / SHEET " + nSheet + " / VIEW " + nView + (stop ? "（SHEET 超 500 截断）" : ""));
                Info("结论: 设计树(SITE)与出图树(REGISTRY→SHEET→VIEW)在 " + (isDesign ? "Design" : "DRAW")
                   + " 模块下均可直接读/遍历/写（数据层无模块限制）");
            }
            catch (Exception ex) { Bad("目录树遍历: " + ex.Message); }
        }

        // [2c] 3D 模型范围选择（纯数据库 WVOL/WORPOS + LimitsBox，无图形依赖，Design 下同样可用）
        private class Vbox
        {
            public double X1, Y1, Z1, X2, Y2, Z2;
        }

        private static int _selVisited;
        private static int _countVisited;

        private static double SelF(double v) { return Math.Round(v, 1); }

        /// <summary>读 WVOL 6 元包围盒（[0..2]=min, [3..5]=max，与 GetElementLimits 语义一致）</summary>
        private static Vbox GetWvolBox(DbElement el)
        {
            try
            {
                double[] w = el.GetDoubleArray(ATT.WVOL);
                if (w == null || w.Length < 6) return null;
                return new Vbox
                {
                    X1 = Math.Min(w[0], w[3]), X2 = Math.Max(w[0], w[3]),
                    Y1 = Math.Min(w[1], w[4]), Y2 = Math.Max(w[1], w[4]),
                    Z1 = Math.Min(w[2], w[5]), Z2 = Math.Max(w[2], w[5])
                };
            }
            catch { return null; }
        }

        private static bool BoxesOverlap(Vbox a, Vbox b)
        {
            return a.X1 <= b.X2 && b.X1 <= a.X2 &&
                   a.Y1 <= b.Y2 && b.Y1 <= a.Y2 &&
                   a.Z1 <= b.Z2 && b.Z1 <= a.Z2;
        }

        private static DbElement FindElementWithWvol(DbElement root, int depth)
        {
            if (root == null || root.IsNull || root.IsDeleted || depth > 10 || _selVisited > 3000) return null;
            _selVisited++;
            if (GetWvolBox(root) != null) return root;
            DbElement[] members = root.Members();
            foreach (DbElement m in members)
            {
                DbElement found = FindElementWithWvol(m, depth + 1);
                if (found != null) return found;
            }
            return null;
        }

        private static int CountInRange(Vbox range, DbElement root, int depth, ref int total)
        {
            if (root == null || root.IsNull || root.IsDeleted || depth > 10 || _countVisited > 3000) return 0;
            _countVisited++;
            total++;
            int n = 0;
            Vbox box = GetWvolBox(root);
            bool inRange;
            if (box != null)
            {
                inRange = BoxesOverlap(box, range);
            }
            else
            {
                Position p = null;
                try { p = root.GetPosition(ATT.WORPOS); } catch { }
                inRange = p != null &&
                          p.X >= range.X1 && p.X <= range.X2 &&
                          p.Y >= range.Y1 && p.Y <= range.Y2 &&
                          p.Z >= range.Z1 && p.Z <= range.Z2;
            }
            if (inRange) n++;
            DbElement[] members = root.Members();
            foreach (DbElement m in members)
                n += CountInRange(range, m, depth + 1, ref total);
            return n;
        }

        private static void TestModelSelection(bool isDesign)
        {
            Step("[2c] 3D 模型范围选择 (WVOL/WORPOS + LimitsBox, 纯数据库)");
            try
            {
                DbElement world = DbElement.GetElement("/*");
                if (world == null || world.IsNull) { Bad("无法取得 /* WORLD"); return; }

                // 跳过 WORLD 自身（它的包围盒=全模型，选出来没有意义），从顶层子元素找
                _selVisited = 0;
                DbElement sample = null;
                DbElement[] tops2 = world.Members();
                foreach (DbElement t in tops2)
                {
                    sample = FindElementWithWvol(t, 0);
                    if (sample != null) break;
                }
                if (sample == null)
                {
                    Info("前 3000 个元素中未找到带 WVOL 包围盒的元素（数据库可能较空），跳过");
                    return;
                }
                Vbox box = GetWvolBox(sample);
                string sampleName = sample.GetAsString(ATT.NAME);
                Info("样例: " + sampleName + "  包围盒 (" + SelF(box.X1) + "," + SelF(box.Y1) + "," + SelF(box.Z1)
                     + ")-(" + SelF(box.X2) + "," + SelF(box.Y2) + "," + SelF(box.Z2) + ")  (检索 " + _selVisited + " 元素)");

                // LimitsBox（Geometry API）回环：包围盒中心必含于自身盒
                LimitsBox lb = LimitsBox.Create(
                    Position.Create(box.X1, box.Y1, box.Z1),
                    Position.Create(box.X2, box.Y2, box.Z2));
                double mx = (box.X1 + box.X2) / 2.0;
                double my = (box.Y1 + box.Y2) / 2.0;
                double mz = (box.Z1 + box.Z2) / 2.0;
                Try(lb.ContainsPoint(Position.Create(mx, my, mz)),
                    "LimitsBox.Create + ContainsPoint(中心) = true", "LimitsBox 包含判定失败");

                // 以样例自身包围盒为选择范围，统计范围内元素（WVOL 相交或 WORPOS 落入）
                _countVisited = 0;
                int total = 0;
                int n = CountInRange(box, world, 0, ref total);
                Try(n >= 1, "范围选择结果: " + n + " 个元素在范围内 (检索 " + total + " 元素)", "范围选择结果为 0（逻辑错误）");
                Info("结论: 3D 模型范围选择在 " + (isDesign ? "Design" : "DRAW") + " 模块均可用（纯数据通道）；"
                   + "入绘制清单再配 ADD ALL WITHIN / DrawList.AddWithinVolume，3D 视图框选配 givolume");
            }
            catch (Exception ex) { Bad("模型范围选择: " + ex.Message); }
        }

        // [3] PML 命令 + 变量回读
        private static string CmdErr(Command cmd)
        {
            // RunInPdms 失败时取 PDMS 报错原文（PdmsBridge.cs:134 同款用法）
            try { return cmd.Error.MessageText(); }
            catch { return ""; }
        }

        /// <summary>截断长字符串用于日志</summary>
        private static string Short(string s)
        {
            if (s == null) return "(null)";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > 80 ? s.Substring(0, 80) + "..." : s;
        }

        private static void TestPmlVariables()
        {
            Step("[3] PML 命令与变量回读 (显示通道 !! 形式)");
            // 19:33 → 20:32 四轮实测 + 官方手册(附录A) + 课件 结论：
            //   GetPMLVariable* 只捕获走了 PML "显示通道(!!)" 的值：
            //   - !NAME = expr                 普通赋值，无显示 → 读不回（野值 2.9e-138 / ""）
            //   - !!NAME = <expr>              显示式赋值 → 可读回（20:12/20:32 3a PASS = 6.5）
            //   - var !!NAME <输出命令>          → 可读回（19:51 3b unit PASS）
            //   - var !!NAME <纯算术/裸函数>     → 读不回（语句不产生显示输出）
            //   - var !!NAME <表达式引用变量>    → "(2,427) The value of the expression is undefined"
            //     （20:12 !VWTD 自引用 / 20:32 VVALUE(!VWTC) 跨命令两种情况同位报错 →
            //       疑变量不跨 RunInPdms 调用持久，3d 专项验证）
            // 课件习惯写法（PML-刘永久-课件.pdf）：!x.val = !!CE（!!VAR 作右值表达式）、
            //   !!OBJ.method(...)（!! 前缀显示式调用）、q var !!X（输出语句）
            // PML 变量命名规范：必须字母开头、≤4 字符、不含下划线（2026-10-14 实测踩坑）。
            try
            {
                Info("3a Real: !!VWTP = 3.25 * 2（显示式赋值，官方 !!SIZE = !!arr.size() 同款）");
                Command c1 = Command.CreateCommand("!!VWTP = 3.25 * 2");
                bool r1 = c1.RunInPdms();
                double v = r1 ? c1.GetPMLVariableReal("VWTP") : double.NaN;
                Try(r1 && Math.Abs(v - 6.5) < 1e-9, "GetPMLVariableReal: 3.25*2 = " + v,
                    "执行=" + r1 + " 读回=" + v + (r1 ? "" : " PDMS报错=" + CmdErr(c1)));
            }
            catch (Exception ex) { Bad("3a GetPMLVariableReal: [" + ex.GetType().Name + "] " + ex.Message); }
            try
            {
                Info("3b String: var !!VWTS unit（19:51 实测 PASS，官方 GetPMLString(\"unit\") 同款）");
                Command c2 = Command.CreateCommand("var !!VWTS unit");
                bool r2 = c2.RunInPdms();
                string s = r2 ? c2.GetPMLVariableString("VWTS") : "";
                Try(r2 && s != null && s.Length > 0, "GetPMLVariableString: unit = " + Short(s),
                    "执行=" + r2 + " 回读=\"" + (s ?? "") + "\"");
            }
            catch (Exception ex) { Bad("3b GetPMLVariableString: [" + ex.GetType().Name + "] " + ex.Message); }
            // 3c 多行命令探针 —— 2026-10-15 起不再执行：结论已三次复测锁定
            //   （10-14 20:53/21:52、10-15 11:42 全一致），但每次执行必弹
            //   "(2,751) Variable !!VWTD does not exist" 红框 → 用户要求去掉，仅留记录结论。
            Info("3c 多行探针（已停执行，记录结论 2026-10-14/15 三测一致）: 多行(\\n) 执行=False → 多行命令不可用 → RunInPdms 仅单行；多语句 PML 走 $M 宏通道（PdmsBridge）");
            // 3d 跨命令持久化探针 —— 同上停执行（每次必弹 "(2,751) Variable !!VWTA does not exist" 红框）。
            //   证据（20:53 定论）：命令1 !VWTA = 1.5 * 2 成功，命令2 !!VWTB = !!VWTA 报变量不存在
            //   → 变量不跨 RunInPdms 调用（每次调用 = 新 PML 上下文）
            Info("3d 跨命令探针（已停执行，记录结论 2026-10-14/15）: 赋值执行=True 读回执行=False (Variable !!VWTA does not exist) → 定论: 变量不跨命令 → 多语句逻辑同一条命令或走 $M 宏通道");
            try
            {
                Info("3e 对照: 纯赋值 !VWTA = 1.5 * 2 直接读回（19:33 起实测读不回，坑位留证）");
                Command c6 = Command.CreateCommand("!VWTA = 1.5 * 2");
                bool r6 = c6.RunInPdms();
                double v6 = r6 ? c6.GetPMLVariableReal("VWTA") : double.NaN;
                Info("纯赋值读回=" + v6
                   + (Math.Abs(v6 - 3.0) < 1e-9 ? "（可读回）" : "（读不回 → Real 回读必须走 !! 显示通道，见 3a）"));
            }
            catch (Exception ex) { Info("3e 纯赋值对照异常: " + ex.Message); }
        }


        // [4] DB 视图链 —— 镜像窗口「图纸生成」的已验证链（drawE3D.cs button2_Click）：
        //     WORLD 分支(:125-145):  CE=WORLD → DEPT → REGISTRY → DRWG → createDrwgDraw
        //     SHEET 分支(:185-249):  BSRF/EXFI → VIEW(VTYP/VFRCOL/VSCA/RRSF/HRSF/THPO) → IDLIST+IDLN → UPDATE all
        //     出图 world 根判定（拓扑随会话启动方式变化，19:31/19:51 双实测）：
        //       - 19:31 会话: GetElement("/*") = 设计 world（顶层 SITE 2、DEPT 0/REGI 0），
        //         出图库挂载且 /DRA 可解析 → 根 = /DRA.Owner（顶层 DEPT /DRA）
        //       - 19:51 会话: GetElement("/*") = 出图 world 本身（顶层 DEPT 5/REGI 26/DRWG 75，/DRA 不可解析）→ 根 = /*
        //       - 统一规则: /* 顶层含 DEPT → 根 = /*；否则取 /DRA.Owner；都失败 → Bad
        //       - 19:04/19:33 CreateFirst 抛 PdmsExceptionImpl 的共同根因 = 在不含出图结构的
        //         world（设计 world）下建出图元素
        //     20:12 实证：根判定 /* 生效（顶层 5 DEPT + 3 PTWLD + 1 STYLWL）；CreateFirst(DEPT)
        //       成功但 SetAttribute(NAME,"VTTEST") 抛 PdmsExceptionImpl —— 窗口从不改 DEPT 名
        //       （drawE3D.cs:133 直接用自动名发 CP），v6 起保持自动名 + 三层非致命改名探测
        private static void TestViewDbChain(bool isDesign)
        {
            Step("[4] DB 视图链 (窗口图纸生成同款: DRA world→DEPT→REGISTRY→DRWG→SHEET→VIEW)");
            DbElement world = null, drwWorld = null, draDept = null;
            DbElement dept = null, regi = null, drwg = null, sheet = null, view = null;
            string backsName = null;
            try
            {
                world = DbElement.GetElement("/*");
                if (world == null || world.IsNull) { Bad("4.0 无法取得 /* 设计 world"); return; }

                // 4.0b 出图 world 根判定（拓扑随会话变化，见方法头注释）
                bool starHasDept = false;
                try
                {
                    DbElement[] starDepts = world.Members(DbElementTypeInstance.DEPT);
                    foreach (DbElement d in starDepts)
                        if (d != null && !d.IsNull) { starHasDept = true; break; }
                }
                catch { }
                if (starHasDept)
                {
                    drwWorld = world;
                }
                else
                {
                    try
                    {
                        draDept = DbElement.GetElement("/DRA");
                        if (draDept != null && !draDept.IsNull) drwWorld = draDept.Owner;
                    }
                    catch (Exception ex) { Info("4.0b GetElement(/DRA) 异常: " + ex.Message); }
                }
                Info("4.0b /* 顶层: " + DescribeTop(world)
                   + "  |  /DRA = " + (draDept != null && !draDept.IsNull ? SafeType(draDept) : "不可解析"));
                if (drwWorld == null || drwWorld.IsNull)
                {
                    Bad("4.0b 无法确定出图 world（/* 顶层无 DEPT 且无 /DRA）—— [4] 无法继续");
                    return;
                }
                Info("4.0b 出图 world 根 = " + (drwWorld == world ? "/*（本会话主库即出图库）" : "/DRA.Owner（DRA 数据库 world）"));

                // 4.1 残留清理（幂等重跑；19:04/19:33 失败运行留过未命名残骸）：
                //     设计 world（/* 非出图 world）：顶层 0 DEPT/0 REGI 为本工程常态 → 顶层任何 DEPT/REGI 都是测试残留，全删；
                //     出图 world（drwWorld）：只删名为 VTTEST / 未命名的 DEPT/REGI（失败 CreateFirst 残骸），
                //                            正常命名结构不动（19:51 实测该 world 有 5 个真实 DEPT）。
                try
                {
                    if (drwWorld != world)
                    {
                        DbElement[] depts = world.Members(DbElementTypeInstance.DEPT);
                        foreach (DbElement d in depts)
                        {
                            if (d == null || d.IsNull) continue;
                            string dn = d.GetAsString(ATT.NAME);
                            DeleteSubtree(d);
                            Info("4.1 设计 world 顶层残留 DEPT " + (dn == null || dn == "" ? "(未命名)" : "/" + dn) + "（含子树）已清理");
                        }
                        DbElement[] oldRegis = world.Members(DbElementTypeInstance.REGISTRY);
                        foreach (DbElement r in oldRegis)
                        {
                            if (r == null || r.IsNull) continue;
                            string rn = r.GetAsString(ATT.NAME);
                            DeleteSubtree(r);
                            Info("4.1 设计 world 顶层残留 REGISTRY " + (rn == null || rn == "" ? "(未命名)" : "/" + rn) + "（含子树）已清理");
                        }
                    }
                    DbElement[] ddepts = drwWorld.Members(DbElementTypeInstance.DEPT);
                    foreach (DbElement d in ddepts)
                    {
                        if (d == null || d.IsNull) continue;
                        string dn = d.GetAsString(ATT.NAME);
                        if ("VTTEST".Equals(dn) || dn == null || dn == "")
                        {
                            DeleteSubtree(d);
                            Info("4.1 出图 world 残留 DEPT " + (dn == null || dn == "" ? "(未命名)" : "/" + dn) + "（含子树）已清理");
                        }
                    }
                    DbElement[] dregis = drwWorld.Members(DbElementTypeInstance.REGISTRY);
                    foreach (DbElement r in dregis)
                    {
                        if (r == null || r.IsNull) continue;
                        string rn = r.GetAsString(ATT.NAME);
                        if ("VTTEST".Equals(rn) || rn == null || rn == "")
                        {
                            DeleteSubtree(r);
                            Info("4.1 出图 world 残留 REGISTRY " + (rn == null || rn == "" ? "(未命名)" : "/" + rn) + "（含子树）已清理");
                        }
                    }
                }
                catch (Exception ex) { Info("4.1 残留清理异常(忽略): [" + ex.GetType().Name + "] " + ex.Message); }

                // 4.2 图纸背景预检（窗口默认 textBox8 = /DRA/MAS/BACKS/MET/A0）
                foreach (string cand in new string[] { "A0", "A1", "A3", "A4" })
                {
                    string path = "/DRA/MAS/BACKS/MET/" + cand;
                    try
                    {
                        DbElement b = DbElement.GetElement(path);
                        if (b != null && !b.IsNull) { backsName = path; break; }
                    }
                    catch { }
                }
                if (backsName == null)
                {
                    Bad("4.2 本库无 /DRA 图纸背景（A0/A1/A3/A4 均不存在）—— [4] 无法建 SHEET；属数据库内容前提，非接口问题");
                    return;
                }
                Info("4.2 图纸背景: " + backsName);

                // 4.3 树：窗口 WORLD 分支（drawE3D.cs:125-130），根 = drwWorld（4.0b 判定的出图 world）。
                //     19:04/19:33 实证：在设计 world 下 CreateFirst 出图元素必抛 PdmsExceptionImpl。
                //     窗口靠用户先在目录树选中 WORLD 再 CreateFirst；测试显式切 CE 到出图 world。
                DbElement savedCe0 = CE.Element;
                bool ceOk = false;
                try { CE.Element = drwWorld; ceOk = true; }
                catch (Exception ex) { Info("4.3 CE 切到出图 world 失败（窗口同款要求 CE=WORLD，仍继续尝试）: " + ex.Message); }
                try
                {
                    dept = drwWorld.CreateFirst(DbElementTypeInstance.DEPT);
                    _created.Add(dept);
                    // 20:12 实证：CreateFirst(DEPT) 成功，但 SetAttribute(NAME) 抛 PdmsExceptionImpl；
                    //   窗口从不改 DEPT 名（drawE3D.cs:133 直接用自动名发 CP）→ 同款：保持自动名
                    Ok("4.3a DEPT 建成（自动名 " + SafeName(dept) + "，窗口: WORLD→DEPT，窗口不改名）");
                    regi = dept.CreateFirst(DbElementTypeInstance.REGISTRY);
                    _created.Add(regi);
                    Ok("4.3b REGISTRY 建成（自动名 " + SafeName(regi) + "，窗口: DEPT→REGISTRY）");
                    drwg = regi.CreateFirst(DbElementTypeInstance.DRWG);
                    _created.Add(drwg);
                    Ok("4.3c DRWG 建成（自动名 " + SafeName(drwg) + "，窗口: REGISTRY→DRWG）");
                    // 改名探测（仅 Info 不致命）：摸清各层命名能力边界（20:12 已知 DEPT 改名抛异常）
                    ProbeRename(dept, "VTTEST", "4.3a2 DEPT");
                    ProbeRename(regi, "VTTEST", "4.3b2 REGISTRY");
                    ProbeRename(drwg, "VTTEST", "4.3c2 DRWG");
                }
                catch (Exception ex)
                {
                    Bad("4.3 树(出图 world 形态, CE切换=" + ceOk
                       + ", 已到达: DEPT=" + (dept != null) + " REGI=" + (regi != null) + " DRWG=" + (drwg != null) + "): " + ExFull(ex));
                    return;
                }
                finally
                {
                    try { CE.Element = savedCe0; } catch { }
                }

                // 4.4 SHEET：createDrwgDraw（窗口同款调用 :137；CE 上下文 = DRWG）
                DbElement savedCe1 = CE.Element;
                try
                {
                    try { CE.Element = drwg; } catch { }
                    try
                    {
                        sheet = drawTool.createDrwgDraw(drwg, backsName);
                        if (sheet == null || sheet.IsNull) { Bad("4.4 createDrwgDraw 返回空（背景定义可能不完整）"); return; }
                        _created.Add(sheet);
                        // 窗口 sheet 命名 = drwg.name + "/" + 类型 + 序号（:139，含斜杠名，窗口实测可工作）
                        string sErr = drawTool.SetAttribute(sheet, ATT.NAME, SafeName(drwg) + "/VT");
                        if (sErr != "") Info("4.4 SHEET NAME(窗口同款含斜杠名)写入警告: " + sErr);
                        Ok("4.4 SHEET 创建: " + SafeName(sheet) + "（createDrwgDraw: SHEET+LIBY+DLLB+TRLB+IDLIST，窗口同款调用）");
                    }
                    finally
                    {
                        try { CE.Element = savedCe1; } catch { }
                    }
                }
                catch (Exception ex) { Bad("4.4 SHEET: " + ExFull(ex)); return; }

                // 4.5 VIEW：窗口 SHEET 分支核心（:185-249）
                //     BSRF/EXFI（背景引用，createDrwgDraw 的参数并不真正设背景）→ VIEW → 属性 → IDLIST/IDLN
                DbElement savedCe2 = CE.Element;
                bool idnmOk = false;   // 4.5 ID 块写入结果（4.7 UPDATE all 分支用，需跨 try 块作用域）
                try
                {
                    try { CE.Element = sheet; } catch { }
                    drawTool.SetAttribute(sheet, ATT.EXFI, "10000");
                    try
                    {
                        DbElement backs = DbElement.GetElement(backsName);
                        sheet.SetAttribute(ATT.BSRF, backs);
                        sheet.Owner.SetAttribute(ATT.BSRF, backs);
                        Info("4.5a BSRF 背景引用已写（SHEET+DRWG，窗口 :188-189）");
                    }
                    catch (Exception exB) { Info("4.5a BSRF 写入异常(忽略): " + exB.Message); }

                    view = sheet.CreateFirst(DbElementTypeInstance.VIEW);
                    _created.Add(view);
                    _testView = view;   // 保活给 [6] AID 标注测试
                    drawTool.SetAttribute(view, ATT.VFRCOL, "2");
                    drawTool.SetAttribute(view, ATT.VTYP, "Universal");
                    try { view.SetAttribute(ATT.RRSF, DbElement.GetElement("/DRA/MAS/H&S/RULESETS/View1")); Info("4.5b RRSF = /DRA/MAS/H&S/RULESETS/View1"); }
                    catch { Info("4.5b 本库无 RRSF 规则集（跳过，窗口默认值）"); }
                    try { view.SetAttribute(ATT.HRSF, DbElement.GetElement("/DRA/PRJ/HRUL/GEN/Hatch-None")); Info("4.5c HRSF = /DRA/PRJ/HRUL/GEN/Hatch-None"); }
                    catch { Info("4.5c 本库无 HRSF 规则集（跳过，窗口默认值）"); }
                    view.SetAttribute(ATT.VSCA, 0.02);   // 窗口默认比例 1/50
                    view.SetAttribute(ATT.THPO, Position.Create(5000, 5000, 0));
                    view.SetAttribute(ATT.XYPS, new double[] { 500, 400 });
                    view.SetAttribute(ATT.SIZE, new double[] { 1189, 841 });
                    drawTool.UpdateModelView(view);

                    // 绘制清单：窗口 FindCurrentDrawlistUnder/FindOrCreateIdlist + IDLN 绑 view+sheet（:248-249）
                    //   20:32 实证：未切 CE 时 ID 块 SetAttribute(DbAttribute,DbElement) 抛 PdmsExceptionImpl
                    //   （BSRF/RRSF/HRSF 同重载均成功——差别在窗口整个 ID 块在 CE=IDLIST 上下文执行 :224）
                    bool idlistBound = false;
                    DbElement savedCe3 = CE.Element;
                    try
                    {
                        DbElement dllb = drawE3DTool.FindCurrentDrawlistUnder(sheet.Owner);
                        if (dllb != null && !dllb.IsNull)
                        {
                            DbElement idlist = drawE3DTool.FindOrCreateIdlist(dllb);
                            if (idlist != null && !idlist.IsNull)
                            {
                                idlistBound = true;
                                try { CE.Element = idlist; }
                                catch (Exception exCe) { Info("4.5d CE→IDLIST 切换失败（继续）: " + exCe.Message); }
                                try
                                {
                                    DbElement addentry = idlist.CreateLast(DbElementTypeInstance.ADDENTRY);
                                    addentry.SetAttribute(ATT.IDNM, world);
                                    idnmOk = true;
                                    Info("4.5d IDNM=world 写入 OK（CE=IDLIST 上下文）");
                                }
                                catch (Exception exNm)
                                {
                                    Info("4.5d IDNM=world 异常（不致命）: " + exNm.Message + " → 回退 IDNM=DRWG");
                                    try
                                    {
                                        DbElement addentry2 = idlist.CreateLast(DbElementTypeInstance.ADDENTRY);
                                        addentry2.SetAttribute(ATT.IDNM, drwg);
                                        idnmOk = true;
                                        Info("4.5d IDNM=DRWG 回退写入 OK");
                                    }
                                    catch (Exception exNm2)
                                    {
                                        // 20:53 实证: world 报 "Cannot access a graphics element from WORLD /"
                                        //   → 本会话无设计库（无 3D 模型元素）→ ID 清单必空
                                        Info("4.5d IDNM=DRWG 回退亦失败（不致命）: " + exNm2.Message
                                           + " → 本会话无可用图形元素（设计库未挂载）→ ID 清单为空，4.7 预期 abandoned");
                                    }
                                }
                                try { view.SetAttribute(ATT.IDLN, idlist); Info("4.5e VIEW.IDLN 已绑定"); }
                                catch (Exception exV) { Info("4.5e VIEW.IDLN 写入异常: " + exV.Message); }
                                try { sheet.SetAttribute(ATT.IDLN, idlist); Info("4.5f SHEET.IDLN 已绑定"); }
                                catch (Exception exS) { Info("4.5f SHEET.IDLN 写入异常: " + exS.Message); }
                            }
                        }
                    }
                    finally { try { CE.Element = savedCe3; } catch { } }
                    Ok("4.5 VIEW 创建 + 属性写入 + " + (idlistBound ? "IDLIST 已找到（绑定明细见 4.5d/e/f）" : "IDLIST 未绑定（SHEET 下无 DLLB）"));
                }
                catch (Exception ex) { Bad("4.5 VIEW: " + ExFull(ex)); view = null; }
                finally
                {
                    try { CE.Element = savedCe2; } catch { }
                }
                if (view == null || view.IsNull) return;

                // 4.6 属性回读
                try
                {
                    double vsca = view.GetDouble(ATT.VSCA);
                    Try(Math.Abs(vsca - 0.02) < 1e-9, "4.6a 读回 VSCA = " + vsca, "4.6a VSCA = " + vsca);

                    double[] rx = view.GetDoubleArray(ATT.XYPS);
                    Try(rx != null && rx.Length >= 2 && Math.Abs(rx[0] - 500) < 1e-6 && Math.Abs(rx[1] - 400) < 1e-6,
                        "4.6b 读回 XYPS = (" + (rx != null && rx.Length >= 2 ? rx[0] + "," + rx[1] : "null") + ")", "4.6b XYPS 读回错误");

                    double[] rs = view.GetDoubleArray(ATT.SIZE);
                    Try(rs != null && rs.Length >= 2 && Math.Abs(rs[0] - 1189) < 1e-6 && Math.Abs(rs[1] - 841) < 1e-6,
                        "4.6c 读回 SIZE = " + (rs != null && rs.Length >= 2 ? rs[0] + "x" + rs[1] : "null"), "4.6c SIZE 读回错误");

                    Position rp = view.GetPosition(ATT.THPO);
                    Try(rp != null && Math.Abs(rp.X - 5000) < 1e-6,
                        "4.6d 读回 THPO = (" + (rp != null ? rp.X + "," + rp.Y + "," + rp.Z : "null") + ")", "4.6d THPO 读回错误");

                    string rtyp = view.GetAsString(ATT.VTYP);
                    // 20:53 实测: 写 "Universal" 无效，读回 = Wireline（默认）—— VTYP 为创建时属性
                    //   （TTY 形式在 NEW VIEW + 属性续行设置；窗口 textBox7 同款写后同样不生效，生产出图不受影响）
                    Info("4.6e VTYP 读回 = " + rtyp
                       + ("Universal".Equals(rtyp) ? "（写入生效）" : "（写入未生效=默认值 → VTYP 疑为创建时属性；TTY 链在 NEW VIEW 续行设置，窗口 textBox7 待核）"));
                    // 4.6e2 CP 属性写探测（CE=view，裸 <attr> <value> 作用于 CE——用户 inBran 同款结构；
                    //   窗口 :133 也靠 CP <id> size ... 写属性，SetAttribute 无效时 CP 可能是正路）
                    DbElement savedCe4 = CE.Element;
                    try { CE.Element = view; } catch { }
                    try
                    {
                        Command cv = Command.CreateCommand("vtyp Universal");
                        bool rv = cv.RunInPdms();
                        string tv = view.GetAsString(ATT.VTYP);
                        Info("4.6e2 CP 'vtyp Universal': 执行=" + rv + " 读回=" + tv
                           + (rv && "Universal".Equals(tv) ? "（CP 写生效 → 属性写走 CP 通道）" : ""));
                    }
                    catch (Exception exTv) { Info("4.6e2 CP vtyp 探测异常: " + exTv.Message); }
                    finally { try { CE.Element = savedCe4; } catch { } }

                    bool idlnBound = false;
                    try { DbElement idRef = view.GetElement(ATT.IDLN); idlnBound = idRef != null && !idRef.IsNull; } catch { }
                    Try(idlnBound, "4.6f VIEW.IDLN 已绑定 IDLIST", "4.6f VIEW.IDLN 未绑定");
                }
                catch (Exception ex) { Bad("4.6 回读: " + ExFull(ex)); }

                // 4.7 UPDATE all（窗口 :1250 同款命令）
                if (!isDesign)
                {
                    bool upd = drawTool.CommandExcute("UPDATE all");
                    if (idnmOk)
                        Try(upd, "4.7 UPDATE all 成功（ID 清单非空，窗口同款命令）", "4.7 UPDATE all 执行失败（ID 清单非空）");
                    else
                        // 20:53 实证: ID 清单空（本会话无设计库）→ "Generation of Design graphics abandoned"
                        Info("4.7 UPDATE all = " + upd
                           + "（ID 清单为空/本会话无模型元素 → abandoned 为预期行为，不计失败；有设计库的会话复测真出图）");
                }
                else Info("4.7 Design 模块：跳过 DRA 专属命令 UPDATE all");
            }
            catch (Exception ex)
            {
                Bad("4.? DB 视图链(未被子步骤标签覆盖): " + ExFull(ex));
            }
            // 清理移交 Run() 结尾统一执行（测试树保活给 [6] AID 标注测试）
        }

        /// <summary>异常全文：类型 + 消息 + 内层链 + 首行堆栈</summary>
        private static string ExFull(Exception ex)
        {
            if (ex == null) return "(null)";
            string s = "[" + ex.GetType().Name + "] " + ex.Message;
            Exception inner = ex.InnerException;
            int depth = 0;
            while (inner != null && depth < 4)
            {
                s += " | inner[" + inner.GetType().Name + "] " + inner.Message;
                inner = inner.InnerException;
                depth++;
            }
            if (ex.StackTrace != null)
            {
                string st = ex.StackTrace.Split('\n')[0].Trim();
                if (st != "") s += " @ " + st;
            }
            return s;
        }

        /// <summary>元素类型的安全名字（常见出图/设计类型映射，其余返回 CLR 类型名）</summary>
        private static string SafeType(DbElement el)
        {
            if (el == null) return "(null)";
            try
            {
                DbElementType t = el.GetElementType();
                if (t == null) return "?";
                if (t == DbElementTypeInstance.WORLD) return "WORLD";
                if (t == DbElementTypeInstance.SITE) return "SITE";
                if (t == DbElementTypeInstance.DEPT) return "DEPT";
                if (t == DbElementTypeInstance.REGISTRY) return "REGISTRY";
                if (t == DbElementTypeInstance.DRWG) return "DRWG";
                if (t == DbElementTypeInstance.SHEET) return "SHEET";
                if (t == DbElementTypeInstance.VIEW) return "VIEW";
                return t.ToString();
            }
            catch { return "?"; }
        }

        /// <summary>某 world 顶层元素概览（类型/名，最多 12 个），用于区分设计 world 与 DRA world</summary>
        private static string DescribeTop(DbElement world)
        {
            if (world == null || world.IsNull) return "(null)";
            string s = "";
            int n = 0;
            try
            {
                DbElement[] ms = world.Members();
                foreach (DbElement m in ms)
                {
                    if (m == null || m.IsNull || m.IsDeleted) continue;
                    if (n > 0) s += ", ";
                    string nm = m.GetAsString(ATT.NAME);
                    s += SafeType(m) + (nm == null || nm == "" ? "(未命名)" : "/" + nm);
                    n++;
                    if (n >= 12) { s += ", ..."; break; }
                }
            }
            catch { return "(读取异常)"; }
            return n == 0 ? "(空)" : s;
        }

        /// <summary>用简单名（不带前导斜杠）写 NAME 并回读核对。
        /// 不用 drawTool.SetAttribute：其 NAME 分支会自动补 "/" 变全路径形态，
        /// 与 TTY 实证链的简单名规范（VTTEST/A0/S1/V1）不一致。</summary>
        private static string SetSimpleName(DbElement el, string simpleName)
        {
            if (el == null || el.IsNull) return "元素为空";
            try
            {
                el.SetAttribute(ATT.NAME, simpleName);
            }
            catch (Exception ex) { return "[" + ex.GetType().Name + "] " + ex.Message; }
            try
            {
                string now = el.GetAsString(ATT.NAME);
                return simpleName.Equals(now) ? "" : "回读不一致: 期望=" + simpleName + " 实际=" + now;
            }
            catch (Exception ex) { return "回读异常: " + ex.Message; }
        }

        /// <summary>尽力改名探测（仅 Info 不致命）：给测试树起名好定位（用户要求，Draft Explorer 可见）。
        ///   SetAttribute(ATT.NAME) 对出图元素三层均抛（20:32/20:53 实证）→
        ///   候选1: CP 裸 `name /NAME`（=CE 改名，用户 inBran.pmlfrm:320 同款惯用法）
        ///   候选2: PML `!ce.name = 'NAME'`</summary>
        private static void ProbeRename(DbElement el, string name, string label)
        {
            DbElement savedCe = null;
            bool ceSet = false;
            try { savedCe = CE.Element; CE.Element = el; ceSet = true; }
            catch { }
            try
            {
                Command c1 = Command.CreateCommand("name /" + name);
                bool r1 = c1.RunInPdms();
                string now = SafeName(el);
                if (r1 && now == name) { Info(label + " 改名: 候选1 CP 'name /' 成功 → /" + name); return; }
                Info(label + " 改名候选1 CP name: 执行=" + r1 + " 读回=" + now + (r1 ? "" : " 报错=" + CmdErr(c1)));
                Command c2 = Command.CreateCommand("!ce.name = '" + name + "'");
                bool r2 = c2.RunInPdms();
                now = SafeName(el);
                Info(label + " 改名候选2 PML !ce.name: 执行=" + r2
                   + (r2 ? (now == name ? " → 成功 /" + name : " → 读回=" + now) : " 报错=" + CmdErr(c2)));
            }
            catch (Exception ex) { Info(label + " 改名探测异常（不致命）: [" + ex.GetType().Name + "] " + ex.Message); }
            finally
            {
                if (ceSet) { try { CE.Element = savedCe; } catch { } }
            }
        }

        private static string SafeName(DbElement el)
        {
            try { string n = el.GetAsString(ATT.NAME); return n != null ? n : ""; }
            catch { return "?"; }
        }

        /// <summary>递归删除子树（先子后父），单个失败忽略</summary>
        private static void DeleteSubtree(DbElement el)
        {
            if (el == null || el.IsNull || el.IsDeleted) return;
            try
            {
                DbElement[] ms = el.Members();
                for (int j = ms.Length - 1; j >= 0; j--)
                    if (ms[j] != null && !ms[j].IsNull && !ms[j].IsDeleted) DeleteSubtree(ms[j]);
            }
            catch { }
            try { el.Delete(); } catch { }
        }

        /// <summary>倒序清理测试元素（先子后父），并报告残留</summary>
        private static void CleanupChain(List<DbElement> created)
        {
            if (created == null || created.Count == 0) return;
            for (int i = created.Count - 1; i >= 0; i--)
                DeleteSubtree(created[i]);
            bool left = false;
            foreach (DbElement el in created)
                if (el != null && !el.IsNull && !el.IsDeleted) left = true;
            if (left) Info("清理不完全: /VTTEST 下可能有残留元素，请人工检查");
            else Info("清理: /VTTEST 测试树已删除");
        }

        // [4b] 用户出图链（D:\AVEVA\Everything3D2.10\PMLLIB\mytool\opgeneralmto.pmlfrm apply() 同款流程）：
        //     options default → $M/<ISODIR>/STD/<选项文件> → detail <pipe> → isodraw status → EXIT
        //     STD 选项文件 = 用户 isodraft 标准（lis 项目 4 个: BASIC.MET/ADVANCED.MET/BASIC.IMP/ADVANCED.IMP；
        //       BASIC.MET 首行即 OPTIONS DEFAULT，含 File /plot SINGLE、MessageFile /mess、Axes WORL /*、Atext 标题栏）
        //     管道来源 = 用户在 Design Explorer 预选（用户表单 .add() 也是 CE 取 name，handle any 兜底）
        //     元素引用惯用法（用户 opgeneralmto:418-422）: 自动名(=开头) → $name；普通名 → /name
        private static void TestUserIsoChain(bool isDesign)
        {
            Step("[4b] 用户出图链 (opgeneralmto 同款: $M STD + DETAIL)");
            try
            {
                // 4b.0 管道来源: CE（用户预选 PIPE/BRANCH）
                DbElement ceEl = null;
                try { ceEl = CE.Element; } catch { }
                string pipeName = "";
                bool ceIsPipe = false;
                if (ceEl != null && !ceEl.IsNull)
                {
                    try
                    {
                        DbElementType t = ceEl.GetElementType();
                        string tn = t != null ? t.ToString().ToUpper() : "";
                        if (tn.Contains("PIPE") || tn.Contains("BRANCH"))
                        {
                            pipeName = ceEl.GetAsString(ATT.NAME) ?? "";
                            ceIsPipe = pipeName != "";
                        }
                    }
                    catch { }
                }
                if (!ceIsPipe)
                {
                    Info("4b.0 CE 当前不是管道（PIPE/BRANCH）→ [4b] 跳过；请在 Design Explorer 选中一条管道后重跑（用户表单 .add() 同款取法）");
                    return;
                }
                Info("4b.0 管道: /" + pipeName);

                // 4b.1 ISODIR（用户脚本 %$:ISODIR$:% 等价：evar 回读）
                string isoDir = "";
                try
                {
                    Command cIso = Command.CreateCommand("var !!VWTS evar ISODIR");
                    if (cIso.RunInPdms()) isoDir = (cIso.GetPMLVariableString("VWTS") ?? "").Trim();
                }
                catch { }
                Info("4b.1 ISODIR = " + (isoDir == "" ? "(evar 为空)" : isoDir));

                // 4b.2 STD 选项文件（用户 isdOptFileList 枚举 <ISODIR>/STD）
                string stdFile = "";
                try
                {
                    if (isoDir != "")
                    {
                        string stdDir = Path.Combine(isoDir, "STD");
                        if (Directory.Exists(stdDir))
                        {
                            string[] files = Directory.GetFiles(stdDir);
                            string joined = "";
                            foreach (string f in files) joined += (joined == "" ? "" : ", ") + Path.GetFileName(f);
                            Info("4b.2 <ISODIR>/STD 选项文件: " + (joined == "" ? "(空)" : joined));
                            foreach (string f in files) if (f.EndsWith("BASIC.MET", StringComparison.OrdinalIgnoreCase)) { stdFile = f; break; }
                            if (stdFile == "") foreach (string f in files) if (f.EndsWith(".MET", StringComparison.OrdinalIgnoreCase)) { stdFile = f; break; }
                            if (stdFile == "" && files.Length > 0) stdFile = files[0];
                        }
                        else Info("4b.2 STD 目录不存在: " + Path.Combine(isoDir, "STD"));
                    }
                }
                catch (Exception ex) { Info("4b.2 STD 枚举异常: " + ex.Message); }

                // 4b.3 ISODRAFTMODE（TTY 出图链同款步骤；DRAW 会话可能不支持 → 不致命，继续按用户裸流程）
                bool isoMode = RunSimple("4b.3", "ISODRAFTMODE");
                if (!isoMode) Info("4b.3 ISODRAFTMODE 失败 → 继续裸流程（options default + $M STD，用户脚本同款）");

                // 4b.4 options default + 用户 STD 宏（STD 文件首行即 OPTIONS DEFAULT，用户 apply():188-189 同款）
                RunSimple("4b.4", "options default");
                if (stdFile != "")
                    RunSimple("4b.4b", "$M/" + stdFile);
                else
                    Info("4b.4b 无 STD 选项文件 → 跳过 $M（仅 options default）");

                // 4b.5 detail（用户: detail $!pipe；= 前缀自动名用 $，普通名用 / —— 用户 :418-422 惯用法）
                string idRef = pipeName.StartsWith("=") ? "$" + pipeName : "/" + pipeName;
                bool rDet = RunSimple("4b.5", "detail " + idRef);
                if (!rDet)
                    rDet = RunSimple("4b.5b", "detail pipe " + idRef + "（TTY 链同款回退）");
                Try(rDet, "4b.5 detail " + idRef + " 执行成功", "4b.5 detail 失败（看红框 (33:xxx) 错误码定位；用户脚本按 message 文件 33:160/33:157 判未出管线）");

                // 4b.6 isodraw status（TTY 链同款核验: status=OK）
                RunSimple("4b.6", "isodraw status");
                Info("4b.6b 产物核查: <ISODIR>/und 与 %PDMSUSER% 下 plt/dxf、MessageFile（/mess 或 D:\\ISO\\mess，用户 apply():289）");

                // 4b.7 EXIT（仅当 ISODRAFTMODE 成功进入过；TTY 链必备收尾）
                if (isoMode) RunSimple("4b.7", "EXIT");
            }
            catch (Exception ex) { Bad("[4b] 用户出图链异常: " + ExFull(ex)); }
        }

        /// <summary>执行单行 CP/PML 命令 + Info 留痕（非致命，返回是否成功）</summary>
        private static bool RunSimple(string label, string cmd)
        {
            Info(label + " cmd: " + Short(cmd));
            try
            {
                Command c = Command.CreateCommand(cmd);
                bool r = c.RunInPdms();
                Info(label + " = " + (r ? "OK" : "失败: " + CmdErr(c)));
                return r;
            }
            catch (Exception ex) { Info(label + " 异常: " + ex.Message); return false; }
        }

        // [5] DrawList 层
        private static void TestDrawList(bool isDesign)
        {
            Step("[5] DrawList (DrawListManager)");
            try
            {
                DrawListManager mgr = DrawListManager.Instance;
                Try(mgr != null, "DrawListManager.Instance 可用", "DrawListManager.Instance = null");
                if (mgr == null) return;

                DrawList dl = mgr.CurrentDrawList;
                if (dl != null)
                {
                    DrawListMember[] members = dl.Members();
                    Ok("CurrentDrawList 成员数 = " + (members != null ? members.Length : 0)
                       + (isDesign ? "（Design 会话当前清单）" : "（DRAW 当前图纸视图）"));
                }
                else
                {
                    Info("CurrentDrawList = null（无活动视图，Design 模块下属正常）");
                }

                // 探针：按 view id 取绘制清单（不用 PML，直接试 getViewDrawList(1..3)）
                bool found = false;
                for (int i = 1; i <= 3 && !found; i++)
                {
                    try
                    {
                        DrawList vdl = mgr.getViewDrawList(i);
                        if (vdl != null)
                        {
                            DrawListMember[] vm = vdl.Members();
                            Ok("getViewDrawList(" + i + ") 成员数 = " + (vm != null ? vm.Length : 0));
                            found = true;
                        }
                    }
                    catch (Exception ex) { Info("getViewDrawList(" + i + "): " + ex.Message); }
                }
                if (!found) Info("getViewDrawList(1..3) 均无（当前会话无关联视图 id 的绘制清单）");
            }
            catch (Exception ex) { Bad("DrawList: " + ex.Message); }
        }

        // [6] AID 标注 + SheetPosition 投影（直接调用原厂 FangYuan.PDMS.Aid / FangYuan.PDMS.Draft）
        //     原厂代码: D:\pdmsAgent\decompiled\FangYuanPDMS_old_csharp\FangYuan.PDMS\{Aid,Draft}.cs（221KB 未混淆版）
        //     项目已有直接调用先例: drawTool.cs:628 Draft.CreateViewNote（生产可用）。
        //     期望值 = 原厂公式手推（[4] 视图: VSCA=0.02, XYPS=(500,400), THPO=(5000,5000,0), SIZE=(1189,841)）：
        //       DIR=d ADEG=0:  +E1000→(520,400) +N1000→(500,420) +W1000→(480,400) +S1000→(500,380) +U1000→(500,400)
        //       ADEG=90:  +E1000→(500,420) +N1000→(480,400)
        //       ADEG=180: +E1000→(480,400) +N1000→(500,380)
        //       ADEG=270: +E1000→(500,380) +N1000→(520,400)
        //     原厂 DIR≠d 分支两个疑似怪癖（竖式视图），仅取证不计 PASS/FAIL：
        //       a) Y 不加 XYPS[1] 居中（俯视分支 X/Y 都加）
        //       b) X = XYPS[0] ± 世界mm 距离，未乘 VSCA（手推 +N1000 → X=500-1000=-500 而非 480）
        private static void TestAidAnnotations(bool isDesign)
        {
            Step("[6] AID 标注 + SheetPosition 投影 (原厂 FangYuan.PDMS.Aid/Draft 直调)");
            if (_testView == null || _testView.IsNull || _testView.IsDeleted)
            {
                Info("跳过: [4] 未建成测试视图（前提链缺失）");
                return;
            }
            DbElement view = _testView;
            Position thpo;
            try { thpo = view.GetPosition(ATT.THPO); }
            catch (Exception ex) { Bad("6.0 THPO 读取: " + ExFull(ex)); return; }
            if (thpo == null) { Bad("6.0 THPO = null"); return; }

            // 6.0 置俯视图 + ADEG=0（确定性矩阵前提），回读确认
            try
            {
                view.SetAttribute(ATT.DIR, Direction.Create("d"));
                view.SetAttribute(ATT.ADEG, 0.0);
                Direction rd = view.GetDirection(ATT.DIR);
                double ra = view.GetDouble(ATT.ADEG);
                Try(rd != null && rd.ToString().ToLower() == "d" && Math.Abs(ra) < 1e-9,
                    "6.0 DIR=d / ADEG=0 写入并回读确认 (DIR=" + rd + " ADEG=" + ra + ")",
                    "6.0 DIR/ADeg 回读不符 (DIR=" + rd + " ADEG=" + ra + ")");
            }
            catch (Exception ex) { Bad("6.0 DIR/ADEG 写入: " + ExFull(ex)); return; }

            // 6.1-6.4 SheetPosition 俯视矩阵（原厂公式 vs 手推期望值）
            CheckSheet(view, thpo, 0, 0, 0, 0.0, 500, 400, "6.1 中心 THPO");
            CheckSheet(view, thpo, 1000, 0, 0, 0.0, 520, 400, "6.1 +E1000");
            CheckSheet(view, thpo, 0, 1000, 0, 0.0, 500, 420, "6.1 +N1000");
            CheckSheet(view, thpo, -1000, 0, 0, 0.0, 480, 400, "6.1 +W1000");
            CheckSheet(view, thpo, 0, -1000, 0, 0.0, 500, 380, "6.1 +S1000");
            CheckSheet(view, thpo, 0, 0, 1000, 0.0, 500, 400, "6.1 +U1000 (俯视图 z 忽略)");
            try { view.SetAttribute(ATT.ADEG, 90.0); } catch { }
            CheckSheet(view, thpo, 1000, 0, 0, 90.0, 500, 420, "6.2 ADEG=90 +E1000");
            CheckSheet(view, thpo, 0, 1000, 0, 90.0, 480, 400, "6.2 ADEG=90 +N1000");
            try { view.SetAttribute(ATT.ADEG, 180.0); } catch { }
            CheckSheet(view, thpo, 1000, 0, 0, 180.0, 480, 400, "6.3 ADEG=180 +E1000");
            CheckSheet(view, thpo, 0, 1000, 0, 180.0, 500, 380, "6.3 ADEG=180 +N1000");
            try { view.SetAttribute(ATT.ADEG, 270.0); } catch { }
            CheckSheet(view, thpo, 1000, 0, 0, 270.0, 500, 380, "6.4 ADEG=270 +E1000");
            CheckSheet(view, thpo, 0, 1000, 0, 270.0, 520, 400, "6.4 ADEG=270 +N1000");
            try { view.SetAttribute(ATT.ADEG, 0.0); } catch { }

            // 6.5 竖式分支取证（DIR=w，原厂公式手推: THPO→(500,0)、+N1000→(-500,0)）
            try
            {
                view.SetAttribute(ATT.DIR, Direction.Create("w"));
                Position2D sp0 = FangYuan.PDMS.Draft.SheetPosition(thpo, view);
                Position2D sp1 = FangYuan.PDMS.Draft.SheetPosition(
                    Position.Create(thpo.X, thpo.Y + 1000, thpo.Z), view);
                Info("6.5 竖式(DIR=w) 取证: THPO→(" + (sp0 != null ? sp0.X + "," + sp0.Y : "null")
                    + ")  +N1000→(" + (sp1 != null ? sp1.X + "," + sp1.Y : "null")
                    + ")  [手推原厂公式 (500,0)/(-500,0)；Y 未居中 + X 未按 VSCA 缩放 → 竖式视图标注定位不可直接沿用]");
            }
            catch (Exception ex) { Info("6.5 竖式取证异常(不影响俯视结论): [" + ex.GetType().Name + "] " + ex.Message); }
            try { view.SetAttribute(ATT.DIR, Direction.Create("d")); } catch { }

            // 6.6 AID 命令冒烟（CE=view；命令全文入日志；失败带 PDMS 报错原文）
            DbElement savedCe = null;
            bool ceSet = false;
            try { savedCe = CE.Element; CE.Element = view; ceSet = true; }
            catch (Exception ex) { Info("6.6 CE 切到测试视图失败(继续尝试): " + ex.Message); }
            try
            {
                Info("6.6a 基线 view 成员: " + DescribeMembers(view));
                RunAid("6.6b aid line",
                    "aid line numb 100 " + thpo.ToString()
                    + " to " + Position.Create(thpo.X + 500, thpo.Y, thpo.Z).ToString() + " LINEStyle SOLId");
                RunAid("6.6c aid text",
                    "aid text numb 100 'VT AID TEXT' at "
                    + Position.Create(thpo.X, thpo.Y + 300, thpo.Z).ToString());
                RunAid("6.6d aid SPHERE",
                    "aid SPHERE numb 100 "
                    + Position.Create(thpo.X - 300, thpo.Y, thpo.Z).ToString() + " DIAmeter 50");
                RunAid("6.6e aid arrow",
                    "aid arrow numb 100 at "
                    + Position.Create(thpo.X, thpo.Y - 300, thpo.Z).ToString() + " direction e height 50 prop 0.5");
                // 6.6f aid Cylinder：20:53 原厂原样（PascalCase "Cylinder"）报 "(47,15) CP: Syntax error"
                //   成功项: line/text/arrow（全小写）、SPHERE（全大写）→ CP 关键字大小写敏感，混合大小写无效
                string cylPos = Position.Create(thpo.X, thpo.Y, thpo.Z + 300).ToString();
                bool cylOk = TryAid("6.6f1 aid cylinder（小写）",
                    "aid cylinder numb 100 at " + cylPos + " orientation u diameter 30 height 80 fill 0");
                if (!cylOk)
                    TryAid("6.6f2 aid CYLINDER（全大写）",
                        "aid CYLINDER numb 100 at " + cylPos + " orientation u diameter 30 height 80 fill 0");
                // 原厂 void 包装交叉验证（命令格式与 6.6b-6.6f 同源，验证类加载与行为）
                try
                {
                    FangYuan.PDMS.Aid.AidText("101", Position.Create(thpo.X + 300, thpo.Y + 300, thpo.Z), "VT AID 2");
                    Ok("6.6g 原厂 Aid.AidText(101, ...) void 调用无异常");
                }
                catch (Exception ex) { Info("6.6g 原厂 Aid.AidText 异常: " + ex.Message); }

                if (!isDesign)
                {
                    bool upd = drawTool.CommandExcute("UPDATE all");
                    Info("6.6h UPDATE all = " + upd + "（aid 对象更新后入成员）");
                }
                Info("6.6i 创建后 view 成员: " + DescribeMembers(view));

                // 6.7 清除验证
                RunAid("6.7a aid clear all", "aid clear all");
                if (!isDesign) drawTool.CommandExcute("UPDATE all");
                Info("6.7b 清除后 view 成员: " + DescribeMembers(view));

                // 6.8 原厂 ClearMark 三连（unmark all + unenhance all + aid clear all）
                try
                {
                    FangYuan.PDMS.Aid.ClearMark();
                    Ok("6.8 原厂 Aid.ClearMark() 无异常执行");
                }
                catch (Exception ex) { Info("6.8 ClearMark 异常(非致命): " + ex.Message); }
            }
            finally
            {
                if (ceSet) { try { CE.Element = savedCe; } catch { } }
            }
        }

        /// <summary>SheetPosition 单点核对：THPO+偏移 的图纸坐标 vs 手推期望值（容差 0.01mm）</summary>
        private static void CheckSheet(DbElement view, Position thpo, double dx, double dy, double dz,
            double adeg, double expX, double expY, string label)
        {
            try
            {
                Position p = Position.Create(thpo.X + dx, thpo.Y + dy, thpo.Z + dz);
                Position2D sp = FangYuan.PDMS.Draft.SheetPosition(p, view);
                bool ok = sp != null && Math.Abs(sp.X - expX) < 0.01 && Math.Abs(sp.Y - expY) < 0.01;
                Try(ok, label + " → (" + (sp != null ? sp.X.ToString("0.##") + "," + sp.Y.ToString("0.##") : "null") + ")",
                    label + " 实际=(" + (sp != null ? sp.X + "," + sp.Y : "null") + ") 期望=(" + expX + "," + expY + ") ADEG=" + adeg);
            }
            catch (Exception ex) { Bad(label + ": [" + ex.GetType().Name + "] " + ex.Message); }
        }

        /// <summary>执行一条 CP 命令并带错误捕获（命令全文入日志）</summary>
        private static void RunAid(string label, string cmd)
        {
            Info(label + " cmd: " + Short(cmd));
            try
            {
                Command c = Command.CreateCommand(cmd);
                if (c.RunInPdms()) Ok(label + " RunInPdms=true");
                else Bad(label + " RunInPdms=false: " + CmdErr(c));
            }
            catch (Exception ex) { Bad(label + " 异常: " + ExFull(ex)); }
        }

        /// <summary>同 RunAid 但返回是否成功（用于候选命令序贯探测）</summary>
        private static bool TryAid(string label, string cmd)
        {
            Info(label + " cmd: " + Short(cmd));
            try
            {
                Command c = Command.CreateCommand(cmd);
                bool r = c.RunInPdms();
                if (r) Ok(label + " RunInPdms=true");
                else { Bad(label + " RunInPdms=false: " + CmdErr(c)); return false; }
                return true;
            }
            catch (Exception ex) { Bad(label + " 异常: " + ExFull(ex)); return false; }
        }

        /// <summary>view 成员概览（类型/名，最多 15 个），用于 AID 对象落点取证</summary>
        private static string DescribeMembers(DbElement view)
        {
            try
            {
                DbElement[] ms = view.Members();
                if (ms == null || ms.Length == 0) return "(0)";
                string s = "";
                int n = 0;
                foreach (DbElement m in ms)
                {
                    if (m == null || m.IsNull || m.IsDeleted) continue;
                    string nm = m.GetAsString(ATT.NAME);
                    if (n > 0) s += ", ";
                    s += SafeType(m) + (nm == null || nm == "" ? "" : "/" + nm);
                    n++;
                    if (n >= 15) { s += ", ..."; break; }
                }
                return "(" + ms.Length + ") " + s;
            }
            catch { return "(读取异常)"; }
        }

        // [7] 管道程序化创建/连接全套（直调原厂 FangYuan.PDMS.Pipe / Paragon / Utilities）
        //     原厂源码: D:\pdmsAgent\decompiled\FangYuanPDMS_old_csharp\FangYuan.PDMS\{Pipe,Paragon,Utilities}.cs
        //     分段: 7.0 上下文预检(unit/mode/规格/区) → 7.1 Paragon 数据源(只读) → 7.2 收集工具(只读)
        //           → 7.3 写段(PipeCreate→PipeNamed→BranchCreate→FittingCreateForward×2→ConnectLeave→Slope)
        //     注: 纯 DB API 部分不依赖 CE 模块上下文; CP 命令(For/mode/coll)在 DRAW 会话下可能失效——
        //         原厂代码对 CP 失败均忽略返回值, 测试照原样暴露行为。写段元素进 _created, Run 结尾清理。
        private static void TestPipeTooling()
        {
            Step("[7] 管道程序化创建/连接 (原厂 FangYuan.PDMS.Pipe/Paragon/Utilities 直调)");
            bool metric = true;
            DbElement spec = null, zone = null;
            List<DbElement> projs = null;

            // 7.0 上下文预检
            try
            {
                string unit = FangYuan.PDMS.Utilities.GetPMLString("unit");
                Info("7.0a GetPMLString(\"unit\") = " + Short(unit));
                metric = unit.IndexOf("INCH Distance") < 0;
                Try(metric, "7.0b 公制工程 (DistanceIsMetric)", "7.0b 英制工程 (bore 需 NominalBore 换算)");
            }
            catch (Exception ex) { Info("7.0a unit 预检异常: " + ex.Message); }
            try
            {
                string mode = FangYuan.PDMS.Pipe.GetMode();
                Info("7.0c GetMode() = '" + mode + "' (var !!MODE mode; 管连接模式, DRAW 会话预期为空)");
            }
            catch (Exception ex) { Info("7.0c GetMode 异常: " + ex.Message); }
            try
            {
                List<DbElement> cats = FangYuan.PDMS.Utilities.MDBMembers(Aveva.Pdms.Database.DbType.Catalog);
                Info("7.0d MDBMembers(Catalog) = " + cats.Count + " 个库");
                TypeFilter specFilt = new TypeFilter(DbElementTypeInstance.SPECIFICATION);
                int checkedSpecs = 0;
                bool found = false;
                foreach (DbElement item in cats)
                {
                    if (found) break;
                    if (item == null || item.IsNull || item.IsDeleted) continue;
                    try
                    {
                        foreach (DbElement s in new DBElementCollection(item, specFilt))
                        {
                            if (s == null || s.IsNull) continue;
                            checkedSpecs++;
                            DbElement txt = s.FirstMember(DbElementTypeInstance.TEXT);
                            if (txt != null && !txt.IsNull && txt.IsValid
                                && txt.GetAsString(ATT.STEX) != null
                                && txt.GetAsString(ATT.STEX).IndexOf("PIPING") >= 0)
                            { spec = s; found = true; break; }
                        }
                    }
                    catch { }
                }
                if (spec == null || spec.IsNull) Info("7.0d 未找到 PIPING 规格 (检查了 " + checkedSpecs + " 个 SPECIFICATION), 写段跳过");
                else Ok("7.0d Spec = " + SafeName(spec) + " (扫 " + checkedSpecs + " 个规格)");
            }
            catch (Exception ex) { Info("7.0d 规格发现异常: " + ex.Message); }
            try
            {
                projs = FangYuan.PDMS.Utilities.MDBMembers(Aveva.Pdms.Database.DbType.Design);
                Info("7.0e MDBMembers(Design) = " + projs.Count + " 个设计 world 顶层元素");
                foreach (DbElement item in projs)
                {
                    if (item != null && !item.IsNull && !item.IsDeleted && item.GetElementType() == DbElementTypeInstance.ZONE)
                    { zone = item; break; }
                }
                if (zone == null) Info("7.0e 未找到 ZONE, 写段跳过");
                else Ok("7.0e Zone = " + SafeName(zone));
            }
            catch (Exception ex) { Info("7.0e 区发现异常: " + ex.Message); }

            // 7.1 Paragon 数据源（只读）
            double bore = 0;
            DbElement elbowSpref = null;
            if (spec != null && !spec.IsNull)
            {
                try
                {
                    System.Collections.SortedList bores = FangYuan.PDMS.Paragon.SpecBoreSortedList(spec, metric);
                    string s = "";
                    int n = 0;
                    foreach (System.Collections.DictionaryEntry de in bores) { if (n++ < 8) s += de.Value + " "; }
                    Try(bores.Count > 0, "7.1a SpecBoreSortedList = " + bores.Count + " 档 bore: " + s, "7.1a 规格无 bore 数据");
                    if (bores.Count > 0)
                    {
                        System.Collections.IEnumerator en = bores.Values.GetEnumerator();
                        if (en.MoveNext()) bore = (double)en.Current;
                    }
                }
                catch (Exception ex) { Info("7.1a 异常: " + ex.Message); }
                try
                {
                    System.Data.DataTable bt = FangYuan.PDMS.Paragon.SpecBoreTable(spec, metric);
                    Try(bt != null && bt.Rows.Count > 0, "7.1b SpecBoreTable 行 = " + (bt != null ? bt.Rows.Count : 0), "7.1b 表为空");
                }
                catch (Exception ex) { Info("7.1b 异常: " + ex.Message); }
                try
                {
                    System.Collections.Generic.Dictionary<string, string> cocos = FangYuan.PDMS.Paragon.CoCoTable();
                    string s = "";
                    int n = 0;
                    foreach (System.Collections.Generic.KeyValuePair<string, string> kv in cocos)
                    { if (n++ < 3) s += kv.Key + "→" + kv.Value + "; "; }
                    Try(cocos.Count > 0, "7.1c CoCoTable = " + cocos.Count + " 条, 例: " + s, "7.1c CoCo 表为空 (连接规则缺失, Connect 将不校验)");
                }
                catch (Exception ex) { Info("7.1c 异常: " + ex.Message); }
                // 7.1d 管件规格表 + 7.1e leave tube 匹配
                try
                {
                    System.Data.DataTable ft = FangYuan.PDMS.Paragon.SpecFittingTable(SafeName(spec), "ELBOW", metric);
                    Info("7.1d SpecFittingTable(ELBOW) 行 = " + (ft != null ? ft.Rows.Count : 0) + " (列: Skey/Type/Size/Stype/Bore1-3/Spref...)");
                    DbElement tubeSpref = FindSprefUnderSelec(spec, "TUBE", null);
                    if (tubeSpref != null && !tubeSpref.IsNull && bore > 0)
                    {
                        DbElement matched = FangYuan.PDMS.Paragon.SpecLeaveTube(bore, tubeSpref);
                        Try(matched != null && !matched.IsNull,
                            "7.1e SpecLeaveTube(" + bore + ") = " + SafeName(matched) + (matched == tubeSpref ? " (与种子一致)" : " (换档)"),
                            "7.1e SpecLeaveTube 未匹配到 leave tube");
                    }
                    else Info("7.1e 无 TUBE spref 种子, 跳过 SpecLeaveTube");
                    elbowSpref = FindSprefUnderSelec(spec, "ELBOW", bore > 0 ? (double?)bore : null);
                    if (elbowSpref == null || elbowSpref.IsNull) elbowSpref = FindSprefUnderSelec(spec, "ELBOW", null);
                    if (elbowSpref != null && !elbowSpref.IsNull)
                        Info("7.1f ELBOW spref 候选 = " + SafeName(elbowSpref) + (bore > 0 ? " (按 PBOR 匹配 bore=" + bore + ")" : " (无 bore 匹配, 取首个)"));
                }
                catch (Exception ex) { Info("7.1d 异常: " + ex.Message); }
            }
            else Info("7.1 跳过 (无规格)");

            // 7.2 收集工具（只读, 样例管 /2001）
            DbElement samplePipe = null;
            if (projs != null)
            {
                foreach (DbElement item in projs)
                {
                    if (item != null && !item.IsNull && SafeName(item) == "2001") { samplePipe = item; break; }
                }
            }
            if (samplePipe == null)
            {
                Info("7.2 未找到样例管 2001, 跳过收集验证");
            }
            else
            {
                Ok("7.2a 样例管 = /2001 type=" + SafeType(samplePipe));
                try
                {
                    double[] wvol = samplePipe.GetDoubleArray(ATT.WVOL);
                    List<string> tubes = FangYuan.PDMS.Utilities.CollectElementsWithin("TUBING", wvol);
                    Try(tubes.Count > 0, "7.2b CollectElementsWithin(TUBING, WVOL) = " + tubes.Count + " 条, 首: " + (tubes.Count > 0 ? tubes[0] : ""), "7.2b 未收集到 TUBING");
                }
                catch (Exception ex) { Info("7.2b 异常: " + ex.Message); }
                try
                {
                    List<string> forRes = FangYuan.PDMS.Utilities.CollectElementsFor("TUBING", samplePipe);
                    Info("7.2c CollectElementsFor(TUBING, /2001) = " + forRes.Count + " 条 (coll all ... for 层级收集)");
                }
                catch (Exception ex) { Info("7.2c 异常: " + ex.Message); }
            }

            // 7.3 写段（spec+zone+bore 齐备才跑; 元素进 _created 由 Run 结尾清理）
            if (spec == null || spec.IsNull || zone == null || zone.IsNull || bore <= 0)
            {
                Info("7.3 写段跳过 (spec/zone/bore 未齐备)");
                return;
            }
            DbElement pipe = DbElement.GetElement();
            try
            {
                string specPath = SafeName(spec);
                DbElement resolved = DbElement.GetElement();
                try { resolved = DbElement.GetElement(specPath); } catch { }
                if (resolved.IsValid)
                {
                    pipe = FangYuan.PDMS.Pipe.PipeCreateAndUpdate(zone, null, specPath, bore, "");
                    Info("7.3a PipeCreateAndUpdate(规格名解析 OK)");
                }
                else
                {
                    // 规格名在当前 CE 上下文解析不到 → 走纯元素引用通道
                    pipe = FangYuan.PDMS.Pipe.PipeCreate(zone, "", spec, null, null);
                    if (pipe != null && !pipe.IsNull) pipe.SetAttribute(ATT.BORE, bore);
                    Info("7.3a 规格名不可解析, 改用 PipeCreate(zone, pspec 元素) + 手写 BORE");
                }
                Try(pipe != null && !pipe.IsNull && !pipe.IsDeleted,
                    "7.3a PIPE 建成 = " + SafeName(pipe) + " BORE=" + (pipe != null && !pipe.IsNull ? pipe.GetDouble(ATT.BORE) : 0) + " PSPE=" + (pipe != null && !pipe.IsNull ? SafeName(pipe.GetElement(ATT.PSPE)) : "?"),
                    "7.3a PIPE 创建失败");
                if (pipe == null || pipe.IsNull) return;
                _created.Add(pipe);
            }
            catch (Exception ex) { Bad("7.3a PipeCreate 异常: " + ExFull(ex)); return; }

            try
            {
                string named = FangYuan.PDMS.Pipe.PipeNamed(pipe, "VTP", "N", 3, "", false);
                string nowName = SafeName(pipe);
                Try(nowName.StartsWith("VTP"), "7.3b PipeNamed → " + nowName + " (规则返回 '" + named + "')", "7.3b 命名未生效: " + nowName);
            }
            catch (Exception ex) { Info("7.3b PipeNamed 异常(非致命): " + ex.Message); }

            DbElement branch = DbElement.GetElement();
            try
            {
                branch = FangYuan.PDMS.Pipe.BranchCreate(pipe, 0);
                Try(branch != null && !branch.IsNull,
                    "7.3c BranchCreate → " + SafeName(branch) + " HBOR=" + (branch != null && !branch.IsNull ? branch.GetDouble(ATT.HBOR) : 0) + " HCON/TCON=OPEN",
                    "7.3c BRANCH 创建失败");
                if (branch == null || branch.IsNull) return;
            }
            catch (Exception ex) { Bad("7.3c BranchCreate 异常: " + ExFull(ex)); return; }

            if (elbowSpref == null || elbowSpref.IsNull)
            {
                Info("7.3d 无可用 ELBOW spref, 管件/连接/坡管段跳过");
                return;
            }
            DbElement elbow1 = DbElement.GetElement();
            try
            {
                elbow1 = FangYuan.PDMS.Pipe.FittingCreateForward(branch, DbElementTypeInstance.ELBOW, elbowSpref, Position.Create());
                Try(elbow1 != null && !elbow1.IsNull,
                    "7.3d FittingCreateForward(ELBOW@头) → " + SafeName(elbow1) + " (CreateFirst+SPRE+POSI/ORIL+FittingSetSpecAttributes+Connect)",
                    "7.3d 弯头创建失败");
            }
            catch (Exception ex) { Info("7.3d 异常(可能 LSTU 链问题): " + ExFull(ex)); }

            if (elbow1 != null && !elbow1.IsNull)
            {
                DbElement elbow2 = DbElement.GetElement();
                try
                {
                    elbow2 = FangYuan.PDMS.Pipe.FittingCreateForward(elbow1, DbElementTypeInstance.ELBOW, elbowSpref, Position.Create());
                    Try(elbow2 != null && !elbow2.IsNull, "7.3e FittingCreateForward(ELBOW@中) → " + SafeName(elbow2), "7.3e 第二弯头创建失败");
                }
                catch (Exception ex) { Info("7.3e 异常: " + ex.Message); }

                if (elbow2 != null && !elbow2.IsNull)
                {
                    try
                    {
                        Position pBefore = elbow1.GetPosition(ATT.POS);
                        FangYuan.PDMS.Pipe.ConnectLeave(elbow1);
                        Position pAfter = elbow1.GetPosition(ATT.POS);
                        Info("7.3f ConnectLeave(elbow1): POS " + pBefore + " → " + pAfter + " (LPOS 对位下一管件 APOS)");
                    }
                    catch (Exception ex) { Info("7.3f 异常: " + ex.Message); }
                }
                try
                {
                    // HDIR 确保已设(新建分支 API 可能未定向), 否则 Slope 内部 Split 会 NRE
                    try { branch.SetAttribute(ATT.HDIR, Direction.Create("e")); } catch { }
                    string hdirBefore = branch.GetAsString(ATT.HDIR);
                    FangYuan.PDMS.Pipe.Slope(branch, true, Direction.Create("n"), 5.0);
                    string hdirAfter = branch.GetAsString(ATT.HDIR);
                    Try(hdirBefore != hdirAfter,
                        "7.3g Slope(向前, 向 n 偏 5°): HDIR " + hdirBefore + " → " + hdirAfter,
                        "7.3g HDIR 未变化 (" + hdirBefore + "); For/mode CP 命令在 DRAW 会话下可能失效");
                }
                catch (Exception ex) { Info("7.3g Slope 异常: " + ExFull(ex)); }
            }

            // 7.3h 测试管树取证（清理前快照）
            try
            {
                Info("7.3h 测试管树: " + SafeName(pipe));
                DbElement[] brs = pipe.Members(DbElementTypeInstance.BRANCH);
                if (brs.Length > 0)
                {
                    foreach (DbElement m in brs[0].Members())
                        Info("  ├ " + SafeType(m) + " " + SafeName(m) + " LBOR/TBOR=" + SafeDoubles(m, ATT.LBOR) + "/" + SafeDoubles(m, ATT.TBOR));
                }
                Info("7.3i (写段元素已进清理队列, Run 结尾 DeleteSubtree)");
            }
            catch (Exception ex) { Info("7.3h 取证异常: " + ex.Message); }
        }

        /// <summary>取元素某 double 属性的安全文本</summary>
        private static string SafeDoubles(DbElement el, DbAttribute att)
        {
            try { return el.GetDouble(att).ToString("0.##"); } catch { return "?"; }
        }

        /// <summary>在规格 gType SELEC 下找 SPCOMPONENT（可选按 PBOR/QUAL=0/ANSW==bore 过滤, 规则同 Paragon.SpecBore）</summary>
        private static DbElement FindSprefUnderSelec(DbElement spec, string gType, double? bore)
        {
            DbElement none = DbElement.GetElement();
            try
            {
                DbElement selec = spec.FirstMember(DbElementTypeInstance.SELEC);
                while (selec != null && !selec.IsNull && !selec.IsDeleted)
                {
                    string tans = "";
                    try { tans = selec.GetAsString(ATT.TANS); } catch { }
                    if (tans != null && tans.ToUpper() == gType.ToUpper()) break;
                    selec = selec.Next(DbElementTypeInstance.SELEC);
                }
                if (selec == null || selec.IsNull) return none;
                TypeFilter tf = new TypeFilter(DbElementTypeInstance.SPCOMPONENT);
                foreach (DbElement item in new DBElementCollection(selec, tf))
                {
                    if (item == null || item.IsNull) continue;
                    if (!bore.HasValue) return item;
                    int depth;
                    try { depth = item.GetInteger(ATT.DDEP) - 2; } catch { continue; }
                    DbElement cur = item;
                    bool match = false;
                    for (int i = 0; i < depth; i++)
                    {
                        try
                        {
                            double ans = cur.GetDouble(ATT.ANSW);
                            cur = cur.Owner;
                            if (cur == null || cur.IsNull) break;
                            string ques = cur.GetAsString(ATT.QUES);
                            if (ques == "PBOR" && cur.GetInteger(ATT.QUAL) == 0
                                && Math.Abs(ans - bore.Value) < 1e-6) { match = true; break; }
                        }
                        catch { break; }
                    }
                    if (match) return item;
                }
            }
            catch { }
            return none;
        }
    }
}
