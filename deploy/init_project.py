#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""init_project.py - 对方项目库首次初始化出图规则/样式（deploy.py 之后、PDMS 已启动时跑）。

通过 pdmsBridge（127.0.0.1:18753，插件自动启动）下发三步：
  1) make_local_rule —— 视图本地规则 + 自建 STYL：ALL BRANCH 零出图（单线由出图流程全责绘制）
  2) set_styl        —— 自建样式设为红单线：CLFG=TRUE,PLFG=FALSE,CLSTYL=SOLID,CLCOLO=2
  3) set_rrsf        —— （可选）视图规则集切换
用法：
  python init_project.py --view "=xxxx/yyyy" --sheet "=xxxx/zzzz"
     view/sheet 在 PDMS Draft 里选中视图/图纸后 Q REF 查看（形如 =16385/1249）。
注意：跑之前 PDMS 必须已启动且进入出图模块（bridge 端口活）。
"""
import socket, json, sys, time

BS = chr(92)
PORT = 18753

def op(kind_op, params, rid, timeout=180):
    req = {"id": rid, "kind": "op", "op": kind_op, "params": params}
    s = socket.create_connection(("127.0.0.1", PORT), timeout=timeout)
    s.sendall((json.dumps(req) + "\n").encode("utf-8"))
    s.settimeout(timeout - 5)
    buf = b""
    while b"\n" not in buf:
        c = s.recv(65536)
        if not c: break
        buf += c
    s.close()
    return json.loads(buf.split(b"\n", 1)[0].decode("utf-8", "replace"))

def getarg(name, default=None):
    if name in sys.argv: return sys.argv[sys.argv.index(name) + 1]
    return default

def main():
    view = getarg("--view")
    sheet = getarg("--sheet")
    if not view or not sheet:
        print("用法: python init_project.py --view \"=xxxx/yyyy\" --sheet \"=xxxx/zzzz\"")
        print("      view/sheet 引用：PDMS Draft 里选中视图/图纸，命令行 Q REF 查看。")
        sys.exit(1)
    # 0) bridge 探活
    try:
        r = op("exec_cmds", {"cmds": "Q SPACE"}, 1, 30)
        print("[OK] bridge 活性: " + r.get("output", "")[:60])
    except Exception as e:
        print("[FAIL] bridge 不可达（127.0.0.1:%d）：%s" % (PORT, e))
        print("      确认 PDMS 已启动且插件加载（Draw(PDMS) 窗口存在）。"); sys.exit(1)
    rid = 10
    # 1) 视图本地规则：ALL BRANCH 零出图 + 自建样式
    r = op("make_local_rule", {"view": view, "sheet": sheet, "crit": "ALL BRANCH", "name": "LR_BR_OFF"}, rid); rid += 1
    print("[make_local_rule] " + ("OK " + r.get("output", "") if r.get("ok") else "FAIL " + r.get("error", "")))
    # 自建样式名形如 AGT_OFF_HHMMSS，从日志回读（op_make_local_rule.log 尾部）
    styl_ref = None
    try:
        logp = r"D:\pdmsAgent\bridge\op_make_local_rule.log"
        txt = open(logp, "r", encoding="utf-8", errors="replace").read()
        for line in reversed(txt.splitlines()):
            if "STYL 建于" in line:
                styl_ref = line.split("ref=")[-1].strip(); break
    except Exception: pass
    # 2) 样式设为红单线
    if styl_ref:
        r = op("set_styl", {"styl": styl_ref,
                            "set": "CLFG=TRUE,PLFG=FALSE,CLSTYL=SOLID,CLCOLO=2,FFSTYL=OFF"}, rid); rid += 1
        print("[set_styl " + styl_ref + "] " + ("OK" if r.get("ok") else "FAIL " + r.get("error", "")))
    else:
        print("[WARN] 未从日志解析到自建 STYL 引用，跳过样式上色（手工: make_local_rule 日志找 ref=）")
    # 3) 若已有所需规则集（如从本机拷来的 AGT-SD），切 RRSF：
    ruleset = getarg("--ruleset")
    if ruleset:
        r = op("set_rrsf", {"view": view, "ruleset": ruleset}, rid); rid += 1
        print("[set_rrsf] " + ("OK" if r.get("ok") else "FAIL " + r.get("error", "")))
    print("== 初始化完成 ==")
    print("下一步：跑一次 replace_symbols（出图全流程）验证单线/轴网/标注；")
    print("        bridge 工具在 ..\\bridge\\（bridge_client.py --kind op --op replace_symbols ...）。")

if __name__ == "__main__":
    main()
