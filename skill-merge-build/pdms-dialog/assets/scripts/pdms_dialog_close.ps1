# close_dialog.ps1 - click OK/Yes button of a dialog HWND (popup-killer style BM_CLICK)
# ASCII ONLY. usage: powershell -File mds_close_dialog.ps1 <dialog HWND decimal>
param([long]$Hwnd)
Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class DlgClose {
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr p);
  public delegate bool EnumWindowsProc(IntPtr h, IntPtr p);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  public static IntPtr OkButton = IntPtr.Zero;
  public static string OkText = "";
  public static long HwndGlobal = 0;
  public static bool Cb(IntPtr h, IntPtr p) {
    var t = new StringBuilder(256); GetWindowText(h, t, 256);
    var c = new StringBuilder(64); GetClassName(h, c, 64);
    string s = t.ToString().Trim().ToUpper();
    if ((s == "OK" || s == "OKAY" || s == "YES" || s == "CLOSE") && c.ToString().Contains("BUTTON")) {
      OkButton = h; OkText = t.ToString();
    }
    return true;
  }
  public static bool ClickOk() {
    EnumChildWindows(new IntPtr(HwndGlobal), new EnumWindowsProc(Cb), IntPtr.Zero);
    if (OkButton != IntPtr.Zero) {
      PostMessage(OkButton, 0x00F5, (IntPtr)1, IntPtr.Zero);
      return true;
    }
    return false;
  }
}
"@
[DlgClose]::HwndGlobal = $Hwnd
if ([DlgClose]::ClickOk()) { "clicked OK (text=[{0}]) via BM_CLICK" -f [DlgClose]::OkText } else { "no OK/Yes button found" }
