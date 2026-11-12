// Decompiled with JetBrains decompiler
// Type: FangYuan.PDMS.NameSystem
// Assembly: FangYuanPDMS, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 59AF7F63-90CC-4428-89DA-25304C180540
// Assembly location: D:\AVEVA\Plant\PDMS12.1.SP4\FangYuanPDMS.dll

using Aveva.Pdms.Database;
using Aveva.PDMS.Database.Filters;
using Aveva.Pdms.Shared;
using drawingPDMS;
using System;
using System.Windows.Forms;


namespace drawingPDMS
{

    public class NameSystem
    {
        public static string NameAndRename(DbElement element, string value, bool showAlert, bool rename)
        {
            string text = "";
            string caption = "命名及批量命名";
            DbAttribute name = DbAttributeInstance.NAME;
            string asString = element.GetAsString(name);
            if (asString == value || asString == "/" + value)
                return "";
            char[] chArray = new char[1] { ' ' };
            value = value.Trim(chArray);
            if (value == "" || value.ToUpper() == "UNSET" || value.ToUpper() == "NULREF")
            {
                element.SetAttribute(DbAttributeInstance.NAME, "");
                return "";
            }
            int maximumSize = name.MaximumSize;
            if (value.Length > maximumSize)
                value = value.Substring(0, maximumSize);
            if (!value.StartsWith("/"))
                value = "/" + value;
            if (!DbElement.GetElement(value).IsNull)
            {
                string str = "名称已经存在";
                if (showAlert)
                {
                    int num = (int)MessageBox.Show(value + "名称已经存在", caption, MessageBoxButtons.OK, MessageBoxIcon.Hand);
                }
                return str;
            }
            try
            {
                if (!element.GetBool(DbAttributeInstance.ISNAME))
                    element.SetAttribute(name, value);
                else if (rename)
                {
                    string str = value;
                    CurrentElement.Element = element;
                    Aveva.Pdms.Utilities.CommandLine.Command.CreateCommand($"ren all {asString} {str}").RunInPdms();
                }
                else
                    element.SetAttribute(name, value);
            }
            catch (Exception ex)
            {
                text = ex.Message;
                if (showAlert)
                {
                    int num = (int)MessageBox.Show(text, caption, MessageBoxButtons.OK, MessageBoxIcon.Hand);
                }
            }
            return text;
        }

        public static bool NameIsExisted(string name, DbElement element)
        {
            Db[] dbArray = MDB.CurrentMDB.GetDBArray(element.DbType);
            bool flag = false;
            for (int index = 0; index < dbArray.Length; ++index)
            {
                if (dbArray[index].ElementExists(name, DbAttributeInstance.NAME))
                {
                    flag = true;
                    break;
                }
            }
            return flag;
        }

        public static bool NameElement(DbElement element, string name)
        {
            string caption = "命名";
            if (name == "" || element.IsNull || element.IsDeleted)
                return false;
            if (!name.StartsWith("/"))
                name = "/" + name;
            if (!(name != element.GetAsString(DbAttributeInstance.NAME)) || !DbElement.GetElement(name).IsNull)
                return false;
            if (name.Length > 50)
            {
                name = name.Substring(0, 50);
                if (!DbElement.GetElement(name).IsNull)
                {
                    int num = (int)MessageBox.Show("命名超过50个字符，自动截取50位后出现重复", caption, MessageBoxButtons.OK, MessageBoxIcon.Hand);
                    return false;
                }
                int num1 = (int)MessageBox.Show("命名超过50个字符，自动截取50位", caption, MessageBoxButtons.OK, MessageBoxIcon.Hand);
            }
            element.SetAttribute(DbAttributeInstance.NAME, name);
            return true;
        }

        private static string NameAddSuffix(DbElement element, string name, string[] suffix)
        {
            if (!name.StartsWith("/"))
                name = "/" + name;
            string asString = element.GetAsString(DbAttributeInstance.NAME);
            if (name == asString || DbElement.GetElement(name).IsNull)
                return name;
            int index = 0;
            string name1 = name + suffix[index];
            while (!DbElement.GetElement(name1).IsNull && index < suffix.Length)
            {
                ++index;
                name1 = name + suffix[index];
                if (name1 == asString)
                    return name;
            }
            return name1;
        }

        public static string NameElementWithSuffix(DbElement element, string name, string[] suffix)
        {
            string name1 = NameSystem.NameAddSuffix(element, name, suffix);
            NameSystem.NameElement(element, name1);
            return name1;
        }

        public static string NameElementWithSuffix(DbElement element, string name, string suffixString)
        {
            string[] suffix = suffixString.Split(' ');
            return NameSystem.NameElementWithSuffix(element, name, suffix);
        }

        public static int NameElementWithNumber(
          DbElement element,
          string prefix,
          int number,
          int length)
        {
            if (element.IsNull || element.IsDeleted)
                return number;
            string asString = element.GetAsString(DbAttributeInstance.NAME);
            if (!prefix.StartsWith("/"))
                prefix = "/" + prefix;
            string str1 = number.ToString().PadLeft(length, '0');
            string name = prefix + str1;
            if (name == asString)
                return number;
            string str2;
            for (; !DbElement.GetElement(name).IsNull; name = prefix + str2)
            {
                ++number;
                str2 = number.ToString().PadLeft(length, '0');
            }
            NameSystem.NameElement(element, name);
            return number;
        }

        public static string NameElementWithRule(
          DbElement element,
          string nameRule,
          string numberKey,
          int number,
          int length,
          bool named)
        {
            if (element.IsNull || element.IsDeleted)
                return "";
            string str = usefulTool.EvaluateExpression(element, nameRule, true);
            if (str == "")
                return str;
            if (!str.StartsWith("/"))
                str = "/" + str;
            ++number;
            string newValue1 = number.ToString().PadLeft(length, '0');
            string name;
            string newValue2;
            for (name = str.Replace(numberKey, newValue1); !DbElement.GetElement(name).IsNull; name = str.Replace(numberKey, newValue2))
            {
                ++number;
                newValue2 = number.ToString().PadLeft(length, '0');
            }
            if (named)
                NameSystem.NameElement(element, name);
            return name;
        }

        public static string NameElementWithRule(
          DbElement element,
          string nameRule,
          string numberKey,
          int number,
          int length,
          bool named,
          DbElement hierarchy,
          string uniqueRule)
        {
            if (element.IsNull || element.IsDeleted)
                return "";
            string expression = usefulTool.EvaluateExpression(element, uniqueRule, true);
            string newValue = number.ToString().PadLeft(length, '0');
            TypeFilter filt = new TypeFilter(element.GetElementType());
            DBElementCollection elementCollection = new DBElementCollection(hierarchy, (BaseFilter)filt);
            bool flag = true;
            while (flag)
            {
                ++number;
                newValue = number.ToString().PadLeft(length, '0');
                string str = expression.Replace(numberKey, newValue);
                flag = false;
                foreach (DbElement dbElement in elementCollection)
                {
                    if (dbElement.GetAsString(DbAttributeInstance.NAME).IndexOf(str) >= 0)
                    {
                        flag = true;
                        break;
                    }
                }
            }
            string name = usefulTool.EvaluateExpression(element, nameRule, true).Replace(numberKey, newValue);
            if (named)
                NameSystem.NameElement(element, name);
            return name;
        }
    }
}