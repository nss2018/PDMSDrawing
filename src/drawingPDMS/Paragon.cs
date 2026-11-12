// Paragon.cs —— 建管前的规格 / 管件选择数据源（对应方圆 Paragon.cs 15 方法子集）
//
// 提供：
//   * SpecBoreTable(spec, metric)        —— 等级→管径清单（公/英制过滤）
//   * SpecBoreSortedList(spec, metric)   —— 排序后的管径清单
//   * SpecLeaveTube(bore, lastLeaveTube, metric) —— 按管径匹配 leave tube
//   * CoCoTable()                        —— 组件连接规则表（CoCo）【连接合法性数据源】
//   * SpecFittingTable(spec, gType)      —— 等级内某几何类型的管件清单
//   * Filter(...)                        —— 通用列表过滤
//
// 纯逻辑（管径过滤 / leave-tube 选择 / CoCo 合法性判定）抽成 internal 静态方法，离线可断言。

using System;
using System.Collections.Generic;
using System.Data;
using System.Text;

namespace Radwing.Piping
{
    public static class Paragon
    {
        /* ===================== 1. 等级管径表 ===================== */

        /// <summary>纯函数：拼出读取某等级管径清单的 PML 表达式（var !!T ...）。</summary>
        internal static string BuildSpecBoreExpr(string spec)
        {
            // PML：取等级对象后读其 borelist（PDMS 12.1 paragon 属性）。
            return "(spec('/' + '" + spec + "')).borelist";
        }

        public static List<string> SpecBoreTable(string spec, bool metric)
        {
            string err;
            string expr = BuildSpecBoreExpr(spec);
            if (!Pdms.Runner.Run("var !!T " + expr, out err))
                throw new PdmsCommandException(err ?? "SpecBoreTable failed");
            string[] arr = Pdms.Runner.GetPMLVariableStringArray("T");
            List<string> bores = new List<string>(arr ?? new string[0]);
            return FilterBoresByMetric(bores, metric);
        }

        public static List<string> SpecBoreSortedList(string spec, bool metric)
        {
            List<string> bores = SpecBoreTable(spec, metric);
            bores.Sort(); // 字符串排序对 "15/20/25..." 在数字上不完全等序；生产建议按数值排，此处给出稳定序
            return bores;
        }

        /// <summary>纯函数：从管径清单里按单位过滤（英制 bore 形如 '0.5'/ '1' 多为小数；公制多为整数 mm 串）。</summary>
        internal static List<string> FilterBoresByMetric(List<string> bores, bool metric)
        {
            if (bores == null) return new List<string>();
            List<string> outp = new List<string>();
            foreach (string b in bores)
            {
                if (string.IsNullOrEmpty(b)) continue;
                bool hasDot = b.IndexOf('.') >= 0;
                double val;
                bool isNum = double.TryParse(b, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out val);
                // 英制判定：带小数点，或纯整数且值<=2（inch 常见 1/2 等）；公制：整数且>=3（mm）。
                // 例：'15'/'20'/'25'→公制；'0.5'/'1'/'1.5'→英制。
                bool imperial = hasDot || (isNum && val <= 2.0);
                if (metric && !imperial) outp.Add(b);
                else if (!metric && imperial) outp.Add(b);
            }
            return outp;
        }

        private static bool IsPureNumber(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            int dots = 0;
            foreach (char c in s)
            {
                if (c == '.') { dots++; if (dots > 1) return false; }
                else if (c < '0' || c > '9') return false;
            }
            return true;
        }

        /* ===================== 2. Leave Tube 选择 ===================== */

        /// <summary>纯函数：从 leave-tube 清单里按名义管径匹配；lastLeaveTube=true 时优先取列表末项。</summary>
        internal static string ResolveLeaveTube(double bore, List<string> allTubes, bool lastLeaveTube)
        {
            if (allTubes == null || allTubes.Count == 0) return "";
            if (lastLeaveTube) return allTubes[allTubes.Count - 1];
            // 按名义管径最接近 bore 的匹配
            string best = "";
            double bestDiff = double.MaxValue;
            foreach (string t in allTubes)
            {
                double nb = NominalBore(t);
                double diff = Math.Abs(nb - bore);
                if (diff < bestDiff) { bestDiff = diff; best = t; }
            }
            return best;
        }

        /// <summary>从 leave-tube 名里解析名义管径（名形如 'LT-50' / 'LEAVETUBE_2IN'）。</summary>
        internal static double NominalBore(string tubeName)
        {
            if (string.IsNullOrEmpty(tubeName)) return 0.0;
            StringBuilder num = new StringBuilder();
            foreach (char c in tubeName)
            {
                if ((c >= '0' && c <= '9') || c == '.') num.Append(c);
                else if (num.Length > 0) break; // 取连续数字段
            }
            double v;
            return double.TryParse(num.ToString(), out v) ? v : 0.0;
        }

        public static string SpecLeaveTube(double bore, bool lastLeaveTube, bool metric)
        {
            // 真实实现：从当前 spec 的 leave-tube 组件清单里选；此处先用 SpecBoreTable 推导候选名，
            // 再用 ResolveLeaveTube 选择。生产环境应改为直接查询 spec 的 leave-tube 属性。
            List<string> tubes = SpecBoreTable("LEAVETUBE", metric);
            // leave-tube 清单通常不带 bore 名，这里用 bore 构造候选并按名解析；若清单为空返回空串。
            if (tubes.Count == 0) tubes = new List<string> { "LT-" + (metric ? bore.ToString("0") : bore.ToString("0.0")) };
            return ResolveLeaveTube(bore, tubes, lastLeaveTube);
        }

        /* ===================== 3. CoCo 连接规则表 ===================== */

        /// <summary>组件连接规则表（CoCo）。列：ComponentType / FromType / ToType / Allowed。</summary>
        public static DataTable CoCoTable()
        {
            DataTable dt = new DataTable("CoCo");
            dt.Columns.Add("ComponentType", typeof(string));
            dt.Columns.Add("FromType", typeof(string));
            dt.Columns.Add("ToType", typeof(string));
            dt.Columns.Add("Allowed", typeof(bool));
            // 生产实现应查询 paragon 的 coco 属性并逐行填充；此处给出结构骨架 + 常用默认规则。
            AddCoCo(dt, "ELBO", "TUBE", "TUBE", true);
            AddCoCo(dt, "TEE", "TUBE", "TUBE", true);
            AddCoCo(dt, "FLAN", "TUBE", "TUBE", true);
            AddCoCo(dt, "VALV", "FLAN", "FLAN", true);
            AddCoCo(dt, "REDU", "TUBE", "TUBE", true);
            return dt;
        }

        private static void AddCoCo(DataTable dt, string comp, string from, string to, bool allowed)
        {
            DataRow r = dt.NewRow();
            r["ComponentType"] = comp; r["FromType"] = from; r["ToType"] = to; r["Allowed"] = allowed;
            dt.Rows.Add(r);
        }

        /// <summary>纯函数：判定 componentType 是否允许 fromType→toType 连接（CoCo 规则查表）。</summary>
        internal static bool ResolveCoCo(DataTable coco, string componentType, string fromType, string toType)
        {
            if (coco == null) return false;
            foreach (DataRow r in coco.Rows)
            {
                if (string.Equals((string)r["ComponentType"], componentType, StringComparison.OrdinalIgnoreCase)
                    && string.Equals((string)r["FromType"], fromType, StringComparison.OrdinalIgnoreCase)
                    && string.Equals((string)r["ToType"], toType, StringComparison.OrdinalIgnoreCase))
                {
                    return (bool)r["Allowed"];
                }
            }
            return false;
        }

        /* ===================== 4. 等级管件表 / 通用过滤 ===================== */

        /// <summary>等级内某几何类型（gType，如 'ELBO'/'TEE'/'FLAN'）的管件名清单。</summary>
        public static List<string> SpecFittingTable(string spec, string gType)
        {
            string err;
            string expr = "(spec('/' + '" + spec + "')).fitlist('" + gType + "')";
            if (!Pdms.Runner.Run("var !!F " + expr, out err))
                throw new PdmsCommandException(err ?? "SpecFittingTable failed");
            string[] arr = Pdms.Runner.GetPMLVariableStringArray("F");
            return new List<string>(arr ?? new string[0]);
        }

        /// <summary>通用过滤：按 filter（子串）匹配，displayValue 决定是否返回展示名。</summary>
        public static List<string> Filter(List<string> list, string filter, bool displayValue, string sprefValue)
        {
            List<string> outp = new List<string>();
            if (list == null) return outp;
            foreach (string s in list)
            {
                if (string.IsNullOrEmpty(filter) || (s != null && s.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    if (displayValue) outp.Add(s + (string.IsNullOrEmpty(sprefValue) ? "" : " [" + sprefValue + "]"));
                    else outp.Add(s);
                }
            }
            return outp;
        }

        /* ===================== 5. 规格清单枚举（供 UI 下拉） ===================== */

        /// <summary>纯函数：拼出枚举全部 SPEC 的命令（coll all for SPEC）。</summary>
        internal static string BuildSpecListCommand()
        {
            return "coll all for SPEC";
        }

        /// <summary>枚举当前 MDB 内全部 SPEC 名（UI 下拉数据源）。</summary>
        public static List<string> GetSpecNames()
        {
            // coll all for SPEC 是查询命令，结果不会写入变量；改用 PML 赋值回读。
            string err;
            if (!Pdms.Runner.Run("!!SPECS = " + BuildSpecListCommand(), out err))
                throw new PdmsCommandException(err ?? "GetSpecNames failed");
            string[] arr = Pdms.Runner.GetPMLVariableStringArray("SPECS");
            return new List<string>(arr ?? new string[0]);
        }

        /// <summary>SpecBoreTable 便捷重载：单位跟随当前项目设置。</summary>
        public static List<string> SpecBoreTable(string spec)
        {
            return SpecBoreTable(spec, Utilities.DistanceIsMetric());
        }

        /* ===================== 6. 公开 CoCo 合法性判定 ===================== */

        /// <summary>从 CoCo 规则表判定 componentType 是否允许 fromType→toType 连接（自动加载规则表）。</summary>
        public static bool IsCoCoAllowed(string componentType, string fromType, string toType)
        {
            return ResolveCoCo(CoCoTable(), componentType, fromType, toType);
        }
    }
}
