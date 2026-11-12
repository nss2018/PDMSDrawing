// Decompiled with JetBrains decompiler
// Type: FangYuan.PDMS.drawE3DTool
// Assembly: FangYuanPDMS, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 59AF7F63-90CC-4428-89DA-25304C180540
// Assembly location: D:\AVEVA\Plant\PDMS12.1.SP4\FangYuanPDMS.dll


using Aveva.Pdms.Database;
using Aveva.PDMS.Database.Filters;
using Aveva.Pdms.Geometry;
using Aveva.Pdms.Shared;
using Aveva.Pdms.Utilities.CommandLine;
using drawingPDMS;
using System;
using System.Collections.Generic;
using System.Data;
using System.Windows.Forms;


namespace drawingPDMS
{
    public class drawE3DTool
    {
        public static string ElementColumn = "Element";
        public static string NpptColumn = "Nppt";
        public static string PlineKeyColumn = "PlineKey";
        public static string PositionColumn = "Position";
        public static string BeforeAfterColumn = "BeforeAfter";

        public static Position2D SheetPosition(Position pos, DbElement view)
        {
            double num1 = view.GetDouble(DbAttributeInstance.ADEG);
            double[] doubleArray1 = view.GetDoubleArray(DbAttributeInstance.XYPS);
            double[] doubleArray2 = view.GetDoubleArray(DbAttributeInstance.SIZE);
            Position position1 = view.GetPosition(DbAttributeInstance.THPO);
            double num2 = view.GetDouble(DbAttributeInstance.VSCA);
            Direction direction1 = view.GetDirection(DbAttributeInstance.DIR);
            Position2D position2D = Position2D.Create();
            if (direction1 == Direction.Create("d"))
            {
                double num3 = (pos.X - position1.X) * num2;
                double num4 = (pos.Y - position1.Y) * num2;
                if (num1 == 0.0)
                {
                    position2D.X = doubleArray1[0] + num3;
                    position2D.Y = doubleArray1[1] + num4;
                }
                else if (num1 == 90.0)
                {
                    position2D.X = doubleArray1[0] - num4;
                    position2D.Y = doubleArray1[1] + num3;
                }
                else if (num1 == 180.0)
                {
                    position2D.X = doubleArray1[0] - num3;
                    position2D.Y = doubleArray1[1] - num4;
                }
                else if (num1 == 270.0 || num1 == -90.0)
                {
                    position2D.X = doubleArray1[0] + num4;
                    position2D.Y = doubleArray1[1] - num3;
                }
            }
            else
            {
                Position pos1 = Plane.Create(direction1, position1).Near(pos);
                position2D.Y = (pos1.Z - position1.Z) * num2;
                Direction direction2 = view.GetDirection(DbAttributeInstance.DIR);
                direction2.RotateAbout(Direction.Create("up"), 90.0);
                Position position2 = Position.Create(position1.X, position1.Y, position1.Z);
                position2.MoveBy(direction2, doubleArray2[0] * 0.5 / num2);
                Direction direction3 = view.GetDirection(DbAttributeInstance.DIR);
                direction3.RotateAbout(Direction.Create("up"), -90.0);
                Position position3 = Position.Create(position1.X, position1.Y, position1.Z);
                position3.MoveBy(direction3, doubleArray2[0] * 0.5 / num2);
                Position position4 = Line.Create(position2, position3).NearestPoint(pos1);
                double num5 = position4.Distance(position1);
                position2D.X = position4.Distance(position2) >= position4.Distance(position3) ? doubleArray1[0] + num5 : doubleArray1[0] - num5;
            }
            return position2D;
        }

        public static bool[,] ViewPoint(DbElement view)
        {
            double[] doubleArray = view.Owner.GetDoubleArray(DbAttributeInstance.SIZE);
            bool[,] flagArray = new bool[Convert.ToInt32(doubleArray[0]), Convert.ToInt32(doubleArray[1])];
            for (int index1 = 0; index1 < Convert.ToInt32(doubleArray[0]); ++index1)
            {
                for (int index2 = 0; index2 < Convert.ToInt32(doubleArray[1]); ++index2)
                    flagArray[index1, index2] = false;
            }
            LimitsBox2D limitsBox2D = drawE3DTool.LimitsBox(view);
            for (int int32_1 = Convert.ToInt32(limitsBox2D.Minimum.X); int32_1 < Convert.ToInt32(limitsBox2D.Maximum.X); ++int32_1)
            {
                for (int int32_2 = Convert.ToInt32(limitsBox2D.Minimum.Y); int32_2 < Convert.ToInt32(limitsBox2D.Maximum.Y); ++int32_2)
                    flagArray[int32_1, int32_2] = true;
            }
            return flagArray;
        }

        public static bool[,] PointNotAvailable(DbElement view, List<DbElement> colls, bool[,] points)
        {
            LimitsBox2D limitsBox2D = drawE3DTool.LimitsBox(view);
            foreach (DbElement coll in colls)
            {
                double[] doubleArray = coll.GetDoubleArray(DbAttributeInstance.WVOL);
                if (doubleArray.Length != 0)
                {
                    LimitsBox limitsBox = Aveva.Pdms.Geometry.LimitsBox.Create(Position.Create(doubleArray[0], doubleArray[1], doubleArray[2]), Position.Create(doubleArray[3], doubleArray[4], doubleArray[5]));
                    Position2D position2D1 = drawE3DTool.SheetPosition(limitsBox.Minimum, view);
                    Position2D position2D2 = drawE3DTool.SheetPosition(limitsBox.Maximum, view);
                    int int32_1 = Convert.ToInt32(Math.Ceiling(position2D1.X));
                    int int32_2 = Convert.ToInt32(Math.Ceiling(position2D1.Y));
                    int int32_3 = Convert.ToInt32(Math.Ceiling(position2D2.X));
                    int int32_4 = Convert.ToInt32(Math.Ceiling(position2D2.Y));
                    for (int index1 = int32_1; index1 <= int32_3; ++index1)
                    {
                        for (int index2 = int32_2; index2 <= int32_4; ++index2)
                        {
                            if ((double)index1 > limitsBox2D.Minimum.X && (double)index1 < limitsBox2D.Maximum.X && (double)index2 > limitsBox2D.Minimum.Y && (double)index2 < limitsBox2D.Maximum.Y)
                                points[index1, index2] = false;
                        }
                    }
                }
            }
            return points;
        }

        public static List<DbElement> ElementsFromDrawlist(
          DbElement view,
          TypeFilter filter,
          bool box,
          bool spref)
        {
            LimitsBox limitsBox = drawE3DTool.ViewLimitBox(view);
            List<DbElement> dbElementList = new List<DbElement>();
            DbQualifier qualifier = new DbQualifier();
            qualifier.wrtQualifier = DbElement.GetElement("/*");
            DbElement element1 = view.GetElement(DbAttributeInstance.IDLN);
            // IDLN 未绑定（视图刚建/绑定失败）时后续集合会抛异常，直接返回空清单
            if (element1 == null || element1.IsNull) return dbElementList;
            TypeFilter filt1 = new TypeFilter(DbElementTypeInstance.ADDENTRY);
            foreach (DbElement dbElement1 in new DBElementCollection(element1, (BaseFilter)filt1))
            {
                DbElement element2 = dbElement1.GetElement(DbAttributeInstance.IDNM);
                try
                {
                    if (!element2.IsNull && !element2.IsDeleted)
                    {
                        foreach (DbElement dbElement2 in new DBElementCollection(element2, (BaseFilter)filter))
                        {
                            if (!(dbElement2.ToString() == "") && (!spref || !(dbElement2.GetAsString(DbAttributeInstance.SPRE).ToUpper() == "NULREF")))
                            {
                                if (box)
                                {
                                    double[] doubleArray = dbElement2.GetDoubleArray(DbAttributeInstance.WVOL);
                                    if (doubleArray.Length != 0)
                                    {
                                        LimitsBox box1 = Aveva.Pdms.Geometry.LimitsBox.Create(Position.Create(doubleArray[0], doubleArray[1], doubleArray[2]), Position.Create(doubleArray[3], doubleArray[4], doubleArray[5]));
                                        if (!limitsBox.Intersects(box1))
                                            continue;
                                    }
                                    else
                                        continue;
                                }
                                else if(DbAttributeInstance.POS.IsValid)
                                {
                                    Position position = dbElement2.GetPosition(DbAttributeInstance.POS, qualifier);
                                    if (!limitsBox.ContainsPoint(position))
                                        continue;
                                }
                                dbElementList.Add(dbElement2);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // 2026-10-17：MessageBox.Show 会按坏条目数量弹模态框（UI 线程被阻塞、用户要连点确定），
                    // 桥接无人值守 op 场景不可接受——改为写桥接日志继续收集。
                    BridgeLog.Write("AddEntry收集错误(静默跳过): " + element2 + " [" + ex.GetType().Name + "] " + ex.Message);
                }
            }
            TypeFilter filt2 = new TypeFilter(DbElementTypeInstance.REMENTRY);
            foreach (DbElement dbElement3 in new DBElementCollection(element1, (BaseFilter)filt2))
            {
                DbElement element3 = dbElement3.GetElement(DbAttributeInstance.IDNM);
                try
                {
                    if (!element3.IsNull && !element3.IsDeleted)
                    {
                        foreach (DbElement dbElement4 in new DBElementCollection(element3, (BaseFilter)filter))
                        {
                            if (dbElementList.Contains(dbElement4))
                                dbElementList.Remove(dbElement4);
                        }
                    }
                }
                catch (Exception ex)
                {
                    int num = (int)MessageBox.Show(element3.ToString() + ex.Message, "RemoveEntry收集错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                }
            }
            return dbElementList;
        }

        public static DbElement CreateLayer(
          DbElement owner,
          string Name,
          string config,
          DataTable settings)
        {
            string asString = owner.GetAsString(DbAttributeInstance.NAME);
            string str = asString + Name;
            DbElement element = DbElement.GetElement(str);
            if (element.IsNull)
            {
                try
                {
                    element = owner.Create(1, DbElementTypeInstance.LAYER);
                    element.SetAttribute(DbAttributeInstance.NAME, str);
                    bool flag = false;
                    for (int index = 0; index < settings.Rows.Count; ++index)
                    {
                        if (settings.Rows[index]["Config"].ToString() == config)
                        {
                            flag = true;
                            DbAttribute dbAttribute = DbAttribute.GetDbAttribute(settings.Rows[index]["Attribute"].ToString());
                            if (element.IsAttributeValid(dbAttribute))
                                usefulTool.SetAttribute(element, dbAttribute, settings.Rows[index]["Value"].ToString());
                        }
                        else if (flag)
                            break;
                    }
                }
                catch
                {
                    int num = (int)MessageBox.Show(asString + "下不能正常创建Layer.", "Error");
                }
            }
            else if (element.GetInteger(DbAttributeInstance.MCOU) != 0 && usefulTool.IsDeletable(element.Member(1), true))
            {
                foreach (DbElement member in element.Members())
                    member.Delete();
            }
            return element;
        }

        public static DbElement CreateLdim(DbElement owner, string Name, Direction dir)
        {
            string asString = owner.GetAsString(DbAttributeInstance.NAME);
            DbElement ldim = DbElement.GetElement();
            string str = "";
            if (Name != "")
            {
                str = asString + Name;
                ldim = DbElement.GetElement(str);
            }
            if (ldim.IsNull)
            {
                if (owner.GetElementType() == DbElementTypeInstance.LAYER)
                    ldim = owner.Create(1, DbElementTypeInstance.LDIMENSION);
                else if (owner.GetElementType() == DbElementTypeInstance.LDIMENSION)
                    ldim = owner.CreateAfter(DbElementTypeInstance.LDIMENSION);
                if (Name != "")
                    ldim.SetAttribute(DbAttributeInstance.NAME, str);
                if (dir != (Direction)null)
                {
                    try
                    {
                        ldim.SetAttribute(DbAttributeInstance.DIR, dir);
                    }
                    catch
                    {
                        int num = (int)MessageBox.Show("Ldim方向设置错误，dir=" + dir.ToString(), "创建LDim层次错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                        return DbElement.GetElement();
                    }
                }
            }
            else if (ldim.GetInteger(DbAttributeInstance.MCOU) != 0 && usefulTool.IsDeletable(ldim.Member(1), true))
            {
                foreach (DbElement member in ldim.Members())
                    member.Delete();
            }
            return ldim;
        }
        
        public static DataTable DimensionDataTable()
        {
            return new DataTable()
            {
                Columns = {
        {
          drawE3DTool.ElementColumn,
          typeof (DbElement)
        },
        {
          drawE3DTool.NpptColumn,
          typeof (int)
        },
        drawE3DTool.PlineKeyColumn,
        {
          drawE3DTool.PositionColumn,
          typeof (Position)
        },
        drawE3DTool.BeforeAfterColumn
      }
            };
        }

        public static void Dimension(DbElement ldim, DataTable dt, string side, double clearanceBase)
        {
            DbElement element1 = ldim.GetElement(DbAttributeInstance.PARENT, DbElementTypeInstance.VIEW);
            Position.Create();
            DbElement.GetElement();
            string str1 = "";
            DbElement element2 = DbElement.GetElement();
            Position2D position2D = Position2D.Create();
            string str2 = "开始标注";
            try
            {
                for (int index = 0; index < dt.Rows.Count; ++index)
                {
                    double num1 = 0.0;
                    DbElement dbElement = (DbElement)dt.Rows[index][drawE3DTool.ElementColumn];
                    Position pos = !(dt.Rows[index][drawE3DTool.PositionColumn].ToString() != "") ? Position.Create() : (Position)dt.Rows[index][drawE3DTool.PositionColumn];
                    int num2 = !(dt.Rows[index]["Nppt"].ToString() != "") ? -1 : (int)dt.Rows[index]["Nppt"];
                    string str3 = dt.Rows[index][drawE3DTool.BeforeAfterColumn].ToString();
                    str1 = dt.Rows[index][drawE3DTool.PlineKeyColumn].ToString();
                    if (dt.Rows[index][drawE3DTool.PositionColumn].ToString() != "")
                    {
                        str2 = "开始标注position";
                        element2 = ldim.Create(index + 1, DbElementTypeInstance.DPOINT);
                        element2.SetAttribute(DbAttributeInstance.POS, pos);
                    }
                    else if (str3 != "")
                    {
                        str2 = "开始标注beforeAfter";
                        element2 = ldim.Create(index + 1, DbElementTypeInstance.DPBA);
                        element2.SetAttribute(DbAttributeInstance.DDNM, dbElement);
                        CurrentElement.Element = element2;
                        Aveva.Pdms.Utilities.CommandLine.Command.CreateCommand("bain " + str3).RunInPdms();
                        num1 = (!(side == "Up") && !(side == "Down") ? element2.GetDouble(DbAttributeInstance.DDNMX) : element2.GetDouble(DbAttributeInstance.DDNMY)) - clearanceBase;
                    }
                    else if (!dbElement.IsNull && !dbElement.IsDeleteable)
                    {
                        str2 = "开始标注Element";
                        element2 = ldim.Create(index + 1, DbElementTypeInstance.DPPT);
                        element2.SetAttribute(DbAttributeInstance.DDNM, dbElement);
                        position2D = drawE3DTool.SheetPosition(usefulTool.GetPositionWrtWorld(dbElement), element1);
                        if (num2 > 0)
                            element2.SetAttribute(DbAttributeInstance.NPPT, num2);
                        num1 = (!(side == "Up") && !(side == "Down") ? element2.GetDouble(DbAttributeInstance.DDNMX) : element2.GetDouble(DbAttributeInstance.DDNMY)) - clearanceBase;
                    }
                    element2.SetAttribute(DbAttributeInstance.PLCL, num1);
                }
            }
            catch
            {
                int num = (int)MessageBox.Show("运行到" + str2, "尺寸标注错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
            }
        }

        public static DbElement CreateViewNote(DbElement owner, string Name)
        {
            string str = owner.GetAsString(DbAttributeInstance.NAME) + Name;
            DbElement element = DbElement.GetElement(str);
            if (element.IsNull)
            {
                element = owner.Create(1, DbElementTypeInstance.VNOTE);
                element.SetAttribute(DbAttributeInstance.NAME, str);
            }
            else if (element.GetInteger(DbAttributeInstance.MCOU) != 0 && usefulTool.IsDeletable(element.Member(1), true))
            {
                foreach (DbElement member in element.Members())
                    member.Delete();
            }
            return element;
        }

        public static void CreateText(DbElement note, DataTable dt)
        {
            string s1 = "0";
            string s2 = "0";
            string caption = "创建文字错误";
            if (!dt.Columns.Contains("X"))
            {
                int num1 = (int)MessageBox.Show("不包含X定位数据", caption, MessageBoxButtons.OK, MessageBoxIcon.Hand);
            }
            else if (!dt.Columns.Contains("Y"))
            {
                int num2 = (int)MessageBox.Show("不包含Y定位数据", caption, MessageBoxButtons.OK, MessageBoxIcon.Hand);
            }
            else
            {
                for (int index1 = 0; index1 < dt.Rows.Count; ++index1)
                {
                    DbElement last = note.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
                    for (int index2 = 0; index2 < dt.Columns.Count; ++index2)
                    {
                        if (!(dt.Rows[index1][index2].ToString() == ""))
                        {
                            if (dt.Columns[index2].ToString().ToUpper() == "X")
                                s1 = dt.Rows[index1][index2].ToString();
                            else if (dt.Columns[index2].ToString().ToUpper() == "Y")
                            {
                                s2 = dt.Rows[index1][index2].ToString();
                            }
                            else
                            {
                                try
                                {
                                    DbAttribute dbAttribute = DbAttribute.GetDbAttribute(dt.Columns[index2].ToString());
                                    if (dbAttribute != (DbAttribute)null && last.IsAttributeValid(dbAttribute))
                                        usefulTool.SetAttribute(last, dbAttribute, dt.Rows[index1][index2].ToString());
                                }
                                catch
                                {
                                }
                            }
                        }
                    }
                    try
                    {
                        double.Parse(s1);
                        double.Parse(s2);
                        CurrentElement.Element = last;
                        Aveva.Pdms.Utilities.CommandLine.Command.CreateCommand($"at x {s1} y {s2}").RunInPdms();
                    }
                    catch
                    {
                        int num3 = (int)MessageBox.Show($"文字定位点无法转换成实数x={s1} y={s2}", caption, MessageBoxButtons.OK, MessageBoxIcon.Hand);
                    }
                }
            }
        }

        public static string ArrangeLabel(
          DbElement view,
          string location,
          DbElement layer,
          string ldim,
          double width,
          double length,
          double offset,
          bool underLine)
        {
            string asString = view.GetAsString(DbAttributeInstance.NAME);
            double[] doubleArray1 = view.GetDoubleArray(DbAttributeInstance.XYPS);
            double num1 = doubleArray1[0];
            double num2 = doubleArray1[1];
            view.GetPosition(DbAttributeInstance.THPO);
            double[] doubleArray2 = view.GetDoubleArray(DbAttributeInstance.SIZE);
            view.GetDouble(DbAttributeInstance.VSCA);
            double num3 = doubleArray2[0];
            double num4 = doubleArray2[1];
            double num5 = num1 - num3 * 0.5;
            double num6 = num1 + num3 * 0.5;
            double num7 = num2 - num4 * 0.5;
            double num8 = num2 + num4 * 0.5;
            string str1 = Application.StartupPath + "\\";
            string str2 = "";
            double num9 = layer.GetDouble(DbAttributeInstance.CHEI) + 1.0;
            double num10 = 3.0;
            double num11 = 2.0;
            int digits1 = 2;
            int digits2 = 1;
            DbElement[] dbElementArray1 = layer.Members(DbElementTypeInstance.GLABEL);
            int length1 = dbElementArray1.Length;
            if (length1 == 0)
                return str2;
            DataTable dataTable1 = new DataTable();
            dataTable1.TableName = "glabelAndOrder";
            dataTable1.Columns.Add("Element", typeof(DbElement));
            dataTable1.Columns.Add("Order", typeof(double));
            dataTable1.Columns.Add("Position", typeof(double));
            dataTable1.Columns.Add("LabelOffset", typeof(double));
            dataTable1.Columns.Add("SpaceIndex", typeof(int));
            DbElement.GetElement();
            Position2D.Create();
            try
            {
                for (int index = 0; index < dbElementArray1.Length; ++index)
                {
                    DataRow row = dataTable1.NewRow();
                    Position2D position2D = drawE3DTool.SheetPosition(usefulTool.GetPositionWrtWorld(dbElementArray1[index]), view);
                    row["Element"] = (object)dbElementArray1[index];
                    if (location == "Left" || location == "Right")
                    {
                        row["Order"] = (object)Math.Round(position2D.Y, digits1);
                        row["Position"] = (object)Math.Round(position2D.X, digits1);
                    }
                    else
                    {
                        row["Order"] = (object)Math.Round(position2D.X, digits1);
                        row["Position"] = (object)Math.Round(position2D.Y, digits1);
                    }
                    dataTable1.Rows.Add(row);
                }
            }
            catch (Exception ex)
            {
                int num12 = (int)MessageBox.Show($"{ex.Message}\n {location} {dbElementArray1.Length.ToString()}", "获取标注排序数据错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                return str2;
            }
            dataTable1.Clone();
            DataView defaultView = dataTable1.DefaultView;
            defaultView.Sort = "Order";
            DataTable table = defaultView.ToTable();
            table.TableName = "glabelSorted";
            string columnName1 = "SpaceNumber";
            string columnName2 = "LabelNumber";
            string columnName3 = "LabelStart";
            string columnName4 = "LabelEnd";
            string columnName5 = "PositionStart";
            string columnName6 = "PositionEnd";
            DataTable dataTable2 = new DataTable();
            dataTable2.TableName = "SpaceTable";
            dataTable2.Columns.Add(columnName1, typeof(int));
            dataTable2.Columns.Add(columnName2, typeof(int));
            dataTable2.Columns.Add(columnName3, typeof(double));
            dataTable2.Columns.Add(columnName4, typeof(double));
            dataTable2.Columns.Add(columnName5, typeof(double));
            dataTable2.Columns.Add(columnName6, typeof(double));
            int num13 = 0;
            double num14 = 0.0;
            double num15 = 0.0;
            DbElement element = DbElement.GetElement($"{asString}{ldim}/{location}");
            DbElement[] dbElementArray2 = new DbElement[0];
            if (element.IsNull)
            {
                DataRow row = dataTable2.NewRow();
                int int32;
                if (location == "Left" || location == "Right")
                {
                    int32 = Convert.ToInt32(Math.Floor((num8 - num7) / num9));
                    row[columnName1] = (object)int32;
                    row[columnName3] = (object)num7;
                    row[columnName4] = (object)(num8 - num9);
                    row[columnName5] = (object)num7;
                    row[columnName6] = (object)num8;
                }
                else
                {
                    int32 = Convert.ToInt32(Math.Floor((num6 - num5) / num9));
                    row[columnName1] = (object)int32;
                    row[columnName3] = (object)(num5 + num9);
                    row[columnName4] = (object)num6;
                    row[columnName5] = (object)num5;
                    row[columnName6] = (object)num6;
                }
                dataTable2.Rows.Add(row);
                num13 = int32;
            }
            else
                dbElementArray2 = element.Members();
            try
            {
                for (int index = 0; index < dbElementArray2.Length; ++index)
                {
                    double num16;
                    if (location == "Left" || location == "Right")
                    {
                        double num17;
                        if (dbElementArray2[index].GetElementType() == DbElementTypeInstance.DPOINT)
                        {
                            num16 = Math.Round(drawE3DTool.SheetPosition(dbElementArray2[index].GetPosition(DbAttributeInstance.POS), view).Y, digits1);
                            num17 = num16 - num14 - num15;
                            num15 = 0.0;
                        }
                        else
                        {
                            num16 = Math.Round(drawE3DTool.SheetPosition(usefulTool.GetPositionWrtWorld(dbElementArray2[index].GetElement(DbAttributeInstance.DDNM)), view).Y, digits1);
                            num17 = num16 - num14 - width - num15;
                            num15 = width;
                        }
                        if (num14 == 0.0)
                        {
                            num14 = num16;
                            continue;
                        }
                        int num18 = Convert.ToInt32(Math.Floor(Math.Abs(num17) / num9));
                        if (num18 < 0)
                            num18 = 0;
                        DataRow row = dataTable2.NewRow();
                        row[columnName1] = (object)num18;
                        row[columnName3] = (object)(num14 + width);
                        row[columnName4] = (object)(num16 - num9 - width);
                        row[columnName5] = (object)num14;
                        row[columnName6] = (object)num16;
                        dataTable2.Rows.Add(row);
                        num13 += num18;
                    }
                    else
                    {
                        double num19;
                        if (dbElementArray2[index].GetElementType() == DbElementTypeInstance.DPOINT)
                        {
                            num16 = Math.Round(drawE3DTool.SheetPosition(dbElementArray2[index].GetPosition(DbAttributeInstance.POS), view).X, digits1);
                            num19 = num16 - num14 - num15;
                            num15 = 0.0;
                        }
                        else
                        {
                            num16 = Math.Round(drawE3DTool.SheetPosition(usefulTool.GetPositionWrtWorld(dbElementArray2[index].GetElement(DbAttributeInstance.DDNM)), view).X, digits1);
                            num19 = num16 - num14 - width - num15;
                            num15 = width;
                        }
                        if (num14 == 0.0)
                        {
                            num14 = num16;
                            continue;
                        }
                        int num20 = Convert.ToInt32(Math.Floor(Math.Abs(num19) / num9));
                        if (num20 < 0)
                            num20 = 0;
                        DataRow row = dataTable2.NewRow();
                        row[columnName1] = (object)num20;
                        row[columnName3] = (object)(num14 + num9 + width);
                        row[columnName4] = (object)(num16 - width);
                        row[columnName5] = (object)num14;
                        row[columnName6] = (object)num16;
                        dataTable2.Rows.Add(row);
                        num13 += num20;
                    }
                    num14 = num16;
                }
            }
            catch (Exception ex)
            {
                int num21 = (int)MessageBox.Show($"{ex.Message}\n {location} {dbElementArray2.Length.ToString()}", "获取占位空间错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                return str2;
            }
            int num22 = num13 - length1;
            try
            {
                if (num22 < 0)
                {
                    int int32 = Convert.ToInt32(Math.Ceiling((double)-num22 * 0.5));
                    DataRow row1 = dataTable2.NewRow();
                    row1[columnName1] = (object)int32;
                    if (location == "Left" || location == "Right")
                    {
                        row1[columnName3] = (object)Math.Round(num7 - (double)int32 * num9, digits1);
                        row1[columnName4] = (object)Math.Round(num7 - num9, digits1);
                        row1[columnName5] = row1[columnName3];
                        row1[columnName6] = (object)Math.Round(num7, digits1);
                    }
                    else
                    {
                        row1[columnName3] = (object)Math.Round(num5 - (double)(int32 - 1) * num9, digits1);
                        row1[columnName4] = (object)Math.Round(num5, 1);
                        row1[columnName5] = (object)Math.Round(num5 - (double)(int32 - 1) * num9, digits1);
                        row1[columnName6] = row1[columnName4];
                    }
                    dataTable2.Rows.InsertAt(row1, 0);
                    DataRow row2 = dataTable2.NewRow();
                    row2[columnName1] = (object)int32;
                    if (location == "Left" || location == "Right")
                    {
                        row2[columnName3] = (object)Math.Round(num8, digits1);
                        row2[columnName4] = (object)Math.Round(num8 + (double)(int32 - 1) * num9, digits1);
                        row2[columnName5] = row2[columnName3];
                        row2[columnName6] = (object)Math.Round(num8 + (double)(int32 - 1) * num9, digits1);
                    }
                    else
                    {
                        row2[columnName3] = (object)Math.Round(num6 + num9, digits1);
                        row2[columnName4] = (object)Math.Round(num6 + (double)int32 * num9, digits1);
                        row2[columnName5] = (object)Math.Round(num6, digits1);
                        row2[columnName6] = (object)Math.Round(num6 + (double)int32 * num9, digits1);
                    }
                    dataTable2.Rows.Add(row2);
                    num13 += int32 * 2;
                }
            }
            catch (Exception ex)
            {
                int num23 = (int)MessageBox.Show($"{ex.Message}\n {location} {num22.ToString()}", "计算扩展空间错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                return str2;
            }
            int num24 = num13 - length1;
            double num25 = 0.0;
            int num26 = 0;
            try
            {
                for (int index1 = 0; index1 < dataTable2.Rows.Count; ++index1)
                {
                    double num27 = Math.Round((double)dataTable2.Rows[index1][columnName5], digits2);
                    double num28 = Math.Round((double)dataTable2.Rows[index1][columnName6], digits2);
                    dataTable2.Rows[index1][columnName2] = (object)0;
                    for (int index2 = num26; index2 < table.Rows.Count; ++index2)
                    {
                        double num29 = Math.Round((double)table.Rows[index2]["Order"], digits2);
                        if (location == "Left" || location == "Right")
                        {
                            if (num29 >= num27 && num29 < num28)
                            {
                                dataTable2.Rows[index1][columnName2] = (object)((int)dataTable2.Rows[index1][columnName2] + 1);
                                table.Rows[index2]["SpaceIndex"] = (object)index1;
                            }
                            else if (index1 == 0 && num29 < num27)
                            {
                                dataTable2.Rows[index1][columnName2] = (object)((int)dataTable2.Rows[index1][columnName2] + 1);
                                table.Rows[index2]["SpaceIndex"] = (object)index1;
                            }
                            else if (index1 == dataTable2.Rows.Count - 1 && num29 >= num28)
                            {
                                dataTable2.Rows[index1][columnName2] = (object)((int)dataTable2.Rows[index1][columnName2] + 1);
                                table.Rows[index2]["SpaceIndex"] = (object)index1;
                            }
                            else
                            {
                                num26 = index2;
                                break;
                            }
                        }
                        else if (num29 > num27 && num29 <= num28)
                        {
                            dataTable2.Rows[index1][columnName2] = (object)((int)dataTable2.Rows[index1][columnName2] + 1);
                            table.Rows[index2]["SpaceIndex"] = (object)index1;
                        }
                        else if (index1 == 0 && num29 <= num27)
                        {
                            dataTable2.Rows[index1][columnName2] = (object)((int)dataTable2.Rows[index1][columnName2] + 1);
                            table.Rows[index2]["SpaceIndex"] = (object)index1;
                        }
                        else if (index1 == dataTable2.Rows.Count - 1 && num29 > num28)
                        {
                            dataTable2.Rows[index1][columnName2] = (object)((int)dataTable2.Rows[index1][columnName2] + 1);
                            table.Rows[index2]["SpaceIndex"] = (object)index1;
                        }
                        else
                        {
                            num26 = index2;
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                int num30 = (int)MessageBox.Show($"{ex.Message}\n {location} {dataTable2.Rows.Count.ToString()}", "计算放置数量错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                return str2;
            }
            int num31 = 0;
            int num32 = 0;
            try
            {
                for (int index3 = 0; index3 < table.Rows.Count; ++index3)
                {
                    double num33 = (double)table.Rows[index3]["Order"];
                    int num34 = (int)table.Rows[index3]["SpaceIndex"];
                    for (int index4 = num31; index4 < dataTable2.Rows.Count; ++index4)
                    {
                        int num35 = (int)dataTable2.Rows[index4][columnName1];
                        if (num35 > 0)
                        {
                            double num36 = (double)dataTable2.Rows[index4][columnName3];
                            double num37 = (double)dataTable2.Rows[index4][columnName4];
                            num25 = (double)dataTable2.Rows[index4][columnName6];
                            int num38 = index4 <= num31 ? (int)dataTable2.Rows[index4][columnName2] : (int)dataTable2.Rows[index4][columnName2] + num32;
                            num31 = index4;
                            if (num24 > 0)
                            {
                                if (num24 > num35 && num34 > index4)
                                {
                                    num13 -= num35;
                                    num24 = num13 - length1;
                                    continue;
                                }
                                if (num33 - num36 <= (double)num24 * num9)
                                {
                                    if (num33 - num36 <= (double)(num35 - num38) * num9)
                                    {
                                        if (num33 >= num36)
                                            num36 = num33;
                                    }
                                    else if (num37 - num36 > (double)(num38 - 1) * num9)
                                    {
                                        double num39 = num36 + num9 * (double)(num35 - num38);
                                        double num40 = num37 - num9 * (double)(num38 - 1);
                                        num36 = Math.Abs(num40 - num33) >= Math.Abs(num39 - num33) ? num39 : (num40 <= num36 ? num39 : num40);
                                    }
                                }
                                else if (num24 <= num35 - num38)
                                {
                                    double num41 = num36 + num9 * (double)num24;
                                    double num42 = num37 - num9 * (double)(num38 - 1);
                                    num36 = Math.Abs(num42 - num33) >= Math.Abs(num41 - num33) ? num41 : (num42 <= num36 ? num41 : num42);
                                }
                                else if (num37 - num36 > (double)num38 * num9)
                                {
                                    double num43 = num36 + num9 * (double)(num35 - num38);
                                    double num44 = num37 - num9 * (double)(num38 - 1);
                                    num36 = Math.Abs(num44 - num33) >= Math.Abs(num43 - num33) ? num43 : (num44 <= num36 ? num43 : num44);
                                }
                            }
                            int int32 = Convert.ToInt32(Math.Floor((num37 - num36) / num9));
                            dataTable2.Rows[index4][columnName2] = (object)(num38 - 1);
                            dataTable2.Rows[index4][columnName1] = (object)int32;
                            dataTable2.Rows[index4][columnName3] = (object)(num36 + num9);
                            num32 = num38 - 1;
                            table.Rows[index3]["LabelOffset"] = (object)(num36 - num33);
                            num13 -= num35 - int32;
                            --length1;
                            num24 = num13 - length1;
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (dataTable2.Rows.Count != 0)
                {
                    string fileName = $"{str1}{location}space.xml";
                    dataTable2.WriteXml(fileName, XmlWriteMode.WriteSchema);
                }
                if (table.Rows.Count != 0)
                {
                    string fileName = $"{str1}{location}glabel.xml";
                    table.Columns.Remove("Element");
                    table.WriteXml(fileName, XmlWriteMode.WriteSchema);
                }
                return "计算标注偏移值错误," + ex.Message;
            }
            double num45 = 0.0;
            double[] val1 = new double[4];
            double[] val2 = new double[2];
            double[] val3 = new double[2];
            double num46 = 0.0;
            try
            {
                for (int index = 0; index < table.Rows.Count; ++index)
                {
                    DbElement dbElement = (DbElement)table.Rows[index]["Element"];
                    try
                    {
                        num45 = (double)table.Rows[index]["Position"];
                        num46 = (double)table.Rows[index]["LabelOffset"];
                    }
                    catch
                    {
                        int num47 = (int)MessageBox.Show($"comparePositon ={table.Rows[index]["Position"]?.ToString()}\nlabelOffset ={table.Rows[index]["LabelOffset"]?.ToString()}", "Label排列数据提取错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                    }
                    switch (location)
                    {
                        case "Left":
                            val1[0] = -(offset + num45 - num5);
                            val1[1] = 0.0;
                            val1[2] = val1[0] - num10;
                            val1[3] = !underLine ? num46 + 1.14 : num46;
                            dbElement.SetAttribute(DbAttributeInstance.BPOF, val1);
                            val3[0] = val1[2] - length;
                            val3[1] = num46;
                            dbElement.SetAttribute(DbAttributeInstance.XYPS, val3);
                            dbElement.SetAttribute(DbAttributeInstance.ADEG, 0.0);
                            dbElement.SetAttribute(DbAttributeInstance.JUST, 1);
                            val2[0] = !underLine ? length - 2.0 : -num11;
                            val2[1] = 0.0;
                            dbElement.SetAttribute(DbAttributeInstance.CPOF, val2);
                            break;
                        case "Right":
                            val1[0] = offset + num6 - num45;
                            val1[1] = 0.0;
                            val1[2] = val1[0] + num10;
                            val1[3] = !underLine ? num46 + 1.14 : num46;
                            dbElement.SetAttribute(DbAttributeInstance.BPOF, val1);
                            val3[0] = val1[2] + num11;
                            val3[1] = num46;
                            dbElement.SetAttribute(DbAttributeInstance.XYPS, val3);
                            dbElement.SetAttribute(DbAttributeInstance.ADEG, 0.0);
                            dbElement.SetAttribute(DbAttributeInstance.JUST, 1);
                            val2[0] = !underLine ? 2.0 : length;
                            val2[1] = 0.0;
                            dbElement.SetAttribute(DbAttributeInstance.CPOF, val2);
                            break;
                        case "Up":
                            val1[0] = 0.0;
                            val1[1] = offset + num8 - num45;
                            val1[2] = num46;
                            val1[3] = val1[1] + num10;
                            dbElement.SetAttribute(DbAttributeInstance.BPOF, val1);
                            val3[1] = val1[3] + num11;
                            val3[0] = num46;
                            dbElement.SetAttribute(DbAttributeInstance.XYPS, val3);
                            dbElement.SetAttribute(DbAttributeInstance.ADEG, 90.0);
                            dbElement.SetAttribute(DbAttributeInstance.JUST, 1);
                            val2[0] = length;
                            val2[1] = 0.0;
                            dbElement.SetAttribute(DbAttributeInstance.CPOF, val2);
                            break;
                        case "Down":
                            val1[0] = 0.0;
                            val1[1] = -(offset + num45 - num7);
                            val1[2] = num46;
                            val1[3] = val1[1] - num10;
                            dbElement.SetAttribute(DbAttributeInstance.BPOF, val1);
                            val3[1] = val1[3] - length;
                            val3[0] = num46;
                            dbElement.SetAttribute(DbAttributeInstance.XYPS, val3);
                            dbElement.SetAttribute(DbAttributeInstance.ADEG, 90.0);
                            dbElement.SetAttribute(DbAttributeInstance.JUST, 1);
                            val2[0] = -num11;
                            val2[1] = 0.0;
                            dbElement.SetAttribute(DbAttributeInstance.CPOF, val2);
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                int num48 = (int)MessageBox.Show($"{ex.Message}\n {location} {table.Rows.Count.ToString()}", "标注定位错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                return str2;
            }
            return str2;
        }

        public static double[] ViewLimits(DbElement view)
        {
            double[] doubleArray1 = view.GetDoubleArray(DbAttributeInstance.XYPS);
            double[] doubleArray2 = view.GetDoubleArray(DbAttributeInstance.SIZE);
            double num1 = doubleArray1[0] + doubleArray2[0] * 0.5;
            double num2 = doubleArray1[0] - doubleArray2[0] * 0.5;
            double num3 = doubleArray1[1] + doubleArray2[1] * 0.5;
            double num4 = doubleArray1[1] - doubleArray2[1] * 0.5;
            return new double[4] { num2, num1, num4, num3 };
        }

        public static LimitsBox2D LimitsBox(DbElement view)
        {
            double[] doubleArray1 = view.GetDoubleArray(DbAttributeInstance.XYPS);
            double num1 = doubleArray1[0];
            double num2 = doubleArray1[1];
            double[] doubleArray2 = view.GetDoubleArray(DbAttributeInstance.SIZE);
            return LimitsBox2D.Create(Position2D.Create(num1 - doubleArray2[0] * 0.5, num2 - doubleArray2[1] * 0.5), Position2D.Create(num1 + doubleArray2[0] * 0.5, num2 + doubleArray2[1] * 0.5));
        }

        public static LimitsBox ViewLimitBox(DbElement view)
        {
            // 注意：VVOL 未设置（ID list 为空/视图未更新）时 GetDoubleArray 抛 PdmsException，
            // 不是返回 null —— 必须捕获（实测崩溃点：button2 出图后立即收集标注元素）
            try
            {
                double[] doubleArray = view.GetDoubleArray(DbAttributeInstance.VVOL);
                if (doubleArray != null && doubleArray.Length >= 6)
                {
                    return Aveva.Pdms.Geometry.LimitsBox.Create(Position.Create(doubleArray[0], doubleArray[1], doubleArray[2]), Position.Create(doubleArray[3], doubleArray[4], doubleArray[5]));
                }
            }
            catch { }
            // IDLI 为空时 VVOL 未初始化（模型尚未加入视图），返回空盒使后续过滤全部跳过
            return Aveva.Pdms.Geometry.LimitsBox.Create(
                Aveva.Pdms.Geometry.Position.Create(double.MaxValue, double.MaxValue, double.MaxValue),
                Aveva.Pdms.Geometry.Position.Create(-double.MaxValue, -double.MaxValue, -double.MaxValue));
        }

        /// <summary>
        /// 用 PML COLLECT 自动计算指定类型模型在给定体积内的包围盒
        /// 替代手动输入坐标，参考 ITD 的 Volume 选择模式
        /// </summary>
        /// <param name="elementType">要收集的元素类型（如 "BRAN", "EQUIPMENT", "STRUCTURE"）</param>
        /// <param name="volumeMinX">包围盒最小 X</param>
        /// <param name="volumeMinY">包围盒最小 Y</param>
        /// <param name="volumeMinZ">包围盒最小 Z</param>
        /// <param name="volumeMaxX">包围盒最大 X</param>
        /// <param name="volumeMaxY">包围盒最大 Y</param>
        /// <param name="volumeMaxZ">包围盒最大 Z</param>
        /// <returns>[minX, maxX, minY, maxY, minZ, maxZ]</returns>
        public static double[] CollectLimits(string elementType, double volumeMinX, double volumeMinY, double volumeMinZ, double volumeMaxX, double volumeMaxY, double volumeMaxZ)
        {
            double[] result = new double[] { volumeMinX, volumeMaxX, volumeMinY, volumeMaxY, volumeMinZ, volumeMaxZ };
            try
            {
                string pmlCmd = string.Format(
                    "VAR !Coll COLLECT ALL {0} WITHIN W{1}N{2}U{3} TO W{4}N{5}U{6} FOR CE",
                    elementType,
                    Math.Round(volumeMinX), Math.Round(volumeMinY), Math.Round(volumeMinZ),
                    Math.Round(volumeMaxX), Math.Round(volumeMaxY), Math.Round(volumeMaxZ)
                );
                // 执行 PML 收集，结果存入临时变量
                drawTool.CommandExcute(pmlCmd);
                // 通过 IDLIST 获取实际包围盒（如果用户已通过 listBox 手动添加了元素）
                DbElement dllb = FindCurrentDrawlistUnder(Aveva.Pdms.Shared.CurrentElement.Element);
                if (dllb != null && !dllb.IsNull)
                {
                    var limits = Graphic.getDrawlistLimits();
                    if (limits != null && limits.Length >= 6)
                    {
                        // 如果有实际数据，用实际包围盒覆盖
                        result[0] = limits[0]; result[1] = limits[1];
                        result[2] = limits[2]; result[3] = limits[3];
                        result[4] = limits[4]; result[5] = limits[5];
                    }
                }
            }
            catch { }
            return result;
        }

        /// <summary>
        /// PDMS 官方 UIDLIVOL 模式：创建体积定义的绘制列表
        /// 参考 DRA/CLIB/UIDLIVOL 宏的实现
        /// 在视图上下文（CE = 该 VIEW）下调用；ADD/REM 命令会自动落到该视图的绘制列表
        /// </summary>
        /// <param name="idliName">IDLIST 名称（如 "/VIEW/VOL"，当前仅作标识）</param>
        /// <param name="limits">世界坐标范围串 "Wx Ny Uz TO Wx2 Ny2 Uz2"（WITHIN 子句格式）</param>
        /// <param name="addTypes">要添加的元素类型，空格分隔（如 "BRAN EQUIP STEEL"）</param>
        /// <param name="addRules">对应过滤规则 "TYPE:RULE;TYPE2:RULE2"（可为空）</param>
        /// <param name="remTypes">要从范围中排除的类型，空格分隔（优先级3）</param>
        /// <param name="remRules">对应排除规则（可为空）</param>
        public static void CreateVolumeDrawlist(string idliName, string limits, string addTypes, string addRules, string remTypes = "", string remRules = "")
        {
            try
            {
                // ADD：ADD ALL <type> [WITH (rule)] WITHIN <limits>
                if (!string.IsNullOrEmpty(addTypes))
                {
                    string[] types = addTypes.Trim().Split(new char[] { ' ' });
                    foreach (string type in types)
                    {
                        if (string.IsNullOrEmpty(type)) continue;
                        string rule = addRules.Contains(";") ? GetRuleForType(addRules, type) : "";
                        string cmd = string.IsNullOrEmpty(rule)
                            ? string.Format("ADD ALL {0} WITHIN {1}", type, limits)
                            : string.Format("ADD ALL {0} WITH ({1}) WITHIN {2}", type, rule, limits);
                        drawTool.CommandExcute(cmd, true);
                    }
                }
                // REM：REM ALL <type> [WITH (rule)] WITHIN <limits>（优先级3）
                if (!string.IsNullOrEmpty(remTypes))
                {
                    string[] types = remTypes.Trim().Split(new char[] { ' ' });
                    foreach (string type in types)
                    {
                        if (string.IsNullOrEmpty(type)) continue;
                        string rule = remRules.Contains(";") ? GetRuleForType(remRules, type) : "";
                        string cmd = string.IsNullOrEmpty(rule)
                            ? string.Format("REM ALL {0} WITHIN {1}", type, limits)
                            : string.Format("REM ALL {0} WITH ({1}) WITHIN {2}", type, rule, limits);
                        drawTool.CommandExcute(cmd, true);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show("创建体积绘制列表失败: " + ex.Message, "错误",
                    System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
            }
        }

        private static string GetRuleForType(string rules, string type)
        {
            // 简单解析: 规则格式 "TYPE1:RULE1;TYPE2:RULE2"
            string[] pairs = rules.Split(';');
            foreach (string pair in pairs)
            {
                if (pair.Contains(type.ToUpper()))
                {
                    string[] parts = pair.Split(':');
                    return parts.Length > 1 ? parts[1].Trim() : "";
                }
            }
            return "";
        }

        /// <summary>
        /// 读取 VIEW 元素的范围信息
        /// 优先级：绘制列表建立后 PDMS 自动填充的 VVOL 属性
        /// （用户在 PDMS 界面手动设置的 :CDLIMITS 由 PDMS 同步进 VVOL，本方法一并覆盖）
        /// 返回 "From W.. N.. U.. To W.. N.. U.." 或 null
        /// </summary>
        public static string GetViewLimits(DbElement view)
        {
            try
            {
                // VVOL 属性：绘制列表建立后由 PDMS 自动填充的真实世界范围
                double[] wvol = view.GetDoubleArray(DbAttributeInstance.VVOL);
                if (wvol != null && wvol.Length >= 6)
                    return string.Format("From W{0} N{1} U{2} To W{3} N{4} U{5}",
                        wvol[0], wvol[1], wvol[2], wvol[3], wvol[4], wvol[5]);
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 解析视图范围串（支持 "From W.. N.. U.. To W.. N.. U.." 或 "W.. N.. U.. TO W.. N.. U.."）
        /// 返回 [xmin,xmax,ymin,ymax,zmin,zmax]，解析失败返回 null
        /// </summary>
        public static double[] ParseLimitsString(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            try
            {
                string t = s.Replace("From", "").Replace("To", "").Replace("TO", "");
                var matches = System.Text.RegularExpressions.Regex.Matches(t, "[WNU][-0-9.]+");
                if (matches.Count >= 6)
                {
                    double[] r = new double[6];
                    for (int i = 0; i < 6; i++)
                        r[i] = Convert.ToDouble(matches[i].Value.Substring(1));
                    return r; // [xmin,xmax,ymin,ymax,zmin,zmax]
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 用 PML 命令获取单个元素的包围盒（参考 PDMSUI/des/clib/relemvol）
        /// </summary>
        public static double[] GetElementVolume(DbElement element)
        {
            try
            {
                string savedPath = Aveva.Pdms.Shared.CurrentElement.Element.GetAsString(DbAttributeInstance.NAME);
                drawTool.CommandExcute("CD " + element.GetAsString(DbAttributeInstance.NAME));
                drawTool.CommandExcute("VAR !VOLU VOL CE");
                // 从返回结果中解析
                // VOL 命令返回: From Wx Ny Uz To Wx2 Ny2 Uz2
                // 这里简化处理，直接返回 WVOL
                double[] wvol = element.GetDoubleArray(DbAttributeInstance.WVOL);
                drawTool.CommandExcute("CD " + savedPath);
                if (wvol != null && wvol.Length >= 6)
                    return wvol;
            }
            catch { }
            return null;
        }

        public static DbElement FindCurrentDrawlistUnder(DbElement root)
        {
            if (root == null || root.IsNull) return null;
            // 1) 从 root 向下递归查找 DLLB（覆盖 DLLB 挂在 VIEW / LAYER 等任意子层的情况，
            //    不再假设它一定直接挂在 DRWG 下 —— 之前按钮2 的 IDLN 绑定因此可能漏绑）
            DbElement found = FindDllbRecursive(root, 0);
            if (found != null && !found.IsNull) return found;
            // 2) 兜底：沿 owner 链向上（DRWG→REGI→DEPT→WORLD）逐级向下搜索
            DbElement up = root.Owner;
            int guard = 0;
            while (up != null && !up.IsNull && guard++ < 16)
            {
                found = FindDllbRecursive(up, 0);
                if (found != null && !found.IsNull) return found;
                up = up.Owner;
            }
            return null;
        }

        private static DbElement FindDllbRecursive(DbElement root, int depth)
        {
            if (root == null || root.IsNull || depth > 14) return null;
            foreach (DbElement m in root.Members())
            {
                if (m.GetElementType() == DbElementTypeInstance.DLLB) return m;
                if (m.GetElementType() == DbElementTypeInstance.LIBY)
                {
                    foreach (DbElement sub in m.Members())
                        if (sub.GetElementType() == DbElementTypeInstance.DLLB) return sub;
                }
                else
                {
                    DbElement found = FindDllbRecursive(m, depth + 1);
                    if (found != null && !found.IsNull) return found;
                }
            }
            return null;
        }

        /// <summary>
        /// 在 DLLB 下找到第一个 IDLIST；不存在则创建（参考 E3D 原版 drawE3D.cs
        /// oneDLLB.CreateLast(IDLIST) —— IDLN 必须指向 IDLIST 视图才能抽取模型）。
        /// </summary>
        public static DbElement FindOrCreateIdlist(DbElement dllb)
        {
            if (dllb == null || dllb.IsNull) return null;
            foreach (DbElement m in dllb.Members())
            {
                if (m.GetElementType() == DbElementTypeInstance.IDLIST) return m;
            }
            return dllb.CreateLast(DbElementTypeInstance.IDLIST);
        }

        // ===== 出图类型 → 体积框选的 ADD/REM 类型与规则映射 =====
        // 规则串格式: "TYPE:RULE;TYPE2:RULE2"，RULE 为 PML 规则表达式（如 ABOR GT 100）
        // 优先级3：通过 remTypes/remRules 支持排除规则，例如设备图不显示钢结构

        /// <summary>体积内要纳入绘制的元素类型（空格分隔）。
        /// 注意：PDMS 没有 STEEL 类型（那是 E3D 的 DbElementTypeInstance），
        /// 结构对应 SCTN（型钢截面）/SUBSTRUCTURE（结构层级），
        /// 用错会报 "CP: Syntax error"（PDMS 对未知类型回显 ^^STEEL）。</summary>
        public static string GetAddTypesForDrawingType(string drawingType)
        {
            if (drawingType == "管道布置图") return "BRAN EQUIP SCTN";
            if (drawingType == "设备布置图") return "EQUIP SCTN";
            if (drawingType == "埋板布置图") return "SCTN";
            return "BRAN EQUIP SCTN"; // 默认：管道 + 设备 + 结构截面
        }

        /// <summary>纳入规则（默认空；可扩展为 "BRAN:ABOR GT 100" 等）</summary>
        public static string GetAddRulesForDrawingType(string drawingType)
        {
            return "";
        }

        /// <summary>体积内要排除的元素类型（优先级3，默认空）</summary>
        public static string GetRemTypesForDrawingType(string drawingType)
        {
            return "";
        }

        /// <summary>排除规则（优先级3，默认空）</summary>
        public static string GetRemRulesForDrawingType(string drawingType)
        {
            return "";
        }
    }
}