#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""wizard.py - pdmsDrawing 出图平台（v4：建设备/建库/版本 三页并入 + 高端改版）。

v5（用户 2026-10-22「最后一个不是版本，是无人操控PDMS」）：
  - /version 版本页升级为 /control 无人操控：运行状态自检 + PML 控制台
    （逐行下发——铁律：多条一次下发易 RunInPdms failed；出错即停可配）
  - 云端 agent op exec_pml 同步改为逐行下发
  - 新增 /equip 建设备（可执行：PML 一行式原语，复用 gen_elec41 实测语法，结果文件回读）
  - 新增 /db 建库（等级库/元件库流程页，对接 pdms-database 工作流，骨架）
  - 新增 /control 无人操控（v5 前身为 /version 环境自检：PDMS 进程/插件 DLL/bridge/平台，纯只读）
  - 全站去图标，墨绿+鎏金设计系统，衬线大标题
v3 保留：四页导航骨架、出图流水线全自动、10 段动画、Add(CE)、材料表
用法：python wizard.py [--port 18760]
"""
import base64, json, re, socket, subprocess, sys, threading, time, webbrowser, os
from http.server import BaseHTTPRequestHandler, HTTPServer

PORT_BRIDGE = 18753
LOG = r"D:\pdmsAgent\bridge\op_replace_symbols.log"
PYMONO = sys.executable
MONO = os.path.join(os.path.dirname(os.path.abspath(__file__)), "pdf_mono.py")
OUTDIR = r"D:\pdmsAgent\bridge\out\wizard"
BRIDGE_DIR = os.path.dirname(os.path.abspath(__file__))
DEPLOY_DIR = os.path.join(os.path.dirname(BRIDGE_DIR), "deploy")
PLATFORM_VER = "v4.0"

# ---------------- 建设备 PML（gen_elec41 实测语法：一行式原语 + 最后才 NAME） ----------------

def pml_path(p):
    return "/" + p.replace("/", "\\")

def pml_create_equipment(p):
    """生成建设备 PML。返回 (code, result_file)。命名风险包 handle ANY（防模态框冻宏）。"""
    zone = str(p.get("zone", "")).strip()
    name = re.sub(r"[^A-Za-z0-9_\-]", "", str(p.get("name", "")).strip()) or ("EQ" + time.strftime("%H%M%S"))
    typ = p.get("etype", "cylinder")
    e = str(p.get("e", "0")); n = str(p.get("n", "0")); u = str(p.get("u", "0"))
    res = os.path.join(OUTDIR, "equip_result.txt")
    L = ["CE " + (zone if zone else "/*"), "NEW EQUIPMENT"]
    if typ == "box":
        L.append("NEW BOX POS E %smm N %smm U %smm XLEN %smm YLEN %smm ZLEN %smm END" %
                 (e, n, u, p.get("xlen", "1000"), p.get("ylen", "1000"), p.get("zlen", "1000")))
    elif typ == "vessel":
        d = str(p.get("diam", "3000")); h = str(p.get("h", "8000")); cap = str(p.get("cap", "400"))
        L.append("NEW CYLINDER POS E %smm N %smm U %smm DIAM %smm HEIG %smm END" % (e, n, u, d, h))
        L.append("NEW CYLINDER POS E %smm N %smm U %smm DIAM %smm HEIG %smm END" %
                 (e, n, str(int(float(u or 0)) + int(float(h or 0))), d, cap))
    else:
        d = str(p.get("diam", "3000")); h = str(p.get("h", "8000"))
        L.append("NEW CYLINDER POS E %smm N %smm U %smm DIAM %smm HEIG %smm END" % (e, n, u, d, h))
    L.append("handle ANY")
    L.append("  NAME /" + name)
    L.append("endhandle")
    rp = pml_path(res)
    L += ["openfile " + rp + " write !F1",
          "handle ANY",
          "  openfile " + rp + " overwrite !F1",
          "endhandle",
          "!OK = 'UNNAMED'",
          "handle ANY",
          "  !OK = (!!ce.Name)",
          "endhandle",
          "writefile !F1 |$!OK|",
          "closefile !F1"]
    return "\n".join(L), res

def ensure_dialog_watcher():
    """复用 bridge_client 的模态弹窗看门狗（防 NAME 重名弹框冻住）。"""
    try:
        sys.path.insert(0, BRIDGE_DIR)
        from bridge_client import ensure_dialog_watcher as _w
        _w()
    except Exception:
        pass

def run_equipment(p):
    """建设备执行：PML 下发 → 结果文件回读 → 结论。"""
    try:
        os.makedirs(OUTDIR, exist_ok=True)
        code, res = pml_create_equipment(p)
        if os.path.exists(res):
            os.remove(res)
        ensure_dialog_watcher()
        r = bridge_req({"id": int(time.time()), "kind": "exec", "code": code}, timeout=120)
        got = ""
        try:
            with open(res, "r", encoding="utf-8", errors="replace") as f:
                got = f.read().strip()
        except Exception:
            pass
        want = "/" + re.sub(r"[^A-Za-z0-9_\-]", "", str(p.get("name", "")).strip())
        if not r.get("ok"):
            return {"ok": False, "error": r.get("error") or "bridge 执行失败", "pml": code}
        if got == want:
            return {"ok": True, "msg": "已建成并命名 " + want, "pml": code}
        if got and got != "UNNAMED":
            return {"ok": True, "msg": "元素已创建，命名结果: " + got + "（若为重名，PDMS 里 Q NAME 核对）", "pml": code}
        return {"ok": True, "msg": "元素可能已创建但命名未确认（检查 ZONE 引用/重名），PDMS 里 Q NAME 核对", "pml": code}
    except Exception as e:
        return {"ok": False, "error": str(e), "pml": locals().get("code", "")}

# ---------------- 版本与环境自检（纯只读） ----------------

def env_report():
    rep = {"platform": PLATFORM_VER, "bridge": "offline", "probe": ""}
    # 1. bridge 通道
    try:
        s = socket.create_connection(("127.0.0.1", PORT_BRIDGE), timeout=3)
        s.close()
        rep["bridge"] = "online (127.0.0.1:%d)" % PORT_BRIDGE
        try:
            pr = bridge_req({"id": int(time.time()), "kind": "op", "op": "probe_read", "params": {}}, 30)
            out = (pr.get("output", "") or "").strip()
            rep["probe"] = out[:300] if out else ""
        except Exception as e:
            rep["probe"] = "probe err: %s" % e
    except Exception:
        pass
    # 2. PDMS 进程 + 插件 DLL（PowerShell 单次采集，只读；PDMS 12.1 进程名是 des）
    try:
        ps = ("Get-Process | Where-Object { $_.Path -and ($_.ProcessName -match '^(des|design|pdms|e3d|draft|monitor)') } | "
              "ForEach-Object { $v=(Get-Item $_.Path).VersionInfo.ProductVersion; "
              "Write-Output ('PROC|' + $_.ProcessName + '|' + $_.Id + '|' + $v + '|' + $_.Path) }; "
              "$dlls = @('drawingPDMS.dll','FangYuanPDMS.dll'); "
              "$roots = @('D:\\AVEVA\\Plant\\PDMS12.1.SP4','C:\\AVEVA\\Plant\\PDMS12.1.SP4'); "
              "foreach ($r in $roots) { foreach ($d in $dlls) { $f = Join-Path $r $d; "
              "if (Test-Path $f) { $i = Get-Item $f; "
              "Write-Output ('DLL|' + $d + '|' + $i.VersionInfo.ProductVersion + '|' + $i.LastWriteTime.ToString('yyyy-MM-dd HH:mm') + '|' + $r) } } }")
        cp = subprocess.run(["powershell", "-NoProfile", "-Command", ps],
                            capture_output=True, timeout=30)
        lines = cp.stdout.decode("gbk", "replace").splitlines()
        rep["procs"] = [l.split("|")[1:] for l in lines if l.startswith("PROC|")]
        rep["dlls"] = [l.split("|")[1:] for l in lines if l.startswith("DLL|")]
    except Exception as e:
        rep["procs"], rep["dlls"] = [], []
        rep["env_err"] = str(e)
    return rep

# ---------------- 无人操控：逐行执行（v5.1，本地/云端共用） ----------------

# 查询行改走 get_ce op 真值回读。实证（用户 PDMS 命令窗口）：
# exec=$M 宏内部行级 CP 报错（如 Q CE → "CP: Meaningless symbol(s)"）不置 RunInPdms false
# → ok:true 不代表行合法；且查询回显在 PDMS 命令窗口，bridge 不捕获文本。
Q_GETCE = {"CE": "all", "CREF": "ref", "REF": "ref", "NAME": "name", "TYPE": "type"}

def exec_pml_lines(code, stop_on_error=True, on_line=None):
    """逐行下发 PdmsBridge。查询行走 get_ce op；其余行 kind=exec。
    on_line(seg) 每行执行完回调（云端 agent 用它推 progress）。"""
    def emit(seg):
        if on_line:
            try: on_line(seg)
            except Exception: pass
    results, ok_all = [], True
    lines = [l.strip() for l in code.splitlines() if l.strip()]
    for i, line in enumerate(lines):
        if not ok_all and stop_on_error:
            results.append({"line": line, "ok": False, "output": "", "error": "skipped (前序行失败)"})
            continue
        rid = int(time.time() * 1000) + i
        tok = line.upper().split()
        qk = Q_GETCE.get(tok[1], "?") if (tok and tok[0] == "Q" and len(tok) > 1) else None
        try:
            if qk:  # 已知查询 → get_ce op 真值
                r = bridge_req({"id": rid, "kind": "op", "op": "get_ce", "params": {}}, 60)
                parts = (r.get("output", "") or "").split("|") if r.get("ok") else []
                if r.get("ok") and parts and any(p.strip() for p in parts):
                    ref = parts[0] if len(parts) > 0 else ""
                    tn = parts[1] if len(parts) > 1 else ""
                    nm = parts[2] if len(parts) > 2 else ""
                    val = {"all": "ref=%s  type=%s  name=%s" % (ref, tn, nm),
                           "ref": ref, "name": nm, "type": tn}[qk]
                    entry = {"line": line, "ok": True, "output": "[get_ce] " + val, "error": ""}
                elif r.get("ok"):
                    # ok:true 但回读全空 —— 实锤语义：部署的 drawingPDMS.dll 早于 get_ce
                    # 正式版（10-20 晚），空回读≠CE为空。给可行动错误而非假成功。
                    entry = {"line": line, "ok": False, "output": "",
                             "error": "get_ce 返回空：正在运行的 drawingPDMS.dll 疑为旧版"
                                      "（需重编译 pdmsDrawing/src 并部署后重启 PDMS 再试）"}
                    ok_all = False
                else:
                    entry = {"line": line, "ok": False, "output": "",
                             "error": "get_ce: " + (r.get("error") or "")}
                    ok_all = False
            else:   # 普通/未知查询行 → exec（未知查询加诚实注记）
                r = bridge_req({"id": rid, "kind": "exec", "code": line}, 600)
                out = r.get("output", "") or ""
                note = ("\n(注: 查询回显在 PDMS 命令窗口，bridge 不捕获文本；"
                        "行级 CP 报错不影响 ok——判合法请看 PDMS 窗口)") if qk == "?" else ""
                entry = {"line": line, "ok": bool(r.get("ok")), "output": out + note,
                         "error": r.get("error")}
                if not r.get("ok"): ok_all = False
        except Exception as e:
            entry = {"line": line, "ok": False, "output": "", "error": str(e)}
            ok_all = False
        results.append(entry)
        seg = "#%d %s → %s%s" % (i + 1, line, "OK" if entry["ok"] else "FAIL",
                                 ("\n" + entry["output"]) if entry["output"]
                                 else (("\n!! " + str(entry["error"])) if entry["error"] else ""))
        emit(seg)
    return {"ok": ok_all, "results": results}

def format_pml_results(res):
    """results -> 人类可读多行文本（云端 task_result 用）。"""
    out = []
    for i, r in enumerate(res["results"]):
        out.append("#%d %s\n%s" % (i + 1, r["line"], r.get("output") or "")
                   + ("" if not r.get("error") else "\n!! " + str(r["error"])))
    return "\n".join(out)



CSS = """
*{box-sizing:border-box;margin:0;padding:0}
body{font-family:"Microsoft YaHei",sans-serif;background:#f2f3f1;color:#1f2a26;font-size:14px}
.serif{font-family:"STZhongsong","SimSun",serif}
.masthead{background:linear-gradient(160deg,#141f1b 0%,#1d3129 55%,#24443a 100%);color:#f4efe4;padding:34px 0 0}
.masthead .inner{max-width:920px;margin:0 auto;padding:0 24px}
.brand{display:flex;align-items:baseline;gap:18px;border-bottom:1px solid rgba(201,164,92,.35);padding-bottom:18px}
.brand h1{font-family:"STZhongsong","SimSun",serif;font-size:30px;font-weight:400;letter-spacing:12px;color:#f4efe4}
.brand .en{font-size:10px;letter-spacing:5px;color:#c9a45c;text-transform:uppercase}
.brand .ver{margin-left:auto;font-size:11px;color:#8fa89d;letter-spacing:2px}
nav{display:flex;gap:34px;max-width:920px;margin:0 auto;padding:0 24px}
nav a{color:#9db4aa;text-decoration:none;font-size:14px;letter-spacing:4px;padding:14px 2px 12px;
      border-bottom:2px solid transparent;transition:color .25s,border-color .25s}
nav a:hover{color:#e8e2d2}
nav a.on{color:#f4efe4;border-bottom-color:#c9a45c}
.wrap{max-width:920px;margin:26px auto 60px;padding:0 24px}
.card{background:#fff;border:1px solid #e2e6e1;border-radius:3px;padding:20px 24px;margin:0 0 16px;
      box-shadow:0 1px 2px rgba(20,31,27,.04)}
.card>h2{font-size:12px;font-weight:700;letter-spacing:4px;color:#8a9a91;margin-bottom:14px}
.card>h2 b{color:#1d3129;font-size:15px;letter-spacing:2px;margin-right:10px}
.frow{display:flex;gap:10px;align-items:center;margin:10px 0}
.frow input[type=text],.frow select{flex:1;padding:9px 12px;border:1px solid #d7dcd6;border-radius:2px;
       font-size:13px;background:#fbfcfb;transition:border-color .2s}
.frow input:focus,.frow select:focus{outline:none;border-color:#c9a45c}
.frow label{width:96px;font-size:12px;color:#5d6e66;letter-spacing:1px;flex:none}
.btn{background:#fff;color:#1d3129;border:1px solid #1d3129;border-radius:2px;padding:8px 16px;
     cursor:pointer;font-size:12px;letter-spacing:2px;white-space:nowrap;transition:all .2s}
.btn:hover{background:#1d3129;color:#f4efe4}
.btn-gold{background:#1d3129;color:#f4efe4;border-color:#1d3129}
.btn-gold:hover{background:#c9a45c;border-color:#c9a45c}
.chks{display:flex;gap:22px;flex-wrap:wrap}
.chks label{cursor:pointer;font-size:13px;color:#33443c}
textarea{width:100%;min-height:56px;border:1px solid #d7dcd6;border-radius:2px;padding:9px 12px;
         font:13px/1.6 "Microsoft YaHei";resize:vertical;background:#fbfcfb}
textarea:focus{outline:none;border-color:#c9a45c}
.go{width:100%;background:linear-gradient(135deg,#1d3129,#2a4a3e);color:#f4efe4;border:0;border-radius:2px;
    padding:14px;font-size:15px;letter-spacing:10px;cursor:pointer;margin-top:6px;transition:filter .2s}
.go:hover{filter:brightness(1.15)}
.go:disabled{background:#a9b3ae;cursor:not-allowed}
.hint{color:#98a49d;font-size:12px;margin-top:6px;line-height:1.7}
.badge{display:inline-block;border:1px solid #c9a45c;color:#8a6d1f;font-size:10px;letter-spacing:2px;
       border-radius:2px;padding:2px 10px;margin-left:10px;vertical-align:2px}
#prog{display:none;margin-top:14px}
.stages{list-style:none;border-top:1px solid #e8ebe7}
.stages li{padding:8px 4px;border-bottom:1px solid #e8ebe7;color:#a3aca6;font-size:13px;letter-spacing:1px;transition:all .4s}
.stages li.cur{color:#1d3129;font-weight:bold;background:#faf8f2}
.stages li.done{color:#2a6e4f}
.stages li.done::before{content:"— DONE  "}.stages li.cur::before{content:"▶  "}
.spin{display:inline-block;width:13px;height:13px;border:2px solid #c9a45c;border-top-color:transparent;
      border-radius:50%;animation:sp 1s linear infinite;vertical-align:-2px;margin-right:9px}
@keyframes sp{to{transform:rotate(360deg)}}
#log{background:#141f1b;color:#9fb89a;font:11px/1.7 Consolas,monospace;padding:12px 14px;border-radius:2px;
     height:180px;overflow-y:auto;white-space:pre-wrap;display:none;margin-top:12px}
#prev{display:none;text-align:center;margin-top:14px}
#prev img{max-width:100%;border:1px solid #d7dcd6;box-shadow:0 3px 14px rgba(20,31,27,.12)}
#st{color:#1d3129;font-size:13px;font-weight:bold;letter-spacing:1px}
.kv{display:flex;border-bottom:1px solid #eef0ed;padding:9px 2px;font-size:13px}
.kv .k{width:150px;color:#8a9a91;letter-spacing:1px;flex:none}
.kv .v{color:#22332c;word-break:break-all}
.ok{color:#2a6e4f}.bad{color:#a94438}
.soon{border:1px dashed #cfc4a4;border-radius:3px;padding:18px 20px;color:#6d5f38;font-size:13px;
      line-height:2;background:#fdfbf5;margin-top:4px}
.step{display:flex;gap:14px;margin:12px 0;align-items:baseline}
.step .no{font-family:Georgia,serif;font-size:20px;color:#c9a45c;width:34px;flex:none;text-align:right}
.step .t{font-size:13px;color:#33443c;letter-spacing:1px}
"""

HEAD = """<!DOCTYPE html><html lang="zh"><head><meta charset="utf-8">
<title>pdmsDrawing 平台</title><style>%s</style></head><body>
<div class="masthead"><div class="inner">
<div class="brand"><h1>pdmsDrawing</h1><span class="en">AI Draft Engine · From Model to Drawing</span>
<span class="ver">%s</span></div></div>
<nav><a href="/draw" class="__A1__">出 图</a><a href="/equip" class="__A2__">建 设 备</a>
<a href="/model" class="__A3__">AI 建 模</a><a href="/db" class="__A4__">建 库</a>
<a href="/iso" class="__A5__">单 线 图</a><a href="/mto" class="__A6__">材 料 表</a>
<a href="/version" class="__A7__">版 本</a></nav></div>
<div class="wrap">""" % (CSS, PLATFORM_VER)

def page(a1="", a2="", a3="", a4="", a5="", a6="", a7=""):
    """Token-based nav assembly (CSS contains bare %, so no second %-format pass)."""
    return (HEAD.replace("__A1__", a1).replace("__A2__", a2).replace("__A3__", a3)
                .replace("__A4__", a4).replace("__A5__", a5).replace("__A6__", a6)
                .replace("__A7__", a7))

FOOT = "</div></body></html>"

# ---------- 出图（/draw） ----------
PAGE_DRAW = (page("on", "", "", "", "", "", "")) + """
<div class="card"><h2><b>目标图纸</b>TARGET DRAWING</h2>
<div class="frow"><label>视图引用</label><input id="view" placeholder="如 =16385/1249">
<button class="btn" onclick="ceTo('view')">取 CE</button></div>
<div class="frow"><label>图纸引用</label><input id="sheet" placeholder="如 =16385/1242">
<button class="btn" onclick="ceTo('sheet')">取 CE</button></div>
<div class="hint">在 PDMS 图形区/树里选中元素点「取 CE」自动填入；或手填（Q REF 查看）</div></div>

<div class="card"><h2><b>出图元素</b>CONTENT</h2>
<div class="chks">
<label><input type="checkbox" id="pipe" checked> 管道（不勾=设备布置图，自动新建Sheet页）</label>
<label><input type="checkbox" id="equip" checked> 设备</label>
<label><input type="checkbox" id="struct"> 结构</label>
<label><input type="checkbox" id="grid" checked> 轴网</label>
</div>
<div class="frow" style="margin-top:12px"><label>轴网 FRMWORK</label><input id="gridfrmws" placeholder="勾轴网时可选指认">
<button class="btn" onclick="ceTo('gridfrmws')">取 CE</button></div></div>

<div class="card"><h2><b>备注</b>NOTE</h2>
<textarea id="note" placeholder="例如：图框换成 A3 横版 / 标题栏加公司名 / 轴距字号加大 —— 随单存档"></textarea></div>

<button class="go" id="go" onclick="run()">开 始 出 图</button>
<div id="prog"><p style="margin:10px 0"><span class="spin"></span><b id="st">准备中…</b></p>
<ul class="stages" id="stages"></ul>
<div id="log"></div><div id="prev"></div></div>
<script>
const STAGES=[["旧符号清理","旧「轴网」NOTE 删除"],["轴网落线","轴网落线 ="],
 ["设备轮廓/标高","设备轮廓 ="],["图块插入","图块插入 ="],["红色管线绘制","红色管线 ="],
 ["交叉断口","交叉断口 ="],["尺寸链/轴距行","轴距行汇总 ="],["落盘","SAVEWORK"],
 ["导出 PDF","[WIZ] EXPORT-OK"],["黑白处理","[WIZ] MONO-OK"]];
let busy=false;
function ceTo(id){if(busy)return;fetch('/api/ce').then(r=>r.json()).then(j=>{
 if(!j.ok){alert(j.error||'CE 读取失败：先在 PDMS 里选中元素');return}
 document.getElementById(id).value=j.ref;}).catch(e=>alert('bridge 不可达：'+e))}
function qp(){const v=document.getElementById('view').value.trim(),s=document.getElementById('sheet').value.trim();
 if(!v||!s){alert('请填视图/图纸引用');return null}
 const p={view:v,sheet:s,drawPipe:document.getElementById('pipe').checked?'1':'0',
  drawEquip:document.getElementById('equip').checked?'1':'0',
  drawStruct:document.getElementById('struct').checked?'1':'0',
  drawGrid:document.getElementById('grid').checked?'1':'0'};
 const gf=document.getElementById('gridfrmws').value.trim();if(gf)p.gridFrmws=gf;
 const nt=document.getElementById('note').value.trim();if(nt)p.note=nt;return p}
async function run(){if(busy)return;const p=qp();if(!p)return;busy=true;
 const go=document.getElementById('go');go.disabled=true;
 document.getElementById('prog').style.display='block';
 document.getElementById('prev').style.display='none';
 const ul=document.getElementById('stages');ul.innerHTML='';
 STAGES.forEach(()=>{const li=document.createElement('li');li.textContent='待执行';ul.appendChild(li)});
 document.getElementById('log').textContent='';document.getElementById('log').style.display='block';
 document.getElementById('st').textContent='画图中…（出图 → 落盘 → 导 PDF → 黑白 → 预览）';
 await fetch('/api/start',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(p)});
 const tmr=setInterval(async()=>{try{const j=await(await fetch('/api/log')).json();
  if(j.log===undefined)return;
  document.getElementById('log').textContent=j.log;document.getElementById('log').scrollTop=1e6;
  const lines=j.log.split('\\n');let curIdx=-1;
  STAGES.forEach((sg,i)=>{const li=ul.children[i];
   const hit=lines.some(l=>l.includes(sg[1]));
   if(hit){li.className='done';li.textContent=sg[0]}
   else if(curIdx<0){curIdx=i;li.className='cur';li.textContent=sg[0]}});
  document.getElementById('st').textContent=curIdx>=0?('流水线 · '+STAGES[curIdx][0]):'流水线运行中…';
  if(j.done){clearInterval(tmr);
   document.getElementById('st').textContent=j.ok?'完成，正在生成预览…':'失败，详见日志';
   if(j.ok&&j.preview){document.getElementById('prev').innerHTML='<img src="'+j.preview+'">';
    document.getElementById('prev').style.display='block';
    document.getElementById('st').textContent='完成（预览如下，PDF: '+j.pdfpath+')'}
   busy=false;go.disabled=false}}catch(e){}},3000)}
</script>""" + FOOT

# ---------- 建设备（/equip，可执行） ----------
PAGE_EQUIP = (page("", "on", "", "", "", "", "")) + """
<div class="card"><h2><b>归属与命名</b>OWNER</h2>
<div class="frow"><label>ZONE 引用</label><input id="zone" placeholder="设备挂在哪个 ZONE 下，如 /WPC-EQUI">
<button class="btn" onclick="ceTo('zone')">取 CE</button></div>
<div class="frow"><label>设备名称</label><input id="name" placeholder="如 C-201（英文/数字）"></div>
<div class="hint">原语定位相对设备原点；ORI 不设（默认本地 E/N/U=世界），朝向需求写到原语上</div></div>

<div class="card"><h2><b>形体</b>GEOMETRY</h2>
<div class="frow"><label>类型</label><select id="etype" onchange="fmt()">
<option value="cylinder">圆柱体（Cylinder）</option>
<option value="box">箱体（Box）</option>
<option value="vessel">立式罐（筒体+顶盖）</option></select></div>
<div class="frow" id="rPos"><label>定位 E / N / U</label>
<input id="e" value="0" style="flex:1"><input id="n" value="0" style="flex:1"><input id="u" value="0" style="flex:1"></div>
<div class="frow" id="rCyl"><label>直径 / 高 (mm)</label>
<input id="diam" value="3000" style="flex:1"><input id="h" value="8000" style="flex:1"></div>
<div class="frow" id="rBox" style="display:none"><label>长宽高 XYZ (mm)</label>
<input id="xlen" value="2000" style="flex:1"><input id="ylen" value="2000" style="flex:1"><input id="zlen" value="3000" style="flex:1"></div></div>

<button class="go" id="go" onclick="run()">建 立 设 备</button>
<div id="out" style="display:none;margin-top:14px">
<div class="card"><h2><b>执行结果</b>RESULT</h2><div id="msg" style="font-size:14px;line-height:1.9"></div>
<div class="hint" style="margin-top:8px">PML 预览（可复制到 PDMS 命令行手工复跑）</div>
<textarea id="pml" readonly style="min-height:120px;font:12px/1.7 Consolas,monospace"></textarea></div></div>
<script>
let busy=false;
function fmt(){const t=document.getElementById('etype').value;
 document.getElementById('rCyl').style.display=(t==='box')?'none':'flex';
 document.getElementById('rBox').style.display=(t==='box')?'flex':'none';}
function ceTo(id){fetch('/api/ce').then(r=>r.json()).then(j=>{
 if(!j.ok){alert(j.error||'CE 读取失败');return}
 document.getElementById(id).value=j.ref;}).catch(e=>alert('bridge 不可达：'+e))}
async function run(){if(busy)return;const z=document.getElementById('zone').value.trim(),
 nm=document.getElementById('name').value.trim();
 if(!z||!nm){alert('请填 ZONE 引用与设备名称');return}
 busy=true;document.getElementById('go').disabled=true;
 const p={zone:z,name:nm,etype:document.getElementById('etype').value,
  e:document.getElementById('e').value.trim()||'0',n:document.getElementById('n').value.trim()||'0',
  u:document.getElementById('u').value.trim()||'0',
  diam:document.getElementById('diam').value.trim()||'3000',h:document.getElementById('h').value.trim()||'8000',
  xlen:document.getElementById('xlen').value.trim()||'2000',ylen:document.getElementById('ylen').value.trim()||'2000',
  zlen:document.getElementById('zlen').value.trim()||'3000'};
 try{const j=await(await fetch('/api/equip',{method:'POST',headers:{'Content-Type':'application/json'},
  body:JSON.stringify(p)})).json();
  document.getElementById('out').style.display='block';
  document.getElementById('msg').innerHTML=j.ok
   ?'<span class="ok">'+(j.msg||'完成')+'</span>':'<span class="bad">失败：'+(j.error||'')+'</span>';
  document.getElementById('pml').value=j.pml||'';}
 catch(e){alert('请求失败：'+e)}
 busy=false;document.getElementById('go').disabled=false}
</script>""" + FOOT

# ---------- AI 建模（/model 占位） ----------
PAGE_MODEL = (page("", "", "on", "", "", "", "")) + """
<div class="card"><h2><b>AI 建模</b>AI MODELING<span class="badge">规划中</span></h2>
<div class="frow"><label>项目/区域</label><input placeholder="如 /DEPT1/AREA-A"></div>
<div class="frow"><label>建模类型</label><select><option>设备建模（泵/罐/塔）</option>
<option>管道建模（走线自动布管）</option><option>结构建模（梁柱框架）</option></select></div>
<div class="frow"><label>规格/标准</label><input placeholder="如 HG20519、ASME"></div>
<textarea placeholder="自然语言描述，例如：在 AREA-A 建一台立式罐 C-201，直径 3m 高 8m，标高 EL+0.500"></textarea></div>
<button class="go" disabled>建 模（开发中）</button>
<div class="soon">规划：自然语言/参数 → AI 生成 PDMS 元素（设备/管道/结构），复用本平台 CE 引用、
bridge 通道与流水线骨架。建设备页的一行式原语法即先行验证。</div>""" + FOOT

# ---------- 建库（/db） ----------
PAGE_DB = (page("", "", "", "on", "", "", "")) + """
<div class="card"><h2><b>建库</b>CATALOGUE &amp; SPEC<span class="badge">流程页 · 规划中</span></h2>
<div class="step"><span class="no">01</span><span class="t">资料解析 —— 厂家目录 / Piping Datasheet / P&amp;ID 抽取为结构化元件数据（编码规则表解码：标准3+子类型1+连接面1+压力等级1+扩展位）</span></div>
<div class="step"><span class="no">02</span><span class="t">元件库 Catalogue —— Paragon 建库：CATEG / CATE / SCOM 逐级挂接，World 名走 Paragon 通道取名</span></div>
<div class="step"><span class="no">03</span><span class="t">等级库 Spec —— SELEC（TANS 筛选）→ SPCOMPONENT（CATR 引用元件）→ ANSW 管径阶梯</span></div>
<div class="step"><span class="no">04</span><span class="t">校验发布 —— CoCo 连接合法性抽查 / 管径表复核 / 设计库 CATR 引用试挂</span></div>
<div class="frow" style="margin-top:14px"><label>CE 元素查看</label>
<input id="dbref" placeholder="选中等级/元件后取 CE，查看世界名与属性">
<button class="btn" onclick="ceTo()">取 CE</button></div>
<div id="dbout" class="hint"></div></div>
<div class="soon">对接 pdms-database 工作流：编码规则权威表、655 条元件库实录、PdmsBridge catinv 通道。
页面先立流程骨架，接口随后逐段接入。</div>
<script>
function ceTo(){fetch('/api/ce').then(r=>r.json()).then(j=>{
 const o=document.getElementById('dbout');
 if(!j.ok){o.textContent='CE 读取失败：先在 PDMS（Paragon/Design）里选中元素';return}
 document.getElementById('dbref').value=j.ref;
 o.textContent='当前 CE: '+j.ref+'（后续接入 dump_attrs 属性回读）';}).catch(e=>alert('bridge 不可达：'+e))}
</script>""" + FOOT

# ---------- 单线图（/iso 占位） ----------
PAGE_ISO = (page("", "", "", "", "on", "", "")) + """
<div class="card"><h2><b>单线图</b>ISOGEN<span class="badge">规划中</span></h2>
<div class="frow"><label>管线号</label><input placeholder="如 265-PL-0012"></div>
<div class="frow"><label>风格</label><select><option>ISOGEN 风格</option><option>PDS 风格</option></select></div>
<div class="frow"><label>图幅</label><select><option>A3 横版</option><option>A4 横版</option></select></div></div>
<button class="go" disabled>生 成（开发中）</button>
<div class="soon">规划：选管线号 → 抽取管段/管件/焊口 → 单线图渲染 + 材料清单联动，输出 PDF。</div>""" + FOOT

# ---------- 材料表（/mto） ----------
PAGE_MTO = (page("", "", "", "", "", "on", "")) + """
<div class="card"><h2><b>材料表统计</b>MATERIAL REPORT</h2>
<div class="frow"><label>统计范围</label><input id="mtoRoot" placeholder="留空 = 整个 DESIGN 库；选中元素可取 CE">
<button class="btn" onclick="ceTo('mtoRoot')">取 CE</button></div>
<div class="hint">范围内遍历管线元件 → 规格汇总 → 导出 Excel（.xls，UTF-8 中文表头）</div>
<button class="go" id="goMto" onclick="runMto()" style="letter-spacing:8px;margin-top:12px">统 计 材 料</button>
<div id="mtoOut" style="display:none;margin-top:14px"></div></div>
<script>
async function runMto(){const b=document.getElementById('goMto'),o=document.getElementById('mtoOut');
 const root=document.getElementById('mtoRoot').value.trim();
 b.disabled=true;o.style.display='block';
 o.innerHTML='<div class="hint">'+(root?'统计中…（遍历范围管线元件）':'统计中…（全库遍历，稍慢）')+'</div>';
 try{const r=await(await fetch('/api/op',{method:'POST',headers:{'Content-Type':'application/json'},
  body:JSON.stringify({op:'material_report',params:{root:root}})})).json();
  if(r.ok){const parts=(r.output||'').split('|');
   o.innerHTML='<div class="ok" style="font-weight:bold">完成</div><div style="margin-top:6px;white-space:pre-wrap;font-size:13px">'
    +(parts[1]||'').replace(/</g,'&lt;')+'</div><div class="hint">文件：'+(parts[0]||'')+(parts[2]?'（范围：'+parts[2].replace(/</g,'&lt;')+'）':'')+'</div>'}
  else{o.innerHTML='<div class="bad">'+((r.error||'').replace(/</g,'&lt;'))+'</div>'}}
 catch(e){o.innerHTML='<div class="bad">'+e+'</div>'}
 b.disabled=false}
</script>""" + FOOT

# ---------- 无人操控（/control） ----------
PAGE_CONTROL = (page("", "", "", "", "", "", "on")) + """
<div class="card"><h2><b>运行状态</b>RUNTIME</h2>
<div id="env"><div class="hint">读取中…（PDMS 进程 / 插件 DLL / bridge 通道，纯只读）</div></div>
<button class="btn" onclick="loadEnv()" style="margin-top:12px">刷 新 状 态</button></div>

<div class="card"><h2><b>PML 控制台</b>UNATTENDED CONSOLE<span class="badge">逐行下发</span></h2>
<div class="hint">一行一条指令，逐条下发至本地 PdmsBridge（铁律：多条一次下发易 RunInPdms failed）；
默认出错即停；执行期间请勿在 PDMS 里弹模态框，否则宏线程会被挂起等人点确定。<br>
查询指令 Q CE / Q NAME / Q REF / Q TYPE / Q CREF 自动改走 get_ce 通道<b>回读真值</b>
（实证：$M 宏内的 CP 报错不影响 exec 的 ok，回显也不被捕获——别拿 ok 当查询合法性）；
其余查询行照发，但回显在 PDMS 命令窗口。</div>
<textarea id="pml" style="min-height:130px;font:13px/1.7 Consolas,monospace"
  placeholder="Q CE&#10;Q NAME&#10;SAVEWORK"></textarea>
<div class="frow" style="margin-top:10px;flex-wrap:wrap">
 <label>快捷指令</label>
 <button class="btn" onclick="quick('Q CE')">Q CE</button>
 <button class="btn" onclick="quick('Q NAME')">Q NAME</button>
 <button class="btn" onclick="quick('Q ATTRS')">Q ATTRS</button>
 <button class="btn" onclick="quick('SAVEWORK')">SAVEWORK</button>
 <button class="btn" onclick="quick('UPDATE ALL OVERWRITE')">UPDATE ALL OVERWRITE</button>
 <button class="btn" onclick="quick('UPDATE ALL OVERWRITE\\nSAVEWORK')">UPDATE + SAVE</button>
</div>
<div class="frow"><label>出错处理</label>
 <select id="stopErr"><option value="1">出错即停（推荐）</option><option value="0">继续执行后续行</option></select></div>
<button class="go" id="goPml" onclick="runPml()" style="letter-spacing:10px">执 行</button>
<div id="pmlOut" style="display:none;margin-top:12px"></div></div>
<script>
function quick(t){const e=document.getElementById('pml');
 e.value=e.value?e.value.replace(/\\s*$/,'')+'\\n'+t:t;e.focus()}
function esc(s){return String(s||'').replace(/</g,'&lt;')}
async function loadEnv(){const o=document.getElementById('env');
 o.innerHTML='<div class="hint">读取中…</div>';
 try{const j=await(await fetch('/api/env')).json();let h='';
  h+=kv('平台版本',j.platform);
  h+=kv('Bridge 通道',(j.bridge||'').indexOf('online')===0?'<span class="ok">'+j.bridge+'</span>':'<span class="bad">'+j.bridge+'</span>');
  if(j.probe)h+=kv('Probe 回读','<span style="white-space:pre-wrap">'+esc(j.probe)+'</span>');
  if((j.procs||[]).length===0)h+=kv('PDMS 进程','<span class="bad">未检测到（DESIGN/PDMS/E3D/DRAFT）</span>');
  else{h+=kv('PDMS 进程','');(j.procs||[]).forEach(p=>{h+=kv('&nbsp;',esc(p[0])+' pid='+p[1]+' ver='+esc(p[2]||'')+'<br><span class="hint">'+esc(p[3]||'')+'</span>')})}
  if((j.dlls||[]).length===0)h+=kv('插件 DLL','<span class="bad">PDMS 目录未找到 drawingPDMS.dll / FangYuanPDMS.dll</span>');
  else (j.dlls||[]).forEach(d=>{h+=kv(d[0],'ver='+esc(d[1]||'')+' &nbsp; '+d[2]+'<br><span class="hint">'+esc(d[3]||'')+'</span>')});
  if(j.env_err)h+=kv('自检异常',esc(j.env_err));
  o.innerHTML=h;}
 catch(e){o.innerHTML='<div class="bad">'+e+'</div>'}}
function kv(k,v){return '<div class="kv"><span class="k">'+k+'</span><span class="v">'+v+'</span></div>'}
async function runPml(){const code=document.getElementById('pml').value.trim();
 if(!code){alert('请输入 PML 指令');return}
 const b=document.getElementById('goPml');b.disabled=true;
 const o=document.getElementById('pmlOut');
 o.style.display='block';o.innerHTML='<div class="hint">执行中…（逐行下发）</div>';
 try{const r=await(await fetch('/api/pml',{method:'POST',
   headers:{'Content-Type':'application/json'},
   body:JSON.stringify({code:code,stop_on_error:document.getElementById('stopErr').value==='1'})})).json();
  let h='';
  (r.results||[]).forEach(function(x,i){
   h+='<div class="kv"><span class="k">#'+(i+1)+' '+esc(x.line)+'</span><span class="v'+
      (x.ok?' ok':' bad')+'">'+(x.ok?'OK':'FAIL')+
      (x.output?'<br><span style="white-space:pre-wrap">'+esc(x.output)+'</span>':'')+
      (x.error?'<br><span class="bad">'+esc(x.error)+'</span>':'')+'</span></div>'});
  h+='<div class="hint" style="margin-top:8px">'+(r.ok?'<span class="ok">全部执行完成</span>':'<span class="bad">存在失败行，见上</span>')+'</div>';
  o.innerHTML=h;}
 catch(e){o.innerHTML='<div class="bad">'+e+'</div>'}
 b.disabled=false}
loadEnv();
</script>""" + FOOT

# ---------------- 后端（原 v3 逻辑保留） ----------------

def bridge_req(req, timeout=300):
    s = socket.create_connection(("127.0.0.1", PORT_BRIDGE), timeout=timeout)
    s.sendall((json.dumps(req) + "\n").encode("utf-8"))
    s.settimeout(timeout - 5)
    buf = b""
    while b"\n" not in buf:
        c = s.recv(65536)
        if not c: break
        buf += c
    s.close()
    return json.loads(buf.split(b"\n", 1)[0].decode("utf-8", "replace"))

def make_preview(sheet):
    """流水线末站：首页渲染 png → base64（PDF 已由流水线导出并黑白化）。"""
    try:
        import pypdfium2 as pdfium
        pdf = os.path.join(OUTDIR, "preview.pdf")
        page = pdfium.PdfDocument(pdf)[0]
        img = page.render(scale=1.4).to_pil()
        png = os.path.join(OUTDIR, "preview.png")
        img.save(png)
        with open(png, "rb") as f:
            return "data:image/png;base64," + base64.b64encode(f.read()).decode(), pdf
    except Exception:
        return None, None

PIPE = {"running": False, "log": [], "done": False, "ok": False, "preview": None, "pdf": None}

def pipeline(params):
    """出图流水线：出图 → 落盘 → 导 PDF → 黑白 → 预览。全自动一键到底。"""
    try:
        PIPE["log"] = []
        def step(msg):
            PIPE["log"].append("[%s] %s" % (time.strftime("%H:%M:%S"), msg))
            print("[pipe]", msg)
        step("流水线 1/5：replace_symbols 出图（约 5-15 分钟）…")
        r = bridge_req({"id": int(time.time()), "kind": "op", "op": "replace_symbols",
                        "params": {k: str(v) for k, v in params.items()}}, timeout=3600)
        if not r.get("ok"): raise Exception("出图失败: " + r.get("error", ""))
        # (10j) 设备布置图新建 Sheet 页：出图日志带 [NEWSHEET] 标记 → PDF 导出/预览切到新页
        sheet_pdf = params.get("sheet", "")
        out1 = r.get("output", "") or ""
        ti = out1.find("[NEWSHEET]")
        if ti >= 0:
            sheet_pdf = out1[ti + len("[NEWSHEET]"):].split()[0].strip()
            step("设备布置图新页: " + sheet_pdf)
        step("流水线 2/5：SAVEWORK 落盘…")
        r = bridge_req({"id": int(time.time()) + 1, "kind": "op", "op": "exec_cmds",
                        "params": {"cmds": "SAVEWORK"}}, 120)
        if not r.get("ok"): raise Exception("落盘失败")
        step("流水线 3/5：导出 PDF…")
        os.makedirs(OUTDIR, exist_ok=True)
        r = bridge_req({"id": int(time.time()) + 2, "kind": "op", "op": "export_pdf",
                        "params": {"sheet": sheet_pdf, "outDir": OUTDIR + os.sep}}, 280)
        if not r.get("ok"): raise Exception("导 PDF 失败: " + r.get("error", ""))
        src = os.path.join(OUTDIR, params.get("sheet", "=x").replace("/", "-").lstrip("=") + ".pdf")
        cand = [f for f in os.listdir(OUTDIR) if f.endswith(".pdf") and f != "preview.pdf"]
        if not cand: raise Exception("PDF 未生成")
        src = os.path.join(OUTDIR, cand[0])
        pdf = os.path.join(OUTDIR, "preview.pdf")
        if os.path.exists(pdf): os.remove(pdf)
        os.rename(src, pdf)
        step("流水线 4/5：黑白处理…")
        try:
            subprocess.run([PYMONO, MONO, pdf], timeout=120, capture_output=True)
            step("[WIZ] MONO-OK")
        except Exception:
            step("[WIZ] MONO-SKIP（黑白失败，保留彩色）")
        step("流水线 5/5：生成预览… [WIZ] EXPORT-OK")
        pv, pd = make_preview(params.get("sheet", ""))
        PIPE["preview"], PIPE["pdf"] = pv, pd
        PIPE["ok"] = True
        step("流水线完成")
    except Exception as e:
        PIPE["ok"] = False
        PIPE["log"].append("X " + str(e))
    finally:
        PIPE["done"] = True

class H(BaseHTTPRequestHandler):
    def log_message(self, *a): pass
    def _send(self, code, body, ctype="application/json; charset=utf-8"):
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)
    def do_GET(self):
        if self.path.startswith("/api/ce"):
            try:
                r = bridge_req({"id": int(time.time()), "kind": "op", "op": "get_ce", "params": {}}, 30)
                if r.get("ok"):
                    ref = r.get("output", "").split("|")[0].strip()
                    self._send(200, json.dumps({"ok": True, "ref": ref}).encode("utf-8"))
                else:
                    self._send(200, json.dumps({"ok": False, "error": r.get("error", "")}).encode("utf-8"))
            except Exception as e:
                self._send(200, json.dumps({"ok": False, "error": str(e)}).encode("utf-8"))
        elif self.path.startswith("/api/env"):
            self._send(200, json.dumps(env_report(), ensure_ascii=False).encode("utf-8"))
        elif self.path.startswith("/api/log"):
            tail = ""
            try:
                with open(LOG, "r", encoding="utf-8", errors="replace") as f:
                    lines = f.readlines()
                st_i = max((i for i, l in enumerate(lines) if "RSYM-START" in l), default=None)
                if st_i is not None: tail = "".join(lines[st_i:][-40:])
            except Exception: pass
            extra = "".join(PIPE["log"])
            self._send(200, json.dumps({"log": tail + "\n" + extra if extra else tail,
                                        "done": PIPE["done"], "ok": PIPE["ok"],
                                        "preview": PIPE["preview"], "pdfpath": PIPE["pdf"]}).encode("utf-8"))
        elif self.path.startswith("/equip"): self._send(200, PAGE_EQUIP.encode("utf-8"), "text/html; charset=utf-8")
        elif self.path.startswith("/model"): self._send(200, PAGE_MODEL.encode("utf-8"), "text/html; charset=utf-8")
        elif self.path.startswith("/db"): self._send(200, PAGE_DB.encode("utf-8"), "text/html; charset=utf-8")
        elif self.path.startswith("/iso"): self._send(200, PAGE_ISO.encode("utf-8"), "text/html; charset=utf-8")
        elif self.path.startswith("/mto"): self._send(200, PAGE_MTO.encode("utf-8"), "text/html; charset=utf-8")
        elif self.path.startswith("/version"): self._send(200, PAGE_VERSION.encode("utf-8"), "text/html; charset=utf-8")
        else: self._send(200, PAGE_DRAW.encode("utf-8"), "text/html; charset=utf-8")
    def do_POST(self):
        ln = int(self.headers.get("Content-Length", 0))
        body = json.loads(self.rfile.read(ln).decode("utf-8")) if ln else {}
        if self.path == "/api/start":
            if PIPE["running"]:
                self._send(200, json.dumps({"ok": False, "error": "流水线已在运行"}).encode("utf-8")); return
            PIPE.update({"running": True, "done": False, "ok": False, "preview": None, "pdf": None, "log": []})
            note = body.pop("note", None)
            if note:
                try:
                    with open(LOG, "a", encoding="utf-8") as f:
                        f.write("[USER NOTE] %s | view=%s sheet=%s | %s\n" %
                                (time.strftime("%Y-%m-%d %H:%M:%S"), body.get("view", ""), body.get("sheet", ""), note.replace("\n", " / ")))
                except Exception: pass
            threading.Thread(target=pipeline, args=(dict(body),), daemon=True).start()
            self._send(200, b'{"ok":true}')
        elif self.path == "/api/equip":
            res = run_equipment(body)
            self._send(200, json.dumps(res, ensure_ascii=False).encode("utf-8"))
        elif self.path == "/api/pml":
            res = exec_pml_lines(str(body.get("code", "")), bool(body.get("stop_on_error", True)))
            self._send(200, json.dumps(res, ensure_ascii=False).encode("utf-8"))
        elif self.path == "/api/op":
            try:
                r = bridge_req({"id": int(time.time()), "kind": "op",
                                "op": body.get("op", ""),
                                "params": {k: str(v) for k, v in (body.get("params") or {}).items()}}, 3600)
                self._send(200, json.dumps(r, ensure_ascii=False).encode("utf-8"))
            except Exception as e:
                self._send(200, json.dumps({"ok": False, "error": str(e)}).encode("utf-8"))

if __name__ == "__main__":
    port = 18760
    if "--port" in sys.argv: port = int(sys.argv[sys.argv.index("--port") + 1])
    print("pdmsDrawing 平台 %s: http://127.0.0.1:%d" % (PLATFORM_VER, port))
    threading.Timer(1.0, lambda: webbrowser.open("http://127.0.0.1:%d/draw" % port)).start()
    HTTPServer(("127.0.0.1", port), H).serve_forever()
