// MaterialReport.cs —— 配管材料表（MTO）收集与导出
//
// 功能：
//   * CollectItems(DbElement root) —— 从 root 子树收集管材/管件明细（DB API 路径，离线返回空表）
//   * Aggregate(items)             —— 按 类型+规格+管径 分组汇总（纯函数，离线可断言）
//   * BuildMaterialWorkbookXml(...)—— 生成 SpreadsheetML（Excel 2003 XML，双 Sheet：明细+汇总）
//   * Export(root, out path, out stats) —— 编排入口：收集→汇总→生成→写文件
//
// 设计对齐（与 Pipe.cs / Utilities.cs 一致）：
//   * 命令/表达式构造集中在 internal 纯函数；AVEVA DbElement 只出现在执行层。
//   * 收集走 PML 赋值回读：`!!CELLS = collect all of <ref> for <type> hier`（hier 覆盖整棵子树，
//     与 Utilities.RunCollect 的已验证模式一致——coll 查询本身不写变量，必须赋值后回读）。
//   * 导出格式选 SpreadsheetML 而非 CSV：原生支持样式/冻结首行/多 Sheet，Excel 与 WPS 直接打开，
//     .NET 3.5 无任何第三方依赖（ ClosedXML/EPPlus 均需 .NET 4+ ）。
//
// 材料表列定义（对齐工程 MTO 惯例）：
//   明细：序号 | 类型 | 名称 | 规格 | 管径 | 长度 | 所属
//   汇总：序号 | 类型 | 规格 | 管径 | 数量 | 长度合计（含总计行）

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Aveva.Pdms.Database;

namespace Radwing.Piping
{
    /// <summary>材料表明细行。</summary>
    public sealed class MaterialItem
    {
        public string Type;     // 元素类型（TUBE/ELBO/TEE/...）
        public string Name;     // 元素 DB 名
        public string Spref;    // 规格引用（SPREF，如 /SPEC-A）
        public double Bore;     // 管径（HBOR，项目单位）
        public double Length;   // 长度（LENG，项目单位；管件为 0）
        public string Owner;    // 所属 BRANCH/PIPE 名（材料表归属列）
    }

    /// <summary>材料表汇总行（按 类型+规格+管径 分组）。</summary>
    public sealed class MaterialSummary
    {
        public string Type;
        public string Spref;
        public double Bore;
        public int Count;
        public double TotalLength;
    }

    public static class MaterialReport
    {
        /* ===================== 1. 收集范围：目标元素类型清单 ===================== */

        /// <summary>材料表统计的目标元素类型（PDMS Design 配管元件常见集合）。
        /// PIPE/BRANCH 计入以支撑“管道长度”汇总；其余为管材管件。</summary>
        internal static readonly string[] TargetTypes = new string[]
        {
            "PIPE", "BRANCH", "TUBE", "ELBO", "TEE", "BEND", "REDUCER", "CAP",
            "FLANGE", "VALVE", "GASKET", "OLET", "ATTACHMENT", "INSULATION", "WELD"
        };

        /* ===================== 2. 纯函数：XML / 数值格式化 ===================== */

        /// <summary>XML 文本转义（SpreadsheetML 的 Data 内容）。</summary>
        internal static string EscapeXml(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length + 16);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '&') sb.Append("&amp;");
                else if (c == '<') sb.Append("&lt;");
                else if (c == '>') sb.Append("&gt;");
                else if (c == '"') sb.Append("&quot;");
                else if (c == '\'') sb.Append("&apos;");
                else sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>管径展示文本：公制 "50 mm" / 英制 "2 in"；bore<=0 返回 "-"。</summary>
        internal static string FormatBore(double bore, bool metric)
        {
            if (bore <= 0.0) return "-";
            return bore.ToString("0.###", CultureInfo.InvariantCulture) + (metric ? " mm" : " in");
        }

        /// <summary>长度展示文本：跟随项目单位；len<=0 返回 "-"。</summary>
        internal static string FormatLength(double len, bool metric)
        {
            if (len <= 0.0) return "-";
            return len.ToString("0.###", CultureInfo.InvariantCulture) + (metric ? " mm" : " in");
        }

        /// <summary>汇总分组的管径键（FormatBore 的短形式：不带单位，供分组比较）。</summary>
        internal static string BoreKey(double bore)
        {
            return bore.ToString("0.###", CultureInfo.InvariantCulture);
        }

        /* ===================== 3. 纯函数：汇总 ===================== */

        /// <summary>按 类型+规格+管径 分组汇总数量与长度；保持首次出现顺序。</summary>
        internal static List<MaterialSummary> Aggregate(List<MaterialItem> items)
        {
            List<MaterialSummary> outp = new List<MaterialSummary>();
            if (items == null) return outp;
            Dictionary<string, MaterialSummary> index = new Dictionary<string, MaterialSummary>();
            foreach (MaterialItem it in items)
            {
                if (it == null) continue;
                string key = it.Type + "|" + (it.Spref ?? "") + "|" + BoreKey(it.Bore);
                MaterialSummary s;
                if (!index.TryGetValue(key, out s))
                {
                    s = new MaterialSummary();
                    s.Type = it.Type ?? "";
                    s.Spref = it.Spref ?? "";
                    s.Bore = it.Bore;
                    s.Count = 0;
                    s.TotalLength = 0.0;
                    index[key] = s;
                    outp.Add(s);
                }
                s.Count++;
                s.TotalLength += it.Length;
            }
            return outp;
        }

        /* ===================== 4. 收集执行层（DB API 路径） ===================== */

        /// <summary>纯函数：拼出“收集 owner 子树内某类型全部元素”的 PML 赋值语句。
        /// hier 关键字覆盖整棵子树（of &lt;ref&gt; 本身不含 root，root 自身由调用方另行加入）。</summary>
        internal static string BuildCollectOfCommand(string ownerRef, string type)
        {
            return "!!CELLS = collect all of " + ownerRef + " for " + type + " hier";
        }

        /// <summary>读取元素字符串属性（容错：异常/未设置返回 ""）。</summary>
        private static string ReadAttr(DbElement el, DbAttribute att)
        {
            try { return el.GetAsString(att) ?? ""; }
            catch { return ""; }
        }

        /// <summary>读取数值属性（容错：解析失败返回 0）。</summary>
        private static double ReadNumAttr(DbElement el, DbAttribute att)
        {
            string s = ReadAttr(el, att);
            double v;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0.0;
        }

        /// <summary>沿 Owner 链找最近的 BRANCH / PIPE 名（材料表“所属”列）。</summary>
        private static string OwnerChainName(DbElement el)
        {
            try
            {
                DbElement cur = el.Owner;
                int guard = 0;
                while (cur != null && !cur.IsNull && guard++ < 32)
                {
                    DbElementType t = cur.GetElementType();
                    if (t == DbElementTypeInstance.BRANCH || t == DbElementTypeInstance.PIPE)
                        return ReadAttr(cur, DbAttributeInstance.NAME);
                    cur = cur.Owner;
                }
            }
            catch { }
            return "";
        }

        /// <summary>执行一条「!!CELLS = collect ...」并回读入库（(10k) 抽取共用：scoped / 整库两路同循环）。</summary>
        private static void CollectByCommand(List<MaterialItem> items, string collectCmd)
        {
            string err;
            if (!Pdms.Runner.Run(collectCmd, out err))
                throw new PdmsCommandException(err ?? ("collect failed: " + collectCmd));
            string[] names = Pdms.Runner.GetPMLVariableStringArray("CELLS");
            if (names == null) return;
            foreach (string n in names)
            {
                if (string.IsNullOrEmpty(n)) continue;
                DbElement el = DbElement.GetElement(n);
                AddIfTarget(items, el);
            }
        }

        /// <summary>从 root 子树收集材料明细（DB API 路径）。
        /// root 自身若属目标类型也计入。离线执行器（CaptureRunner）返回空表。</summary>
        public static List<MaterialItem> CollectItems(DbElement root)
        {
            List<MaterialItem> items = new List<MaterialItem>();
            if (root == null || root.IsNull) return items;
            if (!Pdms.Runner.SupportsDbApi) return items;   // 离线测试：无 DB 可收

            // root 自身
            AddIfTarget(items, root);

            string rootRef = Utilities.Ref(root);
            if (string.IsNullOrEmpty(rootRef)) return items;

            foreach (string type in TargetTypes)
                CollectByCommand(items, BuildCollectOfCommand(rootRef, type));
            return items;
        }

        /// <summary>(10k) 整库收集（无有效 root 时）：scope 用 PML world(DESIGN)——当前 MDB 的
        /// DESIGN 库 World 子树。背景：材料表 root 传「=旧项目库号/1 当 World」跨项目不成立
        /// （本项目实测「统计范围不存在: =15332/1」），且 Draft 侧 CE 收不出管线——
        /// 缺省整库统计才是向导 /mto 表单空 root 的正确语义。</summary>
        public static List<MaterialItem> CollectItemsDesignWorld()
        {
            List<MaterialItem> items = new List<MaterialItem>();
            if (!Pdms.Runner.SupportsDbApi) return items;   // 离线测试：无 DB 可收
            foreach (string type in TargetTypes)
                CollectByCommand(items, "!!CELLS = collect all within world(DESIGN) for " + type + " hier");
            return items;
        }

        /// <summary>元素属目标类型则构造明细行加入（容错跳过无效元素）。</summary>
        private static void AddIfTarget(List<MaterialItem> items, DbElement el)
        {
            if (el == null || el.IsNull) return;
            try
            {
                DbElementType t = el.GetElementType();
                string typeName = TypeNameOf(t);
                if (typeName == null) return;
                MaterialItem it = new MaterialItem();
                it.Type = typeName;
                it.Name = ReadAttr(el, DbAttributeInstance.NAME);
                it.Spref = ReadAttr(el, DbAttributeInstance.SPRE);
                it.Bore = ReadNumAttr(el, DbAttributeInstance.HBOR);
                it.Length = ReadNumAttr(el, DbAttributeInstance.LENG);
                it.Owner = OwnerChainName(el);
                items.Add(it);
            }
            catch { }
        }

        /// <summary>DbElementType → 材料表类型名；非目标类型返回 null。
        /// 注意：DbElementType 是类（非枚举），静态字段不能做 switch case，用 == 链比较
        /// （DbElementType 重载了 op_Equality，与 Pipe.cs 的判定方式一致）。</summary>
        internal static string TypeNameOf(DbElementType t)
        {
            if (t == null) return null;
            if (t == DbElementTypeInstance.PIPE) return "PIPE";
            if (t == DbElementTypeInstance.BRANCH) return "BRANCH";
            if (t == DbElementTypeInstance.TUBE) return "TUBE";
            if (t == DbElementTypeInstance.ELBOW) return "ELBOW";
            if (t == DbElementTypeInstance.TEE) return "TEE";
            if (t == DbElementTypeInstance.BEND) return "BEND";
            if (t == DbElementTypeInstance.REDUCER) return "REDUCER";
            if (t == DbElementTypeInstance.CAP) return "CAP";
            if (t == DbElementTypeInstance.FLANGE) return "FLANGE";
            if (t == DbElementTypeInstance.VALVE) return "VALVE";
            if (t == DbElementTypeInstance.GASKET) return "GASKET";
            if (t == DbElementTypeInstance.OLET) return "OLET";
            if (t == DbElementTypeInstance.ATTACHMENT) return "ATTACHMENT";
            if (t == DbElementTypeInstance.INSULATION) return "INSULATION";
            if (t == DbElementTypeInstance.WELD) return "WELD";
            return null;
        }

        /* ===================== 5. 纯函数：SpreadsheetML 生成 ===================== */

        /// <summary>明细表列宽（与列定义一致：序号/类型/名称/规格/管径/长度/所属）。</summary>
        private static readonly double[] DetailWidths = new double[] { 46, 72, 150, 130, 70, 90, 150 };
        /// <summary>汇总表列宽（序号/类型/规格/管径/数量/长度合计）。</summary>
        private static readonly double[] SummaryWidths = new double[] { 46, 72, 130, 70, 56, 100 };

        internal const string DetailSheetName = "材料明细";
        internal const string SummarySheetName = "汇总统计";

        /// <summary>生成完整 SpreadsheetML（Excel 2003 XML，双 Sheet）。</summary>
        internal static string BuildMaterialWorkbookXml(string title, string scopeText,
            List<MaterialItem> items, List<MaterialSummary> summaries, bool metric, string exportedAt)
        {
            StringBuilder x = new StringBuilder(1 << 16);
            x.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            x.Append("<?mso-application progid=\"Excel.Sheet\"?>\n");
            x.Append("<Workbook xmlns=\"urn:schemas-microsoft-com:office:spreadsheet\" "
                   + "xmlns:o=\"urn:schemas-microsoft-com:office:office\" "
                   + "xmlns:x=\"urn:schemas-microsoft-com:office:excel\" "
                   + "xmlns:ss=\"urn:schemas-microsoft-com:office:spreadsheet\">\n");
            AppendStyles(x);
            AppendDetailSheet(x, title, scopeText, items, metric, exportedAt);
            AppendSummarySheet(x, title, scopeText, summaries, metric);
            x.Append("</Workbook>\n");
            return x.ToString();
        }

        private static void AppendStyles(StringBuilder x)
        {
            x.Append(" <Styles>\n");
            x.Append("  <Style ss:ID=\"Default\" ss:Name=\"Normal\"><Alignment ss:Vertical=\"Center\"/>"
                   + "<Font ss:FontName=\"微软雅黑\" ss:Size=\"10\"/></Style>\n");
            x.Append("  <Style ss:ID=\"title\"><Font ss:Bold=\"1\" ss:Size=\"14\" ss:Color=\"#2F4F4A\"/>"
                   + "<Alignment ss:Vertical=\"Center\"/></Style>\n");
            x.Append("  <Style ss:ID=\"meta\"><Font ss:Size=\"9\" ss:Color=\"#808080\"/></Style>\n");
            x.Append("  <Style ss:ID=\"hdr\"><Font ss:Bold=\"1\" ss:Color=\"#FFFFFF\"/>"
                   + "<Interior ss:Color=\"#2F4F4A\" ss:Pattern=\"Solid\"/>"
                   + "<Alignment ss:Horizontal=\"Center\" ss:Vertical=\"Center\"/>"
                   + "<Borders><Border ss:Position=\"Bottom\" ss:LineStyle=\"Continuous\" ss:Weight=\"1\"/></Borders></Style>\n");
            const string box = "<Borders>"
                + "<Border ss:Position=\"Bottom\" ss:LineStyle=\"Continuous\" ss:Weight=\"1\"/>"
                + "<Border ss:Position=\"Left\" ss:LineStyle=\"Continuous\" ss:Weight=\"1\"/>"
                + "<Border ss:Position=\"Right\" ss:LineStyle=\"Continuous\" ss:Weight=\"1\"/>"
                + "<Border ss:Position=\"Top\" ss:LineStyle=\"Continuous\" ss:Weight=\"1\"/>"
                + "</Borders>";
            x.Append("  <Style ss:ID=\"cell\">" + box + "</Style>\n");
            x.Append("  <Style ss:ID=\"num\">" + box
                   + "<NumberFormat ss:Format=\"#,##0.###\"/><Alignment ss:Horizontal=\"Right\"/></Style>\n");
            x.Append("  <Style ss:ID=\"int\">" + box
                   + "<NumberFormat ss:Format=\"#,##0\"/><Alignment ss:Horizontal=\"Right\"/></Style>\n");
            x.Append("  <Style ss:ID=\"sumrow\"><Font ss:Bold=\"1\"/>"
                   + "<Interior ss:Color=\"#EDF3F1\" ss:Pattern=\"Solid\"/>" + box + "</Style>\n");
            x.Append("  <Style ss:ID=\"total\"><Font ss:Bold=\"1\" ss:Color=\"#FFFFFF\"/>"
                   + "<Interior ss:Color=\"#2F4F4A\" ss:Pattern=\"Solid\"/>" + box + "</Style>\n");
            x.Append(" </Styles>\n");
        }

        private static void AppendDetailSheet(StringBuilder x, string title, string scopeText,
            List<MaterialItem> items, bool metric, string exportedAt)
        {
            int rows = 3 + (items == null ? 0 : items.Count);   // 标题 + 元信息 + 表头 + 明细
            x.Append(" <Worksheet ss:Name=\"" + DetailSheetName + "\">\n");
            x.Append("  <Table ss:ExpandedColumnCount=\"7\" ss:ExpandedRowCount=\"" + rows
                   + "\" x:FullColumns=\"1\" x:FullRows=\"1\">\n");
            AppendColumns(x, DetailWidths);
            // 标题行（跨 7 列合并）
            x.Append("   <Row ss:Height=\"24\"><Cell ss:StyleID=\"title\" ss:MergeAcross=\"6\">"
                   + "<Data ss:Type=\"String\">" + EscapeXml(title) + "</Data></Cell></Row>\n");
            // 元信息行
            x.Append("   <Row ss:Height=\"16\"><Cell ss:StyleID=\"meta\" ss:MergeAcross=\"6\">"
                   + "<Data ss:Type=\"String\">范围: " + EscapeXml(scopeText)
                   + "  导出: " + EscapeXml(exportedAt)
                   + "  单位: " + (metric ? "mm" : "inch") + "</Data></Cell></Row>\n");
            // 表头
            x.Append("   <Row ss:Height=\"20\">");
            string[] headers = new string[] { "序号", "类型", "名称", "规格", "管径", "长度", "所属" };
            for (int i = 0; i < headers.Length; i++)
                x.Append("<Cell ss:StyleID=\"hdr\"><Data ss:Type=\"String\">" + headers[i] + "</Data></Cell>");
            x.Append("</Row>\n");
            // 明细行
            if (items != null)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    MaterialItem it = items[i];
                    x.Append("   <Row>");
                    x.Append("<Cell ss:StyleID=\"int\"><Data ss:Type=\"Number\">" + (i + 1) + "</Data></Cell>");
                    x.Append("<Cell ss:StyleID=\"cell\"><Data ss:Type=\"String\">" + EscapeXml(it.Type) + "</Data></Cell>");
                    x.Append("<Cell ss:StyleID=\"cell\"><Data ss:Type=\"String\">" + EscapeXml(it.Name) + "</Data></Cell>");
                    x.Append("<Cell ss:StyleID=\"cell\"><Data ss:Type=\"String\">" + EscapeXml(it.Spref) + "</Data></Cell>");
                    x.Append("<Cell ss:StyleID=\"cell\"><Data ss:Type=\"String\">" + EscapeXml(FormatBore(it.Bore, metric)) + "</Data></Cell>");
                    string lenText = it.Length > 0.0
                        ? it.Length.ToString("0.###", CultureInfo.InvariantCulture)
                        : "";
                    if (lenText.Length > 0)
                        x.Append("<Cell ss:StyleID=\"num\"><Data ss:Type=\"Number\">" + lenText + "</Data></Cell>");
                    else
                        x.Append("<Cell ss:StyleID=\"cell\"><Data ss:Type=\"String\">-</Data></Cell>");
                    x.Append("<Cell ss:StyleID=\"cell\"><Data ss:Type=\"String\">" + EscapeXml(it.Owner) + "</Data></Cell>");
                    x.Append("</Row>\n");
                }
            }
            x.Append("  </Table>\n");
            AppendFreezePanes(x, 3);
            x.Append(" </Worksheet>\n");
        }

        private static void AppendSummarySheet(StringBuilder x, string title, string scopeText,
            List<MaterialSummary> summaries, bool metric)
        {
            int count = summaries == null ? 0 : summaries.Count;
            int rows = 3 + count + 1;   // 标题 + 元信息 + 表头 + 汇总行 + 总计行
            x.Append(" <Worksheet ss:Name=\"" + SummarySheetName + "\">\n");
            x.Append("  <Table ss:ExpandedColumnCount=\"6\" ss:ExpandedRowCount=\"" + rows
                   + "\" x:FullColumns=\"1\" x:FullRows=\"1\">\n");
            AppendColumns(x, SummaryWidths);
            x.Append("   <Row ss:Height=\"24\"><Cell ss:StyleID=\"title\" ss:MergeAcross=\"5\">"
                   + "<Data ss:Type=\"String\">" + EscapeXml(title) + " — 汇总</Data></Cell></Row>\n");
            x.Append("   <Row ss:Height=\"16\"><Cell ss:StyleID=\"meta\" ss:MergeAcross=\"5\">"
                   + "<Data ss:Type=\"String\">范围: " + EscapeXml(scopeText) + "  单位: "
                   + (metric ? "mm" : "inch") + "</Data></Cell></Row>\n");
            x.Append("   <Row ss:Height=\"20\">");
            string[] headers = new string[] { "序号", "类型", "规格", "管径", "数量", "长度合计" };
            for (int i = 0; i < headers.Length; i++)
                x.Append("<Cell ss:StyleID=\"hdr\"><Data ss:Type=\"String\">" + headers[i] + "</Data></Cell>");
            x.Append("</Row>\n");
            int totalCount = 0;
            double totalLen = 0.0;
            for (int i = 0; i < count; i++)
            {
                MaterialSummary s = summaries[i];
                totalCount += s.Count;
                totalLen += s.TotalLength;
                x.Append("   <Row>");
                x.Append("<Cell ss:StyleID=\"int\"><Data ss:Type=\"Number\">" + (i + 1) + "</Data></Cell>");
                x.Append("<Cell ss:StyleID=\"cell\"><Data ss:Type=\"String\">" + EscapeXml(s.Type) + "</Data></Cell>");
                x.Append("<Cell ss:StyleID=\"cell\"><Data ss:Type=\"String\">" + EscapeXml(s.Spref) + "</Data></Cell>");
                x.Append("<Cell ss:StyleID=\"cell\"><Data ss:Type=\"String\">" + EscapeXml(FormatBore(s.Bore, metric)) + "</Data></Cell>");
                x.Append("<Cell ss:StyleID=\"int\"><Data ss:Type=\"Number\">" + s.Count + "</Data></Cell>");
                x.Append("<Cell ss:StyleID=\"num\"><Data ss:Type=\"Number\">"
                       + s.TotalLength.ToString("0.###", CultureInfo.InvariantCulture) + "</Data></Cell>");
                x.Append("</Row>\n");
            }
            // 总计行
            x.Append("   <Row>");
            x.Append("<Cell ss:StyleID=\"total\"><Data ss:Type=\"String\">合计</Data></Cell>");
            x.Append("<Cell ss:StyleID=\"total\"><Data ss:Type=\"String\"></Data></Cell>");
            x.Append("<Cell ss:StyleID=\"total\"><Data ss:Type=\"String\"></Data></Cell>");
            x.Append("<Cell ss:StyleID=\"total\"><Data ss:Type=\"String\"></Data></Cell>");
            x.Append("<Cell ss:StyleID=\"total\"><Data ss:Type=\"Number\">" + totalCount + "</Data></Cell>");
            x.Append("<Cell ss:StyleID=\"total\"><Data ss:Type=\"Number\">"
                   + totalLen.ToString("0.###", CultureInfo.InvariantCulture) + "</Data></Cell>");
            x.Append("</Row>\n");
            x.Append("  </Table>\n");
            x.Append(" </Worksheet>\n");
        }

        private static void AppendColumns(StringBuilder x, double[] widths)
        {
            for (int i = 0; i < widths.Length; i++)
                x.Append("   <Column ss:Width=\"" + widths[i].ToString("0", CultureInfo.InvariantCulture) + "\"/>\n");
        }

        private static void AppendFreezePanes(StringBuilder x, int headerRows)
        {
            x.Append("  <WorksheetOptions xmlns=\"urn:schemas-microsoft-com:office:excel\">\n");
            x.Append("   <FreezePanes/>\n   <FrozenNoSplit/>\n");
            x.Append("   <SplitHorizontal>" + headerRows + "</SplitHorizontal>\n");
            x.Append("   <TopRowBottomPane>" + headerRows + "</TopRowBottomPane>\n");
            x.Append("   <ActivePane>" + (headerRows + 1) + "</ActivePane>\n");
            x.Append("  </WorksheetOptions>\n");
        }

        /* ===================== 6. 导出入口 ===================== */

        /// <summary>统计摘要文本（日志 / 弹窗用）。</summary>
        internal static string BuildStatsText(int itemCount, List<MaterialSummary> summaries, bool metric)
        {
            int totalQty = 0;
            double totalLen = 0.0;
            if (summaries != null)
                foreach (MaterialSummary s in summaries) { totalQty += s.Count; totalLen += s.TotalLength; }
            return "共 " + itemCount + " 项 / " + (summaries == null ? 0 : summaries.Count)
                + " 种规格组合 / 数量合计 " + totalQty + " / 长度合计 "
                + FormatLength(totalLen, metric);
        }

        /// <summary>编排入口：root 子树收集材料 → 汇总 → 生成 SpreadsheetML → 写文件。
        /// 返回文件路径；stats 带回统计摘要。离线执行器返回空表文件（结构仍完整可校验）。</summary>
        public static void Export(DbElement root, out string path, out string stats)
        {
            bool metric = Utilities.DistanceIsMetric();
            List<MaterialItem> items = CollectItems(root);
            List<MaterialSummary> summaries = Aggregate(items);
            string scopeText = (root != null && !root.IsNull) ? Utilities.Ref(root) : "(当前元素)";
            path = WriteWorkbook(items, summaries, scopeText, metric);
            stats = BuildStatsText(items.Count, summaries, metric);
        }

        /// <summary>(10k) 整库导出：无有效统计范围时全 DESIGN 库收集 → 汇总 → 写文件。</summary>
        public static void ExportDesignWorld(out string path, out string stats)
        {
            bool metric = Utilities.DistanceIsMetric();
            List<MaterialItem> items = CollectItemsDesignWorld();
            List<MaterialSummary> summaries = Aggregate(items);
            path = WriteWorkbook(items, summaries, "world(DESIGN)", metric);
            stats = BuildStatsText(items.Count, summaries, metric);
        }

        /// <summary>汇总 → 生成 SpreadsheetML → 写 .xls（UTF-8 BOM 保证中文表头）。返回文件路径。</summary>
        private static string WriteWorkbook(List<MaterialItem> items, List<MaterialSummary> summaries,
            string scopeText, bool metric)
        {
            string title = "配管材料表 (MTO)";
            string xml = BuildMaterialWorkbookXml(title, scopeText, items, summaries, metric,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));

            string dir = System.IO.Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
            if (string.IsNullOrEmpty(dir) || dir.IndexOf("://") >= 0)
                dir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string path = System.IO.Path.Combine(dir, "RadwingPiping_MTO_"
                + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".xls");
            // UTF-8 BOM：确保 Excel 正确识别中文表头
            System.IO.File.WriteAllText(path, xml, new System.Text.UTF8Encoding(true));
            return path;
        }
    }
}
