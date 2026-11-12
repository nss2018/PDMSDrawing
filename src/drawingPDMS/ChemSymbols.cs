using System;
using System.Collections.Generic;
using Aveva.Pdms.Database;
using Aveva.Pdms.Geometry;

namespace drawingPDMS
{
    using ATT = Aveva.Pdms.Database.DbAttributeInstance;

    /// <summary>
    /// 化工（过程）管道图例符号集 —— 自绘 2D 几何（STRAIGHT 线段拼成），
    /// 不依赖 /MDS/ABA/TAGS、/TT/Isometric_ADP/Flan 等模板库（那些模板随项目走、别的电脑没有）。
    /// 本类编译进 drawingPDMS.dll，插件部署到哪台电脑，这套符号就在哪台电脑可用。
    ///
    /// 坐标约定：图纸 mm。每个符号先在【局部坐标】定义（管道沿 X 轴、原点在符号中心），
    /// 再按 angle（度，逆时针）旋转到管道方向，平移到图纸 (cx, cy)。
    /// 线段用「Fpt Offset(0,0) + Tpt Offset(Δx,Δy) + at x=P1」画，ADEG=0（旋转已在 C# 里做完），
    /// 与图纸范围框 getLimitDraw 的 STRAIGHT 画法一致（该画法已验证可用）。
    /// </summary>
    public static class ChemSymbols
    {
        // 标准实线线型（与范围框同款）
        private const string NLSTYL = "8004";

        /// <summary>局部坐标 (lx,ly) 绕原点旋转 angle 度(逆时针)后平移到 (cx,cy)。</summary>
        private static double[] T(double cx, double cy, double angleDeg, double lx, double ly)
        {
            double a = angleDeg * Math.PI / 180.0;
            double c = Math.Cos(a), s = Math.Sin(a);
            return new double[] { cx + lx * c - ly * s, cy + lx * s + ly * c };
        }

        /// <summary>
        /// 世界坐标(W,N) → 俯视纸面(mm)。公式与范围框/标注的定位完全一致：
        /// paper = XYPS + (WORPOS - THPO) * VSCA（DIR=D/ADEG=0：paper-x=W、paper-y=N）。
        /// 返回 [x, y]。
        /// </summary>
        public static double[] Project(DbElement view, double worldW, double worldN)
        {
            double thx = view.GetPosition(ATT.THPO).X, thy = view.GetPosition(ATT.THPO).Y;
            double vsca = view.GetDouble(ATT.VSCA);
            double[] xy = view.GetDoubleArray(ATT.XYPS);
            return new double[] { xy[0] + (worldW - thx) * vsca, xy[1] + (worldN - thy) * vsca };
        }

        /// <summary>
        /// 由元素方向向量求俯视(W-N)纸面方向角(度，标准数学角：0=+x/东，90=+y/北)。
        /// 用 cos(与轴夹角)=带符号分量 取 w/n（无需 Direction 分量 API，只依赖已验证的 .Angle()），
        /// atan2(n,w)。竖管(纯 U 向)在俯视退化为点 → w=n=0 → 返回 0（符号水平落位）。
        /// 所有符号对 180° 旋转/镜像对称，故此角只需对齐到"线"即可，方向正负无关。
        /// </summary>
        public static double PaperAngle(Direction dir)
        {
            if (dir == null) return 0;
            try
            {
                double w = Math.Cos(dir.Angle(Direction.Create(Axis.X)));
                double n = Math.Cos(dir.Angle(Direction.Create(Axis.Y)));
                if (Math.Abs(w) < 1e-6 && Math.Abs(n) < 1e-6) return 0;
                return Math.Atan2(n, w) * 180.0 / Math.PI;
            }
            catch { return 0; }
        }

        /// <summary>画一条线段（图纸绝对坐标 P1→P2）。</summary>
        public static void Seg(DbElement owner, double x1, double y1, double x2, double y2, int color)
        {
            if (owner == null || owner.IsNull) return;
            DbElement ln = owner.CreateLast(DbElementTypeInstance.STRAIGHT);
            if (ln == null || ln.IsNull) return;
            try
            {
                double dx = x2 - x1, dy = y2 - y1;
                double len = Math.Sqrt(dx * dx + dy * dy);
                ln.SetAttribute(ATT.NLSTYL, NLSTYL);
                ln.SetAttribute(ATT.LENG, len.ToString());
                ln.SetAttribute(ATT.NLCOLO, color);
                string nm = ln.GetAsString(ATT.NAME);
                if (nm == null || nm.Trim() == "" || nm.Contains("=0/0")) return;
                drawTool.CommandExcute(nm + " Fpt Offset X 0mm Y 0mm Tpt Offset X " + dx + "mm Y " + dy + "mm");
                drawTool.CommandExcute(nm + " at x " + x1 + " y " + y1);
            }
            catch { }
        }

        /// <summary>多边形圆（n 段近似），图纸绝对坐标 (cx,cy) 半径 r。</summary>
        public static void Circle(DbElement owner, double cx, double cy, double r, int color, int n)
        {
            if (n < 3) n = 12;
            for (int i = 0; i < n; i++)
            {
                double a1 = i * 2 * Math.PI / n;
                double a2 = (i + 1) * 2 * Math.PI / n;
                Seg(owner, cx + r * Math.Cos(a1), cy + r * Math.Sin(a1),
                       cx + r * Math.Cos(a2), cy + r * Math.Sin(a2), color);
            }
        }

        /// <summary>阀门（蝶阀/通用蝴蝶结 ▷◁）：管道沿 X 过原点，两个对顶三角形 + 阀杆。</summary>
        public static void Valve(DbElement owner, double cx, double cy, double angle, int color)
        {
            double d = 2.6, h = 2.2;
            // 左三角
            AddTri(owner, cx, cy, angle, 0, 0, -d, -h, -d, h, color);
            // 右三角
            AddTri(owner, cx, cy, angle, 0, 0, d, -h, d, h, color);
            // 阀杆（局部 +Y，垂直管道）
            double[] s1 = T(cx, cy, angle, 0, 0);
            double[] s2 = T(cx, cy, angle, 0, h + 1.6);
            Seg(owner, s1[0], s1[1], s2[0], s2[1], color);
            // 手轮（阀杆顶端短横线）
            double[] w1 = T(cx, cy, angle, -1.2, h + 1.6);
            double[] w2 = T(cx, cy, angle, 1.2, h + 1.6);
            Seg(owner, w1[0], w1[1], w2[0], w2[1], color);
        }

        private static void AddTri(DbElement owner, double cx, double cy, double angle,
            double ax, double ay, double bx, double by, double ex, double ey, int color)
        {
            double[] p0 = T(cx, cy, angle, ax, ay);
            double[] p1 = T(cx, cy, angle, bx, by);
            double[] p2 = T(cx, cy, angle, ex, ey);
            Seg(owner, p0[0], p0[1], p1[0], p1[1], color);
            Seg(owner, p1[0], p1[1], p2[0], p2[1], color);
            Seg(owner, p2[0], p2[1], p0[0], p0[1], color);
        }

        /// <summary>法兰：一条垂直于管道的短线（局部 Y 向）。</summary>
        public static void Flange(DbElement owner, double cx, double cy, double angle, int color)
        {
            double f = 1.9;
            double[] a = T(cx, cy, angle, 0, -f);
            double[] b = T(cx, cy, angle, 0, f);
            Seg(owner, a[0], a[1], b[0], b[1], color);
        }

        /// <summary>弯头（俯视竖直管端，end-on）：小圆；centerDot=true 表示远离视点(D)。</summary>
        public static void ElbowEnd(DbElement owner, double cx, double cy, int color, bool centerDot)
        {
            Circle(owner, cx, cy, 1.8, color, 14);
            if (centerDot)
            {
                double rr = 0.55;
                Seg(owner, cx - rr, cy, cx + rr, cy, color);
                Seg(owner, cx, cy - rr, cx, cy + rr, color);
            }
        }

        /// <summary>三通/支管：小方块标记（与弯头圆区分），轴对齐、无需旋转。</summary>
        public static void Tee(DbElement owner, double cx, double cy, int color)
        {
            double s = 1.3;
            Seg(owner, cx - s, cy - s, cx + s, cy - s, color);
            Seg(owner, cx + s, cy - s, cx + s, cy + s, color);
            Seg(owner, cx + s, cy + s, cx - s, cy + s, color);
            Seg(owner, cx - s, cy + s, cx - s, cy - s, color);
        }
    }
}
