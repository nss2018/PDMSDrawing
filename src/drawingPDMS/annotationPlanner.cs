using System;
using System.Collections.Generic;

namespace drawingPDMS
{
    /// <summary>
    /// (14d) 全局标注避让规划器（2026-11-08 用户「标注和管线标签和设备标签糊成一团」）。
    /// 业界对标（2026-11-08 网上调研）：
    ///   · AVEVA E3D Draft 原生 Remote Label Placement——密集区标签撒到视图边界四边
    ///     成排放置 + 最小间距 + 引线连回锚点；
    ///   · 诗诺 Drawing Engine——打分制避让：重叠面积 / 标签对齐 / 引线交叉 /
    ///     引线长度 / 关键点遮挡综合评最优位置；
    ///   · E3D 标签优化算法（公开文章）——硬约束（不越界、标签最小间距）+
    ///     软约束（最小移动距离，能不动就不动）+ 候选位置由近及远。
    /// 本类是全类别共享的 AABB 占位登记表：此前 10g 设备标高（零避让盲放）、
    /// 9g 管线号菱形（只跟同族点距）、9j 尺寸引出（只跟同批 6mm 点距）、
    /// 10c 定位字（只跟同族边界行）互不知情，密集区互相压字。
    /// 落位顺序即执行顺序（优先级）：10g 设备标高 → 9g 菱形 → 10c 定位字 → 9j 引出字。
    /// 线程约定：单线程 Draft 出图流程内调用，静态表跨 op 存活，每轮出图 Reset。
    /// </summary>
    public static class AnnotationPlanner
    {
        // box = {x0, x1, y0, y1, kind}
        // kind: 0=设备标高字(10g) 1=管线号菱形(9g) 2=尺寸引出字(9j) 3=定位字(10c) 9=禁区
        private static readonly List<double[]> _boxes = new List<double[]>();
        // (14f) 几何禁区 = 设备包络 / 管线拐角 / 管件占位圈的纸面矩形 {x0,x1,y0,y1}。
        // 与 _boxes 分开存：标注压几何不该算「标注互压」，但必须参与打分与清洁判定——
        // 11-08 实机截图实证：只登记标注盒时，规划器认为设备轮廓/管线转角处一片空白，
        // 于是「零碰撞即原地」，菱形与标高字照旧压在设备上，视觉上等于没优化。
        private static readonly List<double[]> _forbid = new List<double[]>();
        // (14f) 强制引出（转角/密集区标注）次数，日志用
        private static int _nEscort;
        // leader segment = {x0, y0, x1, y1}
        private static readonly List<double[]> _leads = new List<double[]>();
        // 视图界限 {x0, x1, y0, y1}；null = 边界退让禁用
        private static double[] _lim;
        // 边界成排行占位（E3D remote label 同款）：每侧一组 {lo, hi} 区间
        private static readonly List<double[]> _rowT = new List<double[]>();
        private static readonly List<double[]> _rowB = new List<double[]>();
        private static readonly List<double[]> _rowL = new List<double[]>();
        private static readonly List<double[]> _rowR = new List<double[]>();
        private static int _nFallback;
        private static int _nCommit;

        /// <summary>每轮出图开始时清表并设视图界限（lim={x0,x1,y0,y1}，可 null）。</summary>
        public static void Reset(double[] lim)
        {
            _boxes.Clear(); _leads.Clear(); _forbid.Clear();
            _rowT.Clear(); _rowB.Clear(); _rowL.Clear(); _rowR.Clear();
            _lim = lim; _nFallback = 0; _nCommit = 0; _nEscort = 0;
        }

        /// <summary>本轮退让到边界成排行的次数（日志用）。</summary>
        public static int FallbackCount { get { return _nFallback; } }

        /// <summary>本轮强制引出的转角标注数（日志用）。</summary>
        public static int EscortCount { get { return _nEscort; } }

        /// <summary>本轮登记的几何禁区数（日志用）。</summary>
        public static int ForbidCount { get { return _forbid.Count; } }

        /// <summary>本轮登记的占位盒总数（日志用）。</summary>
        public static int BoxCount { get { return _boxes.Count; } }

        /// <summary>本轮登记的引线段总数（日志用）。</summary>
        public static int LeadCount { get { return _leads.Count; } }

        /// <summary>候选盒 (cx,cy,hw,hh) 与已登记盒的最大重叠面积；0 = 无碰撞。</summary>
        public static double ClashArea(double cx, double cy, double hw, double hh)
        {
            double x0 = cx - hw, x1 = cx + hw, y0 = cy - hh, y1 = cy + hh;
            double sum = 0;
            for (int i = 0; i < _boxes.Count; i++)
            {
                double[] b = _boxes[i];
                double ox = Math.Min(x1, b[1]) - Math.Max(x0, b[0]);
                double oy = Math.Min(y1, b[3]) - Math.Max(y0, b[2]);
                if (ox > 0 && oy > 0) sum += ox * oy;
            }
            return sum;
        }

        /// <summary>登记一个几何禁区矩形（纸面 mm，绝对坐标）。设备包络/拐角/管件圈用。</summary>
        public static void CommitForbiddenBox(double x0, double x1, double y0, double y1)
        {
            if (x1 <= x0 || y1 <= y0) return;
            // 上限保护：禁区过多会拖慢每次打分（Score 对每个候选遍历）
            if (_forbid.Count >= 2400) return;
            _forbid.Add(new double[] { x0, x1, y0, y1 });
        }

        /// <summary>登记一个以 (cx,cy) 为中心的几何禁区（纸面 mm）。</summary>
        public static void CommitForbidden(double cx, double cy, double hw, double hh)
        {
            CommitForbiddenBox(cx - hw, cx + hw, cy - hh, cy + hh);
        }

        /// <summary>
        /// 候选盒与几何禁区的首个重叠面积；0 = 完全不压设备/管线/管件。
        /// 只取首个而非累加：禁区是「不许放」而非「放得越多越差」，提前退出省时。
        /// </summary>
        public static double ForbidArea(double cx, double cy, double hw, double hh)
        {
            double x0 = cx - hw, x1 = cx + hw, y0 = cy - hh, y1 = cy + hh;
            for (int i = 0; i < _forbid.Count; i++)
            {
                double[] b = _forbid[i];
                double ox = Math.Min(x1, b[1]) - Math.Max(x0, b[0]);
                if (ox <= 0) continue;
                double oy = Math.Min(y1, b[3]) - Math.Max(y0, b[2]);
                if (oy > 0) return ox * oy;
            }
            return 0;
        }

        /// <summary>候选盒是否压在设备/管线/管件几何上。</summary>
        public static bool HasForbidden(double cx, double cy, double hw, double hh)
        {
            return ForbidArea(cx, cy, hw, hh) > 0.05;
        }

        /// <summary>候选盒是否与已登记盒碰撞（面积 &gt; 0.05mm² 容差）。</summary>
        public static bool HasClash(double cx, double cy, double hw, double hh)
        {
            return ClashArea(cx, cy, hw, hh) > 0.05;
        }

        /// <summary>
        /// 候选点清洁判定：既不压别的标注，也不压几何禁区。(14f) 后者是 11-08 实机
        /// 截图暴露的缺口——只查标注盒时，设备与转角被当成空地。
        /// </summary>
        public static bool IsClean(double cx, double cy, double hw, double hh)
        {
            return !HasClash(cx, cy, hw, hh) && !HasForbidden(cx, cy, hw, hh);
        }

        /// <summary>
        /// 候选点打分（越小越好）：重叠面积（硬伤，×8 主导）+ 引线长度×0.6 +
        /// 引线与已登记引线交叉（每处 +40）+ 越界 +60。纯几何，不查同族点距——
        /// 调用方原有的同族逻辑照旧叠加。
        /// </summary>
        public static double Score(double cx, double cy, double hw, double hh, double ax, double ay)
        {
            double x0 = cx - hw, x1 = cx + hw, y0 = cy - hh, y1 = cy + hh;
            double s = ClashArea(cx, cy, hw, hh) * 8.0;
            // (14f) 压几何的惩罚：权重低于「标注互压」但足以把候选推离设备/转角
            s += ForbidArea(cx, cy, hw, hh) * 2.5;
            // 引线：锚点到候选盒最近点
            double nx = Math.Max(x0, Math.Min(ax, x1));
            double ny = Math.Max(y0, Math.Min(ay, y1));
            double dvx = ax - nx, dvy = ay - ny;
            double llen = Math.Sqrt(dvx * dvx + dvy * dvy);
            s += llen * 0.6;
            for (int i = 0; i < _leads.Count; i++)
            {
                double[] g = _leads[i];
                if (SegCross(ax, ay, nx, ny, g[0], g[1], g[2], g[3])) s += 40.0;
            }
            if (_lim != null)
            {
                double mg = 2.0;
                if (x0 < _lim[0] - mg || x1 > _lim[1] + mg || y0 < _lim[2] - mg || y1 > _lim[3] + mg) s += 60.0;
            }
            return s;
        }

        /// <summary>
        /// 环形候选选点（由近及远）：围绕锚点 (ax,ay) 按 angles×dists 生成候选，
        /// Score 打分。接受规则：
        ///   fb=0 —— 存在零碰撞候选，取分最低者（工程首选）；
        ///   fb=1 —— 全碰撞，退让到视图边界成排行（E3D remote label 同款）；
        ///   fb=2 —— 边界退让也不可用（无界限/全超界），取分最低候选硬放（与旧行为等价退化）；
        ///   返回 null —— angles/dists 全空且无界限（调用方保留自己的落点）。
        /// 返回 {cx, cy, fb}。
        /// </summary>
        public static double[] PlaceBest(double ax, double ay, double hw, double hh, double[] angles, double[] dists)
        {
            double bestAny = double.MaxValue; double[] posAny = null;
            double bestClean = double.MaxValue; double[] posClean = null;
            if (angles != null && dists != null && angles.Length > 0 && dists.Length > 0)
            {
                for (int ia = 0; ia < angles.Length; ia++)
                {
                    double r = angles[ia] * Math.PI / 180.0;
                    for (int id = 0; id < dists.Length; id++)
                    {
                        double cx = ax + Math.Cos(r) * dists[id];
                        double cy = ay + Math.Sin(r) * dists[id];
                        double sc = Score(cx, cy, hw, hh, ax, ay);
                        sc += dists[id] * 0.01; // 同分偏好近者（最小扰动）
                        if (sc < bestAny) { bestAny = sc; posAny = new double[] { cx, cy }; }
                        if (sc < bestClean && IsClean(cx, cy, hw, hh))
                        {
                            // 零碰撞候选里再取引线最短（重叠项已为 0，等价最小分）
                            if (posClean == null || sc < bestClean) { bestClean = sc; posClean = new double[] { cx, cy }; }
                        }
                    }
                }
            }
            if (posClean != null)
            {
                Commit(posClean[0], posClean[1], hw, hh, 0);
                return new double[] { posClean[0], posClean[1], 0 };
            }
            // 全碰撞 → 边界成排行退让
            double[] fb = PlaceOutsideRow(ax, ay, hw, hh);
            if (fb != null)
            {
                _nFallback++;
                Commit(fb[0], fb[1], hw, hh, 0);
                // 退让引线同样登记（长引线是既成事实，后续候选避让它）
                CommitLead(ax, ay, fb[0], fb[1]);
                return new double[] { fb[0], fb[1], 1 };
            }
            if (posAny != null)
            {
                Commit(posAny[0], posAny[1], hw, hh, 0);
                return new double[] { posAny[0], posAny[1], 2 };
            }
            return null;
        }

        /// <summary>
        /// (14f) 转角/密集区标注强制引出（用户 11-08「转角标注可以引出去」）。
        /// 45° 方向优先（右上 &gt; 左上，项目出图约定），由近及远 {10..76}mm 找
        /// 「不压别的标注 + 不压设备/管线」的位置；近处全满则退让到视图边界成排行。
        /// 与 PlaceBest 的区别：候选更远、方向严格先 45° 族，专治锚点紧挨拐角的情形。
        /// 返回 {cx, cy, fb}；调用方按 fb 决定是否画 45° 折线长引线。
        /// </summary>
        public static double[] PlaceEscort(double ax, double ay, double hw, double hh)
        {
            _nEscort++;
            // (14k) 2026-11-08 用户「标注拉得太远」：最远候选 76→48mm，近距优先
            double[] r = PlaceBest(ax, ay, hw, hh,
                new double[] { 45, 135, 315, 225, 0, 90, 180, 270 },
                new double[] { 8, 12, 18, 26, 36, 48 });
            if (r != null) return r;
            double[] fb = PlaceOutsideRow(ax, ay, hw, hh);
            if (fb == null) return null;
            _nFallback++;
            Commit(fb[0], fb[1], hw, hh, 0);
            CommitLead(ax, ay, fb[0], fb[1]);
            return new double[] { fb[0], fb[1], 1 };
        }

        /// <summary>
        /// 边界成排行退让（E3D remote label 思路）。(14k) 2026-11-08 用户「标注拉得太远」：
        /// 此前只试「几何最近的一条边」，同边堆叠把后到的标签沿边推很远，引线斜穿全图
        /// （11-08 截图实证）。改为四边都试排一遍，取「最终落位离锚点最近」的一边——
        /// 堆叠漂移计入距离，后到的标签自动换到不挤的那边。返回 {cx, cy, 1}；无界限返回 null。
        /// </summary>
        private static double[] PlaceOutsideRow(double ax, double ay, double hw, double hh)
        {
            if (_lim == null) return null;
            double bestD14k = double.MaxValue; double[] best14k = null; int bestSide14k = -1;
            for (int side = 0; side < 4; side++)
            {
                double[] cand = ProbeRowSide(side, ax, ay, hw, hh);
                if (cand == null) continue;
                double d = (cand[0] - ax) * (cand[0] - ax) + (cand[1] - ay) * (cand[1] - ay);
                if (d < bestD14k) { bestD14k = d; best14k = cand; bestSide14k = side; }
            }
            if (best14k == null) return null;
            // 中标后才真正占用该边的排位区间
            if (bestSide14k == 0) _rowL.Add(new double[] { best14k[1] - hh, best14k[1] + hh });
            else if (bestSide14k == 1) _rowR.Add(new double[] { best14k[1] - hh, best14k[1] + hh });
            else if (bestSide14k == 2) _rowB.Add(new double[] { best14k[0] - hw, best14k[0] + hw });
            else _rowT.Add(new double[] { best14k[0] - hw, best14k[0] + hw });
            return new double[] { best14k[0], best14k[1], 1 };
        }

        /// <summary>(14k) 试排某一边（不占用区间）：返回 {cx, cy}；与原 PlaceOutsideRow
        /// 单边逻辑一致——贴边外放 + 与已占区间错档 + 钳回视口。side 0=L 1=R 2=B 3=T。</summary>
        private static double[] ProbeRowSide(int side, double ax, double ay, double hw, double hh)
        {
            double cx, cy; List<double[]> row;
            if (side == 0 || side == 1)
            {
                cx = side == 0 ? _lim[0] - 3.5 - hw : _lim[1] + 3.5 + hw;
                cy = ay; row = side == 0 ? _rowL : _rowR;
                bool clash = true; int guard = 0;
                while (clash && guard < 200)
                {
                    clash = false;
                    for (int i = 0; i < row.Count; i++)
                    {
                        double[] o = row[i];
                        if (cy + hh > o[0] && cy - hh < o[1]) { cy = o[1] + hh + 0.8; clash = true; break; }
                    }
                    guard++;
                }
                if (cy > _lim[3] - hh) cy = _lim[3] - hh;
                if (cy < _lim[2] + hh) cy = _lim[2] + hh;
            }
            else
            {
                cy = side == 2 ? _lim[2] - 2.5 - hh : _lim[3] + 2.5 + hh;
                cx = ax; row = side == 2 ? _rowB : _rowT;
                bool clash = true; int guard = 0;
                while (clash && guard < 200)
                {
                    clash = false;
                    for (int i = 0; i < row.Count; i++)
                    {
                        double[] o = row[i];
                        if (cx + hw > o[0] && cx - hw < o[1]) { cx = o[1] + hw + 0.8; clash = true; break; }
                    }
                    guard++;
                }
                if (cx > _lim[1] - hw) cx = _lim[1] - hw;
                if (cx < _lim[0] + hw) cx = _lim[0] + hw;
            }
            return new double[] { cx, cy };
        }

        /// <summary>登记一个已落位标注盒（kind 见类头注释）。</summary>
        public static void Commit(double cx, double cy, double hw, double hh, int kind)
        {
            _boxes.Add(new double[] { cx - hw, cx + hw, cy - hh, cy + hh, kind });
            _nCommit++;
        }

        /// <summary>登记一个任意矩形盒（x0,x1,y0,y1，如边界外定位字的实际包围盒）。</summary>
        public static void CommitBox(double x0, double x1, double y0, double y1, int kind)
        {
            _boxes.Add(new double[] { x0, x1, y0, y1, kind });
            _nCommit++;
        }

        /// <summary>登记一段已画引线（后续候选的引线交叉惩罚依据）。</summary>
        public static void CommitLead(double x0, double y0, double x1, double y1)
        {
            _leads.Add(new double[] { x0, y0, x1, y1 });
        }

        // —— 线段严格相交测试（不含共线端点相触）——
        private static bool SegCross(double ax, double ay, double bx, double by, double cx, double cy, double dx, double dy)
        {
            double d1 = Cross(cx, cy, dx, dy, ax, ay);
            double d2 = Cross(cx, cy, dx, dy, bx, by);
            double d3 = Cross(ax, ay, bx, by, cx, cy);
            double d4 = Cross(ax, ay, bx, by, dx, dy);
            return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
        }

        private static double Cross(double ox, double oy, double px, double py, double qx, double qy)
        {
            return (px - ox) * (qy - oy) - (py - oy) * (qx - ox);
        }
    }
}
