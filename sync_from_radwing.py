#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""sync_from_radwing.py - 把 radwingPDMS 工作区的最新出图代码同步进本沉淀项目。

用法：python sync_from_radwing.py          # 同步源码（.cs/.csproj/.resx/.config/.targets/Properties/_reference）
      python sync_from_radwing.py --lib    # 同时刷新 lib 依赖副本（依赖 DLL 变了才需要）
      python sync_from_radwing.py --dry    # 只看会变更哪些文件，不写

升级规则：
  1) 只覆盖源码类文件，bin/obj/zip 永不进入；
  2) csproj 同步后自动本地化 HintPath（exportModel bin Debug 目录 -> ..\\..\\..\\lib）并重注入沉淀头注释；
  3) lib 与 bridge 平时不动（bridge 脚本若有改动请手动从 D:/pdmsAgent/bridge 拷）。
同步后到 VS 里 HAOL 编译即完成升级（输出仍部署到 PDMS12.1.SP4 根目录）。
"""
import os, sys, shutil, filecmp

SRC = r"D:\repos\radwingPDMS\drawingPDMS"
DST = r"D:\repos\pdmsDrawing\src\drawingPDMS"
# lib 源候选：老 NSS 路径已失效(2026-10-22)，回落到 radwingPDMS/build_out
LIB_SRC_CANDS = [
    r"D:\NSS\repos\exportModel\exportModel\bin\Debug",
    r"D:\repos\radwingPDMS\build_out",
]
LIB_SRC = next((c for c in LIB_SRC_CANDS if os.path.isdir(c)), LIB_SRC_CANDS[-1])
LIB_DST = r"D:\repos\pdmsDrawing\lib"
EXTS = (".cs", ".csproj", ".resx", ".config", ".targets")
DIRS = ("Properties", "_reference")
BS = chr(92)

def localize_hintpath(csproj_path):
    """csproj 同步后把 HintPath 本地化到 ..\\..\\..\\lib（含 FangYuanPDMS），并重注入沉淀头注释。"""
    import io
    s = io.open(csproj_path, encoding="utf-8-sig").read()
    old = BS.join(["D:", "NSS", "repos", "exportModel", "exportModel", "bin", "Debug"]) + BS
    s = s.replace(old, BS.join(["..", "..", "..", "lib"]) + BS)
    s = s.replace(BS.join(["D:", "AVEVA", "Plant", "PDMS12.1.SP4", "FangYuanPDMS.dll"]),
                  BS.join(["..", "..", "..", "lib", "FangYuanPDMS.dll"]))
    if "pdmsDrawing 沉淀版" not in s:
        head = ("<!-- pdmsDrawing 沉淀版（2026-10-20 自 radwingPDMS/drawingPDMS 复制独立成库）\n"
                "     出图引擎：headlessDrawing（replace_symbols 全流程：轴网/尺寸链/轴距行/设备标高/drawinglist）\n"
                "     + graphicsDrawing（原生 LDIM/DPPT 参考）+ PdmsBridge（127.0.0.1:18753 TCP op 通道）\n"
                "     依赖已本地化 lib\\；bridge 运行脚本见 ..\\bridge\\（bridge_client.py / pdf_mono.py 黑白后处理 / dialog_watcher.py）\n"
                "     部署：Debug 输出仍指向 PDMS12.1.SP4 根目录（PDMS 从此处加载 drawingPDMS.dll）\n"
                "     升级：根目录 sync_from_radwing.py 一键从 radwingPDMS 同步 -->\n")
        idx = s.find("<Project")
        s = s[:idx] + head + s[idx:]
    io.open(csproj_path, "w", encoding="utf-8-sig").write(s)

def main():
    dry = "--dry" in sys.argv
    with_lib = "--lib" in sys.argv
    changed, added = [], []
    for root, dirs, files in os.walk(SRC):
        rel = os.path.relpath(root, SRC)
        if rel.split(os.sep)[0] in ("bin", "obj") or rel == "bin" or rel == "obj":
            dirs[:] = []
            continue
        dirs[:] = [d for d in dirs if d not in ("bin", "obj")]
        for f in files:
            if rel == "." and not f.endswith(EXTS):
                continue
            if rel != "." and rel.split(os.sep)[0] not in DIRS:
                continue
            src_f = os.path.join(root, f)
            dst_f = os.path.join(DST, rel, f) if rel != "." else os.path.join(DST, f)
            os.makedirs(os.path.dirname(dst_f), exist_ok=True)
            if not os.path.exists(dst_f) or not filecmp.cmp(src_f, dst_f, shallow=False):
                (added if not os.path.exists(dst_f) else changed).append(os.path.relpath(dst_f, DST))
                if not dry:
                    shutil.copy2(src_f, dst_f)
    # csproj HintPath 本地化
    if not dry:
        localize_hintpath(os.path.join(DST, "drawingPDMS.csproj"))
    if with_lib and not dry:
        n = 0
        for f in os.listdir(LIB_SRC):
            if f.lower().endswith(".dll") and (f.startswith("Aveva.") or f.startswith("PDMS")):
                shutil.copy2(os.path.join(LIB_SRC, f), os.path.join(LIB_DST, f))
                n += 1
        fy = r"D:\AVEVA\Plant\PDMS12.1.SP4\FangYuanPDMS.dll"
        if os.path.exists(fy):
            shutil.copy2(fy, os.path.join(LIB_DST, "FangYuanPDMS.dll")); n += 1
        print("lib 刷新: %d 个 DLL" % n)
    print("新增 %d、更新 %d%s" % (len(added), len(changed), "（dry run）" if dry else ""))
    for f in added: print("  + " + f)
    for f in changed: print("  ~ " + f)
    if not dry:
        print("下一步：VS 打开 src\\drawingPDMS\\drawingPDMS.csproj → HAOL 编译即升级完成。")

if __name__ == "__main__":
    main()
