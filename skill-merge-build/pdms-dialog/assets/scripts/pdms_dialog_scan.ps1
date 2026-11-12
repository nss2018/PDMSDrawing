# scan_dialogs.ps1 - list top-level windows of given des.exe PIDs (read-only)
# usage: powershell -File scan_dialogs.ps1 20888,26148
param([string]$PidList)
Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Runtime.InteropServices;
using System.Collections.Generic;
public static class DlgScan {
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr p);
  public delegate bool EnumWindowsProc(IntPtr h, IntPtr p);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  public static List<string> Results = new List<string>();
  public static uint[] Pids;
  public static bool Cb(IntPtr h, IntPtr p) {
    uint pid = 0;
    GetWindowThreadProcessId(h, out pid);
    bool mine = false;
    foreach (uint q in Pids) if (q == pid) { mine = true; break; }
    if (mine) {
      var c = new StringBuilder(128); GetClassName(h, c, 128);
      var t = new StringBuilder(512); GetWindowText(h, t, 512);
      Results.Add(string.Format("PID={0} HWND={1} CLASS=[{2}] VIS={3} TITLE=[{4}]",
        pid, h.ToInt64(), c.ToString(), IsWindowVisible(h), t.ToString()));
    }
    return true;
  }
}
"@
$ids = $PidList -split ','
$u32 = [uint32[]](@($ids | ForEach-Object { [uint32]($_.Trim()) }))
[DlgScan]::Pids = $u32
$cb = [DlgScan+EnumWindowsProc]{ param($h,$p) [DlgScan]::Cb($h,$p) }
[void][DlgScan]::EnumWindows($cb, [IntPtr]::Zero)
[DlgScan]::Results
