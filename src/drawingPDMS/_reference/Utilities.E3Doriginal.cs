using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace drawingE3d
{
    using Aveva.Core.Database;
    using Aveva.Core.Database.Filters;
    using Aveva.Core.Geometry;
    using Aveva.Core.Utilities.Messaging;
    using Aveva.Core.Utilities.StringUtilities;
    // Decompiled with JetBrains decompiler
    // Type: usefulTool
    // Assembly: FangYuanPDMS, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null
    // MVID: 59AF7F63-90CC-4428-89DA-25304C180540
    // Assembly location: D:\AVEVA\Plant\PDMS12.1.SP4\FangYuanPDMS.dll


    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Data;
    using System.IO;
    using System.Reflection;
    using System.Windows.Forms;


    namespace drawingE3d
    {

        public class usefulTool
        {
            private static double viewStep = 1000.0;

            public static DbElement CopyElementBeforeFirst(DbElement eleToCopy)
            {
                DbElement dbElement;
                if (eleToCopy.IsValid)
                {
                    string copyPrefix = usefulTool.CreateCopyPrefix(eleToCopy);
                    DbCopyOption option = new DbCopyOption();
                    option.FromName = "";
                    option.ToName = copyPrefix;
                    option.Rename = true;
                    DbElement owner = eleToCopy.Owner;
                    DbElement hierarchyBeforeFirst = eleToCopy.CreateCopyHierarchyBeforeFirst(owner, option);
                    if (eleToCopy.GetBool(DbAttributeInstance.ISNAME))
                    {
                        string val = $"/{copyPrefix}{eleToCopy.GetString(DbAttributeInstance.FLNN)}";
                        hierarchyBeforeFirst.SetAttribute(DbAttributeInstance.NAME, val);
                    }
                    dbElement = hierarchyBeforeFirst;
                }
                else
                    dbElement = (DbElement)null;
                return dbElement;
            }

            private static string CreateCopyPrefix(DbElement ele)
            {
                ArrayList arrayList = new ArrayList();
                arrayList.Add((object)ele);
                foreach (DbElement dbElement in new DBElementCollection(ele, (BaseFilter)new TrueFilter()))
                    arrayList.Add((object)dbElement);
                for (int index = 1; index <= 100; ++index)
                {
                    bool flag = false;
                    string copyPrefix = index != 1 ? string.Format("", (object)$"({index.ToString()})-") : "" + "-";
                    foreach (DbElement element in arrayList)
                    {
                        if (element.GetBool(DbAttributeInstance.ISNAME))
                        {
                            string str = element.GetString(DbAttributeInstance.FLNN);
                            if (NameSystem.NameIsExisted((copyPrefix + str).Insert(0, "/"), element))
                            {
                                flag = true;
                                break;
                            }
                        }
                    }
                    if (!flag)
                        return copyPrefix;
                }
                return "";
            }

            public static bool InsertElement(DbElement ele, int index)
            {
                bool flag;
                if (ele.IsValid)
                {
                    DbElement owner = ele.Owner;
                    if (owner.IsValid)
                    {
                        DbElement[] dbElementArray1 = owner.Members();
                        int num = 0;
                        DbElement[] dbElementArray2 = dbElementArray1;
                        for (int index1 = 0; index1 < dbElementArray2.Length && !dbElementArray2[index1].Equals((object)ele); ++index1)
                            ++num;
                        if (index + 1 > dbElementArray1.Length)
                            index = dbElementArray1.Length - 1;
                        if (dbElementArray1[index].IsValid)
                        {
                            try
                            {
                                if (index == num)
                                    return true;
                                if (num > index)
                                {
                                    ele.InsertBefore(dbElementArray1[index]);
                                    return true;
                                }
                                ele.InsertAfter(dbElementArray1[index]);
                                return true;
                            }
                            catch
                            {
                                return false;
                            }
                        }
                        else
                            flag = false;
                    }
                    else
                        flag = false;
                }
                else
                    flag = false;
                return flag;
            }

            public static bool IsArrayComponent(string testText, ref DbAttribute att, ref int index)
            {
                bool flag = false;
                int length1 = testText.ToLower().IndexOf(" num ");
                if (length1 > 0)
                {
                    att = DbAttribute.GetDbAttribute(testText.Substring(0, length1));
                    flag = att != (DbAttribute)null && att.HashValue != 0;
                    if (flag)
                    {
                        try
                        {
                            index = int.Parse(testText.Substring(length1 + 5));
                        }
                        catch
                        {
                            flag = false;
                        }
                    }
                }
                else
                {
                    int length2 = testText.IndexOf('[');
                    if (length2 > 0)
                    {
                        int num = testText.IndexOf(']');
                        if (num > length2 && testText.Substring(num + 1).Trim() == "")
                        {
                            att = DbAttribute.GetDbAttribute(testText.Substring(0, length2));
                            flag = att != (DbAttribute)null && att.HashValue != 0;
                            if (flag)
                            {
                                try
                                {
                                    index = int.Parse(testText.Substring(length2 + 1, num - length2 - 1));
                                }
                                catch
                                {
                                    flag = false;
                                }
                            }
                        }
                    }
                }
                return flag;
            }

            private static bool isHvacBore(DbElement ele, DbAttribute att)
            {
                return ele.Owner != (DbElement)null && ele.Owner.GetActualType().Equals((object)DbElementTypeInstance.HVAC) && (att.Equals((object)DbAttributeInstance.HBOR) || att.Equals((object)DbAttributeInstance.TBOR));
            }

            public static double ParseDoubleWithUnits(DbAttributeUnit units, string inText)
            {
                DbExpression expression = DbExpression.Parse(inText);
                return DbElement.GetElement("/*").EvaluateDouble(expression, units);
            }

            public static string SetArrayAttributeComponentFromString(
              DbElement ele,
              DbAttribute att,
              int index,
              string val)
            {
                string str ="";
                if (index <= 0)
                {
                    
                }
                else
                {
                    switch (att.Type)
                    {
                        case DbAttributeType.INTEGER:
                            int[] integerArray = ele.GetIntegerArray(att);
                            int length1 = integerArray.Length;
                            if (index > length1)
                                length1 = index;
                            int[] val1 = new int[length1];
                            for (int index1 = 0; index1 < integerArray.Length; ++index1)
                                val1[index1] = integerArray[index1];
                            for (int length2 = integerArray.Length; length2 < length1; ++length2)
                                val1[length2] = 0;
                            val1[index - 1] = int.Parse(val);
                            ele.SetAttribute(att, val1);
                            break;
                        case DbAttributeType.DOUBLE:
                            bool flag = usefulTool.isHvacBore(ele, att);
                            double[] doubleArray = ele.GetDoubleArray(att);
                            int length3 = doubleArray.Length;
                            if (index > length3)
                                length3 = index;
                            double[] val2 = new double[length3];
                            for (int index2 = 0; index2 < doubleArray.Length; ++index2)
                                val2[index2] = doubleArray[index2];
                            for (int length4 = doubleArray.Length; length4 < length3; ++length4)
                                val2[length4] = 0.0;
                            DbDimension standardDimension = att.Dimension.StandardDimension;
                            DbDoubleUnits currentUnits = DbDoubleDimension.GetDimension(standardDimension).CurrentUnits;
                            double doubleWithUnits = usefulTool.ParseDoubleWithUnits(att.Units, val);
                            int num = flag ? 0 : (standardDimension == DbDimension.BORE ? 1 : 0);
                            val2[index - 1] = num == 0 ? DbDouble.Create(doubleWithUnits, currentUnits).Value : DbDouble.CreateBore(doubleWithUnits).Value;
                            ele.SetAttribute(att, val2);
                            break;
                        case DbAttributeType.BOOL:
                            bool[] boolArray = ele.GetBoolArray(att);
                            int length5 = boolArray.Length;
                            if (index > length5)
                                length5 = index;
                            bool[] val3 = new bool[length5];
                            for (int index3 = 0; index3 < boolArray.Length; ++index3)
                                val3[index3] = boolArray[index3];
                            for (int length6 = boolArray.Length; length6 < length5; ++length6)
                                val3[length6] = false;
                            val3[index - 1] = bool.Parse(val);
                            ele.SetAttribute(att, val3);
                            break;
                        case DbAttributeType.ELEMENT:
                            DbElement[] elementArray = ele.GetElementArray(att);
                            int length7 = elementArray.Length;
                            if (index > length7)
                                length7 = index;
                            DbElement[] dbElementArray = new DbElement[length7];
                            for (int index4 = 0; index4 < elementArray.Length; ++index4)
                                dbElementArray[index4] = elementArray[index4];
                            for (int length8 = elementArray.Length; length8 < length7; ++length8)
                                dbElementArray[length8] = DbElement.GetElement();
                            dbElementArray[index - 1] = DbElement.GetElement(val);
                            ele.SetAttribute(att, dbElementArray);
                            break;
                        default:
                            return "";
                    }
                    str = "";
                }
                return str;
            }

            public static string SetAttributeByCommand(DbElement ele, DbAttribute att, string val)
            {
                try
                {
                    DbElement element = CurrentElement.Element;
                    CurrentElement.Element = ele;
                    bool flag = Aveva.Core.Utilities.CommandLine.Command.CreateCommand($"{att.Name} {val}").Run();
                    if (!flag && att.GetInteger(DbAttributeField.DTYP) == 4)
                        flag = Aveva.Core.Utilities.CommandLine.Command.CreateCommand($"{att.Name} '{val}'").Run();
                    CurrentElement.Element = element;
                    if (!flag)
                        return "Invalid value";
                }
                catch (PdmsException ex)
                {
                    return ex.Error.MessageText();
                }
                return "";
            }

            public static string SetAttributeFromArray(DbElement dbEle, DbAttribute dbAtt, ArrayList aList)
            {
                string a = (string)aList[0];
                int index = (int)aList[1] - 1;
                string str;
                if (index == -1)
                    str = usefulTool.SetAttributeByCommand(dbEle, dbAtt, a);
                else if (index < -1)
                {
                    str = "Invalid array element";
                }
                else
                {
                    switch (dbAtt.Type)
                    {
                        case DbAttributeType.INTEGER:
                            int[] integerArray1 = dbEle.GetIntegerArray(dbAtt);
                            integerArray1[index] = Convert.ToInt32(a);
                            dbEle.SetAttribute(dbAtt, integerArray1);
                            break;
                        case DbAttributeType.DOUBLE:
                            DbDouble[] dbDoubleArray = dbEle.GetDbDoubleArray(dbAtt);
                            try
                            {
                                dbDoubleArray[index] = DbDouble.Create(a);
                            }
                            catch
                            {
                                if (!dbAtt.GetInteger(DbAttributeField.ITYP).Equals(16 /*0x10*/))
                                    return "Invalid input";
                                int hashValue = Hash.CreateHash().stringToHashValue(a.ToUpper());
                                dbDoubleArray[index] = DbDouble.Create((double)hashValue);
                            }
                            dbEle.SetAttribute(dbAtt, dbDoubleArray);
                            break;
                        case DbAttributeType.BOOL:
                            dbEle.GetBoolArray(dbAtt)[index] = Convert.ToBoolean(a);
                            break;
                        case DbAttributeType.STRING:
                            string[] asStringArray = dbEle.GetAsStringArray(dbAtt);
                            asStringArray[index] = a;
                            string val = string.Join(" ", asStringArray);
                            dbEle.SetAttribute(dbAtt, val);
                            break;
                        case DbAttributeType.ELEMENT:
                            DbElement[] elementArray = dbEle.GetElementArray(dbAtt);
                            DbElement element = DbElement.GetElement(a);
                            if (!element.IsValid && !a.Equals(string.Empty))
                                return "";
                            elementArray[index] = element;
                            dbEle.SetAttribute(dbAtt, elementArray);
                            break;
                        case DbAttributeType.WORD:
                            int[] integerArray2 = dbEle.GetIntegerArray(dbAtt);
                            Hash hash = Hash.CreateHash();
                            integerArray2[index] = hash.stringToHashValue(a.ToUpper());
                            if (integerArray2[index] <= 0)
                                return "Invalid value";
                            dbEle.SetAttribute(dbAtt, integerArray2);
                            break;
                        default:
                            return "";
                    }
                    str = "";
                }
                return str;
            }

            public static string SetAttributeFromString(DbElement ele, DbAttribute att, string val)
            {
                switch (att.Type)
                {
                    case DbAttributeType.INTEGER:
                        try
                        {
                            int integer = att.GetInteger(DbAttributeField.ITYP);
                            int num1;
                            if (!integer.Equals(48 /*0x30*/))
                            {
                                integer = att.GetInteger(DbAttributeField.ITYP);
                                num1 = integer.Equals(53) ? 1 : 0;
                            }
                            else
                                num1 = 1;
                            if (num1 != 0)
                            {
                                if (val.Equals(string.Empty))
                                {
                                    ele.SetAttributeDefault(att);
                                    break;
                                }
                                DbExpression exp;
                                PdmsMessage error;
                                if (!DbExpression.Parse(val, out exp, out error))
                                    return error.MessageText();
                                ele.SetAttribute(att, exp);
                                break;
                            }
                            integer = att.GetInteger(DbAttributeField.ITYP);
                            if (integer.Equals(38))
                            {
                                if (val.Equals(string.Empty))
                                {
                                    ele.SetAttributeDefault(att);
                                }
                                else
                                {
                                    DbCollection collection;
                                    if (!DbCollection.Parse(val, out collection, out PdmsMessage _))
                                        return usefulTool.SetAttributeByCommand(ele, att, val);
                                    ele.SetAttribute(att, collection.GetStack());
                                }
                            }
                            else
                            {
                                if (att.IsArray)
                                    return usefulTool.SetArrayAttributeFromString(ele, att, val);
                                if (val.Equals(string.Empty))
                                {
                                    ele.SetAttributeDefault(att);
                                }
                                else
                                {
                                    int num2 = int.Parse(val);
                                    ele.SetAttribute(att, num2);
                                }
                            }
                            break;
                        }
                        catch
                        {
                            return usefulTool.SetAttributeByCommand(ele, att, val);
                        }
                    case DbAttributeType.DOUBLE:
                        try
                        {
                            if (att.IsArray)
                                return usefulTool.SetArrayAttributeFromString(ele, att, val);
                            DbDouble dbDouble = usefulTool.isHvacBore(ele, att) || att.Dimension.StandardDimension != DbDimension.BORE ? DbDouble.Create(val) : DbDouble.CreateBore(val);
                            DbDoubleDimension dimension = dbDouble.Dimension;
                            if (att.Units == DbAttributeUnit.PTYP)
                            {
                                ele.SetAttribute(att, dbDouble);
                                break;
                            }
                            if (val.Equals(string.Empty))
                            {
                                double def = 0.0;
                                att.GetDefault(ele.GetElementType(), out def);
                                ele.SetAttribute(att, def);
                            }
                            else
                            {
                                if (dimension.StandardDimension != DbDimension.NONE && !att.Dimension.Equals((object)dimension))
                                    return "Invalid dimension";
                                ele.SetAttribute(att, dbDouble);
                            }
                            break;
                        }
                        catch
                        {
                            return usefulTool.SetAttributeByCommand(ele, att, val);
                        }
                    case DbAttributeType.BOOL:
                        try
                        {
                            if (att.IsArray)
                                return usefulTool.SetArrayAttributeFromString(ele, att, val);
                            if (val.Equals(string.Empty))
                            {
                                ele.SetAttributeDefault(att);
                                break;
                            }
                            bool flag = bool.Parse(val);
                            ele.SetAttribute(att, flag);
                            break;
                        }
                        catch
                        {
                            return usefulTool.SetAttributeByCommand(ele, att, val);
                        }
                    case DbAttributeType.STRING:
                        switch ((DbAttributeType)att.GetInteger(DbAttributeField.DTYP))
                        {
                            case DbAttributeType.INTEGER:
                                return usefulTool.SetAttributeByCommand(ele, att, val);
                            case DbAttributeType.STRING:
                                ele.SetAttribute(att, val);
                                return "";
                            default:
                                try
                                {
                                    if (att.GetBool(DbAttributeField.CATPAR) && att.GetInteger(DbAttributeField.ITYP).Equals(10))
                                    {
                                        string str = val.Trim();
                                        if (str.StartsWith("(") && str.EndsWith(")"))
                                        {
                                            DbExpression exp;
                                            PdmsMessage error;
                                            if (!DbExpression.Parse(val, out exp, out error))
                                                return error.MessageText();
                                            ele.SetAttribute(att, exp);
                                            break;
                                        }
                                        ele.SetAttribute(att, val);
                                        break;
                                    }
                                    if (att.GetBool(DbAttributeField.CATPAR) || att.GetInteger(DbAttributeField.ITYP).Equals(48 /*0x30*/) || att.GetInteger(DbAttributeField.ITYP).Equals(53))
                                    {
                                        DbExpression exp;
                                        PdmsMessage error;
                                        if (!DbExpression.Parse(val, out exp, out error))
                                            return error.MessageText();
                                        ele.SetAttribute(att, exp);
                                    }
                                    else
                                        ele.SetAttribute(att, val);
                                    break;
                                }
                                catch
                                {
                                    return usefulTool.SetAttributeByCommand(ele, att, val);
                                }
                        }
                        break;
                    case DbAttributeType.ELEMENT:
                        try
                        {
                            if (att.IsArray)
                                return usefulTool.SetArrayAttributeFromString(ele, att, val);
                            if (val.Equals(string.Empty) || val.ToUpper().Equals("NULREF"))
                            {
                                ele.SetAttribute(att, DbElement.GetElement());
                                return "";
                            }
                            DbElement element = DbElement.GetElement(val);
                            if (!element.IsValid)
                                element = DbElement.GetElement("/" + val);
                            if (!element.IsValid)
                                return "";
                            ele.SetAttribute(att, element);
                            break;
                        }
                        catch
                        {
                            return usefulTool.SetAttributeByCommand(ele, att, val);
                        }
                    case DbAttributeType.WORD:
                        try
                        {
                            int integer = att.GetInteger(DbAttributeField.ITYP);
                            if (integer.Equals(42))
                            {
                                string[] strArray = val.Trim().Replace("\n", "").Split(' ');
                                ArrayList arrayList = new ArrayList();
                                foreach (string name in strArray)
                                {
                                    if (name.CompareTo(string.Empty) != 0)
                                    {
                                        DbElementType elementType = DbElementType.GetElementType(name);
                                        if (elementType.IsValid)
                                            arrayList.Add((object)elementType.GetHashCode());
                                    }
                                }
                                ele.SetAttribute(att, (int[])arrayList.ToArray(typeof(int)));
                                break;
                            }
                            bool flag = val.IndexOf(' ') < 0;
                            if (flag)
                            {
                                if (val.Equals(string.Empty))
                                {
                                    ele.SetAttributeDefault(att);
                                }
                                else
                                {
                                    integer = att.GetInteger(DbAttributeField.ITYP);
                                    if (!integer.Equals(52))
                                        return usefulTool.SetAttributeByCommand(ele, att, val);
                                    ele.SetAttribute(att, val);
                                }
                            }
                            if (!flag)
                                return "Invalid value";
                            break;
                        }
                        catch
                        {
                            return usefulTool.SetAttributeByCommand(ele, att, val);
                        }
                    case DbAttributeType.DIRECTION:
                        try
                        {
                            if (val.Equals(string.Empty))
                            {
                                ele.SetAttributeDefault(att);
                                break;
                            }
                            PdmsMessage error = (PdmsMessage)null;
                            Direction pos;
                            if (Direction.Parse(val, out pos, out error))
                            {
                                ele.SetAttribute(att, pos);
                                break;
                            }
                            return error != null ? error.MessageText() : "Invalid direction";
                        }
                        catch
                        {
                            return usefulTool.SetAttributeByCommand(ele, att, val);
                        }
                    case DbAttributeType.POSITION:
                        try
                        {
                            if (val.Equals(string.Empty))
                            {
                                ele.SetAttributeDefault(att);
                                break;
                            }
                            PdmsMessage error = (PdmsMessage)null;
                            Position pos;
                            if (Position.Parse(val, out pos, out error))
                            {
                                ele.SetAttribute(att, pos);
                                break;
                            }
                            return error != null ? error.MessageText() : "Invalid position";
                        }
                        catch
                        {
                            return usefulTool.SetAttributeByCommand(ele, att, val);
                        }
                    case DbAttributeType.ORIENTATION:
                        try
                        {
                            if (val.Equals(string.Empty))
                            {
                                ele.SetAttributeDefault(att);
                                break;
                            }
                            PdmsMessage error = (PdmsMessage)null;
                            Aveva.Core.Geometry.Orientation pos;
                            if (Aveva.Core.Geometry.Orientation.Parse(val, out pos, out error))
                            {
                                ele.SetAttribute(att, pos);
                                break;
                            }
                            return error != null ? error.MessageText() : "Invalid orientation";
                        }
                        catch
                        {
                            return usefulTool.SetAttributeByCommand(ele, att, val);
                        }
                    case DbAttributeType.DATETIME:
                        try
                        {
                            if (val.Equals(string.Empty))
                            {
                                ele.SetAttribute(att, new DateTime());
                                break;
                            }
                            DateTime val1 = DateTime.Parse(val);
                            ele.SetAttribute(att, val1);
                            break;
                        }
                        catch
                        {
                            return usefulTool.SetAttributeByCommand(ele, att, val);
                        }
                    default:
                        return "";
                }
                return "";
            }

            public static string SetArrayAttributeFromString(DbElement ele, DbAttribute att, string val)
            {
                string str = "";
                double[] val1 = usefulTool.SplitAsDoubleArray(val);
                ele.SetAttribute(att, val1);
                return str;
            }

            public static bool[] SplitAsBoolArray(string inText)
            {
                string[] strArray = inText.Split(' ', '\n');
                bool[] flagArray = new bool[strArray.Length];
                for (int index = 0; index < strArray.Length; ++index)
                    flagArray[index] = bool.Parse(strArray[index]);
                return flagArray;
            }

            public static DbElement[] SplitAsDbElementArray(string inText)
            {
                string str = inText.Replace("Nulref", "=0/0").Replace("\n", " ");
                int length = 0;
                int startIndex1 = 0;
                while (true)
                {
                    int startIndex2 = str.IndexOf('/', startIndex1);
                    if (startIndex2 >= startIndex1)
                    {
                        ++length;
                        int num = str.IndexOf(' ', startIndex2);
                        if (num >= 0)
                            startIndex1 = num + 1;
                        else
                            break;
                    }
                    else
                        break;
                }
                DbElement[] dbElementArray = new DbElement[length];
                int startIndex3 = 0;
                for (int index = 0; index < length; ++index)
                {
                    int startIndex4 = str.IndexOf('/', startIndex3);
                    int num = str.IndexOf(' ', startIndex4);
                    if (num < 0)
                        num = str.Length;
                    dbElementArray[index] = DbElement.GetElement(str.Substring(startIndex3, num - startIndex3).Trim());
                    if (!dbElementArray[index].IsValid)
                        dbElementArray[index] = DbElement.GetElement();
                    startIndex3 = num + 1;
                }
                return dbElementArray;
            }

            public static ArrayList SplitAsDoubleArrayListWithUnits(DbAttributeUnit units, string inText)
            {
                ArrayList arrayList1 = new ArrayList();
                ArrayList arrayList2 = new ArrayList();
                int index1 = 0;
                string str = inText.Replace("\n", " ");
                int length = str.Length;
                while (index1 < length && str[index1] == ' ')
                    ++index1;
                ArrayList arrayList3;
                if (index1 == length)
                {
                    arrayList3 = arrayList1;
                }
                else
                {
                    arrayList2.Add((object)index1);
                    while (true)
                    {
                        int index2 = str.IndexOf(' ', (int)arrayList2[arrayList2.Count - 1]);
                        if (index2 >= 0)
                        {
                            while (index2 < length && str[index2] == ' ')
                                ++index2;
                            if (index2 != length)
                                arrayList2.Add((object)index2);
                            else
                                break;
                        }
                        else
                            break;
                    }
                    int count = arrayList2.Count;
                    arrayList2.Add((object)length);
                    for (int index3 = 0; index3 < count; ++index3)
                    {
                        for (int index4 = count; index4 > index3; --index4)
                        {
                            try
                            {
                                double doubleWithUnits = usefulTool.ParseDoubleWithUnits(units, str.Substring((int)arrayList2[index3], (int)arrayList2[index4] - (int)arrayList2[index3]));
                                arrayList1.Add((object)doubleWithUnits);
                                index3 = index4 - 1;
                                break;
                            }
                            catch (Exception ex)
                            {
                                if (index4 == index3 + 1)
                                    throw ex;
                            }
                        }
                    }
                    arrayList3 = arrayList1;
                }
                return arrayList3;
            }

            public static double[] SplitAsDoubleArrayWithUnits(DbAttributeUnit units, string inText)
            {
                ArrayList arrayList = usefulTool.SplitAsDoubleArrayListWithUnits(units, inText);
                double[] numArray = new double[arrayList.Count];
                for (int index = 0; index < arrayList.Count; ++index)
                    numArray[index] = (double)arrayList[index];
                return numArray;
            }

            public static double[] SplitAsDoubleArray(string str)
            {
                string[] strArray = str.Split(' ', '\n');
                double[] numArray = new double[strArray.Length];
                for (int index = 0; index < strArray.Length; ++index)
                    numArray[index] = double.Parse(strArray[index]);
                return numArray;
            }

            public static int[] SplitAsIntArray(string inText)
            {
                string[] strArray = inText.Split(' ', '\n');
                int[] numArray = new int[strArray.Length];
                for (int index = 0; index < strArray.Length; ++index)
                    numArray[index] = int.Parse(strArray[index]);
                return numArray;
            }

            public static double StringToDouble(string numStr)
            {
                double num;
                try
                {
                    num = double.Parse(numStr);
                }
                catch
                {
                    if (numStr.IndexOf(".") >= 0)
                        numStr = numStr.Replace(".", ",");
                    else if (numStr.IndexOf(",") >= 0)
                        numStr = numStr.Replace(",", ".");
                    num = double.Parse(numStr);
                }
                return num;
            }

            public static Aveva.Core.Database.DbType DatabaseTypeGet(string type)
            {
                Aveva.Core.Database.DbType dbType = Aveva.Core.Database.DbType.All;
                type = type.Trim().ToUpper();
                if (type.IndexOf("DESI") == 0)
                    dbType = Aveva.Core.Database.DbType.Design;
                else if (type.IndexOf("CATA") == 0)
                    dbType = Aveva.Core.Database.DbType.Catalog;
                else if (type.IndexOf("DICT") == 0)
                    dbType = Aveva.Core.Database.DbType.Dictionary;
                else if (type.IndexOf("DRAF") == 0)
                    dbType = Aveva.Core.Database.DbType.Draft;
                else if (type.IndexOf("ENGI") == 0)
                    dbType = Aveva.Core.Database.DbType.Engineering;
                else if (type.IndexOf("GLOB") == 0)
                    dbType = Aveva.Core.Database.DbType.Global;
                else if (type.IndexOf("ISO") == 0)
                    dbType = Aveva.Core.Database.DbType.Spooler;
                else if (type.IndexOf("PROP") == 0)
                    dbType = Aveva.Core.Database.DbType.Property;
                else if (type.IndexOf("SCHE") == 0)
                    dbType = Aveva.Core.Database.DbType.Schematic;
                else if (type.IndexOf("SPOO") == 0)
                    dbType = Aveva.Core.Database.DbType.Spooler;
                else if (type.IndexOf("SYS") == 0)
                    dbType = Aveva.Core.Database.DbType.System;
                else if (type.IndexOf("TRAN") == 0)
                    dbType = Aveva.Core.Database.DbType.Transaction;
                return dbType;
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

            public static string GetBoreUnit(DbElement element)
            {
                return DbDouble.Create(element.GetAsString(DbAttributeInstance.BORE)).Units.ShortName;
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
                        value = usefulTool.GetTemperatureValue(value);
                    else if (attribute.Type.ToString() == "DOUBLE" && attribute.Units.ToString() == "909565")
                        value = usefulTool.GetPressureValue(value);
                    if (pipe.IsAttributeSetable(attribute))
                    {
                       usefulTool.SetAttribute(pipe, attribute, value);
                        Aveva.Core.Database.Filters.TypeFilter filt1 = new Aveva.Core.Database.Filters.TypeFilter(DbElementTypeInstance.BRANCH);
                        Aveva.Core.Database.Filters.TypeFilter filt2 = new Aveva.Core.Database.Filters.TypeFilter(DbElementTypeInstance.BRANCH.MemberTypes());
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
                                usefulTool.SetAttribute(element, attribute, value);
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
                                    usefulTool.SetAttribute(element, attribute, value);
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

            public static string FullName(DbElement element)
            {
                string asString = element.GetAsString(DbAttributeInstance.NAMTYP);
                if (element.GetBool(DbAttributeInstance.ISNAME))
                    asString = element.GetAsString(DbAttributeInstance.NAME);
                return asString;
            }

            public static string Name(DbElement element, bool ToExcel)
            {
                string str1 = "";
                if (element.IsNull)
                    return str1;
                string str2;
                if (element.GetBool(DbAttributeInstance.ISNAME))
                {
                    str2 = element.GetAsString(DbAttributeInstance.NAME);
                }
                else
                {
                    str2 = element.GetAsString(DbAttributeInstance.REF);
                    if (ToExcel)
                        str2 = "'" + element.GetAsString(DbAttributeInstance.REF);
                }
                if (element.GetElementType() == DbElementTypeInstance.TUBING)
                    str2 += "LeaveTube";
                return str2;
            }

            public static void DeleteElement(DbElement element)
            {
                if (element.IsNull || element.IsDeleted)
                    return;
                element.Delete();
            }

            public static void DeleteMembers(DbElement owner)
            {
                if (owner.IsNull || owner.IsDeleted)
                    return;
                foreach (DbElement member in owner.Members())
                    usefulTool.DeleteElement(member);
            }

            public static void DeleteBranchMembers(DbElement branch)
            {
                if (branch.IsNull || branch.IsDeleted)
                    return;
                DBElementCollection elementCollection = new DBElementCollection(branch, (BaseFilter)new Aveva.Core.Database.Filters.TypeFilter(branch.GetElementType().MemberTypes()));
                List<DbElement> dbElementList = new List<DbElement>();
                foreach (DbElement dbElement in elementCollection)
                {
                    if (!(dbElement.GetElementType() == DbElementTypeInstance.TUBING))
                        dbElementList.Add(dbElement);
                }
                for (int index = dbElementList.Count - 1; index >= 0; --index)
                    usefulTool.DeleteElement(dbElementList[index]);
            }

            public static string GetProjectDefaultFolder()
            {
                string projectDefaultFolder = "";
                Aveva.Core.Utilities.CommandLine.Command command = Aveva.Core.Utilities.CommandLine.Command.CreateCommand($"var !!PATH evar {Project.CurrentProject.Name}dflts");
                if (command.RunInPdms())
                    projectDefaultFolder = command.GetPMLVariableString("PATH");
                return projectDefaultFolder;
            }

            public static bool CheckDate(string date, bool showError)
            {
                bool flag = false;
                DateTime now = DateTime.Now;
                DateTime dateTime = DateTime.Parse(date);
                if (dateTime.Subtract(now).Days < 0)
                    return flag;
                string path = "";
                Aveva.Core.Utilities.CommandLine.Command command = Aveva.Core.Utilities.CommandLine.Command.CreateCommand($"var !!PATH evar '{Project.CurrentProject.Name}000'");
                if (command.RunInPdms())
                    path = command.GetPMLVariableString("PATH");
                foreach (FileInfo file in new DirectoryInfo(path).GetFiles())
                {
                    if (dateTime.Subtract(file.LastWriteTime).Days < 0)
                        return flag;
                }
                return true;
            }

            public static string ConfigFile(string folderName, string fileName)
            {
                string projectDefaultFolder = usefulTool.GetProjectDefaultFolder();
                fileName = "\\" + fileName;
                if (folderName != "")
                    folderName = "\\" + folderName;
                string fileName1 = projectDefaultFolder + folderName + fileName;
                string str1 = fileName1;
                if (!new FileInfo(fileName1).Exists)
                {
                    fileName1 = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) + folderName + fileName;
                    string str2 = fileName1;
                    if (!new FileInfo(fileName1).Exists)
                    {
                        int num = (int)MessageBox.Show($"{str1}\n{str2}", "配置文件不存在", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                        return "";
                    }
                }
                return fileName1;
            }

            public static string GetPDMSUserFolder()
            {
                string pdmsUserFolder = "";
                string name = Project.CurrentProject.Name;
                Aveva.Core.Utilities.CommandLine.Command command = Aveva.Core.Utilities.CommandLine.Command.CreateCommand("var !!PDMSUSER evar pdmsuser");
                if (command.RunInPdms())
                    pdmsUserFolder = command.GetPMLVariableString("PDMSUSER");
                return pdmsUserFolder;
            }

            public static bool DistanceIsMetric(double value)
            {
                return usefulTool.GetPMLString("unit").IndexOf("INCH Distance") < 0;
            }

            public static string GetPMLString(string pmlString)
            {
                string pmlString1 = "";
                Aveva.Core.Utilities.CommandLine.Command command = Aveva.Core.Utilities.CommandLine.Command.CreateCommand("var !!FYSTRING " + pmlString);
                if (command.RunInPdms())
                    pmlString1 = command.GetPMLVariableString("FYSTRING");
                return pmlString1;
            }

            public static string GetPMLVariableString(string pmlString)
            {
                string pmlVariableString = "";
                Aveva.Core.Utilities.CommandLine.Command command = Aveva.Core.Utilities.CommandLine.Command.CreateCommand("var !!FYSTRING " + pmlString);
                if (command.RunInPdms())
                    pmlVariableString = command.GetPMLVariableString("FYSTRING");
                return pmlVariableString;
            }

            public static double GetPMLVariableReal(string pmlString)
            {
                double pmlVariableReal = 0.0;
                Aveva.Core.Utilities.CommandLine.Command command = Aveva.Core.Utilities.CommandLine.Command.CreateCommand("var !!FYREAL " + pmlString);
                if (command.RunInPdms())
                    pmlVariableReal = command.GetPMLVariableReal("FYREAL");
                return pmlVariableReal;
            }

            public static bool GetPMLVariableBoolean(string pmlString)
            {
                bool pmlVariableBoolean = false;
                Aveva.Core.Utilities.CommandLine.Command command = Aveva.Core.Utilities.CommandLine.Command.CreateCommand("var !!FYBOOL " + pmlString);
                if (command.RunInPdms())
                    pmlVariableBoolean = command.GetPMLVariableBoolean("FYBOOL");
                return pmlVariableBoolean;
            }

            public static List<string> GetPMLStringArray(string pmlString)
            {
                List<string> pmlStringArray = new List<string>();
                if (Aveva.Core.Utilities.CommandLine.Command.CreateCommand("var !!FYSTRINGARRAY " + pmlString).RunInPdms())
                {
                    Aveva.Core.Utilities.CommandLine.Command command1 = Aveva.Core.Utilities.CommandLine.Command.CreateCommand("!!SIZE = !!FYSTRINGARRAY.size()");
                    if (command1.RunInPdms())
                    {
                        double pmlVariableReal = command1.GetPMLVariableReal("SIZE");
                        if (pmlVariableReal != 0.0)
                        {
                            for (int index = 1; (double)index <= pmlVariableReal; ++index)
                            {
                                Aveva.Core.Utilities.CommandLine.Command command2 = Aveva.Core.Utilities.CommandLine.Command.CreateCommand($"var !!VALUE !!FYSTRINGARRAY[{index.ToString()}]");
                                if (command2.RunInPdms())
                                    pmlStringArray.Add(command2.GetPMLVariableString("VALUE"));
                            }
                        }
                    }
                }
                return pmlStringArray;
            }

            public static List<string> CollectElementsWithin(string type, double[] volume)
            {
                string str1 = $"e {volume[0].ToString()} n {volume[1].ToString()} u {volume[2].ToString()}";
                string str2 = $" to e {volume[3].ToString()} n {volume[4].ToString()} u {volume[5].ToString()}";
                return usefulTool.GetPMLStringArray($"coll all {type} within {str1}{str2}");
            }

            public static List<string> CollectElementsWithin(
              string type,
              double[] volume,
              string pmlCondition)
            {
                string str1 = $"e {volume[0].ToString()} n {volume[1].ToString()} u {volume[2].ToString()}";
                string str2 = $" to e {volume[3].ToString()} n {volume[4].ToString()} u {volume[5].ToString()}";
                List<string> pmlStringArray;
                if (pmlCondition.Trim() == "")
                    pmlStringArray = usefulTool.GetPMLStringArray($"coll all {type} within {str1}{str2}");
                else
                    pmlStringArray = usefulTool.GetPMLStringArray($"coll all {type} with ({pmlCondition}) within {str1}{str2}");
                return pmlStringArray;
            }

            public static List<string> CollectElementsWithin(string type, LimitsBox limits)
            {
                string str1 = $"e {limits.Minimum.X.ToString()} n {limits.Minimum.Y.ToString()} u {limits.Minimum.Z.ToString()}";
                string str2 = $" to e {limits.Maximum.X.ToString()} n {limits.Maximum.Y.ToString()} u {limits.Maximum.Z.ToString()}";
                return usefulTool.GetPMLStringArray($"coll all {type} within {str1}{str2}");
            }

            public static List<string> CollectElementsFor(string type, DbElement hierarchy)
            {
                return usefulTool.GetPMLStringArray($"coll all {type} for {hierarchy.GetAsString(DbAttributeInstance.NAME)}");
            }

            public static List<string> CollectElementsWithin(
              string type,
              double[] volume,
              string pmlCondition,
              double extend,
              string hierarchy)
            {
                if (hierarchy != "")
                    hierarchy = " for " + hierarchy;
                List<string> stringList = new List<string>();
                if (volume.Length < 6)
                    return stringList;
                string[] strArray1 = new string[6];
                strArray1[0] = "e ";
                double num = volume[0] - extend;
                strArray1[1] = num.ToString();
                strArray1[2] = " n ";
                num = volume[1] - extend;
                strArray1[3] = num.ToString();
                strArray1[4] = " u ";
                num = volume[2] - extend;
                strArray1[5] = num.ToString();
                string str1 = string.Concat(strArray1);
                string[] strArray2 = new string[6];
                strArray2[0] = " to e ";
                num = volume[3] + extend;
                strArray2[1] = num.ToString();
                strArray2[2] = " n ";
                num = volume[4] + extend;
                strArray2[3] = num.ToString();
                strArray2[4] = " u ";
                num = volume[5] + extend;
                strArray2[5] = num.ToString();
                string str2 = string.Concat(strArray2);
                List<string> pmlStringArray;
                if (pmlCondition.Trim() == "")
                    pmlStringArray = usefulTool.GetPMLStringArray($"coll all {type} within {str1}{str2}{hierarchy}");
                else
                    pmlStringArray = usefulTool.GetPMLStringArray($"coll all {type} with ({pmlCondition}) within {str1}{str2}{hierarchy}");
                return pmlStringArray;
            }

            public static List<string> CollectElementsWithin(string type, DbElement element)
            {
                double[] doubleArray = element.GetDoubleArray(DbAttributeInstance.WVOL);
                return usefulTool.CollectElementsWithin(type, doubleArray);
            }

            public static List<string> CollectElementsWithin(
              string type,
              DbElement element,
              string pmlCondition,
              double extend,
              string hierarchy)
            {
                double[] doubleArray = element.GetDoubleArray(DbAttributeInstance.WVOL);
                return usefulTool.CollectElementsWithin(type, doubleArray, pmlCondition, extend, hierarchy);
            }

            public static List<string> CollectElementsWithin(
              string type,
              string pmlCondition,
              double[] volume)
            {
                List<string> stringList = new List<string>();
                string str1 = $"e {volume[0].ToString()} n {volume[1].ToString()} u {volume[2].ToString()}";
                string str2 = $" to e {volume[3].ToString()} n {volume[4].ToString()} u {volume[5].ToString()}";
                List<string> pmlStringArray;
                if (pmlCondition.Trim() == "")
                    pmlStringArray = usefulTool.GetPMLStringArray($"coll all {type} within {str1}{str2}");
                else
                    pmlStringArray = usefulTool.GetPMLStringArray($"coll all {type} with ({pmlCondition}) within {str1}{str2}");
                return pmlStringArray;
            }

            public static List<string> CollectElementsWithin(
              string type,
              List<DbElement> elements,
              string pmlCondition,
              double extend,
              string hierarchy)
            {
                List<string> stringList1 = new List<string>();
                List<string> stringList2 = new List<string>();
                DbElement.GetElement();
                double[] numArray = new double[6];
                foreach (DbElement element1 in elements)
                {
                    double[] doubleArray = element1.GetDoubleArray(DbAttributeInstance.WVOL);
                    foreach (string name in usefulTool.CollectElementsWithin(type, doubleArray, pmlCondition, extend, hierarchy))
                    {
                        DbElement element2 = DbElement.GetElement(name);
                        if (!(element2 == element1) && !(element2.Owner == element1) && !(element2.Owner.Owner == element1) && !(element2.Owner.Owner.Owner == element1) && !stringList1.Contains(name))
                            stringList1.Add(name);
                    }
                }
                return stringList1;
            }

            public static List<DbElement> CollectStringToElement(List<string> names)
            {
                List<DbElement> element1 = new List<DbElement>();
                DbElement.GetElement();
                foreach (string name in names)
                {
                    DbElement element2 = DbElement.GetElement(name);
                    if (!element2.IsNull && !element2.IsDeleted && !element1.Contains(element2))
                        element1.Add(element2);
                }
                return element1;
            }

            public static DataTable HierarchyIsCreatable(
              Aveva.Core.Database.DbType dbtype,
              DbElementType ownerType,
              DbElementType type)
            {
                DataTable dataTable = new DataTable();
                dataTable.Columns.Add("Name");
                string userName = Project.CurrentProject.UserName;
                Aveva.Core.Database.Filters.TypeFilter filt = new Aveva.Core.Database.Filters.TypeFilter(ownerType);
                foreach (DbElement mdbMember in usefulTool.MDBMembers(dbtype))
                {
                    foreach (DbElement element in new DBElementCollection(mdbMember, (BaseFilter)filt))
                    {
                        if (usefulTool.IsCreatable(element, type, false))
                        {
                            DataRow row = dataTable.NewRow();
                            row[0] = (object)element.GetAsString(DbAttributeInstance.NAME);
                            dataTable.Rows.Add(row);
                        }
                    }
                }
                return dataTable;
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

            public static Direction GetDirectionWrtWorld(DbElement element, DbAttribute att)
            {
                return element.GetDirection(att, new DbQualifier()
                {
                    wrtQualifier = DbElement.GetElement("/*")
                });
            }

            public static Direction GetDirectionWrtWorld(DbElement element, DbAttribute att, int point)
            {
                Direction direction = Direction.Create();
                try
                {
                    direction = element.GetDirection(att, point, DbElement.GetElement("/*"));
                }
                catch
                {
                }
                return direction;
            }

            public static Aveva.Core.Geometry.Orientation GetOrientationWrtWorld(DbElement element)
            {
                return element.GetOrientation(DbAttributeInstance.WORI);
            }

            public static DbElement GetElementFromRef(string refName)
            {
                int num = refName.IndexOf("/");
                return DbElement.GetElement(new int[2]
                {
      Convert.ToInt32(refName.Substring(1, num - 1)),
      Convert.ToInt32(refName.Substring(num + 1))
                });
            }

            public static DbElement GetElement(DbElement element, DbAttribute att)
            {
                DbElement element1 = DbElement.GetElement();
                if (att != (DbAttribute)null && att.Type == DbAttributeType.ELEMENT && element.IsAttributeValid(att) && element.GetAsString(att).ToUpper() != "UNSET" && element.GetAsString(att).ToUpper() != "NULREF" && element.GetAsString(att).IndexOf("Unknown") < 0)
                    element1 = element.GetElement(att);
                return element1;
            }

            public static DbElement[] GetElementArray(DbElement element, DbAttribute att)
            {
                DbElement[] elementArray = new DbElement[0];
                if (att != (DbAttribute)null && att.IsArray && att.Type == DbAttributeType.ELEMENT && element.IsAttributeValid(att))
                    elementArray = element.GetElementArray(att);
                return elementArray;
            }

            public static DbElement CopyAndRename(DbElement oldElement, DbElement hierarchy, string newName)
            {
                DbElement.GetElement();
                if (newName.Trim() != "" && !DbElement.GetElement(newName).IsNull)
                {
                    int num = (int)MessageBox.Show(newName + "名称已经存在", "拷贝失败", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                    return DbElement.GetElement();
                }
                DbElement hierarchyAfterLast = oldElement.CreateCopyHierarchyAfterLast(hierarchy, new DbCopyOption()
                {
                    Rename = true,
                    FromName = oldElement.GetAsString(DbAttributeInstance.NAME),
                    ToName = newName
                });
                if (newName.Trim() != "")
                    hierarchyAfterLast.SetAttribute(DbAttributeInstance.NAME, newName);
                return hierarchyAfterLast;
            }

            public static double AngleEast(double x, double y)
            {
                double num = 0.0;
                if (x > 0.0 && y == 0.0)
                    num = 0.0;
                else if (x > 0.0 && y > 0.0)
                    num = Math.Atan(y / x);
                else if (x == 0.0 && y > 0.0)
                    num = Math.PI / 2.0;
                else if (x < 0.0 && y > 0.0)
                    num = Math.Atan(-x / y) + Math.PI / 2.0;
                else if (x < 0.0 && y == 0.0)
                    num = Math.PI;
                else if (x < 0.0 && y < 0.0)
                    num = Math.Atan(-y / -x) + Math.PI;
                else if (x == 0.0 && y < 0.0)
                    num = 3.0 * Math.PI / 2.0;
                if (x > 0.0 && y < 0.0)
                    num = Math.Atan(x / -y) + 3.0 * Math.PI / 2.0;
                return Math.Round(num * 180.0 / Math.PI, 0);
            }

            public static bool IsCreatable(DbElement element, DbElementType type, bool showMessage)
            {
                if (element.IsNull)
                    return false;
                string asString1 = element.GetAsString(DbAttributeInstance.CLMID);
                string asString2 = element.GetAsString(DbAttributeInstance.USERC);
                bool flag1 = element.GetBool(DbAttributeInstance.LCLM);
                bool flag2 = element.GetBool(DbAttributeInstance.LOCK);
                if (element.IsCreatable(type))
                    return true;
                string text = $"当前层次不能创建{type.ToString()}\n";
                if (flag2)
                    text = element.GetAsString(DbAttributeInstance.NAME) + " Locked";
                if (!flag1)
                    text = $"{text}声明 ID={asString1} User={asString2}";
                if (showMessage)
                {
                    int num = (int)MessageBox.Show(text, $"{element.GetElementType().ToString()}层次无法创建{type.ToString()}", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                }
                return false;
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

            public static bool IsAttributeSetable(DbElement element, DbAttribute att, bool showMessage)
            {
                string asString1 = element.GetAsString(DbAttributeInstance.CLMID);
                string asString2 = element.GetAsString(DbAttributeInstance.USERC);
                bool flag1 = element.GetBool(DbAttributeInstance.LCLM);
                bool flag2 = element.GetBool(DbAttributeInstance.LOCK);
                string text = "";
                if (flag2)
                    text = element.GetAsString(DbAttributeInstance.NAME) + " Locked";
                if (!flag1 && !(asString1.ToUpper() == "UNSET"))
                    text = $"{text}声明 ID={asString1} User={asString2}";
                if (text == "")
                {
                    if (element.IsAttributeSetable(att))
                        return true;
                    int num = (int)MessageBox.Show(att.ToString(), "当前元素属性无法赋值", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                    return false;
                }
                if (showMessage)
                {
                    int num1 = (int)MessageBox.Show(text, "当前元素属性无法赋值", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                }
                return false;
            }

            public static List<DbElement> MDBMembers(Aveva.Core.Database.DbType type)
            {
                List<DbElement> dbElementList = new List<DbElement>();
                foreach (Db db in MDB.CurrentMDB.GetDBArray(type))
                {
                    foreach (DbElement worldMember in db.WorldMembers())
                        dbElementList.Add(worldMember);
                }
                return dbElementList;
            }

            public static string EvaluateExpression(DbElement element, string str, bool showError)
            {
                int startIndex = str.IndexOf("[");
                for (; startIndex >= 0; startIndex = str.IndexOf("["))
                {
                    int num1 = str.IndexOf("]");
                    string oldValue = str.Substring(startIndex, num1 - startIndex + 1);
                    string name = str.Substring(startIndex + 1, num1 - startIndex - 1);
                    if (name.IndexOf("\\") >= 0)
                        name = name.Replace("\\", "");
                    DbExpression expression = DbExpression.Parse(name);
                    string newValue;
                    try
                    {
                        newValue = element.EvaluateString(expression);
                    }
                    catch
                    {
                        if (showError)
                        {
                            int num2 = (int)MessageBox.Show($"{str}中的{name}不是PML语句", "PML表达式错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                        }
                        newValue = "";
                    }
                    str = str.Replace(oldValue, newValue);
                }
                return str;
            }

            public static DbElement EvaluateElement(DbElement element, string expression, bool showError)
            {
                DbElement element1 = DbElement.GetElement();
                if (expression.ToUpper() == "PRE")
                    return element.Previous;
                if (expression.ToUpper() == "NEXT")
                    return element.Next();
                DbExpression expression1 = DbExpression.Parse(expression);
                try
                {
                    element1 = element.EvaluateElement(expression1);
                }
                catch
                {
                    if (showError)
                    {
                        int num = (int)MessageBox.Show("DbElement表达式" + expression, "PML表达式取值错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                    }
                }
                return element1;
            }

            public static Direction EvaluateDirection(DbElement element, string dirString, bool showError)
            {
                Direction direction = Direction.Create();
                dirString = dirString.Trim();
                int num1 = dirString.IndexOf("OF");
                int num2 = dirString.IndexOf("P");
                int num3 = dirString.IndexOf(" ");
                if (num2 == 0)
                {
                    int qualifier = num3 >= 0 ? int.Parse(dirString.Substring(num2 + 1, num3 - 1)) : int.Parse(dirString.Substring(num2 + 1));
                    if (num1 >= 0)
                    {
                        DbElement element1 = usefulTool.EvaluateElement(element, dirString.Substring(num1 + 3), true);
                        if (element1.IsNull)
                            return direction;
                        direction = element1.GetDirection(DbAttributeInstance.PDIR, qualifier);
                    }
                    else
                        direction = element.GetDirection(DbAttributeInstance.PDIR, qualifier);
                }
                else
                {
                    DbExpression expression = DbExpression.Parse(dirString);
                    try
                    {
                        direction = element.EvaluateDirection(expression);
                    }
                    catch
                    {
                        if (showError)
                        {
                            int num4 = (int)MessageBox.Show(dirString, "PML表达式错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                        }
                    }
                }
                return direction;
            }

            public static double EvaluateDoubleDistance(DbElement element, string expression, bool showError)
            {
                double doubleDistance = 0.0;
                DbExpression expression1 = DbExpression.Parse(expression);
                try
                {
                    doubleDistance = element.EvaluateDouble(expression1, DbAttributeUnit.DIST);
                }
                catch
                {
                    if (showError)
                    {
                        int num = (int)MessageBox.Show(expression, "PML表达式错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                    }
                }
                return doubleDistance;
            }

            public static double EvaluateDoubleBore(DbElement element, string expression, bool showError)
            {
                double doubleBore = 0.0;
                DbExpression expression1 = DbExpression.Parse(expression);
                try
                {
                    doubleBore = element.EvaluateDouble(expression1, DbAttributeUnit.BORE);
                }
                catch
                {
                    if (showError)
                    {
                        int num = (int)MessageBox.Show(expression, "PML表达式错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                    }
                }
                return doubleBore;
            }

            public static bool EvaluateBool(DbElement element, string expression, bool showError)
            {
                if (expression.Trim() == "")
                    return true;
                bool flag = false;
                DbExpression expression1 = DbExpression.Parse(expression);
                try
                {
                    flag = element.EvaluateBool(expression1);
                }
                catch
                {
                    if (showError)
                    {
                        int num = (int)MessageBox.Show(expression, "PML逻辑表达式错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                    }
                }
                return flag;
            }

            public static LimitsBox VolumeToLimitBox(DbElement element)
            {
                double[] doubleArray = element.GetDoubleArray(DbAttributeInstance.WVOL);
                return LimitsBox.Create(Position.Create(doubleArray[0], doubleArray[1], doubleArray[2]), Position.Create(doubleArray[3], doubleArray[4], doubleArray[5]));
            }

            public static double[] LocalVolume(DbElement element)
            {
                return element.GetDoubleArray(DbAttributeInstance.LVOL, usefulTool.WrtElement(element));
            }

            public static DbQualifier WrtWorld()
            {
                return new DbQualifier()
                {
                    wrtQualifier = DbElement.GetElement("/*")
                };
            }

            public static DbQualifier WrtElement(DbElement element)
            {
                return new DbQualifier() { wrtQualifier = element };
            }
        }

    }
}