using Aveva.Pdms.Database;
using Aveva.PDMS.Database.Filters;
using Aveva.Pdms.Geometry;
using Aveva.Pdms.Shared;
using Aveva.Pdms.Utilities.CommandLine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using ATT = Aveva.Pdms.Database.DbAttributeInstance;
using CE = Aveva.Pdms.Shared.CurrentElement;
using Filter = Aveva.PDMS.Database.Filters.TypeFilter;
namespace drawingPDMS
{
    public class drawTool
    {
        public static bool CommandExcute(string commandline, bool flag = false)
        {
            // 2026-10-15 (2,202) "Cannot go to the element =0/0" 中央护栏：
            // =0/0（旧版串形 Nulref）是 PDMS 空元素引用的 CP 字符串形式
            // （原厂 Utilities.E3Doriginal.cs:686 Replace("Nulref","=0/0") 实证）。
            // 标注元素（SLABEL/GLABEL/STRAIGHT…）创建退化时 NAME 串形变 =0/0，
            // 拼出的 CP 命令变成 "=0/0 at x …" → CP 导航报 (2,202)、弹模态框、中断整条 op 链。
            // 此处统一拦截（覆盖全部调用点）：记日志跳过，不下发。
            if (IsNullRefCommand(commandline))
            {
                BridgeLog.Write("SKIP nullref/empty-prefix cmd: [" + commandline + "]");
                return false;
            }
            Command command = Command.CreateCommand(commandline);
            bool result = command.RunInPdms();
            return result;
        }

        /// <summary>判定命令是否带空引用：首词为 =0/0/Nulref、元素前缀为空（行首空白）、
        /// 裸 CD/CE 无参、或任意位置出现 =0/0 形式的空引用 token。</summary>
        private static bool IsNullRefCommand(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return true;
            char c0 = cmd[0];
            if (c0 == ' ' || c0 == '\t' || c0 == '\r' || c0 == '\n')
                return true; // GetAsString(NAME)="" → 子命令会错落到当前 CE 元素
            string[] toks = cmd.Split(new char[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (toks.Length == 0) return true;
            if (IsNullRefToken(toks[0])) return true;
            if (toks.Length == 1 && (toks[0].Equals("CD", StringComparison.OrdinalIgnoreCase)
                                   || toks[0].Equals("CE", StringComparison.OrdinalIgnoreCase)))
                return true; // 定位命令缺参
            for (int i = 1; i < toks.Length; i++)
                if (IsNullRefToken(toks[i])) return true;
            return false;
        }

        private static bool IsNullRefToken(string t)
        {
            string u = t.Trim().ToUpperInvariant();
            if (u.Length == 0) return true;
            if (u == "=" || u == "/" || u == "=0/0" || u == "/0" || u == "NULREF") return true;
            if (u.StartsWith("=0/")) return true; // =0/n：ref 编号 0 = 空引用族
            return false;
        }
        public static void UpdateModelView(DbElement element)
        {
            // 照 FangYuanPDMS.Design.UpdateModelView 原文：先设 Owner，再设元素本身
            // PDMS 更新视图时需要 Owner 上下文做坐标投影（SheetPosition），否则抛异常
            // 2026-10-16 判空加固（用户纪律「先判断对象是不是空，不是空再继续执行」）：
            // element / Owner 为空或已删 → 直接返回，不做 CE 切换（原实现 element.Owner 空引用即 NRE）
            if (element == null || element.IsNull || element.IsDeleted) return;
            DbElement owner = null;
            try { owner = element.Owner; } catch { }
            if (owner == null || owner.IsNull || owner.IsDeleted) return;
            CurrentElement.Element = owner;
            CurrentElement.Element = element;
        }
        public static string SetAttribute(DbElement element, DbAttribute attribute, string value)
        {
            if (!element.IsAttributeValid(attribute))
                return attribute.ToString() + "属性无效";
            if (!element.IsAttributeSetable(attribute))
                return attribute.ToString() + "属性无法设置";
            string str1 = "";
            string str2 = "";
            try
            {
                if (attribute.Type == DbAttributeType.STRING)
                {
                    if (attribute == DbAttributeInstance.NAME)
                    {
                        str2 = "名称属性";
                        char[] chArray = new char[1] { ' ' };
                        value = value.Trim(chArray);
                        if (value == "" || value.ToUpper() == "UNSET" || value.ToUpper() == "NULREF")
                        {
                            element.SetAttribute(DbAttributeInstance.NAME, "");
                            return "";
                        }
                        if (value.StartsWith("/"))
                        {
                            if (!DbElement.GetElement(value).IsNull)
                                return "名称已经存在";
                        }
                        else
                        {
                            value = "/" + value;
                            if (!DbElement.GetElement(value).IsNull)
                                return "名称已经存在";
                        }
                    }
                    str2 = "字符串属性";
                    int maximumSize = attribute.MaximumSize;
                    if (value.Length > maximumSize)
                        value = value.Substring(0, maximumSize);
                    string asString = element.GetAsString(attribute);
                    str2 = "字符串属性OldValue=" + asString;
                    if (asString != value)
                        element.SetAttribute(attribute, value);
                }
                else if (attribute.Type == DbAttributeType.INTEGER)
                {
                    str2 = "整数属性";
                    if (value == "")
                        value = "0";
                    int result = 0;
                    if (!int.TryParse(value, out result))
                        return value + "整数赋值错误";
                    if (element.GetAsString(attribute) != value)
                        element.SetAttribute(attribute, Convert.ToInt32(value));
                }
                else if (attribute.Type == DbAttributeType.DOUBLE)
                {
                    str2 = "实数属性";
                    if (value == "")
                        value = "0";
                    double result = 0.0;
                    if (!double.TryParse(value, out result))
                        return value + "实数赋值错误";
                    if (attribute.Units.ToString() == "856001")
                        drawTool.SetAttributeTemperature(element, attribute, value, true);
                    else if (attribute.Units.ToString() == "909565")
                    {
                        drawTool.SetAttributePressure(element, attribute, value, true);
                    }
                    else
                    {
                        if (value.IndexOf("m") >= 0)
                        {
                            int length = value.IndexOf("m");
                            value = value.Substring(0, length);
                        }
                        if (attribute.IsArray)
                        {
                            double[] val = new double[1]
                            {
              Convert.ToDouble(value)
                            };
                            element.SetAttribute(attribute, val);
                        }
                        else if (element.GetAsString(attribute) != value)
                            element.SetAttribute(attribute, Convert.ToDouble(value));
                    }
                }
                else if (attribute.Type == DbAttributeType.BOOL)
                {
                    str2 = "布尔属性";
                    if (Convert.ToBoolean(element.GetAsString(attribute)) != Convert.ToBoolean(value))
                        element.SetAttribute(attribute, Convert.ToBoolean(value));
                }
                else if (attribute.Type == DbAttributeType.WORD)
                {
                    str2 = "Word属性";
                    if (element.GetAsString(attribute) != value)
                        element.SetAttribute(attribute, value.ToUpper());
                }
                else if (attribute.Type == DbAttributeType.ELEMENT)
                {
                    str2 = "Element属性";
                    DbElement element1;
                    if (value == "" || value.ToUpper() == "UNSET" || value.ToUpper() == "NULREF")
                        element1 = DbElement.GetElement();
                    else if (value.IndexOf("/") == 0)
                    {
                        element1 = DbElement.GetElement(value);
                        if (element1.IsNull)
                            return "对象不存在";
                    }
                    else
                    {
                        element1 = DbElement.GetElement("/" + value);
                        if (element1.IsNull)
                            return "对象不存在";
                    }
                    if (element.GetAsString(attribute) != value)
                        element.SetAttribute(attribute, element1);
                }
                else
                {
                    str2 = "其他属性";
                    element.SetAttribute(attribute, value);
                }
            }
            catch
            {
                str1 = str2;
            }
            return str1;
        }
        public static string SetAttributeTemperature( DbElement element,DbAttribute attribute, string value, bool showAlert)
        {
            string text = "";
            string asString = element.GetAsString(DbAttributeInstance.NAME);
            if (value == "")
                value = "0";
            double result = 0.0;
            if (!double.TryParse(value, out result))
            {
                int num = (int)MessageBox.Show(value + "无法转换成实数", "温度赋值错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                return "温度赋值错误";
            }
            try
            {
                DbDoubleUnits units = DbDouble.Create(element.GetAsString(DbAttributeInstance.TEMP)).Units;
                DbFormat format = DbFormat.Create();
                format.Units = units;
                DbDouble dbDouble = DbDouble.Create(value, format);
                element.SetAttribute(attribute, dbDouble);
            }
            catch
            {
                text = $"{asString}温度值={value}";
                if (showAlert)
                {
                    int num = (int)MessageBox.Show(text, "温度赋值错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                }
            }
            return text;
        }
        public static string SetAttributePressure(
   DbElement element,
   DbAttribute attribute,
   string value,
   bool showAlert)
        {
            string text = "";
            string asString = element.GetAsString(DbAttributeInstance.NAME);
            if (value == "")
                value = "0";
            double result = 0.0;
            if (!double.TryParse(value, out result))
            {
                int num = (int)MessageBox.Show(value + "无法转换成实数", "温度赋值错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                return "温度赋值错误";
            }
            try
            {
                DbDoubleUnits units = DbDouble.Create(element.GetAsString(DbAttributeInstance.PRES)).Units;
                DbFormat format = DbFormat.Create();
                format.Units = units;
                DbDouble dbDouble = DbDouble.Create(value, format);
                element.SetAttribute(attribute, dbDouble);
            }
            catch
            {
                text = $"{asString}压力值={value}";
                if (showAlert)
                {
                    int num = (int)MessageBox.Show(text, "压力赋值错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                }
            }
            return text;
        }

        public static string GetPressureValue(string value)
        {
            if (value.ToUpper() == "UNSET")
                return "0";
            try
            {
                value = DbDouble.Create(value).Value.ToString();
            }
            catch
            {
                int num = (int)MessageBox.Show("请输入可以转换的压力值", "输入字符格式错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
            }
            return value;
        }

        public static string GetTemperatureValue(string value)
        {
            if (value.ToUpper() == "UNSET")
                return "0";
            try
            {
                value = DbDouble.Create(value).Value.ToString();
            }
            catch
            {
                int num = (int)MessageBox.Show("请输入可以转换的温度值", "输入字符格式错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
            }
            return value;
        }

        public static string GetPressureUnit(DbElement element)
        {
            return DbDouble.Create(element.GetAsString(DbAttributeInstance.PRES)).Units.ShortName;
        }

        public static string GetTemperatureUnit(DbElement element)
        {
            return DbDouble.Create(element.GetAsString(DbAttributeInstance.TEMP)).Units.ShortName;
        }
       
        public static double getAngle(DbElement ele)
        {
            //角度
            Direction nDirX = Direction.Create(Axis.X);
            Direction nozzDir = ele.GetDirection(ATT.PDIR, 1);

            Double Anglex = nDirX.Angle(nozzDir);
            Direction nDirY = Direction.Create(Axis.Y);
            Double Angle = 0;


            if (Anglex >= 0 && Anglex < 90)
            {

                Angle = Math.Round(nDirY.Angle(ele.GetDirection(ATT.PDIR, 1)));

            }
            else
            {
                Angle = Math.Round(360 - nDirY.Angle(ele.GetDirection(ATT.PDIR, 1)));

            }

            if (Angle == 360)
            {
                Angle = 0;
            }
            return Angle;
        }
        public static double getArrawAngle(DbElement ele)
        {
            //角度
            Direction nDirY = Direction.Create(Axis.Y);
            Direction nozzDir = ele.GetDirection(ATT.LDIR);
            Direction nDirX = Direction.Create(Axis.WEST);
            Double Angley = nDirY.Angle(nozzDir);
            Double Angle = 0;


            if (Angley >= 0 && Angley < 90)
            {

                Angle = Math.Round(360 - nDirX.Angle(ele.GetDirection(ATT.LDIR)));
            }
            else
            {

                Angle = Math.Round(nDirX.Angle(ele.GetDirection(ATT.LDIR)));

            }

            if (Angle == 360)
            {
                Angle = 0;
            }
            return Angle;
        }
        public static double getElboAngle(DbElement ele)
        {
            //角度
            Direction nDirX = Direction.Create(Axis.X);
            Direction nozzDir = ele.GetDirection(ATT.ADIR);

            Double Anglex = nDirX.Angle(nozzDir);
            Direction nDirY = Direction.Create(Axis.SOUTH);
            Double Angle = 0;
            if (ele.GetAsString(ATT.ADIR).Length > 1 && ele.GetAsString(ATT.ADIR)[0].ToString() == "E")
            {
                nozzDir = Direction.Create(Axis.EAST);
            }
            else if (ele.GetAsString(ATT.ADIR).Length > 1 && ele.GetAsString(ATT.ADIR)[0].ToString() == "W")
            {
                nozzDir = Direction.Create(Axis.WEST);
            }
            else if (ele.GetAsString(ATT.ADIR).Length > 1 && ele.GetAsString(ATT.ADIR)[0].ToString() == "N")
            {
                nozzDir = Direction.Create(Axis.NORTH);
            }
            else if (ele.GetAsString(ATT.ADIR).Length > 1 && ele.GetAsString(ATT.ADIR)[0].ToString() == "S")
            {
                nozzDir = Direction.Create(Axis.SOUTH);
            }
            if (Anglex >= 0 && Anglex <= 90)
            {

                Angle = Math.Round(nDirY.Angle(nozzDir));

            }
            else
            {
                Angle = Math.Round(360 - nDirY.Angle(nozzDir));

            }

          
            return Angle;
        }
        public static double getValvAngle(DbElement ele)
        {
           
            //角度
            Direction nDirX = Direction.Create(Axis.X);
            Direction nozzDir = ele.GetDirection(ATT.PDIR, 2);

            Double Anglex = nDirX.Angle(nozzDir);
            Direction nDirY = Direction.Create(Axis.Y);
            Double Angle = 0;


            if ( Anglex <= 90)
            {

                Angle = Math.Round(nDirY.Angle(ele.GetDirection(ATT.PDIR, 2)));

            }
            else
            {
                Angle = Math.Round(360 - nDirY.Angle(ele.GetDirection(ATT.PDIR, 2)));

            }

          
            return Angle;
        }
        public static double getValvVAngle(DbElement ele)
        {
            Double Angle = 0;
            try
            {
                //角度
                Direction nDirX = Direction.Create(Axis.X);
                Direction nozzDir = ele.GetDirection(ATT.PDIR, 3);

                Double Anglex = nDirX.Angle(nozzDir);
                Direction nDirY = Direction.Create(Axis.SOUTH);
               


                if (Anglex <= 90)
                {

                    Angle = Math.Round(nDirY.Angle(ele.GetDirection(ATT.PDIR, 3)));

                }
                else
                {
                    Angle = Math.Round(360 - nDirY.Angle(ele.GetDirection(ATT.PDIR, 3)));

                }
            }
            catch (Exception)
            {

               
            }

            return Angle;


        }
        public static string GetBoreUnit(DbElement element)
        {
            return DbDouble.Create(element.GetAsString(DbAttributeInstance.BORE)).Units.ShortName;
        }
        public static DbElement createDrwgDraw(DbElement element,String size)
        {
           
            DbElement sheetDraw = element.CreateLast(DbElementTypeInstance.SHEET);
            if (size.Contains("A4"))
            {
                drawTool.CommandExcute(sheetDraw.GetAsString(ATT.NAME) + "size 210 297");

            }
            DbElement libyDraw = element.CreateLast(DbElementTypeInstance.LIBY);
            DbElement dllbDraw = libyDraw.CreateLast(DbElementTypeInstance.DLLB);
            DbElement trlbDraw = libyDraw.CreateLast(DbElementTypeInstance.TRLB);
            DbElement idliDraw = dllbDraw.CreateLast(DbElementTypeInstance.IDLIST);
            drawTool.UpdateModelView(idliDraw);
            drawTool.UpdateModelView(trlbDraw);
            drawTool.UpdateModelView(dllbDraw);
            drawTool.UpdateModelView(libyDraw);
            drawTool.UpdateModelView(sheetDraw);
            drawTool.UpdateModelView(element);
            return sheetDraw;
        }
        public static void SetAttributePipeMember(
          DbElement pipe,
          DbAttribute attribute,
          string value,
          string level)
        {
            if (attribute == (DbAttribute)null || !pipe.IsAttributeValid(attribute))
            {
                int num1 = (int)MessageBox.Show($"Pipe的{attribute.Name}属性无效", "属性设置错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
            }
            else
            {
                if (attribute.Type.ToString() == "DOUBLE" && attribute.Units.ToString() == "856001")
                    value = drawTool.GetTemperatureValue(value);
                else if (attribute.Type.ToString() == "DOUBLE" && attribute.Units.ToString() == "909565")
                    value = drawTool.GetPressureValue(value);
                if (pipe.IsAttributeSetable(attribute))
                {
                    drawTool.SetAttribute(pipe, attribute, value);
                    Aveva.PDMS.Database.Filters.TypeFilter filt1 = new Aveva.PDMS.Database.Filters.TypeFilter(DbElementTypeInstance.BRANCH);
                    Aveva.PDMS.Database.Filters.TypeFilter filt2 = new Aveva.PDMS.Database.Filters.TypeFilter(DbElementTypeInstance.BRANCH.MemberTypes());
                    DBElementCollection elementCollection1 = new DBElementCollection(pipe, (BaseFilter)filt1);
                    DBElementCollection elementCollection2 = new DBElementCollection(pipe, (BaseFilter)filt2);
                    if (level.ToUpper().Trim() == "BRANCH" || level.ToUpper().Trim() == "COMPONENT")
                    {
                        foreach (DbElement element in elementCollection1)
                        {
                            if (element.GetBool(DbAttributeInstance.LOCK))
                            {
                                int num2 = (int)MessageBox.Show($"Branch : {element.GetAsString(DbAttributeInstance.NAMN)}被锁定", "警告", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                                return;
                            }
                            if (!element.IsAttributeValid(attribute))
                            {
                                int num3 = (int)MessageBox.Show($"Branch的{attribute.Name}属性无效", "属性设置错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                                return;
                            }
                            if (!element.IsAttributeSetable(attribute))
                            {
                                int num4 = (int)MessageBox.Show($"Branch的{attribute.Name}属性无法设置", "属性设置错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                                return;
                            }
                            drawTool.SetAttribute(element, attribute, value);
                        }
                    }
                    if (!(level.ToUpper().Trim() == "COMPONENT"))
                        return;
                    foreach (DbElement element in elementCollection2)
                    {
                        if (!(element.GetElementType() == DbElementTypeInstance.TUBING) && !element.GetBool(DbAttributeInstance.LOCK))
                        {
                            if (!element.IsAttributeValid(attribute) && !element.IsAttributeSetable(attribute))
                            {
                                int num5 = (int)MessageBox.Show($"{element.GetAsString(DbAttributeInstance.TYPE)}不包含{attribute.Name}属性", "属性设置错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                                break;
                            }
                            if (!(element.GetElementType() == DbElementTypeInstance.ATTACHMENT) || !element.GetBool(DbAttributeInstance.SPKBRK))
                                drawTool.SetAttribute(element, attribute, value);
                        }
                    }
                }
                else
                {
                    int num6 = (int)MessageBox.Show($"Pipe的{attribute.Name}属性无法设置", "属性设置错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                }
            }
        }

        public static string FullType(DbElement element)
        {
            return element.GetAsString(DbAttributeInstance.FULSYS);
        }
        public static void getNozzInfo(DbElement nozz)
        {

       
                Direction nDirX = Direction.Create(Axis.X);
                Direction nozzDir = nozz.GetDirection(ATT.PDIR, 1);
   
                Double Anglex = nDirX.Angle(nozzDir);
                Direction nDirY = Direction.Create(Axis.Y);
                Double Angle = 0;
              

                if (Anglex > 0 && Anglex < 90)
                {

                    Angle = Math.Round(nDirY.Angle(nozz.GetDirection(ATT.PDIR, 1)));

                }
                else
                {
                    Angle = Math.Round(360 - nDirY.Angle(nozz.GetDirection(ATT.PDIR, 1)));

                }
              
                if (Angle == 360)
                {
                    Angle = 0;
                }
               
                String nozzCatref = nozz.GetAsString(ATT.CATR);
                String nozzBore = nozz.GetAsString(ATT.PROP, ATT.APAR).Replace("mm", "");
               
                
            
            
        }

        // ===== PDMS Draft 视图生成（E3D Draw 移植补全）=====
        // 参考：G:\方圆资料\PDMS平面出图.pdf
        // PDMS 中 VIEW 建在 SHEET 下，自动引用其 DRAW(DRWG) 层 DLLB 出图清单(IDLIST)抽取 3D 模型；
        // 关键属性：VTYP(视图类型) DIR(方向) VSCA(比例) THPO(视点模型坐标) XYPS(图面位置) ADEG(旋转角) SIZE(视窗尺寸)。
        // E3D 原 drawTool 不建 VIEW（依赖 E3D 自带 Draw 自动建），PDMS 端必须手动建。

        /// <summary>
        /// 在 SHEET 下创建一张 PDMS Draft 视图，并设置标准出图属性。
        /// </summary>
        public static DbElement CreateView(DbElement sheet, double scale, string dirToken, double angleDeg, double[] sheetPos, double[] viewSize)
        {
            DbElement view = sheet.CreateLast(DbElementTypeInstance.VIEW);
            // 视图类型：PLAN 平面 / ELEV 立面 / SECT 剖视 / GEN 通用
            drawTool.SetAttribute(view, DbAttributeInstance.VTYP, "PLAN");
            // 视图方向（plan 视图通常朝向下 "D"）
            view.SetAttribute(DbAttributeInstance.DIR, Direction.Create(dirToken));
            // 出图比例
            drawTool.SetAttribute(view, DbAttributeInstance.VSCA, scale.ToString());
            // 视点原点（模型坐标）
            view.SetAttribute(DbAttributeInstance.THPO, Position.Create(0.0, 0.0, 0.0));
            // 图面位置（视图原点在图纸上的坐标）
            view.SetAttribute(DbAttributeInstance.XYPS, sheetPos);
            // 视角旋转角
            drawTool.SetAttribute(view, DbAttributeInstance.ADEG, angleDeg.ToString());
            // 视窗尺寸
            view.SetAttribute(DbAttributeInstance.SIZE, viewSize);
            return view;
        }

        /// <summary>
        /// 找到 SHEET/DRWG 对应的出图清单 DLLB（DRWG -> LIBY -> DLLB）。
        /// </summary>
        public static DbElement FindDrawlist(DbElement start)
        {
            DbElement drwg = start.GetElementType() == DbElementTypeInstance.SHEET ? start.Owner : start;
            DbElement liby = DbElement.GetElement();
            foreach (DbElement m in drwg.Members())
            {
                if (m.GetElementType() == DbElementTypeInstance.LIBY) { liby = m; break; }
            }
            DbElement dllb = DbElement.GetElement();
            if (!liby.IsNull)
            {
                foreach (DbElement m in liby.Members())
                {
                    if (m.GetElementType() == DbElementTypeInstance.DLLB) { dllb = m; break; }
                }
            }
            return dllb;
        }

        /// <summary>
        /// 把设计模型元素加入出图清单（DLLB 下 add idli）。
        /// 注：PDMS Draft 的精确 PML 命令请对照 G:\方圆资料\PDMS12.1.sp4 Help\Documentation\DRUGP.chm 运行时确认。
        /// </summary>
        public static void PopulateDrawlist(DbElement dllb, string modelName)
        {
            drawTool.CommandExcute(dllb.GetAsString(DbAttributeInstance.NAME) + " add idli " + modelName);
        }

        /// <summary>
        /// 在视图中创建一条引线标注（VNOTE + TEXTPRIMITIVE + 45° 斜引线）。
        /// 复用陈永发 FangYuan.PDMS.Draft.CreateViewNote；引线用 ChemSymbols.Seg 画（STRAIGHT+Fpt/Tpt）。
        ///
        /// 标注摆放约定（2026-10-28 聂工明示）：
        ///   1) 引线优先 45° 斜引线（不是纯水平/垂直正交引线）；
        ///   2) 标注位置优先级：右上 > 左上（引线指向特征点时，优先放元素右上角，右上放不下再放左上）。
        ///
        /// 参数：
        ///   view     : 目标视图（VNOTE 与 45° 引线均挂在其下）
        ///   text     : 标注文本
        ///   x, y     : 被标注特征在图面(mm)上的锚点（引线起点）
        ///   labelDist: 锚点到文本基准点的斜距(mm)，默认 30
        ///   textW/H  : 文本半宽/半高(mm)，用于边界避让，默认 25/5
        ///   bounds   : 视图图面范围 [xmin,ymin,xmax,ymax]，null 则跳过边界检查
        /// 返回创建的 VNOTE 元素（45° 引线为同视图下独立 STRAIGHT）。
        /// </summary>
        public static DbElement CreateAnnotation(DbElement view, string text, double x, double y,
            double labelDist = 30.0, double textW = 25.0, double textH = 5.0, double[] bounds = null)
        {
            // 候选方位：①右上(45°) ②左上(135°)；均按 45° 斜引线
            double[] angles = { 45.0, 135.0 };
            double bx = x, by = y, px = x, py = y;
            bool placed = false;
            foreach (double ang in angles)
            {
                double rad = ang * Math.PI / 180.0;
                double cx = x + labelDist * Math.Cos(rad);   // 文本连接点（引线末端）
                double cy = y + labelDist * Math.Sin(rad);
                // 左上方位时整体左移一个全宽，使文本留在锚点左侧而非压在上方
                double tx = (Math.Abs(ang - 135.0) < 1e-6) ? cx - 2.0 * textW : cx;
                double ty = cy;
                if (bounds != null && bounds.Length >= 4 &&
                    (tx - textW < bounds[0] || ty - textH < bounds[1] ||
                     tx + textW > bounds[2] || ty + textH > bounds[3]))
                    continue; // 该方位出界，试下一个（右上不行再试左上）
                bx = tx; by = ty; px = cx; py = cy; placed = true; break;
            }
            if (!placed) // 兜底：保持右上方 45° 偏移，确保总能落一个位置
            {
                double k = labelDist * 0.70710678;
                bx = x + k; by = y + k; px = bx; py = by;
            }
            DbElement note = FangYuan.PDMS.Draft.CreateViewNote(view, "/LB" + Guid.NewGuid().ToString("N").Substring(0, 6));
            DbElement tp = note.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
            drawTool.SetAttribute(tp, DbAttributeInstance.STEX, text);
            tp.SetAttribute(DbAttributeInstance.POS, Position.Create(bx, by, 0.0));
            // 45° 斜引线：锚点 -> 文本连接点
            ChemSymbols.Seg(view, x, y, px, py, 1);
            return note;
        }
    }

    /// <summary>守卫/诊断日志（落盘，UI 线程 fatal 也不丢）。D:\pdmsAgent\bridge\bridge_cmds.log</summary>
    public static class BridgeLog
    {
        private static readonly object Lock = new object();
        private const string Path = @"D:\pdmsAgent\bridge\bridge_cmds.log";
        public static void Write(string line)
        {
            try
            {
                lock (Lock)
                    System.IO.File.AppendAllText(Path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + line + Environment.NewLine);
            }
            catch { }
        }
    }

    /// <summary>同时把每行追加写盘的日志缓冲（StringBuilder 密封不可继承，用包装）。
    /// 2026-10-15 op #2 教训：纯内存 op 日志随 GUI fatal（UltraTree OnPaint NRE）丢失，
    /// 导致 (2,202) =0/0 的具体触发行无法取证——op 日志必须落盘。
    /// 调用面与 StringBuilder 子集一致：AppendLine(string) / ToString()。</summary>
    public class FileLoggingStringBuilder
    {
        private readonly StringBuilder _sb = new StringBuilder();
        private readonly System.IO.StreamWriter _w;
        /// <summary>2026-10-20 出图提速：全行带时间戳——replace_symbols 15-20 分钟的
        /// 阶段耗时分布全靠它取证（竞品 2 分钟出图，必须知道慢在哪一步）。</summary>
        public static System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
        public FileLoggingStringBuilder(string path)
        {
            try { _w = new System.IO.StreamWriter(path, true, System.Text.Encoding.UTF8); }
            catch { _w = null; }
        }
        public void AppendLine(string value)
        {
            string stamped = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + value;
            _sb.AppendLine(stamped);
            try { if (_w != null) { _w.WriteLine(stamped); _w.Flush(); } } catch { }
        }
        public void AppendLine()
        {
            _sb.AppendLine();
            try { if (_w != null) { _w.WriteLine(); _w.Flush(); } } catch { }
        }
        public override string ToString() { return _sb.ToString(); }
        public void CloseFile() { try { if (_w != null) _w.Close(); } catch { } }
    }
}
