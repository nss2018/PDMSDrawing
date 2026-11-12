// drawingPDMS - usefulTool（从 E3D 源 Utilities.cs 机械移植的 PDMS 版）
//
// 原 E3D 源 Utilities.cs 整体已归档在 _reference/Utilities.E3Doriginal.cs（含 Aveva.Core.* 原始逻辑）。
// 这里只抽取 active 工程实际调用的 7 个方法（SetAttribute / IsDeletable / GetPositionWrtWorld 系列），
// 全部只依赖标准 PDMS API（DbElement / DbAttribute / Position / DbQualifier / DbDouble / DbFormat / MessageBox），
// 不引用任何 E3D 专属类型，可在 .NET 3.5 Client 下编译。
using Aveva.Pdms.Database;
using Aveva.Pdms.Geometry;
using System;
using System.Windows.Forms;

namespace drawingPDMS
{
    public class usefulTool
    {
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
                        usefulTool.SetAttributeTemperature(element, attribute, value, true);
                    else if (attribute.Units.ToString() == "909565")
                    {
                        usefulTool.SetAttributePressure(element, attribute, value, true);
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

        public static string SetAttributeTemperature(
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

        public static bool IsDeletable(DbElement element, bool showMessage)
        {
            string asString1 = element.GetAsString(DbAttributeInstance.CLMID);
            string asString2 = element.GetAsString(DbAttributeInstance.USERC);
            bool flag1 = element.GetBool(DbAttributeInstance.LCLM);
            bool flag2 = element.GetBool(DbAttributeInstance.LOCK);
            if (element.IsDeleteable)
                return true;
            string text = "当前元素无法删除\n";
            if (flag2)
                text = element.GetAsString(DbAttributeInstance.NAME) + " Locked";
            if (!flag1)
                text = $"{text}声明 ID={asString1} User={asString2}";
            if (showMessage)
            {
                int num = (int)MessageBox.Show(text, "当前元素无法删除", MessageBoxButtons.OK, MessageBoxIcon.Hand);
            }
            return false;
        }

        public static Position GetPositionWrtWorld(DbElement element)
        {
            DbQualifier qualifier = new DbQualifier();
            qualifier.wrtQualifier = DbElement.GetElement("/*");
            Position.Create();
            return !(element.GetElementType() == DbElementTypeInstance.BRANCH) ? element.GetPosition(DbAttributeInstance.WORPOS) : element.GetPosition(DbAttributeInstance.HPOS, qualifier);
        }

        public static Position GetPositionWrtWorld(DbElement element, DbAttribute att)
        {
            return element.GetPosition(att, new DbQualifier()
            {
                wrtQualifier = DbElement.GetElement("/*")
            });
        }

        public static Position GetPositionWrtWorld(DbElement element, DbAttribute att, int point)
        {
            return element.GetPosition(att, point, DbElement.GetElement("/*"));
        }

        // 从 FangYuanPDMS\Utilities.cs:1716 移植的 PDMS 实现（等价 E3D 原 EvaluateExpression）
        // 用 DbExpression.Parse + element.EvaluateString 对字符串中的 [PML表达式] 求值并替换
        public static string EvaluateExpression(DbElement element, string str, bool showError)
        {
            int startIndex = str.IndexOf("[");
            for (; startIndex >= 0; startIndex = str.IndexOf("["))
            {
                int num1 = str.IndexOf("]");
                string oldValue = str.Substring(startIndex, num1 - startIndex + 1);
                string name = str.Substring(startIndex + 1, num1 - startIndex - 1);
                if (name.IndexOf("\\") >= 0) name = name.Replace("\\", "");
                DbExpression expression = DbExpression.Parse(name);
                string newValue;
                try
                {
                    newValue = element.EvaluateString(expression);
                }
                catch
                {
                    if (showError)
                        MessageBox.Show($"{str}中的{name}不是PML语句", "PML表达式错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                    newValue = "";
                }
                str = str.Replace(oldValue, newValue);
            }
            return str;
        }
    }
}
