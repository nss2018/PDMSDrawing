using Aveva.ApplicationFramework;
using Aveva.ApplicationFramework.Presentation;
using System;
using System.Windows.Forms;

namespace drawingPDMS
{
    // PDMS 命令/停靠窗口（与 E3D 的 drawCmd.cs 结构一致）。
    // 参考：D:\NSS\source\repos\jackPipe\jackPipe\jackCmd.cs
    public class drawCmd : Command
    {
        private DockedWindow mForm;

        public override bool Checked
        {
            get { return mForm.Visible; }
            set { base.Checked = value; }
        }

        public drawCmd(WindowManager wndManager)
        {
            try
            {
                base.Key = "drawingPDMS";
                mForm = wndManager.CreateDockedWindow(
                    "drawingPDMS.drawE3D",
                    "Draw(PDMS)",
                    new drawE3D(),
                    DockedPosition.Right);
                mForm.SaveLayout = false;
                wndManager.WindowLayoutLoaded += new EventHandler(OnWindowLayoutLoaded);
                mForm.Closed += new EventHandler(OnFormClosed);
            }
            catch (Exception e)
            {
                MessageBox.Show("错误信息：" + e);
            }
        }
        void OnWindowLayoutLoaded(object sender, EventArgs e)
        {
            this.Checked = false;
        }
        void OnFormClosed(object sender, EventArgs e)
        {
            this.Checked = false;
        }
        public override void Execute()
        {
            if (mForm.Visible)
                mForm.Hide();
            else
            {
                mForm.Show();
                mForm.Width = 350;
            }
            base.Execute();
        }
    }
}
