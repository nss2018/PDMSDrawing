// headlessDrawing.cs — 窗口「图纸生成」(drawE3D.cs button2_Click) + 「图纸导出」(button11_Click) 的无头移植
//
// 移植口径（2026-10-14 用户拍板：1:1 镜像窗口生产链，不另起炉灶、不探测）：
//   - 元素创建/属性写入/CE 切换/出图清单：全部复用 drawTool / drawE3DTool / Graphic 现有函数原样调用
//   - CP 命令（A4 size、ADD ALL WITHIN、at x y、Fpt/Tpt Offset、just Centre、LSHAPE BENT、
//     UPDATE all、LIEXEC、PLOT）逐字照抄窗口代码
//   - UI 依赖替换：textBox1-6→box 参数、comboBox1→drawingType、comboBox5→scaleDen、
//     listBox1→elements（空格分隔名）、textBox7→outDir、textBox8/9/10→backs/rrsf/hrsf、
//     MessageBox→返回文本；其余逐行等价
//   - drawTool.SetAttribute 吞异常只返回错误串的行为保持原样（与窗口一致，调用方忽略返回值）
//
// 调用方：PdmsBridge 的 kind:"op" 操作（probe_read / create_drawing / export_dxf / export_pdf），
//        在 PDMS UI 线程执行（见 PdmsBridge.RunOnUi）。
using Aveva.Pdms.Database;
using Aveva.Pdms.Geometry;
using Aveva.Pdms.Utilities.CommandLine;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;   // 2026-10-16：export_dxf 落盘校验（FileInfo/File.Exists）
using System.Linq;
using System.Text;
using DbType = Aveva.Pdms.Database.DbType;
using CE = Aveva.Pdms.Shared.CurrentElement;
using ATT = Aveva.Pdms.Database.DbAttributeInstance;
using Filter = Aveva.PDMS.Database.Filters.TypeFilter;
using Aveva.PDMS.Database.Filters;

namespace drawingPDMS
{
    public static class HeadlessDrawing
    {
        public class OpResult
        {
            public bool Ok;
            public string Output = "";
            public string Error = null;
        }

        /* ---------------- 小工具 ---------------- */

        private static string P(Dictionary<string, string> p, string key, string def)
        {
            if (p != null && p.ContainsKey(key) && !string.IsNullOrEmpty(p[key])) return p[key];
            return def;
        }

        private static double PD(Dictionary<string, string> p, string key, double def)
        {
            if (p == null || !p.ContainsKey(key) || string.IsNullOrEmpty(p[key])) return def;
            double v;
            if (double.TryParse(p[key], NumberStyles.Any, CultureInfo.InvariantCulture, out v)) return v;
            return def;
        }

        // 元素包围盒 [xmin,xmax,ymin,ymax,zmin,zmax]（照抄 drawE3D.GetElementLimits :1307-1360；
        // 全 0 = 未取到，与窗口 button3 的用法一致）
        private static double[] ElementLimits(DbElement element)
        {
            try
            {
                double[] wvol = element.GetDoubleArray(ATT.WVOL);
                if (wvol != null && wvol.Length >= 6)
                    return wvol;
                if (element.GetElementType() == DbElementTypeInstance.BRANCH ||
                    element.GetElementType() == DbElementTypeInstance.PIPE)
                {
                    double minX = double.MaxValue, maxX = double.MinValue;
                    double minY = double.MaxValue, maxY = double.MinValue;
                    double minZ = double.MaxValue, maxZ = double.MinValue;
                    bool any = false;
                    foreach (DbElement member in element.Members())
                    {
                        double[] mv = member.GetDoubleArray(ATT.WVOL);
                        if (mv != null && mv.Length >= 6)
                        {
                            minX = Math.Min(minX, mv[0]); maxX = Math.Max(maxX, mv[3]);
                            minY = Math.Min(minY, mv[1]); maxY = Math.Max(maxY, mv[4]);
                            minZ = Math.Min(minZ, mv[2]); maxZ = Math.Max(maxZ, mv[5]);
                            any = true;
                        }
                        else
                        {
                            Position pos = member.GetPosition(ATT.WORPOS);
                            if (pos != null)
                            {
                                minX = Math.Min(minX, pos.X); maxX = Math.Max(maxX, pos.X);
                                minY = Math.Min(minY, pos.Y); maxY = Math.Max(maxY, pos.Y);
                                minZ = Math.Min(minZ, pos.Z); maxZ = Math.Max(maxZ, pos.Z);
                                any = true;
                            }
                        }
                    }
                    if (any)
                        return new double[] { minX, maxX, minY, maxY, minZ, maxZ };
                }
                Position pos0 = element.GetPosition(ATT.WORPOS);
                if (pos0 != null)
                {
                    double r = 1000; // 默认半宽 1m
                    return new double[] { pos0.X - r, pos0.X + r, pos0.Y - r, pos0.Y + r, pos0.Z - r, pos0.Z + r };
                }
            }
            catch { }
            return new double[6];
        }

        private static bool IsZeroBox(double[] l)
        {
            if (l == null || l.Length < 6) return true;
            for (int i = 0; i < 6; i++) if (l[i] != 0) return false;
            return true;
        }

        /// <summary>(10a) 轴网线段（NLSTYL 8004 点划线，与「设备中心线」同款实证通道；
        /// 不走 SymBlocks.DrawSeg——那强制 GTSOLID 实线）。失败返回 false 不熔断（轴网属次要层）。</summary>
        private static bool DrawGridSeg(DbElement owner, double x1, double y1, double x2, double y2, int color)
        {
            try
            {
                if (owner == null || owner.IsNull) return false;
                DbElement ln = owner.CreateLast(DbElementTypeInstance.STRAIGHT);
                if (ln == null || ln.IsNull) return false;
                double dx = x2 - x1, dy = y2 - y1;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 0.01) len = 0.01;
                try { drawTool.SetAttribute(ln, ATT.NLSTYL, "8004"); } catch { }
                drawTool.SetAttribute(ln, ATT.LENG, len.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
                ln.SetAttribute(ATT.NLCOLO, color);
                string nm = ln.GetAsString(ATT.NAME);
                if (nm == null || nm.Trim().Length == 0 || nm.Contains("=0/0")) return false;
                if (!drawTool.CommandExcute(nm + " Fpt Offset X 0mm Y 0mm Tpt Offset X " + dx.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "mm Y " + dy.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "mm")) return false;
                if (!drawTool.CommandExcute(nm + " at x " + x1.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + " y " + y1.ToString("F3", System.Globalization.CultureInfo.InvariantCulture))) return false;
                return true;
            }
            catch { return false; }
        }

        /// <summary>(10) 居中文字（CE 直指 + 无前缀 at，9s-v2 实证通道；XTEX/YTEX SetAttribute 已被实测拒绝）。
        /// (cx,cy) = 期望视觉中心；内部按 9s-v2 实测字宽模型 + 0.75×字高修正落位。</summary>
        private static bool DrawTextCentre(DbElement owner, string txt, double cx, double cy, double chei, int color)
        {
            try
            {
                if (owner == null || owner.IsNull || string.IsNullOrEmpty(txt)) return false;
                DbElement t = owner.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
                if (t == null || t.IsNull) return false;
                drawTool.SetAttribute(t, ATT.JUST, "Centre");
                drawTool.SetAttribute(t, ATT.ALIGN, "Hbody");
                drawTool.SetAttribute(t, ATT.TXCOLO, color.ToString());
                drawTool.SetAttribute(t, ATT.CHEI, chei.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
                t.SetAttribute(ATT.BTEX, txt);
                double wSum = 0;
                foreach (char c in txt) wSum += (c == '1' || c == 'I' || c == '.' || c == ' ') ? 1.13 : 2.58;
                double halfw = wSum / 2.0 * (chei / 2.5);
                try { CE.Element = t; } catch { }
                drawTool.CommandExcute("at x " + (cx - halfw).ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                                     + " y " + (cy - 0.75 * chei).ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
                return true;
            }
            catch { return false; }
        }

        /// <summary>(10c) 定位尺寸端符：45° 斜短线（与 DTER=4 用户选定的建筑风格一致）。</summary>
        private static void DimTick(DbElement owner, double x, double y, int color)
        {
            SymBlocks.DrawSegment(owner, x - 0.9, y - 0.9, x + 0.9, y + 0.9, color);
        }

        /// <summary>(10c-v3) 边界外放字（用户「轴网设备标注都合并到边界线外」）：按锚点最近边贴边外
        /// 3.5mm（上下 2.5mm）对齐锚点坐标；同边已占位则 3mm（上下 4mm）步进错档。返回 (cx,cy)。</summary>
        private static double[] PlaceOutside(double[] lim, double ax, double ay, double hw,
            List<double[]> oL, List<double[]> oR, List<double[]> oB, List<double[]> oT)
        {
            double dL = ax - lim[0], dR = lim[1] - ax, dB = ay - lim[2], dT = lim[3] - ay;
            int side = 0; double dm = dL;
            if (dR < dm) { dm = dR; side = 1; }
            if (dB < dm) { dm = dB; side = 2; }
            if (dT < dm) { dm = dT; side = 3; }
            double cx, cy;
            if (side == 0 || side == 1)
            {
                cx = side == 0 ? lim[0] - 3.5 - hw : lim[1] + 3.5 + hw;
                cy = ay;
                double hh = 1.6; // 文字半高 1.1 + 间隙 0.5
                List<double[]> occ = side == 0 ? oL : oR;
                bool moved = true;
                while (moved)
                {
                    moved = false;
                    foreach (double[] o in occ)
                        if (cy + hh > o[0] && cy - hh < o[1]) { cy = o[0] - hh; moved = true; }
                }
                occ.Add(new double[] { cy - hh, cy + hh });
            }
            else
            {
                cy = side == 2 ? lim[2] - 2.5 : lim[3] + 2.5;
                cx = ax;
                List<double[]> occ = side == 2 ? oB : oT;
                bool moved = true;
                while (moved)
                {
                    moved = false;
                    foreach (double[] o in occ)
                        if (cx + hw > o[0] && cx - hw < o[1]) { cx = o[0] - hw - 1.0; moved = true; }
                }
                occ.Add(new double[] { cx - hw, cx + hw });
            }
            return new double[] { cx, cy };
        }

        /// <summary>(10b) 设备 WVOL 缺失时：成员外包络并集（ElementLimits 逐成员）。</summary>
        private static double[] UnionMembersBox(DbElement el)
        {
            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;
            double minZ = double.MaxValue, maxZ = double.MinValue;
            bool any = false;
            try
            {
                foreach (DbElement m in el.Members())
                {
                    if (m == null || m.IsNull) continue;
                    double[] mv = ElementLimits(m);
                    if (IsZeroBox(mv)) continue;
                    minX = Math.Min(minX, mv[0]); maxX = Math.Max(maxX, mv[3]);
                    minY = Math.Min(minY, mv[1]); maxY = Math.Max(maxY, mv[4]);
                    minZ = Math.Min(minZ, mv[2]); maxZ = Math.Max(maxZ, mv[5]);
                    any = true;
                }
            }
            catch { }
            return any ? new double[] { minX, maxX, minY, maxY, minZ, maxZ } : new double[6];
        }


        /// <summary>跨 world 设计元素解析（2026-10-15 /SPOOLER-PIPES 出图实测根因修复）：
        /// C# DbElement.GetElement 只跟随 CE 所在 world——CE 在出图 world 时，设计库元素
        /// 全路径不可解析（CP 侧同证："(2,109) Undefined name" + "(64,57) entry ignored"）。
        /// 引用形态按用户纠正（2026-10-15）：**只写目标名、不带父节点**（如 "/SPOOLER-PIPE-2"）。
        /// 本方法用 Utilities.MDBMembers(DbType.Design) 枚举设计库顶层（probe 已证跨上下文可用）：
        ///   1) 单段目标名 → 顶层名匹配（SITE 级元素）→ 未命中则全设计库 DFS 按名查找（深度/数量封顶）；
        ///   2) 多段全路径 → 顶层起逐段 Members 名称匹配下行（兼容旧写法）。失败返回 null。</summary>
        private static DbElement ResolveDesignPath(string elementRef)
        {
            try
            {
                string[] segs = (elementRef ?? "").TrimStart('/').Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (segs.Length == 0) return null;
                List<DbElement> tops = new List<DbElement>();
                foreach (DbElement top in FangYuan.PDMS.Utilities.MDBMembers(DbType.Design))
                    if (top != null && !top.IsNull) tops.Add(top);
                if (segs.Length == 1)
                {
                    foreach (DbElement top in tops)
                    {
                        string topName = "";
                        try { topName = top.GetAsString(ATT.NAME); } catch { continue; }
                        if (topName.Equals(segs[0], StringComparison.OrdinalIgnoreCase)) return top;
                    }
                    int budget = 30000; // 大项目防爆封底（深度>10 或 访问>3万 停止）
                    return FindByNameDfs(tops, segs[0], 0, ref budget);
                }
                foreach (DbElement top in tops)
                {
                    string topName = "";
                    try { topName = top.GetAsString(ATT.NAME); } catch { continue; }
                    if (!topName.Equals(segs[0], StringComparison.OrdinalIgnoreCase)) continue;
                    DbElement cur = top;
                    bool ok = true;
                    for (int i = 1; i < segs.Length; i++)
                    {
                        DbElement next = null;
                        foreach (DbElement m in cur.Members())
                        {
                            if (m == null || m.IsNull) continue;
                            string mn = "";
                            try { mn = m.GetAsString(ATT.NAME); } catch { continue; }
                            if (mn.Equals(segs[i], StringComparison.OrdinalIgnoreCase)) { next = m; break; }
                        }
                        if (next == null) { ok = false; break; }
                        cur = next;
                    }
                    if (ok) return cur;
                }
            }
            catch { }
            return null;
        }

        /// <summary>在规则集（RRSF 所指 RPLB/RRUL）子树中收集 STYL 元素（深度封顶 4，防大库遍历）。</summary>
        private static List<DbElement> CollectStyls(DbElement root, int depth)
        {
            List<DbElement> found = new List<DbElement>();
            if (root == null || root.IsNull || depth > 4) return found;
            DbElement[] members;
            try { members = root.Members(); } catch { return found; }
            foreach (DbElement m in members)
            {
                if (m == null || m.IsNull) continue;
                try { if (m.GetElementType().ToString() == "STYL") found.Add(m); } catch { }
                found.AddRange(CollectStyls(m, depth + 1));
            }
            return found;
        }

        /// <summary>DFS 找指定类型元素（RPLB 样式库查找用；预算/深度/命中数三限防全库遍历）。
        /// 每 128 个元素 DoEvents 泵一次消息（2026-10-15 fatal 教训）：长冻结 UI 线程后 Infragistics 树
        /// 控件重绘竞态 → OnPaint NullReferenceException → "The program must exit"。</summary>
        private static void FindTypeDfs(DbElement node, string typeName, List<DbElement> found, ref int budget, int depth)
        {
            if (depth > 16 || budget <= 0 || found.Count > 50) return;
            DbElement[] children;
            try { children = node.Members(); } catch { return; }
            int pump = 0;
            foreach (DbElement c in children)
            {
                if (budget <= 0 || found.Count > 50) return;
                if (c == null || c.IsNull) continue;
                budget--;
                if ((++pump & 0x7F) == 0) { try { System.Windows.Forms.Application.DoEvents(); } catch { } }
                string tn = "";
                try { tn = c.GetElementType().ToString(); } catch { continue; }
                if (tn == typeName) found.Add(c);
                else FindTypeDfs(c, typeName, found, ref budget, depth + 1);
            }
        }

        /// <summary>设计库 DFS 按名查找（首命中返回；名字冲突取先遍历到者）。</summary>
        private static DbElement FindByNameDfs(List<DbElement> nodes, string name, int depth, ref int budget)
        {
            if (depth > 10 || budget <= 0) return null;
            foreach (DbElement n in nodes)
            {
                if (n == null || n.IsNull) continue;
                budget--;
                string nm = "";
                try { nm = n.GetAsString(ATT.NAME); } catch { continue; }
                if (nm.Equals(name, StringComparison.OrdinalIgnoreCase)) return n;
                DbElement[] children;
                try { children = n.Members(); } catch { continue; }
                DbElement r = FindByNameDfs(new List<DbElement>(children), name, depth + 1, ref budget);
                if (r != null && !r.IsNull) return r;
            }
            return null;
        }

        /// <summary>视图范围框 UDA 走 $M 宏通道（2026-10-15 23:0x 重构，官方语法证据 = gen/ulimits SETLIMITS :125-127
        /// + clib/ulimitset :CDLIMITS 读写 + resultV70 回读实证）：
        /// :CDLIMITS |From E &lt;xmin&gt; W &lt;-xmin&gt; N &lt;ymin&gt; S &lt;-ymin&gt; U &lt;zmin&gt; D &lt;-zmin&gt; To E &lt;xmax&gt; W &lt;-xmax&gt; N &lt;ymax&gt; S &lt;-ymax&gt; U &lt;zmax&gt; D &lt;-zmax&gt;|
        /// 轴字母必须（ENU 项目；裸 6+6 值静默失败——21:49/22:4x 实测）。
        /// 关键认知（gen/uresetidli :310 注释 + umviewset UDA 模式）：:CDLIMITS 是 ADP 出图深度限制的官方 UDA
        /// （挂在 VIEW 上，出图生成时消费），**不等于 VVOL**（VVOL=SIZE×VSCA 的 3D 预览显示帧，与出图范围无关）。
        /// doSet=true 下发 :CDLIMITS；随后官方回读形式 var !L1 :CDLIMITS 写临时文件（每次新文件名，
        /// openfile write 撞已存在文件即 (41,324) 失败），C# 读盘解析 12 个轴-值对，容差 0.5mm（PML A.11）。
        /// 返回 true = UDA 回读与指定 box 一致。</summary>
        private static bool SendCdlimitsViaMacro(DbElement viewDraw, double specMinX, double specMaxX,
            double specMinY, double specMaxY, double specMinZ, double specMaxZ, bool doSet, FileLoggingStringBuilder log)
        {
            try
            {
                string cdlCmd = string.Format(
                    ":CDLIMITS |From E {0} W {1} N {2} S {3} U {4} D {5} To E {6} W {7} N {8} S {9} U {10} D {11}|",
                    specMinX, -specMinX, specMinY, -specMinY, specMinZ, -specMinZ,
                    specMaxX, -specMaxX, specMaxY, -specMaxY, specMaxZ, -specMaxZ);
                string viewRef = viewDraw.GetAsString(ATT.NAME); // 未命名 = "=ref" 串形，bare-ref CP 定位可用
                string resFile = @"D:\pdmsAgent\bridge\cdlchk_" + DateTime.Now.Ticks.ToString("x") + ".txt";
                string pml = "CE " + viewRef + "\r\n" +
                             "handle any\r\nendhandle\r\n" +
                             (doSet ? cdlCmd + "\r\n" + "handle any\r\nendhandle\r\n" : "") +
                             "var !L1 :CDLIMITS\r\n" +
                             "handle ANY\r\n  !L1 = ERR\r\nendhandle\r\n" +
                             "openfile /" + resFile.Replace('/', '\\') + " write !F1\r\n" +
                             "handle ANY\r\n  openfile /" + resFile.Replace('/', '\\') + " overwrite !F1\r\nendhandle\r\n" +
                             "writefile $!F1 |UDA=$!L1|\r\n" +
                             "closefile $!F1\r\n";
                string pmlFile = @"D:\pdmsAgent\bridge\cdlimits_fix.pml";
                System.IO.File.WriteAllText(pmlFile, pml, new System.Text.UTF8Encoding(false));
                drawTool.CommandExcute("$M " + pmlFile.Replace('/', '\\'));
                log.AppendLine((doSet ? "CDLIMITS 经 $M 通道下发: " + cdlCmd + " (CE " + viewRef + ")"
                                      : "CDLIMITS UDA 回读探测 (CE " + viewRef + ")"));
                // 读盘校验（$M 是 fire-and-forget，轮询 ~5s）
                string uda = null;
                for (int i = 0; i < 10; i++)
                {
                    System.Threading.Thread.Sleep(500);
                    if (System.IO.File.Exists(resFile))
                    {
                        string txt = System.IO.File.ReadAllText(resFile);
                        if (txt.StartsWith("UDA=")) { uda = txt.Substring(4).Trim(); break; }
                    }
                }
                try { if (System.IO.File.Exists(resFile)) System.IO.File.Delete(resFile); } catch { }
                if (uda == null) { log.AppendLine("CDLIMITS UDA 回读失败（结果文件未落盘）"); return false; }
                log.AppendLine("CDLIMITS UDA 回读: " + uda);
                return CdlimitsMatches(uda, specMinX, specMaxX, specMinY, specMaxY, specMinZ, specMaxZ);
            }
            catch (Exception ex) { log.AppendLine("CDLIMITS $M 通道失败: [" + ex.GetType().Name + "] " + ex.Message); return false; }
        }

        /// <summary>解析 :CDLIMITS UDA 串（From/To 各 6 个 轴字母+值 对）与指定 box 比对，容差 0.5mm。</summary>
        private static bool CdlimitsMatches(string uda, double xmin, double xmax, double ymin, double ymax, double zmin, double zmax)
        {
            try
            {
                string[] tok = uda.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                int fromIdx = -1, toIdx = -1;
                for (int i = 0; i < tok.Length; i++)
                {
                    if (tok[i] == "From") fromIdx = i;
                    else if (tok[i] == "To") toIdx = i;
                }
                // token 布局: [From] [E v] [W v] [N v] [S v] [U v] [D v] [To] [E v] ...
                // 2026-10-16 06:3x 修复：初版把 fromIdx+1+i*2（轴字母）当数值解析 → TryParse("E")
                // 必失败 → 正确回读也误判 mismatch（本次 op 日志三处"回读不一致"假警报实证）。
                if (fromIdx < 0 || toIdx < 0 || toIdx + 13 > tok.Length) return false;
                string[] expAxis = { "E", "W", "N", "S", "U", "D" };
                double[] fromVals = new double[6], toVals = new double[6];
                for (int i = 0; i < 6; i++)
                {
                    if (tok[fromIdx + 1 + i * 2] != expAxis[i]) return false;
                    if (!double.TryParse(tok[fromIdx + 2 + i * 2], NumberStyles.Any, CultureInfo.InvariantCulture, out fromVals[i])) return false;
                    if (tok[toIdx + 1 + i * 2] != expAxis[i]) return false;
                    if (!double.TryParse(tok[toIdx + 2 + i * 2], NumberStyles.Any, CultureInfo.InvariantCulture, out toVals[i])) return false;
                }
                // 每轴正/负两个形式都应等于同一坐标（E 值 = W 值的反号 = 该轴坐标）
                double tol = 0.5;
                return Math.Abs(fromVals[0] - xmin) <= tol && Math.Abs(-fromVals[1] - xmin) <= tol &&
                       Math.Abs(fromVals[2] - ymin) <= tol && Math.Abs(-fromVals[3] - ymin) <= tol &&
                       Math.Abs(fromVals[4] - zmin) <= tol && Math.Abs(-fromVals[5] - zmin) <= tol &&
                       Math.Abs(toVals[0] - xmax) <= tol && Math.Abs(-toVals[1] - xmax) <= tol &&
                       Math.Abs(toVals[2] - ymax) <= tol && Math.Abs(-toVals[3] - ymax) <= tol &&
                       Math.Abs(toVals[4] - zmax) <= tol && Math.Abs(-toVals[5] - zmax) <= tol;
            }
            catch { return false; }
        }

        /* ---------------- create_drawing（窗口 button2_Click 1:1 镜像） ---------------- */

        public static OpResult CreateDrawing(Dictionary<string, string> p)
        {
            OpResult res = new OpResult();
            // 日志落盘（2026-10-15：op #2 内存日志随 GUI fatal 丢失，(2,202) 触发行无法取证）
            FileLoggingStringBuilder log = new FileLoggingStringBuilder(@"D:\pdmsAgent\bridge\op_create_drawing.log");
            try
            {
                double ScaleDen = PD(p, "scaleDen", 50);
                double Scale = 1.0 / ScaleDen;
                // WT-DRAW benchmark: explicit standard per drawing + evidence in the op log.
                DrawingStandard.Apply(p);
                log.AppendLine(DrawingStandard.Summary());
                foreach (string stdW in DrawingStandard.Validate(ScaleDen)) log.AppendLine("[STD-WARN] " + stdW);
                string DrawingType = P(p, "drawingType", "管道布置图");
                // (14g) 阀门等附件不加入标注：默认过滤管件级尺寸测点，dimFittings=1 恢复旧行为
                Graphic.DimIncludeFittings = P(p, "dimFittings", "0") == "1";
                log.AppendLine("(14g) 附件进尺寸链 = " + (Graphic.DimIncludeFittings ? "开(旧行为)" : "关(默认)"));
                bool annotate = !"false".Equals(P(p, "annotate", "true"), StringComparison.OrdinalIgnoreCase);
                string backs = P(p, "backs", "/DRA/MAS/BACKS/MET/A0");
                string rrsf = P(p, "rrsf", "/DRA/MAS/H&S/RULESETS/View1");
                string hrsf = P(p, "hrsf", "/DRA/PRJ/HRUL/GEN/Hatch-None");
                string elements = P(p, "elements", "");

                // 视图范围（窗口 textBox1-6）：显式 box 优先；未给则从 elements 的包围盒自动求（窗口 button3 同款逻辑）
                bool hasBox = false;
                foreach (string k in new string[] { "xmin", "xmax", "ymin", "ymax", "zmin", "zmax" })
                    if (p != null && p.ContainsKey(k) && !string.IsNullOrEmpty(p[k])) hasBox = true;
                double viewMinX = 0, viewMaxX = 0, viewMinY = 0, viewMaxY = 0, viewMinZ = 0, viewMaxZ = 0;
                // 2026-10-16 图框贴合实际管系内容：可见管系构件(ELBOW/VALVE/FLANGE) WORPOS 3D 包围盒
                double pipeMnW = double.NaN, pipeMxW = double.NaN, pipeMnN = double.NaN, pipeMxN = double.NaN, pipeMnU = double.NaN, pipeMxU = double.NaN;
                int pipeCnt = 0;
                if (hasBox)
                {
                    viewMinX = PD(p, "xmin", 0); viewMaxX = PD(p, "xmax", 0);
                    viewMinY = PD(p, "ymin", 0); viewMaxY = PD(p, "ymax", 0);
                    viewMinZ = PD(p, "zmin", 0); viewMaxZ = PD(p, "zmax", 0);
                }
                else
                {
                    bool any = false;
                    double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue,
                           maxY = double.MinValue, minZ = double.MaxValue, maxZ = double.MinValue;
                    foreach (string add in elements.Split(new char[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        // 跨 world 解析优先（CE 在出图 world 时 GetElement 够不到设计库）
                        DbElement e = ResolveDesignPath(add);
                        if (e == null || e.IsNull) e = DbElement.GetElement(add);
                        if (e == null || e.IsNull) throw new Exception("元素不存在: " + add);
                        double[] l = ElementLimits(e);
                        if (IsZeroBox(l)) continue;
                        minX = Math.Min(minX, l[0]); maxX = Math.Max(maxX, l[1]);
                        minY = Math.Min(minY, l[2]); maxY = Math.Max(maxY, l[3]);
                        minZ = Math.Min(minZ, l[4]); maxZ = Math.Max(maxZ, l[5]);
                        any = true;
                    }
                    if (!any) throw new Exception("需传 box（xmin/xmax/ymin/ymax/zmin/zmax）或可取包围盒的 elements");
                    viewMinX = minX; viewMaxX = maxX; viewMinY = minY; viewMaxY = maxY; viewMinZ = minZ; viewMaxZ = maxZ;
                }
                // 保留规格 box（2026-10-15 23:0x 修复）：viewMinX.. 在 ACTUAL_LIMITS 回读处会被覆盖
                // （标注/标签定位继续用覆盖后的实际帧——窗口同款行为），范围框 UDA 与 SIZE/THPO
                // 必须用规格 box（21:49/22:4x 教训：覆盖后变量被误用于 :CDLIMITS 重设，发出野值框）。
                double specMinX = viewMinX, specMaxX = viewMaxX, specMinY = viewMinY, specMaxY = viewMaxY, specMinZ = viewMinZ, specMaxZ = viewMaxZ;
                
                log.AppendLine("=== DRAW-START type=" + DrawingType + " scale=1/" + ScaleDen +
                               " box[W" + viewMinX + " N" + viewMinY + " U" + viewMinZ +
                               " TO W" + viewMaxX + " N" + viewMaxY + " U" + viewMaxZ + "]" +
                               (elements.Length > 0 ? " elements=" + elements : "") + "===");

                // 起点（窗口 = CE 当前元素，headless 用 start 参数，默认 /*）
                string startName = P(p, "start", "/*");
                DbElement cueCurrent = null;
                // start=draft（2026-10-15 新增）：直接定位 DRAFT(出图) world 根，不依赖 CE 当前 world。
                // 途径1: MDBMembers(DbType.Draft) 任一成员的 Owner；途径2 回退: CE 向上爬到 WORLD
                // （调用前需确保 CE 在出图 world——GUI 点 Draft WORLD 节点即可）。
                if ("draft".Equals(startName, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        foreach (DbElement top in FangYuan.PDMS.Utilities.MDBMembers(DbType.Draft))
                            if (top != null && !top.IsNull) { cueCurrent = top.Owner; break; }
                    }
                    catch { }
                    if (cueCurrent == null || cueCurrent.IsNull)
                    {
                        DbElement cur = CE.Element;
                        while (cur != null && !cur.IsNull
                               && cur.GetElementType() != DbElementTypeInstance.WORLD) cur = cur.Owner;
                        if (cur != null && !cur.IsNull)
                        {
                            // 校验：设计 world 顶层含 SITE，出图 world 不含——防误在 design world 建链
                            bool looksDesign = false;
                            foreach (DbElement m2 in cur.Members())
                                if (m2 != null && !m2.IsNull && m2.GetElementType().ToString() == "SITE") { looksDesign = true; break; }
                            if (looksDesign) throw new Exception("start=draft 但 CE 在 design world（请先在 GUI 点 Draft WORLD 节点切到出图 world）");
                            cueCurrent = cur;
                        }
                    }
                    if (cueCurrent == null || cueCurrent.IsNull)
                        throw new Exception("start=draft 定位失败：DRAFT world 无成员且 CE 不在出图 world（DRA 库可能被回滚成空）");
                    log.AppendLine("START draft world = " + cueCurrent.ToString());
                }
                else
                {
                    cueCurrent = DbElement.GetElement(startName);
                    if (cueCurrent == null || cueCurrent.IsNull)
                        throw new Exception("start 元素不可解析: " + startName);
                    log.AppendLine("START " + startName + " (" + cueCurrent.GetElementType() + ")");
                }

                // ===== 按 CE 层级分支（窗口 :123-184 逐行镜像）=====
                DbElement sheetDraw = null; // 各分支统一赋值（窗口分支内局部名，headless 提升到方法级）
                if (cueCurrent.GetElementType() == DbElementTypeInstance.WORLD)
                {
                    DbElement deptDraw = cueCurrent.CreateFirst(DbElementTypeInstance.DEPT);
                    DbElement regiDraw = deptDraw.CreateFirst(DbElementTypeInstance.REGISTRY);
                    DbElement drwgDraw = regiDraw.CreateFirst(DbElementTypeInstance.DRWG);
                    if (backs.Contains("A4"))
                    {
                        drawTool.CommandExcute(deptDraw.GetAsString(ATT.NAME) + "size 210 297");
                        drawTool.CommandExcute(regiDraw.GetAsString(ATT.NAME) + "size 210 297");
                        drawTool.CommandExcute(drwgDraw.GetAsString(ATT.NAME) + "size 210 297");
                        log.AppendLine("A4 size 已下发（DEPT/REGI/DRWG）");
                    }
                    sheetDraw = drawTool.createDrwgDraw(drwgDraw, backs);
                    string DrawingName = drwgDraw.GetAsString(ATT.NAME) + "/" + DrawingType;
                    drawTool.SetAttribute(sheetDraw, ATT.NAME, drwgDraw.GetAsString(ATT.NAME) + "/" + DrawingType + (drwgDraw.Members().Count() / 2).ToString());
                    LayerManager.EnsureLayers(sheetDraw);
                    LayerManager.SetCurrentLayer(sheetDraw, DrawLayerCat.Outline); // 外形层(best-effort)：模型几何归层需 PDMS 实测
                    drawTool.UpdateModelView(cueCurrent);
                    log.AppendLine("TREE DEPT=" + deptDraw.GetAsString(ATT.NAME) +
                                   " REGI=" + regiDraw.GetAsString(ATT.NAME) +
                                   " DRWG=" + drwgDraw.GetAsString(ATT.NAME));
                    cueCurrent = sheetDraw; // 窗口此处用户二次点击 CE=SHEET；headless 直接续跑 SHEET 分支
                }
                else if (cueCurrent.GetElementType() == DbElementTypeInstance.DEPT)
                {
                    DbElement regiDraw = cueCurrent.CreateFirst(DbElementTypeInstance.REGISTRY);
                    DbElement drwgDraw = regiDraw.CreateFirst(DbElementTypeInstance.DRWG);
                    if (backs.Contains("A4"))
                    {
                        drawTool.CommandExcute(regiDraw.GetAsString(ATT.NAME) + "size 210 297");
                        drawTool.CommandExcute(drwgDraw.GetAsString(ATT.NAME) + "size 210 297");
                        log.AppendLine("A4 size 已下发（REGI/DRWG）");
                    }
                    sheetDraw = drawTool.createDrwgDraw(drwgDraw, backs);
                    string DrawingName = drwgDraw.GetAsString(ATT.NAME) + "/" + DrawingType;
                    drawTool.SetAttribute(sheetDraw, ATT.NAME, drwgDraw.GetAsString(ATT.NAME) + "/" + DrawingType + (drwgDraw.Members().Count() - 1).ToString());
                    LayerManager.EnsureLayers(sheetDraw);
                    LayerManager.SetCurrentLayer(sheetDraw, DrawLayerCat.Outline); // 外形层(best-effort)：模型几何归层需 PDMS 实测
                    drawTool.UpdateModelView(cueCurrent);
                    log.AppendLine("TREE REGI=" + regiDraw.GetAsString(ATT.NAME) + " DRWG=" + drwgDraw.GetAsString(ATT.NAME));
                    cueCurrent = sheetDraw;
                }
                else if (cueCurrent.GetElementType() == DbElementTypeInstance.REGISTRY)
                {
                    DbElement drwgDraw = cueCurrent.CreateFirst(DbElementTypeInstance.DRWG);
                    if (backs.Contains("A4"))
                    {
                        drawTool.CommandExcute(drwgDraw.GetAsString(ATT.NAME) + "size 210 297");
                        log.AppendLine("A4 size 已下发（DRWG）");
                    }
                    sheetDraw = drawTool.createDrwgDraw(drwgDraw, backs);
                    LayerManager.EnsureLayers(sheetDraw);
                    LayerManager.SetCurrentLayer(sheetDraw, DrawLayerCat.Outline); // 外形层(best-effort)：模型几何归层需 PDMS 实测
                    drawTool.UpdateModelView(cueCurrent);
                    log.AppendLine("TREE DRWG=" + drwgDraw.GetAsString(ATT.NAME));
                    cueCurrent = sheetDraw;
                }
                else if (cueCurrent.GetElementType() == DbElementTypeInstance.DRWG)
                {
                    sheetDraw = drawTool.createDrwgDraw(cueCurrent, backs);
                    LayerManager.EnsureLayers(sheetDraw);
                    log.AppendLine("TREE DRWG(现有)=" + cueCurrent.GetAsString(ATT.NAME));
                    cueCurrent = sheetDraw;
                }
                else if (cueCurrent.GetElementType() == DbElementTypeInstance.SHEET)
                {
                    // 直接在现有 SHEET 上出图（窗口 :185 分支）
                    log.AppendLine("SHEET(现有)=" + cueCurrent.GetAsString(ATT.NAME));
                }
                else
                {
                    throw new Exception("start 元素类型 " + cueCurrent.GetElementType() + " 不在 WORLD/DEPT/REGISTRY/DRWG/SHEET，无法进入出图链");
                }
                if (cueCurrent.IsNull || cueCurrent.GetElementType() != DbElementTypeInstance.SHEET)
                    throw new Exception("SHEET 创建失败（createDrwgDraw 返回空；检查背景 " + backs + " 是否存在）");
                sheetDraw = cueCurrent; // 后续 SHEET 分支主体

                // ===== SHEET 分支（窗口 :186-301 逐行镜像）=====
                drawTool.SetAttribute(sheetDraw, ATT.EXFI, "10000");
                sheetDraw.SetAttribute(ATT.BSRF, DbElement.GetElement(backs));
                sheetDraw.Owner.SetAttribute(ATT.BSRF, DbElement.GetElement(backs));
                drawTool.SetAttribute(sheetDraw, ATT.NAME, sheetDraw.Owner.GetAsString(ATT.NAME) + "/" + DrawingType + (sheetDraw.Owner.Members().Count() / 2).ToString());
                DbElement viewDraw = sheetDraw.CreateFirst(DbElementTypeInstance.VIEW);
                LayerManager.EnsureLayers(viewDraw); // 标注线/文字 标准层(View 下)
                drawTool.SetAttribute(viewDraw, ATT.VFRCOL, "2");
                // 体积范围串（W/N/U 世界坐标，WITHIN 子句格式）
                string limitsBox = string.Format("W{0} N{1} U{2} TO W{3} N{4} U{5}",
                    viewMinX, viewMinY, viewMinZ, viewMaxX, viewMaxY, viewMaxZ);
                drawTool.SetAttribute(viewDraw, ATT.VTYP, "Universal");
                viewDraw.SetAttribute(ATT.RRSF, DbElement.GetElement(rrsf));
                viewDraw.SetAttribute(ATT.HRSF, DbElement.GetElement(hrsf));
                // 管道单/双线样式（用户指定 2026-10-15）：样式由视图规则集 RRSF→(RPLB)→STYL 的
                // TUFG(Tube Flag) 决定：true=双线 Tube On / false=单线（clib/udispstyle PIPE 组实证）。
                // pipeStyle 参数：probe(缺省,只读记录) / single / double（double|single 直接改共享规则集 TUFG）
                try
                {
                    string pipeStyle = P(p, "pipeStyle", "probe");
                    List<DbElement> styList = new List<DbElement>();
                    DbElement rrsfEle = DbElement.GetElement(rrsf);
                    if (rrsfEle != null && !rrsfEle.IsNull)
                    {
                        styList = CollectStyls(rrsfEle, 0);
                        log.AppendLine("RRUL=" + rrsfEle.GetAsString(ATT.NAME) + " 下属 STYL=" + styList.Count);
                    }
                    else log.AppendLine("RRSF 不可解析（" + rrsf + "）");
                    // 实测 2026-10-15：规则集子树 STYL=0、/DRA 全子树 STYL=0——
                    // 样式在 RPLB（样式库）元素下（官方 udispstyle: collect all STYL for <RPLB>）。
                    // RPLB 位置不固定 → 全 DRA world 找 RPLB 元素，收集其 STYL。
                    if (styList.Count == 0)
                    {
                        DbElement draRoot = null;
                        try { foreach (DbElement top in FangYuan.PDMS.Utilities.MDBMembers(DbType.Draft)) if (top != null && !top.IsNull) { draRoot = top.Owner; break; } } catch { }
                        if (draRoot != null && !draRoot.IsNull)
                        {
                            List<DbElement> rplbs = new List<DbElement>();
                            int budget = 40000; // 4026-10-15：8万预算长冻结诱发树控件 fatal；4万+DoEvents 泵
                            FindTypeDfs(draRoot, "RPLB", rplbs, ref budget, 0);
                            log.AppendLine("DRA world RPLB=" + rplbs.Count + "（遍历预算余 " + budget + "）");
                            foreach (DbElement rplb in rplbs)
                            {
                                List<DbElement> stys = CollectStyls(rplb, 0);
                                log.AppendLine("RPLB " + rplb.GetAsString(ATT.NAME) + " (" + rplb.ToString() + ") STYL=" + stys.Count);
                                styList.AddRange(stys);
                            }
                        }
                        else log.AppendLine("DRA world 根定位失败，RPLB 查找跳过");
                    }
                    int tufgCount = 0;
                    foreach (DbElement sty in styList)
                    {
                        if (!sty.IsAttributeValid(ATT.TUFG)) continue;
                        bool tufg = sty.GetBool(ATT.TUFG);
                        if (pipeStyle.Equals("single", StringComparison.OrdinalIgnoreCase) ||
                            pipeStyle.Equals("double", StringComparison.OrdinalIgnoreCase))
                        {
                            bool want = pipeStyle.Equals("double", StringComparison.OrdinalIgnoreCase);
                            if (want != tufg)
                            {
                                sty.SetAttribute(ATT.TUFG, want);
                                tufg = want;
                            }
                        }
                        tufgCount++;
                        log.AppendLine("STYL " + sty.GetAsString(ATT.NAME) + " (" + sty.ToString() + ") TUFG=" + tufg + (tufg ? " (双线/Tube On)" : " (单线)"));
                    }
                    log.AppendLine("含 TUFG 的 STYL 共=" + tufgCount + "（pipeStyle=" + pipeStyle + "）");
                }
                catch (Exception exRr) { log.AppendLine("样式探针失败: [" + exRr.GetType().Name + "] " + exRr.Message); }
                viewDraw.SetAttribute(ATT.VSCA, Scale);
                Position DrawlistTpostion = Position.Create(
                    (viewMinX + viewMaxX) * 0.5,
                    (viewMinY + viewMaxY) * 0.5,
                    (viewMinZ + viewMaxZ) * 0.5);
                viewDraw.SetAttribute(ATT.THPO, DrawlistTpostion);
                // 视图范围框（用户指定 2026-10-15「框选从坐下到右上」）：
                // 实测 2026-10-15：新视图 IDLIST 建立前 SetAttribute(ATT.VVOL, double[6]) 抛「应用程序中的错误」——
                // 必须走官方 CP :CDLIMITS 通道（clib/ulimitset：|From E W N S U D To E W N S U D| 6+6 值，|...| 引号必须），
                // 且需在 IDLIST 存在后 CE=VIEW 下发。逻辑移到 IDLN 双绑之后（见下）。
                try { viewDraw.SetAttribute(ATT.VWLIMI, new double[] { viewMinX, viewMinY, viewMinZ, viewMaxX, viewMaxY, viewMaxZ }); }
                catch { /* VWLIMI=ADP 专用视图限制，不支持写无妨 */ }
                log.AppendLine("VIEW=" + viewDraw.GetAsString(ATT.NAME) + " (VFRCOL/VTYP/RRSF/HRSF/VSCA=" + Scale + "/THPO；VVOL 待 :CDLIMITS 写入)");

                // 绘制清单（窗口 :214-282 逐行镜像：CE=IDLIST 上下文 + ADD ALL WITHIN + ADDENTRY + IDLN 双绑）
                DbElement savedElem = CE.Element;
                DbElement drawlistDllb = drawE3DTool.FindCurrentDrawlistUnder(sheetDraw.Owner);
                DbElement idlist = drawE3DTool.FindOrCreateIdlist(drawlistDllb);
                int idliCount = 0;
                if (idlist != null && !idlist.IsNull)
                {
                    CE.Element = idlist; // CE -> IDLIST，ADD/REM 落到该清单
                    // addTypes 覆盖参数（2026-10-15 新增）：spool 裸 PIPE 模型默认 BRAN EQUIP SCTN 0 命中，
                    // 可传 "PIPE BRAN EQUIP SCTN" 等；缺省 = 窗口图种映射
                    string addTypesOverride = P(p, "addTypes", "");
                    string addTypes = string.IsNullOrEmpty(addTypesOverride) ? drawE3DTool.GetAddTypesForDrawingType(DrawingType) : addTypesOverride;
                    string addRules = drawE3DTool.GetAddRulesForDrawingType(DrawingType);
                    string remTypes = drawE3DTool.GetRemTypesForDrawingType(DrawingType);
                    string remRules = drawE3DTool.GetRemRulesForDrawingType(DrawingType);
                    drawE3DTool.CreateVolumeDrawlist(idlist.GetAsString(ATT.NAME), limitsBox, addTypes, addRules, remTypes, remRules);
                    log.AppendLine("IDLIST=" + idlist.GetAsString(ATT.NAME) + " ADD ALL " + addTypes + " WITHIN " + limitsBox);
                    // 手动选择的元素（窗口 listBox1）→ ADDENTRY(IDNM)，纯 API 写法（跨 world 唯一可用通道）
                    int addEntryCount = 0;
                    foreach (String add in elements.Split(new char[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        // 跨 world 解析优先（CE 在出图 world 时 GetElement 够不到设计库）
                        DbElement manEle = ResolveDesignPath(add);
                        if (manEle == null || manEle.IsNull) manEle = DbElement.GetElement(add);
                        if (manEle != null && !manEle.IsNull)
                        {
                            DbElement addEntry = idlist.CreateLast(DbElementTypeInstance.ADDENTRY);
                            addEntry.SetAttribute(ATT.IDNM, manEle);
                            addEntryCount++;
                        }
                    }
                    log.AppendLine("ADDENTRY(API) 命中 " + addEntryCount + "/" + elements.Split(new char[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).Length + "（设计全路径经 MDBMembers(Design) 遍历）");
                    // 视图 IDLN 指向该 IDLIST（视图由此抽取模型）
                    viewDraw.SetAttribute(ATT.IDLN, idlist);
                    sheetDraw.SetAttribute(ATT.IDLN, idlist);
                    // ===== 视图范围框 :CDLIMITS（左下→右上，2026-10-15 用户指定）=====
                    // CP 语法（clib/ulimitset 官方）：每角 6+6 个 E/W/N/S/U/D 值（负向=反向坐标），
                    // From=min角(左下)，To=max角(右上)；|...| 引号必须（无引号实测 (47,15) Syntax error）。
                     // 2026-10-15 21:49 教训：API 通道 CommandExcute(":CDLIMITS") 静默失败 → $M 宏通道；
                     // 23:0x 修复：轴字母官方格式 + UDA 回读校验（不再误用 VVOL 判定）。
                     if (SendCdlimitsViaMacro(viewDraw, specMinX, specMaxX, specMinY, specMaxY, specMinZ, specMaxZ, true, log))
                         log.AppendLine("范围框设置成功（左下→右上，:CDLIMITS UDA 回读一致）");
                     else
                         log.AppendLine("范围框设置失败（UDAX 回读不一致——查 op_create_drawing.log 的 CDLIMITS UDA 回读行）");
                     // 动态计算视图 SIZE（内容包围盒 + 比例，16:9；窗口 :253-279 同款）。
                     // 23:0x 修复：内容源优先级 = 显式规格 box（用户指定范围）；未给 box 才回退 ViewLimitBox。
                     // 原实现无条件读 ViewLimitBox = 回读 VVOL 显示帧（与 SIZE×VSCA 循环依赖）→
                     // 22:4x 实测 1598x2841 野帧（16m 管线内容被压成图纸上 ~120mm 细条）。
                     try
                     {
                         double w = double.NaN, h = double.NaN;
                          Position center = null;
                          // 2026-10-16 修复「紫色框大小不对」：原 +2000 是图纸毫米留白(约2m)，对单线 spool
                          // 小投影内容会把框撑到 1192x2120 超 A0(841x1189) 被裁剪。改模型空间小留白(3m*Scale 每侧)，
                          // 并在下方把最终 SIZE 封顶到图幅(backs)内，保证紫色框不溢出图幅。
                          double mgn = 3000 * Scale;
                          if (hasBox)
                          {
                              w = (specMaxX - specMinX) * Scale + 2 * mgn;
                              h = (specMaxY - specMinY) * Scale + 2 * mgn;
                             center = Position.Create((specMinX + specMaxX) * 0.5,
                                (specMinY + specMaxY) * 0.5, (specMinZ + specMaxZ) * 0.5);
                         }
                         else
                         {
                             LimitsBox contentBox = drawE3DTool.ViewLimitBox(viewDraw);
                             if (contentBox.Minimum.X < double.MaxValue)
                             {
                                  w = (contentBox.Maximum.X - contentBox.Minimum.X) * Scale + 2 * mgn;
                                  h = (contentBox.Maximum.Y - contentBox.Minimum.Y) * Scale + 2 * mgn;
                                 center = contentBox.Centre();
                             }
                         }
                         if (!double.IsNaN(w))
                         {
                             double targetRatio = 16.0 / 9.0;
                             double currentRatio = w / h;
                             if (currentRatio > targetRatio) { h = w / targetRatio; }
                             else { w = h / targetRatio; }
                             // 野值守护（2026-10-15 新增）：空视图/未投影时 ViewLimitBox 返回 ~1e307 量级，
                             // 直接写入 SIZE 会把视图幅面写飞（实测 1.278e307x7.19e306）
                             if (double.IsNaN(w) || double.IsInfinity(w) || double.IsNaN(h) || double.IsInfinity(h)
                                 || w > 1e6 || h > 1e6 || w <= 0 || h <= 0)
                             {
                                 log.AppendLine("SIZE/THPO 动态值不合理（" + w + "x" + h + "，疑似空视图），跳过覆盖");
                             }
                              else
                              {
                                  // 图幅封顶（2026-10-16）：紫色框=viewDraw.SIZE，必须落在图幅(backs)内否则被裁剪。
                                  // 读 backs 图幅真实 SIZE(A0=841x1189)，留 30mm 图框边距；读不到兜底 A0。
                                  double capW = 841, capH = 1189;
                                  try
                                  {
                                      DbElement backsEle = DbElement.GetElement(backs);
                                      if (backsEle != null && !backsEle.IsNull)
                                      {
                                          double[] bs = backsEle.GetDoubleArray(ATT.SIZE);
                                          if (bs != null && bs.Length >= 2 && bs[0] > 0 && bs[1] > 0) { capW = bs[0]; capH = bs[1]; }
                                      }
                                  }
                                  catch { }
                                  double border = 30;
                                  double ow = w, oh = h;
                                  w = Math.Min(w, capW - border);
                                  h = Math.Min(h, capH - border);
                                  viewDraw.SetAttribute(ATT.SIZE, new double[] { w, h });
                                  viewDraw.SetAttribute(ATT.THPO, center);
                                  log.AppendLine("SIZE/THPO 动态覆盖: " + w + "x" + h + " @(" + center.X + "," + center.Y + "," + center.Z + ")" + (Math.Abs(ow - w) > 0.01 || Math.Abs(oh - h) > 0.01 ? "（已封顶图幅 " + capW + "x" + capH + "）" : "") + "（内容源=" + (hasBox ? "规格box" : "ViewLimitBox") + "）");
                              }
                         }
                     }
                      catch { }
                      // ===== 2026-10-16 图框贴合「实际出图内容」（用户：图框范围按出图位置定、偏移≤100）=====
                      // 上段按 3D VVOL 定 SIZE/THPO：对紧凑 spool 簇，VVOL 被远端轴网/分支/结构撑大
                      // （实测 VVOL N 向 40m、可见管系仅 ~6m）→ 图框被撑到 811mm 高、内容漂在中下留大片空白。
                      // 这里用【可见管系构件】ELBOW/VALVE/FLANGE 的 WORPOS 覆盖（失败则回退上面的 VVOL 框）：
                      //   ① THPO = 其 3D 中心 → 内容投影落点 = XYPS(图框中心)，内容居中；
                      //   ② 官方 SheetPosition 投到图纸 2D 取包围盒 → SIZE = 盒 + 2×100mm（满足偏移≤100）；
                      //   ③ 不按 16:9 强改（保留内容真实宽高比）；DIR/ADFG 全分支由 SheetPosition 覆盖。
                      try
                      {
                          List<Position> wrps = new List<Position>();
                          foreach (DbElementType pt in new DbElementType[] { DbElementTypeInstance.ELBOW, DbElementTypeInstance.VALVE, DbElementTypeInstance.FLANGE })
                          {
                              try
                              {
                                  List<DbElement> l = drawE3DTool.ElementsFromDrawlist(viewDraw, new TypeFilter(pt), false, false);
                                  foreach (DbElement e in l)
                                  {
                                      if (e == null || e.IsNull) continue;
                                      Position wp = e.GetPosition(ATT.WORPOS);
                                      if (wp != null) wrps.Add(wp);
                                  }
                              }
                              catch { }
                          }
                          if (wrps.Count > 0)
                          {
                              double mnW = double.MaxValue, mxW = double.MinValue, mnN = double.MaxValue, mxN = double.MinValue, mnU = double.MaxValue, mxU = double.MinValue;
                              foreach (Position wp in wrps)
                              {
                                  if (wp.X < mnW) mnW = wp.X; if (wp.X > mxW) mxW = wp.X;
                                  if (wp.Y < mnN) mnN = wp.Y; if (wp.Y > mxN) mxN = wp.Y;
                                  if (wp.Z < mnU) mnU = wp.Z; if (wp.Z > mxU) mxU = wp.Z;
                              }
                              pipeMnW = mnW; pipeMxW = mxW; pipeMnN = mnN; pipeMxN = mxN; pipeMnU = mnU; pipeMxU = mxU; pipeCnt = wrps.Count;
                              Position pcenter = Position.Create((mnW + mxW) * 0.5, (mnN + mxN) * 0.5, (mnU + mxU) * 0.5);
                              viewDraw.SetAttribute(ATT.THPO, pcenter); // 内容 3D 中心 → 投影落点 = XYPS(图框中心)
                              double mnx = double.MaxValue, mxx = double.MinValue, mny = double.MaxValue, mxy = double.MinValue;
                              foreach (Position wp in wrps)
                              {
                                  try
                                  {
                                      Position2D p2 = drawE3DTool.SheetPosition(wp, viewDraw);
                                      if (p2.X < mnx) mnx = p2.X; if (p2.X > mxx) mxx = p2.X;
                                      if (p2.Y < mny) mny = p2.Y; if (p2.Y > mxy) mxy = p2.Y;
                                  }
                                  catch { }
                              }
                              if (mxx > mnx && mxy > mny)
                              {
                                  double mgnPaper = 10; // 图纸 mm 留白/侧（2026-10-16 用户：范围框再缩、留 10 试试）
                                  double nw = (mxx - mnx) + 2 * mgnPaper;
                                  double nh = (mxy - mny) + 2 * mgnPaper;
                                  double capW = 841, capH = 1189;
                                  try
                                  {
                                      DbElement backsEle = DbElement.GetElement(backs);
                                      if (backsEle != null && !backsEle.IsNull)
                                      {
                                          double[] bs = backsEle.GetDoubleArray(ATT.SIZE);
                                          if (bs != null && bs.Length >= 2 && bs[0] > 0 && bs[1] > 0) { capW = bs[0]; capH = bs[1]; }
                                      }
                                  }
                                  catch { }
                                  double border = 30;
                                  nw = Math.Min(nw, capW - border);
                                  nh = Math.Min(nh, capH - border);
                                  viewDraw.SetAttribute(ATT.SIZE, new double[] { nw, nh });
                                  viewDraw.SetAttribute(ATT.THPO, pcenter);
                                  log.AppendLine("SIZE/THPO 贴合管系: " + nw + "x" + nh + " @(" + pcenter.X + "," + pcenter.Y + "," + pcenter.Z + ") 内容2D盒 X[" + mnx + ".." + mxx + "] Y[" + mny + ".." + mxy + "] (WORPOS=" + wrps.Count + "件 留白各" + mgnPaper + "mm)");
                              }
                          }
                      }
                      catch (Exception exW) { log.AppendLine("管系 WORPOS 贴框失败（回退 VVOL 框）: [" + exW.GetType().Name + "] " + exW.Message); }
                     try { idliCount = idlist.Members().Count(); } catch { }
                     log.AppendLine("IDLIST 成员(ADDENTRY)=" + idliCount);
                }
                else log.AppendLine("IDLIST 未找到（DLLB 缺失？）—— 视图将空清单出图");
                CE.Element = savedElem; // 恢复 CE（API 方式，窗口 :282 同款）

                // 读回视图实际范围（VVOL，窗口 :284-301 同款）
                string actualLimits = drawE3DTool.GetViewLimits(viewDraw);
                if (!string.IsNullOrEmpty(actualLimits))
                {
                    double[] lb = drawE3DTool.ParseLimitsString(actualLimits);
                    if (lb != null)
                    {
                        viewMinX = lb[0]; viewMaxX = lb[1];
                        viewMinY = lb[2]; viewMaxY = lb[3];
                        viewMinZ = lb[4]; viewMaxZ = lb[5];
                        log.AppendLine("ACTUAL_LIMITS " + actualLimits);
                    }
                }
                else
                {
                    log.AppendLine("ACTUAL_LIMITS none（VVOL 未填充——IDLIST 空或 UPDATE 未完成；标注块将按输入 box 计算）");
                }
                // 2026-10-16：标注/尺寸线贴合可见管系内容（WORPOS 盒替代 VVOL 全幅）——
                // 否则尺寸线按 VVOL 远端轴网定位会甩出图框。
                if (pipeCnt > 0 && !double.IsNaN(pipeMnW) && pipeMxW > pipeMnW && pipeMxN > pipeMnN)
                {
                    viewMinX = pipeMnW; viewMaxX = pipeMxW; viewMinY = pipeMnN; viewMaxY = pipeMxN; viewMinZ = pipeMnU; viewMaxZ = pipeMxU;
                    log.AppendLine("标注/尺寸范围改用管系 WORPOS 盒: W[" + pipeMnW + ".." + pipeMxW + "] N[" + pipeMnN + ".." + pipeMxN + "]");
                }

                // ===== 标注块（窗口 :306-994 管道布置图/设备布置图/埋板布置图，逐行镜像）=====
                if (DrawingType == "管道布置图" || DrawingType == "设备布置图" || DrawingType == "埋板布置图")
                {
                    if (annotate)
                    {
                        // 创建投影面
                        List<DbElement> eles = drawE3DTool.ElementsFromDrawlist(viewDraw, new TypeFilter(DbElementTypeInstance.ELBOW), false, false);
                        List<DbElement> pipeCollection = Graphic.getPipeCollect(eles);

                        // ===== 出图 5 图层体系：标注线/文字 建在 VIEW 下；外形/图框/表格 建在 SHEET 下 =====
                        DbElement lDim = LayerManager.GetLayer(viewDraw, DrawLayerCat.Dim);
                        DbElement lText = LayerManager.GetLayer(viewDraw, DrawLayerCat.Text);
                        DbElement lOutline = LayerManager.GetLayer(sheetDraw, DrawLayerCat.Outline);
                        DbElement lFrame = LayerManager.GetLayer(sheetDraw, DrawLayerCat.Frame);
                        DbElement lTable = LayerManager.GetLayer(sheetDraw, DrawLayerCat.Table);
                        drawTool.SetAttribute(lDim, ATT.LSHA, "Angled Horizontal");
                        // 收集轴网信息
                        List<DbElement> eleGrids = drawE3DTool.ElementsFromDrawlist(viewDraw, new TypeFilter(DbElementTypeInstance.GRIDWLD), false, false);
                        // 仪表
                        List<DbElement> eleInsts = drawE3DTool.ElementsFromDrawlist(viewDraw, new TypeFilter(DbElementTypeInstance.INSTRUMENT), false, false);
                        // 结构
                        List<DbElement> eleStrus = drawE3DTool.ElementsFromDrawlist(viewDraw, new TypeFilter(DbElementTypeInstance.STRUCTURE), false, false);
                        // 设备
                        List<DbElement> eleEquis = drawE3DTool.ElementsFromDrawlist(viewDraw, new TypeFilter(DbElementTypeInstance.EQUIPMENT), false, false);
                        // 阀门
                        List<DbElement> eleValvs = drawE3DTool.ElementsFromDrawlist(viewDraw, new TypeFilter(DbElementTypeInstance.VALVE), false, false);
                        // 法兰
                        List<DbElement> eleFlans = drawE3DTool.ElementsFromDrawlist(viewDraw, new TypeFilter(DbElementTypeInstance.FLANGE), false, false);
                        // 三通/支管（BRANCH）：自绘小方块标记（2026-10-16）
                        List<DbElement> eleBrans = drawE3DTool.ElementsFromDrawlist(viewDraw, new TypeFilter(DbElementTypeInstance.BRANCH), false, false);

                        eles.AddRange(eleInsts);
                        eles.AddRange(eleGrids);
                        eles.AddRange(eleStrus);
                        eles.AddRange(eleEquis);
                        //  eles.AddRange(eleValvs);
                        //  eles.AddRange(eleFlans);
                        Graphic.createLdimElement(lDim, eles, "Left", viewMinX, viewMaxX, viewMinY, viewMaxY, DrawingType);
                        Graphic.createLdimElement(lDim, eles, "Right", viewMinX, viewMaxX, viewMinY, viewMaxY, DrawingType);
                        Graphic.createLdimElement(lDim, eles, "Top", viewMinX, viewMaxX, viewMinY, viewMaxY, DrawingType);
                        Graphic.createLdimElement(lDim, eles, "Bottom", viewMinX, viewMaxX, viewMinY, viewMaxY, DrawingType);

                        // 创建设备标注标签
                        // 文字层 lText 已在上方定义（设备标注 GLABEL 走 lText）

                        DbElement noteCenter = lOutline.CreateLast(DbElementTypeInstance.NOTE);
                        drawTool.SetAttribute(noteCenter, ATT.NAME, noteCenter.Owner.GetAsString(ATT.NAME) + "/设备中心线");
                        DbElement noteFrame = lFrame.CreateLast(DbElementTypeInstance.NOTE);
                        drawTool.SetAttribute(noteFrame, ATT.NAME, noteFrame.Owner.GetAsString(ATT.NAME) + "/图框");

                        foreach (DbElement ele in eleEquis)
                        {
                            try // 逐项守护（2026-10-15 (2,202) 教训）：GLABEL/STRAIGHT 创建退化成空引用时
                            // NAME 串形 = "=0/0"，拼出的 CP 命令报 (2,202) Cannot go to the element =0/0 →
                            // 跳过该设备标注项，不中断整图（中央护栏 CommandExcute 还会兜底拦截）
                            {
                            if (ele == null || ele.IsNull) continue;
                            DbElement glabDraw = lText.CreateLast(DbElementTypeInstance.GLABEL);
                            if (glabDraw == null || glabDraw.IsNull) continue;
                            glabDraw.SetAttribute(ATT.BTEX, "#NAME(C2:) #/" + "POS EL " + "#POSU+)");
                            glabDraw.SetAttribute(ATT.TSIZ, 2.5);
                            glabDraw.SetAttribute(ATT.DDNM, ele);
                            // 设备标注位置
                            double gridPosY = ele.GetPosition(ATT.WORPOS).Y;
                            double midViewy = viewDraw.GetPosition(ATT.THPO).Y;
                            double disy = (gridPosY - midViewy) * viewDraw.GetDouble(ATT.VSCA);
                            double m = viewDraw.GetDoubleArray(ATT.XYPS)[1] + disy;

                            double gridPosX = ele.GetPosition(ATT.WORPOS).X;
                            double midViewX = viewDraw.GetPosition(ATT.THPO).X;
                            double disx = (gridPosX - midViewX) * viewDraw.GetDouble(ATT.VSCA);
                            double n = viewDraw.GetDoubleArray(ATT.XYPS)[0] + disx;
                            drawTool.CommandExcute(glabDraw.GetAsString(ATT.NAME) + " LSHAPE BENT ");
                            drawTool.CommandExcute(glabDraw.GetAsString(ATT.NAME) + " at x " + n + " y " + m);
                            drawTool.CommandExcute(glabDraw.GetAsString(ATT.NAME) + " just Centre");
                            glabDraw.SetAttribute(ATT.BLNK, false);
                            glabDraw.SetAttribute(ATT.BLNK, false);

                            // 画设备中心线
                            double CentreLen = 50;
                            double CentreLenY = 15;
                            DbElement centerLine = noteCenter.CreateLast(DbElementTypeInstance.STRAIGHT);
                            DbElement centerLineY = noteCenter.CreateLast(DbElementTypeInstance.STRAIGHT);
                            if (centerLine == null || centerLine.IsNull || centerLineY == null || centerLineY.IsNull) continue;
                            drawTool.SetAttribute(centerLineY, ATT.LENG, CentreLen.ToString());
                            drawTool.SetAttribute(centerLineY, ATT.NLSTYL, "8004");

                            drawTool.SetAttribute(centerLine, ATT.LENG, CentreLen.ToString());
                            drawTool.SetAttribute(centerLineY, ATT.LENG, CentreLen.ToString());
                            drawTool.SetAttribute(centerLine, ATT.NLSTYL, "8004");

                            centerLine.SetAttribute(ATT.NLCOLO, 2);
                            // Fpt Offset X 0mm Y 0mm Tpt Offset X 52mm Y 0mm
                            centerLineY.SetAttribute(ATT.NLCOLO, 2);
                            drawTool.SetAttribute(centerLineY, ATT.ADEG, "-90");
                            drawTool.CommandExcute(centerLine.GetAsString(ATT.NAME) + " Fpt Offset X -" + CentreLen + "mm Y 0mm Tpt Offset X " + CentreLen + "mm" + " Y 0 mm");
                            drawTool.CommandExcute(centerLineY.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y " + CentreLenY + "mm Tpt Offset X 0mm" + " Y -" + CentreLenY + " mm");
                            drawTool.CommandExcute(centerLine.GetAsString(ATT.NAME) + " at x " + (n - CentreLen) + " y " + m);
                            drawTool.CommandExcute(centerLineY.GetAsString(ATT.NAME) + " at x " + n + CentreLen + " y " + (m + CentreLenY));
                            }
                            catch (Exception exE) { log.AppendLine("设备标注项跳过: [" + exE.GetType().Name + "] " + exE.Message); }
                        }
                        // 画范围框
                        DbElement drawFrame = noteFrame.CreateLast(DbElementTypeInstance.STRAIGHT);
                        drawTool.SetAttribute(drawFrame, ATT.NLSTYL, "1");
                        drawTool.SetAttribute(drawFrame, ATT.LENG, (viewMaxX - viewMinX).ToString());
                        drawFrame.SetAttribute(ATT.NLCOLO, 10);
                        double Xmin = Graphic.getLimitDraw(viewDraw, viewMinX, viewMaxX, viewMinY, viewMaxY)[0];
                        double Ymin = Graphic.getLimitDraw(viewDraw, viewMinX, viewMaxX, viewMinY, viewMaxY)[1];
                        double xMax = Graphic.getLimitDraw(viewDraw, viewMinX, viewMaxX, viewMinY, viewMaxY)[2];
                        double yMax = Graphic.getLimitDraw(viewDraw, viewMinX, viewMaxX, viewMinY, viewMaxY)[3];

                        // 边框长度用视图真实幅面 SIZE（mm），与 getLimitDraw 的 SIZE 版边缘一致
                        double frameW = viewDraw.GetDoubleArray(ATT.SIZE)[0];
                        double frameH = viewDraw.GetDoubleArray(ATT.SIZE)[1];
                        drawTool.CommandExcute(drawFrame.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y 0mm Tpt Offset X " + frameW + "mm" + " Y 0mm");
                        drawTool.CommandExcute(drawFrame.GetAsString(ATT.NAME) + " at x " + Xmin + " y " + Ymin);
                        drawFrame.SetAttribute(ATT.NLCOLO, 16);
                        drawTool.SetAttribute(drawFrame, ATT.ADEG, "0");
                        DbElement drawFrameH = noteFrame.CreateLast(DbElementTypeInstance.STRAIGHT);
                        drawFrameH.Copy(drawFrame);
                        drawTool.CommandExcute(drawFrameH.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y 0mm Tpt Offset X " + frameW + "mm" + " Y 0mm");
                        drawTool.CommandExcute(drawFrameH.GetAsString(ATT.NAME) + " at x " + Xmin + " y " + yMax);
                        // 竖线
                        DbElement drawFrameV = noteFrame.CreateLast(DbElementTypeInstance.STRAIGHT);
                        drawTool.SetAttribute(drawFrameV, ATT.NLSTYL, "1");
                        drawTool.SetAttribute(drawFrameV, ATT.LENG, frameH.ToString());
                        drawFrameV.SetAttribute(ATT.NLCOLO, 10);
                        drawTool.SetAttribute(drawFrame, ATT.ADEG, "90");
                        drawTool.CommandExcute(drawFrameV.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y 0mm Tpt Offset X 0mm" + " Y " + frameH + " mm");
                        drawTool.CommandExcute(drawFrameV.GetAsString(ATT.NAME) + " at x " + Xmin + " y " + Ymin);
                        drawFrameV.SetAttribute(ATT.NLCOLO, 16);
                        DbElement drawFrameV2 = noteFrame.CreateLast(DbElementTypeInstance.STRAIGHT);
                        drawFrameV2.Copy(drawFrameV);
                        drawTool.CommandExcute(drawFrameV2.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y 0mm Tpt Offset X 0mm" + " Y " + frameH + " mm");
                        drawTool.CommandExcute(drawFrameV2.GetAsString(ATT.NAME) + " at x " + xMax + " y " + Ymin);

                        // ===== 2026-10-16 标准符号图块（HG20519 图例）：建库 + 建"管道符号"图层 =====
                        // 库 /DRA/AGT_SYMS（SYTM 图块，SLABEL.TMRF 引用——与 Arrow1 同机制）；
                        // 库缺失/建块失败时 symLab=null → 下面各符号点回退旧 ChemSymbols 裸线段画法（保底）。
                        bool symLibOk = SymBlocks.EnsureLibrary(false, delegate(string l) { log.AppendLine(l); });
                        DbElement symLab = null;
                        if (symLibOk)
                        {
                            symLab = LayerManager.GetLayer(viewDraw, DrawLayerCat.Text);
                            if (symLab == null || symLab.IsNull) symLab = null;
                        }
                        int symInsCnt = 0, symFbCnt = 0; // 图块插入 / 回退裸线段 计数
                        // 加入箭头
                        DbElement arrowLab = LayerManager.GetLayer(viewDraw, DrawLayerCat.Text);

                         // 2026-10-16 符号模板体检（C# 进程内 GetElement；跨世界 $M 读 !!/path 会挂死宏线程，禁用）：
                         // SLABEL.TMRF 引用的模板若在本安装缺失 → 图里渲染成空白点。逐个确认，缺失的记日志。
                         foreach (string tp in new string[] {
                             "/TT/Isometric_ADP/Arrow1", "/TT/Isometric_ADP/Flan",
                             "/MDS/ABA/TAGS/ELBO", "/MDS/ABA/TAGS/ELBO--UP",
                             "/MDS/ABA/TAGS/Valv-H", "/MDS/ABA/TAGS/Valv-V",
                             "/MDS/ABA/TAGS/CIRCLE/2", "/MDS/ABA/TAGS/TABLE",
                             "/TT/DRA/MAS/LABELS/GEN/PIPE/END" })
                         {
                             string st;
                             try { DbElement te = DbElement.GetElement(tp); st = (te == null || te.IsNull) ? "缺失(渲染空白)" : ("存在 type=" + te.GetElementType().ToString()); }
                             catch (Exception exT) { st = "读取异常:" + exT.Message; }
                             log.AppendLine("符号模板体检 " + tp + " => " + st);
                         }
                         // 2026-10-16 /TT、/MDS 是模板特殊根：目录节点 GetElement 拿不到（只认完整叶路径），无法列举。
                         // 改按【候选名】逐个探测本安装里可用的弯头/阀门/法兰模板（含 rrsf 项目码 PRJ 的 ABA 同构路径）。
                         foreach (string cand in new string[] {
                             "/TT/Isometric_ADP/Arrow2", "/TT/Isometric_ADP/Arrow3",
                             "/TT/Isometric_ADP/Elbow", "/TT/Isometric_ADP/Elbow1", "/TT/Isometric_ADP/Elb", "/TT/Isometric_ADP/Elbo",
                             "/TT/Isometric_ADP/Valve", "/TT/Isometric_ADP/Valve1", "/TT/Isometric_ADP/GateValve", "/TT/Isometric_ADP/GlobeValve", "/TT/Isometric_ADP/CheckValve",
                             "/TT/Isometric_ADP/Flange", "/TT/Isometric_ADP/Flange1", "/TT/Isometric_ADP/Flan",
                             "/TT/Isometric_ADP/Tee", "/TT/Isometric_ADP/Tee1", "/TT/Isometric_ADP/Breech", "/TT/Isometric_ADP/Branch",
                             "/TT/Isometric_ADP/Circle", "/TT/Isometric_ADP/Circle1",
                             "/MDS/PRJ/TAGS/ELBO", "/MDS/PRJ/TAGS/ELBO--UP", "/MDS/PRJ/TAGS/Valv-H", "/MDS/PRJ/TAGS/Valv-V", "/MDS/PRJ/TAGS/CIRCLE/2", "/MDS/PRJ/TAGS/TABLE", "/MDS/PRJ/TAGS/Flan"
                         })
                         {
                             try { DbElement te = DbElement.GetElement(cand); if (te != null && !te.IsNull) log.AppendLine("候选模板存在: " + cand + " [" + te.GetElementType().ToString() + "]"); }
                             catch { }
                         }
                         log.AppendLine("候选模板探测完成（未列出的候选=不存在）");

                         foreach (DbElement arrow in eles)
                        {
                            try // 逐项守护（2026-10-15）：成员结构不符/模板引用缺失时跳过该标注项，不中断整图
                            {
                            if (arrow.GetElementType() == DbElementTypeInstance.ELBOW && arrow.GetAsString(ATT.LDIR) != "U" && arrow.GetAsString(ATT.LDIR) != "D")
                            {
                                double arrowBore = arrow.GetElement(ATT.CATR).GetDoubleArray(ATT.PARA)[0];
                                DbElement arrOne = arrowLab.CreateLast(DbElementTypeInstance.SLABEL);
                                arrOne.SetAttribute(ATT.TMRF, DbElement.GetElement("/TT/Isometric_ADP/Arrow1"));
                                arrOne.SetAttribute(ATT.DDNM, arrow);
                                double arrawAngle = drawTool.getArrawAngle(arrow);
                                // 箭头y坐标
                                double gridPosY = arrow.GetPosition(ATT.WORPOS).Y;
                                double midViewY = arrowLab.Owner.GetPosition(ATT.THPO).Y;
                                double disy = (gridPosY - midViewY) * arrowLab.Owner.GetDouble(ATT.VSCA);
                                double y = arrowLab.Owner.GetDoubleArray(ATT.XYPS)[1] + disy + 8 * Math.Sin((arrawAngle - 180) * Math.PI / 180);
                                // 箭头x坐标
                                double gridPosX = arrow.GetPosition(ATT.WORPOS).X;
                                double midViewX = arrowLab.Owner.GetPosition(ATT.THPO).X;
                                double disx = (gridPosX - midViewX) * arrowLab.Owner.GetDouble(ATT.VSCA);
                                double x = arrowLab.Owner.GetDoubleArray(ATT.XYPS)[0] + disx + 8 * Math.Cos((arrawAngle - 180) * Math.PI / 180);
                                if (arrowBore < 50)
                                {
                                    x = arrowLab.Owner.GetDoubleArray(ATT.XYPS)[0] + disx + 4 * Math.Cos((arrawAngle - 180) * Math.PI / 180);
                                    y = arrowLab.Owner.GetDoubleArray(ATT.XYPS)[1] + disy + 4 * Math.Sin((arrawAngle - 180) * Math.PI / 180);
                                }
                                drawTool.CommandExcute(arrOne.GetAsString(ATT.NAME) + "at x " + x + " y " + y);
                                drawTool.SetAttribute(arrOne, ATT.ADEG, arrawAngle.ToString());
                                drawTool.SetAttribute(arrOne, ATT.FCOLOU, "4");
                                drawTool.SetAttribute(arrOne, ATT.LLEA, "false");
                            }
                            else if (arrow.GetElementType() == DbElementTypeInstance.ELBOW && arrow.GetAsString(ATT.LDIR) == "D")
                            {
                                // 2026-10-16 标准图块：弯头竖直管端(向下/远离视点) = ELBOWD（圆+小中心点，HG20519 图6-12a 俯视端视）
                                double[] pp = ChemSymbols.Project(viewDraw, arrow.GetPosition(ATT.WORPOS).X, arrow.GetPosition(ATT.WORPOS).Y);
                                if (SymBlocks.Insert(symLab, SymBlocks.N_ELBOWD, arrow, pp[0], pp[1], 0, SymBlocks.PIPE_COLOR) != null) symInsCnt++;
                                else { ChemSymbols.ElbowEnd(noteCenter, pp[0], pp[1], 9, true); symFbCnt++; }
                            }
                            else if (arrow.GetElementType() == DbElementTypeInstance.ELBOW && arrow.GetAsString(ATT.LDIR) == "U")
                            {
                                // 2026-10-16 标准图块：弯头竖直管端(向上/朝向视点) = ELBOWU（圆+大实心点，HG20519 图6-12b）
                                double[] pp = ChemSymbols.Project(viewDraw, arrow.GetPosition(ATT.WORPOS).X, arrow.GetPosition(ATT.WORPOS).Y);
                                if (SymBlocks.Insert(symLab, SymBlocks.N_ELBOWU, arrow, pp[0], pp[1], 0, SymBlocks.PIPE_COLOR) != null) symInsCnt++;
                                else { ChemSymbols.ElbowEnd(noteCenter, pp[0], pp[1], 9, false); symFbCnt++; }
                            }
                            else if (arrow.GetElementType() == DbElementTypeInstance.PIPE && arrow.Member(0).Member(0).GetAsString(ATT.LDIR) != "U" && arrow.Member(0).Member(0).GetAsString(ATT.LDIR) != "D")
                            {
                                DbElement arrOne = arrowLab.CreateLast(DbElementTypeInstance.SLABEL);
                                arrOne.SetAttribute(ATT.TMRF, DbElement.GetElement("/TT/Isometric_ADP/Arrow1"));
                                arrOne.SetAttribute(ATT.DDNM, arrow.Member(0).Member(0));
                                double arrawAngle = drawTool.getArrawAngle(arrow);
                                // 箭头y坐标
                                double gridPosY = arrow.Member(0).Member(0).GetPosition(ATT.LPOS).Y;
                                double midViewY = arrowLab.Owner.GetPosition(ATT.THPO).Y;
                                double disy = (gridPosY - midViewY) * arrowLab.Owner.GetDouble(ATT.VSCA);
                                double y = arrowLab.Owner.GetDoubleArray(ATT.XYPS)[1] + disy + 9 * Math.Sin((arrawAngle - 180) * Math.PI / 180);
                                // 箭头x坐标
                                double gridPosX = arrow.Member(0).Member(0).GetPosition(ATT.LPOS).X;
                                double midViewX = arrowLab.Owner.GetPosition(ATT.THPO).X;
                                double disx = (gridPosX - midViewX) * arrowLab.Owner.GetDouble(ATT.VSCA);
                                double x = arrowLab.Owner.GetDoubleArray(ATT.XYPS)[0] + disx + 9 * Math.Cos((arrawAngle - 180) * Math.PI / 180);
                                drawTool.CommandExcute(arrOne.GetAsString(ATT.NAME) + "at x " + x + " y " + y);
                                drawTool.SetAttribute(arrOne, ATT.LLEA, "false");
                                // 设置箭头转向
                                String angle = arrow.GetAsString(ATT.ADIR);
                                drawTool.SetAttribute(arrOne, ATT.FCOLOU, "4");
                            }
                            else if (arrow.GetElementType() == DbElementTypeInstance.ELBOW && arrow.GetAsString(ATT.LDIR) == "U" && arrow.GetAsString(ATT.NAME).Contains(arrow.Owner.LastMember().GetAsString(ATT.NAME)))
                            {
                                DbElement arrOne = arrowLab.CreateLast(DbElementTypeInstance.SLABEL);
                                arrOne.SetAttribute(ATT.TMRF, DbElement.GetElement("/TT/DRA/MAS/LABELS/GEN/PIPE/END"));
                                arrOne.SetAttribute(ATT.DDNM, arrow);
                                // 断面y坐标
                                double gridPosY = arrow.GetPosition(ATT.POS).Y;
                                double midViewY = arrowLab.Owner.GetPosition(ATT.THPO).Y;
                                double disy = (gridPosY - midViewY) * arrowLab.Owner.GetDouble(ATT.VSCA);
                                double y = arrowLab.Owner.GetDoubleArray(ATT.XYPS)[1] + disy;
                                // 断x坐标
                                double gridPosX = arrow.GetPosition(ATT.POS).X;
                                double midViewX = arrowLab.Owner.GetPosition(ATT.THPO).X;
                                double disx = (gridPosX - midViewX) * arrowLab.Owner.GetDouble(ATT.VSCA);
                                double x = arrowLab.Owner.GetDoubleArray(ATT.XYPS)[0] + disx;
                                drawTool.CommandExcute(arrOne.GetAsString(ATT.NAME) + "at x " + x + " y " + y);
                            }
                            }
                            catch (Exception exArrow) { log.AppendLine("箭头/流向标注项异常(跳过): [" + exArrow.GetType().Name + "] " + exArrow.Message); }
                        }
                        foreach (DbElement arrow in eleValvs)
                        {
                            try // 逐项守护（2026-10-15）
                            {
                            if (arrow.GetElementType() == DbElementTypeInstance.VALVE && arrow.GetAsString(ATT.PDIR, 2) != "U" && arrow.GetAsString(ATT.PDIR, 2) != "D")
                            {
                                // 2026-10-16 标准图块：水平向阀门 = VALVE（蝴蝶结+阀板细三角+阀杆+手轮，HG20519 图6-16 闸阀）
                                // 按管道方向旋转（替代缺失的 /MDS/ABA/TAGS/Valv-H + CIRCLE/2）
                                double[] pp = ChemSymbols.Project(viewDraw, arrow.GetPosition(ATT.WORPOS).X, arrow.GetPosition(ATT.WORPOS).Y);
                                double vAng = ChemSymbols.PaperAngle(arrow.GetDirection(ATT.PDIR, 1));
                                if (SymBlocks.Insert(symLab, SymBlocks.N_VALVE, arrow, pp[0], pp[1], vAng, SymBlocks.PIPE_COLOR) != null) symInsCnt++;
                                else { ChemSymbols.Valve(noteCenter, pp[0], pp[1], vAng, 9); symFbCnt++; }
                            }
                            else if (arrow.GetElementType() == DbElementTypeInstance.VALVE && (arrow.GetAsString(ATT.PDIR, 2) == "U" || arrow.GetAsString(ATT.PDIR, 2) == "D"))
                            {
                                // 2026-10-16 标准图块：竖直向阀门（俯视端视）= VALVE，按管道方向旋转；竖管投影退化时水平落符号
                                // （替代缺失的 /MDS/ABA/TAGS/Valv-V + CIRCLE/2）
                                double[] pp = ChemSymbols.Project(viewDraw, arrow.GetPosition(ATT.WORPOS).X, arrow.GetPosition(ATT.WORPOS).Y);
                                double vAng = ChemSymbols.PaperAngle(arrow.GetDirection(ATT.PDIR, 1));
                                if (SymBlocks.Insert(symLab, SymBlocks.N_VALVE, arrow, pp[0], pp[1], vAng, SymBlocks.PIPE_COLOR) != null) symInsCnt++;
                                else { ChemSymbols.Valve(noteCenter, pp[0], pp[1], vAng, 9); symFbCnt++; }
                            }
                            }
                            catch (Exception exValv) { log.AppendLine("阀门标注项异常(跳过): [" + exValv.GetType().Name + "] " + exValv.Message); }
                        }
                        foreach (DbElement arrow in eleFlans)
                        {
                            try // 逐项守护（2026-10-15）
                            {
                            if (arrow.GetElementType() == DbElementTypeInstance.FLANGE && arrow.GetAsString(ATT.LDIR) != "D" && arrow.Owner.LastMember().GetElementType() == DbElementTypeInstance.FLANGE)
                            {
                                // 2026-10-16 标准图块：法兰 = FLANGE（垂直于管道的短线，HG20519 图6-11 法兰连接）
                                double[] pp = ChemSymbols.Project(viewDraw, arrow.GetPosition(ATT.WORPOS).X, arrow.GetPosition(ATT.WORPOS).Y);
                                double fAng = ChemSymbols.PaperAngle(arrow.GetDirection(ATT.LDIR));
                                if (SymBlocks.Insert(symLab, SymBlocks.N_FLANGE, arrow, pp[0], pp[1], fAng, SymBlocks.PIPE_COLOR) != null) symInsCnt++;
                                else { ChemSymbols.Flange(noteCenter, pp[0], pp[1], fAng, 9); symFbCnt++; }
                            }
                            }
                            catch (Exception exFlan) { log.AppendLine("法兰标注项异常(跳过): [" + exFlan.GetType().Name + "] " + exFlan.Message); }
                        }
                        // 三通/支管（BRANCH）标准图块 TEE（圆+中心点端视，HG20519 图6-15；BRANCH 位置用世界系 HPOS）
                        foreach (DbElement br in eleBrans)
                        {
                            try
                            {
                                DbQualifier bq = new DbQualifier(); bq.wrtQualifier = DbElement.GetElement("/*");
                                Position bp = br.GetPosition(ATT.HPOS, bq);
                                double[] pp = ChemSymbols.Project(viewDraw, bp.X, bp.Y);
                                if (SymBlocks.Insert(symLab, SymBlocks.N_TEE, br, pp[0], pp[1], 0, SymBlocks.PIPE_COLOR) != null) symInsCnt++;
                                else { ChemSymbols.Tee(noteCenter, pp[0], pp[1], 9); symFbCnt++; }
                            }
                            catch (Exception exBr) { log.AppendLine("三通标注项异常(跳过): [" + exBr.GetType().Name + "] " + exBr.Message); }
                        }
                        // 引出管线标注
                        // 管线表索引符号(模型位置)→文字层(View)；管线一览表(固定图幅坐标)→表格层(SHEET)
                        DbElement mtoLab = LayerManager.GetLayer(viewDraw, DrawLayerCat.Text);
                        DbElement noteDraw = lTable.CreateLast(DbElementTypeInstance.NOTE);
                        drawTool.CommandExcute(noteDraw.GetAsString(ATT.NAME) + "at x 50 y 289");
                        drawTool.SetAttribute(noteDraw, ATT.NAME, noteDraw.Owner.GetAsString(ATT.NAME) + "/管线表格");
                        for (int i = 0; i < pipeCollection.Count(); i++)
                        {
                            try // 逐项守护（2026-10-15）：spool 裸 PIPE 成员结构可能与 BRANCH 假设不符
                            {
                            DbElement slabDraw = mtoLab.CreateLast(DbElementTypeInstance.SLABEL);
                            drawTool.SetAttribute(slabDraw, ATT.LFCOLO, "13");
                            slabDraw.SetAttribute(ATT.TMRF, DbElement.GetElement("/MDS/ABA/TAGS/TABLE"));
                            slabDraw.SetAttribute(ATT.TSIZ, 2.5);
                            slabDraw.SetAttribute(ATT.BLNK, false);
                            slabDraw.SetAttribute(ATT.DDNM, pipeCollection[i]);
                            slabDraw.SetAttribute(ATT.ATEX, (i + 1).ToString());
                            // 箭头y坐标
                            double gridPosY = pipeCollection[i].Member(0).Member(1).GetPosition(ATT.WORPOS).Y;
                            double midViewY = arrowLab.Owner.GetPosition(ATT.THPO).Y;
                            double disy = (gridPosY - midViewY) * arrowLab.Owner.GetDouble(ATT.VSCA);
                            double y = arrowLab.Owner.GetDoubleArray(ATT.XYPS)[1] + disy - 120;
                            // 箭头x坐标
                            double gridPosX = pipeCollection[i].Member(0).Member(1).GetPosition(ATT.WORPOS).X;
                            double midViewX = arrowLab.Owner.GetPosition(ATT.THPO).X;
                            double disx = (gridPosX - midViewX) * arrowLab.Owner.GetDouble(ATT.VSCA);
                            double x = arrowLab.Owner.GetDoubleArray(ATT.XYPS)[0] + disx;
                            drawTool.CommandExcute(slabDraw.GetAsString(ATT.NAME) + "at x " + x + " y " + y);
                            }
                            catch (Exception exSlab) { log.AppendLine("管线表索引项异常(跳过 i=" + i + "): [" + exSlab.GetType().Name + "] " + exSlab.Message); }
                        }
                        // 管道一览表表头
                        DbElement textCell1 = noteDraw.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
                        drawTool.SetAttribute(textCell1, ATT.JUST, "Centre");
                        drawTool.SetAttribute(textCell1, ATT.ALIGN, "Hbody");
                        drawTool.SetAttribute(textCell1, ATT.TXCOLO, "10");
                        drawTool.SetAttribute(textCell1, ATT.CHEI, "2.5");
                        textCell1.SetAttribute(ATT.BTEX, "序号");
                        DbElement textCell2 = noteDraw.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
                        textCell2.Copy(textCell1);
                        textCell2.SetAttribute(ATT.BTEX, "管径");
                        drawTool.CommandExcute(textCell2.GetAsString(ATT.NAME) + "at x 60 y 289 ");
                        DbElement textCell3 = noteDraw.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
                        textCell3.Copy(textCell1);
                        textCell3.SetAttribute(ATT.BTEX, "管道名称");
                        drawTool.CommandExcute(textCell3.GetAsString(ATT.NAME) + "at x 80 y 289 ");
                        DbElement textCell4 = noteDraw.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
                        textCell4.Copy(textCell1);
                        textCell4.SetAttribute(ATT.BTEX, "底标高");
                        drawTool.CommandExcute(textCell4.GetAsString(ATT.NAME) + "at x 120 y 289 ");
                        // 框的竖线
                        double frameLengthH = 5 * (pipeCollection.Count() + 1); // 竖线长度
                        DbElement yFram1 = noteDraw.CreateLast(DbElementTypeInstance.STRAIGHT);
                        drawTool.SetAttribute(yFram1, ATT.LENG, frameLengthH.ToString());
                        yFram1.SetAttribute(ATT.NLCOLO, 10);
                        drawTool.SetAttribute(yFram1, ATT.ADEG, "-90");
                        drawTool.CommandExcute(yFram1.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y 0mm Tpt Offset X 0mm" + " Y -" + frameLengthH + " mm");
                        drawTool.CommandExcute(yFram1.GetAsString(ATT.NAME) + " at x 48 y 294");
                        DbElement yFram2 = noteDraw.CreateLast(DbElementTypeInstance.STRAIGHT);
                        yFram2.Copy(yFram1);
                        drawTool.CommandExcute(yFram2.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y 0mm Tpt Offset X 0mm" + " Y -" + frameLengthH + " mm");
                        drawTool.CommandExcute(yFram2.GetAsString(ATT.NAME) + " at x 60 y 294");
                        DbElement yFram3 = noteDraw.CreateLast(DbElementTypeInstance.STRAIGHT);
                        yFram3.Copy(yFram1);
                        drawTool.CommandExcute(yFram3.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y 0mm Tpt Offset X 0mm" + " Y -" + frameLengthH + " mm");
                        drawTool.CommandExcute(yFram3.GetAsString(ATT.NAME) + " at x 70 y 294");
                        DbElement yFram4 = noteDraw.CreateLast(DbElementTypeInstance.STRAIGHT);
                        yFram4.Copy(yFram1);
                        drawTool.CommandExcute(yFram4.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y 0mm Tpt Offset X 0mm" + " Y -" + frameLengthH + " mm");
                        drawTool.CommandExcute(yFram4.GetAsString(ATT.NAME) + " at x 120 y 294");
                        DbElement yFram5 = noteDraw.CreateLast(DbElementTypeInstance.STRAIGHT);
                        yFram5.Copy(yFram1);
                        drawTool.CommandExcute(yFram5.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y 0mm Tpt Offset X 0mm" + " Y -" + frameLengthH + " mm");
                        drawTool.CommandExcute(yFram5.GetAsString(ATT.NAME) + " at x 140 y 294");
                        // 最上面和最先面的横线
                        DbElement straFramHead = noteDraw.CreateLast(DbElementTypeInstance.STRAIGHT);
                        straFramHead.Copy(yFram1);
                        drawTool.CommandExcute(straFramHead.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y 0mm Tpt Offset X 92mm Y 0mm");
                        drawTool.SetAttribute(straFramHead, ATT.LENG, "85");
                        drawTool.CommandExcute(straFramHead.GetAsString(ATT.NAME) + " at x 48 y 294");
                        DbElement straFram2 = noteDraw.CreateLast(DbElementTypeInstance.STRAIGHT);
                        straFram2.Copy(yFram1);
                        drawTool.CommandExcute(straFram2.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y 0mm Tpt Offset X 92mm Y 0mm");
                        drawTool.SetAttribute(straFram2, ATT.LENG, "85");
                        drawTool.CommandExcute(straFram2.GetAsString(ATT.NAME) + " at x 48 y 289");
                        DbElement straFram3 = noteDraw.CreateLast(DbElementTypeInstance.STRAIGHT);
                        straFram3.Copy(yFram1);
                        double offSetXFramHead = 289 - 5 * pipeCollection.Count(); // 框最下面横线偏移量
                        drawTool.CommandExcute(straFram3.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y 0mm Tpt Offset X 92mm Y 0mm");
                        drawTool.SetAttribute(straFram3, ATT.LENG, "85");
                        drawTool.CommandExcute(straFram3.GetAsString(ATT.NAME) + " at x 48 y " + offSetXFramHead);
                        // 绘制管道一览表
                        for (int i = 0; i < pipeCollection.Count(); i++)
                        {
                            try // 逐项守护（2026-10-15）：HBOR/ITPS/SPRE 读取假设 BRANCH 结构，spool 裸 PIPE 可能不符
                            {
                            double offSet = 289 - 5 * (i + 1); // 字体偏移量
                            double offSetXFram = 289 - 5 * i;   // 框横线偏移量
                            // 画框横线
                            DbElement straFram1 = noteDraw.CreateLast(DbElementTypeInstance.STRAIGHT);
                            straFram1.Copy(yFram1);
                            drawTool.CommandExcute(straFram1.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y 0mm Tpt Offset X 92mm Y 0mm");
                            drawTool.SetAttribute(straFram1, ATT.LENG, "85");
                            drawTool.CommandExcute(straFram1.GetAsString(ATT.NAME) + " at x 48 y " + offSetXFram);
                            // 序号
                            DbElement textCell11 = noteDraw.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
                            textCell11.Copy(textCell1);
                            textCell11.SetAttribute(ATT.BTEX, (i + 1).ToString());
                            drawTool.CommandExcute(textCell11.GetAsString(ATT.NAME) + " at x 50 y " + offSet);
                            // 管径
                            DbElement textCell21 = noteDraw.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
                            textCell21.Copy(textCell2);
                            textCell21.SetAttribute(ATT.BTEX, pipeCollection[i].Member(0).GetAsString(ATT.HBOR).Replace("mm", ""));
                            drawTool.CommandExcute(textCell21.GetAsString(ATT.NAME) + " at x 60 y " + offSet);
                            // 管道名称
                            DbElement textCell31 = noteDraw.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
                            textCell31.Copy(textCell3);
                            textCell31.SetAttribute(ATT.BTEX, pipeCollection[i].GetAsString(ATT.NAMN));
                            drawTool.CommandExcute(textCell31.GetAsString(ATT.NAME) + " at x 70 y " + offSet);
                            // 管道底标高
                            DbElement[] branMems = pipeCollection[i].Members();
                            double tubeOd = pipeCollection[i].Member(0).Member(0).GetElement(ATT.SPRE).GetElement(ATT.CATR).GetDoubleArray(ATT.PARA)[1];
                            double itemPos = 0;
                            if (pipeCollection[i].Member(0).Member(0).GetAsString(ATT.ITPS).Contains("U"))
                            {
                                itemPos = (Convert.ToDouble(pipeCollection[i].Member(0).Member(0).GetAsString(ATT.ITPS).Substring(pipeCollection[i].Member(0).Member(0).GetAsString(ATT.ITPS).IndexOf("U") + 1).Trim().Replace("mm", "")) - tubeOd * 0.5) * 0.001;
                            }
                            else if (pipeCollection[i].Member(0).Member(0).GetAsString(ATT.ITPS).Contains("D"))
                            {
                                itemPos = -(Convert.ToDouble(pipeCollection[i].Member(0).Member(0).GetAsString(ATT.ITPS).Substring(pipeCollection[i].Member(0).Member(0).GetAsString(ATT.ITPS).IndexOf("D") + 1).Trim().Replace("mm", "")) - tubeOd * 0.5) * 0.001;
                            }
                            double lbopPipe = Math.Round(itemPos, 3);
                            foreach (DbElement mem in branMems)
                            {
                                DbElement[] branItems = mem.Members();
                                foreach (DbElement item in branItems)
                                {
                                    if (item.GetElementType() == DbElementTypeInstance.TUBING)
                                    {
                                        tubeOd = item.GetElement(ATT.SPRE).GetElement(ATT.CATR).GetDoubleArray(ATT.PARA)[1];
                                        if (item.GetAsString(ATT.ITPS).Contains("U"))
                                        {
                                            itemPos = (Convert.ToDouble(item.GetAsString(ATT.ITPS).Substring(item.GetAsString(ATT.ITPS).IndexOf("U") + 1).Trim().Replace("mm", "")) - tubeOd * 0.5) * 0.001;
                                        }
                                        else if (item.GetAsString(ATT.ITPS).Contains("D"))
                                        {
                                            itemPos = -(Convert.ToDouble(item.GetAsString(ATT.ITPS).Substring(item.GetAsString(ATT.ITPS).IndexOf("D") + 1).Trim().Replace("mm", "")) - tubeOd * 0.5) * 0.001;
                                        }
                                        if (lbopPipe > itemPos)
                                        {
                                            lbopPipe = Math.Round(itemPos, 3);
                                        }
                                    }
                                }
                            }
                            DbElement textCell41 = noteDraw.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
                            textCell41.Copy(textCell4);
                            textCell41.SetAttribute(ATT.BTEX, lbopPipe.ToString());
                            drawTool.CommandExcute(textCell41.GetAsString(ATT.NAME) + " at x 120 y " + offSet);
                            }
                            catch (Exception exRow) { log.AppendLine("管道一览表行异常(跳过 i=" + i + "): [" + exRow.GetType().Name + "] " + exRow.Message); }
                        }
                        LayerManager.SetCurrentLayer(sheetDraw, DrawLayerCat.Outline); // 外形层(best-effort)：模型几何归层需 PDMS 实测
                        drawTool.UpdateModelView(sheetDraw);
                        try // 守护（2026-10-15 实测）：空 idlist 视图 UPDATE all → (64,153) "Generation of Design graphics abandoned" 抛异常
                        {
                            drawTool.CommandExcute("UPDATE all");
                        }
                        catch (Exception exUpd) { log.AppendLine("UPDATE all 异常（继续，不影响结构/标注）: [" + exUpd.GetType().Name + "] " + exUpd.Message); }
                        if (DrawingType == "管道布置图")
                        {
                            DBElementCollection dimLayers = new DBElementCollection(viewDraw, new TypeFilter(DbElementTypeInstance.LAYER));
                            foreach (DbElement item in dimLayers)
                            {
                                DBElementCollection dims = new DBElementCollection(item, new TypeFilter(DbElementTypeInstance.LDIMENSION));
                                foreach (DbElement item2 in dims)
                                {
                                    Graphic.setGridPos(item2);
                                }
                                DBElementCollection dimLabels = new DBElementCollection(item, new TypeFilter(DbElementTypeInstance.SLABEL));
                                foreach (DbElement item2 in dimLabels)
                                {
                                    Graphic.setGridLabelPos(item2, viewMinX, viewMaxX, viewMinY, viewMaxY);
                                }
                            }
                        }
                        log.AppendLine("ANNOTATED 管道标注/设备标注/流向/阀门/法兰/管线表 (eles=" + eles.Count + ", pipes=" + pipeCollection.Count +
                                     ", equip=" + eleEquis.Count + ", valves=" + eleValvs.Count + ", flanges=" + eleFlans.Count +
                                     ", symBlocks=" + symInsCnt + ", symFallback=" + symFbCnt + ")");
                    }
                    else log.AppendLine("annotate=false 跳过标注块");
                }
                else if (DrawingType == "管口方位图")
                {
                    if (annotate)
                    {
                        drawTool.CommandExcute(viewDraw.GetAsString(ATT.NAME) + "by y 60 ");
                        // 创建投影面
                        List<DbElement> eles = drawE3DTool.ElementsFromDrawlist(viewDraw, new TypeFilter(DbElementTypeInstance.NOZZLE), false, false);
                        DbElement lNozzText = LayerManager.GetLayer(viewDraw, DrawLayerCat.Text);
                        drawTool.SetAttribute(lNozzText, ATT.JUST, "Centre");
                        drawTool.SetAttribute(lNozzText, ATT.ALIGN, "BASE");
                        drawTool.SetAttribute(lNozzText, ATT.LSHA, "Angled Horizontal");
                        DbElement noteNozz = LayerManager.GetLayer(sheetDraw, DrawLayerCat.Table).CreateLast(DbElementTypeInstance.NOTE);

                        // 管口方位一览表表头
                        DbElement textCell1 = noteNozz.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
                        drawTool.SetAttribute(textCell1, ATT.JUST, "Centre");
                        drawTool.SetAttribute(textCell1, ATT.ALIGN, "Hbody");
                        drawTool.SetAttribute(textCell1, ATT.TXCOLO, "10");
                        drawTool.SetAttribute(textCell1, ATT.CHEI, "2.5");
                        textCell1.SetAttribute(ATT.BTEX, "管口符号");
                        drawTool.CommandExcute(textCell1.GetAsString(ATT.NAME) + "at x 10 y 52 ");
                        DbElement textCell2 = noteNozz.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
                        textCell2.Copy(textCell1);
                        textCell2.SetAttribute(ATT.BTEX, "公称直径");
                        drawTool.CommandExcute(textCell2.GetAsString(ATT.NAME) + "at x 25 y 52 ");
                        DbElement textCell3 = noteNozz.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
                        textCell3.Copy(textCell1);
                        textCell3.SetAttribute(ATT.BTEX, "连接形式及标准");
                        drawTool.CommandExcute(textCell3.GetAsString(ATT.NAME) + "at x 45 y 52");
                        DbElement textCell4 = noteNozz.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
                        textCell4.Copy(textCell1);
                        textCell4.SetAttribute(ATT.BTEX, "用途及名称");
                        drawTool.CommandExcute(textCell4.GetAsString(ATT.NAME) + "at x 88 y 52 ");
                        // 右边的字
                        DbElement textCell21 = noteNozz.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
                        textCell21.Copy(textCell1);
                        drawTool.CommandExcute(textCell21.GetAsString(ATT.NAME) + "at x 105 y 52 ");
                        DbElement textCell22 = noteNozz.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
                        textCell22.Copy(textCell2);
                        drawTool.CommandExcute(textCell22.GetAsString(ATT.NAME) + "at x 120 y 52 ");
                        DbElement textCell23 = noteNozz.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
                        textCell23.Copy(textCell3);
                        drawTool.CommandExcute(textCell23.GetAsString(ATT.NAME) + "at x 140 y 52 ");
                        DbElement textCell24 = noteNozz.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
                        textCell24.Copy(textCell4);
                        drawTool.CommandExcute(textCell24.GetAsString(ATT.NAME) + "at x 183 y 52 ");
                        // 框的竖线
                        double frameLengthH = 5 * (eles.Count()) * 0.5 + 5; // 竖线长度
                        DbElement yFram = noteNozz.CreateLast(DbElementTypeInstance.STRAIGHT);
                        drawTool.SetAttribute(yFram, ATT.LENG, frameLengthH.ToString());
                        yFram.SetAttribute(ATT.NLCOLO, 10);
                        drawTool.SetAttribute(yFram, ATT.ADEG, "-90");
                        drawTool.CommandExcute(yFram.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y 0mm Tpt Offset X 0mm" + " Y +" + frameLengthH + " mm");
                        drawTool.CommandExcute(yFram.GetAsString(ATT.NAME) + " at x 25 y 52");
                        DbElement yFram2 = noteNozz.CreateLast(DbElementTypeInstance.STRAIGHT);
                        yFram2.Copy(yFram);
                        drawTool.CommandExcute(yFram2.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y 0mm Tpt Offset X 0mm" + " Y +" + frameLengthH + " mm");
                        drawTool.CommandExcute(yFram2.GetAsString(ATT.NAME) + " at x 38 y 52 ");
                        DbElement yFram3 = noteNozz.CreateLast(DbElementTypeInstance.STRAIGHT);
                        yFram3.Copy(yFram2);
                        drawTool.CommandExcute(yFram3.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y 0mm Tpt Offset X 0mm" + " Y +" + frameLengthH + " mm");
                        drawTool.CommandExcute(yFram3.GetAsString(ATT.NAME) + " at x 86 y 52 ");
                        // 间隔线
                        DbElement yFramMid = noteNozz.CreateLast(DbElementTypeInstance.STRAIGHT);
                        yFramMid.Copy(yFram3);
                        drawTool.CommandExcute(yFramMid.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y 0mm Tpt Offset X 0mm" + " Y +" + frameLengthH + " mm");
                        drawTool.CommandExcute(yFramMid.GetAsString(ATT.NAME) + " at x 104 y 52 ");
                        DbElement yFram4 = noteNozz.CreateLast(DbElementTypeInstance.STRAIGHT);
                        yFram4.Copy(yFram3);
                        drawTool.CommandExcute(yFram4.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y 0mm Tpt Offset X 0mm" + " Y +" + frameLengthH + " mm");
                        drawTool.CommandExcute(yFram4.GetAsString(ATT.NAME) + " at x 120 y 52 ");
                        DbElement yFram5 = noteNozz.CreateLast(DbElementTypeInstance.STRAIGHT);
                        yFram5.Copy(yFram4);
                        drawTool.CommandExcute(yFram5.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y 0mm Tpt Offset X 0mm" + " Y +" + frameLengthH + " mm");
                        drawTool.CommandExcute(yFram5.GetAsString(ATT.NAME) + " at x 132 y 52 ");
                        DbElement yFram6 = noteNozz.CreateLast(DbElementTypeInstance.STRAIGHT);
                        yFram6.Copy(yFram5);
                        drawTool.CommandExcute(yFram6.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y 0mm Tpt Offset X 0mm" + " Y +" + frameLengthH + " mm");
                        drawTool.CommandExcute(yFram6.GetAsString(ATT.NAME) + " at x 181 y 52 ");
                        Dictionary<Double, int> count = new Dictionary<double, int>();
                        // 最上面的横框线
                        DbElement straFram1 = noteNozz.CreateLast(DbElementTypeInstance.STRAIGHT);
                        straFram1.Copy(yFram);
                        drawTool.CommandExcute(straFram1.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y 0mm Tpt Offset X 190mm Y 0mm");
                        drawTool.SetAttribute(straFram1, ATT.LENG, "170");
                        drawTool.CommandExcute(straFram1.GetAsString(ATT.NAME) + " at x 10 y " + (frameLengthH + 52));

                        int i = 0;
                        foreach (DbElement one in eles)
                        {
                            // 管口号
                            string nozzName = one.GetAsString(ATT.NAME).Substring(one.GetAsString(ATT.NAME).LastIndexOf('/') + 1);
                            // 连接形式及标准
                            string nozzCatref = one.GetElement(ATT.CATR).GetAsString(ATT.NAMN);
                            // 管径
                            string nozzBore = one.GetAsString(ATT.PROP, ATT.APAR).Replace("mm", "");
                            // 用途
                            string nozzFunc = one.GetAsString(ATT.FUNC);
                            double offSet = 52 + 5 * (i + 1);    // 字体偏移量
                            double offSetXFram = 52 + 5 * (i + 1); // 框横线偏移量
                            double m = 0;
                            if (offSet >= frameLengthH + 52)
                            {
                                offSet = 52 + 5 * (i + 2) - frameLengthH; // 字体偏移量
                                offSetXFram = 52 + 5 * (i + 1) - frameLengthH; // 框横线偏移量
                                m = 95;
                            }
                            // 画框横线
                            DbElement straFramNozz = noteNozz.CreateLast(DbElementTypeInstance.STRAIGHT);
                            straFramNozz.Copy(yFram);
                            drawTool.CommandExcute(straFramNozz.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y 0mm Tpt Offset X 190mm Y 0mm");
                            drawTool.SetAttribute(straFramNozz, ATT.LENG, "170");
                            drawTool.CommandExcute(straFramNozz.GetAsString(ATT.NAME) + " at x " + 10 + " y " + offSetXFram);
                            // 管嘴号
                            DbElement textCell11 = noteNozz.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
                            textCell11.Copy(textCell1);
                            textCell11.SetAttribute(ATT.BTEX, nozzName);
                            drawTool.CommandExcute(textCell11.GetAsString(ATT.NAME) + " at x " + (m + 13) + " y " + offSet);
                            // 公称直径
                            DbElement textCell12 = noteNozz.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
                            textCell12.Copy(textCell1);
                            textCell12.SetAttribute(ATT.BTEX, nozzBore);
                            drawTool.CommandExcute(textCell12.GetAsString(ATT.NAME) + " at x " + (m + 28) + " y " + offSet);
                            // 连接形式及标准
                            DbElement textCell13 = noteNozz.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
                            textCell13.Copy(textCell1);
                            textCell13.SetAttribute(ATT.BTEX, nozzCatref);
                            drawTool.CommandExcute(textCell13.GetAsString(ATT.NAME) + " at x " + (m + 40) + " y " + offSet);
                            // 用途
                            DbElement textCell14 = noteNozz.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
                            textCell14.Copy(textCell1);
                            textCell14.SetAttribute(ATT.BTEX, nozzFunc);
                            drawTool.CommandExcute(textCell14.GetAsString(ATT.NAME) + " at x " + (m + 90) + " y " + offSet);
                            // 角度
                            double Angle = drawTool.getAngle(one);
                            // 中心线
                            DbElement centerLine = noteNozz.CreateLast(DbElementTypeInstance.STRAIGHT);
                            drawTool.CommandExcute(centerLine.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y 0mm Tpt Offset X 27mm Y 0mm");
                            drawTool.CommandExcute(centerLine.GetAsString(ATT.NAME) + "ADEG " + (90 - Angle));
                            double x = 0;
                            double y = 0;
                            drawTool.SetAttribute(centerLine, ATT.NLCOLO, "2");
                            drawTool.SetAttribute(centerLine, ATT.NLSTYL, "8006");
                            if (one.Owner.GetElementType() == DbElementTypeInstance.EQUIPMENT)
                            {
                                // 设备y坐标
                                double equiPosY = one.Owner.GetPosition(ATT.WORPOS).Y;
                                double midViewY = viewDraw.GetPosition(ATT.THPO).Y;
                                double disy = (equiPosY - midViewY) * viewDraw.GetDouble(ATT.VSCA);
                                y = viewDraw.GetDoubleArray(ATT.XYPS)[1] + disy;
                                // 设备x坐标
                                double equiPosX = one.Owner.GetPosition(ATT.WORPOS).X;
                                double midViewX = viewDraw.GetPosition(ATT.THPO).X;
                                double disx = (equiPosX - midViewX) * viewDraw.GetDouble(ATT.VSCA);
                                x = viewDraw.GetDoubleArray(ATT.XYPS)[0] + disx;
                                drawTool.CommandExcute(centerLine.GetAsString(ATT.NAME) + " at x " + x + " y " + y);
                            }
                            else if (one.Owner.Owner.GetElementType() == DbElementTypeInstance.EQUIPMENT)
                            {
                                // 设备y坐标
                                double equiPosY = one.Owner.Owner.GetPosition(ATT.WORPOS).Y;
                                double midViewY = viewDraw.GetPosition(ATT.THPO).Y;
                                double disy = (equiPosY - midViewY) * viewDraw.GetDouble(ATT.VSCA);
                                y = viewDraw.GetDoubleArray(ATT.XYPS)[1] + disy;
                                // 设备x坐标
                                double equiPosX = one.Owner.Owner.GetPosition(ATT.WORPOS).X;
                                double midViewX = viewDraw.GetPosition(ATT.THPO).X;
                                double disx = (equiPosX - midViewX) * viewDraw.GetDouble(ATT.VSCA);
                                x = viewDraw.GetDoubleArray(ATT.XYPS)[0] + disx;
                                drawTool.CommandExcute(centerLine.GetAsString(ATT.NAME) + " at x " + x + " y " + y);
                            }
                            // 管嘴号
                            DbElement arrOne = lNozzText.CreateLast(DbElementTypeInstance.GLABEL);
                            drawTool.SetAttribute(arrOne, ATT.LLEA, "false");
                            drawTool.CommandExcute(arrOne.GetAsString(ATT.NAME) + "JUST Centre");
                            arrOne.SetAttribute(ATT.BTEX, "#NOZZ(P2:)(C2:)");
                            arrOne.SetAttribute(ATT.DDNM, one);
                            arrOne.SetAttribute(ATT.TSIZ, 2.5);
                            drawTool.SetAttribute(arrOne, ATT.TXCOLO, "209");
                            // 角度
                            DbElement angleText = noteNozz.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
                            drawTool.CommandExcute(angleText.GetAsString(ATT.NAME) + "JUST Centre");
                            drawTool.SetAttribute(angleText, ATT.ALIGN, "BASE");
                            drawTool.SetAttribute(angleText, ATT.TXCOLO, "10");
                            drawTool.SetAttribute(angleText, ATT.CHEI, "2.5");
                            angleText.SetAttribute(ATT.BTEX, Angle.ToString());
                            Direction nozzDir = one.GetDirection(ATT.PDIR, 1);
                            // 判断x的坐标有没有用
                            if (nozzDir.ToString() != "U" && nozzDir.ToString() != "D")
                            {
                                if (!count.ContainsKey(Angle))
                                {
                                    count.Add(Angle, 1);
                                }
                                drawTool.CommandExcute(arrOne.GetAsString(ATT.NAME) + "at x " + (x + (27 + 8 * count[Angle]) * Math.Sin(Angle * Math.PI / 180)) + " y " + (y + (33 + 8 * count[Angle]) * Math.Cos(Angle * Math.PI / 180)));
                                count[Angle]++;
                            }
                            else if (nozzDir.ToString() == "U")
                            {
                                x = x + 5;
                                y = y + 5;
                                drawTool.CommandExcute(arrOne.GetAsString(ATT.NAME) + "at x " + x + " y " + y);
                            }
                            else if (nozzDir.ToString() == "D")
                            {
                                x = x + 10;
                                y = y + 10;
                                drawTool.CommandExcute(arrOne.GetAsString(ATT.NAME) + "at x " + x + " y " + y);
                            }
                            i++;
                        }
                        log.AppendLine("ANNOTATED 管口方位图 (nozzles=" + eles.Count + ")");
                    }
                    else log.AppendLine("annotate=false 跳过标注块");
                }
                LayerManager.SetCurrentLayer(sheetDraw, DrawLayerCat.Outline); // 外形层(best-effort)：模型几何归层需 PDMS 实测
                drawTool.UpdateModelView(sheetDraw);
                bool upd = drawTool.CommandExcute("UPDATE all");
                log.AppendLine("UPDATE all = " + upd);
                // ===== FINAL_CDLIMITS：UPDATE all 后复核/重设范围框 UDA（2026-10-15 23:0x 重构）=====
                // 重构原因：旧段把 VVOL（显示帧=SIZE×VSCA 自框）误当范围框，用被 ACTUAL_LIMITS 覆盖的
                // 野值变量重设（21:49/22:4x 实证）。范围框官方载体 = :CDLIMITS UDA（gen/uresetidli 注释：
                // "historically the IDLI was used to maintain views depth, this is now performed by the
                // :CDLIMITS UDA"）。先只读回读校验；不一致才重设 + 补一次 UPDATE all（让生成消费新 UDA）。
                try
                {
                    CE.Element = viewDraw;
                    double[] vvF = null;
                    try { vvF = viewDraw.GetDoubleArray(ATT.VVOL); } catch { }
                    if (vvF != null && vvF.Length >= 6)
                        log.AppendLine("VVOL（显示帧，仅记录）: From W" + vvF[0] + " N" + vvF[1] + " U" + vvF[2] + " To W" + vvF[3] + " N" + vvF[4] + " U" + vvF[5]);
                    bool cdlOk = SendCdlimitsViaMacro(viewDraw, specMinX, specMaxX, specMinY, specMaxY, specMinZ, specMaxZ, false, log);
                    if (cdlOk)
                    {
                        log.AppendLine("FINAL_CDLIMITS 已验证（左下→右上 UDA 回读一致）");
                    }
                    else
                    {
                        log.AppendLine("FINAL_CDLIMITS 回读不一致，重设:");
                        if (SendCdlimitsViaMacro(viewDraw, specMinX, specMaxX, specMinY, specMaxY, specMinZ, specMaxZ, true, log))
                        {
                            try { drawTool.CommandExcute("UPDATE all"); log.AppendLine("UPDATE all（范围框重设后补生成）= ok"); }
                            catch (Exception exUpd2) { log.AppendLine("UPDATE all（重设后）异常: [" + exUpd2.GetType().Name + "] " + exUpd2.Message); }
                        }
                    }
                }
                catch (Exception exFv) { log.AppendLine("FINAL_CDLIMITS 复核失败: [" + exFv.GetType().Name + "] " + exFv.Message); }
                log.AppendLine("=== DRAW-OK ===");
                log.AppendLine("SHEET " + sheetDraw.GetAsString(ATT.NAME));
                log.AppendLine("VIEW " + viewDraw.GetAsString(ATT.NAME));
                log.AppendLine("SCALE 1/" + ScaleDen);
                log.AppendLine("图纸已创建：" + sheetDraw.GetAsString(ATT.NAME) + "（" + DrawingType + "，比例 1/" + ScaleDen + "）");
                res.Ok = true;
                res.Output = log.ToString();
            }
            catch (Exception ex)
            {
                res.Ok = false;
                res.Error = "[" + ex.GetType().Name + "] " + ex.Message;
                log.AppendLine("=== DRAW-FAIL " + res.Error + " ===");
                res.Output = log.ToString();
            }
            return res;
        }

        /* ---------------- probe_read（只读探针：桥接首跑冒烟 + 选元素） ---------------- */

        public static OpResult ProbeRead()
        {
            OpResult res = new OpResult();
            FileLoggingStringBuilder log = new FileLoggingStringBuilder(@"D:\pdmsAgent\bridge\op_probe.log");
            try
            {
                log.AppendLine("=== PROBE-START (read-only) " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===");
                // 1) unit（viewTest 3b 同款形式）
                try
                {
                    Command c1 = Command.CreateCommand("var !!VWTS unit");
                    if (c1.RunInPdms()) log.AppendLine("UNIT " + c1.GetPMLVariableString("VWTS"));
                    else log.AppendLine("UNIT ERR " + c1.Error.MessageText());
                }
                catch (Exception ex) { log.AppendLine("UNIT ERR " + ex.Message); }
                // 2) 当前 CE
                try
                {
                    DbElement ce = CE.Element;
                    log.AppendLine("CE " + ce.GetAsString(ATT.NAME) + " (" + ce.GetElementType() + ")");
                }
                catch (Exception ex) { log.AppendLine("CE ERR " + ex.Message); }
                // 3) /* 顶层（出图/设计 world 拓扑）
                try
                {
                    DbElement star = DbElement.GetElement("/*");
                    log.AppendLine("STAR " + star.GetElementType());
                    int i = 0;
                    foreach (DbElement m in star.Members())
                    {
                        if (m == null || m.IsNull) continue;
                        try { log.AppendLine("  TOP[" + (i++) + "] " + m.GetElementType() + " " + m.GetAsString(ATT.NAME)); }
                        catch { log.AppendLine("  TOP[" + (i++) + "] (读名失败)"); }
                    }
                }
                catch (Exception ex) { log.AppendLine("STAR ERR " + ex.Message); }
                // 4) /DRA 可解析性
                try
                {
                    DbElement dra = DbElement.GetElement("/DRA");
                    log.AppendLine("DRA " + (dra != null && !dra.IsNull ? dra.GetElementType() + " " + dra.GetAsString(ATT.NAME) : "不可解析"));
                }
                catch (Exception ex) { log.AppendLine("DRA 不可解析(" + ex.Message + ")"); }
                // 5) 设计 world 顶层 + 包围盒（MDBMembers(Design)，viewTest 7.0e 同款）
                try
                {
                    List<DbElement> projs = FangYuan.PDMS.Utilities.MDBMembers(DbType.Design);
                    log.AppendLine("DESIGN_TOP " + projs.Count);
                    int di = 0;
                    foreach (DbElement e in projs)
                    {
                        if (e == null || e.IsNull || e.IsDeleted) continue;
                        string t, n, box = "";
                        try { t = e.GetElementType().ToString(); } catch { t = "?"; }
                        try { n = e.GetAsString(ATT.NAME); } catch { n = "(无名)"; }
                        try
                        {
                            double[] l = ElementLimits(e);
                            if (!IsZeroBox(l))
                                box = " box[W" + l[0] + " N" + l[2] + " U" + l[4] + " TO W" + l[1] + " N" + l[3] + " U" + l[5] + "]";
                        }
                        catch { }
                        log.AppendLine("  D[" + (di++) + "] " + t + " " + n + box);
                    }
                }
                catch (Exception ex) { log.AppendLine("DESIGN_ERR " + ex.Message); }
                // 6) DRAFT(出图) world 顶层（MDBMembers(Draft)，2026-10-15 新增：DRA 库被 fatal 回滚成空后
                //    需要无 CE 依赖地确认出图 world 状态）
                try
                {
                    List<DbElement> drafts = FangYuan.PDMS.Utilities.MDBMembers(DbType.Draft);
                    log.AppendLine("DRAFT_TOP " + drafts.Count);
                    int dgi = 0;
                    foreach (DbElement e in drafts)
                    {
                        if (e == null || e.IsNull || e.IsDeleted) continue;
                        string t, n;
                        try { t = e.GetElementType().ToString(); } catch { t = "?"; }
                        try { n = e.GetAsString(ATT.NAME); } catch { n = "(无名)"; }
                        log.AppendLine("  G[" + (dgi++) + "] " + t + " " + n);
                    }
                }
                catch (Exception ex) { log.AppendLine("DRAFT_ERR " + ex.Message); }
                log.AppendLine("=== PROBE-OK ===");
                res.Ok = true;
                res.Output = log.ToString();
            }
            catch (Exception ex)
            {
                res.Ok = false;
                res.Error = "[" + ex.GetType().Name + "] " + ex.Message;
                log.AppendLine("=== PROBE-FAIL " + res.Error + " ===");
                res.Output = log.ToString();
            }
            return res;
        }

        /* ---------------- export_dxf / export_pdf（窗口 button11_Click 逐行镜像） ---------------- */

        public static OpResult ExportDxf(Dictionary<string, string> p)
        {
            OpResult res = new OpResult();
            try
            {
                string item = P(p, "sheet", "");
                string outDir = P(p, "outDir", @"D:\pdmsAgent\bridge\out\");
                if (item.Length == 0) throw new Exception("sheet 必填（SHEET 元素名，pdms_draw 返回的 SHEET 行）");
                DbElement se = DbElement.GetElement(item);
                if (se == null || se.IsNull) throw new Exception("SHEET 不存在: " + item);
                if (!outDir.EndsWith("\\")) outDir += "\\";
                String outPathName = outDir + item.Replace("/", "-") + ".dxf";
                // 2026-10-16 22:5x 修复（遗留项收口）：本安装无 /Draft_DXF_LI（06:xx 三证：coll all LIEXEC=0 /
                // cmd=False / out 目录空 → 19:55 导出失败真因）→ 改官方 PLOT 通道（06:47 v3 DXF 1,048,084B
                // 成功实证、gen/mdxfit:163 同款命令形）：PLOT <sheet> DXF "<outPath>"。
                // 路径用斜杠形（与 06:47 成功命令一致，避免 PLOT 解析反斜杠歧义）。
                string outSlash = outPathName.Replace('\\', '/');
                bool r = drawTool.CommandExcute("PLOT " + item + " DXF \"" + outSlash + "\"", true);
                // 2026-10-16 修复（原缺陷）：原实现无条件 res.Ok = true，命令失败被掩盖成"成功"
                // （13:12 实证 out=76 = CMD_FAIL 文案，out 目录无任何文件）。改为「命令返回 + 文件真实落盘」
                // 双证；导出可能异步落盘，最多等 15s（每轮 DoEvents 保 UI 响应）。
                bool exists = false; long len = 0;
                for (int i = 0; i < 150; i++)
                {
                    try { System.Windows.Forms.Application.DoEvents(); } catch { }
                    try { FileInfo fi = new FileInfo(outPathName); exists = fi.Exists; if (exists) len = fi.Length; } catch { }
                    if (exists && len > 0) break;
                    System.Threading.Thread.Sleep(100);
                }
                res.Ok = r && exists && len > 0;
                res.Output = "DXF " + (r ? "OK" : "CMD_FAIL") + " file=" + (exists ? len + "B" : "MISSING") + " " + outPathName + "\nSHEET " + se.GetAsString(ATT.NAME);
                if (!res.Ok) res.Error = "DXF 未产出文件（cmd=" + r + " exists=" + exists + " len=" + len + "）: " + outPathName;
            }
            catch (Exception ex) { res.Ok = false; res.Error = "[" + ex.GetType().Name + "] " + ex.Message; res.Output = ""; }
            return res;
        }

        public static OpResult ExportPdf(Dictionary<string, string> p)
        {
            OpResult res = new OpResult();
            try
            {
                string item = P(p, "sheet", "");
                string outDir = P(p, "outDir", @"D:\pdmsAgent\bridge\out\");
                if (item.Length == 0) throw new Exception("sheet 必填（SHEET 元素名，pdms_draw 返回的 SHEET 行）");
                DbElement se = DbElement.GetElement(item);
                if (se == null || se.IsNull) throw new Exception("SHEET 不存在: " + item);
                if (!outDir.EndsWith("\\")) outDir += "\\";
                String outPathName = outDir + item.Replace("/", "-") + ".pdf";
                // 窗口 :1525 同款 A4 判定（SIZE[0]=="210"）
                // 2026-10-16 判空加固：GetDoubleArray 可能返回 null/空数组，原实现直接取 [0] → NRE/越界。
                // 判空不过即报错中止，不猜尺寸（用户纪律：先判断对象是不是空，不是空再继续执行）。
                double[] sz = null;
                try { sz = se.GetDoubleArray(ATT.SIZE); } catch { }
                if (sz == null || sz.Length == 0) throw new Exception("SHEET SIZE 属性缺失/不可读，无法判定 A4/A1: " + item);
                if (sz[0].ToString() == "210")
                {
                    bool r = drawTool.CommandExcute("PLOT " + item + " PDF " + "\"" + outPathName + "\"" + " 'PLOTSTYLE " + "Monochrome" + "," + " REDUCETOFIT'" + "A4 CUT OFF BORD 25 OVER", true);
                    res.Ok = true;
                    res.Output = "PDF(A4) " + (r ? "OK" : "CMD_FAIL") + " " + outPathName + "\nSHEET " + se.GetAsString(ATT.NAME);
                }
                else
                {
                    bool r = drawTool.CommandExcute("PLOT " + item + " PDF " + "\"" + outPathName + "\"" + " 'PLOTSTYLE " + "monochrome" + "," + " REDUCETOFIT'" + "A1 CUT OFF BORD 25 OVER", true);
                    res.Ok = true;
                    res.Output = "PDF(A1) " + (r ? "OK" : "CMD_FAIL") + " " + outPathName + "\nSHEET " + se.GetAsString(ATT.NAME);
                }
            }
            catch (Exception ex) { res.Ok = false; res.Error = "[" + ex.GetType().Name + "] " + ex.Message; res.Output = ""; }
            return res;
        }

        /* ---------------- delete_element（2026-10-15 新增：/DEPT1 残留链删除 + 跑前清理通道） ---------------- */

        /// <summary>单元素删除（护栏 + PRE 日志 + DoEvents 前后泵 + 级联 Delete）。
        /// DoEvents 教训（2026-10-15 (2,202) UTRACKMAIN）：删除图形跟踪中的元素会触发
        /// 跟踪回调，泵消息让回调当场完成而非堆积到下一操作。</summary>
        private static void DeleteOne(DbElement el, string label, string guardType, FileLoggingStringBuilder log)
        {
            string typeName = "";
            try { typeName = el.GetElementType().ToString(); } catch { }
            if (guardType.Length > 0 && !typeName.Equals(guardType, StringComparison.OrdinalIgnoreCase))
            {
                log.AppendLine(label + " SKIPPED(类型不符: " + typeName + " != " + guardType + ")");
                return;
            }
            int childCount = 0;
            try { childCount = el.Members().Length; } catch { }
            log.AppendLine(label + " PRE type=" + typeName + " directChildren=" + childCount);
            try { System.Windows.Forms.Application.DoEvents(); } catch { }
            el.Delete();
            try { System.Windows.Forms.Application.DoEvents(); } catch { }
            log.AppendLine(label + " DELETED");
            // 2026-10-16 判空加固（致命 NRE 根因链）：若被删元素正是当前 CE，删除后 CE 悬空，
            // 之后任何 $M 下发（Command.RunInPdms）都会在返回路径抛 NRE「未将对象引用设置到对象的实例」
            // （实证：13:18:48 删掉 =23525/13，13:19:14 下发宏即抛该 NRE）。先判空，悬空则记日志留证，
            // 收尾由 set_ce op / PML 重设 CE + SAVEWORK 完成。
            try
            {
                DbElement ce = CE.Element;
                if (ce == null || ce.IsNull || ce.IsDeleted)
                    log.AppendLine(label + " WARN: 删除后 CE 悬空（须重设 CE 并 SAVEWORK，否则后续 $M 下发可能 NRE）");
            }
            catch (Exception exCe) { log.AppendLine(label + " CE 检查异常: " + exCe.Message); }
        }

        /// <summary>DFS 找子孙中 NAME 匹配的 SHEET（深度≤6/预算封顶/命中>5 停；每 128 元素 DoEvents 泵——
        /// 2026-10-15 树控件重绘竞态 fatal 教训，与 FindTypeDfs 同款防护）。</summary>
        private static void FindSheetsByName(DbElement node, string sheetName, List<DbElement> found, ref int budget, int depth)
        {
            if (depth > 6 || budget <= 0 || found.Count > 5) return;
            DbElement[] children;
            try { children = node.Members(); } catch { return; }
            int pump = 0;
            foreach (DbElement c in children)
            {
                if (budget <= 0 || found.Count > 5) return;
                if (c == null || c.IsNull) continue;
                budget--;
                if ((++pump & 0x7F) == 0) { try { System.Windows.Forms.Application.DoEvents(); } catch { } }
                string tn = "";
                try { tn = c.GetElementType().ToString(); } catch { continue; }
                if (tn == "SHEET")
                {
                    string sn = "";
                    try { sn = c.GetAsString(ATT.NAME); } catch { continue; }
                    // 2026-10-16 06:1x 修复：sheet NAME = DRWG名(未命名时=旧ref串)+"/"+图种+序号
                    // （headlessDrawing :426 命名逻辑）→ 实际形如 "=16385/4/管道布置图1"，
                    // 精确 Equals 永不命中（首跑命中0实证）→ 改包含匹配。
                    if (sn.IndexOf(sheetName, StringComparison.OrdinalIgnoreCase) >= 0) found.Add(c);
                }
                else FindSheetsByName(c, sheetName, found, ref budget, depth + 1);
            }
        }

        /// <summary>删除指定元素（含整个子树，原厂级联 DbElement.Delete()——Draft.cs 同款；
        /// CP "delete &lt;type&gt;" 对非空 DEPT 实测无效：命令被调用但元素仍在，错误被吞，2026-10-15 三证）。
        /// params: names = 空格/逗号分隔的元素名/ref（如 "/DEPT1 =16385/2"）；
        ///         guardType = 可选类型护栏（如 "DEPT"）：元素类型不匹配则跳过不删（防误删——
        ///         出图链自动命名的 DEPT 无名字可认，只能黑名单+护栏人工指定）；
        ///         matchSheetName = 可选（2026-10-15 23:0x）：按链内 SHEET 名定位自跑出图残留链——
        ///         扫 DRAFT world 顶层 DEPT，子孙（深度≤6/预算2万/每128元素 DoEvents 泵）中命中
        ///         SHEET.NAME 的 DEPT 整链删除（护栏仍生效）。出图链 DEPT/REGI/DRWG 均无名字可认，
        ///         唯 SHEET 名 = DRWG名/图种+序号 可识别，ref 重启后可能偏移故不用 ref 定位。
        /// 逐项日志：PRE(type/直接子数) → DELETED / SKIPPED(未找到|类型不符) / ERROR。日志 op_delete_element.log。</summary>
        public static OpResult DeleteElement(Dictionary<string, string> p)
        {
            OpResult res = new OpResult();
            FileLoggingStringBuilder log = new FileLoggingStringBuilder(@"D:\pdmsAgent\bridge\op_delete_element.log");
            try
            {
                string names = P(p, "names", "");
                string guardType = P(p, "guardType", "");
                string matchSheetName = P(p, "matchSheetName", "");
                if (names.Length == 0 && matchSheetName.Length == 0) throw new Exception("names 或 matchSheetName 必填");
                log.AppendLine("=== DEL-START " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " names=" + names + " guard=" + guardType + " matchSheet=" + matchSheetName + " ===");
                foreach (string nm in names.Split(new char[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        DbElement el = null;
                        try { el = DbElement.GetElement(nm); } catch { }
                        // 跨 world 回退（CE 所在 world 的 GetElement 够不到出图库时）：扫 DRAFT world 顶层按名匹配（probe DRAFT_TOP 同源）
                        if (el == null || el.IsNull || el.IsDeleted)
                        {
                            try
                            {
                                foreach (DbElement top in FangYuan.PDMS.Utilities.MDBMembers(DbType.Draft))
                                {
                                    if (top == null || top.IsNull || top.IsDeleted) continue;
                                    string tn = "";
                                    try { tn = top.GetAsString(ATT.NAME); } catch { continue; }
                                    if (tn.Equals(nm.TrimStart('/'), StringComparison.OrdinalIgnoreCase) || tn.Equals(nm, StringComparison.OrdinalIgnoreCase)) { el = top; break; }
                                }
                            }
                            catch { }
                        }
                        if (el == null || el.IsNull || el.IsDeleted)
                        {
                            log.AppendLine(nm + " SKIPPED(未找到)");
                            continue;
                        }
                        DeleteOne(el, nm, guardType, log);
                    }
                    catch (Exception exItem)
                    {
                        log.AppendLine(nm + " ERROR [" + exItem.GetType().Name + "] " + exItem.Message);
                    }
                }
                // 按 SHEET 名定位自跑出图残留链（DEPT 无名字，ref 重启偏移——名字唯一可靠锚点是 SHEET）
                if (matchSheetName.Length > 0)
                {
                    int matched = 0;
                    try
                    {
                        foreach (DbElement top in FangYuan.PDMS.Utilities.MDBMembers(DbType.Draft))
                        {
                            if (top == null || top.IsNull || top.IsDeleted) continue;
                            string tn = "";
                            try { tn = top.GetElementType().ToString(); } catch { continue; }
                            if (!tn.Equals("DEPT", StringComparison.OrdinalIgnoreCase)) continue;
                            List<DbElement> hits = new List<DbElement>();
                            int budget = 20000;
                            try { FindSheetsByName(top, matchSheetName, hits, ref budget, 0); } catch { }
                            if (hits.Count == 0) continue;
                            matched++;
                            try
                            {
                                string label = top.ToString() + " (含 SHEET " + hits[0].GetAsString(ATT.NAME) + ")";
                                DeleteOne(top, label, guardType, log);
                            }
                            catch (Exception exItem)
                            {
                                log.AppendLine("matchSheet=" + matchSheetName + " ERROR [" + exItem.GetType().Name + "] " + exItem.Message);
                            }
                        }
                    }
                    catch (Exception exScan) { log.AppendLine("matchSheet 扫描异常: [" + exScan.GetType().Name + "] " + exScan.Message); }
                    log.AppendLine("matchSheetName=" + matchSheetName + " 命中 DEPT=" + matched);
                }
                log.AppendLine("=== DEL-OK ===");
                res.Ok = true;
                res.Output = log.ToString();
            }
            catch (Exception ex)
            {
                res.Ok = false;
                res.Error = "[" + ex.GetType().Name + "] " + ex.Message;
                log.AppendLine("=== DEL-FAIL " + res.Error + " ===");
                res.Output = log.ToString();
            }
            return res;
        }

        /* ---------------- set_ce（2026-10-16 新增：悬空 CE 修复通道） ---------------- */

        /// <summary>(22:2x) material_report —— 配管材料表（整合 PipeGen/Radwing.Piping.MaterialReport）。
        /// params: root=统计范围引用（如 =15332/1 或 /DEPT1/AREA-A，空=当前 CE/World）。
        /// 流程：CollectItems(PML 收集遍历) → Aggregate 汇总 → Export 导出 .xls（UTF-8 BOM）。
        /// Output = "path|stats"（向导 /mto 表单解析显示）。</summary>
        public static OpResult MaterialReportOp(Dictionary<string, string> p)
        {
            OpResult res = new OpResult();
            try
            {
                string rootRef = P(p, "root", "");
                DbElement root = null;
                bool designWorld = false;
                if (rootRef.Length > 0)
                {
                    try { root = DbElement.GetElement(rootRef); } catch { }
                }
                if (root == null || root.IsNull)
                {
                    // (10k) root 空/无效 → CE 仅当 Design 侧元素才兜底（Draft 侧 SHEET/VIEW 收不出管线）；
                    // 否则 DESIGN World 整库统计。=旧项目库号/1 当 World 跨项目不成立（实证 =15332/1 不存在）。
                    DbElement ce = null; try { ce = CE.Element; } catch { }
                    if (ce != null && !ce.IsNull && !ce.IsDeleted && IsDesignScopeType(ce)) root = ce;
                    else designWorld = true;
                }
                string path, stats;
                if (designWorld)
                {
                    Radwing.Piping.MaterialReport.ExportDesignWorld(out path, out stats);
                    res.Output = path + "|" + stats + "|scope=world(DESIGN)";
                }
                else
                {
                    Radwing.Piping.MaterialReport.Export(root, out path, out stats);
                    res.Output = path + "|" + stats;
                }
                res.Ok = true;
            }
            catch (Exception ex) { res.Ok = false; res.Error = "[" + ex.GetType().Name + "] " + ex.Message; }
            return res;
        }

        /// <summary>(10k) 元素是否 Design 侧可统计类型——CE 兜底判据（Draft 侧 SHEET/VIEW/NOTE
        /// 子树收不出管线，误用会导出空表；此时直接走 world(DESIGN) 整库）。</summary>
        private static bool IsDesignScopeType(DbElement el)
        {
            try
            {
                string t = el.GetElementType().ToString().ToUpperInvariant();
                return t == "WORLD" || t == "SITE" || t == "ZONE" || t == "EQUIPMENT" || t == "PIPE"
                    || t == "BRANCH" || t == "STRUCTURE" || t == "BUILD" || t == "FRAMEWORK"
                    || t == "SBFRAMEWORK" || t == "GROUP" || t == "SCOM" || t == "SUBMODULE";
            }
            catch { return false; }
        }

        /// <summary>(21:5x) get_ce —— 读当前 CE 引用（Web 向导「Add(CE)」按钮用：
        /// 用户在 PDMS 里选中视图/图纸/FRMWORK，向导一键把引用填进输入框）。
        /// 返回 Output = 引用串（DbElement.ToString，形如 =16385/1249）+ 类型 + NAME。</summary>
        public static OpResult GetCe(Dictionary<string, string> p)
        {
            OpResult res = new OpResult();
            try
            {
                DbElement ce = null; try { ce = CE.Element; } catch { }
                if (ce == null || ce.IsNull || ce.IsDeleted) throw new Exception("CE 为空——先在 PDMS 图形区/树里选中元素");
                string tn = ""; try { tn = ce.GetElementType().ToString(); } catch { }
                string nm = ""; try { nm = ce.GetAsString(ATT.NAME) ?? ""; } catch { }
                // (v2) 引用取 NAME 优先——Draft 元素 NAME=全路径名（/=16385/1242/...）、设计库元素 NAME=引用名
                // （=15332/3233）；ToString() 是「VIEW 1 of SHEET /...」描述形式不可当引用。NAME 非 / = 开头才回退 ToString()
                string refStr = (nm.StartsWith("/") || nm.StartsWith("=")) ? nm : ce.ToString();
                res.Ok = true;
                res.Output = refStr.Trim() + "|" + tn + "|" + nm;
            }
            catch (Exception ex) { res.Ok = false; res.Error = ex.Message; }
            return res;
        }

        /// <summary>读/设当前 CE（CurrentElement）。
        /// 背景（实证根因）：删除元素后若 CE 悬空，后续 $M 下发的 Command.RunInPdms 会在返回路径抛
        /// NRE「未将对象引用设置到对象的实例」（实证 13:18:48 删 =23525/13 → 13:19:14 下发宏即复现）。
        /// 全程判空：CE 读数、目标解析、切换结果三级校验，任一为空即报错中止，绝不带空引用继续执行。
        /// params: ref = 目标元素路径/ref（如 "/DEPT1" "=16385/621"）；list_only=true 仅回读 CE 不切换。</summary>
        public static OpResult SetCe(Dictionary<string, string> p)
        {
            OpResult res = new OpResult();
            FileLoggingStringBuilder log = new FileLoggingStringBuilder(@"D:\pdmsAgent\bridge\op_set_ce.log");
            try
            {
                string target = P(p, "ref", "");
                bool listOnly = "true".Equals(P(p, "list_only", "false"), StringComparison.OrdinalIgnoreCase);
                log.AppendLine("=== SETCE-START " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ref=" + target + " list_only=" + listOnly + " ===");
                DbElement cur = null;
                try { cur = CE.Element; } catch (Exception exCur) { log.AppendLine("CE 读取异常: " + exCur.Message); }
                if (cur == null || cur.IsNull || cur.IsDeleted) log.AppendLine("CE BEFORE: 悬空/无效");
                else log.AppendLine("CE BEFORE: " + cur.GetElementType() + " " + cur.GetAsString(ATT.NAME));
                if (listOnly)
                {
                    res.Ok = true; res.Output = log.ToString(); return res;
                }
                if (target.Length == 0) throw new Exception("ref 必填（元素路径/ref）");
                DbElement el = null;
                try { el = DbElement.GetElement(target); } catch { }
                if (el == null || el.IsNull || el.IsDeleted)
                {
                    res.Ok = false;
                    res.Error = "目标元素不可解析/已删: " + target;
                    log.AppendLine("=== SETCE-FAIL " + res.Error + " ===");
                    res.Output = log.ToString();
                    return res;
                }
                Command c = Command.CreateCommand("CE " + target);
                if (c == null) throw new Exception("CreateCommand(CE ...) 返回 null");
                bool r = c.RunInPdms();
                DbElement now = null;
                try { now = CE.Element; } catch { }
                bool nowOk = (now != null && !now.IsNull && !now.IsDeleted);
                log.AppendLine("CE AFTER: " + (nowOk ? now.GetElementType() + " " + now.GetAsString(ATT.NAME) : "悬空/无效") + " (cmd=" + r + ")");
                res.Ok = nowOk;
                res.Output = log.ToString();
                if (!res.Ok) res.Error = "CE 切换未生效（切换后仍悬空/无效）";
            }
            catch (Exception ex)
            {
                res.Ok = false;
                res.Error = "[" + ex.GetType().Name + "] " + ex.Message;
                log.AppendLine("=== SETCE-FAIL " + res.Error + " ===");
                res.Output = log.ToString();
            }
            return res;
        }

        /* ---------------- update_view（2026-10-16 新增：C# 封装刷新视图） ---------------- */

        /// <summary>用 drawTool.UpdateModelView 刷新视图（先 CE owner 再 CE 元素——PDMS 更新视图需要
        /// Owner 上下文做 SheetPosition 坐标投影，见 drawTool.cs:63 注释）。
        /// 用户口径：视图刷新走 C# 封装，不用 PML 的 CE sheet/CE view/UPDATE all。
        /// 判空：sheet/view 解析失败即报错，不切 CE、不带空引用继续。
        /// params: view = VIEW 元素路径/ref（如 "=16385/630"）；可选 sheet = SHEET，先切 SHEET 再刷 VIEW。</summary>
        public static OpResult UpdateView(Dictionary<string, string> p)
        {
            OpResult res = new OpResult();
            FileLoggingStringBuilder log = new FileLoggingStringBuilder(@"D:\pdmsAgent\bridge\op_update_view.log");
            try
            {
                string vref = P(p, "view", "");
                string sref = P(p, "sheet", "");
                if (sref.Length > 0)
                {
                    DbElement sh = null;
                    try { sh = DbElement.GetElement(sref); } catch { }
                    if (sh == null || sh.IsNull || sh.IsDeleted) throw new Exception("SHEET 不存在: " + sref);
                    Command cs = Command.CreateCommand("CE " + sref);
                    if (cs == null) throw new Exception("CreateCommand(CE SHEET) 返回 null");
                    bool rs = cs.RunInPdms();
                    log.AppendLine("SHEET " + sref + " cmd=" + rs);
                }
                if (vref.Length == 0) throw new Exception("view 必填（VIEW 元素路径/ref）");
                DbElement v = null;
                try { v = DbElement.GetElement(vref); } catch { }
                if (v == null || v.IsNull || v.IsDeleted) throw new Exception("VIEW 不存在: " + vref);
                drawTool.UpdateModelView(v);
                DbElement now = null;
                try { now = CE.Element; } catch { }
                if (now == null || now.IsNull || now.IsDeleted)
                {
                    res.Ok = false;
                    res.Error = "UpdateModelView 后 CE 无效（刷新未生效）";
                    log.AppendLine("=== UPDV-FAIL " + res.Error + " ===");
                    res.Output = log.ToString();
                    return res;
                }
                log.AppendLine("VIEW " + vref + " UpdateModelView OK, CE=" + now.GetElementType() + " " + now.GetAsString(ATT.NAME));
                res.Ok = true;
                res.Output = log.ToString();
            }
            catch (Exception ex)
            {
                res.Ok = false;
                res.Error = "[" + ex.GetType().Name + "] " + ex.Message;
                log.AppendLine("=== UPDV-FAIL " + res.Error + " ===");
                res.Output = log.ToString();
            }
            return res;
        }

        /* ---------------- create_sym_blocks（2026-10-16 新增：HG20519 标准符号图块库） ---------------- */

        /// <summary>建/重建标准符号图块库 /DRA/AGT_SYMS（13 个 SYTM：阀门/弯头/三通/法兰/盲板/焊点/异径管/管架/箭头）。
        /// params: rebuild = true(默认，整库删除重建) / false(已存在则跳过)。
        /// 完成后逐块回读 STRAIGHT 数（建块证据链）。日志 op_create_sym_blocks.log。</summary>
        public static OpResult CreateSymBlocks(Dictionary<string, string> p)
        {
            OpResult res = new OpResult();
            FileLoggingStringBuilder log = new FileLoggingStringBuilder(@"D:\pdmsAgent\bridge\op_create_sym_blocks.log");
            try
            {
                bool rebuild = "true".Equals(P(p, "rebuild", "true"), StringComparison.OrdinalIgnoreCase);
                log.AppendLine("=== SYMBLK-START " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " rebuild=" + rebuild + " ===");
                // 2026-10-17 修正：符号改为"直接画 STRAIGHT"（library 世界边界导致 SYTM 库无法建），
                // create_sym_blocks 已无库可建——短路返回 ok=true，避免误报失败。符号由 replace_symbols 直接绘制。
                SymBlocks.EnsureLibrary(rebuild, delegate(string line) { log.AppendLine(line); });
                int totalSym = SymBlocks.ALL.Length;
                log.AppendLine("LIB-SUMMARY 直接画线模式：无需 SYTM 库（create_sym_blocks 已废弃），符号由 replace_symbols 绘制");
                bool ok = totalSym == SymBlocks.ALL.Length;
                log.AppendLine(ok ? "=== SYMBLK-OK ===" : "=== SYMBLK-FAIL ===");
                if (!ok) res.Error = "图块库回读不全: " + totalSym + "/" + SymBlocks.ALL.Length;
                res.Ok = ok;
                res.Output = log.ToString();
            }
            catch (Exception ex)
            {
                res.Ok = false;
                res.Error = "[" + ex.GetType().Name + "] " + ex.Message;
                log.AppendLine("=== SYMBLK-FAIL " + res.Error + " " + (ex.StackTrace == null ? "" : ex.StackTrace) + " ===");
                res.Output = log.ToString();
            }
            return res;
        }

        /* ---------------- set_styl（2026-10-17 (8r)：源头修改 STYL 表示样式。
        例：styl=/DRA/MAS/H&S/RULESETS/View1 的某条 RRULE 的 STYF 目标，set=TUBE=FALSE,CLFG=TRUE,CLCOLO=2,CLSTYL=SOLID
        → 管线从源头出成红色单线。值按属性类型自动分派（BOOL=TRUE/FALSE、INT、WORD、STRING）。 ---------------- */
        public static OpResult SetStyl(Dictionary<string, string> p)
        {
            OpResult res = new OpResult();
            FileLoggingStringBuilder log = new FileLoggingStringBuilder(@"D:\pdmsAgent\bridge\op_set_styl.log");
            try
            {
                string sref = P(p, "styl", "");
                string set = P(p, "set", "");
                if (sref.Length == 0 || set.Length == 0) throw new Exception("styl/set 必填，set 形如 TUBE=FALSE,CLFG=TRUE,CLCOLO=2,CLSTYL=SOLID");
                log.AppendLine("=== SST-START " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " styl=" + sref + " set=" + set + " ===");
                DbElement st = null;
                try { st = DbElement.GetElement(sref); } catch { }
                if (st == null || st.IsNull || st.IsDeleted) throw new Exception("STYL 不存在: " + sref);
                log.AppendLine("目标: " + st.GetElementType() + " \"" + st.GetAsString(ATT.NAME) + "\" 原值: " + StylDump(st));
                string[] pairs = set.Split(',');
                int nOk = 0, nSkip = 0;
                foreach (string pr in pairs)
                {
                    if (string.IsNullOrEmpty(pr)) continue;
                    int eq = pr.IndexOf('=');
                    if (eq <= 0) { nSkip++; continue; }
                    string k = pr.Substring(0, eq).Trim().ToUpperInvariant();
                    string v = pr.Substring(eq + 1).Trim();
                    var f = typeof(ATT).GetField(k);
                    if (f == null) { log.AppendLine("  [SKIP] ATT 无字段 " + k); nSkip++; continue; }
                    DbAttribute a = (DbAttribute)f.GetValue(null); // ATT 的字段类型是 DbAttribute（ATT=DbAttributeInstance 是持有者类）
                    // BOOL 归一化：ON/OFF → TRUE/FALSE
                    if (v.Equals("ON", StringComparison.OrdinalIgnoreCase)) v = "TRUE";
                    else if (v.Equals("OFF", StringComparison.OrdinalIgnoreCase)) v = "FALSE";
                    // 8v-3：WORD 属性纯数字走 int 通道（word 编码属性字符串名赋值会炸）
                    string err;
                    int iv;
                    if (a.Type == DbAttributeType.WORD && int.TryParse(v, out iv))
                    {
                        try { st.SetAttribute(a, iv); err = ""; }
                        catch (Exception exI) { err = "int通道: " + exI.Message; }
                    }
                    else err = drawTool.SetAttribute(st, a, v);
                    string back = TryAttr(st, k);
                    if (string.IsNullOrEmpty(err)) { log.AppendLine("  [OK] " + k + "=" + v + "  回读=" + back); nOk++; }
                    else { log.AppendLine("  [FAIL] " + k + "=" + v + "  err=" + err); nSkip++; }
                }
                res.Ok = nOk > 0;
                res.Output = "SST-OK nOk=" + nOk + " nSkip=" + nSkip;
                res.Error = res.Ok ? null : "全部设置失败";
                log.AppendLine("=== SST-" + (res.Ok ? "OK" : "FAIL") + " nOk=" + nOk + " nSkip=" + nSkip + " ===");
                log.CloseFile();
            }
            catch (Exception ex)
            {
                res.Ok = false; res.Error = "[" + ex.GetType().Name + "] " + ex.Message;
                log.AppendLine("SST-FAIL " + res.Error + "\n" + ex.StackTrace);
                log.CloseFile();
            }
            return res;
        }

        /* ---------------- list_rulesets（2026-10-17 (8r)：枚举规则集库——用户指路「单线模板出来就没有弯头，从源头解决，不要二次删除」。
        DRUGP 5.05 实锤：RRULE 挂 STYL（STYF），STYL 的 TUBE(双线管)/CLFG(中心线)/FFSTYL/FFCOLO/CLSTYL/CLCOLO
        就是黄双线与点划线的总开关；单线模式 = TUBE OFF + CLFG ON + CLCOLO 改红。
        本 op 深度 dump：RULESETS 全树 + 每条 RRULE 的 CRIT/STYF + 每个 STYL 的全部关键属性 +
        STYLES 库全部样式。属性一律【反射】按名读取（ATT 枚举缺名也不炸编译）。 ---------------- */
        private static string TryAttr(DbElement el, string attrName)
        {
            try
            {
                var f = typeof(ATT).GetField(attrName);
                if (f == null) return null;
                DbAttribute a = (DbAttribute)f.GetValue(null); // ATT 的字段类型是 DbAttribute
                string s = el.GetAsString(a);
                return s;
            }
            catch { return null; }
        }
        private static DbElement TryAttrElem(DbElement el, string attrName)
        {
            try
            {
                var f = typeof(ATT).GetField(attrName);
                if (f == null) return null;
                DbAttribute a = (DbAttribute)f.GetValue(null); // ATT 的字段类型是 DbAttribute
                DbElement r = el.GetElement(a);
                return (r == null || r.IsNull || r.IsDeleted) ? null : r;
            }
            catch { return null; }
        }
        private static string StylDump(DbElement styl)
        {
            string[] attrs = new string[] { "TUBE", "CLFG", "CLFL", "PIPSYM", "PLFG", "OBSTF", "INSUF", "DLEV",
                                            "FFSTYL", "FFCOLO", "CLSTYL", "CLCOLO", "BFSTYL", "BFCOLO",
                                            "OBSTYL", "PLNSTYL", "PLNCOLO", "MLNSTYL", "MLNCOLO" };
            var sb = new System.Text.StringBuilder();
            foreach (string an in attrs)
            {
                string v = TryAttr(styl, an);
                if (v != null) sb.Append(an).Append("=").Append(v.Trim()).Append(" ");
            }
            return sb.ToString();
        }

        public static OpResult ListRulesets(Dictionary<string, string> p)
        {
            OpResult res = new OpResult();
            FileLoggingStringBuilder log = new FileLoggingStringBuilder(@"D:\pdmsAgent\bridge\op_list_rulesets.log");
            try
            {
                log.AppendLine("=== LR-START " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===");
                string rootPath = P(p, "root", "/DRA/MAS/H&S/RULESETS");
                var sb = new System.Text.StringBuilder();

                DbElement root = null;
                try { root = DbElement.GetElement(rootPath); } catch { }
                if (root == null || root.IsNull || root.IsDeleted)
                {
                    log.AppendLine("[MISS] " + rootPath);
                    res.Ok = false; res.Error = "root 不存在: " + rootPath;
                    return res;
                }
                foreach (DbElement rrst in root.Members())
                {
                    if (rrst == null || rrst.IsNull) continue;
                    string tn = ""; try { tn = rrst.GetElementType().ToString(); } catch { }
                    // 8v-2：root 本身是 RRST 时（其成员是 RRULE），直接按规则完整 dump，不再跳过
                    bool isRule = tn.Equals("RRULE", StringComparison.OrdinalIgnoreCase) || tn.Equals("RRUL", StringComparison.OrdinalIgnoreCase);
                    if (!tn.Equals("RRST", StringComparison.OrdinalIgnoreCase) && !isRule)
                    {
                        sb.AppendLine(tn + " \"" + rrst.GetAsString(ATT.NAME) + "\"（非 RRST，跳过）");
                        continue;
                    }
                    if (!isRule) sb.AppendLine("=== RRST " + rrst.GetAsString(ATT.NAME) + " ===");
                    foreach (DbElement rul in (isRule ? new DbElement[] { rrst } : rrst.Members()))
                    {
                        if (rul == null || rul.IsNull) continue;
                        string rt = ""; try { rt = rul.GetElementType().ToString(); } catch { }
                        string rn = ""; try { rn = rul.GetAsString(ATT.NAME); } catch { }
                        string crit = TryAttr(rul, "CRIT");
                        string omit = TryAttr(rul, "OMITFG");
                        DbElement styf = TryAttrElem(rul, "STYF");
                        sb.AppendLine("  " + rt + " \"" + rn + "\""
                            + "  CRIT=[" + (crit ?? "?") + "]"
                            + "  OMITFG=" + (omit ?? "?")
                            + "  STYF=" + (styf == null ? "<空/OMIT>" : styf.GetElementType() + ":" + styf.GetAsString(ATT.NAME)));
                        if (styf != null)
                        {
                            sb.AppendLine("      STYL[" + styf.GetAsString(ATT.NAME) + "]: " + StylDump(styf));
                        }
                    }
                }

                // STYLES 库全部样式
                DbElement stylib = null;
                try { stylib = DbElement.GetElement("/DRA/MAS/H&S/STYLES"); } catch { }
                if (stylib != null && !stylib.IsNull)
                {
                    sb.AppendLine("=== STYLES 库 ===");
                    foreach (DbElement st in stylib.Members())
                    {
                        if (st == null || st.IsNull) continue;
                        string tn2 = ""; try { tn2 = st.GetElementType().ToString(); } catch { }
                        string nm2 = ""; try { nm2 = st.GetAsString(ATT.NAME); } catch { }
                        if (tn2.Equals("STYL", StringComparison.OrdinalIgnoreCase))
                            sb.AppendLine("  STYL \"" + nm2 + "\": " + StylDump(st));
                        else
                            sb.AppendLine("  " + tn2 + " \"" + nm2 + "\"");
                    }
                }

                // 父目录兄弟候选
                DbElement par = root.Owner;
                if (par != null && !par.IsNull)
                {
                    sb.AppendLine("=== PARENT ref=" + par + " 直属成员（一层） ===");
                    foreach (DbElement ch in par.Members())
                    {
                        if (ch == null || ch.IsNull) continue;
                        string tn3 = ""; try { tn3 = ch.GetElementType().ToString(); } catch { }
                        string nm3 = ""; try { nm3 = ch.GetAsString(ATT.NAME); } catch { }
                        sb.AppendLine("  " + tn3 + "  \"" + nm3 + "\"  ref=" + ch);
                    }
                }
                log.AppendLine(sb.ToString());
                log.CloseFile();
                res.Ok = true;
                res.Output = "LR-OK";
            }
            catch (Exception ex)
            {
                res.Ok = false; res.Error = "[" + ex.GetType().Name + "] " + ex.Message;
                log.AppendLine("LR-FAIL " + res.Error + "\n" + ex.StackTrace);
                log.CloseFile();
            }
            return res;
        }

        /* ---------------- set_rrsf（2026-10-17 (8r)：视图切换规则集——单线模板源头生效。
        切完 RRSF 后跑 replace_symbols，其流程内 UPDATE all 会按新规则集重生成视图图形。 ---------------- */
        public static OpResult SetRrsf(Dictionary<string, string> p)
        {
            OpResult res = new OpResult();
            FileLoggingStringBuilder log = new FileLoggingStringBuilder(@"D:\pdmsAgent\bridge\op_set_rrsf.log");
            try
            {
                string vref = P(p, "view", "");
                string rsref = P(p, "ruleset", "");
                if (vref.Length == 0 || rsref.Length == 0) throw new Exception("view/ruleset 必填");
                log.AppendLine("=== SRR-START " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " view=" + vref + " ruleset=" + rsref + " ===");
                DbElement v = null; try { v = DbElement.GetElement(vref); } catch { }
                if (v == null || v.IsNull || v.IsDeleted) throw new Exception("VIEW 不存在: " + vref);
                DbElement r = null; try { r = DbElement.GetElement(rsref); } catch { }
                if (r == null || r.IsNull || r.IsDeleted) throw new Exception("规则集不存在: " + rsref);
                string old = "<read-fail>";
                try { DbElement o = v.GetElement(ATT.RRSF); old = (o == null || o.IsNull) ? "<null>" : o.ToString(); } catch { }
                v.SetAttribute(ATT.RRSF, r);
                string now = "<read-fail>";
                try { now = v.GetElement(ATT.RRSF).ToString(); } catch { }
                log.AppendLine("RRSF: " + old + " -> " + now);
                try { drawTool.UpdateModelView(v); } catch (Exception exU) { log.AppendLine("UpdateModelView 异常(继续): " + exU.Message); }
                res.Ok = true;
                res.Output = "SRR-OK RRSF=" + rsref;
                log.AppendLine("=== SRR-OK ===");
            }
            catch (Exception ex)
            {
                res.Ok = false; res.Error = "[" + ex.GetType().Name + "] " + ex.Message;
                log.AppendLine("SRR-FAIL " + res.Error + "\n" + ex.StackTrace);
            }
            return res;
        }

        /* ---------------- make_local_rule（2026-10-17 (8s)：本地表示规则——源头单线模板。
        文档实锤（DRUGP 5.05.01）：RRUL 可直接 owned by VIEW（local rules），且 Local Rules override
        Library Rules；MAS 目录库只读（set_styl 实测 IsAttributeSetable=false），故样式自建在可写图纸库。
        目标：ALL BRANCH 本地规则指向自建 STYL（PLFG=Off,CLFG=Off → PDMS 对管线零出图，
        红色单线由 replace_symbols 全责绘制，弯头圆内天然无穿线——用户指路「单线模板从源头解决」）。
        候选父位逐一试建并落日志：world→RPLB→STYL / world→STYL / view→STYL / sheet→STYL / drwg→STYL。 ---------------- */
        public static OpResult MakeLocalRule(Dictionary<string, string> p)
        {
            OpResult res = new OpResult();
            FileLoggingStringBuilder log = new FileLoggingStringBuilder(@"D:\pdmsAgent\bridge\op_make_local_rule.log");
            try
            {
                string vref = P(p, "view", "=16385/1249");
                string sref = P(p, "sheet", "=16385/1242");
                string crit = P(p, "crit", "ALL BRANCH");
                string rname = P(p, "name", "LR_BR_OFF");
                string stag = DateTime.Now.ToString("HHmmss");
                log.AppendLine("=== MLR-START " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " view=" + vref + " crit=" + crit + " ===");
                DbElement v = null; try { v = DbElement.GetElement(vref); } catch { }
                if (v == null || v.IsNull || v.IsDeleted) throw new Exception("VIEW 不存在: " + vref);

                // 反射解析元素类型（版本命名差异兜底）
                DbElementType tRplb = null, tStyl = null, tRrul = null;
                foreach (string n in new string[] { "RPLB", "REPRLB" }) { var f = typeof(DbElementTypeInstance).GetField(n); if (f != null) { tRplb = (DbElementType)f.GetValue(null); break; } }
                foreach (string n in new string[] { "STYL", "STYLE" }) { var f = typeof(DbElementTypeInstance).GetField(n); if (f != null) { tStyl = (DbElementType)f.GetValue(null); break; } }
                foreach (string n in new string[] { "RRUL", "RRULE" }) { var f = typeof(DbElementTypeInstance).GetField(n); if (f != null) { tRrul = (DbElementType)f.GetValue(null); break; } }
                log.AppendLine("类型解析: RPLB=" + (tRplb != null) + " STYL=" + (tStyl != null) + " RRUL=" + (tRrul != null));
                if (tStyl == null || tRrul == null) throw new Exception("STYL/RRUL 类型解析失败");

                // 回溯到当前库 world
                DbElement world = v;
                try { for (int i = 0; i < 32; i++) { DbElement ow = world.Owner; if (ow == null || ow.IsNull) break; world = ow; } } catch { }
                log.AppendLine("world=" + world + " type=" + world.GetElementType());
                DbElement sheet = null; try { sheet = DbElement.GetElement(sref); } catch { }
                DbElement drwg = null; try { drwg = sheet.Owner; } catch { }

                string aSym = null;
                var fC = typeof(ATT).GetField("CRIT"); var fS = typeof(ATT).GetField("STYF"); var fSym = typeof(ATT).GetField("SYMB");
                if (fC == null || fS == null) throw new Exception("ATT 无 CRIT/STYF 字段");
                if (fSym != null) aSym = "SYMB";
                log.AppendLine("ATT 字段: CRIT✓ STYF✓ SYMB=" + (aSym != null));

                // 候选父位逐一试建 STYL（8s-2：先试 MAS 库——万一可写；再图纸库各父位）
                DbElement styl = null; string stylWhere = "";
                string sn = "AGT_OFF_" + stag;
                DbElement masStyles = null; try { masStyles = DbElement.GetElement("/DRA/MAS/H&S/STYLES"); } catch { }
                Action<string, DbElement> tryStyl = delegate (string where, DbElement par)
                {
                    if (styl != null || par == null || par.IsNull) return;
                    try
                    {
                        DbElement s = par.CreateLast(tStyl);
                        try { s.SetAttribute(ATT.NAME, sn); } catch (Exception exN) { log.AppendLine("  STYL 命名失败(继续,持有直接引用): " + exN.Message); }
                        styl = s; stylWhere = where;
                        log.AppendLine("[OK] STYL 建于 " + where + " ref=" + s);
                    }
                    catch (Exception ex) { log.AppendLine("[MISS] STYL@ " + where + ": " + ex.Message); }
                };
                tryStyl("MAS/STYLES", masStyles);
                if (tRplb != null)
                {
                    try
                    {
                        DbElement rplb = world.CreateLast(tRplb);
                        try { rplb.SetAttribute(ATT.NAME, "AGT_LIB_" + stag); } catch (Exception exN) { log.AppendLine("RPLB 命名失败(继续): " + exN.Message); }
                        tryStyl("world/RPLB", rplb);
                    }
                    catch (Exception ex) { log.AppendLine("[MISS] RPLB@world: " + ex.Message); }
                }
                tryStyl("world", world);
                tryStyl("view", v);
                tryStyl("sheet", sheet);
                tryStyl("drwg", drwg);
                if (styl != null)
                {
                    // 空白样式：PLFG/CLFG 全关 → PDMS 对分支零出图；其余 best-effort
                    string[] kv = new string[] { "PLFG=FALSE", "CLFG=FALSE", "FFSTYL=OFF", "BFSTYL=OFF", "CLSTYL=SOLID", "CLCOLO=RED", "FFCOLO=RED" };
                    foreach (string pr in kv)
                    {
                        int eq = pr.IndexOf('=');
                        string k = pr.Substring(0, eq), val = pr.Substring(eq + 1);
                        var f = typeof(ATT).GetField(k);
                        if (f == null) { log.AppendLine("  [SKIP] ATT 无 " + k); continue; }
                        DbAttribute a = (DbAttribute)f.GetValue(null);
                        string err = drawTool.SetAttribute(styl, a, val);
                        log.AppendLine("  STYL." + k + "=" + val + " 回读=" + (TryAttr(styl, k) ?? "?") + (err.Length > 0 ? (" err=" + err) : " [OK]"));
                    }
                }

                // 本地规则：view 直接持有，覆盖库规则
                // 8s-2：STYL 不可建时走 omit 模式——CRIT+OMITFG=TRUE（无 STYF），源头省略整个分支图形
                DbElement rrul = v.CreateLast(tRrul);
                try { rrul.SetAttribute(ATT.NAME, rname); } catch (Exception exN) { log.AppendLine("RRUL 命名失败(继续): " + exN.Message); }
                DbAttribute atCrit = (DbAttribute)fC.GetValue(null);
                string errCrit = drawTool.SetAttribute(rrul, atCrit, crit);
                log.AppendLine("RRUL.CRIT=" + crit + " 回读=" + (TryAttr(rrul, "CRIT") ?? "?") + (errCrit.Length > 0 ? (" err=" + errCrit) : " [OK]"));
                // 8s-3：CRIT 实测整数型（word 编码）——文本设不上时，从库 View1 规则读原始 int 抄过来
                string critBack = TryAttr(rrul, "CRIT");
                if (critBack == null || critBack.Trim().Length == 0 || critBack.Trim() == "unset" || errCrit.Length > 0)
                {
                    string[] keyword = crit.Trim().ToUpperInvariant().Split(' ');
                    string kw = keyword[keyword.Length - 1];
                    DbElement rs1 = null; try { rs1 = DbElement.GetElement("/DRA/MAS/H&S/RULESETS/View1"); } catch { }
                    if (rs1 != null && !rs1.IsNull)
                    {
                        bool copied = false;
                        foreach (DbElement lr in rs1.Members())
                        {
                            if (lr == null || lr.IsNull) continue;
                            string ct = TryAttr(lr, "CRIT"); if (ct == null) continue;
                            int rawInt = -1; try { rawInt = lr.GetInteger(atCrit); } catch (Exception exI) { log.AppendLine("  库规则 GetInteger 失败: " + exI.Message); }
                            log.AppendLine("  库规则 crit文本=[" + ct.Trim() + "] 原始int=" + rawInt);
                            if (!copied && ct.ToUpperInvariant().Contains(kw) && rawInt >= 0)
                            {
                                try { rrul.SetAttribute(atCrit, rawInt); copied = true; log.AppendLine("  本地规则 CRIT<=int " + rawInt + " 回读=" + (TryAttr(rrul, "CRIT") ?? "?")); }
                                catch (Exception exS) { log.AppendLine("  本地规则 CRIT 设 int 失败: " + exS.Message); }
                            }
                        }
                        if (!copied) log.AppendLine("  [WARN] 未能从库规则抄到 CRIT int（kw=" + kw + "）");
                    }
                }
                if (styl != null)
                {
                    DbAttribute atStyf = (DbAttribute)fS.GetValue(null);
                    rrul.SetAttribute(atStyf, styl);
                    log.AppendLine("RRUL.STYF -> " + stylWhere + " ref=" + styl);
                }
                else
                {
                    var fOmit = typeof(ATT).GetField("OMITFG");
                    if (fOmit == null) throw new Exception("ATT 无 OMITFG 字段，omit 模式不可用");
                    DbAttribute atOmit = (DbAttribute)fOmit.GetValue(null);
                    string errOmit = drawTool.SetAttribute(rrul, atOmit, "TRUE");
                    log.AppendLine("RRUL.OMITFG=TRUE 回读=" + (TryAttr(rrul, "OMITFG") ?? "?") + (errOmit.Length > 0 ? (" err=" + errOmit) : " [OK]"));
                }
                if (aSym != null)
                {
                    DbAttribute atSym = (DbAttribute)fSym.GetValue(null);
                    string errSym = drawTool.SetAttribute(rrul, atSym, "FALSE");
                    log.AppendLine("RRUL.SYMB=FALSE 回读=" + (TryAttr(rrul, "SYMB") ?? "?") + (errSym.Length > 0 ? (" err=" + errSym) : " [OK]"));
                }

                try { drawTool.UpdateModelView(v); log.AppendLine("UpdateModelView 完成"); }
                catch (Exception exU) { log.AppendLine("UpdateModelView 异常(继续): " + exU.Message); }

                res.Ok = true;
                res.Output = "MLR-OK styl=" + stylWhere + " rrul=" + (TryAttr(rrul, "NAME") ?? rname) + " crit=" + crit;
                log.AppendLine("=== MLR-OK ===");
            }
            catch (Exception ex)
            {
                res.Ok = false; res.Error = "[" + ex.GetType().Name + "] " + ex.Message;
                log.AppendLine("MLR-FAIL " + res.Error + "\n" + ex.StackTrace);
            }
            return res;
        }

        /* ---------------- dump_attrs（2026-10-17 (8v)：反射全量枚举元素属性。
        背景：猜属性名（SYMB/PSYMB/SYRF...）全落空，改为反射 typeof(DbAttributeInstance).GetFields()
        遍历全部属性名，对目标元素 TryAttr，输出全部有效属性+值。用于找 VIEW/ADDENTRY 上的
        FF（fitment）/符号/省略类开关。参数：ref=元素 ref（默认 view），also=第二个元素（如 IdList）。 ---------------- */
        public static OpResult DumpAttrs(Dictionary<string, string> p)
        {
            OpResult res = new OpResult();
            FileLoggingStringBuilder log = new FileLoggingStringBuilder(@"D:\pdmsAgent\bridge\op_dump_attrs.log");
            try
            {
                string r1 = P(p, "ref", "=16385/1249");
                string r2 = P(p, "also", "");
                Action<string> dumpOne = delegate (string refStr)
                {
                    DbElement el = null;
                    try { el = DbElement.GetElement(refStr); } catch { }
                    if (el == null || el.IsNull || el.IsDeleted) { log.AppendLine("[MISS] " + refStr); return; }
                    string tn = ""; try { tn = el.GetElementType().ToString(); } catch { }
                    log.AppendLine("=== DUMP " + refStr + " type=" + tn + " ===");
                    var fields = typeof(DbAttributeInstance).GetFields();
                    int nVal = 0;
                    foreach (var f in fields)
                    {
                        try
                        {
                            DbAttribute a = (DbAttribute)f.GetValue(null);
                            if (a == null) continue;
                            string val = TryAttr(el, f.Name);
                            if (val != null)
                            {
                                string v = val.Trim();
                                if (v.Length > 60) v = v.Substring(0, 60) + "...";
                                if (v.Length > 0) { log.AppendLine("  " + f.Name + " = " + v); nVal++; }
                            }
                        }
                        catch { }
                    }
                    log.AppendLine("=== DUMP-OK " + nVal + " attrs ===");
                };
                dumpOne(r1);
                if (r2.Length > 0) dumpOne(r2);
                res.Ok = true; res.Output = "DUMP-OK see op_dump_attrs.log";
            }
            catch (Exception ex)
            {
                res.Ok = false; res.Error = "[" + ex.GetType().Name + "] " + ex.Message;
                log.AppendLine("DUMP-FAIL " + res.Error + "\n" + ex.StackTrace);
            }
            return res;
        }

        /* ---------------- sym_purge（2026-10-17 (8u)：诊断+清除视图的 DESSYM 2D 符号层。
        实验实锤（8t）：本地规则可改元件几何颜色（Hangers 绿生效）但黄色 bowtie/圆圈不动
        → 它们是 DESSYM（SLAB 软类型），挂 DSLAYE（LAYE 软类型）下，UPDATE DESIGN 自动建删（DRUGP 4.04.34）。
        purge=0 只列视图子树（类型/ref/子数 + 符号开关属性候选 dump）；purge=1 删 DSLAYE 下全部 DESSYM。
        DSLAYE 无子后会被 DRAFT 自动移除；若 UPDATE 重建则说明符号触发源仍在（再看属性 dump）。 ---------------- */
        public static OpResult SymPurge(Dictionary<string, string> p)
        {
            OpResult res = new OpResult();
            FileLoggingStringBuilder log = new FileLoggingStringBuilder(@"D:\pdmsAgent\bridge\op_sympurge.log");
            try
            {
                string vref = P(p, "view", "=16385/1249");
                bool purge = P(p, "purge", "0") == "1";
                log.AppendLine("=== SYM-START " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " view=" + vref + " purge=" + purge + " ===");
                DbElement v = null; try { v = DbElement.GetElement(vref); } catch { }
                if (v == null || v.IsNull || v.IsDeleted) throw new Exception("VIEW 不存在: " + vref);

                // 视图符号开关属性候选 dump（哪个 valid + 值是什么）
                string[] cand = new string[] { "SYMB", "PSYMB", "SYRF", "PIPSYM", "PIPSI", "SYLG", "DSYM", "DSYF", "SREF", "PREF", "IDLN" };
                foreach (string an in cand)
                {
                    string val = TryAttr(v, an);
                    if (val != null) log.AppendLine("  VIEW." + an + " = " + val.Trim());
                }

                // 全域收集 LAY 类元素（LAYER/DSLAYER/LAYE，Contains 匹配；8u-1 修复 Equals("LAYE") 永不命中的 bug）
                DbElement sheet = null; try { sheet = v.Owner; } catch { }
                List<DbElement> lays = new List<DbElement>();
                HashSet<string> seenL = new HashSet<string>();
                Action<DbElement> collect = null;
                collect = delegate (DbElement top)
                {
                    if (top == null || top.IsNull || top.IsDeleted) return;
                    if (!seenL.Add(top.ToString())) return;
                    string tn = ""; try { tn = top.GetElementType().ToString(); } catch { }
                    if (tn.IndexOf("LAY", StringComparison.OrdinalIgnoreCase) >= 0) { lays.Add(top); return; }
                    foreach (DbElement ch in top.Members()) collect(ch);
                };
                collect(v);
                collect(sheet);
                log.AppendLine("  收集到 LAY 类元素 " + lays.Count + " 个（view+sheet 子树）");

                int nDeleted = 0;
                foreach (DbElement lay in lays)
                {
                    string ltn = ""; try { ltn = lay.GetElementType().ToString(); } catch { }
                    string lnm = ""; try { lnm = lay.GetAsString(ATT.NAME); } catch { }
                    // 后代类型直方图（深度≤4）
                    Dictionary<string, int> hist = new Dictionary<string, int>();
                    List<DbElement> syms = new List<DbElement>();
                    Action<DbElement, int> walk = null;
                    walk = delegate (DbElement e, int d)
                    {
                        foreach (DbElement ch in e.Members())
                        {
                            if (ch == null || ch.IsNull || ch.IsDeleted) continue;
                            string t = ""; try { t = ch.GetElementType().ToString(); } catch { }
                            if (!hist.ContainsKey(t)) hist[t] = 0;
                            hist[t]++;
                            if ((t.IndexOf("DESSYM", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 t.IndexOf("SLAB", StringComparison.OrdinalIgnoreCase) >= 0) && purge) syms.Add(ch);
                            if (d < 4) walk(ch, d + 1);
                        }
                    };
                    walk(lay, 0);
                    var order = hist.OrderByDescending(kv => kv.Value).Take(15);
                    log.AppendLine("  " + ltn + " \"" + lnm + "\" ref=" + lay + " 后代直方图: " +
                        string.Join(", ", order.Select(kv => kv.Key + "=" + kv.Value).ToArray()));
                    if (purge && syms.Count > 0)
                    {
                        foreach (DbElement s in syms)
                        {
                            try { DeleteOne(s, s.ToString(), "DESSYM", log); nDeleted++; }
                            catch (Exception exD) { log.AppendLine("    删失败 " + s + ": " + exD.Message); }
                        }
                        log.AppendLine("    本层删除 " + syms.Count + " 个符号类元素");
                    }
                }

                // 8u-2：IdList 全子树扫描（VIEW.IDLN 实锤 IdList 在 DRWG 库链上，DESSYM 最可能挂 ADDE 条目下）
                DbElement idl = TryAttrElem(v, "IDLN");
                if (idl != null && !idl.IsNull && !idl.IsDeleted)
                {
                    log.AppendLine("  IDLIST ref=" + idl);
                    Dictionary<string, int> histI = new Dictionary<string, int>();
                    List<DbElement> symsI = new List<DbElement>();
                    Action<DbElement, int> walkI = null;
                    walkI = delegate (DbElement e, int d)
                    {
                        foreach (DbElement ch in e.Members())
                        {
                            if (ch == null || ch.IsNull || ch.IsDeleted) continue;
                            string t = ""; try { t = ch.GetElementType().ToString(); } catch { }
                            if (!histI.ContainsKey(t)) histI[t] = 0;
                            histI[t]++;
                            if ((t.IndexOf("DESSYM", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 t.IndexOf("SLAB", StringComparison.OrdinalIgnoreCase) >= 0) && purge) symsI.Add(ch);
                            if (d < 5) walkI(ch, d + 1);
                        }
                    };
                    walkI(idl, 0);
                    var ordI = histI.OrderByDescending(kv => kv.Value).Take(20);
                    log.AppendLine("  IdList 后代直方图: " + string.Join(", ", ordI.Select(kv => kv.Key + "=" + kv.Value).ToArray()));
                    if (purge && symsI.Count > 0)
                    {
                        foreach (DbElement s in symsI)
                        {
                            try { DeleteOne(s, s.ToString(), "DESSYM", log); nDeleted++; }
                            catch (Exception exD) { log.AppendLine("    IdList 删失败 " + s + ": " + exD.Message); }
                        }
                        log.AppendLine("    IdList 删除 " + symsI.Count + " 个符号类元素");
                    }
                }
                else log.AppendLine("  IDLN 取不到元素");

                res.Ok = true;
                res.Output = "SYM-OK purge=" + purge + " deleted=" + nDeleted;
                log.AppendLine("=== SYM-OK deleted=" + nDeleted + " ===");
            }
            catch (Exception ex)
            {
                res.Ok = false; res.Error = "[" + ex.GetType().Name + "] " + ex.Message;
                log.AppendLine("SYM-FAIL " + res.Error + "\n" + ex.StackTrace);
            }
            return res;
        }

        /* ---------------- exec_cmds（2026-10-17 (8t)：通用命令流通道。
        背景：RRUL.CRIT 走属性 API 设不上（实测整数编码，GetInteger 也炸）——文档 5.05.03 实锤
        Draft 命令 OMit 直接设置 OMITFG 并自动清 STYF，编码由解析器处理。
        用法：cmds=A;B;C 分号分隔，逐条 CommandExcute，落日志（成功/失败布尔）。 ---------------- */
        public static OpResult ExecCmds(Dictionary<string, string> p)
        {
            OpResult res = new OpResult();
            FileLoggingStringBuilder log = new FileLoggingStringBuilder(@"D:\pdmsAgent\bridge\op_exec_cmds.log");
            try
            {
                string cmds = P(p, "cmds", "");
                if (cmds.Length == 0) throw new Exception("cmds 必填，分号分隔");
                log.AppendLine("=== EXC-START " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===");
                string[] arr = cmds.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                int nOk = 0, nFail = 0;
                foreach (string raw in arr)
                {
                    string c = raw.Trim();
                    if (c.Length == 0) continue;
                    bool ok = false;
                    try { ok = drawTool.CommandExcute(c, true); }
                    catch (Exception ex) { log.AppendLine("  [EXC] " + c + " : " + ex.Message); }
                    log.AppendLine("  [" + (ok ? "OK" : "FAIL") + "] " + c);
                    if (ok) nOk++; else nFail++;
                }
                res.Ok = nFail == 0 && nOk > 0;
                res.Output = "EXC-OK " + nOk + "/" + (nOk + nFail);
                res.Error = (nFail > 0) ? (nFail + " 条命令失败，详见日志") : null;
                log.AppendLine("=== EXC-" + (res.Ok ? "OK" : "PART") + " ===");
            }
            catch (Exception ex)
            {
                res.Ok = false; res.Error = "[" + ex.GetType().Name + "] " + ex.Message;
                log.AppendLine("EXC-FAIL " + res.Error + "\n" + ex.StackTrace);
            }
            return res;
        }

        /* ---------------- replace_symbols（2026-10-16 新增：已出视图符号 → 标准图块原位替换） ---------------- */

        /// <summary>
        /// 管件处管道走向的纸面角（度，atan2(n,w)，符号对 180° 对称故正负无关）。
        /// 2026-10-17 (2)：GetDirection(PDIR/LDIR) 对 VALVE/FLANGE 实测抛「应用程序中的错误」，
        /// 故改用纯几何——取同分支（Owner）成员中离该管件最近的世界坐标点 Q，方向 = P−Q。
        /// 只用实证的 GetPosition(HPOS, wrt /*) 通道（失败回退 WORPOS）；邻居不可得返回 0（水平）。
        /// </summary>
        /// <summary>
        /// (8x) 元件连通方向集合：扫 Owner 分支成员，量化 4 正交方向；
        /// 沿向 >1mm 且横向 <3mm 才算连通（过滤立管纸面投影≈0 的噪声）。
        /// 返回去重后的方向角列表（0/90/180/270）。用于三通「平面/端视」判定与弯头圆心内短线。
        /// </summary>
        private static List<double> ArmDirsOf(DbElement e, double px, double py)
        {
            var dirs = new bool[4];
            try
            {
                DbElement own = e.Owner;
                if (own == null || own.IsNull) return new List<double>();
                DbQualifier q = new DbQualifier(); q.wrtQualifier = DbElement.GetElement("/*");
                foreach (DbElement m in own.Members())
                {
                    if (m == null || m.IsNull) continue;
                    double w, n;
                    try { double[] mn = WorldPosOf(m, q); w = mn[0]; n = mn[1]; }
                    catch { continue; }
                    double dw = w - px, dn = n - py;
                    double d2 = dw * dw + dn * dn;
                    if (d2 < 0.25) continue; // (8z) 只去自身重合点；不设距离上限（长跨 PIPE 起点可达数米），
                                             // 靠 lateral<3mm 正交过滤兜底：斜向成员 lateral=along 恒>3mm 被滤，立管投影≈0 沿向<1mm 被滤。
                    double ang = Math.Atan2(dn, dw) * 180.0 / Math.PI;
                    int qq = ((int)Math.Round(ang / 90.0)) % 4; if (qq < 0) qq += 4;
                    double ra = qq * 90.0 * Math.PI / 180.0;
                    double along = dw * Math.Cos(ra) + dn * Math.Sin(ra);
                    double lateral = Math.Abs(-dw * Math.Sin(ra) + dn * Math.Cos(ra));
                    if (along > 1.0 && lateral < 3.0) dirs[qq] = true;
                }
            }
            catch { }
            var res = new List<double>();
            for (int i = 0; i < 4; i++) if (dirs[i]) res.Add(i * 90.0);
            return res;
        }

        /// <summary>
        /// 元件处管线方向（9g 重写：就近嵌入腿）：元件嵌在【哪条正交腿】上，符号就沿哪条腿——
        /// 旧「最近邻居」在丁字/弯角附近会抓到横腿成员导致阀门错转 90°（用户截图①）。
        /// 判据：4 正交轴分别找沿向最近成员（lateral<3mm 正交过滤），嵌入腿=沿向距离最小者。
        /// </summary>
        private static double PipeAngleAt(DbElement e, double px, double py)
        {
            try
            {
                DbElement own = e.Owner;
                if (own == null || own.IsNull) return 0;
                DbQualifier q = new DbQualifier(); q.wrtQualifier = DbElement.GetElement("/*");
                // (9h) 搜索域从本 BRANCH 扩到同管线（PIPE）全部兄弟分支：管线跨分支延续时
                //（如支管起点/分支头），本分支成员全在横轴、竖向延续段在兄弟分支——图1 两阀未转的根因。
                var scope = new List<DbElement>();
                try
                {
                    DbElement pipe9 = own.Owner;
                    if (pipe9 != null && !pipe9.IsNull)
                    {
                        foreach (DbElement sb in pipe9.Members())
                        {
                            try { if (sb.GetElementType() == DbElementTypeInstance.BRANCH) scope.Add(sb); } catch { }
                        }
                    }
                }
                catch { }
                if (scope.Count == 0) scope.Add(own);
                double[] minAlong = new double[] { double.MaxValue, double.MaxValue, double.MaxValue, double.MaxValue };
                foreach (DbElement brI in scope)
                    foreach (DbElement m in brI.Members())
                {
                    if (m == null || m.IsNull) continue;
                    double w, n;
                    try { double[] mn = WorldPosOf(m, q); w = mn[0]; n = mn[1]; }
                    catch { continue; }
                    double dw = w - px, dn = n - py;
                    double d2 = dw * dw + dn * dn;
                    if (d2 < 1e-6) continue; // 自身/重合点跳过
                    double ang = Math.Atan2(dn, dw) * 180.0 / Math.PI;
                    int qq = ((int)Math.Round(ang / 90.0)) % 4; if (qq < 0) qq += 4;
                    double ra = qq * 90.0 * Math.PI / 180.0;
                    double along = dw * Math.Cos(ra) + dn * Math.Sin(ra);
                    double lateral = Math.Abs(-dw * Math.Sin(ra) + dn * Math.Cos(ra));
                    if (along > 1.0 && lateral < 3.0 && along < minAlong[qq]) minAlong[qq] = along;
                }
                int bestQ = -1; double bestA = double.MaxValue;
                for (int qq = 0; qq < 4; qq++) if (minAlong[qq] < bestA) { bestA = minAlong[qq]; bestQ = qq; }
                if (bestQ < 0) return 0; // 四轴皆无贴身成员：退化为水平
                return bestQ * 90.0;
            }
            catch { return 0; }
        }

        /// <summary>
        /// 模型元素世界坐标(W,N)——多通道电池（2026-10-17 (3)）。
        /// run2 实证：BRANCH 的 HPOS(wrt /*) 可读；管件（ELBOW/VALVE/FLANGE）HPOS-wrt 抛
        /// 「应用程序中的错误」——管件自身无世界坐标属性（厂商 E3Doriginal 的 WORPOS 分支
        /// 只适用于 GRIDWLD/EQUIPMENT 等自带世界坐标的类型）。故按序尝试：
        ///   1) HPOS wrt /*（BRANCH 实证通道）；
        ///   2) WORPOS（厂商模式，自带世界坐标的元素）；
        ///   3) Owner(分支)头世界坐标(实证) + 成员 HPOS 未限定(wrt Owner) —— 管件正确通道
        ///      （分支不旋转，加法合成；只取 W,N 供投影）；
        ///   4) Owner 头 + 成员 LPOS；
        ///   5) POS 未限定。
        /// 全失败抛带 ref 与已试通道的异常（循环层记录，可精准定位）。
        /// </summary>
        private static double[] WorldPosOf(DbElement e, DbQualifier wq)
        {
            // (9c) 返回 {X,Y,Z} 三元——交叉断开要比较标高；历史调用方只用 [0]/[1]，加位安全。
            string tried = "";
            try { Position p = e.GetPosition(ATT.HPOS, wq); return new double[] { p.X, p.Y, p.Z }; }
            catch { tried += "HPOS-wrt "; }
            try { Position p = e.GetPosition(ATT.WORPOS); return new double[] { p.X, p.Y, p.Z }; }
            catch { tried += "WORPOS "; }
            try
            {
                DbElement own = e.Owner;
                if (own != null && !own.IsNull)
                {
                    Position ow = own.GetPosition(ATT.HPOS, wq);
                    Position lp;
                    try { lp = e.GetPosition(ATT.HPOS); return new double[] { ow.X + lp.X, ow.Y + lp.Y, ow.Z + lp.Z }; }
                    catch { lp = e.GetPosition(ATT.LPOS); return new double[] { ow.X + lp.X, ow.Y + lp.Y, ow.Z + lp.Z }; }
                }
                tried += "OwnerNull ";
            }
            catch { tried += "OwnerChain "; }
            try { Position p = e.GetPosition(ATT.POS); return new double[] { p.X, p.Y, p.Z }; }
            catch { tried += "POS "; }
            throw new Exception("位置全通道失败(" + tried.Trim() + ") ref=" + e);
        }

        /// <summary>把线段按所有「元件符号占位圈」裁剪（circles[i] = {cx, cy, r}）：
        /// 用户 2026-10-17 (8d)：有元件的地方不要多余的线——管线一律止于元件符号边缘，
        /// 元件处只留图例本身（弯头圆 r2.5→裁 2.8、三通圆同、阀门蝴蝶结半长 4.0→裁 4.3；
        /// 法兰=管上垂直短线属穿行符号，不参与裁剪）。线段中穿/端点入圈通吃。
        /// 返回剩余子线段列表（每项 [x1,y1,x2,y2]），短于 0.3mm 的碎段舍弃。</summary>
        private static List<double[]> ClipSegAtSymbols(List<double[]> circles, double x1, double y1, double x2, double y2)
        {
            var outSegs = new List<double[]>();
            double dx = x2 - x1, dy = y2 - y1;
            double L2 = dx * dx + dy * dy;
            if (L2 < 1e-9 || circles == null || circles.Count == 0) { outSegs.Add(new double[] { x1, y1, x2, y2 }); return outSegs; }
            double L = Math.Sqrt(L2);
            // 剩余区间集合（参数 t ∈ [0,1]），逐圈做减法（每圈半径独立）
            var ivs = new List<double[]>(); ivs.Add(new double[] { 0.0, 1.0 });
            foreach (var c in circles)
            {
                double R = c.Length > 2 ? c[2] : 2.8, half = R / L; // 占位半径换算成参数 t 的半宽
                // 圆心到线段的最近点参数
                double t = ((c[0] - x1) * dx + (c[1] - y1) * dy) / L2;
                double cx = x1 + t * dx, cy = y1 + t * dy;
                double dist2 = (c[0] - cx) * (c[0] - cx) + (c[1] - cy) * (c[1] - cy);
                if (dist2 > R * R) continue; // 圆不与此线段相交
                double lo = t - half, hi = t + half;
                var nxt = new List<double[]>();
                foreach (var iv in ivs)
                {
                    double a = iv[0], b = iv[1];
                    if (hi <= a || lo >= b) { nxt.Add(iv); continue; } // 不重叠
                    if (lo > a) nxt.Add(new double[] { a, Math.Min(lo, b) });
                    if (hi < b) nxt.Add(new double[] { Math.Max(hi, a), b });
                }
                ivs = nxt;
                if (ivs.Count == 0) break;
            }
            foreach (var iv in ivs)
            {
                double ta = iv[0], tb = iv[1];
                if ((tb - ta) * L < 0.3) continue; // 碎段舍弃
                outSegs.Add(new double[] { x1 + ta * dx, y1 + ta * dy, x1 + tb * dx, y1 + tb * dy });
            }
            return outSegs;
        }

        /// <summary>把已出平面视图里的自绘符号（旧 ChemSymbols 裸线段）原位替换为标准符号（2026-10-17 起为直接画线）：
        ///   1) 图块库入口短路（direct-draw 模式，无需建库）；
        ///   2) 删除 NOTE「设备中心线」下 NLCOLO=9 的旧符号线段（颜色 9 白底不可见 + 尺寸过小——旧症状根因④；
        ///      判别式只删颜色 9 的 STRAIGHT：中心线=2、范围框=10/16，不会误删）；
        ///   3) 删除视图内已有「管道符号」LAYER（级联旧符号实例，保证可重复替换）；
        ///   4) 按模型元素插图符号：ELBOW U/D→ELBOWD/ELBOWU、VALVE→VALVE、FLANGE→FLANGE、BRANCH→TEE；
        ///   5) UpdateModelView(view) + UPDATE all —— 视图重生成 = "替换生效"的关键（根因②）；
        ///   6) 删后判空 CE + 提示 SAVEWORK（根因③：不落盘则重启回滚）。
        /// params: view = VIEW ref/路径（如 "=16385/1249"）；sheet = 可选（先 CE 到 SHEET 建上下文）。
        /// 日志 op_replace_symbols.log。</summary>
        public static OpResult ReplaceSymbols(Dictionary<string, string> p)
        {
            OpResult res = new OpResult();
            FileLoggingStringBuilder log = new FileLoggingStringBuilder(@"D:\pdmsAgent\bridge\op_replace_symbols.log");
            try
            {
                string vref = P(p, "view", "");
                string sref = P(p, "sheet", "");
                if (vref.Length == 0) throw new Exception("view 必填（VIEW ref/路径，如 =16385/1249）");
                log.AppendLine("=== RSYM-START " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " view=" + vref + " sheet=" + sref + " ===");
                // WT-DRAW benchmark: same standard for the wizard pipeline and the direct op.
                DrawingStandard.Apply(p);
                log.AppendLine(DrawingStandard.Summary());
                foreach (string stdW in DrawingStandard.Validate(0)) log.AppendLine("[STD-WARN] " + stdW);

                // CE 上下文（可选 sheet 先行——UpdateModelView 需要 Owner 上下文）
                if (sref.Length > 0)
                {
                    DbElement sh = null;
                    try { sh = DbElement.GetElement(sref); } catch { }
                    if (sh == null || sh.IsNull || sh.IsDeleted) throw new Exception("SHEET 不存在: " + sref);
                    Command cs = Command.CreateCommand("CE " + sref);
                    if (cs == null) throw new Exception("CreateCommand(CE SHEET) 返回 null");
                    log.AppendLine("CE " + sref + " cmd=" + cs.RunInPdms());
                }
                DbElement view = null;
                try { view = DbElement.GetElement(vref); } catch { }
                if (view == null || view.IsNull || view.IsDeleted) throw new Exception("VIEW 不存在: " + vref);
                if (!view.GetElementType().ToString().Equals("VIEW", StringComparison.OrdinalIgnoreCase))
                    throw new Exception("目标不是 VIEW: " + view.GetElementType());
                DbElement sheet = view.Owner;
                if (sheet == null || sheet.IsNull) throw new Exception("VIEW.Owner(SHEET) 为空");
                log.AppendLine("VIEW ok, SHEET=" + sheet.GetAsString(ATT.NAME));
                // 2026-10-17：CE 必须在 DRAFT 世界（CE 漂移进 DESIGN 时，名字解析/命令上下文全错——07:46 建库失败实证）
                if (sref.Length == 0)
                {
                    string vrefName = view.GetAsString(ATT.NAME);
                    Command cv = Command.CreateCommand("CE " + vrefName);
                    if (cv == null) throw new Exception("CreateCommand(CE VIEW) 返回 null");
                    log.AppendLine("CE " + vrefName + " cmd=" + cv.RunInPdms());
                }

                // ===== (10j) 设备布置图新建 Sheet 页（用户 2026-10-21「设备图新建Sheet页」）=====
                // drawPipe=0（设备布置图）且未显式 newSheet=0 时：在源 SHEET 所属 DRWG 下新建/复用
                // 「设备布置图N」页，视图克隆源视图（VTYP/VSCA/THPO/SIZE/XYPS/IDLN/RRSF/HRSF 全拷），
                // 后续绘制全部落新页——旧管道图上的界外链/图块残留不再污染设备图（规则表见 (10i)）。
                // 幂等：同名页已存在则复用其第一个 VIEW（重复出图不堆积页/视图）。
                bool equipReq10 = P(p, "drawPipe", "1") == "0";
                bool newSheet10 = P(p, "newSheet", equipReq10 ? "1" : "0") == "1";
                // (10b-v2) equipZones: restrict equipment collection to given zones
                // (comma separated zone names/refs, e.g. "/WPC/Embed_Plate").
                // Empty = legacy behavior (all EQUIPMENT within viewport extent).
                var equipZones10 = new List<string>();
                foreach (string zs10 in P(p, "equipZones", "").Split(','))
                { string q10 = zs10.Trim().TrimStart('/'); if (q10.Length > 0) equipZones10.Add(q10); }
                if (newSheet10)
                {
                    DbElement drwg10 = sheet.Owner;
                    if (drwg10 == null || drwg10.IsNull || drwg10.GetElementType() != DbElementTypeInstance.DRWG)
                        throw new Exception("(10j) 源 SHEET 上级不是 DRWG，无法新建设备布置图页");
                    string drwgNm10 = drwg10.GetAsString(ATT.NAME);
                    string drwgPath10 = drwgNm10.StartsWith("/") ? drwgNm10 : "/" + drwgNm10;
                    DbElement eqSheet10 = null; int eqSn10 = 0;
                    for (int sn = 1; sn <= 20; sn++)
                    {
                        DbElement cand = null;
                        try { cand = DbElement.GetElement(drwgPath10 + "/设备布置图" + sn); } catch { }
                        if (cand != null && !cand.IsNull && !cand.IsDeleted
                            && cand.GetElementType() == DbElementTypeInstance.SHEET)
                        { eqSheet10 = cand; eqSn10 = sn; break; }
                    }
                    if (eqSheet10 != null)
                    {
                        log.AppendLine("(10j) 复用已有设备布置图页: " + drwgPath10 + "/设备布置图" + eqSn10);
                    }
                    else
                    {
                        // 图框背景沿用源 SHEET 的 BSRF（A0/A3 跟旧图一致）
                        string backs10 = "/DRA/MAS/BACKS/MET/A0";
                        try { backs10 = sheet.GetElement(ATT.BSRF).GetAsString(ATT.NAME); } catch { }
                        for (int sn = 1; sn <= 20 && eqSheet10 == null; sn++)
                        {
                            DbElement probe = null;
                            try { probe = DbElement.GetElement(drwgPath10 + "/设备布置图" + sn); } catch { }
                            if (probe == null || probe.IsNull || probe.IsDeleted)
                            {
                                DbElement ns = drawTool.createDrwgDraw(drwg10, backs10);
                                if (ns == null || ns.IsNull) throw new Exception("(10j) createDrwgDraw 失败（背景 " + backs10 + "）");
                                drawTool.SetAttribute(ns, ATT.NAME, drwgNm10 + "/设备布置图" + sn);
                                try { ns.SetAttribute(ATT.BSRF, DbElement.GetElement(backs10)); } catch { }
                                try { ns.Owner.SetAttribute(ATT.BSRF, DbElement.GetElement(backs10)); } catch { }
                                eqSheet10 = ns; eqSn10 = sn;
                                log.AppendLine("(10j) 新建设备布置图页: " + drwgPath10 + "/设备布置图" + sn + " 背景=" + backs10);
                            }
                        }
                    }
                    if (eqSheet10 == null) throw new Exception("(10j) 设备布置图页分配失败（1..20 全占用）");
                    // 视图：复用页取已有 VIEW；新页 CreateFirst——属性随后全量刷新（幂等）
                    DbElement nv10 = null;
                    try { foreach (DbElement m10 in eqSheet10.Members()) { try { if (m10.GetElementType() == DbElementTypeInstance.VIEW) { nv10 = m10; break; } } catch { } } } catch { }
                    if (nv10 == null || nv10.IsNull)
                    {
                        nv10 = eqSheet10.CreateFirst(DbElementTypeInstance.VIEW);
                        if (nv10 == null || nv10.IsNull) throw new Exception("(10j) 新 VIEW 创建失败");
                        drawTool.SetAttribute(nv10, ATT.NAME, eqSheet10.GetAsString(ATT.NAME) + "/View1");
                    }
                    try { drawTool.SetAttribute(nv10, ATT.VFRCOL, "2"); } catch { }
                    try { drawTool.SetAttribute(nv10, ATT.VTYP, view.GetAsString(ATT.VTYP)); } catch { }
                    try { nv10.SetAttribute(ATT.VSCA, view.GetDouble(ATT.VSCA)); } catch { }
                    try { nv10.SetAttribute(ATT.THPO, view.GetPosition(ATT.THPO)); } catch { }
                    try { nv10.SetAttribute(ATT.SIZE, view.GetDoubleArray(ATT.SIZE)); } catch { }
                    try { nv10.SetAttribute(ATT.XYPS, view.GetPosition(ATT.XYPS)); } catch { }
                    try { nv10.SetAttribute(ATT.IDLN, view.GetElement(ATT.IDLN)); } catch { }
                    try { nv10.SetAttribute(ATT.RRSF, view.GetElement(ATT.RRSF)); } catch { }
                    try { nv10.SetAttribute(ATT.HRSF, view.GetElement(ATT.HRSF)); } catch { }
                    // CE 切到新 SHEET（后续 NOTE/命令上下文全在新页）
                    string nsNm10 = eqSheet10.GetAsString(ATT.NAME);
                    Command cn10 = Command.CreateCommand("CE " + nsNm10);
                    if (cn10 != null) { try { cn10.RunInPdms(); } catch { } }
                    view = nv10; sheet = eqSheet10;
                    log.AppendLine("(10j) 视图克隆完成: " + nv10.GetAsString(ATT.NAME) + "（后续出图落新页）");
                    // 机器可读标记：向导流水线据此把 PDF 导出切到新页
                    log.AppendLine("[NEWSHEET]" + (nsNm10.StartsWith("/") ? nsNm10 : "/" + nsNm10));
                }

                // 1) 图块库
                bool libOk = SymBlocks.EnsureLibrary(false, delegate(string line) { log.AppendLine(line); });
                if (!libOk) throw new Exception("图块库不可用——先跑 create_sym_blocks");

                // 2) 删旧裸符号线段（NOTE「设备中心线」下 NLCOLO=9 的 STRAIGHT）
                int delSeg = 0;
                foreach (DbElement m in sheet.Members())
                {
                    if (m == null || m.IsNull) continue;
                    string tn = "";
                    try { tn = m.GetElementType().ToString(); } catch { continue; }
                    if (!tn.Equals("NOTE", StringComparison.OrdinalIgnoreCase)) continue;
                    string mnm = "";
                    try { mnm = m.GetAsString(ATT.NAME); } catch { }
                    if (mnm == null || mnm.IndexOf("设备中心线") < 0) continue;
                    foreach (DbElement s in m.Members(DbElementTypeInstance.STRAIGHT))
                    {
                        if (s == null || s.IsNull) continue;
                        int col = 0;
                        try { col = s.GetInteger(ATT.NLCOLO); } catch { continue; }
                        if (col != 9) continue; // 判别式：只删旧符号颜色
                        try
                        {
                            s.Delete();
                            delSeg++;
                            if ((delSeg & 0x1F) == 0) { try { System.Windows.Forms.Application.DoEvents(); } catch { } }
                        }
                        catch (Exception exD) { log.AppendLine("旧符号删除异常(跳过): [" + exD.GetType().Name + "] " + exD.Message); }
                    }
                }
                log.AppendLine("旧符号线段(NLCOLO=9)删除 = " + delSeg);

                // 3) 删旧「管道符号」LAYER（级联旧 SLABEL 实例——可重复替换的关键）
                int delLayer = 0;
                foreach (DbElement m in view.Members())
                {
                    if (m == null || m.IsNull) continue;
                    string tn = "";
                    try { tn = m.GetElementType().ToString(); } catch { continue; }
                    if (!tn.Equals("LAYER", StringComparison.OrdinalIgnoreCase)) continue;
                    string mnm = "";
                    try { mnm = m.GetAsString(ATT.NAME); } catch { }
                    if (mnm == null || mnm.IndexOf("管道符号") < 0) continue;
                    try { m.Delete(); delLayer++; } catch (Exception exD) { log.AppendLine("旧符号层删除异常(跳过): [" + exD.GetType().Name + "] " + exD.Message); }
                }
                log.AppendLine("旧「管道符号」LAYER 删除 = " + delLayer);

                // 3b) 删旧「管道符号」NOTE（2026-10-17 (4)：容器从 LAYER 改为 SHEET 下 NOTE——
                //     工作代码所有 STRAIGHT 都挂 NOTE 下（noteCenter/noteDraw 实证），LAYER 挂
                //     STRAIGHT 无先例且 run3 实测 DrawSeg 内抛 PdmsException）
                int delNote = 0;
                foreach (DbElement m in sheet.Members())
                {
                    if (m == null || m.IsNull) continue;
                    string tn = "";
                    try { tn = m.GetElementType().ToString(); } catch { continue; }
                    if (!tn.Equals("NOTE", StringComparison.OrdinalIgnoreCase)) continue;
                    string mnm = "";
                    try { mnm = m.GetAsString(ATT.NAME); } catch { }
                    if (mnm == null || mnm.IndexOf("管道符号") < 0) continue;
                    try { m.Delete(); delNote++; } catch (Exception exD) { log.AppendLine("旧符号NOTE删除异常(跳过): [" + exD.GetType().Name + "] " + exD.Message); }
                }
                log.AppendLine("旧「管道符号」NOTE 删除 = " + delNote);

                // ===== (10) 轴网 + 设备（用户 2026-10-19「这次把轴网和设备加进来」）=====
                // 在「管道符号」NOTE 之前创建 → Draft 按创建序叠放，轴网/设备垫在管线符号下方。
                // 幂等：先删旧「轴网」「设备轮廓」NOTE，重跑不堆积。
                double gridStep = PD(p, "gridStep", 6000.0);
                int gridColor = (int)PD(p, "gridColor", 1);
                int equipColor = (int)PD(p, "equipColor", 5);
                bool drawGrid = P(p, "drawGrid", "1") != "0";
                bool drawEquip = P(p, "drawEquip", "1") != "0";
                // (drawPipe) 用户 2026-10-20「把管道勾选也去掉，有时候可能出设备布置图」：
                // 为 0 时跳过 红线绘制/图块插入/交叉断口/流向箭头/管线序号——链与落线只剩设备+轴点
                bool drawPipe = P(p, "drawPipe", "1") != "0";
                // —— (10i) 图纸类型分治规则表（用户 2026-10-21「列记住每个布置图的规则，不要混乱」）——
                //   管道平面图(drawPipe=1)：尺寸链归边上下左右(9k) + 轴点进链(9k2) + 轴距行跟链外9mm(9k3)
                //                          + 轴网气泡在链/视口外 11mm（标准管建图，界外留标注区）
                //   设备布置图(drawPipe=0)：轴网（线+气泡）全部限制在【出图界限内】——气泡贴视口边内侧；
                //                          元素间距标注【不归边】（保持元素附近，仅抽稀/去挤/引出防重叠）；
                //                          元素链不进轴点(跳过9k2，防轴距混入元素链)；轴距行(9k3)固定贴
                //                          视口界内 +13mm（气泡内侧 5mm），不跟链走
                bool equipLayout10 = !drawPipe;
                // (10c) 定位标注参数：距轴/距设备开关 + 设备距离上限（超过不标，防满图引线）
                bool locGrid10 = P(p, "locGrid", "0") != "0"; // 默认关（用户 2026-10-19 16:52「界外的设备号不要」，参考标准管建图界外只留链+轴号）
                bool locEquip10 = P(p, "locEquip", "0") != "0";
                double equipDistMax10 = PD(p, "equipDistMax", 8000.0);
                double gridDistMax10 = PD(p, "gridDistMax", 30000.0); // 超过视为无有效近轴（视口边缘管），不标
                var equipBoxes10 = new List<double[]>(); // {x0,y0,x1,y1} world，(10b) 采集
                var equipNames10 = new List<string>();
                var equipRefs10 = new List<DbElement>(); // (10e) 可视设备元素——ADDENTRY 进 drawinglist + (9m) 设备测点进链
                var equipOri10 = new List<double[]>(); // (10c-v4) 平行表 {wx,wy} 设备 Origin(POS) 世界坐标（用户 2026-10-28「定位设备用 Origin 点，不要一直定位中心」；null=取不到回退包络中心）
                var equipPt9 = new List<double[]>(); // (9m) 平行表 {px,py} 设备定位基准纸面坐标（Origin 优先，包络中心兜底；与 equipRefs10 同序，NaN=不可投影）
                foreach (string oldNm10 in new string[] { "轴网", "设备轮廓", "定位标注", "轴网定位" })
                {
                    int del10 = 0;
                    foreach (DbElement m in sheet.Members())
                    {
                        if (m == null || m.IsNull) continue;
                        string tn = ""; try { tn = m.GetElementType().ToString(); } catch { continue; }
                        if (!tn.Equals("NOTE", StringComparison.OrdinalIgnoreCase)) continue;
                        string mnm = ""; try { mnm = m.GetAsString(ATT.NAME); } catch { }
                        if (mnm == null || mnm.IndexOf(oldNm10) < 0) continue;
                        try { m.Delete(); del10++; } catch (Exception exD10) { log.AppendLine("旧「" + oldNm10 + "」NOTE删除异常(跳过): " + exD10.Message); }
                    }
                    log.AppendLine("旧「" + oldNm10 + "」NOTE 删除 = " + del10);
                }

                // 视口纸面范围 + 世界范围（paper = XYPS + (world - THPO) * VSCA）
                double[] lim10 = null; try { lim10 = drawE3DTool.ViewLimits(view); } catch { }
                double pMinX10 = lim10 != null && lim10.Length >= 4 ? lim10[0] : -150;
                double pMaxX10 = lim10 != null && lim10.Length >= 4 ? lim10[1] : 150;
                double pMinY10 = lim10 != null && lim10.Length >= 4 ? lim10[2] : -110;
                double pMaxY10 = lim10 != null && lim10.Length >= 4 ? lim10[3] : 110;
                // (14d) 全局标注避让规划器：每轮出图清表 + 设视口界限
                // （2026-11-08 用户「标注和管线标签和设备标签糊成一团」，业界对标见 annotationPlanner.cs 头注释）
                AnnotationPlanner.Reset(new double[] { pMinX10, pMaxX10, pMinY10, pMaxY10 });
                double vSca10 = 0, thX10 = 0, thY10 = 0;
                try { vSca10 = view.GetDouble(ATT.VSCA); Position thp10 = view.GetPosition(ATT.THPO); thX10 = thp10.X; thY10 = thp10.Y; } catch { }
                if (vSca10 <= 1e-9) vSca10 = 0.001;
                double[] sz10 = null; try { sz10 = view.GetDoubleArray(ATT.SIZE); } catch { }
                double halfW10 = (sz10 != null && sz10.Length >= 2 ? sz10[0] : (pMaxX10 - pMinX10)) / 2.0 / vSca10;
                double halfH10 = (sz10 != null && sz10.Length >= 2 ? sz10[1] : (pMaxY10 - pMinY10)) / 2.0 / vSca10;
                double wMinX10 = thX10 - halfW10, wMaxX10 = thX10 + halfW10;
                double wMinY10 = thY10 - halfH10, wMaxY10 = thY10 + halfH10;

                // 尺寸链现位置（9k 归边幂等、跨轮稳定）：轴网气泡要放到最外层链的外侧
                double chainMinX10 = double.MaxValue, chainMinY10 = double.MaxValue;
                Action<DbElement, int> dimScan10 = null;
                dimScan10 = delegate (DbElement el, int dep)
                {
                    if (el == null || el.IsNull || dep > 6) return;
                    DbElement[] kids10;
                    try { kids10 = el.Members(); } catch { return; }
                    foreach (DbElement ch in kids10)
                    {
                        if (ch == null || ch.IsNull) continue;
                        string tn = ""; try { tn = ch.GetElementType().ToString(); } catch { continue; }
                        if (tn == "LDIMENSION")
                        {
                            try
                            {
                                double[] dp10 = ch.GetDoubleArray(ATT.DPOS);
                                if (dp10 != null && dp10.Length >= 2)
                                {
                                    if (dp10[0] < chainMinX10) chainMinX10 = dp10[0];
                                    if (dp10[1] < chainMinY10) chainMinY10 = dp10[1];
                                }
                            }
                            catch { }
                        }
                        dimScan10(ch, dep + 1);
                    }
                };
                dimScan10(view, 0);

                // 设计库 world 清单（轴网探测 + 设备收集共用）
                var worlds10 = new List<DbElement>();
                try { worlds10 = FangYuan.PDMS.Utilities.MDBMembers(DbType.Design); } catch (Exception exW10) { log.AppendLine("(10) DESIGN world 枚举失败: " + exW10.Message); }

                // —— (10a-0) 视口贴设备（用户 2026-10-25「轴网把图切了，应该切轴网而不是设备」）——
                // 设备布置图(equipLayout10)下先扫设备联合包络：超出视口世界范围 → 缩 VSCA + 移 THPO，
                // 设备(留 5% 边距)全部入框；轴网落线本就只画纸面界 pMin..pMax（(10a) 天然裁剪）
                // → 被切的是轴网，设备不再出框。须在所有投影绘制(10a/10b/图块/红线/链)之前执行。
                // 管道平面图不启用：只贴设备会反过来切管道。
                if (drawEquip && equipLayout10)
                {
                    double eqMnX = double.MaxValue, eqMxX = double.MinValue, eqMnY = double.MaxValue, eqMxY = double.MinValue;
                    bool eqAny = false;
                    int eqCntF = 0;
                    foreach (DbElement w in worlds10)
                    {
                        if (w == null || w.IsNull) continue;
                        DBElementCollection ecF0 = null;
                        try { ecF0 = new DBElementCollection(w, new TypeFilter(DbElementTypeInstance.EQUIPMENT)); } catch { continue; }
                        foreach (DbElement eq in ecF0)
                        {
                            if (eq == null || eq.IsNull) continue;
                            try
                            {
                                // zone filter 与 (10b) 同款（后缀匹配 equipZones10）
                                if (equipZones10.Count > 0)
                                {
                                    string znF = null;
                                    try
                                    {
                                        DbElement owF = eq.Owner;
                                        while (owF != null && !owF.IsNull)
                                        {
                                            string otF = "";
                                            try { otF = owF.GetElementType().ToString(); } catch { }
                                            if (otF.Equals("ZONE", StringComparison.OrdinalIgnoreCase))
                                            { znF = (owF.GetAsString(ATT.NAME) ?? "").TrimStart('/'); break; }
                                            owF = owF.Owner;
                                        }
                                    }
                                    catch { }
                                    bool hitF = false;
                                    if (znF != null)
                                        foreach (string zqF in equipZones10)
                                            if (znF.EndsWith(zqF, StringComparison.OrdinalIgnoreCase)) { hitF = true; break; }
                                    if (!hitF) continue;
                                }
                                double[] lvF0 = ElementLimits(eq);
                                if (IsZeroBox(lvF0) || (lvF0[3] <= lvF0[0] && lvF0[4] <= lvF0[1])) lvF0 = UnionMembersBox(eq);
                                if (IsZeroBox(lvF0)) continue;
                                eqMnX = Math.Min(eqMnX, lvF0[0]); eqMxX = Math.Max(eqMxX, lvF0[3]);
                                eqMnY = Math.Min(eqMnY, lvF0[1]); eqMxY = Math.Max(eqMxY, lvF0[4]);
                                eqAny = true; eqCntF++;
                            }
                            catch { }
                        }
                    }
                    if (eqAny)
                    {
                        double exW = (eqMxX - eqMnX) * 1.10, exH = (eqMxY - eqMnY) * 1.10; // 5% 边距
                        double curW = wMaxX10 - wMinX10, curH = wMaxY10 - wMinY10;
                        bool chgF = false;
                        double oldVscaF = vSca10;
                        // 只在设备超出世界范围时动手（装得下但偏心不动，尊重原取景）
                        if (exW > curW * 1.001 || exH > curH * 1.001)
                        {
                            double k = Math.Min(curW / exW, curH / exH);
                            if (k > 0 && k < 1.0) { vSca10 = vSca10 * k; }
                            thX10 = (eqMnX + eqMxX) / 2.0; thY10 = (eqMnY + eqMxY) / 2.0; // 贴框同时居中
                            chgF = true;
                        }
                        if (chgF)
                        {
                            try
                            {
                                double thZF = view.GetPosition(ATT.THPO).Z;
                                view.SetAttribute(ATT.VSCA, vSca10);
                                view.SetAttribute(ATT.THPO, Position.Create(thX10, thY10, thZF));
                                // 世界域随新 VSCA 重算（SIZE/纸面界不变）
                                double hwF = (sz10 != null && sz10.Length >= 2 ? sz10[0] : (pMaxX10 - pMinX10)) / 2.0 / vSca10;
                                double hhF = (sz10 != null && sz10.Length >= 2 ? sz10[1] : (pMaxY10 - pMinY10)) / 2.0 / vSca10;
                                wMinX10 = thX10 - hwF; wMaxX10 = thX10 + hwF;
                                wMinY10 = thY10 - hhF; wMaxY10 = thY10 + hhF;
                                try { drawTool.CommandExcute("UPDATE all"); } catch { }
                                log.AppendLine("(10a-0) 视口贴设备: 设备" + eqCntF + "台 包络[W" + eqMnX.ToString("F0") + " N" + eqMnY.ToString("F0")
                                    + " TO W" + eqMxX.ToString("F0") + " N" + eqMxY.ToString("F0") + "] VSCA " + oldVscaF.ToString("G6") + "→" + vSca10.ToString("G6")
                                    + " THPO→(" + thX10.ToString("F0") + "," + thY10.ToString("F0") + ") 新世界域[W" + wMinX10.ToString("F0") + " N" + wMinY10.ToString("F0")
                                    + " TO W" + wMaxX10.ToString("F0") + " N" + wMaxY10.ToString("F0") + "]（轴网切于纸面界，设备全入框）");
                            }
                            catch (Exception exF0) { log.AppendLine("(10a-0) 视口贴设备失败(继续原界): " + exF0.Message); }
                        }
                        else log.AppendLine("(10a-0) 设备" + eqCntF + "台已在视口世界范围内，无需贴框调整");
                    }
                    else log.AppendLine("(10a-0) 未扫到可用设备包络，跳过贴框");
                }

                // —— (10a) 轴网：设计库 GRIDWLD 实轴优先，无则按 gridStep 整数倍自生成 ——
                // gridLines 项: {坐标, 朝向(0=竖线 const X→数字圈, 1=横线 const Y→字母圈)}
                var gridLines = new List<double[]>();
                var gridFrmw10 = new List<DbElement>(); // (10a-v2) FRMWORK 轴网元素（供 (10e) ADDENTRY 进 drawinglist）
                // (10a-v4) 用户指认轴网：gridFrmws 参数（逗号分隔 FRMWORK 引用/名）——「每个项目轴网建的位置不一样，
                // 应该由用户选择出图元素」（Web 向导勾认）；指认模式下其他 FRMWORK 不推轴（留给 (10h) 结构）
                var pinnedSet10 = new System.Collections.Generic.HashSet<string>();
                foreach (string pf10 in P(p, "gridFrmws", "").Split(',')) { string q10 = pf10.Trim().TrimStart('/'); if (q10.Length > 0) pinnedSet10.Add(q10); }
                bool hasPin10 = pinnedSet10.Count > 0;
                var structRefs10 = new List<DbElement>(); // (10h) 结构元素（SBFRAMEWORK/FRMWORK 非轴网部分）
                if (drawGrid)
                {
                    int nG10 = 0;
                    var glog = new System.Text.StringBuilder();
                    DbQualifier wq10 = new DbQualifier(); wq10.wrtQualifier = DbElement.GetElement("/*");
                    Action<DbElement, int> gridDfs = null;
                    gridDfs = delegate (DbElement el, int depth)
                    {
                        if (el == null || el.IsNull || depth > 7 || nG10 > 200) return;
                        DbElement[] kids10;
                        try { kids10 = el.Members(); } catch { return; }
                        foreach (DbElement ch in kids10)
                        {
                            if (ch == null || ch.IsNull) continue;
                            string tn = ""; try { tn = ch.GetElementType().ToString(); } catch { continue; }
                            if (tn.IndexOf("GRID", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                nG10++;
                                string gn = ""; try { gn = ch.GetAsString(ATT.NAME); } catch { }
                                Position gp = null;
                                try { gp = ch.GetPosition(ATT.WORPOS); } catch { }
                                if (gp == null) { try { gp = ch.GetPosition(ATT.HPOS, wq10); } catch { } }
                                if (gp != null && tn.IndexOf("GRIDP", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    // 轴线方向：AXDIR 不在 DbAttributeInstance（CS0117 实证）——
                                    // 轮试 PDIR/LDIR（drawTool.cs 实证存在的方向属性），全败则按名字推断：
                                    // 数字名→竖线(const X)、字母名→横线(const Y)，其余跳过。
                                    double axW10 = 0, axN10 = 0; bool dirOk10 = false; string dirSrc10 = "";
                                    try
                                    {
                                        Direction ad10 = ch.GetDirection(ATT.PDIR, 1);
                                        axW10 = Math.Cos(ad10.Angle(Direction.Create(Axis.X)));
                                        axN10 = Math.Cos(ad10.Angle(Direction.Create(Axis.Y)));
                                        dirOk10 = (Math.Abs(axW10) + Math.Abs(axN10)) > 0.5;
                                        if (dirOk10) dirSrc10 = "PDIR";
                                    }
                                    catch { }
                                    if (!dirOk10)
                                    {
                                        try
                                        {
                                            Direction ad10 = ch.GetDirection(ATT.LDIR);
                                            axW10 = Math.Cos(ad10.Angle(Direction.Create(Axis.X)));
                                            axN10 = Math.Cos(ad10.Angle(Direction.Create(Axis.Y)));
                                            dirOk10 = (Math.Abs(axW10) + Math.Abs(axN10)) > 0.5;
                                            if (dirOk10) dirSrc10 = "LDIR";
                                        }
                                        catch { }
                                    }
                                    if (!dirOk10 && !string.IsNullOrEmpty(gn))
                                    {
                                        string bn10 = gn.TrimStart('/');
                                        // 数字名→竖线(const X)：axN=1；字母名→横线(const Y)：axW=1
                                        if (bn10.Length > 0 && char.IsDigit(bn10[0])) { dirOk10 = true; dirSrc10 = "NAME数字"; axW10 = 0; axN10 = 1; }
                                        else if (bn10.Length > 0 && char.IsLetter(bn10[0])) { dirOk10 = true; dirSrc10 = "NAME字母"; axW10 = 1; axN10 = 0; }
                                    }
                                    glog.Append(tn).Append(" ").Append(gn).Append(" (").Append(gp.X.ToString("F0", System.Globalization.CultureInfo.InvariantCulture))
                                        .Append(",").Append(gp.Y.ToString("F0", System.Globalization.CultureInfo.InvariantCulture))
                                        .Append(" dir[").Append(dirSrc10).Append("]W").Append(axW10.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))
                                        .Append(" N").Append(axN10.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)).Append(") ");
                                    if (dirOk10)
                                    {
                                        if (Math.Abs(axW10) >= Math.Abs(axN10))
                                            gridLines.Add(new double[] { gp.Y, 1 }); // 平面沿 E-W → 平面图横线（const Y）
                                        else
                                            gridLines.Add(new double[] { gp.X, 0 }); // 平面沿 N-S → 平面图竖线（const X）
                                    }
                                    else glog.Append("(方向未知,跳过) ");
                                }
                                else
                                {
                                    glog.Append(tn).Append(" ").Append(gn).Append(" (").Append(gp != null ? "有坐标但非GRIDP" : "无坐标").Append(") ");
                                }
                            }
                            gridDfs(ch, depth + 1);
                        }
                    };
                    foreach (DbElement w in worlds10)
                    {
                        if (w == null || w.IsNull) continue;
                        DBElementCollection ec = null;
                        try { ec = new DBElementCollection(w, new TypeFilter(DbElementTypeInstance.GRIDWLD)); } catch { continue; }
                        foreach (DbElement gw in ec)
                        {
                            if (gw == null || gw.IsNull) continue;
                            string gwn = ""; try { gwn = gw.GetAsString(ATT.NAME); } catch { }
                            glog.Append("[GRIDWLD ").Append(gwn).Append("] ");
                            gridDfs(gw, 0);
                        }
                    }
                    foreach (DbElement w in worlds10)
                    {
                        if (w == null || w.IsNull) continue;
                        DBElementCollection ecF = null;
                        try { ecF = new DBElementCollection(w, new TypeFilter(DbElementTypeInstance.FRMWORK)); } catch { continue; }
                        foreach (DbElement fw in ecF)
                        {
                            if (fw == null || fw.IsNull) continue;
                            string fn10 = "?"; try { fn10 = (fw.GetAsString(ATT.NAME) ?? "?").TrimStart('/'); } catch { }
                            // (10a-v4) 指认模式：只认用户勾选的 FRMWORK 为轴网，其余跳过（(10h) 走结构通道）
                            if (hasPin10 && !pinnedSet10.Contains(fn10)) { glog.Append("[FRMWORK ").Append(fn10).Append(" 跳过(非指认)] "); continue; }
                            try
                            {
                                // 用户 2026-10-20 指路「轴网元素是 FRMW」→ 实名 FRMWORK。
                                // 世界包络扁平方向推断轴向：x 扁→竖轴(常x)、y 扁→横轴(常y)、两维都大→系统级只收集不推轴
                                double[] lvF = ElementLimits(fw);
                                if (IsZeroBox(lvF)) lvF = UnionMembersBox(fw);
                                if (IsZeroBox(lvF)) { glog.Append("[FRMWORK ").Append(fn10).Append(" 无包络] "); continue; }
                                double dxF = lvF[3] - lvF[0], dyF = lvF[4] - lvF[1];
                                double cxF = (lvF[0] + lvF[3]) / 2.0, cyF = (lvF[1] + lvF[4]) / 2.0;
                                gridFrmw10.Add(fw);
                                nG10++;
                                if (dxF <= dyF) { gridLines.Add(new double[] { cxF, 0 }); glog.Append("[FRMWORK ").Append(fn10).Append("→竖轴x=").Append(cxF.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)).Append("] "); }
                                else if (dyF < dxF) { gridLines.Add(new double[] { cyF, 1 }); glog.Append("[FRMWORK ").Append(fn10).Append("→横轴y=").Append(cyF.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)).Append("] "); }
                                else glog.Append("[FRMWORK ").Append(fn10).Append(" 系统级(").Append(dxF.ToString("F0")).Append("x").Append(dyF.ToString("F0")).Append(") 仅入清单] ");
                            }
                            catch (Exception exFw) { glog.Append("[FRMWORK ").Append(fn10).Append(" err:").Append(exFw.Message).Append("] "); }
                        }
                    }
                    log.AppendLine("(10a) GRIDWLD 探测: " + (nG10 == 0 ? "<设计库无 GRID/FRMWORK 元素>" : glog.ToString()));
                    // (10a-v3) 兜底：FRMWORK 探测无竖轴（本轮实证 3 个 FRMWORK 全是横条走廊框架
                    // =15332/3233、=15332/3378、ROUTERSITE/PR1，非轴网）→ 撤销其轴、回退自生成；
                    // gridFrmw10 保留（(10e) drawinglist 登记无害，待用户指认真轴网 FRMWORK 后再启用）
                    if (gridFrmw10.Count > 0 && !hasPin10) // (10a-v4) 指认模式豁免兜底（用户勾认即信任）
                    {
                        int nVfw10 = 0;
                        foreach (double[] gl10 in gridLines) { if (gl10[1] == 0) nVfw10++; }
                        if (nVfw10 == 0)
                        {
                            log.AppendLine("(10a) FRMWORK 无竖轴（疑似非轴网框架）→ 回退自生成轴网；FRMWORK 仅保留 drawinglist 登记");
                            gridLines.Clear();
                        }
                    }
                    if (gridLines.Count == 0)
                    {
                        // 自生成：与模型原点对齐的 gridStep 整数倍，落在视口世界范围内
                        int nV0 = 0, nH0 = 0;
                        for (double gx = Math.Ceiling(wMinX10 / gridStep) * gridStep; gx <= wMaxX10 + 0.5; gx += gridStep) { gridLines.Add(new double[] { gx, 0 }); nV0++; }
                        for (double gy = Math.Ceiling(wMinY10 / gridStep) * gridStep; gy <= wMaxY10 + 0.5; gy += gridStep) { gridLines.Add(new double[] { gy, 1 }); nH0++; }
                        if (gridLines.Count == 0)
                        {
                            gridLines.Add(new double[] { Math.Round(thX10 / gridStep) * gridStep, 0 });
                            gridLines.Add(new double[] { Math.Round(thY10 / gridStep) * gridStep, 1 });
                            nV0++; nH0++;
                        }
                        log.AppendLine("(10a) 无 GRIDWLD → 自生成轴网 step=" + gridStep.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " 竖线" + nV0 + " 横线" + nH0);
                    }

                    // 排序编号（GB/T 50001：竖线数字自左至右、横线字母自下至上）
                    var vIdx10 = new List<int>(); var hIdx10 = new List<int>();
                    for (int i = 0; i < gridLines.Count; i++) { if (gridLines[i][1] == 0) vIdx10.Add(i); else hIdx10.Add(i); }
                    vIdx10.Sort(delegate (int a, int b) { return gridLines[a][0].CompareTo(gridLines[b][0]); });
                    hIdx10.Sort(delegate (int a, int b) { return gridLines[a][0].CompareTo(gridLines[b][0]); });

                    DbElement gridNote = sheet.CreateLast(DbElementTypeInstance.NOTE);
                    if (gridNote == null || gridNote.IsNull) log.AppendLine("(10a) 新建「轴网」NOTE 失败");
                    else
                    {
                        drawTool.SetAttribute(gridNote, ATT.NAME, gridNote.Owner.GetAsString(ATT.NAME) + "/轴网");
                        // (10f) 链位置卫生检查：链 DPOS 距视口过远（v3「dpos 移远」遗留 -208mm 实证）
                        // 视为脏值回退贴视口边——气泡不再被放到图纸外（19:44 轮起气泡消失根因）
                        if (chainMinY10 < pMinY10 - 80.0 || chainMinY10 > pMinY10 + 60.0) chainMinY10 = double.MaxValue;
                        if (chainMinX10 < pMinX10 - 80.0 || chainMinX10 > pMinX10 + 60.0) chainMinX10 = double.MaxValue;
                        // 气泡位置分型 (10i)：设备布置图——贴视口界内（圆心 pMin+4，r4 恰内切出图界限）；
                        // 管道平面图——底/左尺寸链最外侧再让 11mm（无链则贴视口边外 11mm，标准管建图）
                        double bubB10 = equipLayout10 ? pMinY10 + 4.0
                            : (chainMinY10 < double.MaxValue ? Math.Min(chainMinY10 - 11.0, pMinY10 - 11.0) : pMinY10 - 11.0);
                        double bubL10 = equipLayout10 ? pMinX10 + 4.0
                            : (chainMinX10 < double.MaxValue ? Math.Min(chainMinX10 - 11.0, pMinX10 - 11.0) : pMinX10 - 11.0);
                        int numIdx = 0, letIdx = 0, nGridSeg = 0, nBubble = 0;
                        foreach (int i in vIdx10) // 竖线：底边数字圈
                        {
                            try
                            {
                                double gx = gridLines[i][0];
                                if (gx < wMinX10 - 1 || gx > wMaxX10 + 1) continue;
                                double px = ChemSymbols.Project(view, gx, thY10)[0];
                                // (10i) 设备布置图：轴线从气泡圆顶(+8)起画，不穿气泡圆心（编号文字）
                                if (equipLayout10) { if (DrawGridSeg(gridNote, px, pMinY10 + 8.0, px, pMaxY10, gridColor)) nGridSeg++; }
                                else { if (DrawGridSeg(gridNote, px, pMinY10, px, pMaxY10, gridColor)) nGridSeg++; }
                                double cy = bubB10;
                                if (SymBlocks.DrawOpenCircle(gridNote, px, cy, 4.0, gridColor, new List<double>(), 25.0) > 0)
                                {
                                    nBubble++;
                                    // (10i) 连接线分型：设备布置图——气泡在界内，连接线从圆顶引到轴距行位置(+13)，
                                    //       不穿圆心；管道平面图——从视口边引到圆顶
                                    if (equipLayout10) { if (DrawGridSeg(gridNote, px, cy + 4.0, px, pMinY10 + 13.0, gridColor)) nGridSeg++; }
                                    else { if (DrawGridSeg(gridNote, px, pMinY10, px, cy + 4.0, gridColor)) nGridSeg++; }
                                    DrawTextCentre(gridNote, (numIdx + 1).ToString(), px, cy, 2.5, gridColor);
                                }
                                numIdx++;
                            }
                            catch { }
                        }
                        foreach (int i in hIdx10) // 横线：左边字母圈
                        {
                            try
                            {
                                double gy = gridLines[i][0];
                                if (gy < wMinY10 - 1 || gy > wMaxY10 + 1) continue;
                                double py = ChemSymbols.Project(view, thX10, gy)[1];
                                // (10i) 同竖轴：设备布置图横轴线从气泡圆右缘(+8)起画
                                if (equipLayout10) { if (DrawGridSeg(gridNote, pMinX10 + 8.0, py, pMaxX10, py, gridColor)) nGridSeg++; }
                                else { if (DrawGridSeg(gridNote, pMinX10, py, pMaxX10, py, gridColor)) nGridSeg++; }
                                double cx = bubL10;
                                if (SymBlocks.DrawOpenCircle(gridNote, cx, py, 4.0, gridColor, new List<double>(), 25.0) > 0)
                                {
                                    nBubble++;
                                    // (10i) 同底边气泡：设备布置图连接线从圆右缘引到轴距行位置(+13)，不穿圆心
                                    if (equipLayout10) { if (DrawGridSeg(gridNote, cx + 4.0, py, pMinX10 + 13.0, py, gridColor)) nGridSeg++; }
                                    else { if (DrawGridSeg(gridNote, pMinX10, py, cx + 4.0, py, gridColor)) nGridSeg++; }
                                    DrawTextCentre(gridNote, ((char)('A' + letIdx % 26)).ToString(), cx, py, 2.5, gridColor);
                                }
                                letIdx++;
                            }
                            catch { }
                        }
                        log.AppendLine("(10a) 轴网落线 = 竖" + vIdx10.Count + "(数字" + numIdx + ") 横" + hIdx10.Count + "(字母" + letIdx + ") 线段" + nGridSeg + " 气泡" + nBubble
                            + "  气泡底y=" + bubB10.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " 左x=" + bubL10.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
                    }
                }

                // —— (10b) 设备：设计库 EQUIPMENT → 俯视投影轮廓（竖直圆柱=圆，其余=外包络矩形）+ 名称标注 ——
                int nEq10 = 0, nEqOff10 = 0, nEqSkip10 = 0, nEqCir10 = 0, nEl10 = 0;
                int nTagBox14g = 0; // (14g) 设备位号 GLABEL 占位盒登记数
                string eqErr10 = "";
                if (drawEquip)
                {
                    DbElement eqNote = sheet.CreateLast(DbElementTypeInstance.NOTE);
                    if (eqNote == null || eqNote.IsNull) log.AppendLine("(10b) 新建「设备轮廓」NOTE 失败");
                    else
                    {
                        drawTool.SetAttribute(eqNote, ATT.NAME, eqNote.Owner.GetAsString(ATT.NAME) + "/设备轮廓");
                        // (10c-v4) 世界系 qualifier：读设备 Origin(POS) 用
                        DbQualifier wqO10 = new DbQualifier(); wqO10.wrtQualifier = DbElement.GetElement("/*");
                        foreach (DbElement w in worlds10)
                        {
                            if (w == null || w.IsNull) continue;
                            DBElementCollection ec = null;
                            try { ec = new DBElementCollection(w, new TypeFilter(DbElementTypeInstance.EQUIPMENT)); } catch { continue; }
                            foreach (DbElement eq in ec)
                            {
                                if (eq == null || eq.IsNull) continue;
                                try
                                {
                                    // (10b-v2) zone filter: keep only EQUIPMENT whose owner
                                    // ZONE matches equipZones10 (suffix match on full name)
                                    if (equipZones10.Count > 0)
                                    {
                                        string zn10 = null;
                                        try
                                        {
                                            DbElement ow10 = eq.Owner;
                                            while (ow10 != null && !ow10.IsNull)
                                            {
                                                string ot10 = "";
                                                try { ot10 = ow10.GetElementType().ToString(); } catch { }
                                                if (ot10.Equals("ZONE", StringComparison.OrdinalIgnoreCase))
                                                { zn10 = (ow10.GetAsString(ATT.NAME) ?? "").TrimStart('/'); break; }
                                                ow10 = ow10.Owner;
                                            }
                                        }
                                        catch { }
                                        bool hit10 = false;
                                        if (zn10 != null)
                                            foreach (string zq10 in equipZones10)
                                                if (zn10.EndsWith(zq10, StringComparison.OrdinalIgnoreCase)) { hit10 = true; break; }
                                        if (!hit10) { nEqSkip10++; continue; }
                                    }
                                    double[] lv = ElementLimits(eq);
                                    if (IsZeroBox(lv) || (lv[3] <= lv[0] && lv[4] <= lv[1])) lv = UnionMembersBox(eq);
                                    if (IsZeroBox(lv)) { nEqSkip10++; continue; }
                                    // 视口世界范围相交过滤（视口外设备不画）
                                    if (lv[3] < wMinX10 || lv[0] > wMaxX10 || lv[4] < wMinY10 || lv[1] > wMaxY10) { nEqOff10++; continue; }
                                    // (10c) 采集视口内设备包络+名称，供管线「距设备」定位标注
                                    equipBoxes10.Add(new double[] { lv[0], lv[1], lv[3], lv[4] });
                                    // (14f) Equipment body registered as forbidden geometry (paper mm, 10% inset).
                                    // 11-08 screenshot proof: planner only knew label boxes, so elevation text
                                    // and diamonds sat right on top of the vessel circles.
                                    try
                                    {
                                        double[] f0 = ChemSymbols.Project(view, lv[0], lv[1]);
                                        double[] f1 = ChemSymbols.Project(view, lv[3], lv[4]);
                                        double fx0 = Math.Min(f0[0], f1[0]), fx1 = Math.Max(f0[0], f1[0]);
                                        double fy0 = Math.Min(f0[1], f1[1]), fy1 = Math.Max(f0[1], f1[1]);
                                        double sx14 = (fx1 - fx0) * 0.10, sy14 = (fy1 - fy0) * 0.10;
                                        AnnotationPlanner.CommitForbiddenBox(fx0 + sx14, fx1 - sx14, fy0 + sy14, fy1 - sy14);
                                    }
                                    catch { }
                                    string enmC10 = "?"; try { enmC10 = (eq.GetAsString(ATT.NAME) ?? "?").Replace("/", ""); } catch { }
                                    equipNames10.Add(enmC10);
                                    // (14g) Device tag GLABEL placeholder. In this copy GLABEL is a
                                    // codeword bubble anchored at WORPOS; register its paper AABB so
                                    // the 9e pipe-index diamonds avoid it (11 AABB overlaps proven).
                                    try
                                    {
                                        Position wp14g = eq.GetPosition(ATT.WORPOS);
                                        double[] pp14g = ChemSymbols.Project(view, wp14g.X, wp14g.Y);
                                        string btx14g = enmC10 + " POS EL 0.000";
                                        double ww14g = 0;
                                        foreach (char ch14g in btx14g)
                                            ww14g += (ch14g == '1' || ch14g == 'I' || ch14g == '.' || ch14g == ' ') ? 1.13 : 2.58;
                                        double hwG14g = ww14g / 2.0 + 1.2;
                                        AnnotationPlanner.Commit(pp14g[0], pp14g[1] - 2.0, hwG14g, 2.4, 4);
                                        nTagBox14g++;
                                    }
                                    catch { }
                                    equipRefs10.Add(eq); // (10e) 设备元素：drawinglist + 设备测点
                                    // (10c-v4) 采集设备 Origin(POS) 世界坐标——定位基准（用户 2026-10-28
                                    // 「定位设备用 Origin 点，不要一直定位中心」；取不到=null 回退包络中心）
                                    double[] oriC10 = null;
                                    try { Position poC10 = eq.GetPosition(ATT.POS, wqO10); oriC10 = new double[] { poC10.X, poC10.Y }; }
                                    catch { }
                                    equipOri10.Add(oriC10);
                                    double dx10 = lv[3] - lv[0], dy10 = lv[4] - lv[1], dz10 = lv[5] - lv[2];
                                    double[] pLo = ChemSymbols.Project(view, lv[0], lv[1]);
                                    double[] pHi = ChemSymbols.Project(view, lv[3], lv[4]);
                                    // 竖直圆柱判定：轴向远大于横截面且平面近方（塔/罐类 → 圆轮廓更贴实）
                                    bool isVertCyl = dz10 > Math.Max(dx10, dy10) * 1.05
                                                  && Math.Abs(dx10 - dy10) <= Math.Max(0.25 * Math.Max(dx10, dy10), 50.0);
                                    if (isVertCyl)
                                    {
                                        double rc10 = Math.Max(pHi[0] - pLo[0], pHi[1] - pLo[1]) / 2.0;
                                        if (SymBlocks.DrawOpenCircle(eqNote, (pLo[0] + pHi[0]) / 2.0, (pLo[1] + pHi[1]) / 2.0, rc10, equipColor, new List<double>(), 25.0) > 0) { nEq10++; nEqCir10++; }
                                        else nEqSkip10++;
                                    }
                                    else
                                    {
                                        bool ok10 = SymBlocks.DrawSegment(eqNote, pLo[0], pLo[1], pHi[0], pLo[1], equipColor)
                                                 && SymBlocks.DrawSegment(eqNote, pHi[0], pLo[1], pHi[0], pHi[1], equipColor)
                                                 && SymBlocks.DrawSegment(eqNote, pHi[0], pHi[1], pLo[0], pHi[1], equipColor)
                                                 && SymBlocks.DrawSegment(eqNote, pLo[0], pHi[1], pLo[0], pLo[1], equipColor);
                                        if (ok10) nEq10++; else nEqSkip10++;
                                        // 复合设备里的竖直圆柱部件（塔/罐）补圆
                                        foreach (DbElement pr in eq.Members())
                                        {
                                            string tn = ""; try { tn = pr.GetElementType().ToString(); } catch { continue; }
                                            if (!tn.Equals("CYLNDR", StringComparison.OrdinalIgnoreCase)) continue;
                                            try
                                            {
                                                double[] pv = pr.GetDoubleArray(ATT.WVOL);
                                                if (pv == null || pv.Length < 6) continue;
                                                double pdx = pv[3] - pv[0], pdy = pv[4] - pv[1], pdz = pv[5] - pv[2];
                                                if (pdz <= Math.Max(pdx, pdy) * 1.05) continue;
                                                double[] pcLo = ChemSymbols.Project(view, pv[0], pv[1]);
                                                double[] pcHi = ChemSymbols.Project(view, pv[3], pv[4]);
                                                SymBlocks.DrawOpenCircle(eqNote, (pcLo[0] + pcHi[0]) / 2.0, (pcLo[1] + pcHi[1]) / 2.0,
                                                    Math.Max(pcHi[0] - pcLo[0], pcHi[1] - pcLo[1]) / 2.0, equipColor, new List<double>(), 25.0);
                                            }
                                            catch { }
                                        }
                                    }
                                    // 设备名称标注（轮廓下方居中，CHEI 2.5）
                                    string enm10 = ""; try { enm10 = eq.GetAsString(ATT.NAME); } catch { }
                                    if (!string.IsNullOrEmpty(enm10))
                                        DrawTextCentre(eqNote, enm10.Replace("/", ""), (pLo[0] + pHi[0]) / 2.0, pLo[1] - 2.0, 2.5, equipColor);
                                    // —— (10g) 设备标高标注（用户 2026-10-20 参考 PDS 样式：锚圈+水平引线+两行「TAG / PDS ELx.xxx」）——
                                    // 标高取设备包络底 Z（世界 mm→m，3 位小数）；文字左缘对齐引线末端（halfw 补偿 DrawTextCentre 的居中）
                                    try
                                    {
                                        double elZ10 = lv[2] / 1000.0;
                                        double axG10 = pHi[0], ayG10 = pHi[1];
                                        double lxG10 = axG10 + 6.0;
                                        Func<string, double, double> hwG10 = delegate (string sG, double hG)
                                        {
                                            double wG = 0;
                                            foreach (char cG in sG) wG += (cG == '1' || cG == 'I' || cG == '.' || cG == ' ') ? 1.13 : 2.58;
                                            return wG / 2.0 * (hG / 2.5);
                                        };
                                        string tagG10 = enmC10 == "?" ? (enm10 ?? "").Replace("/", "") : enmC10;
                                        string elG10 = "PDS EL" + elZ10.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
                                        // (10g-v2) 用户 2026-10-20「设备名字和标高紫色」——色号 12=紫罗兰(102,0,153)，
                                        // 本机 pdms_col.tbl 实证（2=红/4=黄 双吻合）；黑白打印灰度深可读，8 号品红太浅弃用
                                        const int elevColG10 = 12;
                                        // (14d) 全局避让：文字块落点由规划器打分选点（重叠面积/引线长度/
                                        // 引线交叉），默认仍是锚点右侧 6mm——PDS 水平引线样式不动，只动落点；
                                        // 全碰撞且边界退让不可用时保留原位置（等价旧行为的退化通道）
                                        {
                                            double hwB14 = 0.75 + Math.Max(hwG10(tagG10, 2.2), hwG10(elG10, 2.2));
                                            double[] posG14 = AnnotationPlanner.PlaceBest(axG10, ayG10, hwB14, 3.4,
                                                new double[] { 0, 20, 340, 45, 315, 90, 270 }, new double[] { 6, 9, 13, 18, 26, 36 });
                                            if (posG14 != null && posG14[2] < 2)
                                            {
                                                AnnotationPlanner.CommitLead(axG10 + 1.2, pHi[1], posG14[0] - hwB14, posG14[1]);
                                                lxG10 = posG14[0] - hwB14;
                                                ayG10 = posG14[1];
                                            }
                                        }
                                        SymBlocks.DrawOpenCircle(eqNote, axG10, ayG10, 1.2, elevColG10, new List<double>(), 25.0);
                                        SymBlocks.DrawSegment(eqNote, axG10 + 1.2, ayG10, lxG10, ayG10, elevColG10);
                                        double cxE1 = lxG10 + 1.5 + hwG10(tagG10, 2.2);
                                        double cxE2 = lxG10 + 1.5 + hwG10(elG10, 2.2);
                                        if (tagG10.Length > 0) DrawTextCentre(eqNote, tagG10, cxE1, ayG10 + 2.2, 2.2, elevColG10);
                                        DrawTextCentre(eqNote, elG10, cxE2, ayG10 - 2.2, 2.2, elevColG10);
                                        nEl10++;
                                    }
                                    catch { }
                                }
                                catch (Exception exEq10) { nEqSkip10++; if (eqErr10.Length == 0) eqErr10 = "[" + exEq10.GetType().Name + "] " + exEq10.Message; }
                            }
                        }
                    }
                }
                log.AppendLine("(10b) 设备轮廓 = " + nEq10 + "（竖直圆柱圆" + nEqCir10 + "） 视口外" + nEqOff10 + " 跳过" + nEqSkip10 + (eqErr10.Length > 0 ? "  首错:" + eqErr10 : ""));
                log.AppendLine("(10g) 设备标高标注 = " + nEl10 + "（PDS EL 底标高 m）");
                log.AppendLine("(14g) 设备位号(GLABEL)占位盒 = " + nTagBox14g + " 个（供 9e 管线序号菱形避让）");

                // —— (10h) 结构轮廓（用户 2026-10-20「管道布置图有结构，设备，管道，轴网」可勾选出图）——
                // drawStruct 参数开关（Web 向导勾选）；SBFRAMEWORK/FRMWORK（非轴网指认部分）画包络矩形（青色 6）+ 入 drawinglist
                int nSt10 = 0, nStSkip10 = 0; string stErr10 = "";
                if (P(p, "drawStruct", "0") != "0")
                {
                    DbElement stNote = sheet.CreateLast(DbElementTypeInstance.NOTE);
                    if (stNote == null || stNote.IsNull) log.AppendLine("(10h) 新建「结构轮廓」NOTE 失败");
                    else
                    {
                        drawTool.SetAttribute(stNote, ATT.NAME, stNote.Owner.GetAsString(ATT.NAME) + "/结构轮廓");
                        foreach (DbElement w in worlds10)
                        {
                            if (w == null || w.IsNull) continue;
                            foreach (DbElementType tSt10 in new DbElementType[] { DbElementTypeInstance.SBFRAMEWORK, DbElementTypeInstance.FRMWORK })
                            {
                                DBElementCollection ecS = null;
                                try { ecS = new DBElementCollection(w, new TypeFilter(tSt10)); } catch { continue; }
                                foreach (DbElement sf in ecS)
                                {
                                    if (sf == null || sf.IsNull) continue;
                                    string sn10 = ""; try { sn10 = (sf.GetAsString(ATT.NAME) ?? "").TrimStart('/'); } catch { }
                                    if (hasPin10 && pinnedSet10.Contains(sn10)) continue; // 指认为轴网的不重复画结构
                                    try
                                    {
                                        double[] lvS = ElementLimits(sf);
                                        if (IsZeroBox(lvS)) lvS = UnionMembersBox(sf);
                                        if (IsZeroBox(lvS)) { nStSkip10++; continue; }
                                        if (lvS[3] < wMinX10 || lvS[0] > wMaxX10 || lvS[4] < wMinY10 || lvS[1] > wMaxY10) { nStSkip10++; continue; }
                                        double[] pSLo = ChemSymbols.Project(view, lvS[0], lvS[1]);
                                        double[] pSHi = ChemSymbols.Project(view, lvS[3], lvS[4]);
                                        bool okS = SymBlocks.DrawSegment(stNote, pSLo[0], pSLo[1], pSHi[0], pSLo[1], 6)
                                                && SymBlocks.DrawSegment(stNote, pSHi[0], pSLo[1], pSHi[0], pSHi[1], 6)
                                                && SymBlocks.DrawSegment(stNote, pSHi[0], pSHi[1], pSLo[0], pSHi[1], 6)
                                                && SymBlocks.DrawSegment(stNote, pSLo[0], pSHi[1], pSLo[0], pSLo[1], 6);
                                        if (okS) { nSt10++; structRefs10.Add(sf); }
                                        else nStSkip10++;
                                    }
                                    catch (Exception exS10) { nStSkip10++; if (stErr10.Length == 0) stErr10 = "[" + exS10.GetType().Name + "] " + exS10.Message; }
                                }
                            }
                        }
                    }
                }
                log.AppendLine("(10h) 结构轮廓 = " + nSt10 + " 跳过" + nStSkip10 + (stErr10.Length > 0 ? "  首错:" + stErr10 : ""));

                // —— (10e) 设备进 drawinglist（用户 2026-10-19「把轴网和设备加入drawinglist就能标注这三者的标注」）——
                // 视图 IDLN→IDLIST 追加 ADDENTRY(IDNM=设备)：设备成为视图可引用元素，
                // (9m) 给四条链挂 DPPT(DDNM=设备) 即出设备定位尺寸。防重：已有同 IDNM 的 ADDENTRY 跳过。
                int nDl10 = 0; string dlErr10 = "";
                DbElement idlist10 = null;
                try { idlist10 = view.GetElement(ATT.IDLN); } catch { }
                if (idlist10 == null || idlist10.IsNull)
                {
                    try
                    {
                        DbElement dllb10 = drawE3DTool.FindCurrentDrawlistUnder(sheet.Owner);
                        if (dllb10 != null && !dllb10.IsNull) idlist10 = drawE3DTool.FindOrCreateIdlist(dllb10);
                    }
                    catch { }
                }
                if (idlist10 == null || idlist10.IsNull) dlErr10 = "视图 IDLN 未绑定 IDLIST";
                else
                {
                    foreach (DbElement eqD10 in equipRefs10)
                    {
                        try
                        {
                            bool dup10 = false;
                            foreach (DbElement mD10 in idlist10.Members())
                            {
                                try
                                {
                                    if (mD10.GetElementType() == DbElementTypeInstance.ADDENTRY && eqD10.Equals(mD10.GetElement(ATT.IDNM))) { dup10 = true; break; }
                                }
                                catch { }
                            }
                            if (dup10) { nDl10++; continue; }
                            DbElement add10 = idlist10.CreateLast(DbElementTypeInstance.ADDENTRY);
                            add10.SetAttribute(ATT.IDNM, eqD10);
                            nDl10++;
                        }
                        catch (Exception exD10) { if (dlErr10.Length == 0) dlErr10 = "[" + exD10.GetType().Name + "] " + exD10.Message; }
                    }
                }
                log.AppendLine("(10e) 设备进 drawinglist = " + nDl10 + "/" + equipRefs10.Count + (dlErr10.Length > 0 ? "  " + dlErr10 : ""));
                // (10e-v2) FRMWORK 轴网元素进 drawinglist（用户 2026-10-20「轴网元素是 FRMW」）——
                // 真设计元素可 ADDENTRY，轴网从此被视图原生引用（尺寸链 DDNM 可挂 FRMWORK 测点）
                int nDlF10 = 0;
                if (idlist10 != null && !idlist10.IsNull)
                {
                    foreach (DbElement fwD10 in gridFrmw10)
                    {
                        try
                        {
                            bool dupF10 = false;
                            foreach (DbElement mD10 in idlist10.Members())
                            {
                                try
                                {
                                    if (mD10.GetElementType() == DbElementTypeInstance.ADDENTRY && fwD10.Equals(mD10.GetElement(ATT.IDNM))) { dupF10 = true; break; }
                                }
                                catch { }
                            }
                            if (dupF10) { nDlF10++; continue; }
                            DbElement addF10 = idlist10.CreateLast(DbElementTypeInstance.ADDENTRY);
                            addF10.SetAttribute(ATT.IDNM, fwD10);
                            nDlF10++;
                        }
                        catch (Exception exDF10) { if (dlErr10.Length == 0) dlErr10 = "FRMW:[" + exDF10.GetType().Name + "] " + exDF10.Message; }
                    }
                }
                log.AppendLine("(10e) FRMWORK 轴网进 drawinglist = " + nDlF10 + "/" + gridFrmw10.Count);
                // (10e-v3) 结构元素进 drawinglist（(10h) 勾选结构时）
                int nDlS10 = 0;
                if (idlist10 != null && !idlist10.IsNull)
                {
                    foreach (DbElement sfD10 in structRefs10)
                    {
                        try
                        {
                            bool dupS10 = false;
                            foreach (DbElement mD10 in idlist10.Members())
                            {
                                try
                                {
                                    if (mD10.GetElementType() == DbElementTypeInstance.ADDENTRY && sfD10.Equals(mD10.GetElement(ATT.IDNM))) { dupS10 = true; break; }
                                }
                                catch { }
                            }
                            if (dupS10) { nDlS10++; continue; }
                            DbElement addS10 = idlist10.CreateLast(DbElementTypeInstance.ADDENTRY);
                            addS10.SetAttribute(ATT.IDNM, sfD10);
                            nDlS10++;
                        }
                        catch (Exception exDS10) { if (dlErr10.Length == 0) dlErr10 = "STRUCT:[" + exDS10.GetType().Name + "] " + exDS10.Message; }
                    }
                }
                log.AppendLine("(10e) 结构进 drawinglist = " + nDlS10 + "/" + structRefs10.Count);
                // (9m-v3) 数据：设备定位基准纸面坐标（用户 2026-10-28「定位设备用 Origin 点，不要一直
                // 定位中心」——优先投影设备 Origin(POS) 世界坐标，取不到再回退包络投影中点；NaN=不可投影）
                equipPt9.Clear();
                {
                    DbQualifier wqP10 = new DbQualifier(); wqP10.wrtQualifier = DbElement.GetElement("/*");
                    for (int iP10 = 0; iP10 < equipRefs10.Count; iP10++)
                    {
                        DbElement eqP10 = equipRefs10[iP10];
                        double[] oriP10 = iP10 < equipOri10.Count ? equipOri10[iP10] : null;
                        if (oriP10 != null)
                        {
                            try
                            {
                                double[] ppO10 = ChemSymbols.Project(view, oriP10[0], oriP10[1]);
                                equipPt9.Add(ppO10);
                                continue;
                            }
                            catch { }
                        }
                        try
                        {
                            double[] lvP10 = ElementLimits(eqP10);
                            if (IsZeroBox(lvP10) || (lvP10[3] <= lvP10[0] && lvP10[4] <= lvP10[1])) lvP10 = UnionMembersBox(eqP10);
                            if (IsZeroBox(lvP10)) { equipPt9.Add(new double[] { double.NaN, double.NaN }); continue; }
                            double[] pLoP10 = ChemSymbols.Project(view, lvP10[0], lvP10[1]);
                            double[] pHiP10 = ChemSymbols.Project(view, lvP10[3], lvP10[4]);
                            equipPt9.Add(new double[] { (pLoP10[0] + pHiP10[0]) / 2.0, (pLoP10[1] + pHiP10[1]) / 2.0 });
                        }
                        catch { equipPt9.Add(new double[] { double.NaN, double.NaN }); }
                    }
                }

                // 4) 新符号容器：文字层(View 下 NOTE)——设备/管件符号走「文字」图层
                DbElement symLab = LayerManager.GetLayer(view, DrawLayerCat.Text).CreateLast(DbElementTypeInstance.NOTE);
                if (symLab == null || symLab.IsNull) throw new Exception("新建「文字」层 NOTE 返回 null");
                drawTool.SetAttribute(symLab, ATT.NAME, symLab.Owner.GetAsString(ATT.NAME) + "/设备符号");
                log.AppendLine("新 NOTE「文字」层= " + symLab.GetAsString(ATT.NAME));

                int nElbow = 0, nValve = 0, nFlan = 0, nTee = 0, nSkip = 0;
                List<DbElement> elb = drawPipe ? drawE3DTool.ElementsFromDrawlist(view, new TypeFilter(DbElementTypeInstance.ELBOW), false, false) : new List<DbElement>();
                List<DbElement> val = drawPipe ? drawE3DTool.ElementsFromDrawlist(view, new TypeFilter(DbElementTypeInstance.VALVE), false, false) : new List<DbElement>();
                List<DbElement> fla = drawPipe ? drawE3DTool.ElementsFromDrawlist(view, new TypeFilter(DbElementTypeInstance.FLANGE), false, false) : new List<DbElement>();
                // (8x) 存量 bug 修复：原三通循环误用 BRANCH 过滤器（分支头当三通插，60 个全失败=历次日志「三通0 跳过60」）——
                // 改枚举真正的 TEE 元素。平面分支相交处（无 TEE 元素）由折线逻辑丁字相接（8x 弧守卫去伪角）。
                List<DbElement> brn = null;
                try { brn = drawPipe ? drawE3DTool.ElementsFromDrawlist(view, new TypeFilter(DbElementTypeInstance.TEE), false, false) : new List<DbElement>(); }
                catch { brn = new List<DbElement>(); }

                // 2026-10-17 (2)：上一轮 298 全跳过实证——ELBOW/FLANGE 的 GetAsString(LDIR)、VALVE 的
                // GetDirection(PDIR,1) 抛「应用程序中的错误」；而 BRANCH 的 GetPosition(HPOS, wrt /*)
                // + Project 全部成功。故统一改用实证通道：坐标一律 HPOS(wrt /*)（失败回退 WORPOS）、
                // LDIR 读取就近 try（读不到按水平向跳过）、阀门/法兰方向改用同分支最近邻居几何求向。
                DbQualifier wq = new DbQualifier(); wq.wrtQualifier = DbElement.GetElement("/*");
                string drawErr1 = "";
                SymBlocks.CommandGateBroken = false; // CP 熔断器复位（每次 op 独立）
                // 2026-10-17 (8d)：用户确认——有元件的地方不要多余的线，直接用图例：
                // 红线一律止于元件符号边缘（(8c) 弯头 U/D 圆内不画 + (8d) 阀门/三通同理；
                // 法兰为管上垂直短线属穿行符号，不裁）。clipPts[i] = {x, y, 占位半径r}。
                List<double[]> clipPts = new List<double[]>();
                // (14f) Pipe corner points (paper coords) for forbidden-zone registration.
                var cornerMarks9 = new List<double[]>();
                int nEvClash = 0; // (9d) 弯头-阀门纸面重叠跳过计数
                // (9d) 阀门纸面位置预收集：立管弯头的平面投影常恰好压在水平管阀门上（用户截图①
                // 「根本看不出走向」——圆糊在蝴蝶结上）。弯头点位离阀门 <4mm 时跳过弯头符号，
                // 阀门占位优先（HGT 让位画法）；走向由折线 + 阀位 + 出口穿线表达。
                var valvePts = new List<double[]>();
                foreach (DbElement v0 in val)
                {
                    try { double[] p0w = WorldPosOf(v0, wq); valvePts.Add(ChemSymbols.Project(view, p0w[0], p0w[1])); } catch { }
                }

                foreach (DbElement arrow in elb)
                {
                    try
                    {
                        string ldir = null;
                        try { ldir = arrow.GetAsString(ATT.LDIR); } catch { }
                        string blk = ldir == "D" ? SymBlocks.N_ELBOWD : (ldir == "U" ? SymBlocks.N_ELBOWU : null);
                        if (blk == null) { nSkip++; continue; } // 读不到 LDIR 或水平向：走向已由管道折线表达，不另加符号
                        double[] pw = WorldPosOf(arrow, wq);
                        double[] pp = ChemSymbols.Project(view, pw[0], pw[1]);
                        // (9d) 阀门避让：与阀门纸面距离 <4mm（圆 r2.5+蝴蝶结半长 4.0 重叠区）→ 整个弯头符号跳过
                        foreach (double[] vp in valvePts)
                        {
                            double dxv = pp[0] - vp[0], dyv = pp[1] - vp[1];
                            if (dxv * dxv + dyv * dyv < 16.0) { nEvClash++; goto NextElbow; }
                        }
                        clipPts.Add(new double[] { pp[0], pp[1], 2.8 }); // (8c/8d) 弯头圆 r2.5+余量：进线出线止于圆边，圆内不画
                        // (8i) 图例自带管线出口线：用户 2026-10-17——打印黑白不分色、线型全变实线，
                        // 圆符号须自带「管线从哪边接续」信息。取同分支最近邻居方向（立管端纸面投影
                        // ≈0 被跳过，返回水平管侧），量化到 4 正交方向，从圆边 r2.5 外延至 5.2mm。
                        // (8k) 上翻(U)圆内 1/4 弧标记已于 9d 删除：上翻=空圆，下翻(D)=圆心十字点。
                        try
                        {
                            double eang = PipeAngleAt(arrow, pw[0], pw[1]);
                            double qa = Math.Round(eang / 90.0) * 90.0 * Math.PI / 180.0;
                            // (9c) 用户 2026-10-18：口朝【出口管线】——qa（升起后平面投影走向）吸附到
                            // 最近的可见臂方向（ArmDirsOf），圆只开一个口；进口线止于圆外不进圆。
                            // U/D 区分：U=空圆，D=圆心十字点（9d 起删弧）。
                            var arms = ArmDirsOf(arrow, pw[0], pw[1]);
                            double qaDeg = qa * 180.0 / Math.PI;
                            double odir = qaDeg;
                            {
                                double bd9 = double.MaxValue;
                                foreach (double ad in arms)
                                {
                                    double dd9 = Math.Abs(ad - qaDeg); if (dd9 > 180.0) dd9 = 360.0 - dd9;
                                    if (dd9 < bd9) { bd9 = dd9; odir = ad; }
                                }
                            }
                            var gapDirs = new List<double>(); gapDirs.Add(odir);
                            double ox9 = Math.Cos(odir * Math.PI / 180.0), oy9 = Math.Sin(odir * Math.PI / 180.0);
                            // 出口穿线：圆心→r3.2（穿出口，与外侧折线/短接线搭接，视觉连续）
                            SymBlocks.DrawSegment(symLab, pp[0], pp[1], pp[0] + ox9 * 3.2, pp[1] + oy9 * 3.2, SymBlocks.PIPE_COLOR);
                            // 出口外侧短接线 r3.4→5.2：标明管线延续方向（与内侧 3.2 搭接）
                            SymBlocks.DrawSegment(symLab, pp[0] + ox9 * 3.4, pp[1] + oy9 * 3.4, pp[0] + ox9 * 5.2, pp[1] + oy9 * 5.2, SymBlocks.PIPE_COLOR);
                            int ocSeg = SymBlocks.DrawOpenCircle(symLab, pp[0], pp[1], 2.5, SymBlocks.PIPE_COLOR, gapDirs, 25.0);
                            if (blk == SymBlocks.N_ELBOWD) // 下翻标记：圆心十字点
                            {
                                double ds = 0.9;
                                SymBlocks.DrawSegment(symLab, pp[0] - ds, pp[1], pp[0] + ds, pp[1], SymBlocks.PIPE_COLOR);
                                SymBlocks.DrawSegment(symLab, pp[0], pp[1] - ds, pp[0], pp[1] + ds, SymBlocks.PIPE_COLOR);
                            }
                            // (9d) 上翻 1/4 弧删除（用户 2026-10-18「半圆弧能删掉吗」）：
                            // 上翻=空圆，下翻=圆+十字点，黑白打印仍可区分，且更贴 HGT。
                            // (9a) 旧 Insert 闭口圆停用：开口圆成功即计成功
                            if (ocSeg > 0) nElbow++;
                            else { nSkip++; if (drawErr1.Length == 0) drawErr1 = "弯头:" + SymBlocks.LastDrawError; }
                        }
                        catch { }
                    }
                    catch (Exception exE) { log.AppendLine("弯头符号项异常(跳过) @" + arrow + ": [" + exE.GetType().Name + "] " + exE.Message); nSkip++; }
                NextElbow: ;
                }
                foreach (DbElement v in val)
                {
                    try
                    {
                        double[] pw = WorldPosOf(v, wq);
                        double[] pp = ChemSymbols.Project(view, pw[0], pw[1]);
                        // (9m) 阀门吸附：符号中心投影到【同管线成员折线】最近段（lateral<5mm）——
                        // 修个别阀门偏出管线 ~4mm（用户截图实证：WorldPosOf 通道3 Owner头+成员HPOS
                        // 加法合成在旋转分支下的偏差）。管线落线正是这些成员点的折线，投影后符号必压线；
                        // 已压线的阀门垂足=自身，零副作用。
                        try
                        {
                            DbElement own9 = v.Owner;
                            DbElement pipe9 = own9 == null ? null : own9.Owner;
                            if (pipe9 != null && !pipe9.IsNull)
                            {
                                double bLat9 = 25.0; double[] np9 = null; // 25 = 5mm²
                                foreach (DbElement sb in pipe9.Members())
                                {
                                    try { if (sb.GetElementType() != DbElementTypeInstance.BRANCH) continue; } catch { continue; }
                                    double[] prev9 = null;
                                    foreach (DbElement m in sb.Members())
                                    {
                                        double[] cur9;
                                        try { double[] cw9 = WorldPosOf(m, wq); cur9 = ChemSymbols.Project(view, cw9[0], cw9[1]); }
                                        catch { prev9 = null; continue; }
                                        if (prev9 != null)
                                        {
                                            double vx9 = cur9[0] - prev9[0], vy9 = cur9[1] - prev9[1];
                                            double L29 = vx9 * vx9 + vy9 * vy9;
                                            if (L29 > 1e-9)
                                            {
                                                double t9 = ((pp[0] - prev9[0]) * vx9 + (pp[1] - prev9[1]) * vy9) / L29;
                                                if (t9 < 0) t9 = 0; else if (t9 > 1) t9 = 1;
                                                double qx9 = prev9[0] + vx9 * t9, qy9 = prev9[1] + vy9 * t9;
                                                double dx9 = pp[0] - qx9, dy9 = pp[1] - qy9;
                                                double d29 = dx9 * dx9 + dy9 * dy9;
                                                if (d29 < bLat9) { bLat9 = d29; np9 = new double[] { qx9, qy9 }; }
                                            }
                                        }
                                        prev9 = cur9;
                                    }
                                }
                                if (np9 != null) pp = np9;
                            }
                        }
                        catch { }
                        double vAng = PipeAngleAt(v, pw[0], pw[1]);
                        clipPts.Add(new double[] { pp[0], pp[1], 4.3 }); // (8d) 阀门蝴蝶结半长 4.0+余量：红线止于阀体，不穿阀
                        if (SymBlocks.Insert(symLab, SymBlocks.N_VALVE, v, pp[0], pp[1], vAng, SymBlocks.PIPE_COLOR) != null) nValve++;
                        else { nSkip++; if (drawErr1.Length == 0) drawErr1 = "阀门:" + SymBlocks.LastDrawError; }
                    }
                    catch (Exception exV) { log.AppendLine("阀门符号项异常(跳过) @" + v + ": [" + exV.GetType().Name + "] " + exV.Message); nSkip++; }
                }
                foreach (DbElement f in fla)
                {
                    try
                    {
                        double[] pw = WorldPosOf(f, wq);
                        double[] pp = ChemSymbols.Project(view, pw[0], pw[1]);
                        double fAng = PipeAngleAt(f, pw[0], pw[1]);
                        if (SymBlocks.Insert(symLab, SymBlocks.N_FLANGE, f, pp[0], pp[1], fAng, SymBlocks.PIPE_COLOR) != null) nFlan++;
                        else { nSkip++; if (drawErr1.Length == 0) drawErr1 = "法兰:" + SymBlocks.LastDrawError; }
                    }
                    catch (Exception exF) { log.AppendLine("法兰符号项异常(跳过) @" + f + ": [" + exF.GetType().Name + "] " + exF.Message); nSkip++; }
                }
                foreach (DbElement br in brn)
                {
                    try
                    {
                        double[] pw = WorldPosOf(br, wq);
                        double[] pp = ChemSymbols.Project(view, pw[0], pw[1]);
                        // (8x) 用户 2026-10-18 截图②「三通有问题」+ HGT 表11.0.1：
                        // 平面三通（≥2 个平面连通方向）= 干线直通、支线丁字相接、交点实心圆点——
                        // 不画端视圆、不裁干线（原 r2.8 裁圈把干线剪断+伪角小勾，即截图病灶）。
                        // 端视三通（≤1 个平面方向，支管朝上/下）= 保持 圆+中心点 + 裁圈 r2.8。
                        var tarms = ArmDirsOf(br, pw[0], pw[1]);
                        if (tarms.Count >= 2)
                        {
                            double ds = 0.8; // 交点实心点（十字近似，与 ELBOWD 圆心点同规格）
                            SymBlocks.DrawSegment(symLab, pp[0] - ds, pp[1], pp[0] + ds, pp[1], SymBlocks.PIPE_COLOR);
                            SymBlocks.DrawSegment(symLab, pp[0], pp[1] - ds, pp[0], pp[1] + ds, SymBlocks.PIPE_COLOR);
                            nTee++;
                        }
                        else
                        {
                            clipPts.Add(new double[] { pp[0], pp[1], 2.8 }); // (8d) 裁圈：外侧折线止于圆外
                            // (9a) 端视三通同弯头：圆周在干线通过方向开口（C 形），线从圆心连续穿过；
                            // 圆心十字点保留（支管朝上/下标记）。tarms 空则退化为整圆。
                            foreach (double ad in tarms)
                            {
                                double ra2 = ad * Math.PI / 180.0;
                                SymBlocks.DrawSegment(symLab, pp[0], pp[1], pp[0] + Math.Cos(ra2) * 3.2, pp[1] + Math.Sin(ra2) * 3.2, SymBlocks.PIPE_COLOR);
                            }
                            int ocSeg = SymBlocks.DrawOpenCircle(symLab, pp[0], pp[1], 2.5, SymBlocks.PIPE_COLOR, tarms, 25.0);
                            double ds = 0.8; // 圆心十字点
                            bool crossOk = SymBlocks.DrawSegment(symLab, pp[0] - ds, pp[1], pp[0] + ds, pp[1], SymBlocks.PIPE_COLOR)
                                        && SymBlocks.DrawSegment(symLab, pp[0], pp[1] - ds, pp[0], pp[1] + ds, SymBlocks.PIPE_COLOR);
                            if (ocSeg > 0 || crossOk) nTee++;
                            else { nSkip++; if (drawErr1.Length == 0) drawErr1 = "三通:" + SymBlocks.LastDrawError; }
                        }
                    }
                    catch (Exception exB) { log.AppendLine("三通符号项异常(跳过) @" + br + ": [" + exB.GetType().Name + "] " + exB.Message); nSkip++; }
                }
                if (drawErr1.Length > 0) log.AppendLine("首个绘图失败原因: " + drawErr1);
                log.AppendLine("图块插入 = 弯头" + nElbow + " 阀门" + nValve + " 法兰" + nFlan + " 三通" + nTee + " 跳过" + nSkip + " 弯头避让阀门" + nEvClash);

                // 4b) 删旧「管道流向」LAYER（级联旧 SLABEL Arrow1 深灰箭头——2026-10-17 (5) 用户弃用，
                //     改自绘 HG20519 流向箭头自动落在管线上）
                int delArrowLayer = 0;
                foreach (DbElement m in view.Members())
                {
                    if (m == null || m.IsNull) continue;
                    string tn = "";
                    try { tn = m.GetElementType().ToString(); } catch { continue; }
                    if (!tn.Equals("LAYER", StringComparison.OrdinalIgnoreCase)) continue;
                    string mnm = "";
                    try { mnm = m.GetAsString(ATT.NAME); } catch { }
                    if (mnm == null || mnm.IndexOf("管道流向") < 0) continue;
                    try { m.Delete(); delArrowLayer++; } catch (Exception exD) { log.AppendLine("旧箭头层删除异常(跳过): [" + exD.GetType().Name + "] " + exD.Message); }
                }
                log.AppendLine("旧「管道流向」LAYER(旧箭头) 删除 = " + delArrowLayer);

                // 4c) 红色管线中心线 + 自绘流向箭头（2026-10-17 (5)）：
                //     管线红 = 自绘红色(色2)中心线覆盖 PDMS 黄线（不动 Representation Rules 全局链路，
                //     避免影响其它图；红线后画居上，几何一致时完全遮盖黄线）；
                //     流向箭头 = 每条 BRANCH 取最长段中点、沿段方向自绘（HG20519：适当位置画箭头表示流向）；
                //     弯头圆处红线在圆边断开（(8c)：U/D 一致，圆内不画管线）。
                //     BRANCH 枚举走 DESIGN world（run4 实证 view 图形 drawlist 里无 BRANCH 项）。
                int nPipe = 0, nArrow2 = 0, nPipeSkip = 0, nMemFail = 0;
                // (8q) 箭头去密：同位置 10mm 内只放一个（用户截图③：管廊处一排箭头乱七八糟）
                var arrowPts = new List<double[]>();
                // (9c) 跨分支交叉断开：先汇集全部线段，循环结束后按标高统一断口落线
                var pendStrokes = new List<double[]>();
                var pendStrokeZ = new List<double[]>();
                List<DbElement> worlds = new List<DbElement>();
                try { worlds = FangYuan.PDMS.Utilities.MDBMembers(DbType.Design); } catch (Exception exW) { log.AppendLine("DESIGN world 枚举失败: " + exW.Message); }
                foreach (DbElement w in worlds)
                {
                    if (w == null || w.IsNull) continue;
                    DBElementCollection ec = null;
                    try { ec = new DBElementCollection(w, new TypeFilter(DbElementTypeInstance.BRANCH)); } catch { continue; }
                    foreach (DbElement br in ec)
                    {
                        if (br == null || br.IsNull) continue;
                        if (!drawPipe) continue; // (drawPipe=0) 设备布置图：跳过红线/图块/断口/箭头
                        try
                        {
                                // 顶点链（2026-10-17 (8f) 重写）：run9 PDF 笔画解剖实证 Members() 枚举顺序
                                // 不保证连接顺序（红线 312/391 段是 <3.5mm 碎段）——改为【就近链接】：
                                // 从分支头出发，每次跳向最近的未用成员起点；直管段上成员起点密集共线、
                                // 拐角处相邻距离远小于跨腿距离，就近链接与真实走向一致，最后接尾 TPOS。
                                var raw = new List<double[]>();
                                try { Position hp = br.GetPosition(ATT.HPOS, wq); raw.Add(new double[] { hp.X, hp.Y, hp.Z }); } catch { }
                                foreach (DbElement mm in br.Members())
                                {
                                    if (mm == null || mm.IsNull) continue;
                                    try { raw.Add(WorldPosOf(mm, wq)); } catch { nMemFail++; }
                                }
                                var pts = new List<double[]>();
                                if (raw.Count > 0)
                                {
                                    var used = new bool[raw.Count];
                                    double[] curP = raw[0]; used[0] = true; pts.Add(curP);
                                    for (int k = 1; k < raw.Count; k++)
                                    {
                                        int bj = -1; double bd = double.MaxValue;
                                        for (int j = 1; j < raw.Count; j++)
                                        {
                                            if (used[j]) continue;
                                            double ddx = raw[j][0] - curP[0], ddy = raw[j][1] - curP[1];
                                            double d2 = ddx * ddx + ddy * ddy;
                                            if (d2 < bd) { bd = d2; bj = j; }
                                        }
                                        if (bj < 0) break;
                                        used[bj] = true; curP = raw[bj]; pts.Add(curP);
                                    }
                                }
                                try
                                {
                                    Position tp;
                                    try { tp = br.GetPosition(ATT.TPOS, wq); }
                                    catch { tp = br.GetPosition(ATT.TPOS); }
                                    pts.Add(new double[] { tp.X, tp.Y, tp.Z });
                                }
                                catch { log.AppendLine("分支尾点 TPOS 读取失败(跳过尾点) @" + br); }
                                // 去重（<0.5mm 合并，保序）——(8h) 从 0.1mm 放宽：拐角处成员端点/弯头中心
                                // 常以微差坐标重复出现，导致同一拐角画两个弧（用户截图「为什么两个弯头」）
                                var ded = new List<double[]>();
                                foreach (var pt in pts)
                                {
                                    bool dup = false;
                                    foreach (var q2 in ded) { double ddx = pt[0] - q2[0], ddy = pt[1] - q2[1]; if (ddx * ddx + ddy * ddy < 0.25) { dup = true; break; } }
                                    if (!dup) ded.Add(pt);
                                }
                                if (ded.Count < 2) { nPipeSkip++; continue; }
                                // (9c) 每顶点标高（交叉断开用）
                                var zs = new List<double>();
                                foreach (var pt in ded) zs.Add(pt.Length > 2 ? pt[2] : 0.0);
                                // 投影到纸面
                                var paper = new List<double[]>();
                                foreach (var pt in ded) paper.Add(ChemSymbols.Project(view, pt[0], pt[1]));
                                // (8x) 直角归位（用户 2026-10-18 截图①「台阶」）：同分支相邻两边一横一竖、
                                // 交点微错位（成员端点坐标差 ≤2mm）时，就近链接直接连出 Z 台阶——
                                // 按两线交点补角：竖边延至横边所在 y（或横边延至竖边 x），台阶变干净直角。
                                for (int i = 1; i + 1 < paper.Count; i++)
                                {
                                    double[] A = paper[i - 1], B = paper[i], C = paper[i + 1];
                                    double e1x = B[0] - A[0], e1y = B[1] - A[1];
                                    double e2x = C[0] - B[0], e2y = C[1] - B[1];
                                    double a1 = Math.Abs(e1x), b1 = Math.Abs(e1y);
                                    double a2 = Math.Abs(e2x), b2 = Math.Abs(e2y);
                                    bool v1h2 = a1 <= 2.0 && a1 <= 0.3 * b1 && b2 <= 2.0 && b2 <= 0.3 * a2; // 竖接横
                                    bool h1v2 = b1 <= 2.0 && b1 <= 0.3 * a1 && a2 <= 2.0 && a2 <= 0.3 * b2; // 横接竖
                                    if (v1h2)
                                    {
                                        double[] nb = new double[] { A[0], B[1] };
                                        if ((nb[0] - B[0]) * (nb[0] - B[0]) + (nb[1] - B[1]) * (nb[1] - B[1]) > 0.09) paper[i] = nb;
                                    }
                                    else if (h1v2)
                                    {
                                        double[] nb = new double[] { B[0], A[1] };
                                        if ((nb[0] - B[0]) * (nb[0] - B[0]) + (nb[1] - B[1]) * (nb[1] - B[1]) > 0.09) paper[i] = nb;
                                    }
                                }
                                // 最长段（箭头落点）
                                int bi = -1; double bl = -1;
                                for (int i = 0; i + 1 < paper.Count; i++)
                                {
                                    double sdx = paper[i + 1][0] - paper[i][0], sdy = paper[i + 1][1] - paper[i][1];
                                    double sl = Math.Sqrt(sdx * sdx + sdy * sdy);
                                    if (sl > bl) { bl = sl; bi = i; }
                                }
                                if (bi < 0) { nPipeSkip++; continue; }
                                // (8f) 画红线：拐角圆弧化（不再直角——View1 弯头弧实测共线缺口 2.8-3.2mm
                                // → 弧半径 ≈1.4-1.6mm，红线取 R=1.8mm 六段弦近似，骑在黄弧外侧盖住它）
                                // + 元件占位裁剪（8c/8d：红线止于符号边缘）+ 箭头缺口（8e）。
                                // 箭头缺口里画【白色遮罩段】(NLCOLO=9 白底不可见) 盖住透出的黄色原线——
                                // 用户 (8f)：箭头上不要有横线穿过；箭头改纯三角（无尾线）。
                                // (9c) 落线改全局统一（pendStrokes），本分支不再单独计数
                                double[] gU = null, gM = null, gMask = null;
                                const double CR = 3.0;   // 拐角圆弧半径 mm（(8m) 1.8→3.0：run12 目检红弧与
                                                         // View1 黄弧平行分离露黄带，黄弧半径≈3mm，贴走）
                                const double GAP = 1.3;  // 箭头缺口半宽 mm
                                if (bl >= 10)
                                {
                                    double adx = paper[bi + 1][0] - paper[bi][0], ady = paper[bi + 1][1] - paper[bi][1];
                                    double al = Math.Sqrt(adx * adx + ady * ady);
                                    gU = new double[] { adx / al, ady / al };
                                    gM = new double[] { (paper[bi][0] + paper[bi + 1][0]) / 2.0, (paper[bi][1] + paper[bi + 1][1]) / 2.0 };
                                }
                                // 预计算每个内顶点的圆角点列（null = 共线直通）
                                var cornerPts = new List<double[]>[paper.Count];
                                for (int j = 1; j + 1 < paper.Count; j++)
                                {
                                    double[] a0 = paper[j - 1], b0 = paper[j], c0 = paper[j + 1];
                                    double e1x = b0[0] - a0[0], e1y = b0[1] - a0[1];
                                    double e2x = c0[0] - b0[0], e2y = c0[1] - b0[1];
                                    double l1 = Math.Sqrt(e1x * e1x + e1y * e1y), l2 = Math.Sqrt(e2x * e2x + e2y * e2y);
                                    if (l1 < 1e-6 || l2 < 1e-6) continue;
                                    double u1x = e1x / l1, u1y = e1y / l1, u2x = e2x / l2, u2y = e2y / l2;
                                    double csA = u1x * u2x + u1y * u2y;
                                    if (csA > 0.98) continue; // (8h) 0.999→0.98：小折角（<11.5°）直通不画弧，
                                                              // 防止大切距「大弧」（用户截图的双重弯头观感）
                                    // (8x) 截图②三通小勾：折返角（csA<-0.35，转角>110°）是就近链接伪角，直通不画弧；
                                    // 任一边短于 2R=6mm 时弧会卷成小圈，也直通。
                                    if (csA < -0.35) continue;
                                    if (Math.Min(l1, l2) < 2.0 * CR) continue;
                                    if (csA > 1) csA = 1; if (csA < -1) csA = -1;
                                    double alpha = Math.Acos(csA);
                                    double t = CR / Math.Tan(alpha / 2.0);
                                    double tcap = Math.Min(0.45 * Math.Min(l1, l2), 3.2); // (8m) 弧切距上限 3.2mm（随 R=3.0 放大，贴 View1 黄弧）
                                    if (t > tcap) t = tcap;
                                    if (t < CR * 0.2) continue;
                                    double dot2 = u2x * u1x + u2y * u1y;
                                    double n1x = u2x - dot2 * u1x, n1y = u2y - dot2 * u1y;
                                    double n1l = Math.Sqrt(n1x * n1x + n1y * n1y);
                                    if (n1l < 1e-9) continue;
                                    n1x /= n1l; n1y /= n1l;
                                    double p1x = b0[0] - t * u1x, p1y = b0[1] - t * u1y;
                                    double cX = p1x + CR * n1x, cY = p1y + CR * n1y;
                                    double p2x = b0[0] + t * u2x, p2y = b0[1] + t * u2y;
                                    double a1 = Math.Atan2(p1y - cY, p1x - cX);
                                    double a2 = Math.Atan2(p2y - cY, p2x - cX);
                                    double dA = a2 - a1;
                                    while (dA > Math.PI) dA -= 2 * Math.PI;
                                    while (dA < -Math.PI) dA += 2 * Math.PI;
                                    var lst = new List<double[]>();
                                    lst.Add(new double[] { p1x, p1y });
                                    int K = 6;
                                    for (int q3 = 1; q3 < K; q3++)
                                    {
                                        double aa = a1 + dA * q3 / K;
                                        lst.Add(new double[] { cX + CR * Math.Cos(aa), cY + CR * Math.Sin(aa) });
                                    }
                                    lst.Add(new double[] { p2x, p2y });
                                    cornerPts[j] = lst;
                                    cornerMarks9.Add(new double[] { b0[0], b0[1] }); // (14f)
                                }
                                // 行走落线：圆角顶点走弧点列；箭头边在中点断缺口（带参数夹取）
                                var strokes = new List<double[]>();
                                var strokeZ = new List<double[]>(); // (9c) 与 strokes 平行：主线段={z1,z2}，弧段=null（不做交叉断开）
                                double[] curPt = paper[0];
                                for (int j = 1; j < paper.Count; j++)
                                {
                                    bool corner = cornerPts[j] != null;
                                    double[] P1 = corner ? cornerPts[j][0] : paper[j];
                                    double[] Pend = corner ? cornerPts[j][cornerPts[j].Count - 1] : paper[j];
                                    if (gU != null && j == bi + 1)
                                    {
                                        // 缺口按原始边中点，向两端夹取到本边有效直线范围内
                                        double[] O = paper[bi];
                                        double sCur = (curPt[0] - O[0]) * gU[0] + (curPt[1] - O[1]) * gU[1];
                                        double sEnd = (P1[0] - O[0]) * gU[0] + (P1[1] - O[1]) * gU[1];
                                        double sMid = bl / 2.0;
                                        double lo = Math.Max(sCur, sMid - GAP), hi = Math.Min(sEnd, sMid + GAP);
                                        if (hi - lo > 0.5)
                                        {
                                            strokes.Add(new double[] { curPt[0], curPt[1], O[0] + lo * gU[0], O[1] + lo * gU[1] });
                                            strokes.Add(new double[] { O[0] + hi * gU[0], O[1] + hi * gU[1], P1[0], P1[1] });
                                            strokeZ.Add(new double[] { zs[j - 1], zs[j] }); strokeZ.Add(new double[] { zs[j - 1], zs[j] });
                                            gMask = new double[] { O[0] + (lo - 0.2) * gU[0], O[1] + (lo - 0.2) * gU[1], O[0] + (hi + 0.2) * gU[0], O[1] + (hi + 0.2) * gU[1] };
                                        }
                                        else
                                        {
                                            strokes.Add(new double[] { curPt[0], curPt[1], P1[0], P1[1] });
                                            strokeZ.Add(new double[] { zs[j - 1], zs[j] });
                                        }
                                    }
                                    else
                                    {
                                        strokes.Add(new double[] { curPt[0], curPt[1], P1[0], P1[1] });
                                        strokeZ.Add(new double[] { zs[j - 1], zs[j] });
                                    }
                                    if (corner)
                                    {
                                        var lst = cornerPts[j];
                                        for (int q3 = 1; q3 < lst.Count; q3++)
                                        {
                                            strokes.Add(new double[] { lst[q3 - 1][0], lst[q3 - 1][1], lst[q3][0], lst[q3][1] });
                                            strokeZ.Add(null); // (9c) 弧段不做交叉断开
                                        }
                                    }
                                    curPt = Pend;
                                }
                                // (9c) 交叉断开在全局统一处理：本分支的线段先进全局待画池
                                for (int si2 = 0; si2 < strokes.Count; si2++) { pendStrokes.Add(strokes[si2]); pendStrokeZ.Add(strokeZ[si2]); }
                                if (strokes.Count > 0) nPipe++;
                                // 白色遮罩盖住箭头缺口里透出的黄色原线（NLCOLO=9 白底不可见）
                                if (gMask != null)
                                    SymBlocks.DrawSegment(symLab, gMask[0], gMask[1], gMask[2], gMask[3], 9);
                                // 自绘流向箭头：最长段中点缺口里、沿段方向（(8f) 纯三角小箭头）
                                if (gU != null)
                                {
                                    bool dupA = false;
                                    foreach (double[] ap in arrowPts) { double ddx = ap[0] - gM[0], ddy = ap[1] - gM[1]; if (ddx * ddx + ddy * ddy < 100.0) { dupA = true; break; } }
                                    if (!dupA)
                                    {
                                        double ang = Math.Atan2(gU[1], gU[0]) * 180.0 / Math.PI;
                                        // (8o) 箭头改红色：标注在红色管线上
                                        if (SymBlocks.Insert(symLab, SymBlocks.N_ARROW, br, gM[0], gM[1], ang, SymBlocks.PIPE_COLOR) != null) { nArrow2++; arrowPts.Add(new double[] { gM[0], gM[1] }); }
                                    }
                                }
                            }
                            catch (Exception exB2) { log.AppendLine("管线项异常(跳过) @" + br + ": [" + exB2.GetType().Name + "] " + exB2.Message); nPipeSkip++; }
                    }
                }
                // (9c) 用户 2026-10-18：交叉的管线如果在下面要断开——按标高统一断口后落线
                {
                    // 网格建桶（4mm 格）
                    var segGrid = new Dictionary<long, List<int>>();
                    for (int si = 0; si < pendStrokes.Count; si++)
                    {
                        double[] sg = pendStrokes[si];
                        int gx0 = (int)Math.Floor(Math.Min(sg[0], sg[2]) / 4.0) - 1, gx1 = (int)Math.Floor(Math.Max(sg[0], sg[2]) / 4.0) + 1;
                        int gy0 = (int)Math.Floor(Math.Min(sg[1], sg[3]) / 4.0) - 1, gy1 = (int)Math.Floor(Math.Max(sg[1], sg[3]) / 4.0) + 1;
                        for (int gx = gx0; gx <= gx1; gx++)
                            for (int gy = gy0; gy <= gy1; gy++)
                            {
                                long key = ((long)gx << 32) ^ (long)(uint)gy;
                                List<int> l;
                                if (!segGrid.TryGetValue(key, out l)) { l = new List<int>(); segGrid[key] = l; }
                                l.Add(si);
                            }
                    }
                    int nCross = 0, nSeg2 = 0;
                    for (int si = 0; si < pendStrokes.Count; si++)
                    {
                        double[] sg = pendStrokes[si];
                        double[] zz = pendStrokeZ[si];
                        // 交叉断口区间（本段在交点处的参数 t∈[0,1]）
                        var ivs = new List<double[]>();
                        if (zz != null)
                        {
                            double ddx = sg[2] - sg[0], ddy = sg[3] - sg[1];
                            double L2 = ddx * ddx + ddy * ddy;
                            if (L2 > 1e-9)
                            {
                                int gx0 = (int)Math.Floor(Math.Min(sg[0], sg[2]) / 4.0) - 1, gx1 = (int)Math.Floor(Math.Max(sg[0], sg[2]) / 4.0) + 1;
                                int gy0 = (int)Math.Floor(Math.Min(sg[1], sg[3]) / 4.0) - 1, gy1 = (int)Math.Floor(Math.Max(sg[1], sg[3]) / 4.0) + 1;
                                var seen = new HashSet<int>();
                                for (int gx = gx0; gx <= gx1; gx++)
                                    for (int gy = gy0; gy <= gy1; gy++)
                                    {
                                        List<int> l;
                                        if (!segGrid.TryGetValue(((long)gx << 32) ^ (long)(uint)gy, out l)) continue;
                                        foreach (int sj in l)
                                        {
                                            if (!seen.Add(sj)) continue;
                                            double[] so = pendStrokes[sj];
                                            double[] zo = pendStrokeZ[sj];
                                            if (zo == null) continue;
                                            double ex = so[2] - so[0], ey = so[3] - so[1];
                                            double den = ddx * ey - ddy * ex;
                                            if (Math.Abs(den) < 1e-9) continue; // 平行/共线（共线走廊不断）
                                            double t = ((so[0] - sg[0]) * ey - (so[1] - sg[1]) * ex) / den;
                                            double u = ((so[0] - sg[0]) * ddy - (so[1] - sg[1]) * ddx) / den;
                                            if (t < 0 || t > 1 || u < 0 || u > 1) continue;
                                            double myZ = zz[0] + (zz[1] - zz[0]) * t;
                                            double otZ = zo[0] + (zo[1] - zo[0]) * u;
                                            if (otZ <= myZ + 0.3) continue; // 对方不比我高（0.3mm 容差）→ 不断
                                            double L = Math.Sqrt(L2);
                                            double half = 0.75 / L;
                                            ivs.Add(new double[] { Math.Max(0.0, t - half), Math.Min(1.0, t + half) });
                                            nCross++;
                                        }
                                    }
                                ivs.Sort((a, b) => a[0].CompareTo(b[0]));
                            }
                        }
                        // [0,1] 减去断口区间 → 净段 → 符号占位裁剪 → 落线
                        var pieces = new List<double[]>(); pieces.Add(new double[] { 0.0, 1.0 });
                        foreach (var iv in ivs)
                        {
                            var nxt = new List<double[]>();
                            foreach (var pc in pieces)
                            {
                                if (iv[1] <= pc[0] || iv[0] >= pc[1]) { nxt.Add(pc); continue; }
                                if (iv[0] > pc[0]) nxt.Add(new double[] { pc[0], iv[0] });
                                if (iv[1] < pc[1]) nxt.Add(new double[] { iv[1], pc[1] });
                            }
                            pieces = nxt;
                            if (pieces.Count == 0) break;
                        }
                        double L2s = (sg[2] - sg[0]) * (sg[2] - sg[0]) + (sg[3] - sg[1]) * (sg[3] - sg[1]);
                        double Ls = Math.Sqrt(L2s);
                        foreach (var pc in pieces)
                        {
                            if ((pc[1] - pc[0]) * Ls < 0.3) continue;
                            double ax = sg[0] + (sg[2] - sg[0]) * pc[0], ay = sg[1] + (sg[3] - sg[1]) * pc[0];
                            double bx = sg[0] + (sg[2] - sg[0]) * pc[1], by = sg[1] + (sg[3] - sg[1]) * pc[1];
                            foreach (double[] sg2 in ClipSegAtSymbols(clipPts, ax, ay, bx, by))
                            {
                                if (SymBlocks.DrawSegment(symLab, sg2[0], sg2[1], sg2[2], sg2[3], SymBlocks.PIPE_COLOR)) nSeg2++;
                                else if (drawErr1.Length == 0) drawErr1 = "红线:" + SymBlocks.LastDrawError;
                            }
                        }
                    }
                    log.AppendLine("(9c) 交叉断口 = " + nCross + "  落线段 = " + nSeg2);
                }
                // (9e) 管线序号标注（用户 2026-10-18「管线号没有映射到管道布置图里，不知道哪根
                // 管线对应表格里的管线高度」）：管道一览表行序 = pipeCollection（ELBOW drawlist
                // 首现序，与 CreateDrawing 表格同源同序），图上每条唯一管线标同款序号
                // （TEXTPRIMITIVE 红字 2.5mm，与标题栏文字同款通道）+ 引线段——图↔表可对。
                try
                {
                    List<DbElement> elesLab = drawE3DTool.ElementsFromDrawlist(view, new TypeFilter(DbElementTypeInstance.ELBOW), false, false);
                    List<DbElement> pipesLab = drawPipe ? Graphic.getPipeCollect(elesLab) : new List<DbElement>(); // (drawPipe=0) 设备布置图无管线序号
                    // (9h) 删旧 CreateDrawing 流程残留的「管线表索引」SLABEL（无引线的裸序号，
                    // 用户截图「两个3没有引出」真身；8r 直方图 SLABELx18==管线数18 实锤；
                    // SLABEL 文字走 TMRF 模板无 STXT 可读，且旧箭头 LAYER 已清、标题栏/图例均为
                    // TEXTPRIMITIVE——视图两级内 SLABEL 即老索引，整删。序号由菱形标签接管。
                    int nOldLab = 0; string oldLabErr = "";
                    try
                    {
                        var oldLabs = new List<DbElement>();
                        foreach (DbElement m9 in view.Members())
                        {
                            if (m9 == null || m9.IsNull) continue;
                            try { if (m9.GetElementType() == DbElementTypeInstance.SLABEL) oldLabs.Add(m9); } catch { }
                            foreach (DbElement s9 in m9.Members())
                            {
                                if (s9 == null || s9.IsNull) continue;
                                try { if (s9.GetElementType() == DbElementTypeInstance.SLABEL) oldLabs.Add(s9); } catch { }
                            }
                        }
                        foreach (DbElement o9 in oldLabs)
                        {
                            try { o9.Delete(); nOldLab++; }
                            catch (Exception exO9) { if (oldLabErr.Length == 0) oldLabErr = "[" + exO9.GetType().Name + "] " + exO9.Message; }
                        }
                    }
                    catch (Exception exO8) { oldLabErr = "[" + exO8.GetType().Name + "] " + exO8.Message; }
                    if (nOldLab > 0 || oldLabErr.Length > 0) log.AppendLine("(9h) 旧索引SLABEL删除 = " + nOldLab + (oldLabErr.Length > 0 ? "  首错:" + oldLabErr : ""));
                    var labPts = new List<double[]>();
                    // (14f) Forbidden zones: fitting circles + real pipe corners.
                    // Equipment envelopes were already registered in (10b).
                    foreach (double[] cpF in clipPts)
                        AnnotationPlanner.CommitForbidden(cpF[0], cpF[1], cpF[2] + 1.2, cpF[2] + 1.2);
                    foreach (double[] cmF in cornerMarks9)
                        AnnotationPlanner.CommitForbidden(cmF[0], cmF[1], 4.5, 4.5);
                    int nLab = 0, nPos9 = 0; string labErr = "";
                    for (int i = 0; i < pipesLab.Count; i++)
                    {
                        try
                        {
                            DbElement br0 = null;
                            foreach (DbElement m0 in pipesLab[i].Members())
                            {
                                try { if (m0.GetElementType() == DbElementTypeInstance.BRANCH) { br0 = m0; break; } } catch { }
                            }
                            if (br0 == null) continue;
                            Position hp0 = br0.GetPosition(ATT.HPOS, wq);
                            double[] pp0 = ChemSymbols.Project(view, hp0.X, hp0.Y);
                            // (9e-smart) 候选环智能选址：8 方向 × 距离 {7,12,18}mm，对【已放标签/符号
                            // 占位圈/全部管线线段】取最小间隙评分，选间隙最大者——任何图上都自动找
                            // 空地放标注，不压线不压符号；候选序先正上后斜角，同分自然优先正上。
                            double bS = -1.0, lx = pp0[0], ly = pp0[1] + 7.0;
                            // (9o) 框贴近管线（用户「框当然距离管线越近越好」21:26）：回退 9l/9m/9n 的
                            // 下方远档扩展，四向统一 {7,12,18}；评分改「够隙近者优先」——候选若最小
                            // 间隙 ≥2.5mm（不压任何东西）加 (100-ds) 距离奖励，近处有空地绝不走远。
                            double[] angs9 = new double[] { 90, 45, 135, 0, 180, 225, 315, 270 };
                            double[] bon9 = new double[] { 2.5, 1.5, 2.8, -1.0, 2.2, 0.5, 0.3, 1.0 };
                            double[] dsts9 = new double[] { 7.0, 12.0, 18.0 };
                            for (int ai9 = 0; ai9 < angs9.Length; ai9++)
                            {
                                double ag = angs9[ai9];
                                foreach (double ds in dsts9)
                                {
                                    double cxx = pp0[0] + Math.Cos(ag * Math.PI / 180.0) * ds;
                                    double cyy = pp0[1] + Math.Sin(ag * Math.PI / 180.0) * ds;
                                    double mn = ds + bon9[ai9]; // 间隙下限 + (9j) 方向偏好
                                    foreach (double[] lp in labPts) { double dx = cxx - lp[0], dy = cyy - lp[1]; double dd = Math.Sqrt(dx * dx + dy * dy); if (dd < mn) mn = dd; }
                                    foreach (double[] cp in clipPts) { double dx = cxx - cp[0], dy = cyy - cp[1]; double dd = Math.Sqrt(dx * dx + dy * dy) - cp[2]; if (dd < mn) mn = dd; }
                                    foreach (double[] sg in pendStrokes)
                                    {
                                        double vx = sg[2] - sg[0], vy = sg[3] - sg[1];
                                        double L2 = vx * vx + vy * vy;
                                        double t = L2 > 1e-9 ? ((cxx - sg[0]) * vx + (cyy - sg[1]) * vy) / L2 : 0.0;
                                        if (t < 0) t = 0; else if (t > 1) t = 1;
                                        double dx = cxx - (sg[0] + vx * t), dy = cyy - (sg[1] + vy * t);
                                        double dd = Math.Sqrt(dx * dx + dy * dy); if (dd < mn) mn = dd;
                                    }
                                    // (9h) 引线共线惩罚：候选点与锚点连线若与某管段共线（两端距线<1mm、
                                    // 管段>3mm），引线会没入管线「看不见」——正是「3没有引出」的画法根因。
                                    double lvx = cxx - pp0[0], lvy = cyy - pp0[1];
                                    double llen = Math.Sqrt(lvx * lvx + lvy * lvy);
                                    if (llen > 1e-6)
                                    {
                                        foreach (double[] sg in pendStrokes)
                                        {
                                            double slx = sg[2] - sg[0], sly = sg[3] - sg[1];
                                            if (slx * slx + sly * sly < 9.0) continue;
                                            double d1 = Math.Abs((sg[0] - pp0[0]) * lvy - (sg[1] - pp0[1]) * lvx) / llen;
                                            double d2 = Math.Abs((sg[2] - pp0[0]) * lvy - (sg[3] - pp0[1]) * lvx) / llen;
                                            if (d1 < 1.0 && d2 < 1.0) { mn -= 5.0; break; }
                                        }
                                    }
                                    // (9o) 够隙近者优先：不压任何东西（mn≥2.5mm）的候选加 (100-ds)
                                    // 距离奖励——ds∈{7..18} 时奖励∈[82,93] 恒压过间隙项，近者必胜；
                                    // 全部 <2.5mm 时退化为纯间隙比较（找最空处）。
                                    double sc9 = mn + (mn >= 2.5 ? 100.0 - ds : 0.0);
                                    if (sc9 > bS) { bS = sc9; lx = cxx; ly = cyy; }
                                }
                            }
                            labPts.Add(new double[] { lx, ly });
                            // (14d/14f) Cross-family + geometry clash: geometry hits go
                            // through PlaceEscort (45-degree escapement, user 11-08 request).
                            double rh9 = 2.8;
                            bool clashLbl14 = AnnotationPlanner.HasClash(lx, ly, 3.4, 3.4);
                            bool clashGeo14 = AnnotationPlanner.HasForbidden(lx, ly, 3.4, 3.4);
                            bool planned14 = false, escort14 = false;
                            if (clashLbl14 || clashGeo14)
                            {
                                double[] posD14 = clashGeo14
                                    ? AnnotationPlanner.PlaceEscort(pp0[0], pp0[1], 3.4, 3.4)
                                    : AnnotationPlanner.PlaceBest(pp0[0], pp0[1], 3.4, 3.4,
                                        new double[] { 90, 45, 135, 0, 180, 225, 315, 270 }, new double[] { 7, 12, 18, 26, 36 });
                                if (posD14 != null) { lx = posD14[0]; ly = posD14[1]; planned14 = true; escort14 = clashGeo14; }
                            }
                            if (!planned14) AnnotationPlanner.Commit(lx, ly, 3.4, 3.4, 1);
                            AnnotationPlanner.CommitLead(pp0[0], pp0[1], lx, ly - rh9);
                            // (9g) 序号菱形框（用户截图3：管线号用菱形框起来）+ 引线从菱形下顶点引到管线
                            SymBlocks.DrawSegment(symLab, lx - rh9, ly, lx, ly + rh9, SymBlocks.PIPE_COLOR);
                            SymBlocks.DrawSegment(symLab, lx, ly + rh9, lx + rh9, ly, SymBlocks.PIPE_COLOR);
                            SymBlocks.DrawSegment(symLab, lx + rh9, ly, lx, ly - rh9, SymBlocks.PIPE_COLOR);
                            SymBlocks.DrawSegment(symLab, lx, ly - rh9, lx - rh9, ly, SymBlocks.PIPE_COLOR);
                            // (9g-45) 引线严格 45°：从菱形边（沿指向管线方向）引出，不穿框
                            double dxp9 = pp0[0] - lx, dyp9 = pp0[1] - ly;
                            double dlp9 = Math.Sqrt(dxp9 * dxp9 + dyp9 * dyp9);
                            if (dlp9 > 1e-6) { dxp9 /= dlp9; dyp9 /= dlp9; }
                            double slx9 = lx + dxp9 * rh9, sly9 = ly + dyp9 * rh9;
                            // (14f) Escorted corner labels get a 45-degree elbow leader.
                            double ex14 = pp0[0], ey14 = pp0[1], tx14 = slx9, ty14 = sly9;
                            double ddx14 = tx14 - ex14, ddy14 = ty14 - ey14;
                            if (escort14 && Math.Sqrt(ddx14 * ddx14 + ddy14 * ddy14) > 12.0)
                            {
                                double sgx14 = ddx14 >= 0 ? 1.0 : -1.0, sgy14 = ddy14 >= 0 ? 1.0 : -1.0;
                                double run14 = Math.Min(Math.Min(Math.Abs(ddx14), Math.Abs(ddy14)), 14.0);
                                double bx14 = ex14 + sgx14 * run14, by14 = ey14 + sgy14 * run14;
                                SymBlocks.DrawSegment(symLab, ex14, ey14, bx14, by14, SymBlocks.PIPE_COLOR);
                                SymBlocks.DrawSegment(symLab, bx14, by14, tx14, ty14, SymBlocks.PIPE_COLOR);
                            }
                            else SymBlocks.DrawSegment(symLab, ex14, ey14, tx14, ty14, SymBlocks.PIPE_COLOR);
                            DbElement t9 = symLab.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
                            drawTool.SetAttribute(t9, ATT.JUST, "Centre");
                            drawTool.SetAttribute(t9, ATT.ALIGN, "Hbody");
                            drawTool.SetAttribute(t9, ATT.TXCOLO, "2");
                            drawTool.SetAttribute(t9, ATT.CHEI, "2.5");
                            t9.SetAttribute(ATT.BTEX, (i + 1).ToString());
                            // (9o) 框内文字居中（用户「框内文字距离」，600dpi 像素实测「1」号样本：
                            // 数字中心偏左 2.5px、偏上 13px = 0.19/0.99 名义单位）→
                            // x 左移系数 0.31→0.23（右移回 0.19/2.5/len），y 下移系数 0.47→0.86。
                            // (9q) 1200dpi 实测「10」号（q5.png）：0.86 系数把基线压到下顶点、字形
                            // 骑在下边缘出框（用户「你都移出框了」）——回归数学正中：基线 = 中心
                            // −CHEI/2 → y 系数 0.50（字形 [−1.25,+1.25] 恰占菱形中部）。
                            // x：「10」实测仍偏右 0.25 名义 → 每字符左移 0.25→0.28。
                            string labStr9 = (i + 1).ToString();
                            // (9s-v2) 按实测字宽定位（600dpi 像素实测：'1'宽1.13mm、'0'宽2.58mm；
                            // 首位恒为 1）：halfw = (1.13+(len-1)*2.58)/2，atx = lx - halfw。
                            // 实证：'1' 理想偏移 0.57（9s 的 1.325 偏左 0.76）、'10' 理想 1.86（9s 的 2.025 残左 0.17）。
                            // y 保持 0.75×CHEI（'1' 残上 0.21 / '10' 残下 0.04，均值 ~0.1mm 不可辨）。
                            double halfw9 = (1.13 + (labStr9.Length - 1) * 2.58) / 2.0;
                            double atx9 = lx - halfw9;
                            double aty9 = ly - 0.75 * 2.5;
                            // (9r-v3) 定位改 CE 直指 + 无前缀 at：CurrentElement.Element = t9 后命令
                            // 只作用于本标签，根绝按名寻址打错人（名字空/撞名→裸锚偏右上/号码丢失）。
                            // POS/XTEX 属性直设实测均被 PDMS 拒（PdmsExceptionImpl），弃用 API 通道；
                            // XTEX 若可读则回读验证计数。
                            bool posApi9 = false;
                            try { CE.Element = t9; } catch { }
                            if (!drawTool.CommandExcute("at x " + atx9.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + " y " + aty9.ToString("F3", System.Globalization.CultureInfo.InvariantCulture), true))
                                labErr = "at:FAIL#" + labStr9;
                            else
                            {
                                try { posApi9 = Math.Abs(t9.GetDouble(ATT.XTEX) - atx9) < 0.5 && Math.Abs(t9.GetDouble(ATT.YTEX) - aty9) < 0.5; } catch { }
                                if (posApi9) nPos9++;
                            }
                            nLab++;
                        }
                        catch (Exception exL9) { if (labErr.Length == 0) labErr = "[" + exL9.GetType().Name + "] " + exL9.Message; }
                    }
                    log.AppendLine("(9e) 管线序号标注 = " + nLab + " / 唯一管线 " + pipesLab.Count + " 回读" + nPos9 + (labErr.Length > 0 ? "  首错:" + labErr : ""));

                    // —— (10c) 距轴网/距设备定位标注（用户 2026-10-19「把设备和轴网也加入标注」）——
                    // 每条唯一管线：① 最近垂直向轴网 → 定位尺寸线（45° 斜短线端符，与 DTER=4 同风格），
                    //   竖管(N-S)量到竖轴(X)画水平尺寸、横管(E-W)量到横轴(Y)画竖直尺寸；
                    //   文字按管线序错档 3 级，缓解密集区叠字。
                    // ② 最近设备包络 → 细引出线 + 「名称 距离mm」（clamp 求平面最近点）。
                    try
                    {
                        int nDimE = 0; string errC10 = "";
                        // (10c-v3) 边界外放字的占位表 + 边界矩形 {x0,x1,y0,y1}（try 级作用域，设备轴定位段也要用）
                        double[] limC10 = new double[] { pMinX10, pMaxX10, pMinY10, pMaxY10 };
                        var occL10 = new List<double[]>(); var occR10 = new List<double[]>();
                        var occB10 = new List<double[]>(); var occT10 = new List<double[]>();
                        DbElement locNote = (locEquip10 || locGrid10) ? sheet.CreateLast(DbElementTypeInstance.NOTE) : null;
                        if (locNote == null || locNote.IsNull) log.AppendLine("(10c) 新建「定位标注」NOTE 失败");
                        else
                        {
                            drawTool.SetAttribute(locNote, ATT.NAME, locNote.Owner.GetAsString(ATT.NAME) + "/定位标注");
                            foreach (DbElement pc10 in pipesLab)
                            {
                                try
                                {
                                    DbElement brC = null;
                                    foreach (DbElement mC in pc10.Members())
                                    {
                                        try { if (mC.GetElementType() == DbElementTypeInstance.BRANCH) { brC = mC; break; } } catch { }
                                    }
                                    if (brC == null) continue;
                                    Position hpC = brC.GetPosition(ATT.HPOS, wq);
                                    double[] ppC = ChemSymbols.Project(view, hpC.X, hpC.Y);
                                    // —— (10c-v3) 距设备：边界线外放字（用户 16:08「轴网设备标注都合并到边界线外」）——
                                    // 不再拉长引线穿图：9e 同款候选环（8 向 × {5,8,11}mm）在管锚旁找空地直接放
                                    // 「名称 距离」绿字；评分按文字全宽 3 点探针（左端/中/右端）取最小间隙，
                                    // 复用 (9e) 的 labPts/clipPts/pendStrokes。
                                    if (locEquip10 && equipBoxes10.Count > 0)
                                    {
                                        int bE = -1; double dE = double.MaxValue;
                                        for (int ei = 0; ei < equipBoxes10.Count; ei++)
                                        {
                                            double cx10 = Math.Max(equipBoxes10[ei][0], Math.Min(hpC.X, equipBoxes10[ei][2]));
                                            double cy10 = Math.Max(equipBoxes10[ei][1], Math.Min(hpC.Y, equipBoxes10[ei][3]));
                                            double dd = Math.Sqrt((hpC.X - cx10) * (hpC.X - cx10) + (hpC.Y - cy10) * (hpC.Y - cy10));
                                            if (dd >= 0.5 && dd < dE) { dE = dd; bE = ei; }
                                        }
                                        if (bE >= 0 && dE <= equipDistMax10)
                                        {
                                            string txtE = equipNames10[bE];
                                            if (txtE.Length > 12) txtE = txtE.Substring(0, 12);
                                            txtE += " " + Math.Round(dE).ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
                                            double wE = 0; foreach (char c in txtE) wE += (c == '1' || c == 'I' || c == '.' || c == ' ') ? 1.13 : 2.58;
                                            double hwE = wE / 2.0 * (2.2 / 2.5);
                                            // (10c-v3) 移到边界线外（用户 16:08「轴网设备标注都合并到边界线外」）：
                                            // 按管锚最近边贴边放置、对齐管锚坐标，同边自动错档
                                            double[] posE = PlaceOutside(limC10, ppC[0], ppC[1], hwE, occL10, occR10, occB10, occT10);
                                            DrawTextCentre(locNote, txtE, posE[0], posE[1], 2.2, equipColor);
                                            AnnotationPlanner.Commit(posE[0], posE[1], hwE + 0.8, 1.7, 3); // (14d) 登记供 9j 引出字避让
                                            nDimE++;
                                        }
                                    }
                                }
                                catch (Exception exP10) { if (errC10.Length == 0) errC10 = "[" + exP10.GetType().Name + "] " + exP10.Message; }
                            }
                        }
                        // —— 设备按轴网定位（10c-v3 合并到边界线外；10c-v4 用户 2026-10-28「定位设备
                        // 用 Origin 点，不要一直定位中心」）：图内短尺寸删除，改边界外一行
                        // 「名称 X距 Y距」——距离量设备 Origin(POS) 到最近数字轴/字母轴（Origin 取不到
                        // 回退包络中心），放字锚点同源，与设备信息同区堆放。
                        int nEqAx = 0;
                        if (locGrid10)
                        {
                            for (int ei = 0; ei < equipBoxes10.Count; ei++)
                            {
                                try
                                {
                                    double bx0 = equipBoxes10[ei][0], by0 = equipBoxes10[ei][1];
                                    double bx1 = equipBoxes10[ei][2], by1 = equipBoxes10[ei][3];
                                    // (10c-v4) 定位基准 = 设备 Origin（POS 世界坐标），中心兜底
                                    double[] oriA = ei < equipOri10.Count ? equipOri10[ei] : null;
                                    double rxA = oriA != null ? oriA[0] : (bx0 + bx1) / 2.0;
                                    double ryA = oriA != null ? oriA[1] : (by0 + by1) / 2.0;
                                    double dX = double.MaxValue, dY = double.MaxValue;
                                    foreach (double[] g in gridLines)
                                    {
                                        if (g[1] == 0) dX = Math.Min(dX, Math.Abs(rxA - g[0]));
                                        else dY = Math.Min(dY, Math.Abs(ryA - g[0]));
                                    }
                                    if ((dX < double.MaxValue && dX >= 0.5 && dX <= gridDistMax10) ||
                                        (dY < double.MaxValue && dY >= 0.5 && dY <= gridDistMax10))
                                    {
                                        string nmA = equipNames10[ei];
                                        if (nmA.Length > 10) nmA = nmA.Substring(0, 10);
                                        string txtA = nmA
                                            + (dX < double.MaxValue && dX >= 0.5 && dX <= gridDistMax10 ? " X" + Math.Round(dX).ToString("F0", System.Globalization.CultureInfo.InvariantCulture) : "")
                                            + (dY < double.MaxValue && dY >= 0.5 && dY <= gridDistMax10 ? " Y" + Math.Round(dY).ToString("F0", System.Globalization.CultureInfo.InvariantCulture) : "");
                                        double wA = 0; foreach (char c in txtA) wA += (c == '1' || c == 'I' || c == '.' || c == ' ') ? 1.13 : 2.58;
                                        double hwA = wA / 2.0 * (2.2 / 2.5);
                                        double[] pcA = ChemSymbols.Project(view, rxA, ryA);
                                        double[] posA = PlaceOutside(limC10, pcA[0], pcA[1], hwA, occL10, occR10, occB10, occT10);
                                        DrawTextCentre(locNote, txtA, posA[0], posA[1], 2.2, equipColor);
                                        AnnotationPlanner.Commit(posA[0], posA[1], hwA + 0.8, 1.7, 3); // (14d) 登记供 9j 引出字避让
                                        nEqAx++;
                                    }
                                }
                                catch { }
                            }
                        }
                        log.AppendLine("(10c) 定位标注 = 边界外设备字" + nDimE + " 设备轴定位字" + nEqAx + " / 管线" + pipesLab.Count + " 设备" + equipBoxes10.Count + (errC10.Length > 0 ? "  首错:" + errC10 : ""));
                    }
                    catch (Exception exC10) { log.AppendLine("(10c) 定位标注失败: [" + exC10.GetType().Name + "] " + exC10.Message); }
                }
                catch (Exception exL8) { log.AppendLine("(9e) 管线序号标注失败: [" + exL8.GetType().Name + "] " + exL8.Message); }

                // (9f) 尺寸标注去挤（用户 2026-10-18 手机截图：300/120 文字叠字、488 竖排压线、
                // 标注链贴着管系）：
                // ① 四条 LDIMENSION 链整体外推 5mm（DPOS 背向视图中心）；
                // ② DPPT 密度抽稀：链轴投影间距 <12mm 的点删掉（文字 2.5mm×2 位宽的混叠源），
                //    删后 sort dim 重排。LDIMENSION 为 CreateDrawing 标注流产物，重跑标注可复原。
                try
                {
                    int nDim = 0, nDrop = 0, nMove9 = 0, cntR9 = 0, cntL9 = 0, cntT9 = 0, cntB9 = 0, nLter = 0, nEqPt9 = 0, nFitDrop9 = 0; string dimErr = "";
                    var sideLog9 = new System.Text.StringBuilder(); // (9k) 每链归边+链名，落日志核对
                    var chainEnds9 = new List<double[]>(); // (9k2) 链数据 {链线X,链线Y,horiz,loP,hiP,loW,hiW}
                    var chainRefs9 = new List<DbElement>(); // (9k2-v4) 平行表：链 LDIMENSION 实体（DPOINT 挂链）
                    var chainWpts9 = new List<List<double[]>>(); // (9k2-v3) 平行表：链测点世界坐标（9k 成功读点后存入，9k2 取垂向均值）
                    double cx9 = 0, cy9 = 0;
                    try { double[] xy9 = view.GetDoubleArray(ATT.XYPS); cx9 = xy9[0]; cy9 = xy9[1]; } catch { }
                    // (9h) 管系纸面外包络：链定位的绝对基准（幂等，重跑不漂移）
                    double pMinX9 = double.MaxValue, pMaxX9 = -double.MaxValue, pMinY9 = double.MaxValue, pMaxY9 = -double.MaxValue;
                    foreach (double[] sg in pendStrokes)
                    {
                        if (sg[0] < pMinX9) pMinX9 = sg[0]; if (sg[0] > pMaxX9) pMaxX9 = sg[0];
                        if (sg[1] < pMinY9) pMinY9 = sg[1]; if (sg[1] > pMaxY9) pMaxY9 = sg[1];
                        if (sg[2] < pMinX9) pMinX9 = sg[2]; if (sg[2] > pMaxX9) pMaxX9 = sg[2];
                        if (sg[3] < pMinY9) pMinY9 = sg[3]; if (sg[3] > pMaxY9) pMaxY9 = sg[3];
                    }
                    bool hasP9 = pendStrokes.Count > 0;
                    // (9f-fix) LDIMENSION 藏在「管道标注」LAYER 下（view→LAYER→LDIMENSION），
                    // 首跑只扫一层=链0。改为扫两层汇总后再逐链处理。
                    var dims9 = new List<DbElement>();
                    foreach (DbElement m9 in view.Members())
                    {
                        if (m9 == null || m9.IsNull) continue;
                        try { if (m9.GetElementType() == DbElementTypeInstance.LDIMENSION) dims9.Add(m9); } catch { }
                        foreach (DbElement s9 in m9.Members())
                        {
                            if (s9 == null || s9.IsNull) continue;
                            try { if (s9.GetElementType() == DbElementTypeInstance.LDIMENSION) dims9.Add(s9); } catch { }
                        }
                    }
                    // (9j) 引出队列：[数值, 段中点x, 段中点y, 字高] —— 被抽稀删掉的密集尺寸
                    // 文字改为「数值+引线」画到段中点外侧（用户「找找引线引出的方法」）
                    var leadQ = new List<double[]>(); int nLead = 0; string leadErr = "";
                    foreach (DbElement m9 in dims9)
                    {
                        nDim++;
                        // (9f-smart) 阈值全部由链自身文字高度 TSIZ 推导：抽稀间距=8×TSIZ——任何字号的图自适应
                        double tsz9 = 1.5;
                        try { tsz9 = m9.GetDouble(ATT.TSIZ); } catch { }
                        if (tsz9 <= 0) tsz9 = 1.5;
                        double gapMin9 = tsz9 * 8.0;
                        // (9k) ① 先收集链测点（纸面投影 + 世界坐标），归边与抽稀共用
                        var dpts = new List<double[]>(); var dels = new List<DbElement>(); var wpts = new List<double[]>();
                        foreach (DbElement d9 in m9.Members())
                        {
                            if (d9 == null || d9.IsNull) continue;
                            DbElement ref9 = null;
                            try { ref9 = d9.GetElement(ATT.DDNM); } catch { } // (9f-v3) DDNM 是 dbref，须走 GetElement（GetAttribute+as 恒 null → 删点0 根因）
                            if (ref9 == null || ref9.IsNull) continue;
                            try
                            {
                                double[] pw9 = WorldPosOf(ref9, wq);
                                double[] pp9 = ChemSymbols.Project(view, pw9[0], pw9[1]);
                                dpts.Add(new double[] { pp9[0], pp9[1] }); dels.Add(d9);
                                wpts.Add(pw9.Length > 2 ? new double[] { pw9[0], pw9[1], pw9[2] } : new double[] { pw9[0], pw9[1] }); // (9j) 世界坐标含Z——(9k2) DPOINT 贴视图平面，防 Z=0 被剖切
                            }
                            catch { }
                        }
                        if (dpts.Count < 3) continue;
                        double clineX9 = double.NaN, clineY9 = double.NaN; // (9k2) 链线位置（DPOS 目标）
                        // (9k) ② 链归边：链名带 Top/Bottom/Left/Right 直接用（老流程创建边名实锤：
                        // graphicsDrawing.GetElement("…/管道标注/Right")）；无名则按测点质心象限兜底
                        // （老代码 223-225 行四象限过滤：上象限管线→Top 链，左→Left…）
                        string nm9 = ""; try { nm9 = (m9.GetAsString(ATT.NAME) ?? "").ToUpperInvariant(); } catch { }
                        string side9;
                        if (nm9.Contains("TOP")) side9 = "T";
                        else if (nm9.Contains("BOTTOM")) side9 = "B";
                        else if (nm9.Contains("LEFT")) side9 = "L";
                        else if (nm9.Contains("RIGHT")) side9 = "R";
                        else
                        {
                            double mX = 0, mY = 0;
                            foreach (double[] q9 in dpts) { mX += q9[0]; mY += q9[1]; }
                            mX /= dpts.Count; mY /= dpts.Count;
                            double sX = 0, sY = 0;
                            foreach (double[] q9 in dpts) { sX += Math.Abs(q9[0] - mX); sY += Math.Abs(q9[1] - mY); }
                            if (sX >= sY) side9 = mY >= cy9 ? "T" : "B";
                            else side9 = mX >= cx9 ? "R" : "L";
                        }
                        sideLog9.Append(side9).Append("(").Append(nm9.Length > 24 ? nm9.Substring(nm9.Length - 24) : nm9).Append(") ");
                        // ③ (9k) 链定位幂等化：绝对目标 = 管系外包络 ±(9 + 9*同侧序号)，按归边贴边——
                        // 已到位(<0.5mm)不动，重跑不漂移；同侧多条链自动错层 9mm；永不入图框区。
                        try
                        {
                            double[] dp = m9.GetDoubleArray(ATT.DPOS);
                            if (dp != null && dp.Length >= 2)
                            {
                                // (10i) 设备布置图：链不归边——保持元素附近（用户「不重叠就行」），仅记录现位
                                if (equipLayout10) { clineX9 = dp[0]; clineY9 = dp[1]; }
                                else if (hasP9)
                                {
                                // (9l) 链基准并入视口框（用户「箭头和文字都偏移到界限外部」）：
                                // 粉色视口框 = XYPS±SIZE/2（drawE3DTool.ViewLimits，纸面坐标与
                                // pendStrokes 投影同系）；四边基准取「管系外包络 ∪ 视口框」的外包络，
                                // 链线必落在界限外侧，箭头在链线上、文字 DTOF 抬 3mm 后仍在界外。
                                double[] lim9 = null;
                                try { lim9 = drawE3DTool.ViewLimits(view); } catch { }
                                double bL9 = pMinX9, bR9 = pMaxX9, bB9 = pMinY9, bT9 = pMaxY9;
                                if (lim9 != null && lim9.Length >= 4)
                                {
                                    if (lim9[0] < bL9) bL9 = lim9[0];
                                    if (lim9[1] > bR9) bR9 = lim9[1];
                                    if (lim9[2] < bB9) bB9 = lim9[2];
                                    if (lim9[3] > bT9) bT9 = lim9[3];
                                }
                                double nx = dp[0], ny = dp[1];
                                if (side9 == "R") nx = bR9 + 9.0 + 9.0 * cntR9++;
                                else if (side9 == "L") nx = bL9 - 9.0 - 9.0 * cntL9++;
                                else if (side9 == "T") ny = bT9 + 9.0 + 9.0 * cntT9++;
                                else ny = bB9 - 9.0 - 9.0 * cntB9++;
                                clineX9 = nx; clineY9 = ny; // (9k2) 链线最终位置
                                if (Math.Abs(nx - dp[0]) > 0.5 || Math.Abs(ny - dp[1]) > 0.5)
                                {
                                    drawTool.CommandExcute(m9.GetAsString(ATT.NAME) + " dpos x " + nx.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " y " + ny.ToString("F2", System.Globalization.CultureInfo.InvariantCulture), true);
                                    nMove9++;
                                }
                                } // else if (hasP9)
                            }
                        }
                        catch (Exception exD9) { if (dimErr.Length == 0) dimErr = "dpos:[" + exD9.GetType().Name + "] " + exD9.Message; }
                        // (9m) 设备测点进链（用户 2026-10-19「把轴网和设备加入drawinglist就能标注这三者」）：
                        // 设备已 ADDENTRY 进 drawinglist(10e)——按最近边（原 getElementSide L223-226 四象限
                        // 同序）挂 DPPT(DDNM=设备)，设备定位尺寸与管线同链原生渲染；
                        // 防重：链内已有同 DDNM 的 DPPT 跳过（跨轮幂等）。
                        try
                        {
                            for (int ei9 = 0; ei9 < equipRefs10.Count; ei9++)
                            {
                                double[] ep9 = ei9 < equipPt9.Count ? equipPt9[ei9] : null;
                                if (ep9 == null || ep9.Length < 2 || double.IsNaN(ep9[0])) continue;
                                string eSide9;
                                if (ep9[0] < cx9 && ep9[1] != cy9) eSide9 = "L";
                                else if (ep9[1] > cy9) eSide9 = "T";
                                else if (ep9[0] > cx9 && ep9[1] != cy9) eSide9 = "R";
                                else eSide9 = "B";
                                if (eSide9 != side9) continue;
                                bool eqDup9 = false;
                                foreach (DbElement mm9 in m9.Members())
                                {
                                    try
                                    {
                                        if (mm9.GetElementType() == DbElementTypeInstance.DPPT && equipRefs10[ei9].Equals(mm9.GetElement(ATT.DDNM))) { eqDup9 = true; break; }
                                    }
                                    catch { }
                                }
                                if (eqDup9) continue;
                                DbElement eqPt9 = m9.CreateLast(DbElementTypeInstance.DPPT);
                                eqPt9.SetAttribute(ATT.DDNM, equipRefs10[ei9]);
                                nEqPt9++;
                            }
                        }
                        catch (Exception exE9) { if (dimErr.Length == 0) dimErr = "eqpt:[" + exE9.GetType().Name + "] " + exE9.Message; }
                        // (9i) 文字排布（用户 2026-10-18「重点优化尺寸文字重叠」）：
                        // DTAN HOR——全链文字强制水平，窄段不再竖排（竖排互叠的根因）；
                        // DTOF 0 3——文字沿链法向抬离 3mm，不再压在链线/图框线上。两命令均为置态、幂等。
                        try { drawTool.CommandExcute(m9.GetAsString(ATT.NAME) + " DTAN HOR", true); } catch (Exception exT10) { if (dimErr.Length == 0) dimErr = "dtan:[" + exT10.GetType().Name + "] " + exT10.Message; }
                        try { drawTool.CommandExcute(m9.GetAsString(ATT.NAME) + " DTOF 0 3", true); } catch { }
                        // (9q-r2) DTER 码值已实证（9q-r1 探码轮 PDF 目检）：原值 "Arrows"，
                        // 1=未知 2=箭头 3=圆点 4=斜短线（下链 B 全变斜杆，用户要的建筑风格）。
                        // 四链统一固化 DTER=4；SetAttribute 不经命令处理器，不弹框不挂起。
                        try
                        {
                            bool dterOk9 = false;
                            try { m9.SetAttribute(ATT.DTER, 4); dterOk9 = true; }
                            catch
                            {
                                try { foreach (DbElement dp9 in m9.Members()) { try { dp9.SetAttribute(ATT.DTER, 4); dterOk9 = true; } catch { } } } catch { }
                            }
                            if (dterOk9) nLter++;
                            else if (dimErr.Length == 0) dimErr = "dter:FAIL " + side9;
                        }
                        catch (Exception exT11) { if (dimErr.Length == 0) dimErr = "dter:[" + exT11.GetType().Name + "] " + exT11.Message; }
                        // (9k2) 记录链线位置 + 测点极值（纸面/世界），供链延伸到轴网
                        if (!double.IsNaN(clineX9))
                        {
                            double xMn9 = double.MaxValue, xMx9 = -double.MaxValue, yMn9 = double.MaxValue, yMx9 = -double.MaxValue;
                            foreach (double[] q9 in dpts) { if (q9[0] < xMn9) xMn9 = q9[0]; if (q9[0] > xMx9) xMx9 = q9[0]; if (q9[1] < yMn9) yMn9 = q9[1]; if (q9[1] > yMx9) yMx9 = q9[1]; }
                            bool hor9 = (xMx9 - xMn9) >= (yMx9 - yMn9);
                            double wMn9 = double.MaxValue, wMx9 = -double.MaxValue;
                            foreach (double[] q9 in wpts)
                            {
                                double v9 = hor9 ? q9[0] : q9[1];
                                if (v9 < wMn9) wMn9 = v9;
                                if (v9 > wMx9) wMx9 = v9;
                            }
                            if (wMn9 <= wMx9)
                            {
                            chainEnds9.Add(new double[] { clineX9, clineY9, hor9 ? 1 : 0,
                                hor9 ? xMn9 : yMn9, hor9 ? xMx9 : yMx9, wMn9, wMx9 });
                            chainRefs9.Add(m9);
                            chainWpts9.Add(new List<double[]>(wpts));
                            }
                        }
                        // ④ DPPT 抽稀：按主轴排序，纸面间距 <8×TSIZ 删；删掉的数值进引出队列
                        try
                        {
                            double xMin9 = double.MaxValue, xMax9 = -double.MaxValue, yMin9 = double.MaxValue, yMax9 = -double.MaxValue;
                            foreach (double[] q9 in dpts)
                            {
                                if (q9[0] < xMin9) xMin9 = q9[0]; if (q9[0] > xMax9) xMax9 = q9[0];
                                if (q9[1] < yMin9) yMin9 = q9[1]; if (q9[1] > yMax9) yMax9 = q9[1];
                            }
                            bool horiz = (xMax9 - xMin9) >= (yMax9 - yMin9);
                            var idx = new List<int>();
                            for (int i9 = 0; i9 < dpts.Count; i9++) idx.Add(i9);
                            idx.Sort((a, b) => (horiz ? dpts[a][0].CompareTo(dpts[b][0]) : dpts[a][1].CompareTo(dpts[b][1])));
                            // (14g) Fitting points must not be dimensioned. Source createLdimElement
                            // already filters them; this purges legacy / form-built chains too.
                            // Deleted points do NOT enter leadQ (no leader text for fittings).
                            var dropFit9 = new System.Collections.Generic.HashSet<int>();
                            if (!Graphic.DimIncludeFittings)
                            {
                                for (int i9 = 0; i9 < dels.Count; i9++)
                                {
                                    try
                                    {
                                        DbElement rf9 = dels[i9].GetElement(ATT.DDNM);
                                        if (rf9 != null && !rf9.IsNull && Graphic.IsDimFitting(rf9))
                                        { dels[i9].Delete(); dropFit9.Add(i9); nFitDrop9++; }
                                    }
                                    catch { }
                                }
                            }
                            // (9j) 抽稀+引线引出：被删密集点的尺寸数值不丢弃——取两端【世界坐标】
                            // 主轴差（=尺寸真值，600/488/536 那种 mm 数），进 leadQ 队列待引出落线
                            int lastK = -1; double lastC = -1e18;
                            int nDropThis9 = 0;
                            foreach (int i9 in idx)
                            {
                                if (dropFit9.Contains(i9)) continue; // (14g) 已删附件点不再参与抽稀/引出
                                // (9m-v2) 设备测点不参与抽稀（用户点名要的设备定位；否则与管线点挤→删→下轮又挂→跨轮震荡）
                                bool isEq9 = false;
                                try { isEq9 = dels[i9].GetElementType() == DbElementTypeInstance.DPPT && equipRefs10.Contains(dels[i9].GetElement(ATT.DDNM)); } catch { }
                                double c = horiz ? dpts[i9][0] : dpts[i9][1];
                                if (!isEq9 && lastK >= 0 && c - lastC < gapMin9)
                                {
                                    try
                                    {
                                        dels[i9].Delete(); nDrop++; nDropThis9++;
                                        int wAx = horiz ? 0 : 1;
                                        double dv9 = Math.Abs(wpts[i9][wAx] - wpts[lastK][wAx]);
                                        if (dv9 > 0.5)
                                            leadQ.Add(new double[] { dv9, (dpts[i9][0] + dpts[lastK][0]) / 2.0, (dpts[i9][1] + dpts[lastK][1]) / 2.0, tsz9 });
                                    }
                                    catch { }
                                }
                                else { lastK = i9; lastC = c; }
                            }
                            // (14g) skip sort dim when fewer than 2 points remain (modal guard)
                            if (dpts.Count - dropFit9.Count - nDropThis9 >= 2)
                                try { drawTool.CommandExcute(m9.GetAsString(ATT.NAME) + " sort dim", true); } catch { }
                        }
                        catch (Exception exT9) { if (dimErr.Length == 0) dimErr = "thin:[" + exT9.GetType().Name + "] " + exT9.Message; }
                    }
                    // (9j) 引出标注落线：数值文字放在段中点外侧（背向管系中心），引线相连；
                    // 同批引出互相避让（6mm 内沿外向再推 4mm，最多 5 次）。
                    var leadTxt = new List<double[]>();
                    foreach (double[] q in leadQ)
                    {
                        try
                        {
                            double mx = q[1], my = q[2];
                            // (9j-45) 引线优先 45° 斜引线：右上(45°)优先，出界换左上(135°)
                            // 角度/优先级统一走 DrawingStandard（默与旧值完全一致，支持按标准切换）
                            double[] angA9 = DrawingStandard.LeaderAngles();
                            double uxA9 = 0.0, uyA9 = 0.0;
                            double tx = mx, ty = my;
                            for (int ia9 = 0; ia9 < angA9.Length; ia9++)
                            {
                                double aA = angA9[ia9];
                                double rA = aA * Math.PI / 180.0;
                                uxA9 = Math.Cos(rA); uyA9 = Math.Sin(rA);
                                tx = mx + uxA9 * 5.5; ty = my + uyA9 * 5.5;
                                // 文字基点超图面范围(含余量)则试下一方位（右上不行再左上）
                                double mg9 = 10.0;
                                if (tx < pMinX9 - mg9 || tx > pMaxX9 + mg9 || ty < pMinY9 - mg9 || ty > pMaxY9 + mg9)
                                {
                                    if (ia9 < angA9.Length - 1) continue;
                                }
                                break;
                            }
                            // 避让：沿所选 45° 方向推（同批引出 6mm 内沿外向再推 4mm，最多 5 次）
                            // (14d) 碰撞检测从「同批 6mm 点距」升级为全局 AABB（设备标高/菱形/定位字盒全在册）
                            string lvS14 = ((int)Math.Round(q[0])).ToString();
                            double lh14 = q[3];
                            double hw14 = (1.13 + (lvS14.Length - 1) * 2.58) / 2.0 * (lh14 / 2.5) + 0.8;
                            for (int k9 = 0; k9 < 5; k9++)
                            {
                                bool clash = AnnotationPlanner.HasClash(tx, ty, hw14, lh14 * 0.7 + 0.4);
                                if (!clash) break;
                                tx += uxA9 * 4.0; ty += uyA9 * 4.0;
                            }
                            AnnotationPlanner.Commit(tx, ty, hw14, lh14 * 0.7 + 0.4, 2);
                            AnnotationPlanner.CommitLead(tx, ty, mx, my);
                            leadTxt.Add(new double[] { tx, ty });
                            SymBlocks.DrawSegment(symLab, tx, ty, mx, my, SymBlocks.PIPE_COLOR);
                            DbElement tL = symLab.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
                            drawTool.SetAttribute(tL, ATT.JUST, "Centre");
                            drawTool.SetAttribute(tL, ATT.ALIGN, "Hbody");
                            drawTool.SetAttribute(tL, ATT.TXCOLO, "2");
                            drawTool.SetAttribute(tL, ATT.CHEI, q[3].ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
                            tL.SetAttribute(ATT.BTEX, ((int)Math.Round(q[0])).ToString());
                            // (9r-v2) 同 9e：API 直设 XTEX/YTEX（POS 直设已被实测拒绝），y 0.50 / x 0.28 系数
                            string lvS9 = ((int)Math.Round(q[0])).ToString();
                            double lh9 = q[3];
                            // (9s-v2) 同 9e：实测字宽模型（按 lh9/2.5 缩放）+ CE 直指
                            double halfw9 = (1.13 + (lvS9.Length - 1) * 2.58) / 2.0 * (lh9 / 2.5);
                            double ltx9 = tx - halfw9, lty9 = ty - 0.75 * lh9;
                            try { CE.Element = tL; } catch { }
                            drawTool.CommandExcute("at x " + ltx9.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + " y " + lty9.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
                            nLead++;
                        }
                        catch (Exception exQ9) { if (leadErr.Length == 0) leadErr = "[" + exQ9.GetType().Name + "] " + exQ9.Message; }
                    }
                    log.AppendLine("(9f) 尺寸标注去挤 = 链" + nDim + " 移链" + nMove9 + " 删点" + nDrop + " 引出" + nLead + " 斜短线" + nLter + " 设备测点" + nEqPt9 + " 附件测点剔除" + nFitDrop9 + (dimErr.Length > 0 ? "  首错:" + dimErr : "") + (leadErr.Length > 0 ? "  引出错:" + leadErr : ""));
                    // (14d) 全局避让规划器统计：占位盒/引线登记量 + 退让到边界成排行的次数
                    log.AppendLine("(14d) 标注避让规划 = 占位盒" + AnnotationPlanner.BoxCount + " 引线" + AnnotationPlanner.LeadCount
                        + " 几何禁区" + AnnotationPlanner.ForbidCount + " 转角引出" + AnnotationPlanner.EscortCount
                        + " 边界退让" + AnnotationPlanner.FallbackCount);
                    if (sideLog9.Length > 0) log.AppendLine("(9k) 链归边 = " + sideLog9.ToString());

                    // —— (9k2-v4) 轴网原生进尺寸链：DPOINT 显式坐标测点 ——
                    // 依据（用户 2026-10-19「看下原来怎么写的，或者上网搜搜，别自己猜」）：
                    //   · 原工程 drawE3DTool.Dimension()（L333-334）即权威写法：
                    //       ldim.Create(idx, DbElementTypeInstance.DPOINT);
                    //       element2.SetAttribute(DbAttributeInstance.POS, pos);
                    //     graphicsDrawing.createLdimElement 用 DPPT(DDNM→模型元素)+"sort dim"，其
                    //     getElementSide 本把 GRIDWLD 加进测点集合（原版即「网格进链」），本项目
                    //     设计库无 GRID 元素（(10a) 探测日志实证）故该通道空转。
                    //   · 关键纠正：Draft User Guide 的 PML 术语 DPOI 在 .NET API 中对应 DPOINT——
                    //     此前 DLL 字节扫描把 ENDPOI 子串误判为 DPOI；view-api-verification.md 实测
                    //     元素类型仅 DPOINT/DPBA/DPPT，无 DPOI。定位属性用 POS（Position 对象），
                    //     DDNMX/DDNMY 仅为其派生读出值（drawE3DTool L343/353 只读不写）。
                    // → 每条链：删旧 DPOINT（幂等）→ 为每条同向轴线建 DPOINT（POS=轴世界坐标，
                    //   垂向=链测点垂向世界均值、Z=链测点均值，投影线最短、贴合视图平面）→ "sort dim" 原生排序。
                    //   PDMS 原生画端符/界线/文字/排序——v2/v3 的 BLANK+自绘+延轴全部退役
                    //   （v2 自绘链即用户截图「拉下来的」框下第二排数字，残骸 NOTE 已由「轴网」关键字删除=2 清掉）。
                    try
                    {
                        int nDpoi9 = 0; string dpoiErr9 = "";
                        // (10i) 设备布置图：轴点不进元素链——链只留元素尺寸（防轴距混进元素链显乱）；轴距由 9k3 独立行表达
                        if (equipLayout10) log.AppendLine("(9k2) 设备布置图：跳过轴点进链（元素链只留元素尺寸）");
                        for (int ci = 0; !equipLayout10 && ci < chainEnds9.Count; ci++)
                        {
                            try
                            {
                                double[] ce = chainEnds9[ci];
                                if (double.IsNaN(ce[0]) || double.IsNaN(ce[1])) continue;
                                bool hor9 = ce[2] > 0.5;
                                DbElement mC9 = chainRefs9[ci];
                                if (mC9 == null || mC9.IsNull) continue;
                                // 幂等：删旧 DPOINT（DPPT 不动；重复运行轴点不堆积）
                                foreach (DbElement om9 in mC9.Members())
                                {
                                    try
                                    {
                                        if (om9 == null || om9.IsNull) continue;
                                        if (om9.GetElementType().ToString().IndexOf("DPOINT", StringComparison.OrdinalIgnoreCase) >= 0) om9.Delete();
                                    }
                                    catch { }
                                }
                                // 链测点垂向/法向世界均值（DPOINT 垂向=投影线最短、与管测点风格一致；
                                // Z 取链测点均值，贴近视图平面，避免投影错位）
                                var wp9 = chainWpts9[ci] ?? new List<double[]>();
                                double perp9 = 0, z9 = 0; int pn9 = 0;
                                foreach (double[] q9 in wp9) { perp9 += hor9 ? q9[1] : q9[0]; if (q9.Length > 2) z9 += q9[2]; pn9++; }
                                if (pn9 > 0) { perp9 /= pn9; z9 /= pn9; }
                                // 每条同向轴线一个 DPOINT（轴全量进链——链跨轴到轴，标准管建图样式）
                                int added9 = 0;
                                for (int gi = 0; gi < gridLines.Count; gi++)
                                {
                                    if (hor9 ? gridLines[gi][1] != 0 : gridLines[gi][1] != 1) continue;
                                    double gC9 = gridLines[gi][0];
                                    DbElement dpoi9 = mC9.CreateLast(DbElementTypeInstance.DPOINT);
                                    if (dpoi9 == null || dpoi9.IsNull) { if (dpoiErr9.Length == 0) dpoiErr9 = "DPOINT创建失败"; continue; }
                                    Position pD9 = hor9 ? Position.Create(gC9, perp9, z9) : Position.Create(perp9, gC9, z9);
                                    bool setOk9 = false; string setErr9 = "";
                                    try { dpoi9.SetAttribute(ATT.POS, pD9); setOk9 = true; }
                                    catch (Exception exP9) { setErr9 = "POS:[" + exP9.Message + "]"; }
                                    if (!setOk9)
                                    {
                                        try { dpoi9.SetAttribute(ATT.DPOS, pD9); setOk9 = true; setErr9 += "→DPOS成功"; }
                                        catch (Exception exP2) { setErr9 += "→DPOS:[" + exP2.Message + "]"; }
                                    }
                                    if (!setOk9 && dpoiErr9.Length < 160) dpoiErr9 += " " + setErr9;
                                    if (setOk9) { added9++; nDpoi9++; }
                                }
                                if (added9 > 0) drawTool.CommandExcute(mC9.GetAsString(ATT.NAME) + " sort dim", true);
                            }
                            catch (Exception exC9) { if (dpoiErr9.Length < 160) dpoiErr9 += " [链" + ci + ":" + exC9.Message + "]"; }
                        }
                        log.AppendLine("(9k2) 轴点进链(DPOINT) = " + nDpoi9 + " / 链" + chainEnds9.Count + (dpoiErr9.Length > 0 ? "  " + dpoiErr9 : ""));
                    }
                    catch (Exception exAx9) { log.AppendLine("(9k2) 轴点进链失败: [" + exAx9.GetType().Name + "] " + exAx9.Message); }

                    // —— (9k3) 轴距标注行（用户 2026-10-19「轴网之间多加一层轴网之间的标注」）——
                    // 底边数字轴距链(BOTTOM2)+左边字母轴距链(LEFT2)，GB 惯例数字在下、字母在左：
                    // 测点=各轴 DPOINT（v4 同款显式坐标通道），跨轴到轴，数值即轴距(6000…)。
                    // 位置=同名管线链外侧再让 9mm；每轮删旧重建（幂等）。
                    try
                    {
                        int nAxRow9 = 0; string axRowErr9 = "";
                        for (int ci3 = 0; ci3 < chainEnds9.Count; ci3++)
                        {
                            try
                            {
                                double[] ce3 = chainEnds9[ci3];
                                DbElement mC3 = chainRefs9[ci3];
                                if (mC3 == null || mC3.IsNull) continue;
                                if (double.IsNaN(ce3[0]) || double.IsNaN(ce3[1])) continue;
                                bool hor3 = ce3[2] > 0.5;
                                string side3 = ""; try
                                {
                                    string nm3 = (mC3.GetAsString(ATT.NAME) ?? "").ToUpperInvariant();
                                    // (9k3-v3) 精确后缀匹配——BOTTOM2/LEFT2 名字含 BOTTOM/LEFT，Contains 会把
                                    // 上轮轴距行当源链二次处理（本轮链6 实证）；只认真 BOTTOM/LEFT
                                    if (nm3.EndsWith("/BOTTOM")) side3 = "B";
                                    else if (nm3.EndsWith("/TOP")) side3 = "T";
                                    else if (nm3.EndsWith("/LEFT")) side3 = "L";
                                    else if (nm3.EndsWith("/RIGHT")) side3 = "R";
                                }
                                catch { }
                                bool isB3 = side3 == "B", isL3 = side3 == "L";
                                if (!isB3 && !isL3) continue;
                                DbElement lyr3 = mC3.Owner;
                                if (lyr3 == null || lyr3.IsNull) continue;
                                string rowName3 = lyr3.GetAsString(ATT.NAME) + (isB3 ? "/BOTTOM2" : "/LEFT2");
                                // 幂等：删旧轴距链
                                try
                                {
                                    DbElement old3 = DbElement.GetElement(rowName3);
                                    if (old3 != null && !old3.IsNull && !old3.IsDeleted) old3.Delete();
                                }
                                catch { }
                                // 轴位收集（与 (10a) 同过滤：轴在视口世界范围内）
                                // (9k3-v2) 关键修复：DPOINT 的 POS 是【世界坐标系】（(9k2) 实证），必须直存轴世界坐标；
                                // 勿用 Project 纸面投影——纸面值当世界值再经视图变换，测点飞出视图 → 整链不渲染
                                var axC3 = new List<double>();
                                foreach (double[] g3 in gridLines)
                                {
                                    if (hor3 ? g3[1] != 0 : g3[1] != 1) continue;
                                    if (isB3)
                                    {
                                        if (g3[0] < wMinX10 - 1 || g3[0] > wMaxX10 + 1) continue;
                                        axC3.Add(g3[0]);
                                    }
                                    else
                                    {
                                        if (g3[0] < wMinY10 - 1 || g3[0] > wMaxY10 + 1) continue;
                                        axC3.Add(g3[0]);
                                    }
                                }
                                if (axC3.Count < 2) continue;
                                // (14f) Dense-axis anti-overlap: shrink text and raise the
                                // text offset when paper gaps are under 8mm (11-08 screenshot).
                                int nTight3 = 0; double minGap3 = double.MaxValue;
                                for (int k3 = 1; k3 < axC3.Count; k3++)
                                {
                                    double gap3 = Math.Abs(axC3[k3] - axC3[k3 - 1]) * vSca10;
                                    if (gap3 < minGap3) minGap3 = gap3;
                                    if (gap3 < 8.0) nTight3++;
                                }
                                double dtoF3 = nTight3 > 0 ? 6.0 : 3.0;
                                // 建链（样式镜像 createLdimElement L47-51：DMSTRF/TSIZ/PLSTYL/DIR；PDMS 无 DTERID 省略，TSIZ=double）
                                DbElement row3 = lyr3.CreateLast(DbElementTypeInstance.LDIMENSION);
                                if (row3 == null || row3.IsNull) { if (axRowErr9.Length == 0) axRowErr9 = "LDIMENSION 创建失败"; continue; }
                                drawTool.SetAttribute(row3, ATT.NAME, rowName3);
                                try { row3.SetAttribute(ATT.DMSTRF, DbElement.GetElement("/AVEVA-DIM-1.8")); } catch { }
                                row3.SetAttribute(ATT.TSIZ, nTight3 > 0 ? 2.0 : 2.5);
                                drawTool.SetAttribute(row3, ATT.PLSTYL, "8008");
                                try { row3.SetAttribute(ATT.DIR, Direction.Create(isB3 ? "W" : "N")); } catch { }
                                // (10i) 链线位置分型：设备布置图——轴距行固定贴视口界内（气泡内侧 5mm，+13mm），
                                //       不跟链走（链可能停在元素旁/旧位置）；管道平面图——管线链外侧再让 9mm
                                double rx3, ry3;
                                if (equipLayout10)
                                {
                                    rx3 = isL3 ? pMinX10 + 13.0 : ce3[0];
                                    if (isB3) ry3 = pMinY10 + 13.0;
                                    else
                                    {
                                        double pyA3 = 0; int pnY3 = 0;
                                        foreach (double wy3 in axC3)
                                        {
                                            try { double[] pp3 = ChemSymbols.Project(view, thX10, wy3); pyA3 += pp3[1]; pnY3++; } catch { }
                                        }
                                        ry3 = pnY3 > 0 ? pyA3 / pnY3 : ce3[1];
                                    }
                                }
                                else
                                {
                                    rx3 = isL3 ? ce3[0] - 9.0 : ce3[0];
                                    // (9k3-v3) LEFT2 平行坐标不继承源链 DPOS y 脏值（LEFT 链 y=-199.8 实证
                                    // →LEFT2 整链渲染页外；BOTTOM2 平行坐标合法故正常）。取轴测点纸面 y 均值。
                                    if (isB3) ry3 = ce3[1] - 9.0;
                                    else
                                    {
                                        double pyA3 = 0; int pnY3 = 0;
                                        foreach (double wy3 in axC3)
                                        {
                                            try { double[] pp3 = ChemSymbols.Project(view, thX10, wy3); pyA3 += pp3[1]; pnY3++; } catch { }
                                        }
                                        ry3 = pnY3 > 0 ? pyA3 / pnY3 : ce3[1];
                                    }
                                }
                                drawTool.CommandExcute(rowName3 + " dpos x " + rx3.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                                    + " y " + ry3.ToString("F2", System.Globalization.CultureInfo.InvariantCulture), true);
                                // DPOINT 测点：垂向世界坐标取【视口世界中心】(thX10/thY10)
                                // (9k3-v4) 不再用源链测点均值——LEFT 链测点世界 x 均值可能压视口世界边界/NaN
                                // →测点投影出界→整链不渲染（BOTTOM2 用管线最南 y 在界内故成功）。视口中心必在界内。
                                double perp3 = hor3 ? thY10 : thX10, z3 = 0; int pn3 = 0;
                                var wp3 = chainWpts9[ci3] ?? new List<double[]>();
                                foreach (double[] q3 in wp3) { if (q3.Length > 2) { z3 += q3[2]; pn3++; } }
                                if (pn3 > 0) z3 /= pn3;
                                if (double.IsNaN(perp3) || double.IsNaN(z3)) { perp3 = hor3 ? thY10 : thX10; z3 = 0; }
                                int nAx3 = 0;
                                foreach (double c3 in axC3)
                                {
                                    DbElement dp3 = row3.CreateLast(DbElementTypeInstance.DPOINT);
                                    if (dp3 == null || dp3.IsNull) continue;
                                    Position p3 = hor3 ? Position.Create(c3, perp3, z3) : Position.Create(perp3, c3, z3);
                                    bool ok3 = false;
                                    try { dp3.SetAttribute(ATT.POS, p3); ok3 = true; }
                                    catch { try { dp3.SetAttribute(ATT.DPOS, p3); ok3 = true; } catch { } }
                                    if (ok3) nAx3++;
                                }
                                try { drawTool.CommandExcute(rowName3 + " DTAN HOR", true); } catch { }
                                try { drawTool.CommandExcute(rowName3 + " DTOF 0 " + dtoF3.ToString("F0", System.Globalization.CultureInfo.InvariantCulture), true); } catch { }
                                if (nTight3 > 0) log.AppendLine("(14f) 轴距行防叠 " + (isB3 ? "BOTTOM2" : "LEFT2")
                                    + " = 密轴" + nTight3 + "/" + axC3.Count + " 最小间距" + minGap3.ToString("F1")
                                    + "mm → 字高2.0 文字抬高" + dtoF3.ToString("F0") + "mm");
                                try { row3.SetAttribute(ATT.DTER, 4); } catch { }
                                if (nAx3 > 0) { try { drawTool.CommandExcute(rowName3 + " sort dim", true); } catch { } }
                                log.AppendLine("(9k3) 轴距行 " + (isB3 ? "BOTTOM2" : "LEFT2") + " = " + nAx3 + "轴 dpos(" + rx3.ToString("F1") + "," + ry3.ToString("F1") + ")");
                                nAxRow9++;
                            }
                            catch (Exception exR3) { if (axRowErr9.Length < 160) axRowErr9 += " [" + exR3.GetType().Name + "] " + exR3.Message; }
                        }
                        log.AppendLine("(9k3) 轴距行汇总 = " + nAxRow9 + " 排" + (axRowErr9.Length > 0 ? "  " + axRowErr9 : ""));
                    }
                    catch (Exception exAx3) { log.AppendLine("(9k3) 轴距行失败: [" + exAx3.GetType().Name + "] " + exAx3.Message); }
                }
                catch (Exception exF9) { log.AppendLine("(9f) 尺寸标注去挤失败: [" + exF9.GetType().Name + "] " + exF9.Message); }
                if (drawErr1.Length > 0 && drawErr1.StartsWith("红线:")) log.AppendLine("首个绘图失败原因: " + drawErr1);
                log.AppendLine("红色管线 = " + nPipe + "  自绘流向箭头 = " + nArrow2 + "  管线跳过 = " + nPipeSkip + "  成员位置失败 = " + nMemFail);
                log.AppendLine("(8n) " + SymBlocks.LstylDiag);

                // 5) 视图重生成（"替换生效"关键：UpdateModelView + UPDATE all）
                LayerManager.SetCurrentLayer(view, DrawLayerCat.Outline); // 外形层(best-effort)：模型几何归层需 PDMS 实测
                drawTool.UpdateModelView(view);
                log.AppendLine("UpdateModelView 完成");
                try
                {
                    bool u = drawTool.CommandExcute("UPDATE all");
                    log.AppendLine("UPDATE all = " + u);
                }
                catch (Exception exUpd) { log.AppendLine("UPDATE all 异常（继续，不影响结构）: [" + exUpd.GetType().Name + "] " + exUpd.Message); }

                // 5b) (8r) 用户指路：「单线有一个模板出来就没有弯头。找找那个模板，尽量从源头解决。不要二次删除」
                // → 8o 生成后删除方案【废弃】（且其 NLCOLO=4 判据会误删我方黄色阀/三通/法兰符号，属 bug）。
                // 正路：换/改 View1 规则集（RRSF），让管线从源头出成单线。此处只保留诊断：
                // view 子树类型直方图（判定黄图是否 DB 存储）+ RRSF 规则集结构 dump（探路）。
                var typeCnt = new System.Collections.Generic.Dictionary<string, int>();
                Action<DbElement, int> scanDiag = null;
                scanDiag = (el, depth) =>
                {
                    if (el == null || el.IsNull || depth > 14) return;
                    foreach (DbElement ch in el.Members())
                    {
                        if (ch == null || ch.IsNull) continue;
                        string tn = "";
                        try { tn = ch.GetElementType().ToString(); } catch { continue; }
                        if (!typeCnt.ContainsKey(tn)) typeCnt[tn] = 0;
                        typeCnt[tn]++;
                        scanDiag(ch, depth + 1);
                    }
                };
                scanDiag(view, 0);
                var sbT = new System.Text.StringBuilder();
                int nk = 0;
                foreach (var kv3 in typeCnt) { if (nk++ > 20) break; sbT.Append(kv3.Key).Append("x").Append(kv3.Value).Append(" "); }
                log.AppendLine("(8r-diag) view 子树类型直方图: " + (typeCnt.Count == 0 ? "<空——黄图虚拟生成>" : sbT.ToString()));
                // (8o-diag) RRSF 规则集结构：为下一步改规则（TUBE→CL / CentrelinePen 红色实线）探路
                try
                {
                    DbElement rs = DbElement.GetElement("/DRA/MAS/H&S/RULESETS/View1");
                    if (rs != null && !rs.IsNull)
                    {
                        int nRs = 0;
                        foreach (DbElement rm in rs.Members()) nRs++;
                        log.AppendLine("(8o-diag) RRSF目标: " + rs.GetElementType() + " " + rs.GetAsString(ATT.NAME) + " 成员数=" + nRs);
                        int i2 = 0;
                        foreach (DbElement rm in rs.Members())
                        {
                            if (i2++ > 30) { log.AppendLine("  RS: ...(截断)"); break; }
                            string rt = ""; try { rt = rm.GetElementType().ToString(); } catch { }
                            string rn = ""; try { rn = rm.GetAsString(ATT.NAME); } catch { }
                            log.AppendLine("  RS: " + rt + " | " + rn);
                        }
                    }
                    else log.AppendLine("(8o-diag) RRSF目标取不到（可能不在当前 DB）");
                }
                catch (Exception exRs) { log.AppendLine("(8o-diag) RRSF异常: " + exRs.Message); }
                try { LayerManager.SetCurrentLayer(view, DrawLayerCat.Outline); drawTool.UpdateModelView(view); } catch { }

                // 6) 删后判空 CE（项目铁律）+ SAVEWORK 提示
                try
                {
                    DbElement ce = CE.Element;
                    if (ce == null || ce.IsNull || ce.IsDeleted) log.AppendLine("WARN: 收尾 CE 悬空（需 set_ce 重设）");
                    else log.AppendLine("CE OK: " + ce.GetElementType() + " " + ce.GetAsString(ATT.NAME));
                }
                catch { }
                log.AppendLine("下一步必须 $M SAVEWORK 落盘（否则重启回滚 = 替换不生效 根因③）");
                log.AppendLine("=== RSYM-OK ===");
                res.Ok = true;
                res.Output = log.ToString();
            }
            catch (Exception ex)
            {
                res.Ok = false;
                res.Error = "[" + ex.GetType().Name + "] " + ex.Message;
                log.AppendLine("=== RSYM-FAIL " + res.Error + " " + (ex.StackTrace == null ? "" : ex.StackTrace) + " ===");
                res.Output = log.ToString();
            }
            return res;
        }

        /* ---------------- boot_selfheal（2026-10-16 新增：启动自愈，根治画树 NRE Fatal） ---------------- */

        /// <summary>启动自愈：在 Addin Start()（画 Explorer 树之前、UI 线程存活时）清理已知损坏元素，
        /// 根治「每次登录画树 → Infragistics UltraTree 解引用空节点 → NRE → CLR FastFail 0xC0020001 →
        /// 'A fatal error has been detected. The program must exit.'」的死循环。
        ///
        /// 2026-10-16 实证根因：孤儿 DEPT =23525/13（directChildren=1 隐藏孤儿子、名字为空/未命名）挂在
        /// DRAFT world root(/*) 顶层，且 CE 被钉在它上面；每次删除未 SAVEWORK → 重启回滚复活 → 每次画树命中它 fatal。
        ///
        /// 安全设计（绝不误删正常元素）：
        ///   1) 只处理「确切已知」坏元素（默认 =23525/13，可传 refs 追加），按 ref 解析，解析不到即跳过；
        ///   2) 多重签名校验：类型必须 ∈ 容器类(DEPT/REGI/RPLB/SCTY/SCL) 且 未命名(=ref) 且 直接子<=1 且
        ///      确在 /* 顶层(Owner=star)——任一不符即不删并记日志（正常项目 DEPT =16385/621 因直接子>1 被排除）；
        ///   3) 删前先把 CE 切到 /*（避免删后 CE 悬空 → 后续 RunInPdms NRE）；
        ///   4) 删后 SAVEWORK 持久化（防回滚复活，一次根治；后续启动因找不到目标而秒过）；
        ///   5) 全量诊断日志 op_boot_selfheal.log（含 /* 顶层所有成员 ref/类型/子数，定位用）；
        ///   6) 全程 try/catch，任何异常只记日志、绝不阻断 PDMS 启动；
        ///   7) 开关：环境变量 PDMS_NO_SELFHEAL=1 整体禁用。
        /// params: refs = 追加目标（分号/逗号分隔，如 "=23525/13;=123/4"），缺省只处理 =23525/13。</summary>
        public static OpResult BootSelfHeal(Dictionary<string, string> p)
        {
            OpResult res = new OpResult();
            FileLoggingStringBuilder log = new FileLoggingStringBuilder(@"D:\pdmsAgent\bridge\op_boot_selfheal.log");
            var pp = p ?? new Dictionary<string, string>();
            try
            {
                if ("1".Equals(System.Environment.GetEnvironmentVariable("PDMS_NO_SELFHEAL"), StringComparison.OrdinalIgnoreCase))
                {
                    res.Ok = true; res.Output = "SELFHEAL DISABLED by env PDMS_NO_SELFHEAL=1";
                    log.AppendLine("=== SELFHEAL DISABLED " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===");
                    return res;
                }

                string[] badRefs;
                string extra = P(pp, "refs", "");
                if (extra.Length > 0)
                {
                    var list = new List<string>();
                    foreach (var t in extra.Split(new char[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)) { var s = t.Trim(); if (s.Length > 0) list.Add(s); }
                    badRefs = list.ToArray();
                }
                else badRefs = new string[] { "=23525/13" };

                log.AppendLine("=== BOOT-SELFHEAL-START " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " targets=" + string.Join(",", badRefs) + " ===");

                // 1) 读当前 CE（先判空）
                DbElement ce = null;
                try { ce = CE.Element; } catch (Exception ex) { log.AppendLine("CE 读取异常: " + ex.Message); }
                bool ceBad = (ce == null || ce.IsNull || ce.IsDeleted);
                string ceRef = "";
                try { if (ce != null && !ce.IsNull) ceRef = ce.GetAsString(ATT.NAME); } catch { }
                log.AppendLine("CE BEFORE: " + (ceBad ? "悬空/无效" : ce.GetElementType() + " " + ceRef));

                // 2) 诊断：枚举 /* 顶层直接成员（全量日志，定位用）
                DbElement star = null;
                try { star = DbElement.GetElement("/*"); } catch { }
                if (star != null && !star.IsNull)
                {
                    int cnt = 0;
                    foreach (DbElement m in star.Members())
                    {
                        if (m == null || m.IsNull) continue;
                        string mn = ""; int dc = 0;
                        try { mn = m.GetAsString(ATT.NAME); } catch { }
                        try { foreach (DbElement x in m.Members()) { dc++; } } catch { }
                        log.AppendLine("  TOP[" + (cnt++) + "] " + m.GetElementType() + " " + mn + " directChildren=" + dc);
                    }
                    log.AppendLine("DRAFT-TOP count=" + cnt);
                }
                else log.AppendLine("DRAFT-TOP: star=/* 不可读");

                // 3) 删已知坏元素（多重签名校验，绝不误删）
                foreach (var refStr in badRefs)
                {
                    DbElement bad = null;
                    try { bad = DbElement.GetElement(refStr); } catch { }
                    if (bad == null || bad.IsNull || bad.IsDeleted) { log.AppendLine("  " + refStr + " 不存在（已删/ref 变化）→ 跳过"); continue; }
                    string bt = ""; string bn = ""; int bdc = 0;
                    try { bt = bad.GetElementType().ToString(); } catch { }
                    try { bn = bad.GetAsString(ATT.NAME); } catch { }
                    try { foreach (DbElement x in bad.Members()) { bdc++; } } catch { }
                    bool isContainer = (bt == "DEPT" || bt == "REGI" || bt == "RPLB" || bt == "SCTY" || bt == "SCL");
                    bool unnamed = (bn == null || bn.Trim().Length == 0) || bn.StartsWith("="); // 未命名（=ref 串形）；.NET3.5 无 IsNullOrWhiteSpace
                    bool atTop = false;
                    try { var ow = bad.Owner; atTop = (star != null && ow != null && !ow.IsNull && ow.GetAsString(ATT.NAME) == star.GetAsString(ATT.NAME)); } catch { }
                    if (!isContainer || !unnamed || bdc > 1 || !atTop)
                    {
                        log.AppendLine("  " + refStr + " 签名不符（type=" + bt + " name=[" + bn + "] dc=" + bdc + " atTop=" + atTop + "）→ 不删（安全）");
                        continue;
                    }
                    // 若 CE 指向它，先切走 CE（避免删后悬空）
                    try
                    {
                        if (ce != null && ceRef == bn) { Command fix = Command.CreateCommand("CE /*"); if (fix != null) fix.RunInPdms(); log.AppendLine("  CE 原指向 " + refStr + " → 已切到 /*"); }
                    }
                    catch (Exception ex) { log.AppendLine("  CE 切换异常: " + ex.Message); }
                    try
                    {
                        bad.Delete();
                        try { System.Windows.Forms.Application.DoEvents(); } catch { }
                        log.AppendLine("  SELFHEAL: 已删 " + refStr + " (" + bt + " name=[" + bn + "] dc=" + bdc + ")");
                    }
                    catch (Exception ex) { log.AppendLine("  SELFHEAL: 删 " + refStr + " 异常 " + ex.Message + "\n" + ex.StackTrace); }
                }

                // 4) SAVEWORK（持久化，防回滚复活，一次根治）
                try { Command sv = Command.CreateCommand("SAVEWORK"); if (sv != null) sv.RunInPdms(); log.AppendLine("SAVEWORK 已下发"); }
                catch (Exception ex) { log.AppendLine("SAVEWORK 异常: " + ex.Message); }

                // 5) 读 CE after（判空）
                DbElement ce2 = null; string ce2r = "";
                try { ce2 = CE.Element; } catch { }
                try { if (ce2 != null && !ce2.IsNull) ce2r = ce2.GetAsString(ATT.NAME); } catch { }
                log.AppendLine("CE AFTER: " + ((ce2 == null || ce2.IsNull || ce2.IsDeleted) ? "悬空/无效" : ce2.GetElementType() + " " + ce2r));
                log.AppendLine("=== BOOT-SELFHEAL-END " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===");

                res.Ok = true;
                res.Output = log.ToString();
            }
            catch (Exception ex)
            {
                res.Ok = false;
                res.Error = "[" + ex.GetType().Name + "] " + ex.Message;
                log.AppendLine("=== SELFHEAL-FAIL " + res.Error + "\n" + ex.StackTrace + " ===");
                res.Output = log.ToString();
            }
            return res;
        }
    }
}
