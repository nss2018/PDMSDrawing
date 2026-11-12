using System;
using System.Collections.Generic;
using Aveva.Pdms.Database;
using Aveva.Pdms.Geometry;
using Aveva.Pdms.Utilities.CommandLine;

namespace drawingPDMS
{
    using ATT = Aveva.Pdms.Database.DbAttributeInstance;
    using CE = Aveva.Pdms.Shared.CurrentElement;
    using DbType = Aveva.Pdms.Database.DbType;

    /// <summary>
    /// 化工管道布置图标准符号（HG20519《化工工艺设计施工图内容与深度统一规定》图例样式）。
    ///
    /// 【机制（2026-10-17 修正）】
    /// 原方案用 SYTM（Symbol Template）图块库 + SLABEL 引用；但 SYTM / LALB 是【LIBRARY 世界】元素，
    /// 只能在 LIBRARY 世界（LIBY）下用 CreateLast 创建，无法挂在 DRAFT 世界的 DEPT 下——
    /// 实测 `DEPT.CreateLast(LALB)` / `LALB.CreateLast(SYTM)` 在 DRAFT 上下文一律抛
    /// 「应用程序中的错误」（CreateDrawing 的同款 CreateLast 对 VIEW/SHEET/LAYER/NOTE/STRAIGHT 全部正常，
    /// 印证只是 library 世界边界问题）。
    ///
    /// 故改为【直接在视图 LAYER 内画 STRAIGHT 线段几何】——与 CreateDrawing 画图框/中心线完全同款通道，
    /// PLOT DXF/PDF 已实证可渲染导出；每个符号在局部坐标（原点=符号中心、管道沿 +X 轴）定义一组线段，
    /// 按纸面坐标 (cx,cy) 平移 + 绕中心逆时针旋转 angleDeg（管道方向角）后落线段。
    /// 颜色：符号固定 4（黄，Draft 默认色板 4=Yellow），管线中心线 2（红，用户指定）；线型 8004。
    ///
    /// 【图例依据】（HG20519 图 6-11~6-18）：弯头端视=圆+中心点；三通=圆+中心点（—⊙—）；
    ///   闸阀=蝴蝶结+阀板细三角+阀杆+手轮；截止阀=蝴蝶结+中心实心点+阀杆+手轮；
    ///   法兰=垂直管道短线；法兰盖=垂直双短线；焊接点=实心点；同心异径管=梯形；
    ///   管架=圆+内接X（Φ5mm）；无管托=X 记号；流向箭头=实心三角。
    /// </summary>
    public static class SymBlocks
    {
        /// <summary>图块库 DEPT 名字（保留以兼容 headlessDrawing 的引用；本方案不再建库，仅占位）。</summary>
        public const string LIB_PATH = "/AGT_SYMS";
        public const string LABELS_PATH = LIB_PATH + "/LABELS";

        /// <summary>符号线条颜色：4 = 黄（PDMS 默认色板 1=Grey 2=Red 4=Yellow 15=Black，实测渲染黄）。</summary>
        public const int SYM_COLOR = 4;

        /// <summary>管线中心线颜色：2 = 红（2026-10-17 (5) 用户要求管线红色；Draft 默认色板 Colour 2 = Red）。</summary>
        public const int PIPE_COLOR = 2;

        /// <summary>标准实线线型（与范围框/旧符号同款）。</summary>
        /// <summary>线型：2026-10-17 (8g) 起不再设置 NLSTYL——8004=GTCHAIN 点划、8001 亦非实线
        /// （run10「红-黄斑马」目检 + DXF LTYPE 无 pattern 实证），统一用数据库默认（实线）。
        /// 常量保留作历史记录，不再被引用。</summary>
        private const string NLSTYL = "8001";

        // ---------------- 图块名常量 ----------------
        public const string N_VALVE = "VALVE";       // 闸阀/通用阀
        public const string N_VALVES = "VALVES";     // 截止阀
        public const string N_ELBOWD = "ELBOWD";     // 弯头端视：竖直管段向下
        public const string N_ELBOWU = "ELBOWU";     // 弯头端视：竖直管段向上
        public const string N_TEE = "TEE";           // 三通/支管端视
        public const string N_TEEFL = "TEEFL";       // 法兰三通端视
        public const string N_FLANGE = "FLANGE";     // 法兰
        public const string N_FLANGEB = "FLANGEB";   // 法兰盖/盲板
        public const string N_WELD = "WELD";         // 焊接点
        public const string N_REDUCER = "REDUCER";   // 同心异径管
        public const string N_SUPPORT = "SUPPORT";   // 管架/有管托
        public const string N_SUPPX = "SUPPX";       // 无管托或其它
        public const string N_ARROW = "ARROW";       // 流向箭头（备用）

        /// <summary>全部图块名（建库顺序，保留兼容性）。</summary>
        public static readonly string[] ALL = new string[]
        {
            N_VALVE, N_VALVES, N_ELBOWD, N_ELBOWU, N_TEE, N_TEEFL,
            N_FLANGE, N_FLANGEB, N_WELD, N_REDUCER, N_SUPPORT, N_SUPPX, N_ARROW
        };

        // ================= 局部几何（纸面 mm，原点在符号中心、管道沿 +X 轴；每线段 [x1,y1,x2,y2]） =================

        /// <summary>(9a) HGT 表11.0.1 参考画法：圆周在管线通过的方向【开口】（C 形圆），线从圆心连续穿过。
        /// gapDirs = 需开口的纸面方向角（度）；gapHalfDeg = 每个口的半角（25°≈弦长 2.1mm@r2.5）。</summary>
        public static int DrawOpenCircle(DbElement layer, double cx, double cy, double r, int color, List<double> gapDirs, double gapHalfDeg)
        {
            int n = 36, cnt = 0;
            double step = 360.0 / n;
            for (int i = 0; i < n; i++)
            {
                double am = (i + 0.5) * step;
                bool skip = false;
                if (gapDirs != null)
                    foreach (double g in gapDirs)
                    {
                        double d = Math.Abs(am - g); if (d > 180.0) d = 360.0 - d;
                        if (d < gapHalfDeg + step * 0.5) { skip = true; break; }
                    }
                if (skip) continue;
                double r0 = i * step * Math.PI / 180.0, r1 = (i + 1) * step * Math.PI / 180.0;
                if (DrawSeg(layer, cx + r * Math.Cos(r0), cy + r * Math.Sin(r0),
                                cx + r * Math.Cos(r1), cy + r * Math.Sin(r1), color)) cnt++;
            }
            return cnt;
        }

        /// <summary>把半径 r 的圆近似为 n 段折线，追加到 L。</summary>
        private static void CircleSegs(List<double[]> L, double r, int n)
        {
            if (n < 3) n = 16;
            for (int i = 0; i < n; i++)
            {
                double a1 = i * 2 * Math.PI / n;
                double a2 = (i + 1) * 2 * Math.PI / n;
                L.Add(new double[] { r * Math.Cos(a1), r * Math.Sin(a1), r * Math.Cos(a2), r * Math.Sin(a2) });
            }
        }

        /// <summary>返回某符号的局部线段集合（原点中心、管道沿 +X）。未知名返回空表。</summary>
        private static List<double[]> Segs(string nm)
        {
            var L = new List<double[]>();
            if (nm == N_VALVE || nm == N_VALVES)
            {
                double d = 4.0, h = 2.2; // 蝴蝶结半长 / 半高
                // 左三角
                L.Add(new double[] { 0, 0, -d, h });
                L.Add(new double[] { -d, h, -d, -h });
                L.Add(new double[] { -d, -h, 0, 0 });
                // 右三角
                L.Add(new double[] { 0, 0, d, h });
                L.Add(new double[] { d, h, d, -h });
                L.Add(new double[] { d, -h, 0, 0 });
                // (8y) HGT 20519-2009 闸阀单线=蝴蝶结+阀杆+手轮；顶冠细三角线（-d,h→0,7.0 两根）
                // 非标删除——用户 2026-10-18「其他图例一步到位」。
                if (nm == N_VALVE)
                {
                    L.Add(new double[] { 0, 0, 0, 7.6 });       // 阀杆
                    L.Add(new double[] { -1.6, 7.6, 1.6, 7.6 }); // 手轮横杠
                }
                else // 截止阀：蝴蝶结 + 中心实心点 + 阀杆 + 手轮
                {
                    L.Add(new double[] { 0, 0, 0, 6.0 });       // 阀杆
                    L.Add(new double[] { -1.6, 6.0, 1.6, 6.0 }); // 手轮横杠
                    L.Add(new double[] { -0.9, 0, 0.9, 0 });    // 中心实心点（十字）
                    L.Add(new double[] { 0, -0.9, 0, 0.9 });
                }
            }
            else if (nm == N_ELBOWD) // 下翻（2026-10-17 (8l) 用户：下翻=圆 + 圆心实心点（十字近似））
            {
                CircleSegs(L, 2.5, 16);
                L.Add(new double[] { -0.9, 0, 0.9, 0 });
                L.Add(new double[] { 0, -0.9, 0, 0.9 });
            }
            else if (nm == N_ELBOWU) // 上翻（2026-10-17 (8k) 用户：打印黑白不分色，上翻=圆内一根 1/4 弧
                                     // （弧由调用方按出口方向画，此处只画圆）；原圆心十字点删除）
            {
                CircleSegs(L, 2.5, 16);
            }
            else if (nm == N_TEE) // 三通/支管端视：圆 + 中心点
            {
                CircleSegs(L, 2.5, 16);
                L.Add(new double[] { -0.8, 0, 0.8, 0 });
                L.Add(new double[] { 0, -0.8, 0, 0.8 });
            }
            else if (nm == N_TEEFL) // 法兰三通端视
            {
                CircleSegs(L, 2.5, 16);
                L.Add(new double[] { -0.8, 0, 0.8, 0 });
                L.Add(new double[] { 0, -0.8, 0, 0.8 });
                L.Add(new double[] { 3.8, -2.2, 3.8, 2.2 });
                L.Add(new double[] { 4.6, -2.2, 4.6, 2.2 });
            }
            else if (nm == N_FLANGE) // 法兰连接（(8y) HGT 表11.0.1 单线=垂直于管道的一对平行短线，间距 0.8mm）
            {
                L.Add(new double[] { -0.4, -2.6, -0.4, 2.6 });
                L.Add(new double[] { 0.4, -2.6, 0.4, 2.6 });
            }
            else if (nm == N_FLANGEB) // 法兰盖：垂直双短线
            {
                L.Add(new double[] { -0.6, -2.6, -0.6, 2.6 });
                L.Add(new double[] { 0.6, -2.6, 0.6, 2.6 });
            }
            else if (nm == N_WELD) // 焊接点：实心点（十字）
            {
                L.Add(new double[] { -1.0, 0, 1.0, 0 });
                L.Add(new double[] { 0, -1.0, 0, 1.0 });
            }
            else if (nm == N_REDUCER) // 同心异径管（梯形，左大右小）
            {
                L.Add(new double[] { -2.6, -2.2, -2.6, 2.2 });
                L.Add(new double[] { -2.6, 2.2, 2.6, 1.2 });
                L.Add(new double[] { 2.6, 1.2, 2.6, -1.2 });
                L.Add(new double[] { 2.6, -1.2, -2.6, -2.2 });
            }
            else if (nm == N_SUPPORT) // 管架（圆 Φ5mm + 内接 X）
            {
                CircleSegs(L, 2.5, 16);
                L.Add(new double[] { -1.7, -1.7, 1.7, 1.7 });
                L.Add(new double[] { -1.7, 1.7, 1.7, -1.7 });
            }
            else if (nm == N_SUPPX) // 无管托（X 记号）
            {
                L.Add(new double[] { -2.4, -2.4, 2.4, 2.4 });
                L.Add(new double[] { -2.4, 2.4, 2.4, -2.4 });
            }
            else if (nm == N_ARROW) // 流向箭头（2026-10-17 (8f)：纯三角小箭头，无尾线——用户要求
            {                        // 箭头上不要横线；总长 1.4mm，指向 +X，缺口+白遮罩由调用方负责）
                L.Add(new double[] { -0.7, -0.75, 0.7, 0 });    // 三角下边
                L.Add(new double[] { 0.7, 0, -0.7, 0.75 });     // 三角上边
                L.Add(new double[] { -0.7, -0.75, -0.7, 0.75 }); // 三角底边
            }
            return L;
        }

        // ================= 绘制 =================

        /// <summary>最近一次 DrawSeg 失败原因（供 ReplaceSymbols 日志诊断；空串=无失败）。</summary>
        public static string LastDrawError = "";

        /// <summary>(8n) 线型设置诊断：只记第一次的结果（已设值/候选全败），供 ReplaceSymbols 日志输出。</summary>
        public static string LstylDiag = "";

        /// <summary>CP 命令熔断器：Fpt/at 定位命令一旦失败（RunInPdms 会弹 PDMS 模态框），
        /// 置位后所有后续 DrawSeg 直接短路返回 false——最多弹 1 个框，绝不逐段轰炸。</summary>
        public static bool CommandGateBroken = false;

        /// <summary>画一条纸面线段（与 CreateDrawing 图框同款定位命令，颜色固定 SYM_COLOR）。
        /// 线段端点绝对纸面坐标 (x1,y1)-(x2,y2)：Fpt=原点、Tpt=偏移(dx,dy)、at 定位原点。
        /// 2026-10-17 (3)：全程 try 包裹（CreateLast 失败不再外抛，统一走 LastDrawError 诊断链）、
        /// 定位命令失败即熔断。</summary>
        /// <summary>公开包装：画一条指定颜色的纸面线段（红色管线中心线用）。</summary>
        public static bool DrawSegment(DbElement owner, double x1, double y1, double x2, double y2, int color)
        {
            return DrawSeg(owner, x1, y1, x2, y2, color);
        }

        private static bool DrawSeg(DbElement owner, double x1, double y1, double x2, double y2, int color)
        {
            LastDrawError = "";
            if (CommandGateBroken) { LastDrawError = "CP 熔断（此前定位命令已失败）"; return false; }
            string step = "create";
            try
            {
                if (owner == null || owner.IsNull) { LastDrawError = "owner null"; return false; }
                DbElement ln = owner.CreateLast(DbElementTypeInstance.STRAIGHT);
                if (ln == null || ln.IsNull) { LastDrawError = "CreateLast(STRAIGHT) null"; return false; }
                double dx = x2 - x1, dy = y2 - y1;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 0.01) len = 0.01;
                // 2026-10-17 (8n) 线型真根因：NLSTYL 是【线型名】引用——此前设 "8004"/"8001" 数字串
                // 大概率从未设置成功（静默失败/异常被吞），红线一直继承 CE 上下文默认虚线。
                // 实证链：run8 改 8001 仍斑马；run12/13 不设仍斑马（H 区单线斑马、G 区多分支
                // 虚线叠加互补伪装成连续）；PDF 内容流红线短划 = 打印驱动对虚线的离散化。
                // 修复：显式设 GTSOLID（DXF LTYPE 表首项=实线），候选依次尝试，成功后回读验证。
                if (LstylDiag.Length == 0)
                {
                    bool okL = false; string tried = "";
                    foreach (string cand in new string[] { "GTSOLID", "GT SOLID", "SOLID" })
                    {
                        try
                        {
                            drawTool.SetAttribute(ln, ATT.NLSTYL, cand);
                            string back = "";
                            try { back = ln.GetAsString(ATT.NLSTYL); } catch { }
                            tried += cand + "->回读[" + back + "] ";
                            if (!string.IsNullOrEmpty(back) && back.IndexOf(cand.Split(' ')[0], StringComparison.OrdinalIgnoreCase) >= 0) { okL = true; break; }
                        }
                        catch (Exception exL) { tried += cand + "->异常[" + exL.Message + "] "; }
                    }
                    LstylDiag = (okL ? "线型OK " : "线型全败 ") + tried;
                }
                else
                {
                    try { drawTool.SetAttribute(ln, ATT.NLSTYL, "GTSOLID"); } catch { }
                }
                step = "leng";
                drawTool.SetAttribute(ln, ATT.LENG, len.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
                step = "colo";
                ln.SetAttribute(ATT.NLCOLO, color);
                step = "name";
                string nm = ln.GetAsString(ATT.NAME);
                if (nm == null || nm.Trim().Length == 0 || nm.Contains("=0/0")) { LastDrawError = "NAME 异常: " + nm; return false; }
                step = "fpt";
                if (!drawTool.CommandExcute(nm + " Fpt Offset X 0mm Y 0mm Tpt Offset X " + dx.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "mm Y " + dy.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "mm"))
                { LastDrawError = "CP Fpt 失败(step=" + step + "): " + nm; CommandGateBroken = true; return false; }
                step = "at";
                if (!drawTool.CommandExcute(nm + " at x " + x1.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + " y " + y1.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)))
                { LastDrawError = "CP at 失败(step=" + step + "): " + nm; CommandGateBroken = true; return false; }
                return true;
            }
            catch (Exception ex) { LastDrawError = "step=" + step + " [" + ex.GetType().Name + "] " + ex.Message; return false; }
        }

        /// <summary>
        /// 在 layer 内直接绘制符号：把局部线段按 (cx,cy) 平移 + 绕中心逆时针旋转 angleDeg（管道方向角）后落 STRAIGHT。
        /// 返回实际落下的线段数（0 = 未知符号或全失败）。
        /// </summary>
        public static int Draw(DbElement layer, string symName, double cx, double cy, double angleDeg)
        {
            return Draw(layer, symName, cx, cy, angleDeg, SYM_COLOR);
        }

        /// <summary>带颜色版本（(8o) 弯头图例=立管属管道本体，随管线红色）。</summary>
        public static int Draw(DbElement layer, string symName, double cx, double cy, double angleDeg, int color)
        {
            LastDrawError = "";
            var list = Segs(symName);
            if (list == null || list.Count == 0) return 0;
            double a = angleDeg * Math.PI / 180.0;
            double cos = Math.Cos(a), sin = Math.Sin(a);
            int n = 0;
            foreach (var s in list)
            {
                double lx1 = s[0], ly1 = s[1], lx2 = s[2], ly2 = s[3];
                double X1 = lx1 * cos - ly1 * sin + cx;
                double Y1 = lx1 * sin + ly1 * cos + cy;
                double X2 = lx2 * cos - ly2 * sin + cx;
                double Y2 = lx2 * sin + ly2 * cos + cy;
                if (DrawSeg(layer, X1, Y1, X2, Y2, color)) n++;
            }
            return n;
        }

        /// <summary>
        /// 兼容 ReplaceSymbols 的调用签名：在 layer 内画符号，成功（>=1 段）返回 layer（非 null），失败返回 null。
        /// dataElem 保留参数（可用于未来标注关联），本方案不强制引用。
        /// </summary>
        public static DbElement Insert(DbElement layer, string symName, DbElement dataElem, double x, double y, double angleDeg)
        {
            return Insert(layer, symName, dataElem, x, y, angleDeg, SYM_COLOR);
        }

        /// <summary>带颜色版本（(8o)）。</summary>
        public static DbElement Insert(DbElement layer, string symName, DbElement dataElem, double x, double y, double angleDeg, int color)
        {
            int n = Draw(layer, symName, x, y, angleDeg, color);
            if (n > 0) return layer;
            return null;
        }

        /// <summary>
        /// 兼容调用：本方案不再建 SYTM 图块库（library 世界边界导致 CreateLast 失败），符号改为直接画线，
        /// 故 EnsureLibrary 恒返回 true（无库可建/可缺）。
        /// </summary>
        public static bool EnsureLibrary(bool rebuild, Action<string> log)
        {
            if (log != null) log("  [direct-draw 模式] 不再建 SYTM 库（library 世界边界），符号直接画 STRAIGHT——EnsureLibrary 短路返回 true");
            return true;
        }
    }
}
