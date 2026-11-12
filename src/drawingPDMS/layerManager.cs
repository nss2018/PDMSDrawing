using Aveva.Pdms.Database;
using System;
using System.Collections.Generic;
using ATT = Aveva.Pdms.Database.DbAttributeInstance;

namespace drawingPDMS
{
    /// <summary>
    /// 出图 5 图层（FRAME）体系：外形 / 标注线 / 文字 / 图框 / 表格。
    /// 每个生成块调用 EnsureLayers / GetLayer 取得标准图层，把元素按类别建到对应层，
    /// 实现「出图后五类元素分图层、可在 Draft/CAD 中分别开关/打印」。
    ///
    /// 归层位置约定（坐标安全）：
    ///  - 标注线 / 文字：元素本就建在 VIEW 下的 LAYER 里（LDIMENSION/GLABEL/SLABEL），
    ///    标准层建在 VIEW 下，直接改 owner 即可，坐标不变。
    ///  - 外形(中心线) / 图框 / 表格：元素建在 SHEET 下的 NOTE 里，NOTE 子元素用绝对图幅坐标，
    ///    若把 NOTE 移到 VIEW 层会双重偏移。故这三类的标准层建在 SHEET 下，NOTE 挂在层内，坐标不变。
    ///  - 模型投影几何（外形本体）来自 UpdateModelView：在 UpdateModelView 前 SET FRAM 到外形层
    ///    （best-effort，Draft 是否按当前帧放置模型几何需在 PDMS 实测）。
    /// </summary>
    public enum DrawLayerCat
    {
        Outline, // 外形：模型投影几何 + 设备中心线
        Dim,     // 标注线：尺寸标注 LDIMENSION
        Text,    // 文字：设备标注 GLABEL / 符号 SLABEL / 标题文字
        Frame,   // 图框：图纸边框
        Table    // 表格：管线一览表 / 管口方位表
    }

    public static class LayerManager
    {
        // 中文默认名 + 颜色(NLCOLO Draft 色号) + 线型(NLSTYL)
        private sealed class LayerDef
        {
            public string Name;
            public int Color;
            public string LStyle;
            public LayerDef(string n, int c, string l) { Name = n; Color = c; LStyle = l; }
        }

        private static readonly Dictionary<DrawLayerCat, LayerDef> Defs = new Dictionary<DrawLayerCat, LayerDef>
        {
            { DrawLayerCat.Outline, new LayerDef("外形", 7, "1") },
            { DrawLayerCat.Dim,     new LayerDef("标注线", 4, "1") },
            { DrawLayerCat.Text,    new LayerDef("文字", 2, "1") },
            { DrawLayerCat.Frame,   new LayerDef("图框", 1, "1") },
            { DrawLayerCat.Table,   new LayerDef("表格", 3, "1") },
        };

        // owner 的数据库 ref(唯一) -> 该 owner 下 5 类图层缓存（避免重复查找/重复建）
        private static readonly Dictionary<string, Dictionary<DrawLayerCat, DbElement>> Cache =
            new Dictionary<string, Dictionary<DrawLayerCat, DbElement>>();

        /// <summary>取得（必要时创建）某 owner 下的标准图层。owner 传 viewDraw(标注线/文字) 或 sheetDraw(外形/图框/表格)。</summary>
        public static DbElement GetLayer(DbElement owner, DrawLayerCat cat)
        {
            if (owner == null || owner.IsNull) return DbElement.GetElement();
            string key = owner.GetAsString(ATT.NAME); // 元素完整 ref 路径(未命名时为唯一 bare-ref)：同名不同图/未命名元素也各自独立，批量出图不串层
            Dictionary<DrawLayerCat, DbElement> map;
            if (!Cache.TryGetValue(key, out map))
            {
                map = new Dictionary<DrawLayerCat, DbElement>();
                Cache[key] = map;
            }
            DbElement layer;
            if (map.TryGetValue(cat, out layer) && !layer.IsNull && !layer.IsDeleted) return layer;

            LayerDef def = Defs[cat];
            // WT-DRAW benchmark: standard layer name/colour/linetype come from DrawingStandard.
            // With preset "project" every value equals Defs (zero behaviour change).
            def = new LayerDef(
                DrawingStandard.LayerName(cat, def.Name),
                DrawingStandard.LayerColor(cat, def.Color),
                DrawingStandard.LayerLStyle(cat, def.LStyle));
            // 先在 owner 下按名找已有同名 LAYER（缓存被清空或层由别处建时复用，避免重复建层）
            layer = FindLayer(owner, def.Name);
            if (layer.IsNull)
            {
                layer = owner.CreateLast(DbElementTypeInstance.LAYER);
                if (!layer.IsNull)
                {
                    drawTool.SetAttribute(layer, ATT.NAME, def.Name);   // 层显示名(中文)；若 PDMS 拒中文则退化为自动名，层对象仍被缓存复用
                    drawTool.SetAttribute(layer, ATT.GRSYS, "/PLANT_GRID");
                    drawTool.SetAttribute(layer, ATT.JUST, "left");
                    drawTool.SetAttribute(layer, ATT.ALIGN, "Hbody");
                    drawTool.SetAttribute(layer, ATT.CHEI, DrawingStandard.LayerChei());
                    drawTool.SetAttribute(layer, ATT.NLCOLO, def.Color.ToString()); // 层默认色（元素未显式指定时继承）
                    drawTool.SetAttribute(layer, ATT.NLSTYL, def.LStyle);
                }
            }
            map[cat] = layer;
            return layer;
        }

        // 在 owner 成员里找名为 name 的 LAYER（精确匹配显示名）
        private static DbElement FindLayer(DbElement owner, string name)
        {
            foreach (DbElement m in owner.Members())
            {
                if (m.GetElementType() == DbElementTypeInstance.LAYER && m.GetAsString(ATT.NAME) == name)
                    return m;
            }
            return DbElement.GetElement();
        }

        /// <summary>在 owner 下确保 5 个标准图层都存在（只建缺失的）。</summary>
        public static void EnsureLayers(DbElement owner)
        {
            if (owner == null || owner.IsNull) return;
            foreach (DrawLayerCat c in Enum.GetValues(typeof(DrawLayerCat)))
                GetLayer(owner, c);
        }

        /// <summary>
        /// 把指定类别图层设为当前帧（best-effort）。用于 UpdateModelView 前的模型几何归层（外形）。
        /// Draft 是否按当前帧放置模型几何需在 PDMS 实测；命令异常已被 drawTool 护栏吞掉不中断。
        /// </summary>
        public static void SetCurrentLayer(DbElement owner, DrawLayerCat cat)
        {
            try
            {
                DbElement layer = GetLayer(owner, cat);
                if (layer == null || layer.IsNull) return;
                drawTool.CommandExcute("FRAM " + layer.GetAsString(ATT.NAME));
            }
            catch { }
        }

        /// <summary>清空缓存（换图/重画出新 view 时调用，避免命中旧 ref）。</summary>
        public static void ResetCache()
        {
            Cache.Clear();
        }
    }
}
