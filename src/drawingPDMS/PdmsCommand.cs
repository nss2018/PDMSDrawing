// PdmsCommand.cs —— 命令执行通道 + 离线可测的执行器
//
// 设计要点（对齐评估报告 §8「只用自有代码重写、不依赖方圆 DLL」）：
//   * 所有“真正下发给 PDMS”的动作都收口在 IPdmsCommandRunner 之后。
//   * LiveRunner   —— 进程内 Command.CreateCommand(...).RunInPdms()，仅在 PDMS 会话内可用。
//   * CaptureRunner —— 只记录命令串、按预设回读 PML 变量值，不依赖 AVEVA 运行时外的任何东西；
//                     供单元测试断言“生成的 CP/PML 命令是否正确”（无需 PDMS 即可编译运行）。
//   * 各业务类把“命令构造”拆成 internal 纯函数（BuildXxxCommand），测试直接断言这些串。
//
// 注意：PML 变量名约定 ≤4 字符（方圆 Utilities 内可见该限制），本工程统一用单字符占位名 X/T/V。

using System;
using System.Collections.Generic;
using Aveva.Pdms.Database;
using Aveva.Pdms.Utilities.CommandLine;

namespace Radwing.Piping
{
    /// <summary>PDMS 命令执行通道抽象。</summary>
    public interface IPdmsCommandRunner
    {
        /// <summary>执行一条 CP/PML 命令；失败时在 error 带回 PDMS 报错文本。</summary>
        bool Run(string command, out string error);

        /// <summary>等效于 `var !!X &lt;expr&gt;` 后回读 !!X（字符串）。</summary>
        string GetPMLString(string expr);

        string GetPMLVariableString(string name);
        double GetPMLVariableReal(string name);
        bool GetPMLVariableBoolean(string name);
        string[] GetPMLVariableStringArray(string name);

        /// <summary>new 类命令执行成功后回读当前元素（!!ce.name）。离线执行器返回 null。</summary>
        DbElement GetCreatedElement();

        /// <summary>是否支持 AVEVA DB API 直连（CreateFirst/SetAttribute 等）。
        /// LiveRunner=true（PDMS 会话内），CaptureRunner=false（离线测试只能走 CP 命令路径）。</summary>
        bool SupportsDbApi { get; }
    }

    /// <summary>真实执行器：走 PDMS 进程内 Command。</summary>
    public sealed class LiveRunner : IPdmsCommandRunner
    {
        public bool SupportsDbApi { get { return true; } }

        public bool Run(string command, out string error)
        {
            error = null;
            // 多行命令逐行下发：Command 对含 \n 的整串支持不稳定，拆分后按序执行，
            // 失败时报错带行号，便于定位是哪一句 CP 出错。
            string[] lines = command.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r').Trim();
                if (line.Length == 0) continue;
                Command cmd = Command.CreateCommand(line);
                if (!cmd.RunInPdms())
                {
                    string msg;
                    try { msg = (cmd.Error != null) ? cmd.Error.MessageText() : "RunInPdms failed"; }
                    catch { msg = "RunInPdms failed"; }
                    error = (lines.Length > 1 ? "第 " + (i + 1) + " 行: " : "") + msg + "  [" + line + "]";
                    return false;
                }
            }
            return true;
        }

        public string GetPMLString(string expr)
        {
            Command cmd = Command.CreateCommand("var !!X " + expr);
            if (!cmd.RunInPdms())
                throw new PdmsCommandException(cmd.Error != null ? cmd.Error.MessageText() : "RunInPdms failed");
            return cmd.GetPMLVariableString("X") ?? "";
        }

        public string GetPMLVariableString(string name)
        {
            Command cmd = Command.CreateCommand("var !!" + name + " !!" + name);
            if (!cmd.RunInPdms())
                throw new PdmsCommandException(cmd.Error != null ? cmd.Error.MessageText() : "RunInPdms failed");
            return cmd.GetPMLVariableString(name) ?? "";
        }

        public double GetPMLVariableReal(string name)
        {
            Command cmd = Command.CreateCommand("var !!" + name + " !!" + name);
            if (!cmd.RunInPdms())
                throw new PdmsCommandException(cmd.Error != null ? cmd.Error.MessageText() : "RunInPdms failed");
            return cmd.GetPMLVariableReal(name);
        }

        public bool GetPMLVariableBoolean(string name)
        {
            Command cmd = Command.CreateCommand("var !!" + name + " !!" + name);
            if (!cmd.RunInPdms())
                throw new PdmsCommandException(cmd.Error != null ? cmd.Error.MessageText() : "RunInPdms failed");
            return cmd.GetPMLVariableBoolean(name);
        }

        public string[] GetPMLVariableStringArray(string name)
        {
            // AVEVA Command 无 GetPMLVariableStringArray 方法：
            // 用 PML do 循环把数组逐元素拼成 '|' 分隔字符串后回读再切分。
            Command cmd = Command.CreateCommand(
                "var !!" + name + " !!" + name + "\n" +
                "!!zzjoin = ''\n" +
                "!!zzn = !!" + name + ".size()\n" +
                "if (!!zzn gt 0) then\n" +
                "do !zzi from 1 to !!zzn\n" +
                "!!zzjoin = !!zzjoin + (string(!!" + name + "[!zzi])) + '|'\n" +
                "enddo\n" +
                "endif");
            if (!cmd.RunInPdms())
                throw new PdmsCommandException(cmd.Error != null ? cmd.Error.MessageText() : "RunInPdms failed");
            string joined = cmd.GetPMLVariableString("zzjoin") ?? "";
            if (joined.Length == 0) return new string[0];
            string[] parts = joined.Split('|');
            List<string> list = new List<string>();
            foreach (string p in parts)
            {
                if (p.Length > 0) list.Add(p);
            }
            return list.ToArray();
        }

        /// <summary>new 类命令成功后，CE 已指向新建元素：回读 !!ce.name 取引用。</summary>
        public DbElement GetCreatedElement()
        {
            Command cmd = Command.CreateCommand("var !!X !!ce.name");
            if (!cmd.RunInPdms()) return null;
            string s = cmd.GetPMLVariableString("X");
            if (string.IsNullOrEmpty(s)) return null;
            return DbElement.GetElement(s);
        }
    }

    /// <summary>离线执行器：记录命令、按预设回读变量。单元测试用。</summary>
    public sealed class CaptureRunner : IPdmsCommandRunner
    {
        public bool SupportsDbApi { get { return false; } }

        private readonly List<string> _commands = new List<string>();
        private readonly Dictionary<string, string> _strVars = new Dictionary<string, string>();
        private readonly Dictionary<string, double> _realVars = new Dictionary<string, double>();
        private readonly Dictionary<string, bool> _boolVars = new Dictionary<string, bool>();
        private readonly Dictionary<string, string[]> _arrVars = new Dictionary<string, string[]>();
        private readonly Dictionary<string, string> _exprResponses = new Dictionary<string, string>();

        public IList<string> CapturedCommands { get { return _commands; } }

        public void SetVar(string name, string value) { _strVars[name] = value; }
        public void SetVar(string name, double value) { _realVars[name] = value; }
        public void SetVar(string name, bool value) { _boolVars[name] = value; }
        public void SetVar(string name, string[] value) { _arrVars[name] = value; }
        public void SetExpr(string expr, string value) { _exprResponses[expr] = value; }

        /// <summary>清空已捕获的命令（每个用例开始时调用）。</summary>
        public void Reset() { _commands.Clear(); }

        public bool Run(string command, out string error)
        {
            error = null;
            _commands.Add(command);
            return true;
        }

        public string GetPMLString(string expr)
        {
            // 记录命令，便于断言：应当生成 "var !!X <expr>"
            _commands.Add("var !!X " + expr);
            string v;
            return _exprResponses.TryGetValue(expr, out v) ? v : "";
        }

        public string GetPMLVariableString(string name)
        {
            string v;
            return _strVars.TryGetValue(name, out v) ? v : "";
        }

        public double GetPMLVariableReal(string name)
        {
            double v;
            return _realVars.TryGetValue(name, out v) ? v : 0.0;
        }

        public bool GetPMLVariableBoolean(string name)
        {
            bool v;
            return _boolVars.TryGetValue(name, out v) ? v : false;
        }

        public string[] GetPMLVariableStringArray(string name)
        {
            string[] v;
            return _arrVars.TryGetValue(name, out v) ? v : new string[0];
        }

        /// <summary>离线不回读真实元素（测试只断言命令串）。</summary>
        public DbElement GetCreatedElement() { return null; }
    }

    /// <summary>运行时异常：PDMS 命令执行失败。</summary>
    public sealed class PdmsCommandException : Exception
    {
        public PdmsCommandException(string message) : base(message) { }
    }

    /// <summary>全局执行通道门面。默认走 LiveRunner；测试时替换为 CaptureRunner。</summary>
    public static class Pdms
    {
        public static IPdmsCommandRunner Runner { get; set; }

        static Pdms()
        {
            Runner = new LiveRunner();
        }

        public static bool Run(string command, out string error)
        {
            return Runner.Run(command, out error);
        }

        public static string GetPMLString(string expr)
        {
            return Runner.GetPMLString(expr);
        }

        public static string GetPMLVariableString(string name)
        {
            return Runner.GetPMLVariableString(name);
        }

        public static double GetPMLVariableReal(string name)
        {
            return Runner.GetPMLVariableReal(name);
        }

        public static bool GetPMLVariableBoolean(string name)
        {
            return Runner.GetPMLVariableBoolean(name);
        }

        public static string[] GetPMLVariableStringArray(string name)
        {
            return Runner.GetPMLVariableStringArray(name);
        }
    }
}
