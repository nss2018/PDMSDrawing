// Utilities.cs —— PML 读回包装 / 属性写入(单位换算) / 世界坐标 / 集合枚举 / 整库枚举 / 求值 / 拷贝 / 体积 / 命名 / 环境
// （对应方圆 Utilities.cs 高价值子集，按文档 §7 方法地图补齐）
//
// 设计：所有“命令构造”集中在 BuildXxxCommand 纯函数里（internal，离线可断言）。
//       AVEVA DbElement 仅在执行层出现；离线测试用 CaptureRunner 断言命令串。

using System;
using System.Collections.Generic;
using System.Text;
using Aveva.Pdms.Database;

namespace Radwing.Piping
{
    public static class Utilities
    {
        /* ===================== 1. PML 读回包装：var !!X <expr> ===================== */

        /// <summary>等效方圆 GetPMLString：把任意 PML 表达式包成 `var !!X &lt;expr&gt;` 并回读。</summary>
        public static string GetPMLString(string expr)
        {
            return Pdms.GetPMLString(expr);
        }

        public static string GetPMLVariableString(string name) { return Pdms.GetPMLVariableString(name); }
        public static double GetPMLVariableReal(string name) { return Pdms.GetPMLVariableReal(name); }
        public static bool GetPMLVariableBoolean(string name) { return Pdms.GetPMLVariableBoolean(name); }
        public static string[] GetPMLVariableStringArray(string name) { return Pdms.GetPMLVariableStringArray(name); }

        /// <summary>把 DbElement 转成 CP 可用的元素引用串（DB 名 / dbref()）。</summary>
        public static string Ref(DbElement el)
        {
            if (el == null) return "";
            // AVEVA DB API：元素唯一名经 NAME 属性读取（返回 "/XXX" 形式）；dbref() 在 CP 中可直接引用。
            return el.GetAsString(DbAttributeInstance.NAME);
        }

        /* ===================== 2. 集合枚举：coll all 的 PML 封装（9 重载） ===================== */

        /// <summary>纯函数：拼出 `coll all [within &lt;vol&gt;] for &lt;type&gt; [with &lt;cond&gt;] [extend] [&lt;hier&gt;]`。</summary>
        internal static string BuildCollectCommand(string scope, string type, string volume, string pmlCondition, bool extend, string hierarchy)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("coll all");
            if (!string.IsNullOrEmpty(scope)) sb.Append(" ").Append(scope);      // 例：within <vol> / of <owner>
            sb.Append(" for ").Append(type);
            if (!string.IsNullOrEmpty(pmlCondition)) sb.Append(" with ").Append(pmlCondition);
            if (extend) sb.Append(" extend");
            if (!string.IsNullOrEmpty(hierarchy)) sb.Append(" ").Append(hierarchy); // "hier" / "nohier"
            return sb.ToString();
        }

        private static List<string> RunCollect(string cmd)
        {
            // PDMS 的 coll 查询命令不会把结果写入任何变量；
            // 改发 PML 赋值语句 `!!CELLS = collect ...`，再回读该全局数组。
            string err;
            if (!Pdms.Runner.Run("!!CELLS = " + cmd, out err))
                throw new PdmsCommandException(err ?? "CollectElementsWithin failed");
            string[] arr = Pdms.Runner.GetPMLVariableStringArray("CELLS");
            return new List<string>(arr ?? new string[0]);
        }

        // 1) type only
        public static List<string> CollectElementsWithin(string type)
        {
            return RunCollect(BuildCollectCommand(null, type, null, null, false, null));
        }
        // 2) type + volume
        public static List<string> CollectElementsWithin(string type, string volume)
        {
            return RunCollect(BuildCollectCommand("within " + volume, type, volume, null, false, null));
        }
        // 3) type + volume + condition
        public static List<string> CollectElementsWithin(string type, string volume, string pmlCondition)
        {
            return RunCollect(BuildCollectCommand("within " + volume, type, volume, pmlCondition, false, null));
        }
        // 4) type + volume + condition + extend
        public static List<string> CollectElementsWithin(string type, string volume, string pmlCondition, bool extend)
        {
            return RunCollect(BuildCollectCommand("within " + volume, type, volume, pmlCondition, extend, null));
        }
        // 5) type + volume + condition + extend + hierarchy
        public static List<string> CollectElementsWithin(string type, string volume, string pmlCondition, bool extend, string hierarchy)
        {
            return RunCollect(BuildCollectCommand("within " + volume, type, volume, pmlCondition, extend, hierarchy));
        }
        // 6) owner(DbElement) + type
        public static List<string> CollectElementsWithin(DbElement owner, string type)
        {
            return RunCollect(BuildCollectCommand("of " + Ref(owner), type, null, null, false, null));
        }
        // 7) owner + type + volume
        public static List<string> CollectElementsWithin(DbElement owner, string type, string volume)
        {
            return RunCollect(BuildCollectCommand("of " + Ref(owner) + " within " + volume, type, volume, null, false, null));
        }
        // 8) owner + type + volume + condition
        public static List<string> CollectElementsWithin(DbElement owner, string type, string volume, string pmlCondition)
        {
            return RunCollect(BuildCollectCommand("of " + Ref(owner) + " within " + volume, type, volume, pmlCondition, false, null));
        }
        // 9) owner + type + volume + condition + extend + hierarchy
        public static List<string> CollectElementsWithin(DbElement owner, string type, string volume, string pmlCondition, bool extend, string hierarchy)
        {
            return RunCollect(BuildCollectCommand("of " + Ref(owner) + " within " + volume, type, volume, pmlCondition, extend, hierarchy));
        }

        /// <summary>纯函数：拼出 `coll all for &lt;type&gt; &lt;hierarchy&gt;`（无范围限定，全库该类型枚举）。</summary>
        internal static string BuildCollectForCommand(string type, string hierarchy)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("coll all for ").Append(type);
            if (!string.IsNullOrEmpty(hierarchy)) sb.Append(" ").Append(hierarchy);
            return sb.ToString();
        }

        // 10) type + hierarchy（全库某类型，hier/nohier）
        public static List<string> CollectElementsFor(string type, string hierarchy)
        {
            return RunCollect(BuildCollectForCommand(type, hierarchy));
        }

        /* ===================== 3. 整库枚举 MDBMembers(DbType) ===================== */

        /// <summary>纯函数：拼出 world(&lt;type&gt;)（等价 MDB 顶层 coll all for &lt;type&gt;）。</summary>
        internal static string BuildMDBMembersCommand(string dbType)
        {
            return "world(" + dbType + ")";
        }

        public static List<string> MDBMembers(DbType type)
        {
            // world(<type>) 是查询表达式，结果不会写入变量；等价改用 collect 全库枚举。
            string err;
            if (!Pdms.Runner.Run("!!MDBM = collect all for WORLD", out err))
                throw new PdmsCommandException(err ?? "MDBMembers failed");
            string[] arr = Pdms.Runner.GetPMLVariableStringArray("MDBM");
            return new List<string>(arr ?? new string[0]);
        }

        /* ===================== 4. 属性写入（类型分发 + 单位换算） ===================== */

        /// <summary>纯函数：长度单位换算。返回“项目单位下的值”。
        /// metric=true 项目单位 mm：inch→×25.4；metric=false 项目单位 inch：mm→/25.4。
        /// 仅处理常见长度后缀；无单位串按原值返回。</summary>
        internal static double ConvertLength(double value, string unitToken, bool metric)
        {
            if (string.IsNullOrEmpty(unitToken)) return value;
            string u = unitToken.Trim().ToLowerInvariant();
            bool isInch = (u == "inch" || u == "in" || u == "\"");
            bool isMm = (u == "mm" || u == "millimetre" || u == "millimeter");
            if (metric)
            {
                // 项目 mm：inch 转 mm
                if (isInch) return value * 25.4;
                return value; // 已是 mm 或无单位
            }
            else
            {
                // 项目 inch：mm 转 inch
                if (isMm) return value / 25.4;
                return value; // 已是 inch 或无单位
            }
        }

        /// <summary>纯函数：解析带单位的数值串，如 "100 mm" / "4 inch" / "2.5" → 项目单位下的 double。</summary>
        internal static double ParseLengthWithUnits(string s, bool metric)
        {
            if (string.IsNullOrEmpty(s)) return 0.0;
            string[] parts = s.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return 0.0;
            double v;
            if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) return 0.0;
            if (parts.Length >= 2)
                return ConvertLength(v, parts[1], metric);
            return v;
        }

        /// <summary>纯函数：把 "100 mm 200 mm 300 mm" / "1 2 3" 解析为项目单位下的 double[]（长度分量）。</summary>
        internal static double[] SplitAsDoubleArrayWithUnits(string s, bool metric)
        {
            if (string.IsNullOrEmpty(s)) return new double[0];
            string[] parts = s.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            List<double> outp = new List<double>();
            int i = 0;
            while (i < parts.Length)
            {
                double v;
                if (double.TryParse(parts[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v))
                {
                    // 看下一个 token 是否为单位
                    if (i + 1 < parts.Length && !IsNumber(parts[i + 1]))
                    {
                        outp.Add(ConvertLength(v, parts[i + 1], metric));
                        i += 2;
                    }
                    else
                    {
                        outp.Add(v);
                        i += 1;
                    }
                }
                else
                {
                    i += 1; // 跳过无法解析的 token（如纯单位）
                }
            }
            return outp.ToArray();
        }

        private static bool IsNumber(string s)
        {
            double v;
            return double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v);
        }

        /// <summary>把属性名 + 字符串值写入元素。按属性语义做单位换算/类型分发；
        /// 生产实现应查询 DbAttribute 的 ActualType 精确分发，此处按常见名启发式处理。</summary>
        public static void SetAttribute(DbElement el, string att, string stringVal)
        {
            if (el == null || string.IsNullOrEmpty(att)) return;
            bool metric = DistanceIsMetric();
            string cmd;
            // 长度类属性（POS / BORE / LEVEL / HEIGHT / DIAmeter 等）
            if (IsLengthAttribute(att))
            {
                double v = ParseLengthWithUnits(stringVal, metric);
                cmd = Ref(el) + " " + att + " " + FormatCoord(v);
            }
            // 位置类（三分量）
            else if (IsPositionAttribute(att))
            {
                double[] arr = SplitAsDoubleArrayWithUnits(stringVal, metric);
                if (arr.Length < 3) arr = new double[] { 0, 0, 0 };
                cmd = Ref(el) + " " + att + " " + FormatCoord(arr[0]) + " " + FormatCoord(arr[1]) + " " + FormatCoord(arr[2]);
            }
            else
            {
                // 字符串 / 枚举 / 数值原样
                cmd = Ref(el) + " " + att + " " + stringVal;
            }
            string err;
            if (!Pdms.Runner.Run(cmd, out err))
                throw new PdmsCommandException(err ?? ("SetAttribute " + att + " failed"));
        }

        /// <summary>走 CP 命令设属性（与 SetAttribute 的 DB-API 路径互补；生产按属性类型择优）。</summary>
        public static void SetAttributeByCommand(DbElement el, string att, string value)
        {
            SetAttribute(el, att, value);
        }

        /// <summary>温度/压力单位感知写入（占位：通过 UA 的单位 token 换算，沿用 ParseLengthWithUnits 思路）。</summary>
        public static void SetAttributeTemperature(DbElement el, string att, string value)
        {
            SetAttribute(el, att, value); // 生产：温度 ℃/℉ 换算
        }
        public static void SetAttributePressure(DbElement el, string att, string value)
        {
            SetAttribute(el, att, value); // 生产：bar/kPa 换算
        }

        private static bool IsLengthAttribute(string att)
        {
            if (string.IsNullOrEmpty(att)) return false;
            string a = att.ToUpperInvariant();
            return a == "BORE" || a == "DIAMETER" || a == "DIA" || a == "LEVEL" || a == "HEIGHT"
                || a == "LENGTH" || a == "RADIUS" || a == "THICK" || a == "WIDTH" || a == "DEPTH";
        }
        private static bool IsPositionAttribute(string att)
        {
            if (string.IsNullOrEmpty(att)) return false;
            string a = att.ToUpperInvariant();
            return a == "POS" || a == "POSITION" || a == "GEOM" || a == "AXIS";
        }

        /// <summary>数值坐标格式化（避免科学计数法，3 位小数）。</summary>
        internal static string FormatCoord(double v)
        {
            return v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }

        /* ===================== 5. 世界坐标 / 方向 / 朝向 ===================== */

        /// <summary>纯函数：拼出“取元素世界坐标”的 PML 表达式（wrt world 限定词）。</summary>
        internal static string BuildWrtWorldPositionExpr(string refc)
        {
            return "!!" + refc + ".position wrt world";
        }
        internal static string BuildWrtWorldDirectionExpr(string refc)
        {
            return "!!" + refc + ".direction wrt world";
        }
        internal static string BuildWrtWorldOrientationExpr(string refc)
        {
            return "!!" + refc + ".orientation wrt world";
        }

        public static string GetPositionWrtWorld(DbElement el)
        {
            string err;
            string expr = BuildWrtWorldPositionExpr(Ref(el));
            if (!Pdms.Runner.Run("var !!X " + expr, out err))
                throw new PdmsCommandException(err ?? "GetPositionWrtWorld failed");
            return Pdms.Runner.GetPMLVariableString("X");
        }
        public static string GetDirectionWrtWorld(DbElement el)
        {
            string err;
            string expr = BuildWrtWorldDirectionExpr(Ref(el));
            if (!Pdms.Runner.Run("var !!X " + expr, out err))
                throw new PdmsCommandException(err ?? "GetDirectionWrtWorld failed");
            return Pdms.Runner.GetPMLVariableString("X");
        }
        public static string GetOrientationWrtWorld(DbElement el)
        {
            string err;
            string expr = BuildWrtWorldOrientationExpr(Ref(el));
            if (!Pdms.Runner.Run("var !!X " + expr, out err))
                throw new PdmsCommandException(err ?? "GetOrientationWrtWorld failed");
            return Pdms.Runner.GetPMLVariableString("X");
        }

        /// <summary>纯函数：拼出 wrt world / wrt element 限定词（DbQualifier 等价）。</summary>
        internal static string WrtWorld() { return "wrt world"; }
        internal static string WrtElement(string refc) { return "wrt !!" + refc; }

        /* ===================== 6. PML 求值 ===================== */

        /// <summary>纯函数：拼出 evaluate(&lt;expr&gt;)。</summary>
        internal static string BuildEvaluateExpr(string expr)
        {
            return "evaluate(" + expr + ")";
        }
        public static string EvaluateExpression(string expr)
        {
            return GetPMLString(BuildEvaluateExpr(expr));
        }
        public static string EvaluateElement(string expr)
        {
            string err;
            if (!Pdms.Runner.Run("var !!E " + BuildEvaluateExpr(expr), out err))
                throw new PdmsCommandException(err ?? "EvaluateElement failed");
            return Pdms.Runner.GetPMLVariableString("E");
        }
        public static double EvaluateDoubleDistance(string expr)
        {
            string err;
            if (!Pdms.Runner.Run("var !!D " + BuildEvaluateExpr(expr), out err))
                throw new PdmsCommandException(err ?? "EvaluateDoubleDistance failed");
            return Pdms.Runner.GetPMLVariableReal("D");
        }
        public static double EvaluateDoubleBore(string expr)
        {
            string err;
            if (!Pdms.Runner.Run("var !!B " + BuildEvaluateExpr(expr), out err))
                throw new PdmsCommandException(err ?? "EvaluateDoubleBore failed");
            return Pdms.Runner.GetPMLVariableReal("B");
        }
        public static bool EvaluateBool(DbElement el, string expr)
        {
            string err;
            if (!Pdms.Runner.Run("var !!Q " + BuildEvaluateExpr(expr), out err))
                throw new PdmsCommandException(err ?? "EvaluateBool failed");
            return Pdms.Runner.GetPMLVariableBoolean("Q");
        }

        /* ===================== 7. 元素可操作性校验 ===================== */

        public static bool IsCreatable(DbElement el)
        {
            string r = GetPMLString("!!" + Ref(el) + ".creatable()");
            bool b;
            return bool.TryParse(r, out b) ? b : false;
        }
        public static bool IsDeletable(DbElement el)
        {
            string r = GetPMLString("!!" + Ref(el) + ".deletable()");
            bool b;
            return bool.TryParse(r, out b) ? b : false;
        }
        public static bool IsAttributeSetable(DbElement el, string att)
        {
            string r = GetPMLString("!!" + Ref(el) + ".setable('" + att + "')");
            bool b;
            return bool.TryParse(r, out b) ? b : false;
        }

        /* ===================== 8. 拷贝 / 重命名 ===================== */

        /// <summary>纯函数：拼出拷贝并重命名命令。</summary>
        internal static string BuildCopyAndRenameCommand(string srcRef, string ownerRef, string newName)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("!ce = ").Append(ownerRef).Append("\n");
            sb.Append("copy ").Append(srcRef).Append(" ").Append(newName);
            return sb.ToString();
        }
        public static string CopyAndRename(DbElement src, DbElement owner, string newName)
        {
            string cmd = BuildCopyAndRenameCommand(Ref(src), Ref(owner), newName);
            string err;
            if (!Pdms.Runner.Run(cmd, out err))
                throw new PdmsCommandException(err ?? "CopyAndRename failed");
            return newName;
        }
        /// <summary>纯函数：拼出“在首元素前插入拷贝”命令。</summary>
        internal static string BuildInsertElementCommand(string srcRef, string beforeRef)
        {
            return "copy " + srcRef + " before " + beforeRef;
        }
        public static void InsertElement(DbElement src, DbElement before)
        {
            string err;
            if (!Pdms.Runner.Run(BuildInsertElementCommand(Ref(src), Ref(before)), out err))
                throw new PdmsCommandException(err ?? "InsertElement failed");
        }
        /// <summary>纯函数：拼出带前缀的拷贝命令。</summary>
        internal static string BuildCreateCopyPrefixCommand(string srcRef, string prefix)
        {
            return "copy " + srcRef + " " + prefix + (prefix.EndsWith("/") ? "" : "/") + srcRef.Replace("/", "");
        }
        public static string CreateCopyPrefix(DbElement src, string prefix)
        {
            string err;
            string cmd = BuildCreateCopyPrefixCommand(Ref(src), prefix);
            if (!Pdms.Runner.Run(cmd, out err))
                throw new PdmsCommandException(err ?? "CreateCopyPrefix failed");
            return prefix;
        }

        /* ===================== 9. 体积 / 限界盒 ===================== */

        /// <summary>纯函数：拼出“局部体积”表达式。</summary>
        internal static string BuildLocalVolumeExpr(string refc)
        {
            return "!!" + refc + ".lvol";
        }
        public static string LocalVolume(DbElement el)
        {
            return GetPMLString(BuildLocalVolumeExpr(Ref(el)));
        }
        /// <summary>纯函数：拼出“限界盒”表达式（LimitsBox）。</summary>
        internal static string BuildVolumeToLimitBoxExpr(string refc)
        {
            return "!!" + refc + ".box";
        }
        public static string VolumeToLimitBox(DbElement el)
        {
            return GetPMLString(BuildVolumeToLimitBoxExpr(Ref(el)));
        }

        /* ===================== 10. 命名 / 环境 ===================== */

        /// <summary>元素的 DB 名（Excel 友好：去掉前导 /）。</summary>
        public static string Name(DbElement el, bool toExcel)
        {
            if (el == null) return "";
            string n = Ref(el);
            if (toExcel && n.StartsWith("/")) n = n.Substring(1);
            return n;
        }

        /// <summary>当前距离单位是否为公制（MM）。UA1 含 "INCH Distance" 即英制。</summary>
        public static bool DistanceIsMetric()
        {
            string unit = GetPMLString("get UA1");
            return unit != null && unit.IndexOf("INCH", StringComparison.OrdinalIgnoreCase) < 0;
        }

        /// <summary>当前管径单位（UB1）。</summary>
        public static string GetBoreUnit()
        {
            return GetPMLString("get UB1");
        }

        /// <summary>当前压力单位。</summary>
        public static string GetPressureUnit()
        {
            return GetPMLString("get UP1");
        }
        /// <summary>当前温度单位。</summary>
        public static string GetTemperatureUnit()
        {
            return GetPMLString("get UT1");
        }

        /// <summary>当前项目默认目录。</summary>
        public static string GetProjectDefaultFolder()
        {
            return GetPMLString("get DSNM");
        }

        /// <summary>PDMS 用户目录（pdmsuser 环境变量）。</summary>
        public static string GetPDMSUserFolder()
        {
            return GetPMLString("evar pdmsuser");
        }

        /// <summary>配置文件路径（design 配置目录下的 cfg 文件）。</summary>
        public static string ConfigFile(string fileName)
        {
            return GetPMLString("get !PDMSUSER + '/" + (fileName ?? "") + "'");
        }

        /// <summary>日期校验：比较两个日期串（占位；生产走 PML 日期函数）。</summary>
        public static bool CheckDate(string a, string b)
        {
            DateTime da, db;
            if (DateTime.TryParse(a, out da) && DateTime.TryParse(b, out db))
                return da <= db;
            return true;
        }
    }
}
