// drawingPDMS - Graphics（E3D Graphic 类的 PDMS Draft 移植实现）
//
// 移植说明（E3D -> PDMS）：
//   - E3D 的 DrawListManager / View3DManager 是 E3D 专属 API，PDMS 没有对应物；
//     本文件用 PDMS Draft 等价写法替换：
//        * DrawlistAdd/Remove/RemoveAll -> 模型出图清单 DLLB 的 PML 命令（dllb add/remove idli）
//        * View3DManager.GetDrawlistLimits -> 当前 DLLB 内 IDLIST 成员的模型坐标包围盒
//   - 其余方法（getPipeCollect/createLdimElement/getLimitDraw/setGridPos/setGridLabelPos
//     及辅助 getElementSide/getLdimDir/get2DPos）所用属性名/元素类型在 PDMS 中同名或近名，
//     仅做字段名映射：
//        SHXYPS -> XYPS（视图图面位置）
//        TSIZTX -> TSIZ（标注文本字号）
//        DTERID -> （PDMS 无，省略；标注文本引用保持默认）
//        AGSIDE -> 由 DIR 方向相反推导（PDMS 无此属性）
//        GRIDPL -> GRIDWLD（世界坐标网格）
//        REFGRD / GRIDELEVATION -> PDMS 无，省略对应排除条件
//   原始 E3D 逻辑见 _reference/graphicsDrawing.E3Doriginal.cs。
using Aveva.Pdms.Database;
using Aveva.Pdms.Geometry;
using Aveva.Pdms.Shared;
using System;
using System.Collections.Generic;
using ATT = Aveva.Pdms.Database.DbAttributeInstance;
using CE = Aveva.Pdms.Shared.CurrentElement;

namespace drawingPDMS
{
    public class Graphic
    {
        // 表单调用：抽取管道集合（业主的业主）
        public static List<DbElement> getPipeCollect(List<DbElement> eles)
        {
            List<DbElement> pipeCollection = new List<DbElement>();
            foreach (DbElement ele in eles)
            {
                if (ele == null || ele.IsNull) continue; // 2026-10-15 (2,202)：空引用守护
                if (ele.Owner != null && ele.Owner.Owner != null && !pipeCollection.Contains(ele.Owner.Owner))
                    pipeCollection.Add(ele.Owner.Owner);
            }
            return pipeCollection;
        }

        // (14g) 2026-11-08 user: valves and similar fittings must not be dimensioned.
        // Only equipment / grid / structure / pipe-level elements stay as chain points.
        // Evidence: 12:44 DXF bottom row y=83.46 has 57 dimension texts, 49 gaps < 8mm
        // (min 0.7mm), all from ELBOW / fitting spacings; Top and Bottom chains duplicate.
        // DimIncludeFittings=true restores the old behaviour (param dimFittings=1).
        public static bool DimIncludeFittings = false;

        /// <summary>(14g) True when the element is a fitting that must be skipped.</summary>
        public static bool IsDimFitting(DbElement ele)
        {
            if (ele == null || ele.IsNull) return false;
            string t = "";
            try { t = ele.GetElementType().ToString().ToUpperInvariant(); } catch { return false; }
            if (t.Length == 0) return false;
            string[] bad = { "VALV", "FLAN", "ELBO", "TEE", "REDU", "INST", "OLET", "GASK",
                             "BOLT", "UNIO", "CAP",  "BEND", "PCOM", "ATTA", "SUPP", "COUP",
                             "WELD", "TUBI", "VENT", "DRAI", "INLI", "BLIN", "CONN" };
            foreach (string b in bad)
                if (t.StartsWith(b, StringComparison.Ordinal)) return true;
            return false;
        }

        // 表单调用：创建立面标注元素（LDIMENSION + DPPT 标注点）
        public static void createLdimElement(DbElement layerDraw, List<DbElement> eles, String Side, Double viewMinX, Double viewMaxX, Double viewMinY, Double viewMaxY, String DrawingType)
        {
            DbElement ldimDraw = layerDraw.CreateLast(DbElementTypeInstance.LDIMENSION);
            ldimDraw.SetAttribute(ATT.DMSTRF, DbElement.GetElement("/AVEVA-DIM-1.8"));
            // E3D: drawTool.SetAttribute(ldimDraw, ATT.DTERID, "-5");  // PDMS 无 DTERID，省略
            ldimDraw.SetAttribute(ATT.TSIZ, 1.5);                       // PDMS: TSIZ（E3D 原名 TSIZTX）
            drawTool.SetAttribute(ldimDraw, ATT.PLSTYL, "8008");
            ldimDraw.SetAttribute(ATT.DIR, Direction.Create(getLdimDir(Side)));
            drawTool.SetAttribute(ldimDraw, ATT.NAME, ldimDraw.Owner.GetAsString(ATT.NAME) + "/" + Side);
            List<DbElement> eles2 = getElementSide(ldimDraw, eles, Side, viewMinX, viewMaxX, viewMinY, viewMaxY, DrawingType);
            List<DbElement> pipeCollection = getPipeCollect(eles);
            // (14k) 2026-11-08 user: pipe-level points used to be added into EVERY
// side chain unconditionally, so the same dimension run was drawn on both
// Top and Bottom (11-08 screenshot/DXF proof). Filter by quadrant, same
// rule as getElementSide (view centre THPO). Points whose position cannot
// be resolved keep the old behaviour (still added, no point loss).
            double thX14k = 0, thY14k = 0;
            try { Position thP14k = ldimDraw.Owner.Owner.GetPosition(ATT.THPO); thX14k = thP14k.X; thY14k = thP14k.Y; } catch { }
            foreach (DbElement pe14k in pipeCollection)
            {
                if (pe14k == null || pe14k.IsNull) continue;
                try
                {
                    string tn14k = pe14k.GetElementType().ToString();
                    if (tn14k == "SITE" || tn14k == "ZONE") continue;
                }
                catch { }
                double px14k, py14k;
                if (!TrySideXY(pe14k, out px14k, out py14k)) { eles2.Add(pe14k); continue; }
                if (px14k < thX14k && py14k != thY14k && Side.Contains("Left")) eles2.Add(pe14k);
                else if (py14k > thY14k && Side.Contains("Top")) eles2.Add(pe14k);
                else if (px14k > thX14k && py14k != thY14k && Side.Contains("Right")) eles2.Add(pe14k);
                else if (px14k != thX14k && py14k < thY14k && Side.Contains("Bottom")) eles2.Add(pe14k);
            }
            foreach (DbElement ele in eles2)
            {
                if (ele == null || ele.IsNull) continue; // 2026-10-15 (2,202)：空引用守护（DDNM 写空引用会退化）
                if (!DimIncludeFittings && IsDimFitting(ele)) continue; // (14g) 阀门/弯头/法兰/仪表等附件不进尺寸链
                // PDMS 无 GRIDELEVATION，省略原 REFGRD/GRIDELEVATION 排除条件
                if (ele.GetElementType() != DbElementTypeInstance.SITE && ele.GetElementType() != DbElementTypeInstance.ZONE)
                {
                    DbElement dpptDraw = ldimDraw.CreateLast(DbElementTypeInstance.DPPT);
                    if (dpptDraw == null || dpptDraw.IsNull) continue;
                    dpptDraw.SetAttribute(ATT.DDNM, ele);
                }
            }
            drawTool.CommandExcute(ldimDraw.GetAsString(ATT.NAME) + " sort dim", true);
        }

        // (14k) side-assignment position probe: WORPOS first (same convention as
// getElementSide for plain elements), WVOL bounding-box centre as fallback
// (BRANCH/PIPE level elements may lack WORPOS). Returns false if both fail.
        private static bool TrySideXY(DbElement ele, out double px, out double py)
        {
            px = 0; py = 0;
            if (ele == null || ele.IsNull) return false;
            try { Position p14k = ele.GetPosition(ATT.WORPOS); px = p14k.X; py = p14k.Y; return true; } catch { }
            try
            {
                double[] lv14k = ele.GetDoubleArray(ATT.WVOL);
                if (lv14k != null && lv14k.Length >= 6 && (lv14k[3] > lv14k[0] || lv14k[4] > lv14k[1]))
                {
                    px = (lv14k[0] + lv14k[3]) / 2.0;
                    py = (lv14k[1] + lv14k[4]) / 2.0;
                    return true;
                }
            }
            catch { }
            return false;
        }

        // 表单调用：取视图范围（图面位置 XYPS +/- 视窗尺寸 SIZE 的一半）
        // 注意：必须用视图自身的 SIZE（真实图面幅面），不能用用户输入范围 span*VSCA 推算——
        // PDMS 会按 IDLI 内容自动调整视图窗口（日志 "Limits of View may have changed"），
        // 用户输入范围与视图实际窗口可能不一致（陈永发 ViewLimitsBox2D 同款）。
        public static double[] getLimitDraw(DbElement viewDraw, double viewMinX, double viewMaxX, double viewMinY, double viewMaxY)
        {
            double[] size = viewDraw.GetDoubleArray(ATT.SIZE);
            double xMin = viewDraw.GetDoubleArray(ATT.XYPS)[0] - size[0] * 0.5;
            double yMin = viewDraw.GetDoubleArray(ATT.XYPS)[1] - size[1] * 0.5;
            double xMax = viewDraw.GetDoubleArray(ATT.XYPS)[0] + size[0] * 0.5;
            double yMax = viewDraw.GetDoubleArray(ATT.XYPS)[1] + size[1] * 0.5;
            return new double[] { xMin, yMin, xMax, yMax };
        }

        // 表单调用：设置网格位置（由 DIR 反推方位，执行 dpos 命令）
        public static void setGridPos(DbElement ldimElement)
        {
            String side = getSide(ldimElement);
            if (side == "Right")
            {
                Double x = DbElement.GetElement("/" + ldimElement.Owner.Owner.GetAsString(ATT.NAME) + "/管道标注/Right").GetPosition(ATT.DPOS).X + 10;
                Double y = ldimElement.GetDoubleArray(ATT.DPOS)[1];
                drawTool.CommandExcute(ldimElement.GetAsString(ATT.NAME) + " dpos x " + x + " y " + y, true);
                ldimElement.SetAttribute(ATT.DMSTRF, DbElement.GetElement("/AVEVA-DIM-1.8"));
            }
            if (side == "Left")
            {
                Double x = DbElement.GetElement("/" + ldimElement.Owner.Owner.GetAsString(ATT.NAME) + "/管道标注/Right").GetPosition(ATT.DPOS).X - 10;
                Double y = ldimElement.GetDoubleArray(ATT.DPOS)[1];
                drawTool.CommandExcute(ldimElement.GetAsString(ATT.NAME) + " dpos x " + x + " y " + y, true);
                ldimElement.SetAttribute(ATT.DMSTRF, DbElement.GetElement("/AVEVA-DIM-1.8"));
            }
            if (side == "Top")
            {
                Double x = ldimElement.GetDoubleArray(ATT.DPOS)[0];
                Double y = DbElement.GetElement("/" + ldimElement.Owner.Owner.GetAsString(ATT.NAME) + "/管道标注/Bottom").GetPosition(ATT.DPOS).Y + 10;
                drawTool.CommandExcute(ldimElement.GetAsString(ATT.NAME) + " dpos x " + x + " y " + y, true);
                ldimElement.SetAttribute(ATT.DMSTRF, DbElement.GetElement("/AVEVA-DIM-1.8"));
            }
            if (side == "Bottom")
            {
                Double x = ldimElement.GetDoubleArray(ATT.DPOS)[0];
                Double y = DbElement.GetElement("/" + ldimElement.Owner.Owner.GetAsString(ATT.NAME) + "/管道标注/Bottom").GetPosition(ATT.DPOS).Y - 10;
                drawTool.CommandExcute(ldimElement.GetAsString(ATT.NAME) + " dpos x " + x + " y " + y, true);
                ldimElement.SetAttribute(ATT.DMSTRF, DbElement.GetElement("/AVEVA-DIM-1.8"));
            }
        }

        // 表单调用：设置网格标注位置
        public static void setGridLabelPos(DbElement ldimLabElement, Double viewMinX, Double viewMaxX, Double viewMinY, Double viewMaxY)
        {
            String side = getSide(ldimLabElement);
            if (side == "Right")
            {
                double y = get2DPos(ldimLabElement, viewMinX, viewMaxX, viewMinY, viewMaxY)[1];
                Double x = DbElement.GetElement("/" + ldimLabElement.Owner.Owner.GetAsString(ATT.NAME) + "/管道标注/Right").GetPosition(ATT.DPOS).X + 25;
                drawTool.CommandExcute(ldimLabElement.GetAsString(ATT.NAME) + $"at x " + x + " y " + y);
            }
            if (side == "Left")
            {
                double y = get2DPos(ldimLabElement, viewMinX, viewMaxX, viewMinY, viewMaxY)[1];
                Double x = DbElement.GetElement("/" + ldimLabElement.Owner.Owner.GetAsString(ATT.NAME) + "/管道标注/Right").GetPosition(ATT.DPOS).X - 25;
                drawTool.CommandExcute(ldimLabElement.GetAsString(ATT.NAME) + $"at x " + x + " y " + y);
            }
            if (side == "Top")
            {
                double x = get2DPos(ldimLabElement, viewMinX, viewMaxX, viewMinY, viewMaxY)[0];
                Double y = DbElement.GetElement("/" + ldimLabElement.Owner.Owner.GetAsString(ATT.NAME) + "/管道标注/Bottom").GetPosition(ATT.DPOS).Y + 25;
                drawTool.CommandExcute(ldimLabElement.GetAsString(ATT.NAME) + $"at x " + x + " y " + y);
            }
            if (side == "Bottom")
            {
                double x = get2DPos(ldimLabElement, viewMinX, viewMaxX, viewMinY, viewMaxY)[0];
                Double y = DbElement.GetElement("/" + ldimLabElement.Owner.Owner.GetAsString(ATT.NAME) + "/管道标注/Bottom").GetPosition(ATT.DPOS).Y - 25;
                drawTool.CommandExcute(ldimLabElement.GetAsString(ATT.NAME) + $"at x " + x + " y " + y);
            }
        }

        // 表单调用：把元素加入出图清单（DLLB -> IDLIST -> ADDENTRY(IDNM)）
        // 纯 API 写法（E3D 原版 drawE3D.cs:158 同款）——ADD/CD 类命令对上下文挑剔
        // （实测 "This command is only available at ID Lists" / 未命名元素 CD 语法错误），
        // 直接建 ADDENTRY 写 IDNM 最稳。
        public static void DrawlistAdd(DbElement element)
        {
            try
            {
                if (element == null || element.IsNull) return;
                DbElement dllb = FindCurrentDrawlist();
                if (dllb == null || dllb.IsNull) return;
                DbElement idlist = drawE3DTool.FindOrCreateIdlist(dllb);
                if (idlist == null || idlist.IsNull) return;
                // 防重：已含指向同一元素的 ADDENTRY 就不再加
                foreach (DbElement m in idlist.Members())
                {
                    if (m.GetElementType() == DbElementTypeInstance.ADDENTRY
                        && element.Equals(m.GetElement(ATT.IDNM)))
                        return;
                }
                DbElement addEntry = idlist.CreateLast(DbElementTypeInstance.ADDENTRY);
                addEntry.SetAttribute(ATT.IDNM, element);
            }
            catch { }
        }

        // 表单调用：从出图清单移除（PDMS: DLLB REMOVE IDLI）
        public static void DrawlistRemove(DbElement element)
        {
            try
            {
                DbElement dllb = FindCurrentDrawlist();
                if (dllb == null || dllb.IsNull) return;
                DbElement liby = dllb.Owner;
                string savedPath = CE.Element.GetAsString(ATT.NAME);
                drawTool.CommandExcute("CD " + liby.GetAsString(ATT.NAME), true);
                drawTool.CommandExcute("DLLB REMOVE IDLI " + element.GetAsString(ATT.NAME), true);
                drawTool.CommandExcute("CD " + savedPath, true);
            }
            catch { }
        }

        // 表单调用：清空出图清单（PDMS: DLLB REMOVE ALL）
        public static void DrawlistRemoveAll()
        {
            try
            {
                DbElement dllb = FindCurrentDrawlist();
                if (dllb == null || dllb.IsNull) return;
                DbElement liby = dllb.Owner;
                string savedPath = CE.Element.GetAsString(ATT.NAME);
                drawTool.CommandExcute("CD " + liby.GetAsString(ATT.NAME), true);
                drawTool.CommandExcute("DLLB REMOVE ALL", true);
                drawTool.CommandExcute("CD " + savedPath, true);
            }
            catch { }
        }

        // ===== 辅助方法（PDMS Draft 写法）=====

        // 取指定方位一侧的元素集合（含世界网格）
        private static List<DbElement> getElementSide(DbElement ldimDraw, List<DbElement> eles, String Side, Double viewMinX, Double viewMaxX, Double viewMinY, Double viewMaxY, String DrawingType)
        {
            List<DbElement> newElementCollection = new List<DbElement>();
            foreach (DbElement ele in eles)
            {
                if (ele == null || ele.IsNull) continue; // 2026-10-15 (2,202)：空引用守护
                try
                {
                // 分侧中心用视图实时 THPO（视图窗口由 PDMS 按内容自动调整，
                // 用户输入范围推算的中心会分错侧）
                Double eleMidx = ldimDraw.Owner.Owner.GetPosition(ATT.THPO).X;
                Double eleMidY = ldimDraw.Owner.Owner.GetPosition(ATT.THPO).Y;
                Double eleX = ele.GetPosition(ATT.WORPOS).X;
                Double eleY = ele.GetPosition(ATT.WORPOS).Y;
                if (eleX < eleMidx && eleY != eleMidY && Side.Contains("Left")) newElementCollection.Add(ele);
                else if (eleY > eleMidY && Side.Contains("Top")) newElementCollection.Add(ele);
                else if (eleX > eleMidx && eleY != eleMidY && Side.Contains("Right")) newElementCollection.Add(ele);
                else if (eleX != eleMidx && eleY < eleMidY && Side.Contains("Bottom")) newElementCollection.Add(ele);
                // PDMS: GRIDWLD 世界坐标网格（E3D 为 GRIDPL）
                if (ele.GetElementType() == DbElementTypeInstance.GRIDWLD && !newElementCollection.Contains(ele)) newElementCollection.Add(ele);
                }
                catch { continue; } // 无 WORPOS/类型不符的元素跳过，不中断标注
            }
            Double x = 0, y = 0;
            // 视图图面边缘 = XYPS +/- SIZE/2（SIZE 是视图真实幅面；用户输入 span*VSCA
            // 会因 PDMS 按内容自动调整窗口而失真——陈永发 ViewLimitsBox2D 同款）
            DbElement ownerView = ldimDraw.Owner.Owner;
            double halfW = ownerView.GetDoubleArray(ATT.SIZE)[0] * 0.5;
            double halfH = ownerView.GetDoubleArray(ATT.SIZE)[1] * 0.5;
            if (Side.Contains("Left"))
            {
                x = ownerView.GetDoubleArray(ATT.XYPS)[0] - halfW - 10;
                y = ownerView.GetDoubleArray(ATT.XYPS)[1] - halfH;
            }
            else if (Side.Contains("Top"))
            {
                x = ownerView.GetDoubleArray(ATT.XYPS)[0] - halfW;
                y = ownerView.GetDoubleArray(ATT.XYPS)[1] + halfH + 10;
            }
            else if (Side.Contains("Right"))
            {
                x = ownerView.GetDoubleArray(ATT.XYPS)[0] + halfW + 10;
                y = ownerView.GetDoubleArray(ATT.XYPS)[1] + halfH;
            }
            else if (Side.Contains("Bottom"))
            {
                x = ownerView.GetDoubleArray(ATT.XYPS)[0] + halfW;
                y = ownerView.GetDoubleArray(ATT.XYPS)[1] - halfH - 10;
            }
            if (DrawingType == "管道布置图")
            {
                drawTool.CommandExcute(ldimDraw.GetAsString(ATT.NAME) + " dpos x " + x + " y " + y, true);
            }
            return newElementCollection;
        }

        // 方位 -> 方向记号（Left=N, Top=E, Right=S, Bottom=W）
        private static String getLdimDir(String Side)
        {
            if (Side == "Left") return "N";
            else if (Side == "Top") return "E";
            else if (Side == "Right") return "S";
            else if (Side == "Bottom") return "W";
            return "";
        }

        // 由 DIR 反推方位（PDMS 无 AGSIDE 属性）
        // 注意：PDMS Draft 只有 LDIMENSION 有 DIR 属性；SLABEL（流向箭头/MTO/范围板等
        // 符号标注）没有，GetAsString 直接抛 PdmsException（实测弹窗
        // "Element SLAB 1 ... does not have attribute DIR"）。E3D 原版读 AGSIDE 亦同理
        // 仅网格标注可用。故非标注元素一律捕获后返回空串，调用方按 side=="" 跳过。
        private static String getSide(DbElement ldimElement)
        {
            try
            {
                String dir = ldimElement.GetAsString(ATT.DIR).Trim().ToUpper();
                if (dir == "N") return "Left";
                if (dir == "E") return "Top";
                if (dir == "S") return "Right";
                if (dir == "W") return "Bottom";
                return "";
            }
            catch
            {
                return "";
            }
        }

        // 标注元素在图面上的二维坐标（由引用模型元素世界坐标换算）
        // 锚点必须用视图实时 THPO（PDMS 按 IDLI 内容自动调整窗口后 THPO 会变，
        // 用户输入范围推算的中心已失效——陈永发 SheetPosition 同款，实测正解）
        private static double[] get2DPos(DbElement element, Double viewMinX, Double viewMaxX, Double viewMinY, Double viewMaxY)
        {
            DbElement view = element.Owner.Owner;
            double gridPosY = element.GetElement(ATT.DDNM).GetPosition(ATT.WORPOS).Y;
            double midViewy = view.GetPosition(ATT.THPO).Y;
            double disy = (gridPosY - midViewy) * view.GetDouble(ATT.VSCA);
            double y = view.GetDoubleArray(ATT.XYPS)[1] + disy;
            double gridPosX = element.GetElement(ATT.DDNM).GetPosition(ATT.WORPOS).X;
            double midViewX = view.GetPosition(ATT.THPO).X;
            double disx = (gridPosX - midViewX) * view.GetDouble(ATT.VSCA);
            double x = view.GetDoubleArray(ATT.XYPS)[0] + disx;
            return new double[] { x, y };
        }

        // ===== PDMS 版 View3DManager.GetDrawlistLimits 等价实现 =====
        // 返回模型坐标包围盒 [xmin,xmax,ymin,ymax,zmin,zmax]
        public static double[] getDrawlistLimits()
        {
            double[] ds = new double[6];   // 必须长度 6，避免调用方索引越界
            try
            {
                DbElement dllb = FindCurrentDrawlist();
                if (dllb == null || dllb.IsNull) return ds;
                double xmin = double.MaxValue, xmax = double.MinValue;
                double ymin = double.MaxValue, ymax = double.MinValue;
                double zmin = double.MaxValue, zmax = double.MinValue;
                bool any = false;
                foreach (DbElement m in dllb.Members())
                {
                    if (m.GetElementType() == DbElementTypeInstance.IDLIST)
                    {
                        foreach (DbElement idli in m.Members())
                        {
                            DbElement model = idli.GetElement(ATT.DDNM);
                            if (model == null || model.IsNull) model = idli;
                            AddModelPos(model, ref any, ref xmin, ref xmax, ref ymin, ref ymax, ref zmin, ref zmax);
                        }
                    }
                    else
                    {
                        AddModelPos(m, ref any, ref xmin, ref xmax, ref ymin, ref ymax, ref zmin, ref zmax);
                    }
                }
                if (any)
                {
                    ds[0] = xmin; ds[1] = xmax; ds[2] = ymin; ds[3] = ymax; ds[4] = zmin; ds[5] = zmax;
                }
            }
            catch { }
            return ds;
        }

        private static DbElement FindCurrentDrawlist()
        {
            DbElement e = CurrentElement.Element;
            int guard = 0;
            while (e != null && !e.IsNull && e.Owner != null && !e.Owner.IsNull && guard++ < 25)
                e = e.Owner;
            return FindDllbUnder(e);
        }

        private static DbElement FindDllbUnder(DbElement root)
        {
            if (root == null || root.IsNull) return DbElement.GetElement();
            foreach (DbElement m in root.Members())
            {
                if (m.GetElementType() == DbElementTypeInstance.DLLB) return m;
                if (m.GetElementType() == DbElementTypeInstance.LIBY)
                {
                    foreach (DbElement n in m.Members())
                        if (n.GetElementType() == DbElementTypeInstance.DLLB) return n;
                }
            }
            return DbElement.GetElement();
        }

        private static void AddModelPos(DbElement model, ref bool any, ref double xmin, ref double xmax, ref double ymin, ref double ymax, ref double zmin, ref double zmax)
        {
            try
            {
                Position p = model.GetPosition(ATT.WORPOS);
                if (p.X < xmin) xmin = p.X; if (p.X > xmax) xmax = p.X;
                if (p.Y < ymin) ymin = p.Y; if (p.Y > ymax) ymax = p.Y;
                if (p.Z < zmin) zmin = p.Z; if (p.Z > zmax) zmax = p.Z;
                any = true;
            }
            catch { }
        }
    }
}
