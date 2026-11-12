using Aveva.Pdms.Database;
using Aveva.Pdms.Utilities.CommandLine;
using drawingPDMS;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using DbType = Aveva.Pdms.Database.DbType;
using CE = Aveva.Pdms.Shared.CurrentElement;
using ATT = Aveva.Pdms.Database.DbAttributeInstance;
using Filter = Aveva.PDMS.Database.Filters.TypeFilter;
using Aveva.Pdms.Graphics;
using Aveva.Pdms.Geometry;
using pic = Aveva.Pdms.Graphics;
using Aveva.PDMS.Database.Filters;

using System.IO;
using Orientation = Aveva.Pdms.Geometry.Orientation;
using System.Net;
using System.Text.RegularExpressions;
namespace drawingPDMS
{
    public partial class drawE3D : UserControl
    {
        public drawE3D()
        {
            InitializeComponent();
            // 桥接注册（kind:"op" 操作派发到本控件线程 = PDMS UI 线程，与窗口按钮同线程执行）
            PdmsBridge.RegisterUi(this);
            comboBox1.Items.Add("管道布置图");
            comboBox1.Items.Add("设备布置图");
            comboBox1.Items.Add("仪表条件图");
            comboBox1.Items.Add("埋板条件图");
            comboBox1.Items.Add("管口方位图");
            comboBox1.SelectedIndex = 0;
            this.textBox7.Text = @"D:\";
            // ITD 风格：纯数值比例列表（分母），比 "1/50" 更专业
            string[] scaleItems = { "30", "50", "100", "150", "200", "250", "300", "500", "1000" };
            comboBox5.Items.AddRange(scaleItems);
            comboBox5.SelectedIndex = 1; // 默认 1/50
            textBox8.Text = "/DRA/MAS/BACKS/MET/A0";
            // PDMS 默认管道表示库（master.dat 中的 View1）
            // 如果项目有自定义库，可改为 /DRA/PRJ/REPR/GEN/PIPE
            textBox9.Text = "/DRA/MAS/H&S/RULESETS/View1";
            textBox10.Text = "/DRA/PRJ/HRUL/GEN/Hatch-None";

            // ===== 视图 API 实机测试页签（程序内调用，无需外部 PML 脚本）=====
            // 测试引擎：viewTest.cs / ViewApiTest（模块探测→几何令牌→PML变量→DB视图链→DrawList）
            TabPage viewTestTab = new TabPage("视图API测试");
            btnViewTest = new Button();
            btnViewTest.Text = "运行全链路测试";
            btnViewTest.Location = new Point(8, 8);
            btnViewTest.Size = new Size(130, 28);
            btnViewTest.Click += new EventHandler(BtnViewTest_Click);
            txtViewTestLog = new TextBox();
            txtViewTestLog.Multiline = true;
            txtViewTestLog.ReadOnly = true;
            txtViewTestLog.ScrollBars = ScrollBars.Both;
            txtViewTestLog.WordWrap = false;
            txtViewTestLog.Font = new Font("Consolas", 9F);
            txtViewTestLog.Location = new Point(8, 44);
            txtViewTestLog.Size = new Size(317, 258);
            viewTestTab.Controls.Add(btnViewTest);
            viewTestTab.Controls.Add(txtViewTestLog);
            drawPage.TabPages.Add(viewTestTab);
        }

        private Button btnViewTest;
        private TextBox txtViewTestLog;

        private void BtnViewTest_Click(object sender, EventArgs e)
        {
            btnViewTest.Enabled = false;
            txtViewTestLog.Clear();
            txtViewTestLog.AppendText("运行中（需数十秒，请勿操作模型）...\n\n");
            try
            {
                // 增量输出：每步完成立即刷新，长步骤期间也能看到进度
                ViewApiTest.Run(delegate (string line) { txtViewTestLog.AppendText(line); });
                int p = ViewApiTest.PassCount;
                int f = ViewApiTest.FailCount;
                MessageBox.Show("视图 API 实机测试完成：\nPASS " + p + " / FAIL " + f,
                    "视图API测试", MessageBoxButtons.OK,
                    f > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                txtViewTestLog.AppendText("测试中断：" + ex);
                MessageBox.Show(ex.ToString());
            }
            finally
            {
                btnViewTest.Enabled = true;
            }
        }
        
        private void button1_Click(object sender, EventArgs e)
        {

        }

        private void button2_Click(object sender, EventArgs e)
        {
            // TODO PDMS: 防碰撞占位区（LimitsBox2D/Position2D 为 E3D 专属几何类型，PDMS 下停用；原逻辑见 _reference）
            // List<LimitsBox2D> occupiedAreas = new List<LimitsBox2D>();
            Dictionary<double, double> pointLabel = new Dictionary<double, double>();
            Double Scale = 1.0 / Convert.ToDouble(this.comboBox5.Text);
            Double viewMinX = Convert.ToDouble(textBox1.Text);
            Double viewMaxX = Convert.ToDouble(textBox4.Text);
            Double viewMinY = Convert.ToDouble(textBox2.Text);
            Double viewMaxY = Convert.ToDouble(textBox5.Text);
            Double viewMinZ = Convert.ToDouble(textBox3.Text);
            Double viewMaxZ = Convert.ToDouble(textBox6.Text);
            String DrawingType = comboBox1.Text;
            String DrawingName = "";

            try
            {


                DbElement cueCurrent = CE.Element;
                String newCreatName = cueCurrent.GetAsString(ATT.NAME);
                if (cueCurrent.GetElementType() == DbElementTypeInstance.WORLD)
                {
                    //drawTool.CommandExcute("CALLIBM FCREATE DEPT NO $<SHOW _CDRDEPT $> newCreatName");
                    DbElement deptDraw = cueCurrent.CreateFirst(DbElementTypeInstance.DEPT);
                    DbElement regiDraw = deptDraw.CreateFirst(DbElementTypeInstance.REGISTRY);
                    DbElement drwgDraw = regiDraw.CreateFirst(DbElementTypeInstance.DRWG);
                    if (textBox8.Text.Contains("A4"))
                    {
                        drawTool.CommandExcute(deptDraw.GetAsString(ATT.NAME) + "size 210 297");
                        drawTool.CommandExcute(regiDraw.GetAsString(ATT.NAME) + "size 210 297");
                        drawTool.CommandExcute(drwgDraw.GetAsString(ATT.NAME) + "size 210 297");
                    }
                    DbElement sheetDraw = drawTool.createDrwgDraw(drwgDraw, textBox8.Text);
                    DrawingName = drwgDraw.GetAsString(ATT.NAME) + "/" + DrawingType;
                    drawTool.SetAttribute(sheetDraw, ATT.NAME, drwgDraw.GetAsString(ATT.NAME) + "/" + DrawingType + (drwgDraw.Members().Count()/2).ToString());
                    // drawTool.UpdateModelView(regiDraw);
                    //  drawTool.UpdateModelView(regiDraw);
                    //  drawTool.UpdateModelView(deptDraw);
                    LayerManager.EnsureLayers(sheetDraw);
                    LayerManager.SetCurrentLayer(sheetDraw, DrawLayerCat.Outline); // 外形层(best-effort)：模型几何归层需 PDMS 实测
                    drawTool.UpdateModelView(cueCurrent);

                }
                else if (cueCurrent.GetElementType() == DbElementTypeInstance.DEPT)
                {
                    // drawTool.CommandExcute(" CALLIBM FCREATE REGI NO $< SHOW _CDRREGI $> newCreatName");
                    DbElement regiDraw = cueCurrent.CreateFirst(DbElementTypeInstance.REGISTRY);
                
                    DbElement drwgDraw = regiDraw.CreateFirst(DbElementTypeInstance.DRWG);
                    if (textBox8.Text.Contains("A4"))
                    {
                        drawTool.CommandExcute(regiDraw.GetAsString(ATT.NAME) + "size 210 297");
                        drawTool.CommandExcute(drwgDraw.GetAsString(ATT.NAME) + "size 210 297");
                    }
                   DbElement sheetDraw =  drawTool.createDrwgDraw(drwgDraw, textBox8.Text);
                    DrawingName = drwgDraw.GetAsString(ATT.NAME) + "/" + DrawingType;
                    drawTool.SetAttribute(sheetDraw, ATT.NAME, drwgDraw.GetAsString(ATT.NAME) + "/" + DrawingType + (drwgDraw.Members().Count()-1).ToString());
                    // drawTool.UpdateModelView(regiDraw);
                    LayerManager.EnsureLayers(sheetDraw);
                    LayerManager.SetCurrentLayer(sheetDraw, DrawLayerCat.Outline); // 外形层(best-effort)：模型几何归层需 PDMS 实测
                    drawTool.UpdateModelView(cueCurrent);
                }
                else if (cueCurrent.GetElementType() == DbElementTypeInstance.REGISTRY)
                {
                    //drawTool.CommandExcute("CALLIBM FCREATE DRWG NO $< SHOW _CDRGDPDRWG $> newCreatName");
                    DbElement drwgDraw = cueCurrent.CreateFirst(DbElementTypeInstance.DRWG);
                    if (textBox8.Text.Contains("A4"))
                    {
                        drawTool.CommandExcute(drwgDraw.GetAsString(ATT.NAME) + "size 210 297");

                    }
                    DbElement sheetDraw = drawTool.createDrwgDraw(drwgDraw, textBox8.Text);
                    // drawTool.UpdateModelView(drwgDraw);
                    LayerManager.EnsureLayers(sheetDraw);
                    LayerManager.SetCurrentLayer(sheetDraw, DrawLayerCat.Outline); // 外形层(best-effort)：模型几何归层需 PDMS 实测
                    drawTool.UpdateModelView(cueCurrent);
                }
                else if (cueCurrent.GetElementType() == DbElementTypeInstance.DRWG)
                {

                    drawTool.createDrwgDraw(cueCurrent, textBox8.Text);

                    // drawTool.CommandExcute("!!TEMPCIESTRING=!!CDRDFLTS.DVSD.VAL !!TEMPCIESTRING = !!CDRDFLTS.DVSL.VAL !!TEMPCIESTRING = !!CDRDFLTS.DVST.VAL !!TEMPCIESTRING = !!CDRDFLTS.DVSV.VAL !!TEMPCIESTRING = !!CDRDFLTS.DVSR.VAL",true);

                    // drawTool.UpdateModelView(libyDraw);
                }
                else if (cueCurrent.GetElementType() == DbElementTypeInstance.SHEET)
                {
                    drawTool.SetAttribute(cueCurrent, ATT.EXFI, "10000");
                    cueCurrent.SetAttribute(ATT.BSRF, DbElement.GetElement(textBox8.Text));
                    cueCurrent.Owner.SetAttribute(ATT.BSRF, DbElement.GetElement(textBox8.Text));
                    drawTool.SetAttribute(cueCurrent, ATT.NAME, cueCurrent.Owner.GetAsString(ATT.NAME)+"/"+ DrawingType+ (cueCurrent.Owner.Members().Count()/2).ToString());
                    DbElement viewDraw = cueCurrent.CreateFirst(DbElementTypeInstance.VIEW);
                    LayerManager.EnsureLayers(viewDraw); // 标注线/文字 标准层(View 下)
                    // pic.View3DManager.Instance.CurrentView.SetViewLimits(new double[] { viewMinX, viewMaxX, viewMinY, viewMaxY, viewMinZ, viewMaxZ });
                    drawTool.SetAttribute(viewDraw, ATT.VFRCOL, "2");
                    // ===== 收集目标（PDMS 官方 UIDLIVOL 体积框选，替代手动 DLLB ADD IDLI）=====
                    // 优先级2：若用户在 PDMS 界面已通过 :CDLIMITS 设置了视图范围，创建后由 GetViewLimits 读回
                    // 优先级1/3：用 ADD/REM 规则的体积框选创建绘制列表

                    // 1) 组装体积范围串（W/N/U 世界坐标，WITHIN 子句格式）
                    string limitsBox = string.Format("W{0} N{1} U{2} TO W{3} N{4} U{5}",
                        viewMinX, viewMinY, viewMinZ, viewMaxX, viewMaxY, viewMaxZ);

                    // 2) 设置视图基本属性（用 API 写属性；按未命名元素的 NAME 串发命令会
                    //    "CP: Syntax error"，且失败后 CE 停在 WORLD，殃及后续所有命令）
                    drawTool.SetAttribute(viewDraw, ATT.VTYP, "Universal");
                    viewDraw.SetAttribute(ATT.RRSF, DbElement.GetElement(textBox9.Text));
                    viewDraw.SetAttribute(ATT.HRSF, DbElement.GetElement(textBox10.Text));
                    viewDraw.SetAttribute(ATT.VSCA, Scale);
                    Position DrawlistTpostion = Position.Create(
                        (viewMinX + viewMaxX) * 0.5,
                        (viewMinY + viewMaxY) * 0.5,
                        (viewMinZ + viewMaxZ) * 0.5);
                    viewDraw.SetAttribute(ATT.THPO, DrawlistTpostion);

                    // 3) 体积框选：ADD/REM 命令仅在 ID List 上下文有效（PDMS 实测报
                    //    "This command is only available at ID Lists"——之前 CD 到 VIEW 是错的）。
                    //    改为找到/创建 DLLB 下的 IDLIST，并用 CurrentElement API 切 CE
                    //    （陈永发 UpdateModelView 同款写法，替代 CD 命令——未命名元素的
                    //    NAME 串发 CD 会 "CP: Syntax error"，失败后命令全落在 WORLD 上）。
                    DbElement savedElem = CE.Element;
                    DbElement drawlistDllb = drawE3DTool.FindCurrentDrawlistUnder(cueCurrent.Owner);
                    DbElement idlist = drawE3DTool.FindOrCreateIdlist(drawlistDllb);
                    if (idlist != null && !idlist.IsNull)
                    {
                        CE.Element = idlist; // CE -> IDLIST，ADD/REM 落到该清单

                        string addTypes = drawE3DTool.GetAddTypesForDrawingType(DrawingType);
                        string addRules = drawE3DTool.GetAddRulesForDrawingType(DrawingType);
                        string remTypes = drawE3DTool.GetRemTypesForDrawingType(DrawingType);
                        string remRules = drawE3DTool.GetRemRulesForDrawingType(DrawingType);
                        drawE3DTool.CreateVolumeDrawlist(
                            idlist.GetAsString(ATT.NAME),
                            limitsBox, addTypes, addRules, remTypes, remRules);

                        // 3.1) 手动选择的元素（listBox1）→ ADDENTRY(IDNM)，纯 API 写法
                        //      （E3D 原版 drawE3D.cs:158 同款，绕开命令上下文限制）
                        foreach (String add in listBox1.Items)
                        {
                            if (string.IsNullOrEmpty(add)) continue;
                            DbElement manEle = DbElement.GetElement(add);
                            if (manEle != null && !manEle.IsNull)
                            {
                                DbElement addEntry = idlist.CreateLast(DbElementTypeInstance.ADDENTRY);
                                addEntry.SetAttribute(ATT.IDNM, manEle);
                            }
                        }

                        // 5) 视图 IDLN 指向该 IDLIST（视图由此抽取模型，修复 "empty ID list"）
                        viewDraw.SetAttribute(ATT.IDLN, idlist);
                        cueCurrent.SetAttribute(ATT.IDLN, idlist);

                        // 5.1) 动态计算视图 SIZE（根据内容包围盒 + 比例，避免固定 A4 图框）
                        //      PDMS 会在 IDLI 填充后自动调整窗口，但显式设 SIZE 更稳定
                        try
                        {
                            LimitsBox contentBox = drawE3DTool.ViewLimitBox(viewDraw);
                            // LimitsBox.Create() 返回空盒（最小值为正无穷，最大值为负无穷）
                            // 通过 Minimum 属性判断是否为空
                            if (contentBox.Minimum.X < double.MaxValue)
                            {
                                double w = (contentBox.Maximum.X - contentBox.Minimum.X) * Scale + 2000; // 2m padding
                                double h = (contentBox.Maximum.Y - contentBox.Minimum.Y) * Scale + 2000;
                                // 保持宽高比接近 16:9，避免畸变
                                double targetRatio = 16.0 / 9.0;
                                double currentRatio = w / h;
                                if (currentRatio > targetRatio)
                                {
                                    h = w / targetRatio;
                                }
                                else
                                {
                                    w = h * targetRatio;
                                }
                                viewDraw.SetAttribute(ATT.SIZE, new double[] { w, h });
                                // THPO 设为中心点（使用 Centre() 方法）
                                Position center = contentBox.Centre();
                                viewDraw.SetAttribute(ATT.THPO, center);
                            }
                        }
                        catch { }
                    }
                    // 恢复 CE（API 方式，替代之前失败的 CD 命令）
                    CE.Element = savedElem;

                    // 4) 优先级2：读回视图实际范围（:CDLIMITS -> VVOL），同步文本框与本地变量
                    string actualLimits = drawE3DTool.GetViewLimits(viewDraw);
                    if (!string.IsNullOrEmpty(actualLimits))
                    {
                        double[] lb = drawE3DTool.ParseLimitsString(actualLimits);
                        if (lb != null)
                        {
                            viewMinX = lb[0]; viewMaxX = lb[1];
                            viewMinY = lb[2]; viewMaxY = lb[3];
                            viewMinZ = lb[4]; viewMaxZ = lb[5];
                            textBox1.Text = Math.Round(viewMinX).ToString();
                            textBox4.Text = Math.Round(viewMaxX).ToString();
                            textBox2.Text = Math.Round(viewMinY).ToString();
                            textBox5.Text = Math.Round(viewMaxY).ToString();
                            textBox3.Text = Math.Round(viewMinZ).ToString();
                            textBox6.Text = Math.Round(viewMaxZ).ToString();
                        }
                    }

                    // （原步骤5 IDLN 查找循环、步骤6 CD 回切已并入上方 IDLIST 块：
                    //   IDLN 直接绑定 FindOrCreateIdlist 的结果，CE 用 API 恢复）
                    //  drawE3DTool.CreateLayer(cueCurrent, "", "config", drawE3DTool.DimensionDataTable());
                    if (comboBox1.Text == "管道布置图" || comboBox1.Text == "设备布置图" || comboBox1.Text == "埋板布置图")
                    {
                        //创建投影面
                        List<DbElement> eles = drawE3DTool.ElementsFromDrawlist(viewDraw, new TypeFilter(DbElementTypeInstance.ELBOW), false, false);
                        List<DbElement> pipeCollection = Graphic.getPipeCollect(eles);

                        // ===== 出图 5 图层体系：标注线/文字 建在 VIEW 下；外形/图框/表格 建在 SHEET 下 =====
                        DbElement lDim = LayerManager.GetLayer(viewDraw, DrawLayerCat.Dim);
                        DbElement lText = LayerManager.GetLayer(viewDraw, DrawLayerCat.Text);
                        DbElement lOutline = LayerManager.GetLayer(cueCurrent, DrawLayerCat.Outline);
                        DbElement lFrame = LayerManager.GetLayer(cueCurrent, DrawLayerCat.Frame);
                        DbElement lTable = LayerManager.GetLayer(cueCurrent, DrawLayerCat.Table);
                        drawTool.SetAttribute(lDim, ATT.LSHA, "Angled Horizontal");
                        //收集轴网信息
                        List<DbElement> eleGrids = drawE3DTool.ElementsFromDrawlist(viewDraw, new TypeFilter(DbElementTypeInstance.GRIDWLD), false, false);
                        //仪表
                        List<DbElement> eleInsts = drawE3DTool.ElementsFromDrawlist(viewDraw, new TypeFilter(DbElementTypeInstance.INSTRUMENT), false, false);
                        //结构
                        List<DbElement> eleStrus = drawE3DTool.ElementsFromDrawlist(viewDraw, new TypeFilter(DbElementTypeInstance.STRUCTURE), false, false);
                        //设备
                        List<DbElement> eleEquis = drawE3DTool.ElementsFromDrawlist(viewDraw, new TypeFilter(DbElementTypeInstance.EQUIPMENT), false, false);
                        //阀门
                        List<DbElement> eleValvs = drawE3DTool.ElementsFromDrawlist(viewDraw, new TypeFilter(DbElementTypeInstance.VALVE), false, false);
                        //法兰
                        List<DbElement> eleFlans = drawE3DTool.ElementsFromDrawlist(viewDraw, new TypeFilter(DbElementTypeInstance.FLANGE), false, false);

                        eles.AddRange(eleInsts);
                        eles.AddRange(eleGrids);
                         eles.AddRange(eleStrus);
                          eles.AddRange(eleEquis);
                        // eles.AddRange(eleValvs);
                        //  eles.AddRange(eleFlans);
                        //DbElement ldimDraw =  drawE3DTool.CreateLdim(layerDraw, "", Direction.Create("Down"));
                        Graphic.createLdimElement(lDim, eles, "Left", viewMinX, viewMaxX, viewMinY, viewMaxY, DrawingType);
                        Graphic.createLdimElement(lDim, eles, "Right", viewMinX, viewMaxX, viewMinY, viewMaxY, DrawingType);
                        Graphic.createLdimElement(lDim, eles, "Top", viewMinX, viewMaxX, viewMinY, viewMaxY, DrawingType);
                        Graphic.createLdimElement(lDim, eles, "Bottom", viewMinX, viewMaxX, viewMinY, viewMaxY, DrawingType);


                        // drawTool.UpdateModelView(layerDraw);
                        //创建设备标注标签
                        // 文字层 lText 已在上方定义（设备标注 GLABEL 走 lText）

                        // drawTool.UpdateModelView(layerLab);
                        DbElement noteCenter = lOutline.CreateLast(DbElementTypeInstance.NOTE);
                        drawTool.SetAttribute(noteCenter, ATT.NAME, noteCenter.Owner.GetAsString(ATT.NAME) + "/设备中心线");
                        DbElement noteFrame = lFrame.CreateLast(DbElementTypeInstance.NOTE);
                        drawTool.SetAttribute(noteFrame, ATT.NAME, noteFrame.Owner.GetAsString(ATT.NAME) + "/图框");
                        
                       

                        foreach (DbElement ele in eleEquis)
                        {


                            //drawTool.UpdateModelView(layerLab);
                           // drawTool.CommandExcute("UPDATE ALL");
                            DbElement glabDraw = lText.CreateLast(DbElementTypeInstance.GLABEL);
                            glabDraw.SetAttribute(ATT.BTEX, "#NAME(C2:) #/" + "POS EL " + "#POSU+");
                            glabDraw.SetAttribute(ATT.TSIZ, 2.5);
                            glabDraw.SetAttribute(ATT.DDNM, ele);
                            //设备标注位置
                            double gridPosY = ele.GetPosition(ATT.WORPOS).Y;
                            double midViewy = viewDraw.GetPosition(ATT.THPO).Y;
                            double disy = (gridPosY - midViewy) * viewDraw.GetDouble(ATT.VSCA);
                            double m = viewDraw.GetDoubleArray(ATT.XYPS)[1] + disy;

                            double gridPosX = ele.GetPosition(ATT.WORPOS).X;
                            double midViewX = viewDraw.GetPosition(ATT.THPO).X;
                            double disx = (gridPosX - midViewX) * viewDraw.GetDouble(ATT.VSCA);
                            double n = viewDraw.GetDoubleArray(ATT.XYPS)[0] + disx;
                            drawTool.CommandExcute(glabDraw.GetAsString(ATT.NAME) + $" LSHAPE BENT ");
                            drawTool.CommandExcute(glabDraw.GetAsString(ATT.NAME) + $" at x " + n+ " y " + m);
                            //Position2D p =Graphic.getObjectPostion(n,m, occupiedAreas,"GLAB");
                            //if (p != null)
                            //{
                            //    drawTool.CommandExcute(glabDraw.GetAsString(ATT.NAME) + $" at x " + p.X +" y "+p.Y);
                            //    LimitsBox2D limitsBox1 = LimitsBox2D.Create(Position2D.Create(p.X - 20, p.X - 8), Position2D.Create(p.X + 20, p.Y + 8));
                            //    occupiedAreas.Add(limitsBox1);
                            //}
                            //
                            //else if(!pointLabel.ContainsKey(m+5) )
                            //{
                            //  
                            //        drawTool.CommandExcute(glabDraw.GetAsString(ATT.NAME) + $" at x " + n  + " y " +( m + 5));
                            //        pointLabel.Add(m + 5, 1);
                            // 
                            //   
                            //}
                            //else 
                            //{
                            //    
                            //        pointLabel[m + 5]++;
                            //        drawTool.CommandExcute(glabDraw.GetAsString(ATT.NAME) + $" at x " + n   + " y " + (m + 5 + 12 * pointLabel[m + 5]));
                            //  
                            // }
                            //MessageBox.Show("可用点坐标" + p.X.ToString() + "    " + p.X.ToString());

                            drawTool.CommandExcute(glabDraw.GetAsString(ATT.NAME) + " just Centre");
                            glabDraw.SetAttribute(ATT.BLNK, false);
                            glabDraw.SetAttribute(ATT.BLNK, false);
                          
                            //drawTool.UpdateModelView(glabDraw);
                            // listBox1.Items.Add(ele.GetAsString(ATT.NAME));
                            //画设备中心线
                            Double CentreLen = 50;//中心线长度长度
                            Double CentreLenY = 15;//中心线长度长度
                            DbElement centerLine = noteCenter.CreateLast(DbElementTypeInstance.STRAIGHT);
                            DbElement centerLineY = noteCenter.CreateLast(DbElementTypeInstance.STRAIGHT);
                            drawTool.SetAttribute(centerLineY, ATT.LENG, CentreLen.ToString());
                            drawTool.SetAttribute(centerLineY, ATT.NLSTYL, "8004");

                            drawTool.SetAttribute(centerLine, ATT.LENG, CentreLen.ToString());
                            drawTool.SetAttribute(centerLineY, ATT.LENG, CentreLen.ToString());
                            drawTool.SetAttribute(centerLine, ATT.NLSTYL, "8004");

                            centerLine.SetAttribute(ATT.NLCOLO, 2);
                            //Fpt Offset X 0mm Y 0mm Tpt Offset X 52mm Y 0mm
                            centerLineY.SetAttribute(ATT.NLCOLO, 2);
                            drawTool.SetAttribute(centerLineY, ATT.ADEG, "-90");
                            drawTool.CommandExcute(centerLine.GetAsString(ATT.NAME) + " Fpt Offset X -" + CentreLen + "mm Y 0mm Tpt Offset X " + CentreLen + "mm" + " Y 0 mm");

                            // drawTool.CommandExcute(centerLine.GetAsString(ATT.NAME) + " Fpt Offset X -" + CentreLen +"mm Y 0mm Tpt Offset X 0mm"+" Y 0mm");
                            drawTool.CommandExcute(centerLineY.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y " + CentreLenY + "mm Tpt Offset X 0mm" + " Y -" + CentreLenY + " mm");

                            // drawTool.CommandExcute(centerLineY.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y "+ CentreLenY+ "mm Tpt Offset X 0mm" + " Y 0mm");
                          
                            drawTool.CommandExcute(centerLine.GetAsString(ATT.NAME) + $" at x " + (n - CentreLen) + " y " + m);
                            drawTool.CommandExcute(centerLineY.GetAsString(ATT.NAME) + $" at x " + n + CentreLen + " y " + (m + CentreLenY));
                        }
                        //画范围框
                        DbElement drawFrame = noteFrame.CreateLast(DbElementTypeInstance.STRAIGHT);
                        drawTool.SetAttribute(drawFrame, ATT.NLSTYL, "1");
                        drawTool.SetAttribute(drawFrame, ATT.LENG,( viewMaxX- viewMinX).ToString());
                        drawFrame.SetAttribute(ATT.NLCOLO, 10);
                        //Fpt Offset X 0mm Y 0mm Tpt Offset X 52mm Y 0mm
                        // double x = viewDraw.GetDoubleArray(ATT.XYPS)[0]-(viewMaxX - viewMinX) * viewDraw.GetDouble(ATT.VSCA)*0.5;
                        // double y = viewDraw.GetDoubleArray(ATT.XYPS)[1] - (viewMaxY - viewMinY) * viewDraw.GetDouble(ATT.VSCA) * 0.5;
                        double Xmin = Graphic.getLimitDraw(viewDraw, viewMinX, viewMaxX, viewMinY, viewMaxY)[0];
                        double Ymin = Graphic.getLimitDraw(viewDraw, viewMinX, viewMaxX, viewMinY, viewMaxY)[1];
                        double xMax = Graphic.getLimitDraw(viewDraw, viewMinX, viewMaxX, viewMinY, viewMaxY)[2];
                        double yMax = Graphic.getLimitDraw(viewDraw, viewMinX, viewMaxX, viewMinY, viewMaxY)[3];

                        // 边框长度用视图真实幅面 SIZE（mm），与 getLimitDraw 的 SIZE 版边缘一致
                        double frameW = viewDraw.GetDoubleArray(ATT.SIZE)[0];
                        double frameH = viewDraw.GetDoubleArray(ATT.SIZE)[1];
                        drawTool.CommandExcute(drawFrame.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y 0mm Tpt Offset X " + frameW + "mm" + " Y 0mm");
                        drawTool.CommandExcute(drawFrame.GetAsString(ATT.NAME) + $" at x "+ Xmin + " y "+ Ymin);
                        drawFrame.SetAttribute(ATT.NLCOLO, 16);
                        drawTool.SetAttribute(drawFrame, ATT.ADEG, "0");
                        DbElement drawFrameH = noteFrame.CreateLast(DbElementTypeInstance.STRAIGHT);
                        drawFrameH.Copy(drawFrame);
                        drawTool.CommandExcute(drawFrameH.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y 0mm Tpt Offset X " + frameW + "mm" + " Y 0mm");
                        drawTool.CommandExcute(drawFrameH.GetAsString(ATT.NAME) + $" at x " + Xmin + " y " + yMax);
                        //竖线
                        DbElement drawFrameV = noteFrame.CreateLast(DbElementTypeInstance.STRAIGHT);
                        drawTool.SetAttribute(drawFrameV, ATT.NLSTYL, "1");

                        drawTool.SetAttribute(drawFrameV, ATT.LENG, frameH.ToString());
                        drawFrameV.SetAttribute(ATT.NLCOLO, 10);
                        drawTool.SetAttribute(drawFrame, ATT.ADEG, "90");
                        //Fpt Offset X 0mm Y 0mm Tpt Offset X 52mm Y 0mm
                     


                       // x = viewDraw.GetDoubleArray(ATT.XYPS)[0] - (viewMaxX - viewMinX) * viewDraw.GetDouble(ATT.VSCA)*0.5;
                       // y = viewDraw.GetDoubleArray(ATT.XYPS)[1]- (viewMaxY - viewMinY) * viewDraw.GetDouble(ATT.VSCA) * 0.5;
                        drawTool.CommandExcute(drawFrameV.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y 0mm Tpt Offset X 0mm" + " Y " + frameH + " mm");
                        drawTool.CommandExcute(drawFrameV.GetAsString(ATT.NAME) + $" at x " + Xmin + " y " + Ymin);
                        drawFrameV.SetAttribute(ATT.NLCOLO, 16);

                        DbElement drawFrameV2 = noteFrame.CreateLast(DbElementTypeInstance.STRAIGHT);
                        drawFrameV2.Copy(drawFrameV);
                       
                        drawTool.CommandExcute(drawFrameV2.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y 0mm Tpt Offset X 0mm" + " Y " + frameH + " mm");
                        drawTool.CommandExcute(drawFrameV2.GetAsString(ATT.NAME) + $" at x " + xMax + " y " + Ymin); ;
                       //避免碰撞（E3D 专属 LimitsBox2D/Position2D，PDMS 下停用，原逻辑见 _reference）
                        // LimitsBox2D limitsBox2 = LimitsBox2D.Create(Position2D.Create(Xmin - 20, Ymin - 20), Position2D.Create(Xmin + 20, yMax + 20));
                        // occupiedAreas.Add(limitsBox2);
                        // LimitsBox2D limitsBox3 = LimitsBox2D.Create(Position2D.Create(Xmin - 20, Ymin - 20), Position2D.Create(xMax + 20, Ymin + 20));
                        // occupiedAreas.Add(limitsBox3);
                        // LimitsBox2D limitsBox4 = LimitsBox2D.Create(Position2D.Create(xMax - 20 , Ymin - 20), Position2D.Create(xMax + 20, yMax + 20));
                        // occupiedAreas.Add(limitsBox4);
                        // LimitsBox2D limitsBox5 = LimitsBox2D.Create(Position2D.Create(Xmin - 20, yMax - 20), Position2D.Create(xMax + 20, yMax + 20));
                        // occupiedAreas.Add(limitsBox5);
                       
                       // //加入箭头
                       DbElement arrowLab = LayerManager.GetLayer(viewDraw, DrawLayerCat.Text);

                        drawTool.SetAttribute(arrowLab, ATT.NAME, arrowLab.Owner.GetAsString(ATT.NAME) + "管道流向");

                        foreach (DbElement arrow in eles)
                        {
                            if (arrow.GetElementType() == DbElementTypeInstance.ELBOW && arrow.GetAsString(ATT.LDIR) != "U" && arrow.GetAsString(ATT.LDIR) != "D")
                            {
                                Double arrowBore = arrow.GetElement(ATT.CATR).GetDoubleArray(ATT.PARA)[0];
                                DbElement arrOne = arrowLab.CreateLast(DbElementTypeInstance.SLABEL);
                                arrOne.SetAttribute(ATT.TMRF, DbElement.GetElement("/TT/Isometric_ADP/Arrow1"));
                                arrOne.SetAttribute(ATT.DDNM, arrow);
                                double arrawAngle = drawTool.getArrawAngle(arrow);
                                //箭头y坐标
                                double gridPosY = arrow.GetPosition(ATT.WORPOS).Y;
                                double midViewY = arrowLab.Owner.GetPosition(ATT.THPO).Y;
                                double disy = (gridPosY - midViewY) * arrowLab.Owner.GetDouble(ATT.VSCA);
                                double y = arrowLab.Owner.GetDoubleArray(ATT.XYPS)[1] + disy + 8 * Math.Sin((arrawAngle - 180) * Math.PI / 180);
                                //箭头x坐标
                                double gridPosX = arrow.GetPosition(ATT.WORPOS).X;
                                double midViewX = arrowLab.Owner.GetPosition(ATT.THPO).X;
                                double disx = (gridPosX - midViewX) * arrowLab.Owner.GetDouble(ATT.VSCA);
                                double x = arrowLab.Owner.GetDoubleArray(ATT.XYPS)[0] + disx + 8 * Math.Cos((arrawAngle - 180) * Math.PI / 180);
                                if (arrowBore < 50)
                                {
                                    x = arrowLab.Owner.GetDoubleArray(ATT.XYPS)[0] + disx + 4 * Math.Cos((arrawAngle - 180) * Math.PI / 180);
                                    y = arrowLab.Owner.GetDoubleArray(ATT.XYPS)[1] + disy + 4 * Math.Sin((arrawAngle - 180) * Math.PI / 180);
                                }
                                drawTool.CommandExcute(arrOne.GetAsString(ATT.NAME) + $"at x " + x + " y " + y);
                                //设置箭头转向


                                drawTool.SetAttribute(arrOne, ATT.ADEG, arrawAngle.ToString());
                                drawTool.SetAttribute(arrOne, ATT.FCOLOU, "4");
                                drawTool.SetAttribute(arrOne, ATT.LLEA, "false");
                            }
                            else if (arrow.GetElementType() == DbElementTypeInstance.ELBOW && arrow.GetAsString(ATT.LDIR) == "D")
                            {
                                Double arrowBore = arrow.GetElement(ATT.CATR).GetDoubleArray(ATT.PARA)[0];
                                DbElement arrOne = arrowLab.CreateLast(DbElementTypeInstance.SLABEL);
                                arrOne.SetAttribute(ATT.TMRF, DbElement.GetElement("/MDS/ABA/TAGS/ELBO"));
                                arrOne.SetAttribute(ATT.DDNM, arrow);
                                double arrawAngle = drawTool.getElboAngle(arrow);
                                //弯头y坐标
                                double gridPosY = arrow.GetPosition(ATT.WORPOS).Y;
                                double midViewY = arrowLab.Owner.GetPosition(ATT.THPO).Y;
                                double disy = (gridPosY - midViewY) * arrowLab.Owner.GetDouble(ATT.VSCA);
                               double y = arrowLab.Owner.GetDoubleArray(ATT.XYPS)[1] + disy;
                                //弯头x坐标
                                double gridPosX = arrow.GetPosition(ATT.WORPOS).X;
                                double midViewX = arrowLab.Owner.GetPosition(ATT.THPO).X;
                                double disx = (gridPosX - midViewX) * arrowLab.Owner.GetDouble(ATT.VSCA);
                                double x = arrowLab.Owner.GetDoubleArray(ATT.XYPS)[0] + disx;

                                drawTool.CommandExcute(arrOne.GetAsString(ATT.NAME) + $"at x " + x + " y " + y);
                                // drawTool.CommandExcute(arrOne.GetAsString(ATT.NAME) + " Xyscale "  +(arrowBore / 100) + " " + (arrowBore / 100));
                                //设置箭头转向


                                drawTool.SetAttribute(arrOne, ATT.ADEG, arrawAngle.ToString());
                                drawTool.SetAttribute(arrOne, ATT.FCOLOU, "9");
                                drawTool.SetAttribute(arrOne, ATT.LLEA, "false");

                            }
                            else if (arrow.GetElementType() == DbElementTypeInstance.ELBOW && arrow.GetAsString(ATT.LDIR) == "U")
                            {
                                Double arrowBore = arrow.GetElement(ATT.CATR).GetDoubleArray(ATT.PARA)[0];
                                DbElement arrOne = arrowLab.CreateLast(DbElementTypeInstance.SLABEL);
                                arrOne.SetAttribute(ATT.TMRF, DbElement.GetElement("/MDS/ABA/TAGS/ELBO--UP"));
                                arrOne.SetAttribute(ATT.DDNM, arrow);
                                //弯头y坐标
                                double gridPosY = arrow.GetPosition(ATT.WORPOS).Y;
                                double midViewY = arrowLab.Owner.GetPosition(ATT.THPO).Y;
                                double disy = (gridPosY - midViewY) * arrowLab.Owner.GetDouble(ATT.VSCA);
                                double y = arrowLab.Owner.GetDoubleArray(ATT.XYPS)[1] + disy;
                                //弯头x坐标
                                double gridPosX = arrow.GetPosition(ATT.WORPOS).X;
                                double midViewX = arrowLab.Owner.GetPosition(ATT.THPO).X;
                                double disx = (gridPosX - midViewX) * arrowLab.Owner.GetDouble(ATT.VSCA);
                                double x = arrowLab.Owner.GetDoubleArray(ATT.XYPS)[0] + disx;

                                drawTool.CommandExcute(arrOne.GetAsString(ATT.NAME) + $"at x " + x + " y " + y);
                                //drawTool.CommandExcute(arrOne.GetAsString(ATT.NAME) + " Xyscale " + (arrowBore / 100) + " " + (arrowBore / 100));
                                //设置箭头转向


                                drawTool.SetAttribute(arrOne, ATT.FCOLOU, "9");
                                drawTool.SetAttribute(arrOne, ATT.LLEA, "false");

                            }
                            else if (arrow.GetElementType() == DbElementTypeInstance.PIPE && arrow.Member(0).Member(0).GetAsString(ATT.LDIR) != "U" && arrow.Member(0).Member(0).GetAsString(ATT.LDIR) != "D")
                            {
                                DbElement arrOne = arrowLab.CreateLast(DbElementTypeInstance.SLABEL);
                                arrOne.SetAttribute(ATT.TMRF, DbElement.GetElement("/TT/Isometric_ADP/Arrow1"));
                                arrOne.SetAttribute(ATT.DDNM, arrow.Member(0).Member(0));
                                double arrawAngle = drawTool.getArrawAngle(arrow);
                                //箭头y坐标
                                double gridPosY = arrow.Member(0).Member(0).GetPosition(ATT.LPOS).Y;
                                double midViewY = arrowLab.Owner.GetPosition(ATT.THPO).Y;
                                double disy = (gridPosY - midViewY) * arrowLab.Owner.GetDouble(ATT.VSCA);
                                double y = arrowLab.Owner.GetDoubleArray(ATT.XYPS)[1] + disy + 9 * Math.Sin((arrawAngle - 180) * Math.PI / 180);
                                //箭头x坐标
                                double gridPosX = arrow.Member(0).Member(0).GetPosition(ATT.LPOS).X;

                                double midViewX = arrowLab.Owner.GetPosition(ATT.THPO).X;
                                double disx = (gridPosX - midViewX) * arrowLab.Owner.GetDouble(ATT.VSCA);
                                double x = arrowLab.Owner.GetDoubleArray(ATT.XYPS)[0] + disx + 9 * Math.Cos((arrawAngle - 180) * Math.PI / 180);
                                drawTool.CommandExcute(arrOne.GetAsString(ATT.NAME) + $"at x " + x + " y " + y);
                                drawTool.SetAttribute(arrOne, ATT.LLEA, "false");
                                //设置箭头转向
                                String angle = arrow.GetAsString(ATT.ADIR);
                                drawTool.SetAttribute(arrOne, ATT.FCOLOU, "4");

                            }
                            else if (arrow.GetElementType() == DbElementTypeInstance.ELBOW && arrow.GetAsString(ATT.LDIR) == "U" && arrow.GetAsString(ATT.NAME).Contains(arrow.Owner.LastMember().GetAsString(ATT.NAME)))
                            {

                                DbElement arrOne = arrowLab.CreateLast(DbElementTypeInstance.SLABEL);
                                arrOne.SetAttribute(ATT.TMRF, DbElement.GetElement("/TT/DRA/MAS/LABELS/GEN/PIPE/END"));
                                arrOne.SetAttribute(ATT.DDNM, arrow);

                                //断面y坐标
                                double gridPosY = arrow.GetPosition(ATT.POS).Y;
                                double midViewY = arrowLab.Owner.GetPosition(ATT.THPO).Y;
                                double disy = (gridPosY - midViewY) * arrowLab.Owner.GetDouble(ATT.VSCA);
                                double y = arrowLab.Owner.GetDoubleArray(ATT.XYPS)[1] + disy;
                                //断x坐标
                                double gridPosX = arrow.GetPosition(ATT.POS).X;
                                double midViewX = arrowLab.Owner.GetPosition(ATT.THPO).X;
                                double disx = (gridPosX - midViewX) * arrowLab.Owner.GetDouble(ATT.VSCA);
                                double x = arrowLab.Owner.GetDoubleArray(ATT.XYPS)[0] + disx;
                                drawTool.CommandExcute(arrOne.GetAsString(ATT.NAME) + $"at x " + x + " y " + y);


                            }
                        }
                        foreach (DbElement arrow in eleValvs)
                        {
                            if (arrow.GetElementType() == DbElementTypeInstance.VALVE && arrow.GetAsString(ATT.PDIR, 2) != "U" && arrow.GetAsString(ATT.PDIR, 2) != "D")
                            {
                                Double arrowBore = arrow.GetElement(ATT.CATR).GetDoubleArray(ATT.PARA)[0];
                                DbElement arrOne = arrowLab.CreateLast(DbElementTypeInstance.SLABEL);
                                arrOne.SetAttribute(ATT.TMRF, DbElement.GetElement("/MDS/ABA/TAGS/Valv-H"));
                                DbElement valvCir = arrowLab.CreateLast(DbElementTypeInstance.SLABEL);
                                valvCir.SetAttribute(ATT.TMRF, DbElement.GetElement("/MDS/ABA/TAGS/CIRCLE/2"));
                               valvCir.SetAttribute(ATT.DDNM, arrow);
                                drawTool.CommandExcute(valvCir.GetAsString(ATT.NAME) + $" LSHAPE BENT " );

                                arrOne.SetAttribute(ATT.TXCOLO, 9);
                                arrOne.SetAttribute(ATT.DDNM, arrow);
                                double arrawAngle = drawTool.getValvAngle(arrow) + 90;
                                //弯头y坐标
                                double gridPosY = arrow.GetPosition(ATT.WORPOS).Y;
                                double midViewY = arrowLab.Owner.GetPosition(ATT.THPO).Y;
                                double disy = (gridPosY - midViewY) * arrowLab.Owner.GetDouble(ATT.VSCA);
                                double y = arrowLab.Owner.GetDoubleArray(ATT.XYPS)[1] + disy;
                                //弯头x坐标
                                double gridPosX = arrow.GetPosition(ATT.WORPOS).X;
                                double midViewX = arrowLab.Owner.GetPosition(ATT.THPO).X;
                                double disx = (gridPosX - midViewX) * arrowLab.Owner.GetDouble(ATT.VSCA);
                                double x = arrowLab.Owner.GetDoubleArray(ATT.XYPS)[0] + disx;
                                drawTool.CommandExcute(valvCir.GetAsString(ATT.NAME) + $"at x " + x  + " y " + y );
                                //避免碰撞
                               // Position2D p = Graphic.getObjectPostion(x, y, occupiedAreas,"SLAB");
                               // if (p != null)
                               // {
                               //     drawTool.CommandExcute(valvCir.GetAsString(ATT.NAME) + $" at x " + p.X + " y " + p.Y);
                               //     LimitsBox2D limitsBox1 = LimitsBox2D.Create(Position2D.Create(p.X - 5, p.Y - 5), Position2D.Create(p.X + 5, p.Y + 5));
                               //     occupiedAreas.Add(limitsBox1);
                               // }
                               // else if (!pointLabel.ContainsKey(y +5))
                               // {
                               //
                               //     drawTool.CommandExcute(valvCir.GetAsString(ATT.NAME) + $" at x " + (x+5) + " y " + (y+5 ));
                               //     pointLabel.Add(y+5 , 1);
                               //
                               //
                               // }
                               // else
                               // {
                               //
                               //     pointLabel[y+5 ]++;
                               //    
                               //
                               // }
                                drawTool.CommandExcute(arrOne.GetAsString(ATT.NAME) + $"at x " + x + " y " + y);
                             
                              

                                drawTool.SetAttribute(arrOne, ATT.FCOLOU, "9");
                                drawTool.SetAttribute(arrOne, ATT.LLEA, "false");

                                drawTool.SetAttribute(arrOne, ATT.ADEG, arrawAngle.ToString());
                            }
                            else if (arrow.GetElementType() == DbElementTypeInstance.VALVE && (arrow.GetAsString(ATT.PDIR, 2) == "U" || arrow.GetAsString(ATT.PDIR, 2) == "D"))
                            {
                                double arrawAngle = drawTool.getValvVAngle(arrow) + 90;
                                Double arrowBore = arrow.GetElement(ATT.CATR).GetDoubleArray(ATT.PARA)[0];
                                DbElement arrOne = arrowLab.CreateLast(DbElementTypeInstance.SLABEL);
                               
                                DbElement valvCir = arrowLab.CreateLast(DbElementTypeInstance.SLABEL);
                                valvCir.SetAttribute(ATT.TMRF, DbElement.GetElement("/MDS/ABA/TAGS/CIRCLE/2"));
                                valvCir.SetAttribute(ATT.DDNM, arrow);
                                drawTool.CommandExcute(valvCir.GetAsString(ATT.NAME) + $" LSHAPE BENT ");
                                arrOne.SetAttribute(ATT.TMRF, DbElement.GetElement("/MDS/ABA/TAGS/Valv-V"));
                                arrOne.SetAttribute(ATT.DDNM, arrow);
                                arrOne.SetAttribute(ATT.TXCOLO, 9);
                                //阀门y坐标
                                double gridPosY = arrow.GetPosition(ATT.WORPOS).Y;
                                double midViewY = arrowLab.Owner.GetPosition(ATT.THPO).Y;
                                double disy = (gridPosY - midViewY) * arrowLab.Owner.GetDouble(ATT.VSCA);
                                double y = arrowLab.Owner.GetDoubleArray(ATT.XYPS)[1] + disy;
                                //阀门x坐标
                                double gridPosX = arrow.GetPosition(ATT.WORPOS).X;
                                double midViewX = arrowLab.Owner.GetPosition(ATT.THPO).X;
                                double disx = (gridPosX - midViewX) * arrowLab.Owner.GetDouble(ATT.VSCA);
                                double x = arrowLab.Owner.GetDoubleArray(ATT.XYPS)[0] + disx;

                                drawTool.CommandExcute(arrOne.GetAsString(ATT.NAME) + $"at x " + x + " y " + y);
                                // drawTool.CommandExcute(arrOne.GetAsString(ATT.NAME) + " Xyscale " + (arrowBore / 100) + " " + (arrowBore / 100));
                                //设置阀门坐标
                                drawTool.CommandExcute(valvCir.GetAsString(ATT.NAME) + $"at x " + x  + " y " + y );
                                //避免碰撞
                               // Position2D p = Graphic.getObjectPostion(x, y, occupiedAreas,"SLAB");
                               // if (p != null)
                               // {
                               //     drawTool.CommandExcute(valvCir.GetAsString(ATT.NAME) + $" at x " + p.X + " y " + p.Y);
                               //     LimitsBox2D limitsBox1 = LimitsBox2D.Create(Position2D.Create(p.X - 5, p.Y - 5), Position2D.Create(p.X + 5, p.Y + 5));
                               //     occupiedAreas.Add(limitsBox1);
                               // }
                               // else if (!pointLabel.ContainsKey(y+5))
                               // {
                               //
                               //     drawTool.CommandExcute(valvCir.GetAsString(ATT.NAME) + $" at x " + (x + 5) + " y " + (y + 5));
                               //     pointLabel.Add(y + 5, 1);
                               //
                               //
                               // }
                               // else
                               // {
                               //
                               //     pointLabel[y + 5]++;
                               //     drawTool.CommandExcute(valvCir.GetAsString(ATT.NAME) + $" at x " + x + 5 + " y " + ((y + 5) + 5 * pointLabel[y+5]));
                               //   
                               // }
                                drawTool.SetAttribute(arrOne, ATT.FCOLOU, "9");
                                drawTool.SetAttribute(arrOne, ATT.LLEA, "false");
                                drawTool.SetAttribute(arrOne, ATT.ADEG, arrawAngle.ToString());
                            } }
                        foreach (DbElement arrow in eleFlans)
                        {
                             if (arrow.GetElementType() == DbElementTypeInstance.FLANGE && arrow.GetAsString(ATT.LDIR) != "D" && arrow.Owner.LastMember().GetElementType() == DbElementTypeInstance.FLANGE)
                            {
                                Double arrowBore = arrow.GetElement(ATT.CATR).GetDoubleArray(ATT.PARA)[0];
                                DbElement arrOne = arrowLab.CreateLast(DbElementTypeInstance.SLABEL);
                                arrOne.SetAttribute(ATT.TMRF, DbElement.GetElement("/TT/Isometric_ADP/Flan"));
                                arrOne.SetAttribute(ATT.DDNM, arrow);
                                double arrawAngle = drawTool.getArrawAngle(arrow);
                                //弯头y坐标
                                double gridPosY = arrow.GetPosition(ATT.WORPOS).Y;
                                double midViewY = arrowLab.Owner.GetPosition(ATT.THPO).Y;
                                double disy = (gridPosY - midViewY) * arrowLab.Owner.GetDouble(ATT.VSCA);
                                double y = arrowLab.Owner.GetDoubleArray(ATT.XYPS)[1] + disy;
                                //弯头x坐标
                                double gridPosX = arrow.GetPosition(ATT.WORPOS).X;
                                double midViewX = arrowLab.Owner.GetPosition(ATT.THPO).X;
                                double disx = (gridPosX - midViewX) * arrowLab.Owner.GetDouble(ATT.VSCA);
                                double x = arrowLab.Owner.GetDoubleArray(ATT.XYPS)[0] + disx;

                                drawTool.CommandExcute(arrOne.GetAsString(ATT.NAME) + $"at x " + x + " y " + y);
                              
                                //设置箭头转向


                                drawTool.SetAttribute(arrOne, ATT.ADEG, arrawAngle.ToString());
                                drawTool.SetAttribute(arrOne, ATT.FCOLOU, "9");
                                drawTool.SetAttribute(arrOne, ATT.LLEA, "false");

                            }
                           

                        }
                        //引出管线标注
                        // 管线表索引符号(模型位置)→文字层(View)；管线一览表(固定图幅坐标)→表格层(SHEET)
                        DbElement mtoLab = LayerManager.GetLayer(viewDraw, DrawLayerCat.Text);
                        DbElement noteDraw = lTable.CreateLast(DbElementTypeInstance.NOTE);
                        drawTool.CommandExcute(noteDraw.GetAsString(ATT.NAME) + $"at x 50 y 289");
                        drawTool.SetAttribute(noteDraw, ATT.NAME, noteDraw.Owner.GetAsString(ATT.NAME) + "/管线表格");
                        for (int i = 0; i < pipeCollection.Count(); i++)
                        {
                            DbElement slabDraw = mtoLab.CreateLast(DbElementTypeInstance.SLABEL);
                            drawTool.SetAttribute(slabDraw, ATT.LFCOLO, "13");
                            slabDraw.SetAttribute(ATT.TMRF, DbElement.GetElement("/MDS/ABA/TAGS/TABLE"));
                            slabDraw.SetAttribute(ATT.TSIZ, 2.5);
                            slabDraw.SetAttribute(ATT.BLNK, false);
                            slabDraw.SetAttribute(ATT.DDNM, pipeCollection[i]);
                            slabDraw.SetAttribute(ATT.ATEX, (i + 1).ToString());
                            //箭头y坐标
                            double gridPosY = pipeCollection[i].Member(0).Member(1).GetPosition(ATT.WORPOS).Y;
                            double midViewY = arrowLab.Owner.GetPosition(ATT.THPO).Y;
                            double disy = (gridPosY - midViewY) * arrowLab.Owner.GetDouble(ATT.VSCA);
                            double y = arrowLab.Owner.GetDoubleArray(ATT.XYPS)[1] + disy -120;
                            //箭头x坐标
                            double gridPosX = pipeCollection[i].Member(0).Member(1).GetPosition(ATT.WORPOS).X;

                            double midViewX = arrowLab.Owner.GetPosition(ATT.THPO).X;
                            double disx = (gridPosX - midViewX) * arrowLab.Owner.GetDouble(ATT.VSCA);
                            double x = arrowLab.Owner.GetDoubleArray(ATT.XYPS)[0] + disx ;
                            drawTool.CommandExcute(slabDraw.GetAsString(ATT.NAME) + $"at x " + x + " y " + y);
                            //noteDraw.CreateLast(DbElementTypeInstance.)
                        }
                        //管道一览表表头
                        DbElement textCell1 = noteDraw.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
                        drawTool.SetAttribute(textCell1, ATT.JUST, "Centre");
                        drawTool.SetAttribute(textCell1, ATT.ALIGN, "Hbody");
                        drawTool.SetAttribute(textCell1, ATT.TXCOLO, "10");
                        drawTool.SetAttribute(textCell1, ATT.CHEI, "2.5");
                        textCell1.SetAttribute(ATT.BTEX, "序号");
                        //drawTool.UpdateModelView(slabDraw);
                        DbElement textCell2 = noteDraw.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
                        textCell2.Copy(textCell1);
                        textCell2.SetAttribute(ATT.BTEX, "管径");
                        drawTool.CommandExcute(textCell2.GetAsString(ATT.NAME) + $"at x 60 y 289 ");
                        DbElement textCell3 = noteDraw.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
                        textCell3.Copy(textCell1);
                        textCell3.SetAttribute(ATT.BTEX, "管道名称");
                        drawTool.CommandExcute(textCell3.GetAsString(ATT.NAME) + $"at x 80 y 289 ");
                        DbElement textCell4 = noteDraw.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
                        textCell4.Copy(textCell1);
                        textCell4.SetAttribute(ATT.BTEX, "底标高");
                        drawTool.CommandExcute(textCell4.GetAsString(ATT.NAME) + $"at x 120 y 289 ");
                        // listBox1.Items.Add(ele.GetAsString(ATT.NAME));
                        //drawTool.UpdateModelView(mtoLab);
                        //框的竖线
                        DbElement db = cueCurrent.Owner.Owner.Owner.Owner;

                        Double frameLengthH = 5 * (pipeCollection.Count() + 1);//竖线长度
                        DbElement yFram1 = noteDraw.CreateLast(DbElementTypeInstance.STRAIGHT);

                        drawTool.SetAttribute(yFram1, ATT.LENG, frameLengthH.ToString());
                        yFram1.SetAttribute(ATT.NLCOLO, 10);
                        //Fpt Offset X 0mm Y 0mm Tpt Offset X 52mm Y 0mm

                        drawTool.SetAttribute(yFram1, ATT.ADEG, "-90");
                        drawTool.CommandExcute(yFram1.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y 0mm Tpt Offset X 0mm" + " Y -" + frameLengthH + " mm");
                        drawTool.CommandExcute(yFram1.GetAsString(ATT.NAME) + $" at x 48 y 294");

                        DbElement yFram2 = noteDraw.CreateLast(DbElementTypeInstance.STRAIGHT);
                        yFram2.Copy(yFram1);
                        drawTool.CommandExcute(yFram2.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y 0mm Tpt Offset X 0mm" + " Y -" + frameLengthH + " mm");
                        drawTool.CommandExcute(yFram2.GetAsString(ATT.NAME) + $" at x 60 y 294");
                        //
                        DbElement yFram3 = noteDraw.CreateLast(DbElementTypeInstance.STRAIGHT);
                        yFram3.Copy(yFram1);
                        drawTool.CommandExcute(yFram3.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y 0mm Tpt Offset X 0mm" + " Y -" + frameLengthH + " mm");
                        drawTool.CommandExcute(yFram3.GetAsString(ATT.NAME) + $" at x 70 y 294");
                        DbElement yFram4 = noteDraw.CreateLast(DbElementTypeInstance.STRAIGHT);
                        yFram4.Copy(yFram1);
                        drawTool.CommandExcute(yFram4.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y 0mm Tpt Offset X 0mm" + " Y -" + frameLengthH + " mm");
                        drawTool.CommandExcute(yFram4.GetAsString(ATT.NAME) + $" at x 120 y 294");
                        DbElement yFram5 = noteDraw.CreateLast(DbElementTypeInstance.STRAIGHT);
                        yFram5.Copy(yFram1);
                        drawTool.CommandExcute(yFram5.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y 0mm Tpt Offset X 0mm" + " Y -" + frameLengthH + " mm");

                        drawTool.CommandExcute(yFram5.GetAsString(ATT.NAME) + $" at x 140 y 294");
                        //最上面和最先面的横线
                        DbElement straFramHead = noteDraw.CreateLast(DbElementTypeInstance.STRAIGHT);
                        straFramHead.Copy(yFram1);

                        drawTool.CommandExcute(straFramHead.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y 0mm Tpt Offset X 92mm Y 0mm");
                        drawTool.SetAttribute(straFramHead, ATT.LENG, "85");
                        drawTool.CommandExcute(straFramHead.GetAsString(ATT.NAME) + $" at x 48 y 294");

                        DbElement straFram2 = noteDraw.CreateLast(DbElementTypeInstance.STRAIGHT);
                        straFram2.Copy(yFram1);

                        drawTool.CommandExcute(straFram2.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y 0mm Tpt Offset X 92mm Y 0mm");
                        drawTool.SetAttribute(straFram2, ATT.LENG, "85");
                        drawTool.CommandExcute(straFram2.GetAsString(ATT.NAME) + $" at x 48 y 289");
                        DbElement straFram3 = noteDraw.CreateLast(DbElementTypeInstance.STRAIGHT);
                        straFram3.Copy(yFram1);
                        Double offSetXFramHead = 289 - 5 * pipeCollection.Count();//框最下面横线偏移量
                        drawTool.CommandExcute(straFram3.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y 0mm Tpt Offset X 92mm Y 0mm");
                        drawTool.SetAttribute(straFram3, ATT.LENG, "85");
                        drawTool.CommandExcute(straFram3.GetAsString(ATT.NAME) + $" at x 48 y " + offSetXFramHead);
                        //绘制管道一览表
                        for (int i = 0; i < pipeCollection.Count(); i++)
                        {
                            Double offSet = 289 - 5 * (i + 1);//字体偏移量
                            Double offSetXFram = 289 - 5 * i;//框横线偏移量



                            //画框横线
                            DbElement straFram1 = noteDraw.CreateLast(DbElementTypeInstance.STRAIGHT);
                            straFram1.Copy(yFram1);
                            drawTool.CommandExcute(straFram1.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y 0mm Tpt Offset X 92mm Y 0mm");
                            drawTool.SetAttribute(straFram1, ATT.LENG, "85");
                            drawTool.CommandExcute(straFram1.GetAsString(ATT.NAME) + $" at x 48 y " + offSetXFram);
                            //序号
                            DbElement textCell11 = noteDraw.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
                            textCell11.Copy(textCell1);
                            textCell11.SetAttribute(ATT.BTEX, (i + 1).ToString());
                            drawTool.CommandExcute(textCell11.GetAsString(ATT.NAME) + $" at x 50 y " + offSet);

                            //管径
                            DbElement textCell21 = noteDraw.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
                            textCell21.Copy(textCell2);
                            textCell21.SetAttribute(ATT.BTEX, pipeCollection[i].Member(0).GetAsString(ATT.HBOR).Replace("mm", ""));
                            drawTool.CommandExcute(textCell21.GetAsString(ATT.NAME) + $" at x 60 y " + offSet);
                            //管道名称
                            DbElement textCell31 = noteDraw.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
                            textCell31.Copy(textCell3);
                            textCell31.SetAttribute(ATT.BTEX, pipeCollection[i].GetAsString(ATT.NAMN));
                            drawTool.CommandExcute(textCell31.GetAsString(ATT.NAME) + $" at x 70 y " + offSet);
                            //管道底标高
                            DbElement[] branMems = pipeCollection[i].Members();
                           
                            double tubeOd = pipeCollection[i].Member(0).Member(0).GetElement(ATT.SPRE).GetElement(ATT.CATR).GetDoubleArray(ATT.PARA)[1];
                            double itemPos = 0;
                            if (pipeCollection[i].Member(0).Member(0).GetAsString(ATT.ITPS).Contains("U"))
                            {
                               itemPos = (Convert.ToDouble(pipeCollection[i].Member(0).Member(0).GetAsString(ATT.ITPS).Substring(pipeCollection[i].Member(0).Member(0).GetAsString(ATT.ITPS).IndexOf("U") + 1).Trim().Replace("mm", "")) - tubeOd * 0.5) * 0.001;

                            }
                            else if (pipeCollection[i].Member(0).Member(0).GetAsString(ATT.ITPS).Contains("D"))
                            {
                               itemPos = - (Convert.ToDouble(pipeCollection[i].Member(0).Member(0).GetAsString(ATT.ITPS).Substring(pipeCollection[i].Member(0).Member(0).GetAsString(ATT.ITPS).IndexOf("D") + 1).Trim().Replace("mm", "")) - tubeOd * 0.5) * 0.001;

                            }
                            Double lbopPipe = Math.Round(itemPos,3);
                            
                            foreach (DbElement mem in branMems)
                            {
                                DbElement[] branItems = mem.Members();
                                foreach (DbElement item in branItems)
                                {
                                   
                                    if (item.GetElementType() == DbElementTypeInstance.TUBING)
                                    {
                                         tubeOd = item.GetElement(ATT.SPRE).GetElement(ATT.CATR).GetDoubleArray(ATT.PARA)[1];
                                        if (item.GetAsString(ATT.ITPS).Contains("U"))
                                        {
                                            itemPos = (Convert.ToDouble(item.GetAsString(ATT.ITPS).Substring(item.GetAsString(ATT.ITPS).IndexOf("U") + 1).Trim().Replace("mm", "")) - tubeOd * 0.5) * 0.001;

                                        }else if (item.GetAsString(ATT.ITPS).Contains("D"))
                                        {
                                            itemPos = -(Convert.ToDouble(item.GetAsString(ATT.ITPS).Substring(item.GetAsString(ATT.ITPS).IndexOf("D") + 1).Trim().Replace("mm", "")) - tubeOd * 0.5) * 0.001;

                                        }

                                        // break;
                                        // MessageBox.Show(itemPos.ToString());
                                        if (lbopPipe > itemPos)
                                        {
                                            lbopPipe = Math.Round(itemPos,3);
                                        }
                                    }

                                }
                            }

                           
                            DbElement textCell41 = noteDraw.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
                            textCell41.Copy(textCell4);
                            textCell41.SetAttribute(ATT.BTEX, lbopPipe.ToString());
                            drawTool.CommandExcute(textCell41.GetAsString(ATT.NAME) + $" at x 120 y " + offSet);
                            //noteDraw.CreateLast(DbElementTypeInstance.)
                        }
                        LayerManager.SetCurrentLayer(cueCurrent, DrawLayerCat.Outline); // 外形层(best-effort)：模型几何归层需 PDMS 实测
                        drawTool.UpdateModelView(cueCurrent);

                        drawTool.CommandExcute("UPDATE all");
                        if (DrawingType == "管道布置图")
                        {
                            DBElementCollection dimLayers = new DBElementCollection(viewDraw, new TypeFilter(DbElementTypeInstance.LAYER));
                            foreach (DbElement item in dimLayers)
                            {

                                DBElementCollection dims = new DBElementCollection(item, new TypeFilter(DbElementTypeInstance.LDIMENSION));
                                foreach (DbElement item2 in dims)
                                {

                                    Graphic.setGridPos(item2);
                                }
                                DBElementCollection dimLabels = new DBElementCollection(item, new TypeFilter(DbElementTypeInstance.SLABEL));
                                foreach (DbElement item2 in dimLabels)
                                {

                                    Graphic.setGridLabelPos(item2, viewMinX, viewMaxX, viewMinY, viewMaxY);
                                }

                            }
                        }
                       
                    }
                    else if (comboBox1.Text == "管口方位图")
                    {
                        drawTool.CommandExcute(viewDraw.GetAsString(ATT.NAME) + "by y 60 ");

                        //创建投影面
                        List<DbElement> eles = drawE3DTool.ElementsFromDrawlist(viewDraw, new TypeFilter(DbElementTypeInstance.NOZZLE), false, false);

                        DbElement lNozzText = LayerManager.GetLayer(viewDraw, DrawLayerCat.Text);
                        drawTool.SetAttribute(lNozzText, ATT.JUST, "Centre");
                        drawTool.SetAttribute(lNozzText, ATT.ALIGN, "BASE");
                        drawTool.SetAttribute(lNozzText, ATT.LSHA, "Angled Horizontal");
                        DbElement noteNozz = LayerManager.GetLayer(cueCurrent, DrawLayerCat.Table).CreateLast(DbElementTypeInstance.NOTE);
                       
                       
                        //管口方位一览表表头
                        DbElement textCell1 = noteNozz.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
                        drawTool.SetAttribute(textCell1, ATT.JUST, "Centre");
                        drawTool.SetAttribute(textCell1, ATT.ALIGN, "Hbody");
                        drawTool.SetAttribute(textCell1, ATT.TXCOLO, "10");
                        drawTool.SetAttribute(textCell1, ATT.CHEI, "2.5");
                        textCell1.SetAttribute(ATT.BTEX, "管口符号");
                        drawTool.CommandExcute(textCell1.GetAsString(ATT.NAME) + $"at x 10 y 52 ");
                        //drawTool.UpdateModelView(slabDraw);
                        DbElement textCell2 = noteNozz.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
                        textCell2.Copy(textCell1);
                        textCell2.SetAttribute(ATT.BTEX, "公称直径");
                        drawTool.CommandExcute(textCell2.GetAsString(ATT.NAME) + $"at x 25 y 52 ");
                        DbElement textCell3 = noteNozz.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
                        textCell3.Copy(textCell1);
                        textCell3.SetAttribute(ATT.BTEX, "连接形式及标准");
                        drawTool.CommandExcute(textCell3.GetAsString(ATT.NAME) + $"at x 45 y 52");
                        DbElement textCell4 = noteNozz.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
                        textCell4.Copy(textCell1);
                        textCell4.SetAttribute(ATT.BTEX, "用途及名称");
                        drawTool.CommandExcute(textCell4.GetAsString(ATT.NAME) + $"at x 88 y 52 ");
                       //右边的字
                        DbElement textCell21 = noteNozz.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
                        textCell21.Copy(textCell1);
                        drawTool.CommandExcute(textCell21.GetAsString(ATT.NAME) + $"at x 105 y 52 ");
                        DbElement textCell22 = noteNozz.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
                        textCell22.Copy(textCell2);
                        drawTool.CommandExcute(textCell22.GetAsString(ATT.NAME) + $"at x 120 y 52 ");
                        DbElement textCell23 = noteNozz.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
                        textCell23.Copy(textCell3);
                        drawTool.CommandExcute(textCell23.GetAsString(ATT.NAME) + $"at x 140 y 52 ");
                        DbElement textCell24 = noteNozz.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
                        textCell24.Copy(textCell4);
                        drawTool.CommandExcute(textCell24.GetAsString(ATT.NAME) + $"at x 183 y 52 ");
                       // listBox1.Items.Add(ele.GetAsString(ATT.NAME));
                       //drawTool.UpdateModelView(mtoLab);
                        //框的竖线

                        Double frameLengthH = 5 * (eles.Count() )*0.5+5;//竖线长度
                        DbElement yFram= noteNozz.CreateLast(DbElementTypeInstance.STRAIGHT);

                        drawTool.SetAttribute(yFram, ATT.LENG, frameLengthH.ToString());
                        yFram.SetAttribute(ATT.NLCOLO, 10);
                        //Fpt Offset X 0mm Y 0mm Tpt Offset X 52mm Y 0mm

                        drawTool.SetAttribute(yFram, ATT.ADEG, "-90");
                        drawTool.CommandExcute(yFram.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y 0mm Tpt Offset X 0mm" + " Y +" + frameLengthH + " mm");
                        drawTool.CommandExcute(yFram.GetAsString(ATT.NAME) + $" at x 25 y 52");
                        DbElement yFram2 = noteNozz.CreateLast(DbElementTypeInstance.STRAIGHT);
                        yFram2.Copy(yFram);
                        drawTool.CommandExcute(yFram2.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y 0mm Tpt Offset X 0mm" + " Y +" + frameLengthH + " mm");

                        drawTool.CommandExcute(yFram2.GetAsString(ATT.NAME) + " at x 38 y 52 ");

                        DbElement yFram3 = noteNozz.CreateLast(DbElementTypeInstance.STRAIGHT);
                        yFram3.Copy(yFram2);
                        drawTool.CommandExcute(yFram3.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y 0mm Tpt Offset X 0mm" + " Y +" + frameLengthH + " mm");

                        drawTool.CommandExcute(yFram3.GetAsString(ATT.NAME) + " at x 86 y 52 ");

                        //间隔线
                        DbElement yFramMid = noteNozz.CreateLast(DbElementTypeInstance.STRAIGHT);
                        yFramMid.Copy(yFram3);
                        drawTool.CommandExcute(yFramMid.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y 0mm Tpt Offset X 0mm" + " Y +" + frameLengthH + " mm");

                        drawTool.CommandExcute(yFramMid.GetAsString(ATT.NAME) + " at x 104 y 52 ");

                        DbElement yFram4 = noteNozz.CreateLast(DbElementTypeInstance.STRAIGHT);
                        yFram4.Copy(yFram3);
                        drawTool.CommandExcute(yFram4.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y 0mm Tpt Offset X 0mm" + " Y +" + frameLengthH + " mm");

                        drawTool.CommandExcute(yFram4.GetAsString(ATT.NAME) + " at x 120 y 52 ");


                        DbElement yFram5 = noteNozz.CreateLast(DbElementTypeInstance.STRAIGHT);
                        yFram5.Copy(yFram4);
                        drawTool.CommandExcute(yFram5.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y 0mm Tpt Offset X 0mm" + " Y +" + frameLengthH + " mm");

                        drawTool.CommandExcute(yFram5.GetAsString(ATT.NAME) + " at x 132 y 52 "); 
                        DbElement yFram6 = noteNozz.CreateLast(DbElementTypeInstance.STRAIGHT);
                        yFram6.Copy(yFram5);
                        drawTool.CommandExcute(yFram6.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y 0mm Tpt Offset X 0mm" + " Y +" + frameLengthH + " mm");

                        drawTool.CommandExcute(yFram6.GetAsString(ATT.NAME) + " at x 181 y 52 ");
                        Dictionary<Double, int> count = new Dictionary<double, int>();
                        //最上面的横框线
                        DbElement straFram1 = noteNozz.CreateLast(DbElementTypeInstance.STRAIGHT);
                        straFram1.Copy(yFram);
                        drawTool.CommandExcute(straFram1.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y 0mm Tpt Offset X 190mm Y 0mm");
                        drawTool.SetAttribute(straFram1, ATT.LENG, "170");
                        drawTool.CommandExcute(straFram1.GetAsString(ATT.NAME) + $" at x 10 y " + (frameLengthH+52));
                       
                     
                       
                        int i = 0;
                        foreach (DbElement one in eles)
                        {

                            //管口号
                            String nozzName = one.GetAsString(ATT.NAME).Substring(one.GetAsString(ATT.NAME).LastIndexOf("/")+1);
                            //连接形式及标准
                            String nozzCatref = one.GetElement(ATT.CATR).GetAsString(ATT.NAMN);
                            //管径
                            String nozzBore = one.GetAsString(ATT.PROP, ATT.APAR).Replace("mm", "");
                            //用途
                            String nozzFunc = one.GetAsString(ATT.FUNC);
                            Double offSet = 52 + 5 * (i+1) ;//字体偏移量
                            Double offSetXFram = 52 + 5 * (i+1);//框横线偏移量
                            Double m = 0;
                            if (offSet >= frameLengthH+52)
                            {
                                offSet = 52 + 5 * (i+2) - frameLengthH;//字体偏移量
                                 offSetXFram = 52 + 5 * (i+1)- frameLengthH;//框横线偏移量
                                m = 95;
                            }

                           
                                //画框横线
                                DbElement straFramNozz = noteNozz.CreateLast(DbElementTypeInstance.STRAIGHT);
                                straFramNozz.Copy(yFram);
                                drawTool.CommandExcute(straFramNozz.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y 0mm Tpt Offset X 190mm Y 0mm");
                                drawTool.SetAttribute(straFramNozz, ATT.LENG, "170");
                                drawTool.CommandExcute(straFramNozz.GetAsString(ATT.NAME) + $" at x " + 10 + " y " + offSetXFram);
                            
                          
                            //管嘴号
                            DbElement textCell11 = noteNozz.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
                            textCell11.Copy(textCell1);
                            textCell11.SetAttribute(ATT.BTEX, nozzName);
                            drawTool.CommandExcute(textCell11.GetAsString(ATT.NAME) + $" at x " +(m+13) + " y " + offSet);
                            //公称直径
                            DbElement textCell12 = noteNozz.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
                            textCell12.Copy(textCell1);
                            textCell12.SetAttribute(ATT.BTEX, nozzBore);
                            drawTool.CommandExcute(textCell12.GetAsString(ATT.NAME) + $" at x " + (m +28)+ " y " + offSet);
                            //连接形式及标准
                            DbElement textCell13 = noteNozz.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
                            textCell13.Copy(textCell1);
                            textCell13.SetAttribute(ATT.BTEX, nozzCatref);
                            drawTool.CommandExcute(textCell13.GetAsString(ATT.NAME) + $" at x " + (m+40) + " y " + offSet);
                            //用途
                            DbElement textCell14 = noteNozz.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
                            textCell14.Copy(textCell1);
                            textCell14.SetAttribute(ATT.BTEX, nozzFunc);
                            drawTool.CommandExcute(textCell14.GetAsString(ATT.NAME) + $" at x " + (m+90) + " y " + offSet);
                            //角度
         
                            Double Angle = drawTool.getAngle(one);


                            //中心线
                            DbElement centerLine= noteNozz.CreateLast(DbElementTypeInstance.STRAIGHT);
                            drawTool.CommandExcute(centerLine.GetAsString(ATT.NAME) + " Fpt Offset X 0mm Y 0mm Tpt Offset X 27mm Y 0mm");
                            drawTool.CommandExcute(centerLine.GetAsString(ATT.NAME) + "ADEG " +(90-Angle));
                            double x = 0;
                            double y = 0;
                            drawTool.SetAttribute(centerLine, ATT.NLCOLO, "2");
                            drawTool.SetAttribute(centerLine, ATT.NLSTYL, "8006");
                            if (one.Owner.GetElementType() == DbElementTypeInstance.EQUIPMENT)
                            {
                                //设备y坐标
                                double equiPosY = one.Owner.GetPosition(ATT.WORPOS).Y;
                                double midViewY = viewDraw.GetPosition(ATT.THPO).Y;
                                double disy = (equiPosY - midViewY) * viewDraw.GetDouble(ATT.VSCA);
                                y = viewDraw.GetDoubleArray(ATT.XYPS)[1] + disy;
                                //设备x坐标
                                double equiPosX = one.Owner.GetPosition(ATT.WORPOS).X;

                                double midViewX = viewDraw.GetPosition(ATT.THPO).X;
                                double disx = (equiPosX - midViewX) * viewDraw.GetDouble(ATT.VSCA);
                                 x = viewDraw.GetDoubleArray(ATT.XYPS)[0] + disx;
                                drawTool.CommandExcute(centerLine.GetAsString(ATT.NAME) + $" at x "+x+" y " + y);

                            }else if (one.Owner.Owner.GetElementType() == DbElementTypeInstance.EQUIPMENT)
                            {
                                //设备y坐标
                                double equiPosY = one.Owner.Owner.GetPosition(ATT.WORPOS).Y;
                                double midViewY = viewDraw.GetPosition(ATT.THPO).Y;
                                double disy= (equiPosY - midViewY) * viewDraw.GetDouble(ATT.VSCA);
                                y = viewDraw.GetDoubleArray(ATT.XYPS)[1] + disy;
                                //设备x坐标
                                double equiPosX = one.Owner.Owner.GetPosition(ATT.WORPOS).X;

                                double midViewX= viewDraw.GetPosition(ATT.THPO).X;
                                double disx = (equiPosX - midViewX) * viewDraw.GetDouble(ATT.VSCA);
                                 x = viewDraw.GetDoubleArray(ATT.XYPS)[0] + disx;
                                drawTool.CommandExcute(centerLine.GetAsString(ATT.NAME) + $" at x " + x + " y " + y);
                            }
                            //管嘴号
                            DbElement arrOne = lNozzText.CreateLast(DbElementTypeInstance.GLABEL);
                            drawTool.SetAttribute(arrOne, ATT.LLEA, "false");
                            drawTool.CommandExcute(arrOne.GetAsString(ATT.NAME) + "JUST Centre");
                            arrOne.SetAttribute(ATT.BTEX, "#NOZZ(P2:)(C2:)");
                                // MessageBox.Show(item.name);
                                arrOne.SetAttribute(ATT.DDNM, one);
                                arrOne.SetAttribute(ATT.TSIZ, 2.5);
                            drawTool.SetAttribute(arrOne, ATT.TXCOLO, "209");

                            //角度
                            DbElement angleText = noteNozz.CreateLast(DbElementTypeInstance.TEXTPRIMITIVE);
                            drawTool.CommandExcute(angleText.GetAsString(ATT.NAME) + "JUST Centre");
                            drawTool.SetAttribute(angleText, ATT.ALIGN, "BASE");
                                drawTool.SetAttribute(angleText, ATT.TXCOLO, "10");
                                drawTool.SetAttribute(angleText, ATT.CHEI, "2.5");
                           
                            angleText.SetAttribute(ATT.BTEX, Angle.ToString());
                            Direction nozzDir = one.GetDirection(ATT.PDIR, 1);
                            //判断x的坐标有没有用
                            if (nozzDir.ToString() != "U" && nozzDir.ToString() != "D")
                            {
                                 
                                if (!count.ContainsKey(Angle))
                                {
                                    count.Add(Angle, 1);
                                }
                              
                                drawTool.CommandExcute(arrOne.GetAsString(ATT.NAME) + $"at x " + (x + (27+8 * count[Angle] )* Math.Sin(Angle * Math.PI / 180)) + " y " + (y + (33+  8*count[Angle]) * Math.Cos(Angle * Math.PI / 180)));
                                count[Angle]++;

                            }
                            else if (nozzDir.ToString() == "U"  )
                            {
                                x = x + 5;
                                y = y + 5;
                                drawTool.CommandExcute(arrOne.GetAsString(ATT.NAME) + $"at x " + x + " y " + y);
                               
                            }
                            else if ( nozzDir.ToString() == "D")
                            {
                                x = x + 10;
                                y = y + 10;
                                drawTool.CommandExcute(arrOne.GetAsString(ATT.NAME) + $"at x " + x + " y " + y);

                            }
                            i++;
                        }
                        
                    }
                    LayerManager.SetCurrentLayer(cueCurrent, DrawLayerCat.Outline); // 外形层(best-effort)：模型几何归层需 PDMS 实测
                    drawTool.UpdateModelView(cueCurrent);
                    drawTool.CommandExcute("UPDATE all");
                    // ITD 风格：明确的完成提示 + 图纸名称
                    MessageBox.Show("图纸已创建：\n" + DrawingName + "\n\n比例: 1/" + comboBox5.Text,
                        "出图完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    this.listBox2.Items.Add(cueCurrent.GetAsString(ATT.NAME));

                }

            }
            catch (Exception p)
            {

                MessageBox.Show(p.ToString());
            }
        }

        private void button1_Click_1(object sender, EventArgs e)
        {
        }

        private void button3_Click(object sender, EventArgs e)
        {
            DbElement addElement = CE.Element;
            if (!listBox1.Items.Contains(addElement.GetAsString(ATT.NAME)))
            {
                this.listBox1.Items.Add(addElement.GetAsString(ATT.NAME));
                Graphic.DrawlistAdd(addElement);
            }

            // 优先从选中的模型直接读取包围盒（WVOL = World Volume）
            double[] limits = GetElementLimits(addElement);
            if (limits != null && limits.Length >= 6)
            {
                this.textBox1.Text = Math.Round(limits[0]).ToString();
                this.textBox2.Text = Math.Round(limits[2]).ToString();
                this.textBox3.Text = Math.Round(limits[4]).ToString();
                this.textBox4.Text = Math.Round(limits[1]).ToString();
                this.textBox5.Text = Math.Round(limits[3]).ToString();
                this.textBox6.Text = Math.Round(limits[5]).ToString();
                return;
            }

            // 回退到 drawlist 所有元素的包围盒
            double[] ds = Graphic.getDrawlistLimits();
            this.textBox1.Text = Math.Round(ds[0]).ToString();
            this.textBox2.Text = Math.Round(ds[2]).ToString();
            this.textBox3.Text = Math.Round(ds[4]).ToString();
            this.textBox4.Text = Math.Round(ds[1]).ToString();
            this.textBox5.Text = Math.Round(ds[3]).ToString();
            this.textBox6.Text = Math.Round(ds[5]).ToString();
        }

        /// <summary>
        /// 从单个模型元素读取三维包围盒
        /// 优先级: WVOL > POSITION + 尺寸估算
        /// returns null array on failure
        /// </summary>
        private static double[] GetElementLimits(DbElement element)
        {
            try
            {
                // 1. 尝试直接读取 WVOL（设备/管道等元素的包围盒）
                double[] wvol = element.GetDoubleArray(ATT.WVOL);
                if (wvol != null && wvol.Length >= 6)
                    return wvol;

                // 2. 对于 Branch/Pipe，尝试从子成员聚合
                if (element.GetElementType() == DbElementTypeInstance.BRANCH ||
                    element.GetElementType() == DbElementTypeInstance.PIPE)
                {
                    double minX = double.MaxValue, maxX = double.MinValue;
                    double minY = double.MaxValue, maxY = double.MinValue;
                    double minZ = double.MaxValue, maxZ = double.MinValue;
                    bool any = false;
                    foreach (DbElement member in element.Members())
                    {
                        double[] mv = member.GetDoubleArray(ATT.WVOL);
                        if (mv != null && mv.Length >= 6)
                        {
                            minX = Math.Min(minX, mv[0]); maxX = Math.Max(maxX, mv[3]);
                            minY = Math.Min(minY, mv[1]); maxY = Math.Max(maxY, mv[4]);
                            minZ = Math.Min(minZ, mv[2]); maxZ = Math.Max(maxZ, mv[5]);
                            any = true;
                        }
                        else
                        {
                            Position pos = member.GetPosition(ATT.WORPOS);
                            if (pos != null)
                            {
                                minX = Math.Min(minX, pos.X); maxX = Math.Max(maxX, pos.X);
                                minY = Math.Min(minY, pos.Y); maxY = Math.Max(maxY, pos.Y);
                                minZ = Math.Min(minZ, pos.Z); maxZ = Math.Max(maxZ, pos.Z);
                                any = true;
                            }
                        }
                    }
                    if (any)
                        return new double[] { minX, maxX, minY, maxY, minZ, maxZ };
                }

                // 3. 退回 WORPOS 单点
                Position pos0 = element.GetPosition(ATT.WORPOS);
                if (pos0 != null)
                {
                    double r = 1000; // 默认半宽 1m
                    return new double[] { pos0.X - r, pos0.X + r, pos0.Y - r, pos0.Y + r, pos0.Z - r, pos0.Z + r };
                }
            }
            catch { }
            return new double[6];
        }
       
        private void listBox1_SelectedIndexChanged(object sender, EventArgs e)
        {
            try
            {
                CE.Element = DbElement.GetElement(listBox1.SelectedItem.ToString());
            }
            catch (Exception)
            {

                
            }
           
        }

        private void button5_Click(object sender, EventArgs e)
        {
            try
            {
                listBox1.Items.Remove(listBox1.SelectedItem);
                Graphic.DrawlistRemove(DbElement.GetElement(listBox1.SelectedItem.ToString()));
            }
            catch (Exception)
            {

              
            }
               
         
           
        }

        private void button6_Click(object sender, EventArgs e)
        {
            // E3D 原版用 PmlLoadSheet.Open(name) 打开图纸；PDMS 无 PmlLoadSheet 类型，
            // "打开图纸"= 把目标 SHEET 设为当前元素（等价 CE 定位）。
            if (CE.Element.GetElementType() == DbElementTypeInstance.SHEET)
            {
                CE.Element = DbElement.GetElement(CE.Element.GetAsString(ATT.NAME));
            }
        }

        private void button7_Click(object sender, EventArgs e)
        {
            this.listBox1.Items.Clear();
            Graphic.DrawlistRemoveAll();
        }

      

        private void button4_Click(object sender, EventArgs e)
        {
           
           drawTool.CommandExcute("UPDATE all");
           
        }

        private void comboBox1_SelectedIndexChanged(object sender, EventArgs e)
        {

        }

        private void tabPage2_Click(object sender, EventArgs e)
        {

        }

        private void button13_Click(object sender, EventArgs e)
        {
            using (FolderBrowserDialog folderBrowserDialog = new FolderBrowserDialog())
            {
                // 设置默认文件夹路径（可选）
                folderBrowserDialog.RootFolder = Environment.SpecialFolder.MyComputer;
                // 设置对话框标题（可选）
                folderBrowserDialog.Description = "请选择一个文件夹";
                // 显示对话框并检查用户是否点击了“确定”按钮
                if (folderBrowserDialog.ShowDialog() == DialogResult.OK)
                {
                    // 获取选中的文件夹路径
                    string selectedFolderPath = folderBrowserDialog.SelectedPath;
                   this.textBox7.Text= selectedFolderPath;
                }
            }
        }
        private void folderBrowserDialog1_HelpRequest(object sender, EventArgs e)
        {

        }

        private void button8_Click(object sender, EventArgs e)
        {
            DbElement addElement = CE.Element;
            if (!listBox2.Items.Contains(addElement.GetAsString(ATT.NAME)) && addElement.GetElementType() == DbElementTypeInstance.SHEET)
            {
                this.listBox2.Items.Add(addElement.GetAsString(ATT.NAME));
                            
            }else if (!listBox2.Items.Contains(addElement.GetAsString(ATT.NAME)) && addElement.GetElementType() != DbElementTypeInstance.SHEET)
            {
             
                TypeFilter filt = new TypeFilter(DbElementTypeInstance.SHEET);
                DBElementCollection dbElementCollection = new DBElementCollection(filt);
                foreach (var item in dbElementCollection)
                {
                    DbElement element = (DbElement)item;
                    String c = element.GetAsString(ATT.NAME);
                    this.listBox2.Items.Add(c);

                }
            }
        }

        private void listBox2_SelectedIndexChanged(object sender, EventArgs e)
        {
            try
            {
                CE.Element = DbElement.GetElement(listBox1.SelectedItem.ToString());
            }
            catch (Exception)
            {

               
            }
           
        }

        private void button9_Click(object sender, EventArgs e)
        {
            try
            {
                listBox2.Items.Remove(listBox2.SelectedItem);
            }
            catch (Exception)
            {

               
            }
              
        
        }

        private void button10_Click(object sender, EventArgs e)
        {
            if (listBox2.Items != null)
            {
                this.listBox2.Items.Clear();
            }
        }

        private void button11_Click(object sender, EventArgs e)
        {
            if (checkBox1.Checked)
            {
                foreach (String item in this.listBox2.Items)
                {
                    String outPathName = this.textBox7.Text  + item.Replace("/","-") + ".dxf";
                    drawTool.CommandExcute(item+ " LIEXEC /Draft_DXF_LI 'DXFOUT' BLACKBG 'ON' OWN 'OFF' CUT 'ON' OutputF " + "'"+ @outPathName + "'", true);
                   // drawTool.CommandExcute("$p" + @outPathName);
               }
            }
            if (checkBox3.Checked)
            {
                foreach (String item in this.listBox2.Items)
                {
                    String outPathName = this.textBox7.Text + item.Replace("/", "-") + ".pdf";
                    if (DbElement.GetElement(item).GetDoubleArray(ATT.SIZE)[0].ToString() == "210")
                    {
                        drawTool.CommandExcute("PLOT " + item + " PDF " + "\"" + outPathName + "\"" + " 'PLOTSTYLE " + "Monochrome" + "," + " REDUCETOFIT'" + "A4 CUT OFF BORD 25 OVER", true);

                    }
                    else
                    {
                        drawTool.CommandExcute("PLOT " + item + " PDF " + "\"" + outPathName + "\"" + " 'PLOTSTYLE " + "monochrome" + "," + " REDUCETOFIT'" + "A1 CUT OFF BORD 25 OVER", true);

                    }
                    // drawTool.CommandExcute("$p" + "PLOT " + item + " PDF " + "\"" + outPathName + "\"" + " 'PLOTSTYLE " + "GREYSCALE" + "," + " REDUCETOFIT'" + "A1 CUT OFF BORD 25 OVER");

                }
            }
        }

        private void button4_Click_1(object sender, EventArgs e)
        {
            textBox8.Text = CE.Element.ToString();
        }

        private void button6_Click_1(object sender, EventArgs e)
        {
            textBox9.Text = CE.Element.ToString();
        }

        private void button12_Click(object sender, EventArgs e)
        {
            textBox10.Text = CE.Element.ToString();
        }

        private void button14_Click(object sender, EventArgs e)
        {
            double[] ds = Graphic.getDrawlistLimits();  // PDMS: 当前 DLLB 模型坐标包围盒（替代 E3D View3DManager.GetDrawlistLimits）
            this.textBox1.Text = Math.Round(ds[0]).ToString();
            this.textBox2.Text = Math.Round(ds[2]).ToString();
            this.textBox3.Text = Math.Round(ds[4]).ToString();
            this.textBox4.Text = Math.Round(ds[1]).ToString();
            this.textBox5.Text = Math.Round(ds[3]).ToString();
            this.textBox6.Text = Math.Round(ds[5]).ToString();
        }
        #if false
        // 替换为你的DeepSeek API Key
        private const string SiliconFlowApiKey = "sk-uwlafalfsmuwmpdoseclxffpeyfqkuqlqcyxowfaeahtuvcq";
        // DeepSeek Chat API地址
        private const string SiliconFlowApiUrl = "https://api.siliconflow.cn/v1/chat/completions";
        private string _fullResult = string.Empty;
        // 定义API请求/响应模型（与DeepSeek参数匹配）
        public class Message
        {
            public string role { get; set; }
            public string content { get; set; }
        }

        public class RequestModel
        {
            public string model { get; set; } = "deepseek-ai/DeepSeek-V3.1-Terminus"; // 指定使用的模型
            public List<Message> messages { get; set; } = new List<Message>();
            public bool stream { get; set; } = false;
            public float temperature { get; set; } = 0.7f;
            public int max_tokens { get; set; } = 2048; // 添加max_tokens
            public string stop { get; set; } = null; // 添加stop
        }

        public class DeepSeekStreamResponse
        {
            public string id { get; set; }
            public string model { get; set; }
            public List<Choice> choices { get; set; }
        }

        public class Choice
        {
            public Delta delta { get; set; }
            public int index { get; set; }
        }

        public class Delta
        {
            public string content { get; set; }
        }
        // public class Choice
        // {
        //     public Message message { get; set; }
        //     public String finish_reason { get; set; }
        // }

        public class ResponseModel
        {
            public List<Choice> choices { get; set; }
            public string error { get; set; } // 用于捕获API错误信息
        }
        /// <summary>
        /// 构造发送给 DeepSeek 的提示词（包含 E3D 元器件和标签数据）
        /// </summary>
        public string BuildPrompt(DbElement viewDraw)
        {
            List<double> ds = deepseekTool.getLimitDraw(viewDraw);

            double xMin = ds[0]; double yMin = ds[1]; double xMax = ds[2]; double yMax = ds[3];
            string prompt ="请优化 E3D Sheet 页LableId坐标，属性名字不能改变，避免重叠，遵循以下约束： 1. 图纸尺寸 1189mm × 841mm ，左下角为（0,0）点 2. Lablel偏移最小间距 3mm，Lable间最小间距 2mm 3. 优先布局在原来坐标上下左右，输出 JSON 格式结果，不要多余文字，符号，提示，格式错误的纠正 " + "在xMin:" + xMin + "yMin:" + yMin + "xMax:" + xMax + "yMax:" + yMax + "左右上下30mm内不要放置Lable"+ "Lable数据:" + JsonConvert.SerializeObject(getDeepSeekInput()).Replace("\r\n"," ").Replace("\n"," ")+ "如果lableType是GLAB,以坐标为圆心，预留长50，宽10的水平矩形范围，如果lableType是SLAB,以坐标为圆心，预留直径10的圆形形范围，坐标取整";
            return prompt.Trim();
        }
        /// <summary>
        /// 获取标签数组
        /// </summary>
        public List<OptimizedLabel> getDeepSeekInput()
        {
            var labels = new List<OptimizedLabel>();
            if (CE.Element.GetElementType() == DbElementTypeInstance.VIEW)
            {

                DBElementCollection symLabels = new DBElementCollection(CE.Element, new TypeFilter(DbElementTypeInstance.SLABEL));
                DBElementCollection glabLabels = new DBElementCollection(CE.Element, new TypeFilter(DbElementTypeInstance.GLABEL));
                foreach (DbElement item in symLabels)
                {
                    if (item.GetAsString(ATT.TMRF).Contains("MDS/ABA/TAGS/CIRCLE/2"))
                    {
                        labels.Add(new OptimizedLabel
                        {
                            LableID = item.GetAsString(ATT.NAME),

                            X = item.GetDoubleArray(ATT.XYPS)[0],
                            Y = item.GetDoubleArray(ATT.XYPS)[1],
                            lableType = item.GetAsString(ATT.TYPE)
                        });
                    }
                   

                }
                foreach (DbElement item in glabLabels)
                {
                    labels.Add(new OptimizedLabel
                    {
                        LableID = item.GetAsString(ATT.NAME),
                       
                        X = Math.Round(item.GetDoubleArray(ATT.XYPS)[0]),
                        Y = Math.Round(item.GetDoubleArray(ATT.XYPS)[1]),
                        lableType = item.GetAsString(ATT.TYPE)
                    });

                }
            }
                return labels;
            
        }
        /// <summary>
        /// 单正则+if-else常规写法 完成 labelid、x、y 清洗
        /// </summary>
        public static string CleanCoordinateJson(string rawJson)
        {
            // 核心正则：匹配 labelid、x、y 键值对
            string pattern = "\"(LableID|X|Y)\"\\s*:\\s*\"([^\"]*?)\"";

            rawJson = Regex.Replace(
                rawJson,
                pattern,
                new MatchEvaluator(ProcessMatch), // 委托到处理方法
                RegexOptions.IgnoreCase
            );

            return rawJson;
        }

        /// <summary>
        /// 匹配处理方法（if-else常规逻辑）
        /// </summary>
        private static string ProcessMatch(Match match)
        {
            string key = match.Groups[1].Value.ToLower();
            string value = match.Groups[2].Value;
            string result = match.Value; // 默认返回原匹配内容

            // 处理 labelid：保留字母和数字
            if (key == "LableID")
            {
                string cleanLabel = Regex.Replace(value, @"[^a-zA-Z0-9]", "");
                result = $"\"LableID\":\"{cleanLabel}\"";
            }
            // 处理 x/y：提取开头连续数字，截断后续内容
            else if (key == "X" || key == "Y")
            {
                Match numMatch = Regex.Match(value, @"^-?\d+\.?\d*");
                string numStr = numMatch.Success ? numMatch.Value : "0";
                result = $"\"{key}\":{numStr}";
            }

            return result;
        }
        /// <summary>
        /// 解析 DeepSeek 返回的文本，转为标签数组
        /// </summary>
        public List<OptimizedLabel> ParseDeepSeekOutput(string deepSeekText)
        {
            //var labels = new List<OptimizedLabel>();
            List<OptimizedLabel> labels = JsonConvert.DeserializeObject<List<OptimizedLabel>>(CleanCoordinateJson(deepSeekText));

//
//  foreach (JObject item in jArray)
//  {
//      labels.Add(new OptimizedLabel
//      {
//          LableID = item["LableID"].ToString(),
//         
//          X = Convert.ToDouble(item["X"]),
//          Y = Convert.ToDouble(item["Y"]),
//          lableType = item["lableType"].ToString()
//      });
//  }
           
            
            return labels;
        }
        /// <summary>
        /// 回写坐标到 E3D
        /// </summary>
        public static bool CommandExcute(string commandline, bool flag = false)
        {
            Command command = Command.CreateCommand(commandline);
            bool result;
            if (!(result = command.RunInPdms()) && flag)
            {
                Command command2 = Command.CreateCommand("$p " + command.Error.MessageText());
                command2.RunInPdms();
            }
            return result;
        }
        public void WriteLabelsToE3D(List<OptimizedLabel> labels)
        {
            foreach (var label in labels)
            {
                try
                {
                    CommandExcute(label.LableID + $" at x " + label.X + " y " + label.Y);

                    drawTool.UpdateModelView(DbElement.GetElement(label.LableID));

                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.ToString());
                }
            }
        }
        private async void button15_ClickAsync(object sender, EventArgs e)
        {
           
            btnSend.Enabled = false; // 防止重复点击
            textResult.AppendText(BuildPrompt(CE.Element));

            // txtResult.AppendText("正在调用硅基流动模型API...\n");
            try
            {
                // 1. 创建HttpClient（推荐全局单例，此处简化）
                using (var httpClient = new HttpClient())
                {
                    // 2. 设置请求头（携带API Key）
                    httpClient.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", SiliconFlowApiKey);
                    // 添加更多请求头
                    httpClient.DefaultRequestHeaders.Accept.Clear();
                    httpClient.DefaultRequestHeaders.Accept.Add(
                        new MediaTypeWithQualityHeaderValue("application/json"));
                    // 3. 构建请求参数（用户提问内容）
                    var requestModel = new RequestModel
                    {
                        messages = new List<Message>
                        {

                            new Message { role = "user", content =BuildPrompt(CE.Element) }
                        },
                        stream = false,
                        temperature = 0.7f,
                        max_tokens = 2048
                    };

                    // 4. 序列化请求为JSON

                    string requestJson = JsonConvert.SerializeObject(requestModel);

                    //  txtResult.AppendText($"请求JSON: {requestJson}\n");


                    var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
                    //  txtResult.AppendText("正在发送请求...\n");
                    // 5. 发送POST请求（await避免阻塞UI线程）
                    var response = await httpClient.PostAsync(SiliconFlowApiUrl, content);
                    response.EnsureSuccessStatusCode(); // 若HTTP状态码错误，抛出异常
                    using (var stream = await response.Content.ReadAsStreamAsync())
                    using (var reader = new StreamReader(stream))
                    {
                        string line;
                        while ((line = await reader.ReadLineAsync()) != null)
                        {
                            if (string.IsNullOrWhiteSpace(line) || line.StartsWith(":"))
                                continue;

                            var json = line.TrimStart("data: ".ToCharArray());
                            if (json.Equals("[DONE]"))
                            {
                                // 流结束：显示最终结果（黑色）
                                textResult.Invoke(new Action(() =>
                                {
                                    textResult.Clear(); // 清空中间灰色文本
                                    textResult.SelectionColor = Color.Black;
                                    textResult.AppendText("最终结果：" + _fullResult);
                                }));
                                break;
                            }

                            // 解析中间片段（思考过程，灰色显示）
                            var result = JsonConvert.DeserializeObject<DeepSeekStreamResponse>(json);
                            var textChunk = result.choices[0].delta.content;
                            if (!string.IsNullOrEmpty(textChunk))
                            {
                                _fullResult += textChunk; // 累加完整结果
                                                          // 显示中间思考过程（灰色）
                                textResult.Invoke(new Action(() =>
                                {
                                    textResult.SelectionColor = Color.SkyBlue;
                                    textResult.AppendText("思考中：" + textChunk + Environment.NewLine);
                                }));
                            }
                        }
                    }
                    // if (response.IsSuccessStatusCode)
                    // {
                    //     textResult.Clear();
                    //     string responseBody = await response.Content.ReadAsStringAsync();
                    //     // 提取返回结果中的 content 字段
                    //     var responseObj = JObject.Parse(responseBody);
                    //   String result =  responseObj["choices"][0]["message"]["content"].ToString();
                    //     textResult.AppendText(result.Replace("json", ""));
                    //
                    //     List<OptimizedLabel> labels =  ParseDeepSeekOutput(result.Replace("json", ""));
                    //     WriteLabelsToE3D(labels);
                    //    
                    // }
                }


            }
            catch (Exception ex)
            {
                // 捕获异常并显示（如API Key错误、网络问题）
                MessageBox.Show(ex.ToString());
            }
            finally
            {
                btnSend.Enabled = true; // 恢复按钮可用

            }
        }

        private void btnSend_Click(object sender, EventArgs e)
        {

        }

        private void button15_Click(object sender, EventArgs e)
        {
            if (CE.Element.GetElementType() == DbElementTypeInstance.VIEW)
            {
                List<double> ds = deepseekTool.getLimitDraw(CE.Element);

                double xMin = ds[0]; double yMin = ds[1]; double xMax = ds[2]; double yMax = ds[3];

                LabelOptimizer.getOutPut(JsonConvert.SerializeObject(getDeepSeekInput()).ToString(), xMin, yMin, xMax, yMax);
            }
           
        }
#endif
    }
}
