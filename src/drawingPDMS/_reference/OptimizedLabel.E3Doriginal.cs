using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO;
using System.Windows.Forms;
using Aveva.Pdms.Database;
using Aveva.Pdms.Utilities.CommandLine;
using System.Diagnostics;

namespace drawingPDMS
{

    public struct OptimizedLabel
    {
        public string LableID { get; set; }

        public double X { get; set; }
        public double Y { get; set; }
        public string lableType { get; set; }
    }


    public class LabelOptimizer
    {
        // 定义标签类
        public class LabelData
        {
            public string LableID { get; set; }
            public double X { get; set; }
            public double Y { get; set; }
            public string lableType { get; set; }

            // 取整后的坐标
            [JsonIgnore]
            public int RoundedX { get; set; }

            [JsonIgnore]
            public int RoundedY { get; set; }
            // 原始取整坐标（用于计算移动距离）
            [JsonIgnore]
            public int OriginalRoundedX { get; set; }

            [JsonIgnore]
            public int OriginalRoundedY { get; set; }
            // 标签的实际范围（用于碰撞检测）
          
            [JsonIgnore]
            public LabelShape Shape { get; set; }
            // 移动距离
            [JsonIgnore]
            public double MoveDistance { get; set; }
            // 预留范围
            [JsonIgnore]
            public Rectangle ReservedArea { get; set; }
        }
        // 标签形状基类
        public abstract class LabelShape
        {
            public int X { get; set; }
            public int Y { get; set; }
            public abstract bool Intersects(LabelShape other, double minSpacing);
            public abstract bool IntersectsRectangle(Rectangle rect, double minSpacing);
            public abstract Rectangle GetBoundingBox();
        }

        // 圆形标签（SLAB）
        public class CircleShape : LabelShape
        {
            public double Radius { get; set; }

            public override bool Intersects(LabelShape other, double minSpacing)
            {
                double totalDistance = Radius + minSpacing;

                if (other is CircleShape circle)
                {
                    double dx = X - circle.X;
                    double dy = Y - circle.Y;
                    double distance = Math.Sqrt(dx * dx + dy * dy);
                    return distance < (Radius + circle.Radius + minSpacing);
                }
                else if (other is RectangleShape rect)
                {
                    // 计算圆心到矩形的最短距离
                    return IntersectsRectangle(rect.GetBoundingBox(), minSpacing);
                }

                return false;
            }

            public override bool IntersectsRectangle(Rectangle rect, double minSpacing)
            {
                // 计算圆心到矩形的最短距离
                double closestX = Math.Max(rect.XMin, Math.Min(X, rect.XMax));
                double closestY = Math.Max(rect.YMin, Math.Min(Y, rect.YMax));

                double dx = X - closestX;
                double dy = Y - closestY;
                double distance = Math.Sqrt(dx * dx + dy * dy);

                return distance < (Radius + minSpacing);
            }

            public override Rectangle GetBoundingBox()
            {
                double r = Radius + 1; // 加上1mm的安全余量
                return new Rectangle
                {
                    XMin = X - (int)Math.Ceiling(r),
                    YMin = Y - (int)Math.Ceiling(r),
                    XMax = X + (int)Math.Ceiling(r),
                    YMax = Y + (int)Math.Ceiling(r)
                };
            }
        }

        // 矩形标签（GLAB）- 水平矩形
        public class RectangleShape : LabelShape
        {
            public double Width { get; set; }   // 长50mm
            public double Height { get; set; }  // 宽10mm

            // 获取矩形的四个边界（考虑间距）
            private Rectangle GetExpandedRectangle(double spacing = 2)
            {
                double halfWidth = Width / 2 + spacing;
                double halfHeight = Height / 2 + spacing;

                return new Rectangle
                {
                    XMin = X - halfWidth,
                    YMin = Y - halfHeight,
                    XMax = X + halfWidth,
                    YMax = Y + halfHeight
                };
            }

            public override bool Intersects(LabelShape other, double minSpacing)
            {
                Rectangle myRect = GetExpandedRectangle(minSpacing / 2);

                if (other is CircleShape circle)
                {
                    // 圆形与矩形碰撞检测
                    return circle.IntersectsRectangle(myRect, minSpacing / 2);
                }
                else if (other is RectangleShape rect)
                {
                    // 矩形与矩形碰撞检测 - 精确的轴对齐矩形检测
                    Rectangle otherRect = rect.GetExpandedRectangle(minSpacing / 2);

                    // 检查两个矩形是否分离
                    if (myRect.XMax < otherRect.XMin || myRect.XMin > otherRect.XMax ||
                        myRect.YMax < otherRect.YMin || myRect.YMin > otherRect.YMax)
                    {
                        return false; // 分离，不相交
                    }

                    return true; // 相交
                }

                return false;
            }

            public override bool IntersectsRectangle(Rectangle rect, double minSpacing)
            {
                // 扩展当前矩形的边界
                double expandedLeft = X - Width * 0.5 - minSpacing;
                double expandedRight = X + Width * 0.5 + minSpacing;
                double expandedBottom = Y - Height * 0.5 - minSpacing;
                double expandedTop = Y + Height * 0.5 + minSpacing;

                // 分离轴测试：检查是否在任一轴上分离
                bool separatedInX = expandedRight < rect.XMin || expandedLeft > rect.XMax;
                bool separatedInY = expandedTop < rect.YMin || expandedBottom > rect.YMax;

                // 如果在任一轴上分离，则不相交
                return !(separatedInX || separatedInY);
            }

            public override Rectangle GetBoundingBox()
            {
                return GetExpandedRectangle();
            }
        }
        // 定义矩形区域
        // 定义矩形区域
        public class Rectangle
        {
            public double XMin { get; set; }
            public double YMin { get; set; }
            public double XMax { get; set; }
            public double YMax { get; set; }

            // 判断两个矩形是否相交（轴对齐）
            public bool Intersects(Rectangle other)
            {
                return !(XMax < other.XMin || XMin > other.XMax ||
                         YMax < other.YMin || YMin > other.YMax);
            }

            // 判断点是否在矩形内
            public bool Contains(int x, int y)
            {
                return x >= XMin && x <= XMax && y >= YMin && y <= YMax;
            }

            // 获取矩形宽度
            public double Width => XMax - XMin;

            // 获取矩形高度
            public double Height => YMax - YMin;

            // 扩展矩形
            public Rectangle Expand(double margin)
            {
                return new Rectangle
                {
                    XMin = XMin - margin,
                    YMin = YMin - margin,
                    XMax = XMax + margin,
                    YMax = YMax + margin
                };
            }

            // 判断矩形是否完全在另一个矩形内
            public bool IsInside(Rectangle other)
            {
                return XMin >= other.XMin && XMax <= other.XMax &&
                       YMin >= other.YMin && YMax <= other.YMax;
            }
        }
        // 主优化方法
        public static string OptimizeLabels(string inputJson,double  x1, double y1, double x2, double y2)
        {
            try
            {
                // 1. 解析输入JSON
                var labels = JsonConvert.DeserializeObject<List<LabelData>>(inputJson);
                if (labels == null || labels.Count == 0)
                    return inputJson;

                // 2. 参数设置
               
                double minSpacing =2.0; // 标签间最小间距2mm
                double maxMoveDistance = 25.0; // 最大移动距离限制
                // 定义原矩形区域
                Rectangle originalArea = new Rectangle
                {
                    XMin = x1,
                    YMin = y1,
                    XMax = x2,
                    YMax =y2
                };
                // 4. 创建四个条形禁止区域（边界线向两边各扩30mm）
                // 注意：这四个条形区域不能放在原矩形内部，是边界线本身向两边扩展
                List<Rectangle> forbiddenBands = CreateForbiddenBands(originalArea, 15.0);

                // 5. 预处理：创建标签形状，记录原始坐标
                foreach (var label in labels)
                {
                    label.RoundedX = (int)Math.Round(label.X);
                    label.RoundedY = (int)Math.Round(label.Y);
                    label.OriginalRoundedX = (int)Math.Round(label.X);
                    label.OriginalRoundedY = (int)Math.Round(label.Y);
                    label.Shape = CreateLabelShape(label);
                }

                // 6. 使用迭代优化算法
                var optimizedLabels = IterativeOptimization(
                    labels,
                    forbiddenBands,
                     x1,
                     y1,
                      x2,
                     y2,
                    minSpacing,
                    maxMoveDistance);

                // 7. 返回优化后的JSON
                return JsonConvert.SerializeObject(
                    optimizedLabels.Select(l => new
                    {
                        l.LableID,
                        X = l.RoundedX,
                        Y = l.RoundedY,
                        l.lableType
                    }),
                    Formatting.Indented);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"优化过程中发生错误: {ex.Message}");
                return inputJson;
            }
        }

        // 创建标签形状
        private static LabelShape CreateLabelShape(LabelData label)
        {
            if (label.lableType == "GLAB")
            {
                return new RectangleShape
                {
                    X = label.RoundedX,
                    Y = label.RoundedY,
                    Width = 50,  // 长50mm
                    Height = 15  // 宽10mm
                };
            }
            else // SLAB
            {
                return new CircleShape
                {
                    X = label.RoundedX,
                    Y = label.RoundedY,
                    Radius = 5   // 直径10mm，半径5mm
                };
            }
        }

        // 迭代优化算法
        private static List<LabelData> IterativeOptimization(
            List<LabelData> labels,
            List<Rectangle> forbiddenBands,
             double x1,
            double y1,
             double x2,
              double y2,
            double minSpacing,
            double maxMoveDistance)
        {
            // 第一步：复制标签数据
            var currentLabels = labels.Select(l => new LabelData
            {
                LableID = l.LableID,
                X = l.X,
                Y = l.Y,
                RoundedX = l.RoundedX,
                RoundedY = l.RoundedY,
                OriginalRoundedX = l.OriginalRoundedX,
                OriginalRoundedY = l.OriginalRoundedY,
                lableType = l.lableType,
                Shape = CreateLabelShape(l)
            }).ToList();

            // 第二步：执行多轮微调
            int maxIterations = 50;
            double temperature = 5.0; // 模拟退火的"温度"，控制接受劣化解的概率
            double coolingRate = 0.95;
            Random random = new Random(42); // 固定种子确保可重复性

            for (int iteration = 0; iteration < maxIterations; iteration++)
            {
                bool anyChange = false;

                // 随机顺序处理标签，避免顺序偏差
                var shuffledLabels = currentLabels.OrderBy(l => random.Next()).ToList();

                foreach (var label in shuffledLabels)
                {
                    // 尝试轻微调整当前位置
                    var improvedPosition = TryLocalImprovement(
                        label,
                        forbiddenBands,
                        x1,  y1, x2, y2,
                        minSpacing,
                        maxMoveDistance,
                        currentLabels,
                        temperature,
                        random);

                    if (improvedPosition != null)
                    {
                        anyChange = true;
                        // 更新标签位置和形状
                        label.RoundedX = improvedPosition.Value.X;
                        label.RoundedY = improvedPosition.Value.Y;
                        label.Shape = CreateLabelShape(label);
                        label.MoveDistance = Math.Sqrt(
                            Math.Pow(label.RoundedX - label.OriginalRoundedX, 2) +
                            Math.Pow(label.RoundedY - label.OriginalRoundedY, 2));
                    }
                }

                // 降低"温度"
                temperature *= coolingRate;

                // 如果没有变化或温度很低，提前结束
                if (!anyChange && temperature < 0.1)
                    break;
            }

            // 第三步：最终微调（只调整有冲突的标签）
            FinalAdjustment(currentLabels, forbiddenBands, x1, y1, x2, y2, minSpacing, maxMoveDistance);

            return currentLabels;
        }

        // 尝试局部改进
        private static (int X, int Y)? TryLocalImprovement(
            LabelData label,
            List<Rectangle> forbiddenBands,
            double x1,
            double y1,
             double x2,
              double y2,
            double minSpacing,
            double maxMoveDistance,
            List<LabelData> allLabels,
            double temperature,
            Random random)
        {
            // 检查当前位置是否有效
            bool currentValid = IsPositionValid(
                label.RoundedX, label.RoundedY, label,
                forbiddenBands, x1, y1, x2, y2, minSpacing, allLabels);

            double currentDistance = Math.Sqrt(
                Math.Pow(label.RoundedX - label.OriginalRoundedX, 2) +
                Math.Pow(label.RoundedY - label.OriginalRoundedY, 2));
            if (label.lableType == "SLAB")
            {
                maxMoveDistance = 5;
            }
            // 如果当前位置有效且移动距离很小，保持当前位置
            if (currentValid && currentDistance <= maxMoveDistance)
            {
                return null;
            }

            // 生成候选位置（优先靠近原始位置）
            var candidates = GenerateNearbyCandidates(
                label.OriginalRoundedX, label.OriginalRoundedY,
                maxMoveDistance, random);

            // 按优先级排序：先按有效性，再按距离
            var evaluatedCandidates = candidates.Select(pos => new
            {
                Position = pos,
                Valid = IsPositionValid(pos.X, pos.Y, label, forbiddenBands,
                     x1, y1, x2, y2, minSpacing, allLabels),
                Distance = Math.Sqrt(
                    Math.Pow(pos.X - label.OriginalRoundedX, 2) +
                    Math.Pow(pos.Y - label.OriginalRoundedY, 2))
            }).ToList();

            // 寻找最优位置
            var bestCandidate = evaluatedCandidates
                .Where(c => c.Valid && c.Distance <= maxMoveDistance)
                .OrderBy(c => c.Distance)
                .FirstOrDefault();

            if (bestCandidate != null)
            {
                return bestCandidate.Position;
            }

            // 如果没有有效位置，使用模拟退火接受一个劣化解
            if (temperature > 0.1)
            {
                var acceptableCandidate = evaluatedCandidates
                    .Where(c => c.Distance <= maxMoveDistance * 1.5) // 允许稍微远一点
                    .OrderBy(c => random.NextDouble() * temperature - c.Distance)
                    .FirstOrDefault();

                if (acceptableCandidate != null)
                {
                    return acceptableCandidate.Position;
                }
            }

            return null;
        }

        // 最终调整：只处理仍有冲突的标签
        private static void FinalAdjustment(
            List<LabelData> labels,
            List<Rectangle> forbiddenBands,
             double x1,
            double y1,
             double x2,
              double y2,
            double minSpacing,
            double maxMoveDistance)
        {
            // 检测有冲突的标签
            var conflictedLabels = new List<LabelData>();
            foreach (var label in labels)
            {
                if (!IsPositionValid(label.RoundedX, label.RoundedY, label,
                    forbiddenBands, x1, y1, x2, y2, minSpacing, labels))
                {
                    conflictedLabels.Add(label);
                }
            }

            // 对有冲突的标签进行更激进的调整
            foreach (var label in conflictedLabels)
            {
                // 尝试在允许范围内找到任何有效位置
                var foundPosition = FindAnyValidPosition(
                    label, forbiddenBands, x1,y1, x2, y2, minSpacing,
                    labels, (int)maxMoveDistance * 2); // 放宽搜索范围

                if (foundPosition != null)
                {
                    label.RoundedX = foundPosition.Value.X;
                    label.RoundedY = foundPosition.Value.Y;
                    label.Shape = CreateLabelShape(label);
                    label.MoveDistance = Math.Sqrt(
                        Math.Pow(label.RoundedX - label.OriginalRoundedX, 2) +
                        Math.Pow(label.RoundedY - label.OriginalRoundedY, 2));
                }
            }
        }

        // 生成附近候选位置
        private static List<(int X, int Y)> GenerateNearbyCandidates(
            int originalX, int originalY, double maxDistance, Random random)
        {
            var candidates = new List<(int, int)>();

            // 首选：原始位置
            candidates.Add((originalX, originalY));

            // 次选：紧邻的8个方向（距离3mm）
            for (int dx = -3; dx <= 3; dx++)
            {
                for (int dy = -3; dy <= 3; dy++)
                {
                    if (dx == 0 && dy == 0) continue;
                    candidates.Add((originalX + dx, originalY + dy));
                }
            }

            // 再选：稍远的16个方向（距离6mm）
            for (int i = 0; i < 16; i++)
            {
                double angle = 2 * Math.PI * i / 16;
                int x = originalX + (int)(6 * Math.Cos(angle));
                int y = originalY + (int)(6 * Math.Sin(angle));
                candidates.Add((x, y));
            }

            // 最后：随机位置（在最大距离内）
            for (int i = 0; i < 20; i++)
            {
                double angle = 2 * Math.PI * random.NextDouble();
                double distance = maxDistance * random.NextDouble();
                int x = originalX + (int)(distance * Math.Cos(angle));
                int y = originalY + (int)(distance * Math.Sin(angle));
                candidates.Add((x, y));
            }

            // 去重
            return candidates.Distinct().ToList();
        }

        // 查找任何有效位置
        private static (int X, int Y)? FindAnyValidPosition(
            LabelData label,
            List<Rectangle> forbiddenBands,
           double x1,
            double y1,
             double x2,
              double y2,
            double minSpacing,
            List<LabelData> allLabels,
            int maxSearchRadius)
        {
            // 从原始位置开始螺旋搜索
            for (int radius = 0; radius <= maxSearchRadius; radius += 5)
            {
                for (int angle = 0; angle < 360; angle += 15)
                {
                    double rad = angle * Math.PI / 180;
                    int x = label.OriginalRoundedX + (int)(radius * Math.Cos(rad));
                    int y = label.OriginalRoundedY + (int)(radius * Math.Sin(rad));

                    if (IsPositionValid(x, y, label, forbiddenBands,
                        x1, y1, x2,y2, minSpacing, allLabels))
                    {
                        return (x, y);
                    }
                }
            }

            return null;
        }

        // 检查位置是否有效
        private static bool IsPositionValid(
            int x, int y,
            LabelData label,
            List<Rectangle> forbiddenBands,
            double x1,
            double y1,
             double x2,
            double y2,
            double minSpacing,
            List<LabelData> allLabels)
        {
            // 创建临时标签用于检查
            var testLabel = new LabelData
            {
                LableID = label.LableID,
                RoundedX = x,
                RoundedY = y,
                lableType = label.lableType
            };

            testLabel.Shape = CreateLabelShape(testLabel);

            // 1. 检查边界
            if (!IsWithinBounds(testLabel, x1, y1,x2,y2))
                return false;

            // 2. 检查禁止区域（保持3mm间距）
            if (IsInForbiddenBands(testLabel, forbiddenBands, 3.0))
                return false;

            // 3. 检查与所有其他标签的碰撞
            foreach (var otherLabel in allLabels)
            {
                if (otherLabel.LableID == label.LableID) continue;

                if (testLabel.Shape.Intersects(otherLabel.Shape, minSpacing))
                    return false;
            }

            return true;
        }


        // 检查是否在边界内
        private static bool IsWithinBounds(LabelData label, double x1, double y1, double x2, double y2)
        {
            var bbox = label.Shape.GetBoundingBox();

            return bbox.XMin >= x1+30 && bbox.XMax <= x2-30 &&
                   bbox.YMin >= y1+30 && bbox.YMax <= y2-30;
        }

        // 检查是否在禁止条形区域内（包括安全距离）
        private static bool IsInForbiddenBands(LabelData label, List<Rectangle> forbiddenBands, double safeDistance)
        {
            // 对于每个条形区域，检查标签是否与之相交（考虑安全距离）
            foreach (var band in forbiddenBands)
            {
                // 将条形区域扩展安全距离
                Rectangle expandedBand = band.Expand(safeDistance);

                if (label.Shape.IntersectsRectangle(expandedBand, 0))
                {
                    return true;
                }
            }

            return false;
        }
        // 创建四个条形禁止区域
        private static List<Rectangle> CreateForbiddenBands(Rectangle originalArea, double bandWidth)
        {
            // 四个条形区域：上、下、左、右
            // 每个条形区域的宽度为 bandWidth * 2（向两边各扩）

            List<Rectangle> bands = new List<Rectangle>();

            // 上边界条带（Y方向）
            bands.Add(new Rectangle
            {
                XMin = originalArea.XMin - bandWidth,  // 向左扩展
                XMax = originalArea.XMax + bandWidth,  // 向右扩展
                YMin = originalArea.YMax - bandWidth,  // 从边界向下
                YMax = originalArea.YMax + bandWidth   // 从边界向上
            });

            // 下边界条带
            bands.Add(new Rectangle
            {
                XMin = originalArea.XMin - bandWidth,
                XMax = originalArea.XMax + bandWidth,
                YMin = originalArea.YMin - bandWidth,
                YMax = originalArea.YMin + bandWidth
            });

            // 左边界条带（X方向）
            bands.Add(new Rectangle
            {
                XMin = originalArea.XMin - bandWidth,
                XMax = originalArea.XMin + bandWidth,
                YMin = originalArea.YMin - bandWidth,  // 注意：上下也扩展，避免角落空隙
                YMax = originalArea.YMax + bandWidth
            });

            // 右边界条带
            bands.Add(new Rectangle
            {
                XMin = originalArea.XMax - bandWidth,
                XMax = originalArea.XMax + bandWidth,
                YMin = originalArea.YMin - bandWidth,
                YMax = originalArea.YMax + bandWidth
            });

            return bands;
        }
        // 生成圆形搜索位置
        private static List<(int X, int Y)> GenerateCirclePositions(int centerX, int centerY, int radius, int numPoints)
        {
            var positions = new List<(int, int)>();

            for (int i = 0; i < numPoints; i++)
            {
                double angle = 2 * Math.PI * i / numPoints;
                int x = centerX + (int)(radius * Math.Cos(angle));
                int y = centerY + (int)(radius * Math.Sin(angle));
                positions.Add((x, y));
            }

            return positions;
        }

      

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
        // 使用示例

        public static void getOutPut(string inputJson, double x1, double y1, double x2, double y2)
        {
            // 读取JSON文件
          


            // 方法2：批量优化（更高效）
            string batchOutputJson = LabelOptimizer.OptimizeLabels(inputJson,x1,   y1,  x2,  y2);

           
            // 分析结果
            var inputLabels = JsonConvert.DeserializeObject<dynamic>(inputJson);
            var outputLabels = JsonConvert.DeserializeObject<dynamic>(batchOutputJson);
         


            // 统计移动的标签数量
            int movedCount = 0;
            var inputDict = ((JArray)inputLabels).ToDictionary(
                item => item["LableID"].ToString(),
                item => new { X = (int)item["X"], Y = (int)item["Y"] });

            foreach (var label in (JArray)outputLabels)
            {
                string id = label["LableID"].ToString();
                int x = (int)label["X"];
                int y = (int)label["Y"];

                if (inputDict.ContainsKey(id) &&
                    (inputDict[id].X != x || inputDict[id].Y != y))
                {
                    movedCount++;
                }
                try
                {
                    CommandExcute(id + $" at x " + x + " y " + y);

                    drawTool.UpdateModelView(DbElement.GetElement(id));

                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.ToString());
                }
            }

            MessageBox.Show($"移动的标签数: {movedCount}");
        }

    }
}
