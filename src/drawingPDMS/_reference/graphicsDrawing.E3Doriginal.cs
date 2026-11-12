// Decompiled with JetBrains decompiler
// Assembly: FangYuanPDMS, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 59AF7F63-90CC-4428-89DA-25304C180540

using Aveva.Core.Database;
using Aveva.Core.Geometry;
using Aveva.Core3D.Graphics;
using pic = Aveva.Core3D.Graphics;
using ATT = Aveva.Core.Database.DbAttributeInstance;
using System;
using drawingE3d;
using Aveva.Core.Database.Filters;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace radwingE3d
{
    public class Graphic
    {
        public static DrawListMember[] DrawlistMembers()
        {
            

            return DrawListManager.Instance.CurrentDrawList.Members(); 
        }
        // public static Position getDrawlistTpostion()
        // {

        //double[] ds = pic.View3DManager.Instance.CurrentView.GetDirection();
        // double centerPos = this.txt
        //Position p = Position.Create((ds[0]+ds[1])*0.5,( ds[2] + ds[3]) * 0.5,(ds[4] + ds[5])*0.5);

        // return p;
        // }
        public static List<Double> getOffSetViewSize(List<DbElement> eles)
        {
            List<Double> getViewSize = new List<Double>();
            List<Double> doubleXs = new List<Double>();
            List<Double> doubleYs = new List<Double>();
            foreach (DbElement ele in eles)
            {
                doubleXs.Add(ele.GetPosition(ATT.POS).X);
                doubleYs.Add(ele.GetPosition(ATT.POS).Y);
            }
            Double eleMidx = (doubleXs.Max() - doubleXs.Min()) * 0.5;
            Double eleMidY = (doubleYs.Max() - doubleYs.Min()) * 0.5;
            getViewSize.Add(eleMidx);
            getViewSize.Add(eleMidY);
            return getViewSize;
        }
        public static List<DbElement> getElementSide(DbElement ldimDraw, List<DbElement> eles, String Side, Double viewMinX, Double viewMaxX, Double viewMinY, Double  viewMaxY,String DrawingType)
        {
            List<DbElement> newElementCollection = new List<DbElement>();
           
                foreach (DbElement ele in eles)
            {
               
                Double eleMidx = (viewMinX + viewMaxX) * 0.5;
                Double eleMidY = (viewMinY + viewMaxY) * 0.5;
                Double eleX = ele.GetPosition(ATT.WORPOS).X;
                Double eleY = ele.GetPosition(ATT.WORPOS).Y;
               
                if (eleX < eleMidx && eleY != eleMidY && Side.Contains("Left"))
                {
                   
                    newElementCollection.Add(ele);

                }
                else if ( eleY > eleMidY && Side.Contains("Top"))
                {
                    newElementCollection.Add(ele);

                }
                else if (eleX > eleMidx && eleY != eleMidY && Side.Contains("Right"))
                {
                    newElementCollection.Add(ele);

                }
                else if (eleX != eleMidx && eleY < eleMidY && Side.Contains("Bottom"))
                {
                    newElementCollection.Add(ele);

                }
                if (ele.GetElementType() == DbElementTypeInstance.GRIDPL && !newElementCollection.Contains(ele))
                {
                    newElementCollection.Add(ele);
                }

              

            }
            Double x = 0;
            Double y = 0;

           if (Side.Contains("Left") )
           {
          
               x = ldimDraw.Owner.Owner.GetDoubleArray(ATT.SHXYPS)[0] - (viewMaxX- viewMinX)*0.5 * ldimDraw.Owner.Owner.GetDouble(ATT.VSCA) - 10;
          
               y= ldimDraw.Owner.Owner.GetDoubleArray(ATT.SHXYPS)[1] - (viewMaxY - viewMinY) * 0.5 * ldimDraw.Owner.Owner.GetDouble(ATT.VSCA) ;
           }
           else if (Side.Contains("Top"))
           {
               x = ldimDraw.Owner.Owner.GetDoubleArray(ATT.SHXYPS)[0] - (viewMaxX - viewMinX) * 0.5 * ldimDraw.Owner.Owner.GetDouble(ATT.VSCA) ;
          
               y = ldimDraw.Owner.Owner.GetDoubleArray(ATT.SHXYPS)[1] + (viewMaxY - viewMinY) * 0.5 * ldimDraw.Owner.Owner.GetDouble(ATT.VSCA) + 10;
          
           }
           else if (Side.Contains("Right"))
           {
          
               x = ldimDraw.Owner.Owner.GetDoubleArray(ATT.SHXYPS)[0] + (viewMaxX - viewMinX) * 0.5* ldimDraw.Owner.Owner.GetDouble(ATT.VSCA) + 10;
          
               y = ldimDraw.Owner.Owner.GetDoubleArray(ATT.SHXYPS)[1] + (viewMaxY - viewMinY) * 0.5 * ldimDraw.Owner.Owner.GetDouble(ATT.VSCA) ;
           }
           else if (Side.Contains("Bottom"))
           {
          
               x = ldimDraw.Owner.Owner.GetDoubleArray(ATT.SHXYPS)[0] + (viewMaxX - viewMinX) * 0.5 * ldimDraw.Owner.Owner.GetDouble(ATT.VSCA );
          
               y = ldimDraw.Owner.Owner.GetDoubleArray(ATT.SHXYPS)[1] - (viewMaxY - viewMinY) * 0.5 * ldimDraw.Owner.Owner.GetDouble(ATT.VSCA) - 10;
           }
            if (DrawingType == "管道布置图")
            {
                drawTool.CommandExcute(ldimDraw.GetAsString(ATT.NAME) + " dpos x " + x + " y " + y, true);

            }
            return newElementCollection;
        }

        public static List<DbElement> getGridSide(List<DbElement> eles, String Side)
        {
            List<DbElement> newElementCollection = new List<DbElement>();
            //double[] ds = pic.View3DManager.Instance.CurrentView.GetDirection();

            List<Double> doubleXs = new List<Double>();
            List<Double> doubleYs = new List<Double>();
            foreach (DbElement ele in eles)
            {
                doubleXs.Add(ele.GetPosition(ATT.POS).X);
                doubleYs.Add(ele.GetPosition(ATT.POS).Y);
            }
           
           
            foreach (DbElement ele in eles)
            {
                Double eleX = ele.GetPosition(ATT.POS).X;
                Double eleY = ele.GetPosition(ATT.POS).Y;
                if (eleX < doubleXs.Max()  && eleX > doubleXs.Min() && Side.Contains("Left"))
                {

                    newElementCollection.Add(ele);

                }
                else if (eleY > doubleYs.Max() && eleX > doubleYs.Min() && Side.Contains("Bottom"))
                {
                    newElementCollection.Add(ele);

                }
              
            }
            return newElementCollection;
        }
        public static String getLdimDir( String Side)
        {
            String dir= "";
            if ( Side == "Left")
            {
                dir = "N";

            }
            else if ( Side == "Top")
            {
                dir = "E";

            }
            else if ( Side == "Right")
            {
                dir = "S";

            }
            else if ( Side == "Bottom")
            {
                dir = "W";

            }
            return dir;
        }
        private static bool IsPointOccupied(Position2D point, List<LimitsBox2D> occupiedAreas, String type)
        {

            foreach (var area in occupiedAreas)
            {
                if (point.X >= area.Minimum.X && point.X <= area.Maximum.X && point.Y >= area.Minimum.Y && point.Y <= area.Maximum.Y)

                {
                    return true;
                }
                else if ((Math.Abs(point.X - area.Minimum.X) < 50 || (Math.Abs(point.Y - area.Minimum.Y) < 15) || Math.Abs(point.X - area.Maximum.X) < 50 || (Math.Abs(point.Y - area.Maximum.Y) < 15)))
                {
                    return true;

                }

               
              
            }
            return false;
        }
        public static List<double> getLimitDraw(DbElement viewDraw, double viewMinX, double viewMaxX, double viewMinY, double viewMaxY)
        {
            List<double> ds = new List<double> { };
            double xMin = viewDraw.GetDoubleArray(ATT.SHXYPS)[0] - (viewMaxX - viewMinX) * viewDraw.GetDouble(ATT.VSCA) * 0.5;
            double yMin = viewDraw.GetDoubleArray(ATT.SHXYPS)[1] - (viewMaxY - viewMinY) * viewDraw.GetDouble(ATT.VSCA) * 0.5;
            double xMax = viewDraw.GetDoubleArray(ATT.SHXYPS)[0] + (viewMaxX - viewMinX) * viewDraw.GetDouble(ATT.VSCA) * 0.5;
            double yMax = viewDraw.GetDoubleArray(ATT.SHXYPS)[1] + (viewMaxY - viewMinY) * viewDraw.GetDouble(ATT.VSCA) * 0.5;
            ds.Add(xMin); ds.Add(yMin); ds.Add(xMax); ds.Add(yMax);
            return ds;
        }
        public static Position2D getObjectPostion(Double x ,Double y, List<LimitsBox2D> occupiedAreas,String type)
        {
            LimitsBox2D limitsBox2D = LimitsBox2D.Create();
            double gridSize = 50.0;
           
            if (type == "GLAB") {
                Position2D boxMinPstion = Position2D.Create(x - 75, y - 30);
                Position2D boxMaxPstion = Position2D.Create(x + 75, y + 30);
              
                 limitsBox2D = LimitsBox2D.Create(boxMinPstion, boxMaxPstion);
            }
            else if (type == "SLAB")
            {
                Position2D boxMinPstion = Position2D.Create(x - 50, y - 30);
                Position2D boxMaxPstion = Position2D.Create(x + 50, y + 30);
                gridSize = 10.0;
               limitsBox2D = LimitsBox2D.Create(boxMinPstion, boxMaxPstion);
            }

            // 计算网格数量
            int gridX = (int)Math.Ceiling((limitsBox2D.Maximum.X - limitsBox2D.Minimum.X) / gridSize);
            int gridY = (int)Math.Ceiling((limitsBox2D.Maximum.Y - limitsBox2D.Minimum.Y) / gridSize);

            // 搜索网格
            for (int i = 0; i < gridX; i++)
            {
                for (int j = 0; j < gridX; j++)
                {
                    double m = limitsBox2D.Minimum.X + i * gridSize + gridSize;
                    double n = limitsBox2D.Minimum.Y + j * gridSize + gridSize / 2;
                    Position2D testPoint = Position2D.Create(m, n);
                    LimitsBox2D limitsBoxTest = LimitsBox2D.Create(Position2D.Create(m - 25, n - 8), Position2D.Create(m + 25, n + 8));
                    if (!IsPointOccupied(testPoint, occupiedAreas, type))
                    {
                        return testPoint;

                    }

                }
            }
            return null;
        }
            public static void createLdimElement(DbElement layerDraw, List<DbElement> eles, String Side, Double viewMinX, Double viewMaxX, Double viewMinY, Double viewMaxY,String DrawingType)
        {
            DbElement ldimDraw = layerDraw.CreateLast(DbElementTypeInstance.LDIMENSION);
            ldimDraw.SetAttribute(ATT.DMSTRF, DbElement.GetElement("/AVEVA-DIM-1.8"));
            drawTool.SetAttribute(ldimDraw, ATT.DTERID, "-5");
            // ldimDraw.SetAttribute(ATT.PLTX, "#PIPE(C2:)");
            ldimDraw.SetAttribute(ATT.TSIZTX, "1.5");
            drawTool.SetAttribute(ldimDraw, ATT.PLSTYL, "8008");
            ldimDraw.SetAttribute(ATT.DIR, Direction.Create(Graphic.getLdimDir(Side)));
            drawTool.SetAttribute(ldimDraw, ATT.NAME, ldimDraw.Owner.GetAsString(ATT.NAME) + "/" + Side);
            // ldimDraw.SetAttribute(ATT.DPOS,Graphic.getLdimTpostion(viewDraw));
            //String content = "";
            //drawE3DTool.Dimension(ldimDraw, drawE3DTool.DimensionDataTable(), "Down", 50);
            List<DbElement> eles2 = Graphic.getElementSide(ldimDraw,eles, Side,viewMinX,viewMaxX, viewMinY,viewMaxY,DrawingType);
            //添加管线标注
            List<DbElement> pipeCollection = Graphic.getPipeCollect(eles);
            
            eles2.AddRange(pipeCollection);
            foreach (DbElement ele in eles2)
            {

                if (ele.GetElementType() != DbElementTypeInstance.SITE && ele.GetElementType() != DbElementTypeInstance.ZONE && ele.GetElementType() != DbElementTypeInstance.REFGRD && ele.Owner.GetElementType() != DbElementTypeInstance.GRIDELEVATION)
                {
                    DbElement dpptDraw = ldimDraw.CreateLast(DbElementTypeInstance.DPPT);

                    dpptDraw.SetAttribute(ATT.DDNM, ele);
                }
                   
                
            }
         
            
         
            drawTool.CommandExcute(ldimDraw.GetAsString(ATT.NAME) + " sort dim " , true);



         

        }
        public static void createGrid(DbElement layerDraw, List<DbElement> eles, String Side)
        {
            DbElement gridDraw = layerDraw.CreateLast(DbElementTypeInstance.SLABEL);
            gridDraw.SetAttribute(ATT.TMRF, DbElement.GetElement("/TT/DRA/PRJ/LABEL/REFGRD/ID"));
            gridDraw.SetAttribute(ATT.LBSTRF, DbElement.GetElement("/AVEVA-REFGRD-BALLOON"));
            DbElement gridDrawDim = layerDraw.CreateLast(DbElementTypeInstance.LDIMENSION);
            gridDrawDim.SetAttribute(ATT.DMSTRF, DbElement.GetElement("/AVEVA-BLACK"));
            gridDrawDim.SetAttribute(ATT.TSIZTX, "2.5");
            //gridDraw.SetAttribute(ATT.NPPT, 1);
            List<DbElement> eles2 = Graphic.getGridSide(eles, Side);
            // MessageBox.Show(eles2.Count().ToString());
            String content = "";
            foreach (DbElement ele in eles2)
           {
                // DbElement dpptDraw = gridDrawDim.CreateLast(DbElementTypeInstance.DPPT);
                // dpptDraw.SetAttribute(ATT.DDNM, ele);
                //gridDraw.SetAttribute(ATT.DDNM, ele);
                content += ele.GetAsString(ATT.IDPLAN) + "  "  + "\n";

            }
            File.WriteAllText(@"D:\88.txt", content);

        }

        public static void setGridPos(DbElement ldimElement)
       {
            if(ldimElement.GetAsString(ATT.AGSIDE) == "Right")
            {
               
                Double x = DbElement.GetElement("/"+ldimElement.Owner.Owner.GetAsString(ATT.NAME) + "/管道标注/Right").GetPosition(ATT.DPOS).X + 10;

                Double y = ldimElement.GetDoubleArray(ATT.DPOS)[1] ;

                drawTool.CommandExcute(ldimElement.GetAsString(ATT.NAME) + " dpos x " + x + " y " + y, true);
                ldimElement.SetAttribute(ATT.DMSTRF, DbElement.GetElement("/AVEVA-DIM-1.8"));

            }

            if (ldimElement.GetAsString(ATT.AGSIDE) == "Left")
            {


                Double x = DbElement.GetElement("/" + ldimElement.Owner.Owner.GetAsString(ATT.NAME) + "/管道标注/Right").GetPosition(ATT.DPOS).X - 10;

                Double y = ldimElement.GetDoubleArray(ATT.DPOS)[1];

                drawTool.CommandExcute(ldimElement.GetAsString(ATT.NAME) + " dpos x " + x + " y " + y, true);
                ldimElement.SetAttribute(ATT.DMSTRF, DbElement.GetElement("/AVEVA-DIM-1.8"));

            }
            if (ldimElement.GetAsString(ATT.AGSIDE) == "Top")
            {


                Double x = ldimElement.GetDoubleArray(ATT.DPOS)[0];

                Double y = DbElement.GetElement("/" + ldimElement.Owner.Owner.GetAsString(ATT.NAME) + "/管道标注/Bottom").GetPosition(ATT.DPOS).Y + 10;


                drawTool.CommandExcute(ldimElement.GetAsString(ATT.NAME) + " dpos x " + x + " y " + y, true);
                ldimElement.SetAttribute(ATT.DMSTRF, DbElement.GetElement("/AVEVA-DIM-1.8"));

            }
            if (ldimElement.GetAsString(ATT.AGSIDE) == "Bottom")
            {

                Double x = ldimElement.GetDoubleArray(ATT.DPOS)[0] ;

                Double y = DbElement.GetElement("/" + ldimElement.Owner.Owner.GetAsString(ATT.NAME) + "/管道标注/Bottom").GetPosition(ATT.DPOS).Y -10;


                drawTool.CommandExcute(ldimElement.GetAsString(ATT.NAME) + " dpos x " + x + " y " + y, true);
                ldimElement.SetAttribute(ATT.DMSTRF, DbElement.GetElement("/AVEVA-DIM-1.8"));

            }
        }
        public static double[] get2DPos(DbElement element, Double viewMinX, Double viewMaxX, Double viewMinY, Double viewMaxY)
        {
            double gridPosY = element.GetElement(ATT.DDNM).GetPosition(ATT.WORPOS).Y;
            double midViewy = (viewMaxY + viewMinY) * 0.5;
            double disy = (gridPosY - midViewy) * element.Owner.Owner.GetDouble(ATT.VSCA);
            double y = element.Owner.Owner.GetDoubleArray(ATT.SHXYPS)[1] + disy;

            double gridPosX = element.GetElement(ATT.DDNM).GetPosition(ATT.WORPOS).X;
            double midViewX = (viewMaxX + viewMinX) * 0.5;
            double disx = (gridPosX - midViewX) * element.Owner.Owner.GetDouble(ATT.VSCA);
            double x = element.Owner.Owner.GetDoubleArray(ATT.SHXYPS)[0] + disx;
            double[] ds = new double[] {x,y };

            return ds;
        }
            public static void setGridLabelPos(DbElement ldimLabElement, Double viewMinX, Double viewMaxX, Double viewMinY, Double viewMaxY)
        {
            if (ldimLabElement.GetAsString(ATT.AGSIDE) == "Right")
            {

                double y = Graphic.get2DPos(ldimLabElement, viewMinX, viewMaxX, viewMinY, viewMaxY)[1];

                //MessageBox.Show(ldimLabElement.GetElement(ATT.DDNM).GetPosition(ATT.WORPOS).X.ToString());

                double x = DbElement.GetElement("/" + ldimLabElement.Owner.Owner.GetAsString(ATT.NAME) + "/管道标注/Right").GetPosition(ATT.DPOS).X + 25;
                

                drawTool.CommandExcute(ldimLabElement.GetAsString(ATT.NAME) + $"at x " + x +" y "+ y);
            }

            if (ldimLabElement.GetAsString(ATT.AGSIDE) == "Left")
            {


                double y = Graphic.get2DPos(ldimLabElement, viewMinX, viewMaxX, viewMinY, viewMaxY)[1];
                
                Double x = DbElement.GetElement("/" + ldimLabElement.Owner.Owner.GetAsString(ATT.NAME) + "/管道标注/Right").GetPosition(ATT.DPOS).X - 25;


                drawTool.CommandExcute(ldimLabElement.GetAsString(ATT.NAME) + $"at x " + x + " y " + y);
            }
            if (ldimLabElement.GetAsString(ATT.AGSIDE) == "Top")
            {

                double x = Graphic.get2DPos(ldimLabElement, viewMinX, viewMaxX, viewMinY, viewMaxY)[0];



                Double y = DbElement.GetElement("/" + ldimLabElement.Owner.Owner.GetAsString(ATT.NAME) + "/管道标注/Bottom").GetPosition(ATT.DPOS).Y + 25;


                drawTool.CommandExcute(ldimLabElement.GetAsString(ATT.NAME) + $"at x " + x + " y " + y);
            }
            if (ldimLabElement.GetAsString(ATT.AGSIDE) == "Bottom")
            {
                double x = Graphic.get2DPos(ldimLabElement, viewMinX, viewMaxX, viewMinY, viewMaxY)[0];


                Double y = DbElement.GetElement("/" + ldimLabElement.Owner.Owner.GetAsString(ATT.NAME) + "/管道标注/Bottom").GetPosition(ATT.DPOS).Y- 25;


                drawTool.CommandExcute(ldimLabElement.GetAsString(ATT.NAME) + $"at x " + x + " y " + y);
            }
        }
        public static Position getLdimTpostion(DbElement viewDraw)
        {

            //double[] ds = pic.View3DManager.Instance.CurrentView.GetDirection();
            double[] ds = pic.View3DManager.Instance.CurrentView.GetDrawlistLimits();
            Position p = Position.Create((ds[0] + ds[1]) * 0.5, (ds[2] + ds[3]) * 0.5, (ds[4] + ds[5]) * 0.5);
            Position2D w = drawE3DTool.SheetPosition(p, viewDraw);
           
            return p;
        }
        public static List<DbElement> getPipeCollect(List<DbElement> eles)
        {
            //收集pipe管线
            List<DbElement> pipeCollection = new List<DbElement>();

            foreach (DbElement ele in eles)
            {
                if (!pipeCollection.Contains(ele.Owner.Owner))
                {
                    pipeCollection.Add(ele.Owner.Owner);
                }
            }
            return pipeCollection;
        }
        public static Position getLDimTpostion()
        {
         
            //double[] ds = pic.View3DManager.Instance.CurrentView.GetDirection();
            double[] ds = pic.View3DManager.Instance.CurrentView.GetDrawlistLimits();
            Position p = Position.Create(ds[0],ds[2],ds[4]);
            return p;
        }
        public static Double getDrawlistLen()
        {
            //double[] ds = pic.View3DManager.Instance.CurrentView.GetDirection();
            double[] ds = pic.View3DManager.Instance.CurrentView.GetDrawlistLimits();
            Double p = Math.Abs(ds[1] - ds[0]); 
            return p;
        }
        public static double getViewLen(DbElement sheetName)
        {
            String sheetSize = sheetName.GetAsString(ATT.BSRF);
            if (sheetSize.Contains("A0"))
            {
                return 1000;
            }else if (sheetSize.Contains("A4"))
            {
                return 270;
            }
            return 0.5;
        }
        public static void DrawlistAdd(DbElement element)
        {
            DrawList currentDrawList = DrawListManager.Instance.CurrentDrawList;
            if (currentDrawList.Exists(element))
                return;
            currentDrawList.Add(element);
        }

        public static void DrawlistAdd(DbElement[] elements)
        {
            DrawListManager.Instance.CurrentDrawList.Add(elements);
        }

        public static void DrawlistAddReference(DbElement element)
        {
            DrawListManager.Instance.CurrentDrawList.AddReferences(element);
        }

        public static void DrawlistAddWithinVolume(DbElement element)
        {
            DrawListManager.Instance.CurrentDrawList.AddWithinVolume(element);
        }

        public static bool DrawlistExists(DbElement element)
        {
            return DrawListManager.Instance.CurrentDrawList.Exists(element);
        }

        public static DrawListMember DrawlistGetDrawListMember(DbElement element)
        {
            return DrawListManager.Instance.CurrentDrawList.GetDrawListMember(element);
        }

        public static void DrawlistGetVisualProperties(
          ref bool autoColour,
          ref double autoColorRed,
          ref double autoColorGreen,
          ref double autoColorBlue,
          ref int transparency,
          ref bool edges)
        {
            DrawListManager.Instance.CurrentDrawList.GetVisualProperties(ref autoColour, ref autoColorRed, ref autoColorGreen, ref autoColorBlue, ref transparency, ref edges);
        }

        public static DbElement[] DrawlistGetElementsWithinVolume(DbElement element)
        {
            return DrawListManager.Instance.CurrentDrawList.GetWithinVolume(element);
        }

        public static void DrawlistHighlight(DbElement element)
        {
            DrawListManager.Instance.CurrentDrawList.Highlight(element);
        }

        public static void DrawlistHighlight(DbElement[] elements)
        {
            DrawListManager.Instance.CurrentDrawList.Highlight(elements);
        }

        public static void DrawlistRemove(DbElement element)
        {
            DrawList currentDrawList = DrawListManager.Instance.CurrentDrawList;
            if (!currentDrawList.Exists(element))
                return;
            currentDrawList.Remove(element);
        }

        public static void DrawlistRemove(DbElement[] elements)
        {
            DrawListManager.Instance.CurrentDrawList.Remove(elements);
        }

        public static void DrawlistRemove(DrawListMember member)
        {
            DrawListManager.Instance.CurrentDrawList.Remove(member);
        }

        public static void DrawlistRemove(DrawListMember[] members)
        {
            DrawListManager.Instance.CurrentDrawList.Remove(members);
        }

        public static void DrawlistRemoveAll() => DrawListManager.Instance.CurrentDrawList.RemoveAll();

        public static void DrawlistSetColour(DrawListMember[] members, int colour)
        {
            DrawListManager.Instance.CurrentDrawList.SetColour(members, colour);
        }

        public static void DrawlistSetEdges(DrawListMember[] members, bool edges)
        {
            DrawListManager.Instance.CurrentDrawList.SetEdges(members, edges);
        }

        public static void DrawlistSetTransparency(DrawListMember[] members, int transparency)
        {
            DrawListManager.Instance.CurrentDrawList.SetTransparency(members, transparency);
        }

        public static void DrawlistSetVisibility(DrawListMember[] members, bool visibility)
        {
            DrawListManager.Instance.CurrentDrawList.SetVisibility(members, visibility);
        }

        public static void DrawlistSetVisualProperties(
          DrawListMember[] members,
          bool visibility,
          int colour,
          int transparency,
          bool edges)
        {
            DrawListManager.Instance.CurrentDrawList.SetVisualProperties(members, visibility, colour, transparency, edges);
        }

        public static void DrawlistSetVisualProperties(
          bool autoColour,
          int colour,
          int transparency,
          bool edges)
        {
            DrawListManager.Instance.CurrentDrawList.SetVisualProperties(autoColour, colour, transparency, edges);
        }

        public static void DrawlistUnhighlight(DbElement element)
        {
            DrawListManager.Instance.CurrentDrawList.Unhighlight(element);
        }

        public static void DrawlistUnhighlight(DbElement[] elements)
        {
            DrawListManager.Instance.CurrentDrawList.Unhighlight(elements);
        }

        public static void DrawlistVisibleAll() => DrawListManager.Instance.CurrentDrawList.VisibleAll();
    }
}