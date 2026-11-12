#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""PDMS modal-dialog auto-closer (watchdog).

Background: headless bridge ops hang when PDMS pops a modal error box
(e.g. 'Current element does not have an attribute LTER') - the command
processor waits for OK and the whole pipeline stalls.

Strategy (mirrors the proven Accio approach):
  1. Poll top-level visible windows owned by PDMS processes.
  2. A candidate = window that has a child control whose text is an
     OK-ish label. Child search is NOT restricted to the Button class
     (PDMS OK buttons are custom controls).
  3. Close it: posted Enter first (no foreground impact); if the window
     survives, fall back to a real mouse click on the button rect; then
     WM_CLOSE as last resort.

Log: D:/pdmsAgent/bridge/dialog_watcher.log
Single instance via pidfile D:/pdmsAgent/bridge/dialog_watcher.pid
"""
import ctypes, ctypes.wintypes as wt, os, time, subprocess, sys

BRIDGE = r"D:\pdmsAgent\bridge"
LOG = os.path.join(BRIDGE, "dialog_watcher.log")
PIDFILE = os.path.join(BRIDGE, "dialog_watcher.pid")
POLL = 1.0
OK_LABELS = ("OK", "ok", "Ok", "confirm", "CONFIRM", "yes", "YES", "Yes")
# CJK labels checked via code points below to keep this file ASCII-safe
CJK_OK = ((0x786E, 0x5B9A), (0x786E, 0x8BA4))  # queding / queren

user32 = ctypes.windll.user32
kernel32 = ctypes.windll.kernel32

WM_GETTEXT = 0x000D
WM_GETTEXTLENGTH = 0x000E
WM_KEYDOWN = 0x0100
WM_KEYUP = 0x0101
WM_CLOSE = 0x0010
VK_RETURN = 0x0D
MOUSEEVENTF_LEFTDOWN = 0x0002
MOUSEEVENTF_LEFTUP = 0x0004
GWL_STYLE = -16
WS_POPUP = 0x80000000
WS_CAPTION = 0x00C00000
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000

ENUMPROC = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
ENUMCHILD = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)


def log(msg):
    try:
        with open(LOG, "a", encoding="utf-8") as f:
            f.write(time.strftime("%Y-%m-%d %H:%M:%S ") + msg + "\n")
    except Exception:
        pass


def window_text(h):
    n = user32.SendMessageW(h, WM_GETTEXTLENGTH, 0, 0)
    buf = ctypes.create_unicode_buffer(n + 2)
    user32.SendMessageW(h, WM_GETTEXT, n + 1, buf)
    return buf.value


def pid_exe(pid):
    h = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not h:
        return ""
    try:
        buf = ctypes.create_unicode_buffer(520)
        size = wt.DWORD(520)
        if kernel32.QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(size)):
            return os.path.basename(buf.value).lower()
        return ""
    finally:
        kernel32.CloseHandle(h)


def is_pdms_pid(pid):
    exe = pid_exe(pid)
    if not exe:
        return False
    return exe in ("design.exe", "dra.exe", "mon.exe", "pdms.exe") or "pdms" in exe


def find_ok_child(hdlg):
    """Return hwnd of an OK-labelled child, any control class."""
    hit = []

    def cb(hchild, lparam):
        t = window_text(hchild).strip()
        if t in OK_LABELS:
            hit.append(hchild)
            return False
        # CJK labels via code points (file kept ASCII)
        cps = [ord(c) for c in t]
        for pair in CJK_OK:
            if len(cps) == 2 and cps[0] == pair[0] and cps[1] == pair[1]:
                hit.append(hchild)
                return False
        return True

    user32.EnumChildWindows(hdlg, ENUMCHILD(cb), 0)
    return hit[0] if hit else None


def pdms_pids():
    pids = set()

    @ENUMPROC
    def cb(h, l):
        pid = wt.DWORD()
        user32.GetWindowThreadProcessId(h, ctypes.byref(pid))
        if pid.value and pid.value not in pids and is_pdms_pid(pid.value):
            pids.add(pid.value)
        return True

    user32.EnumWindows(cb, 0)
    return pids


def close_dialog(hdlg, hbtn):
    # 1) posted Enter to the dialog (default button) - no foreground impact
    user32.PostMessageW(hdlg, WM_KEYDOWN, VK_RETURN, 0)
    user32.PostMessageW(hdlg, WM_KEYUP, VK_RETURN, 0)
    time.sleep(1.5)
    if not user32.IsWindow(hdlg) or not user32.IsWindowVisible(hdlg):
        return "enter"
    # 2) real mouse click on the button rect (custom control safe)
    if hbtn:
        r = wt.RECT()
        if user32.GetWindowRect(hbtn, ctypes.byref(r)):
            x, y = (r.left + r.right) // 2, (r.top + r.bottom) // 2
            user32.SetCursorPos(x, y)
            user32.mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0)
            user32.mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0)
            time.sleep(1.5)
            if not user32.IsWindow(hdlg) or not user32.IsWindowVisible(hdlg):
                return "click"
    # 3) WM_CLOSE last resort
    user32.PostMessageW(hdlg, WM_CLOSE, 0, 0)
    time.sleep(1.0)
    return "wmclose"


def scan_once():
    closed = 0
    pids = pdms_pids()
    if not pids:
        return 0
    hits = []

    @ENUMPROC
    def cb(h, l):
        if not user32.IsWindowVisible(h):
            return True
        pid = wt.DWORD()
        user32.GetWindowThreadProcessId(h, ctypes.byref(pid))
        if pid.value not in pids:
            return True
        style = user32.GetWindowLongW(h, GWL_STYLE)
        if not (style & WS_POPUP):  # popups only, skip main frame windows
            return True
        t = window_text(h)
        if t in ("Program Manager", "Windows Input Experience"):
            return True
        hbtn = find_ok_child(h)
        if hbtn:
            hits.append((h, hbtn, t))
        return True

    user32.EnumWindows(cb, 0)
    for h, hbtn, t in hits:
        how = close_dialog(h, hbtn)
        closed += 1
        log("auto-closed dlg hwnd=%s title=%r via=%s" % (h, t[:80], how))
    return closed


def alive_pid(path):
    try:
        with open(path) as f:
            pid = int(f.read().strip())
    except Exception:
        return 0
    h = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not h:
        return 0
    kernel32.CloseHandle(h)
    return pid


def spawn_detached():
    pyw = sys.executable.replace("python.exe", "pythonw.exe")
    if not os.path.exists(pyw):
        pyw = sys.executable
    subprocess.Popen([pyw, os.path.abspath(__file__)],
                     creationflags=subprocess.DETACHED_PROCESS | subprocess.CREATE_NEW_PROCESS_GROUP,
                     close_fds=True, cwd=BRIDGE)


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "--ensure":
        if alive_pid(PIDFILE):
            print("watcher alive")
            return
        spawn_detached()
        print("watcher spawned")
        return
    cur = os.getpid()
    other = alive_pid(PIDFILE)
    if other:
        log("exit: already running pid=%d" % other)
        return
    with open(PIDFILE, "w") as f:
        f.write(str(cur))
    log("watcher started pid=%d" % cur)
    try:
        while True:
            try:
                scan_once()
            except Exception as e:
                log("scan error: %r" % e)
            time.sleep(POLL)
    finally:
        try:
            os.remove(PIDFILE)
        except Exception:
            pass


if __name__ == "__main__":
    main()
