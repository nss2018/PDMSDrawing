# dump_dialog.ps1 - dump child controls of a dialog HWND (read-only)
# usage: powershell -File mds_dump_dialog.ps1 <HWND decimal>
param([long]$Hwnd)
Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Runtime.InteropServices;
using System.Collections.Generic;
public static class DlgDump {
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr p);
  public delegate bool EnumWindowsProc(IntPtr h, IntPtr p);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern int SendMessage(IntPtr h, int msg, int wParam, IntPtr lParam);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int SendMessageW(IntPtr h, uint msg, IntPtr wParam, StringBuilder lParam);
  public static List<string> Results = new List<string>();
  public static bool Cb(IntPtr h, IntPtr p) {
    var t = new StringBuilder(1024); GetWindowText(h, t, 1024);
    var c = new StringBuilder(64); GetClassName(h, c, 64);
    string extra = "";
    if (c.ToString().Contains("RichEdit")) {
      int len = SendMessage(h, 0x000E, 0, IntPtr.Zero);
      if (len > 0) {
        var sb = new StringBuilder(len + 1);
        if (SendMessageW(h, 0x000D, (IntPtr)(len + 1), sb) > 0) extra = " RICHTEXT=[" + sb.ToString() + "]";
      }
    }
    Results.Add(string.Format("HWND={0} CLASS=[{1}] TEXT=[{2}]{3}", h.ToInt64(), c.ToString(), t.ToString(), extra));
    return true;
  }
}
"@
$cb = [DlgDump+EnumWindowsProc]{ param($h,$p) [DlgDump]::Cb($h,$p) }
[void][DlgDump]::EnumChildWindows([IntPtr]$Hwnd, $cb, [IntPtr]::Zero)
[DlgDump]::Results
