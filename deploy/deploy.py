#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""deploy.py - pdmsDrawing 一键部署（对方机器跑这个）。

做什么：
  1) 探测 PDMS 12.1 安装目录（扫各盘符 AVEVA\\Plant\\PDMS12.1*，找含 DesignAddins.xml 的根目录）
  2) 拷 drawingPDMS.dll 到 PDMS 根目录（已存在则备份 .bak）
  3) 在 DesignAddins.xml 注册 drawingPDMS 插件（防重：已注册则跳过）
  4) 环境自检：.NET 3.5（PDMS 插件宿主 CLR v2.0 硬要求）、FangYuanPDMS.dll
用法：
  python deploy.py               # 自动探测 PDMS 目录
  python deploy.py --pdms "D:\\AVEVA\\Plant\\PDMS12.1.SP4"   # 手动指定
部署后：启动 PDMS → Draft 模块 → 右侧「Draw(PDMS)」窗口即插件界面；
        首次部署再跑 init_project.py 建出图规则/样式（需 PDMS 已启动）。

云端化（2026-10-22）：install() 抽成函数供 bridge/agent.py 的 deploy 指令复用。
注意：addin 注册必须写【裸程序集名】（如 <string>drawingPDMS</string>）——
写完整路径会被加载器当相对名再拼一遍安装目录导致加载失败（2026-10-19 实锤）。
"""
import os, sys, glob, shutil, winreg

BS = chr(92)
HERE = os.path.dirname(os.path.abspath(__file__))
PAYLOAD = os.path.join(HERE, "payload", "drawingPDMS.dll")

def find_pdms(explicit=None):
    cands = []
    if explicit:
        cands.append(explicit)
    for drive in "CDEFGH":
        cands += glob.glob(drive + ":" + BS + "AVEVA" + BS + "Plant" + BS + "PDMS12.1*")
        cands += glob.glob(drive + ":" + BS + "Program Files" + BS + "AVEVA" + BS + "Plant" + BS + "PDMS12.1*")
    for c in cands:
        if os.path.exists(os.path.join(c, "DesignAddins.xml")):
            return c
    return None

def check_net35():
    try:
        k = winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE,
                           "SOFTWARE" + BS + "Microsoft" + BS + "NET Framework Setup" + BS + "NDP" + BS + "v3.5")
        v, _ = winreg.QueryValueEx(k, "Install")
        return v == 1
    except Exception:
        return False

def install(pdms, dll_path, lib_dir=None):
    """Deploy plugin dll into PDMS dir. Returns True on success.
    Used by deploy.py CLI and by bridge/agent.py cloud deploy command."""
    if not os.path.exists(dll_path):
        print("[FAIL] missing dll: " + dll_path)
        return False
    # copy dll (backup old)
    dst_dll = os.path.join(pdms, "drawingPDMS.dll")
    if os.path.exists(dst_dll):
        bak = dst_dll + ".bak"
        shutil.copy2(dst_dll, bak)
        print("[OK] 旧 drawingPDMS.dll 已备份 -> " + os.path.basename(bak))
    shutil.copy2(dll_path, dst_dll)
    print("[OK] drawingPDMS.dll 已部署 -> " + dst_dll)
    # FangYuanPDMS dependency (should ship with PDMS env; backfill from lib if missing)
    fy = os.path.join(pdms, "FangYuanPDMS.dll")
    if not os.path.exists(fy):
        src_fy = os.path.join(lib_dir, "FangYuanPDMS.dll") if lib_dir else None
        if src_fy and os.path.exists(src_fy):
            shutil.copy2(src_fy, fy); print("[OK] FangYuanPDMS.dll 已补装（来自 lib）")
        else:
            print("[WARN] FangYuanPDMS.dll 缺失且无 lib 副本")
    else:
        print("[OK] FangYuanPDMS.dll 已存在")
    # register addin - BARE assembly name (full path breaks the AVEVA addin loader)
    axml = os.path.join(pdms, "DesignAddins.xml")
    asm = os.path.splitext(os.path.basename(dst_dll))[0]
    entry = "<string>" + asm + "</string>"
    txt = open(axml, "r", encoding="utf-8-sig").read() if os.path.exists(axml) else ""
    if asm in txt:
        print("[OK] DesignAddins.xml 已注册 " + asm + "（跳过）")
    else:
        txt = txt.replace("</ArrayOfString>", "  " + entry + "\n</ArrayOfString>")
        open(axml, "w", encoding="utf-8-sig").write(txt)
        print("[OK] DesignAddins.xml 已注册 -> " + asm)
    return True

def main():
    explicit = None
    if "--pdms" in sys.argv:
        explicit = sys.argv[sys.argv.index("--pdms") + 1]
    pdms = find_pdms(explicit)
    print("== pdmsDrawing 部署 ==")
    if not pdms:
        print("[FAIL] 未找到 PDMS 12.1 安装目录（需含 DesignAddins.xml）。用 --pdms 手动指定。"); sys.exit(1)
    print("[OK] PDMS 目录: " + pdms)
    if not os.path.exists(PAYLOAD):
        print("[FAIL] 缺 payload\\drawingPDMS.dll（部署包不完整）"); sys.exit(1)
    if check_net35(): print("[OK] .NET 3.5 已启用")
    else: print("[WARN] .NET 3.5 未检测到——PDMS 12.1 插件宿主为 CLR v2.0，请在「启用或关闭 Windows 功能」勾选 .NET Framework 3.5 后重试")
    install(pdms, PAYLOAD, os.path.join(HERE, "..", "lib"))
    print("== 部署完成 ==")
    print("下一步：启动 PDMS → Draft 模块 → 右侧「Draw(PDMS)」窗口。")
    print("        首次使用跑: python init_project.py --view <视图引用> --sheet <图纸引用>")

if __name__ == "__main__":
    main()
