using Aveva.ApplicationFramework;
using Aveva.ApplicationFramework.Presentation;
using System;

namespace drawingPDMS
{
    // PDMS 插件入口（与 E3D 的 drawAddin.cs 结构一致，仅命名空间/名称不同）。
    // 参考：D:\NSS\source\repos\jackPipe\jackPipe\jackAddin.cs
    class drawAddin : IAddin
    {
        #region IAddin Members
        public string Description
        {
            get { return "drawingPDMS Addin"; }
        }
        public string Name
        {
            get { return "drawingPDMS"; }
        }
        public void Start(ServiceManager serviceManager)
        {
            #if PDMS_BRIDGE
            // 2026-10-16 启动自愈：在画 Explorer 树之前清理已知损坏元素，根治
            // 「UltraTree 画树 NRE → CLR FastFail → 每次登录都 fatal」。
            // 全程 try/catch + 多重签名校验，绝不影响 PDMS 正常启动；日志 op_boot_selfheal.log。
            // 详见 headlessDrawing.BootSelfHeal。
            try { drawingPDMS.HeadlessDrawing.BootSelfHeal(null); }
            catch (Exception) { /* 自愈失败不阻断启动（内部已记日志） */ }
            // 智能体桥接：插件加载时启动本地 TCP 监听器(127.0.0.1:18753)，
            // 供 MCP 连接器(pdms-bridge-mcp)下发 PML/命令。
            // 详见 D:\pdmsAgent\bridge\README.md（日志写 bridge\bridge.log）。
            drawingPDMS.PdmsBridge.Start();
            #endif

            CommandManager sCommandManager = (CommandManager)serviceManager.GetService(typeof(CommandManager));
            WindowManager sWindowManager = (WindowManager)serviceManager.GetService(typeof(WindowManager));
            drawCmd newForm = new drawCmd(sWindowManager);
            sCommandManager.Commands.Add(newForm);
        }
        public void Stop()
        {
        }
        #endregion
    }
}
