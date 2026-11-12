// PdmsBridgeAddin.cs — 让 PdmsBridge 成为可独立部署的 PDMS Addin（自带 IAddin 入口）
//
// 用法（档位2 · 需编译）：
//   1) 用 VS / MSBuild 打开同目录 PdmsBridge.csproj（.NET Framework 3.5）。
//   2) 工程已引用 PDMS 安装目录下的：
//        Aveva.ApplicationFramework.dll   ← IAddin 接口就在这里（不是 Aveva.Pdms.Addins.dll）
//        Aveva.Pdms.Database.dll / Aveva.Pdms.Utilities.dll
//      HintPath 默认按 C:\AVEVA\Plant\PDMS12.1.SP4\ 填好，换机请改。
//   3) 编译出 PdmsBridge.dll。
//   4) 注册（PDMS 12.1 用 *Addins.xml 清单，不是 .addin 文件）：
//        把  <string>PdmsBridge</string>  追加进 PDMS 安装目录的
//        DesignAddins.xml / DraftAddins.xml / AdminAddins.xml / IsodraftAddins.xml
//        （每个都是 <ArrayOfString>，直接加一行 <string>）。
//   5) 把 PdmsBridge.dll 放到 PDMS 安装目录根（与你现有 FangYuanPDMS.dll 同级）。
//   6) 启动 PDMS，监听器在 127.0.0.1:18753 自动起（日志见 <桥目录>\bridge.log）。
//
// 说明：监听器本体在【同目录】PdmsBridge.cs（namespace PdmsBridge / class PdmsBridge），
//       本文件只负责在 Addin 加载/卸载时启停它，无需改动你现有的 FangYuanPDMS 工程。

using System;
using System.IO;                    // Directory/File（构造痕迹日志用）
using Aveva.ApplicationFramework;   // IAddin

namespace PdmsBridge
{
    /// <summary>
    /// PDMS 12.1 Addin 入口：实现 Aveva.ApplicationFramework.IAddin。
    /// 接口真实签名（从 AVEVA dll 字符串表验证）：
    ///   interface IAddinInterfaceObject { string Name; string Description; bool IsStarted; object Addin; }
    /// 无 Start()/Stop() 方法。监听器在构造函数中启动。
    /// </summary>
    public class PdmsBridgeAddin : IAddin
    {
        public string Name { get; set; }
        public string Description { get; set; }

        public PdmsBridgeAddin()
        {
            // Name 要与 *Addins.xml 里注册的字符串一致
            Name = "PdmsBridge";
            Description = "AI agent bridge: local TCP listener, executes PML/commands inside PDMS.";
            // 加载痕迹（2026-10-19）：构造先于 Start。
            // 有此行无 Start() entered → 类型/Start 层出错；两行都无 → 程序集根本没加载（DLL 缺失）。
            try
            {
                string td = @"C:\pdmsAgent\bridge";
                if (!Directory.Exists(td)) Directory.CreateDirectory(td);
                File.AppendAllText(Path.Combine(td, "bridge.log"),
                    DateTime.Now + " PdmsBridgeAddin constructed\n");
            }
            catch { }
        }

        public void Start(ServiceManager serviceManager)
        {
            // 用 global:: 消除"命名空间 PdmsBridge 与类 PdmsBridge 同名"的解析歧义
            global::PdmsBridge.PdmsBridge.Start();
        }

        public void Stop()
        {
            // 2026-10-19 事故②：AVEVA 会在会话中途调用 IAddin.Stop()（-modswitch 模块
            // 重初始化等），之后并不保证再调 Start。旧实现会静默杀掉监听器（无日志），
            // 表现为"桥在线过一阵，之后又连不上"（当日 15:03 复现：18753/18754 双双失踪）。
            // 现改为 no-op：监听器活到进程退出——PDMS 关闭时随进程一起消失，
            // 127.0.0.1 本地监听无泄漏风险。
            global::PdmsBridge.PdmsBridge.Note(
                "IAddin.Stop() received - listener kept alive (no-op since 2026-10-19)");
        }
    }
}
