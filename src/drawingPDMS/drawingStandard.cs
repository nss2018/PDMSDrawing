using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace drawingPDMS
{
    /// <summary>
    /// Central drafting standard for PDMS Draft output (WT-DRAW benchmark, 2026-11-08).
    ///
    /// Purpose: commercial tools such as WT-DRAW ship "drawing content needs no manual edit"
    /// by keeping fonts, line weights, layers, scales and annotation rules in one explicit
    /// standard. This class is the single place for those values in drawingPDMS.
    ///
    /// Compatibility rules:
    ///  - preset "project" reproduces the current project defaults exactly (no behaviour change).
    ///  - presets "HG20519" / "SH3052" follow HG/T 20519.1-2009 (6.1 line width, 6.2 text height),
    ///    HG/T 20519.4-2009 (2.1 A1 / 1:50 / mm) and SH/T 3052-2014 (3.1 line type usage).
    ///  - line widths are RECORDED + VALIDATED here. PDMS Draft uses STYL/pen tables, so the
    ///    values are not pushed into geometry by this class (pushing would change drawn output).
    ///  - any value can be overridden per drawing through the op parameter dictionary.
    /// </summary>
    public static class DrawingStandard
    {
        private sealed class Spec
        {
            public string Id;
            public string FrameSize;
            public double ScaleDen;
            public double TextHeight;
            public double DimTextHeight;
            public double TableTextHeight;
            public double TitleHeight;
            public double LineHeavy;
            public double LineMid;
            public double LineThin;
            public double LeaderPrimary;
            public double LeaderAlt;
            public bool PreferRight;
            public string Font;

            public Spec Copy()
            {
                return (Spec)MemberwiseClone();
            }
        }

        private static readonly Dictionary<string, Spec> Presets = new Dictionary<string, Spec>();
        private static readonly Dictionary<string, string> Ovr = new Dictionary<string, string>();
        private static Spec cur;

        static DrawingStandard()
        {
            // Project default = current behaviour (2026-10-28 baseline). Line widths 0 = not set.
            Presets["project"] = new Spec {
                Id = "project", FrameSize = "A0", ScaleDen = 50,
                TextHeight = 2.5, DimTextHeight = 2.5, TableTextHeight = 2.5, TitleHeight = 7,
                LineHeavy = 0, LineMid = 0, LineThin = 0,
                LeaderPrimary = 45, LeaderAlt = 135, PreferRight = true, Font = "project"
            };
            // HG/T 20519: A1, 1:50, text 5 / numbers 3.5 / table 3.5 / title 7,
            // line width classes 0.6-0.9 / 0.3-0.5 / 0.15-0.25.
            Presets["HG20519"] = new Spec {
                Id = "HG20519", FrameSize = "A1", ScaleDen = 50,
                TextHeight = 5, DimTextHeight = 3.5, TableTextHeight = 3.5, TitleHeight = 7,
                LineHeavy = 0.7, LineMid = 0.35, LineThin = 0.2,
                LeaderPrimary = 45, LeaderAlt = 135, PreferRight = true, Font = "长仿宋"
            };
            // SH/T 3052: single-line pipe 0.7, double-line pipe 0.5, dimension line 0.25,
            // centre line 0.2; keep the same frame/scale/text as HG20519.
            Spec sh = Presets["HG20519"].Copy();
            sh.Id = "SH3052"; sh.LineHeavy = 0.7; sh.LineMid = 0.5; sh.LineThin = 0.25;
            Presets["SH3052"] = sh;
            cur = Presets["project"].Copy();
        }

        private static string Get(Dictionary<string, string> p, string key)
        {
            if (p == null || !p.ContainsKey(key)) return null;
            string v = p[key];
            return string.IsNullOrEmpty(v) ? null : v.Trim();
        }

        private static double Num(Dictionary<string, string> p, string key, double def)
        {
            string s = Get(p, key);
            double v;
            if (s != null && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out v)) return v;
            return def;
        }

        /// <summary>Apply a standard preset + per-drawing overrides. Call at the start of every drawing op.</summary>
        public static void Apply(Dictionary<string, string> p)
        {
            Ovr.Clear();
            string id = Get(p, "standard");
            if (id == null || !Presets.ContainsKey(id)) id = "project";
            cur = Presets[id].Copy();

            cur.FrameSize = Str(p, "frameSize", cur.FrameSize);
            cur.ScaleDen = Num(p, "stdScaleDen", cur.ScaleDen);
            cur.TextHeight = Num(p, "textHeight", cur.TextHeight);
            cur.DimTextHeight = Num(p, "dimTextHeight", cur.DimTextHeight);
            cur.TableTextHeight = Num(p, "tableTextHeight", cur.TableTextHeight);
            cur.TitleHeight = Num(p, "titleHeight", cur.TitleHeight);
            cur.LineHeavy = Num(p, "lineHeavy", cur.LineHeavy);
            cur.LineMid = Num(p, "lineMid", cur.LineMid);
            cur.LineThin = Num(p, "lineThin", cur.LineThin);
            cur.LeaderPrimary = Num(p, "leaderAngle", cur.LeaderPrimary);
            cur.LeaderAlt = Num(p, "leaderAltAngle", cur.LeaderAlt);
            cur.Font = Str(p, "font", cur.Font);
            string prefer = Str(p, "leaderPrefer", cur.PreferRight ? "right" : "left");
            cur.PreferRight = !"left".Equals(prefer, StringComparison.OrdinalIgnoreCase);

            foreach (string k in new string[] {
                "layerOutline", "layerDim", "layerText", "layerFrame", "layerTable",
                "layerColorOutline", "layerColorDim", "layerColorText", "layerColorFrame", "layerColorTable",
                "layerLStyleOutline", "layerLStyleDim", "layerLStyleText", "layerLStyleFrame", "layerLStyleTable" })
            {
                string v = Get(p, k);
                if (v != null) Ovr[k] = v;
            }
        }

        private static string Str(Dictionary<string, string> p, string key, string def)
        {
            string s = Get(p, key);
            return s == null ? def : s;
        }

        /// <summary>45-degree leaders, right-above first (project convention 2026-10-28).</summary>
        public static double[] LeaderAngles()
        {
            return cur.PreferRight
                ? new double[] { cur.LeaderPrimary, cur.LeaderAlt }
                : new double[] { cur.LeaderAlt, cur.LeaderPrimary };
        }

        public static string LayerName(DrawLayerCat cat, string def)
        {
            return LayerVal(cat, "layer", def);
        }

        public static int LayerColor(DrawLayerCat cat, int def)
        {
            string s = LayerVal(cat, "layerColor", null);
            int v;
            if (s != null && int.TryParse(s, out v)) return v;
            return def;
        }

        public static string LayerLStyle(DrawLayerCat cat, string def)
        {
            return LayerVal(cat, "layerLStyle", def);
        }

        private static string LayerVal(DrawLayerCat cat, string prefix, string def)
        {
            string key = prefix + cat.ToString();
            string v;
            return Ovr.TryGetValue(key, out v) ? v : def;
        }

        /// <summary>Default text height of the standard LAYER elements.</summary>
        public static string LayerChei()
        {
            return cur.TextHeight.ToString("0.##", CultureInfo.InvariantCulture);
        }

        /// <summary>One-line standard digest for op logs (evidence for unattended runs).</summary>
        public static string Summary()
        {
            return "[STD] preset=" + cur.Id + " frame=" + cur.FrameSize +
                   " scale=1:" + cur.ScaleDen.ToString("0.##", CultureInfo.InvariantCulture) +
                   " text=" + cur.TextHeight.ToString("0.##", CultureInfo.InvariantCulture) +
                   " dim=" + cur.DimTextHeight.ToString("0.##", CultureInfo.InvariantCulture) +
                   " table=" + cur.TableTextHeight.ToString("0.##", CultureInfo.InvariantCulture) +
                   " title=" + cur.TitleHeight.ToString("0.##", CultureInfo.InvariantCulture) +
                   " line=" + cur.LineHeavy.ToString("0.##", CultureInfo.InvariantCulture) + "/" +
                   cur.LineMid.ToString("0.##", CultureInfo.InvariantCulture) + "/" +
                   cur.LineThin.ToString("0.##", CultureInfo.InvariantCulture) +
                   " leader=" + cur.LeaderPrimary.ToString("0.##", CultureInfo.InvariantCulture) + ">" +
                   cur.LeaderAlt.ToString("0.##", CultureInfo.InvariantCulture) +
                   " font=" + cur.Font;
        }

        /// <summary>
        /// Non-fatal compliance checks. Returns warning strings only - never aborts a drawing.
        /// scaleDen: real drawing scale denominator (0 = unknown, skip the scale check).
        /// </summary>
        public static List<string> Validate(double scaleDen)
        {
            List<string> w = new List<string>();
            if (cur.TextHeight < 2.5)
                w.Add("textHeight " + cur.TextHeight + " < 2.5mm (HG/T 20519.1 6.2.2)");
            if ((cur.FrameSize == "A0" || cur.FrameSize == "A1") && cur.TextHeight < 5 && cur.Id != "project")
                w.Add("A0/A1 textHeight " + cur.TextHeight + " < 5mm (HG/T 20519.1 6.2.2)");
            if (cur.LineHeavy > 0 && (cur.LineHeavy < 0.6 || cur.LineHeavy > 0.9))
                w.Add("lineHeavy " + cur.LineHeavy + " outside 0.6-0.9mm");
            if (cur.LineMid > 0 && (cur.LineMid < 0.3 || cur.LineMid > 0.5))
                w.Add("lineMid " + cur.LineMid + " outside 0.3-0.5mm");
            if (cur.LineThin > 0 && (cur.LineThin < 0.15 || cur.LineThin > 0.25))
                w.Add("lineThin " + cur.LineThin + " outside 0.15-0.25mm");
            if (cur.LineHeavy > 0 && cur.LineMid > 0 && cur.LineThin > 0 &&
                !(cur.LineHeavy > cur.LineMid && cur.LineMid > cur.LineThin))
                w.Add("line width classes are not heavy > mid > thin");
            double sc = scaleDen > 0 ? scaleDen : cur.ScaleDen;
            if (sc != 25 && sc != 30 && sc != 50)
                w.Add("scale 1:" + sc + " is not the usual 1:25 / 1:30 / 1:50 (HG/T 20519.4 2.1.2)");
            if (cur.LeaderPrimary != 45 || cur.LeaderAlt != 135)
                w.Add("leader angles " + cur.LeaderPrimary + "/" + cur.LeaderAlt + " differ from the 45/135 project convention");
            if (cur.FrameSize != "A0" && cur.FrameSize != "A1" && cur.FrameSize != "A2")
                w.Add("frameSize " + cur.FrameSize + " is not A0/A1/A2");
            return w;
        }

        /// <summary>Compact compliance report for the batch/audit report (no PDMS calls needed).</summary>
        public static string Report()
        {
            StringBuilder b = new StringBuilder();
            b.AppendLine(Summary());
            List<string> w = Validate(0);
            if (w.Count == 0) b.AppendLine("[STD] compliance: OK");
            else foreach (string s in w) b.AppendLine("[STD-WARN] " + s);
            return b.ToString();
        }
    }
}
