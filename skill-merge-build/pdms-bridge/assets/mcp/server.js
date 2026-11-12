#!/usr/bin/env node
/*
 * pdms-bridge-mcp — PDMS 操作连接器（MCP server, stdio 传输）
 *
 * 让智能体（WorkBuddy / 任意 MCP client）直接驱动 AVEVA PDMS 12.1.SP4：
 *   - 发送 PML / PDMS 命令到正在运行的 PDMS 实例
 *   - 查询数据库、创建图纸(SHEET/VIEW/DLLB)、列绘制清单
 *   - 参数化创建设备（22 类，移植方圆 TestAttribute 代码逻辑）+ 反向读取设备/管嘴表
 *   - ISO 等轴测批量出图（Isodraft 选项文件驱动）
 *
 * 传输：stdio(JSON-RPC 2.0, 换行分隔) <-> 本地 TCP 监听器
 * PDMS 侧监听器由 PdmsBridge.cs（drawingPDMS 插件）提供，
 * 或由 AGENTBRIDGE.PML（文件轮询兜底，无需重编译插件）提供。
 *
 * 环境变量：
 *   PDMS_BRIDGE_HOST   默认 127.0.0.1
 *   PDMS_BRIDGE_PORT   默认 18753
 *   PDMS_BRIDGE_RESULT 结果文件路径（PML 写结果用），默认 D:/pdmsAgent/bridge/result.txt
 */
'use strict';
const net = require('net');

const HOST = process.env.PDMS_BRIDGE_HOST || '127.0.0.1';
const PORT = parseInt(process.env.PDMS_BRIDGE_PORT || '18753', 10);
const RESULT_FILE = process.env.PDMS_BRIDGE_RESULT || ((process.env.PDMS_BRIDGE_DIR || 'D:/pdmsAgent/bridge') + '/result.txt');
const TIMEOUT_MS = parseInt(process.env.PDMS_BRIDGE_TIMEOUT || '20000', 10);
const TRANSPORT = (process.env.PDMS_BRIDGE_TRANSPORT || 'tcp').toLowerCase();
const BRIDGE_DIR = process.env.PDMS_BRIDGE_DIR || 'D:/pdmsAgent/bridge';
const QUEUE_DIR = process.env.PDMS_BRIDGE_QUEUE || (BRIDGE_DIR + '/queue');
const DONE_DIR = process.env.PDMS_BRIDGE_DONE || (BRIDGE_DIR + '/done');
const fs = require('fs');

/* ----------------------------- 多模块发现（Design/Paragon/Draft 同时打开） -----------------------------
 * 每个 PDMS 模块进程(drawingPDMS 插件)启动时按自身进程名判定模块，监听确定性端口：
 *   DESIGN=18753, PARAGON=18754, DRAFT=18755, MONITOR=18756, ISO=18757
 * 并在 <BRIDGE_DIR>/instances/<module>_<pid>.json 写发现清单。
 * 连接器据此按模块选端口，支持多模块并存时精准定位目标（pdms_select_module 可手动切换）。
 */
function resolveBridgeDir() {
  if (process.env.PDMS_BRIDGE_DIR) return process.env.PDMS_BRIDGE_DIR.replace(/\\/g, '/');
  for (const c of ['C:/pdmsAgent/bridge', 'D:/pdmsAgent/bridge']) {
    try { if (fs.existsSync(c)) return c; } catch (_) {}
  }
  return 'C:/pdmsAgent/bridge';
}
function readManifests() {
  const dir = resolveBridgeDir() + '/instances';
  const out = [];
  try {
    if (!fs.existsSync(dir)) return out;
    for (const f of fs.readdirSync(dir)) {
      if (!f.endsWith('.json')) continue;
      try { const j = JSON.parse(fs.readFileSync(dir + '/' + f, 'utf8')); if (j && j.module && j.port) out.push(j); } catch (_) {}
    }
  } catch (_) {}
  return out;
}
function probePort(host, port) {
  return new Promise((res) => {
    const s = net.createConnection({ host: host, port: port });
    let done = false;
    const fin = (v) => { if (!done) { done = true; try { s.destroy(); } catch (_) {} res(v); } };
    s.setTimeout(400);
    s.on('connect', () => fin(true));
    s.on('timeout', () => fin(false));
    s.on('error', () => fin(false));
  });
}
let _instCache = { at: 0, list: [] };
const INST_TTL = 2000;
async function discoverInstances() {
  const now = Date.now();
  if (now - _instCache.at < INST_TTL) return _instCache.list;   // 2s 缓存，避免每个调用都探活(每端口 400ms)
  const raw = readManifests();
  const live = [];
  await Promise.all(raw.map(async (j) => { if (await probePort(HOST, j.port)) live.push(j); }));
  _instCache = { at: now, list: live };
  return live;
}
// 解析本次连接目标：
//   1) PDMS_BRIDGE_PORT 显式端口（向后兼容，最高优先）
//   2) state.targetModule 或 PDMS_BRIDGE_MODULE 指定模块 → 取该模块清单端口
//   3) 仅一个实例 → 用其端口
//   4) 多个 → 优先 DESIGN，否则第一个
//   5) 无清单 → 回退 legacy 18753
async function resolveTarget() {
  if (process.env.PDMS_BRIDGE_PORT) {
    return { host: HOST, port: parseInt(process.env.PDMS_BRIDGE_PORT, 10), module: null, via: 'env-port' };
  }
  const inst = await discoverInstances();
  const modEnv = (process.env.PDMS_BRIDGE_MODULE || '').trim().toUpperCase();
  const want = (state && state.targetModule) ? state.targetModule : modEnv;
  if (want) {
    const hit = inst.filter(i => String(i.module).toUpperCase() === want)[0];
    if (hit) return { host: HOST, port: hit.port, module: hit.module, via: 'module:' + want };
  }
  if (inst.length === 1) return { host: HOST, port: inst[0].port, module: inst[0].module, via: 'single' };
  const design = inst.filter(i => i.module === 'DESIGN')[0];
  if (design) return { host: HOST, port: design.port, module: 'DESIGN', via: 'default-design' };
  if (inst.length) return { host: HOST, port: inst[0].port, module: inst[0].module, via: 'first' };
  return { host: HOST, port: PORT, module: null, via: 'legacy' };
}
const state = { targetModule: (process.env.PDMS_BRIDGE_MODULE || '').trim().toUpperCase() || null };

function log(...a) { process.stderr.write('[pdms-bridge] ' + a.join(' ') + '\n'); }
function sleep(ms) { return new Promise(r => setTimeout(r, ms)); }
function ensureDir(p) { try { if (!fs.existsSync(p)) fs.mkdirSync(p, { recursive: true }); } catch (_) {} }

/* ----------------------------- 安全护栏 + 串行队列（2026-10-14 P0，MCP-GENERIC-TOOLS.md §4） ----------------------------- */
const BRIDGE_PATH_PREFIX = (process.env.PDMS_BRIDGE_DIR || 'D:/pdmsAgent/bridge').replace(/\\/g, '/').toUpperCase() + '/'; // openfile 写路径沙箱（与 BRIDGE_DIR 同源，统一大写比较；2026-10-17 修复：原硬编码 'C:/PDMSAGENT/BRIDGE/' 与默认桥接目录 D:/pdmsAgent/bridge 不一致，导致所有合法 openfile 被护栏误杀）
const EXEC_BLOCKLIST = [
  { re: /^\s*QUIT\b/i,    why: 'QUIT 会结束 PDMS 会话' },
  { re: /^\s*SHUTDOWN\b/i, why: 'SHUTDOWN 会关闭 PDMS' },
  { re: /^\s*DELETE\s+REGI\b/i, why: '注册表级删除，须经 pdms_delete（confirm+快照）流程' }
];

/** 返回 null = 放行；返回字符串 = 拒绝原因。逐行检查 CP 命令 + openfile 路径沙箱。 */
function checkExecGuardrails(text) {
  for (const line of text.split(/\r?\n/)) {
    for (const { re, why } of EXEC_BLOCKLIST) {
      if (re.test(line)) return `命中黑名单「${line.trim()}」：${why}`;
    }
    if (/^\s*SET\s+MDB\b/i.test(line)) {
      return 'SET MDB（切库）为高危操作，pdms_exec 通道禁用（需按 ROADMAP 由用户显式决策）';
    }
  }
  const re = /openfile\s+\/?([A-Za-z]:[\\/][^\s|]+)/gi;
  let m;
  while ((m = re.exec(text)) !== null) {
    const p = m[1].replace(/\\/g, '/').toUpperCase();
    if (!p.startsWith(BRIDGE_PATH_PREFIX)) {
      return `openfile 目标路径在沙箱外（必须位于 ${BRIDGE_DIR}/ 下）：${m[1]}`;
    }
  }
  return null;
}

// 单 RESULT_FILE 被所有调用共享；MCP 客户端（WorkBuddy 多 Agent）可并发调用 → 必须串行，否则串话。
let serialChain = Promise.resolve();
function runSerial(fn) {
  const p = serialChain.then(fn);
  serialChain = p.catch(() => {});
  return p;
}

/* ----------------------------- 传输 A：TCP（需插件重编译 + PDMS_BRIDGE 符号） -----------------------------
 * 请求两种形态：
 *   { id, code, kind:'exec' }  → PDMS 侧 $M 下发 PML/CP 文本（原通道）
 *   { id, op, params, kind:'op' } → PDMS 侧直调 C# 命名操作（HeadlessDrawing：
 *     probe_read / create_drawing / export_dxf / export_pdf，在 PDMS UI 线程执行）
 */
function callTcpPayload(payload, timeoutMs, target) {
  return new Promise((resolve) => {
    const TO = timeoutMs || TIMEOUT_MS;
    const host = (target && target.host) || HOST;
    const port = (target && target.port) || PORT;
    let sock;
    try {
      sock = net.createConnection(port, host);
    } catch (e) {
      return resolve({ ok: false, error: 'connect failed: ' + e.message, output: '' });
    }
    let buf = '';
    let settled = false;
    const finish = (r) => { if (!settled) { settled = true; clearTimeout(timer); try { sock.end(); } catch (_) {} resolve(r); } };
    const timer = setTimeout(() => finish({ ok: false, error: 'PDMS bridge timeout (>=' + TO + 'ms)', output: '' }), TO);

    sock.on('error', (err) => finish({ ok: false, error: 'PDMS bridge offline: ' + err.message, output: '' }));
    sock.on('connect', () => {
      sock.write(JSON.stringify(payload) + '\n');
    });
    sock.on('data', (d) => {
      buf += d.toString();
      let idx;
      while ((idx = buf.indexOf('\n')) >= 0) {
        const line = buf.slice(0, idx); buf = buf.slice(idx + 1);
        if (!line.trim()) continue;
        try {
          const resp = JSON.parse(line);
          return finish({ ok: resp.ok !== false, output: resp.output || '', error: resp.error || null });
        } catch (_) { /* 继续读下一行 */ }
      }
    });
    sock.on('close', () => {
      if (buf.trim()) {
        try { const resp = JSON.parse(buf); return finish({ ok: resp.ok !== false, output: resp.output || '', error: resp.error || null }); } catch (_) {}
      }
      finish({ ok: false, error: 'PDMS bridge closed without response', output: '' });
    });
  });
}
function callTcp(code) {
  return resolveTarget().then(t => callTcpPayload({ id: 1, code: String(code || ''), kind: 'exec' }, TIMEOUT_MS, t));
}
// kind:"op" 直调 C# 操作（默认 10 分钟超时——create_drawing 含标注块 + UPDATE all 可能数分钟）
function callOp(op, params, timeoutMs) {
  return resolveTarget().then(t => callTcpPayload({ id: 1, kind: 'op', op: op, params: params || {} }, timeoutMs || 600000, t));
}

/* ----------------------------- 传输 B：文件轮询（无需重编译插件，加载 AGENTBRIDGE.PML 即可） ----------------------------- */
function callFile(code) {
  ensureDir(QUEUE_DIR); ensureDir(DONE_DIR);
  const id = Date.now() + '-' + Math.floor(Math.random() * 1000);
  const reqPath = QUEUE_DIR + '/' + id + '.req';
  const donePath = DONE_DIR + '/' + id + '.res';
  try { fs.writeFileSync(reqPath, String(code || ''), 'utf8'); }
  catch (e) { return Promise.resolve({ ok: false, error: 'write queue failed: ' + e.message, output: '' }); }
  const start = Date.now();
  return new Promise((resolve) => {
    const tick = () => {
      if (fs.existsSync(donePath)) {
        try {
          let out = fs.readFileSync(donePath, 'utf8');
          let ok = true, error = null;
          const nl = out.indexOf('\n');
          if (nl >= 0) {
            const head = out.slice(0, nl).trim();
            if (head === 'ERR') { ok = false; error = out.slice(nl + 1).trim(); out = ''; }
            else if (head === 'OK') { out = out.slice(nl + 1); }
          }
          try { fs.unlinkSync(donePath); } catch (_) {}
          try { fs.unlinkSync(reqPath); } catch (_) {}
          return resolve({ ok, output: out, error });
        } catch (e) { return resolve({ ok: false, error: 'read done failed: ' + e.message, output: '' }); }
      }
      if (Date.now() - start > TIMEOUT_MS) {
        try { fs.unlinkSync(reqPath); } catch (_) {}
        return resolve({ ok: false, error: 'PDMS file-bridge timeout (>=' + TIMEOUT_MS + 'ms). 请确认 PDMS 已加载 AGENTBRIDGE.PML', output: '' });
      }
      setTimeout(tick, 300);
    };
    tick();
  });
}

function callPdms(code) {
  return TRANSPORT === 'file' ? callFile(code) : callTcp(code);
}

/* ----------------------------- 高层工具：生成 PML（12.1.SP4 已验证语法） -----------------------------
 * 以下生成的 PML 全部采用 2026-10-14 TTY 全链路实测通过的语法（证据链见 D:\pdmsAgent\view_api\）：
 *   - 结果落盘: openfile <path> write !v / handle ANY → overwrite / writefile $!v |...$!var...| / closefile $!v
 *     （writefile 缓冲到 closefile；write=仅新文件、overwrite=仅已存在，故 write+handle 双形式兼容两种情况）
 *   - 建链: CE /* → NEW REGI/DRWG/SHEE/VIEW(+属性续行 VSCA/VTYP/SIZE/XYPS/DIR, 无等号)
 *           → NEW LIBY/DLLB/IDLI/TRLB → END 与 NEW 一一对应
 *   - 回读: !!ce.type/.vsca/.vtyp/.dbType/.name（handle ANY 吞错保护，READFAIL 兜底值）
 *   - 引用: drawing-rooted（/DRWG/SHEET/VIEW，不含 registry 名；registry-rooted 会报 (2,109)）
 *   - 陷阱: PML 里 ! 是变量前缀不是逻辑非（否定用 .not()）；一句一行；无 CREATE/DO/MESSAGE；
 *           路径用"前导斜杠+反斜杠"形式（openfile 已验证）
 */
function pmlPath(jsPath) {
  // 'D:/x/y' → '/D:\x\y'
  return '/' + String(jsPath).replace(/\//g, '\\');
}
// 结果文件打开（write 失败则 overwrite —— cdxattdump.pmlfrm 官方先例）
function pmlResultOpen() {
  const p = pmlPath(RESULT_FILE);
  return [
    'openfile ' + p + ' write !F1',
    'handle ANY',
    '  openfile ' + p + ' overwrite !F1',
    'endhandle'
  ];
}
// 体积范围串（W/N/U 世界坐标，WITHIN 子句格式；ADD ALL 尚未实测，默认关闭）
function volumeBox(r) {
  return `W${r.xmin} N${r.ymin} U${r.zmin} TO W${r.xmax} N${r.ymax} U${r.zmax}`;
}

function pmlCreateDrawing(a) {
  const regi  = a.regi   || (a.drawing ? 'AGT_' + a.drawing : 'AGT_REGI');
  const dwg   = a.drawing || 'AGT001';
  const sheet = a.sheet  || 'S1';
  const view  = a.view   || 'V1';
  const scale = a.scale  || 50;               // VSCA = 1/scale（0.02 = 1:50，A0 黄金链取值）
  const size  = a.size   || [1189, 841];      // A0
  const xy    = a.xy     || [500, 400];
  const lines = pmlResultOpen().concat([
    'writefile $!F1 |===CHUTU-START ' + dwg + '/' + sheet + '/' + view + '===|',
    'CE /*',
    '-- registry 已存在则直接进去，不存在才建（避免重名 (41,x) 中止）',
    '!F2 = false',
    'CE /' + regi,
    'handle ANY',
    '  !F2 = true',
    'endhandle',
    'if (!F2) then',
    '  NEW REGI /' + regi,
    'endif',
    'NEW DRWG /' + dwg,
    'NEW SHEE /' + dwg + '/' + sheet,
    'NEW VIEW /' + dwg + '/' + sheet + '/' + view,
    'VSCA  ' + (1 / scale),
    'VTYP  Universal',
    'SIZE  ' + size[0] + ' ' + size[1],
    'XYPS  X' + xy[0] + ' Y' + xy[1],
    'DIR   D',
    'END',
    'END',
    'NEW LIBY /' + dwg + '/LIBY',
    'NEW DLLB /' + dwg + '/DRAWLIST',
    'NEW IDLI /' + dwg + '/DRAWLIST/DRWG',
    'END',
    'END',
    'NEW TRLB /' + dwg + '/TAGRULE',
    'END',
    'END',
    'END',
    'END',
    'CE /' + dwg + '/' + sheet + '/' + view
  ]);
  // 可选：体积框选加入绘制清单（ADD ALL 未实测，默认关；先验证基础链再开）
  if (a.addAll && a.range) {
    lines.push('ADD ALL ' + (a.addTypes || 'BRAN EQUIP STEEL') + ' WITHIN ' + volumeBox(a.range));
  }
  // 回读验证（READFAIL 兜底 + handle 吞错，任何单点失败不拖垮落盘）
  lines.push(
    '!T1 = READFAIL',
    '!T1 = !!ce.type',
    'handle ANY',
    'endhandle',
    '!R1 = READFAIL',
    '!R1 = !!ce.vsca',
    'handle ANY',
    'endhandle',
    '!S1 = READFAIL',
    '!S1 = !!ce.vtyp',
    'handle ANY',
    'endhandle',
    '!B1 = READFAIL',
    '!B1 = !!ce.dbType',
    'handle ANY',
    'endhandle',
    'writefile $!F1 |VIEW_TYPE_$!T1|',
    'writefile $!F1 |VIEW_VSCA_$!R1|',
    'writefile $!F1 |VIEW_VTYPE_$!S1|',
    'writefile $!F1 |VIEW_DB_$!B1|',
    'writefile $!F1 |===CHUTU-OK===|',
    'closefile $!F1'
  );
  return lines.join('\n');
}

/* ----------------------------- pdms_iso_generate PML（2026-10-14 TTY 全链路实测通过） -----------------------------
 * 链路（每管线一轮，与旧 E3D Design 端 button2 同构）：
 *   CE <pipe> → ISODRAFTMODE → options default → $M <optionsFile> → DETAIL <TYPE> <NAME>
 *   → isodraw status/number/plotfile 回读 → EXIT
 * 零 UDA / 零服务器 / 零 XML：optionsFile 用自带 Standard iso（%SAMISO%/std/previewisostd.txt）；
 * DXF 输出 = 选项文件把 File 行改为 "File AND DXF <名> SINGLE"（实测 82KB dxf）。
 * 坑（TTY 踩实，详见 D:\pdmsAgent\ISO-PLAN.md §6）：变量名大小写不敏感；每管线独立变量集；
 * 宏内 CP 行禁 mess/禁裸盘符路径（本 PML 走进程内桥，路径以 TTY 验证形式为准：$M 用 "%ENV%/..." 双引号）。
 */
function pmlIsoGenerate(a) {
  const raw = (a.pipes || []).map(s => String(s).trim()).filter(Boolean);
  const pipes = raw.slice(0, a.maxPipes || 10).map(s => (s.charAt(0) === '/' ? s : '/' + s));
  if (!pipes.length) return null;
  const optFile = String(a.optionsFile || '%SAMISO%/std/previewisostd.txt');
  const lines = pmlResultOpen().concat([
    '!OPTF = ' + JSON.stringify(optFile).replace(/"/g, "'"),
    'writefile $!F1 |===ISO-START pipes_' + pipes.length + ' opt_' + optFile + '===|'
  ]);
  pipes.forEach((p, i) => {
    const n = i + 1;
    lines.push(
      '!CE' + n + ' = false',
      'CE ' + p,
      'handle ANY',
      '  !CE' + n + ' = true',
      'endhandle',
      'if (!CE' + n + ') then',
      '  writefile $!F1 |PIPE' + n + ' ' + p + ' STATUS=CE_FAIL|',
      'else',
      '  ISODRAFTMODE',
      '  handle ANY',
      '  endhandle',
      '  options default',
      '  $M "$!OPTF"',
      '  handle ANY',
      '  endhandle',
       // 与 v10 验证宏逐行同构：DETAIL <TYPE> <裸名>（!!ce.name，无斜杠）；
       // 失败不写 DETAIL_FAIL（避免与回读行重复），回读自然落到 UNSET/0/ERR
       '!PT' + n + " = 'UNK'",
       '!PT' + n + ' = !!ce.type',
       '  handle ANY',
       '  endhandle',
       '!PN' + n + " = 'UNK'",
       '!PN' + n + ' = !!ce.name',
       '  handle ANY',
       '  endhandle',
       '  DETAIL $!PT' + n + ' $!PN' + n,
       '  handle ANY',
       '  endhandle',
      '  var !ST' + n + ' isodraw status',
      '  handle ANY',
      '    !ST' + n + " = 'UNSET'",
      '  endhandle',
      '  var !NP' + n + ' isodraw number',
      '  handle ANY',
      '    !NP' + n + " = '0'",
      '  endhandle',
      '  var !PF' + n + ' isodraw plotfile 1 filename',
      '  handle ANY',
      '    !PF' + n + " = 'ERR'",
      '  endhandle',
      '  writefile $!F1 |PIPE' + n + ' ' + p + ' STATUS=$!ST' + n + ' PLOTS=$!NP' + n + ' FILE=$!PF' + n + '|',
      '  EXIT',
      '  handle ANY',
      '  endhandle',
      'endif'
    );
  });
  lines.push('writefile $!F1 |===ISO-END===|', 'closefile $!F1');
  return lines.join('\n');
}

function parseIsoResult(text) {
  const out = [];
  for (const line of String(text || '').split(/\r?\n/)) {
    const m = line.match(/^PIPE\d+\s+(\S+)\s+STATUS=([^\s|]+)(?:\s+PLOTS=(\S+))?(?:\s+FILE=([^|]+))?/);
    if (m) out.push({ pipe: m[1], status: m[2], plots: m[3] || null, plotfile: (m[4] || '').trim() || null });
  }
  return out;
}

/* ----------------------------- 高层工具：参数化设备（移植自方圆 TestAttribute 插件代码逻辑） -----------------------------
 * 来源: D:\newRepos\TestAttribute - 12.1.sp4\TestAttribute\BuildOne.cs（23 构造器，22 移植）
 *       + TowerImpl.cs + Attribute.cs（UI 默认值）。权威参数表 =
 *       skills/pdms/parametric-equipment/references/equipment-catalog.md（含源码行号）。
 * CP 语法证据: 官方 router_tutorial.dat L591-630（NEW EQUI 嵌套基本体、AT 符号轴、ORIA、CATR、HEIG）。
 * ORI 字符串（'Y is <d> and Z is <d>'）→ ORIA 三元组: 组合序 E→N→U（外轴，dat 6 个映射全复现），
 *   90° 步进穷举解 24 朝向群；无解时退回 ORI 字符串 [首跑 E4]。
 * [未验证·首跑 E1-E7] $M 进程内下发 CP 行、嵌套 NEW 块、FUNC 类、CATR±SPCO、属性回读——
 *   见 references/cp-syntax.md §5；总闸 = ROADMAP §2 步骤 3（^^ 污染判定）。
 */
const ORI_DIRS = { E: [1, 0, 0], W: [-1, 0, 0], N: [0, 1, 0], S: [0, -1, 0], U: [0, 0, 1], D: [0, 0, -1] };
function rotE(a, v) { const c = Math.cos(a), s = Math.sin(a); return [v[0], c * v[1] - s * v[2], s * v[1] + c * v[2]]; }
function rotN(a, v) { const c = Math.cos(a), s = Math.sin(a); return [c * v[0] + s * v[2], v[1], -s * v[0] + c * v[2]]; }
function rotU(a, v) { const c = Math.cos(a), s = Math.sin(a); return [c * v[0] - s * v[1], s * v[0] + c * v[1], v[2]]; }
// 'Y is S and Z is W' → [aE,aN,aU]（度）；null = 无解（退回 ORI 字符串）。
// 欧拉表示不唯一（如 'Y IS D AND Z IS N' = [-90,0,0] = [90,180,180]）→ 取 |aE|+|aN|+|aU| 最小的标准形
// （官方 dat 的 6 组映射均收敛到该标准形）。
function oriToOria(oriStr) {
  const m = String(oriStr || '').toUpperCase().match(/Y\s+IS\s+([EWNSUD])\s+AND\s+Z\s+IS\s+([EWNSUD])/);
  if (!m) return null;
  const yT = ORI_DIRS[m[1]], zT = ORI_DIRS[m[2]];
  if (!yT || !zT) return null;
  const A = Math.PI / 180, EPS = 1e-9;
  let best = null, bestCost = Infinity;
  for (const aE of [0, 90, -90, 180]) for (const aN of [0, 90, -90, 180]) for (const aU of [0, 90, -90, 180]) {
    const y = rotU(aU * A, rotN(aN * A, rotE(aE * A, [0, 1, 0])));
    const z = rotU(aU * A, rotN(aN * A, rotE(aE * A, [0, 0, 1])));
    if (Math.abs(y[0] - yT[0]) < EPS && Math.abs(y[1] - yT[1]) < EPS && Math.abs(y[2] - yT[2]) < EPS &&
        Math.abs(z[0] - zT[0]) < EPS && Math.abs(z[1] - zT[1]) < EPS && Math.abs(z[2] - zT[2]) < EPS) {
      const cost = Math.abs(aE) + Math.abs(aN) + Math.abs(aU);
      if (cost < bestCost) { bestCost = cost; best = [aE, aN, aU]; }
    }
  }
  return best;
}
function cpNum(v) { return (Math.round(Number(v) * 1000) / 1000).toFixed(3); }
// 符号轴定位: 负值换字母（-1150 → W1150，官方 dat 同款），0 → E0.000 形式
function cpAt(x, y, z) {
  const s = (v, pos, neg) => (v >= 0 ? pos : neg) + cpNum(Math.abs(v));
  return 'AT  ' + s(x, 'E', 'W') + '  ' + s(y, 'N', 'S') + '  ' + s(z, 'U', 'D');
}
function eqPart(name, type, at, dims, ori) { return { name, type, at, dims: dims || {}, ori: ori || null }; }
// 附加管嘴径向方位（0=N 90=E 180=S 270=W，水平向外；非四正方向 v1 不自动给 ORI）
const RADIAL_ORIA = { 0: [-90, 0, 0], 90: [0, 90, 0], 180: [90, 0, 0], 270: [0, -90, 0] };

/* 22 类设备目录（转录自 BuildOne.cs/TowerImpl.cs，公式与 vendor 一致；约定见 equipment-catalog.md）
 * params: [key, required(1/0), default]；DISH DIAM=直径+1（焊口线）、THIC=50、管嘴 HEIG=200 均按代码原样 */
const EQUIP_CATALOG = {
  /* ---------- 水平设备: 轴向沿 N, 原点 = 前封头中心 ---------- */
  horizontal_vessel: {
    desc: '水平容器（双封头+双鞍座）', src: 'BuildOne.UpdateModel2 L110', func: 'HorizontalVessel',
    params: [ ['diameter', 1], ['dishHeight', 1], ['length', 1], ['xLength', 1], ['yLengthBehind', 1], ['zLength', 1], ['offset', 1] ],
    build(p) {
      const d = p.diameter, L = p.length;
      return [
        eqPart('DishFront', 'DISH', [0, 0, 0], { DIAM: d + 1, HEIG: p.dishHeight, THIC: 50 }, 'Y is u and Z is s'),
        eqPart('Cylinder', 'CYLI', [0, L / 2, 0], { DIAM: d, HEIG: L }, 'Y is u and Z is s'),
        eqPart('DishBehind', 'DISH', [0, L, 0], { DIAM: d + 1, HEIG: p.dishHeight, THIC: 50 }, 'Y is d and Z is n'),
        eqPart('BoxFront', 'BOX', [0, p.offset + L / 2, -p.zLength / 2], { XLEN: p.xLength, YLEN: p.yLengthBehind, ZLEN: p.zLength }),
        eqPart('BoxBehind', 'BOX', [0, -p.offset + L / 2, -p.zLength / 2], { XLEN: p.xLength, YLEN: p.yLengthBehind, ZLEN: p.zLength })
      ];
    }
  },
  dished_flan_with_nozzles: {
    desc: '水平容器带法兰管嘴（4 管嘴；vendor 命名 N1/N3/N4/N5 已规整为 N1..N4）', src: 'BuildOne.dishedFlanWithNozzles L231', func: 'dishedFlanWithNozzles',
    params: [ ['catref', 1], ['diameter', 1], ['dishHeight', 1], ['length', 1], ['headLength', 1], ['disCL', 1], ['xLength', 1], ['flangeThick', 1], ['flangeDiameter', 1], ['yLengthBehind', 1], ['zLength', 1], ['offset', 1], ['nozzDis1', 1], ['nozzDis2', 1] ],
    build(p) {
      const d = p.diameter, L = p.length, fT = p.flangeThick, fD = p.flangeDiameter, zL = p.zLength, cz = d / 2 + 150;
      return [
        eqPart('DishFront', 'DISH', [0, 0, 0], { DIAM: d + 1, HEIG: p.dishHeight, THIC: 50 }, 'Y is u and Z is s'),
        eqPart('Cylinder', 'CYLI', [0, L / 2, 0], { DIAM: d, HEIG: L }, 'Y is u and Z is s'),
        eqPart('flangeHead', 'CYLI', [0, L, 0], { DIAM: fD, HEIG: fT * 2 }, 'Y is u and Z is s'),
        eqPart('flangeFront', 'CYLI', [0, L - p.headLength, 0], { DIAM: fD, HEIG: fT * 2 }, 'Y is u and Z is s'),
        eqPart('BoxFront', 'BOX', [0, p.offset + L / 2, -p.disCL + zL / 2], { XLEN: p.xLength, YLEN: p.yLengthBehind, ZLEN: zL }),
        eqPart('BoxBehind', 'BOX', [0, -p.offset + L / 2, -p.disCL + zL / 2], { XLEN: p.xLength, YLEN: p.yLengthBehind, ZLEN: zL }),
        eqPart('N1', 'NOZZ', [0, p.nozzDis1, cz], { HEIG: 200, CATR: p.catref }, 'Y is S and Z is W'),
        eqPart('N2', 'NOZZ', [0, 2 * p.nozzDis2, cz], { HEIG: 200, CATR: p.catref }, 'Y is S and Z is W'),
        eqPart('N3', 'NOZZ', [0, p.nozzDis1, -cz], { HEIG: 200, CATR: p.catref }, 'Y is W and Z is S'),
        eqPart('N4', 'NOZZ', [0, 2 * p.nozzDis2, -cz], { HEIG: 200, CATR: p.catref }, 'Y is W and Z is S')
      ];
    }
  },
  dished_both_ends_with_nozzles: {
    desc: '双封头法兰容器 6 管嘴', src: 'BuildOne.dishedBothEndWithNozzles L724', func: 'FlangeVessel2',
    params: [ ['catref', 1], ['diameter', 1], ['dishHeight', 1], ['length', 1], ['headLength', 1], ['disCL', 1], ['xLength', 1], ['flangeThick', 1], ['flangeDiameter', 1], ['yLengthBehind', 1], ['zLength', 1], ['offset', 1], ['nozzDis1', 1], ['nozzDis2', 1], ['nozzDis3', 1] ],
    build(p) {
      const d = p.diameter, L = p.length, fT = p.flangeThick, fD = p.flangeDiameter, zL = p.zLength, cz = d / 2 + 150;
      const ys = [p.nozzDis1, 2 * p.nozzDis2, 2 * p.nozzDis2 + p.nozzDis3];
      const parts = [
        eqPart('DishFront', 'DISH', [0, 0, 0], { DIAM: d + 1, HEIG: p.dishHeight, THIC: 50 }, 'Y is u and Z is s'),
        eqPart('DishBehind', 'DISH', [0, L, 0], { DIAM: d + 1, HEIG: p.dishHeight, THIC: 50 }, 'Y is d and Z is n'),
        eqPart('Cylinder', 'CYLI', [0, L / 2, 0], { DIAM: d, HEIG: L }, 'Y is u and Z is s'),
        eqPart('flangeFront', 'CYLI', [0, L - p.headLength, 0], { DIAM: fD, HEIG: fT * 2 }, 'Y is u and Z is s'),
        eqPart('BoxFront', 'BOX', [0, p.offset + L / 2, -p.disCL + zL / 2], { XLEN: p.xLength, YLEN: p.yLengthBehind, ZLEN: zL }),
        eqPart('BoxBehind', 'BOX', [0, -p.offset + L / 2, -p.disCL + zL / 2], { XLEN: p.xLength, YLEN: p.yLengthBehind, ZLEN: zL })
      ];
      for (let i = 0; i < 3; i++) parts.push(eqPart('N' + (i + 1), 'NOZZ', [0, ys[i], cz], { HEIG: 200, CATR: p.catref }, 'Y is S and Z is W'));
      for (let i = 0; i < 3; i++) parts.push(eqPart('N' + (i + 4), 'NOZZ', [0, ys[i], -cz], { HEIG: 200, CATR: p.catref }, 'Y is W and Z is S'));
      return parts;
    }
  },
  dished_both_ends: {
    desc: '双封头双法兰（无管嘴）', src: 'BuildOne.dishedBothEnds L915', func: 'dishedBothEnds',
    params: [ ['diameter', 1], ['dishHeight', 1], ['length', 1], ['headLength', 1], ['tailLength', 1], ['disCL', 1], ['xLength', 1], ['flangeThick', 1], ['flangeDiameter', 1], ['yLengthBehind', 1], ['zLength', 1], ['offset', 1] ],
    build(p) {
      const d = p.diameter, L = p.length, fT = p.flangeThick, fD = p.flangeDiameter, zL = p.zLength;
      return [
        eqPart('DishFront', 'DISH', [0, 0, 0], { DIAM: d + 1, HEIG: p.dishHeight, THIC: 50 }, 'Y is u and Z is s'),
        eqPart('Cylinder', 'CYLI', [0, L / 2, 0], { DIAM: d, HEIG: L }, 'Y is u and Z is s'),
        eqPart('DishBehind', 'DISH', [0, L, 0], { DIAM: d + 1, HEIG: p.dishHeight, THIC: 50 }, 'Y is d and Z is n'),
        eqPart('flangeFront', 'CYLI', [0, L - p.headLength, 0], { DIAM: fD, HEIG: fT * 2 }, 'Y is u and Z is s'),
        eqPart('flangeBehind', 'CYLI', [0, p.tailLength, 0], { DIAM: fD, HEIG: fT * 2 }, 'Y is U and Z is S'),
        eqPart('BoxFront', 'BOX', [0, p.offset + L / 2, -p.disCL + zL / 2], { XLEN: p.xLength, YLEN: p.yLengthBehind, ZLEN: zL }),
        eqPart('BoxBehind', 'BOX', [0, -p.offset + L / 2, -p.disCL + zL / 2], { XLEN: p.xLength, YLEN: p.yLengthBehind, ZLEN: zL })
      ];
    }
  },
  flan_both_ends: {
    desc: '双法兰端（无封头，四法兰）', src: 'BuildOne.flanBothEnds L1074', func: 'flanBothEnds',
    params: [ ['diameter', 1], ['length', 1], ['headLength', 1], ['tailLength', 1], ['disCL', 1], ['xLength', 1], ['flangeThick', 1], ['flangeDiameter', 1], ['yLengthBehind', 1], ['zLength', 1], ['offset', 1] ],
    build(p) {
      const d = p.diameter, L = p.length, fT = p.flangeThick, fD = p.flangeDiameter, zL = p.zLength;
      return [
        eqPart('flangeTail', 'CYLI', [0, 0, 0], { DIAM: fD, HEIG: fT * 2 }, 'Y is u and Z is s'),
        eqPart('flangeBehind', 'CYLI', [0, p.tailLength, 0], { DIAM: fD, HEIG: fT * 2 }, 'Y is u and Z is s'),
        eqPart('Cylinder', 'CYLI', [0, L / 2, 0], { DIAM: d, HEIG: L }, 'Y is u and Z is s'),
        eqPart('flangeHead', 'CYLI', [0, L, 0], { DIAM: fD, HEIG: fT * 2 }, 'Y is u and Z is s'),
        eqPart('flangeFront', 'CYLI', [0, L - p.headLength, 0], { DIAM: fD, HEIG: fT * 2 }, 'Y is u and Z is s'),
        eqPart('BoxFront', 'BOX', [0, p.offset + L / 2, -p.disCL + zL / 2], { XLEN: p.xLength, YLEN: p.yLengthBehind, ZLEN: zL }),
        eqPart('BoxBehind', 'BOX', [0, -p.offset + L / 2, -p.disCL + zL / 2], { XLEN: p.xLength, YLEN: p.yLengthBehind, ZLEN: zL })
      ];
    }
  },
  /* ---------- 立式设备: 轴向沿 U, 原点 = 底部 ---------- */
  dished_top_tank: {
    desc: '立式罐（上封头平底）', src: 'BuildOne.dishedTopTank L1238', func: 'VerticalVessel',
    params: [ ['diameter', 1], ['dishHeight', 1], ['height', 1] ],
    build(p) {
      const d = p.diameter, H = p.height;
      return [
        eqPart('DishTop', 'DISH', [0, 0, H], { DIAM: d + 1, HEIG: p.dishHeight, THIC: 50 }, 'Y is N and Z is u'),
        eqPart('Cylinder', 'CYLI', [0, 0, H / 2], { DIAM: d, HEIG: H }, 'Y is N and Z is U')
      ];
    }
  },
  flat_top_tank: {
    desc: '立式平顶罐（vendor 忽略 Up 的 bug 已按局部坐标语义归一：原点即罐底）', src: 'BuildOne.flatTopTank L1322', func: 'VerticalVessel',
    params: [ ['diameter', 1], ['height', 1] ],
    build(p) {
      return [ eqPart('Cylinder', 'CYLI', [0, 0, p.height / 2], { DIAM: p.diameter, HEIG: p.height }, 'Y is N and Z is U') ];
    }
  },
  flanged_dished_top: {
    desc: '立式上法兰+双封头', src: 'BuildOne.flangedDishedTop L1402', func: 'VerticalVessel',
    params: [ ['diameter', 1], ['flangeThick', 1], ['flangeDiameter', 1], ['dishHeight', 1], ['height', 1] ],
    build(p) {
      const d = p.diameter, H = p.height;
      return [
        eqPart('DishTop', 'DISH', [0, 0, H], { DIAM: d + 1, HEIG: p.dishHeight, THIC: 50 }, 'Y is N and Z is u'),
        eqPart('DishBottom', 'DISH', [0, 0, 0], { DIAM: d + 1, HEIG: p.dishHeight, THIC: 50 }, 'Y is N and Z is D'),
        eqPart('Cylinder', 'CYLI', [0, 0, H / 2], { DIAM: d, HEIG: H }, 'Y is N and Z is U'),
        eqPart('flangeHead', 'CYLI', [0, 0, H - p.flangeThick], { DIAM: p.flangeDiameter, HEIG: p.flangeThick * 2 }, 'Y is N and Z is u')
      ];
    }
  },
  dish_top_and_bottom: {
    desc: '立式双封头+双棱台支撑', src: 'BuildOne.dishTopAndBottom L1506', func: 'VerticalVessel',
    params: [ ['diameter', 1], ['dishHeight', 1], ['height', 1], ['supportLength', 1], ['supportWidth', 1], ['supportHeight', 1] ],
    build(p) {
      const d = p.diameter, H = p.height, sL = p.supportLength, sW = p.supportWidth, sH = p.supportHeight;
      return [
        eqPart('DishTop', 'DISH', [0, 0, H], { DIAM: d + 1, HEIG: p.dishHeight, THIC: 50 }, 'Y is N and Z is u'),
        eqPart('DishBottom', 'DISH', [0, 0, 0], { DIAM: d + 1, HEIG: p.dishHeight, THIC: 50 }, 'Y is N and Z is D'),
        eqPart('Cylinder', 'CYLI', [0, 0, H / 2], { DIAM: d, HEIG: H }, 'Y is N and Z is U'),
        eqPart('SupportPyramid', 'PYRA', [0, 0, H - sH], { XBOT: sL - 100, YBOT: sW - 100, XTOP: sL, YTOP: sW, HEIG: 100 }),
        eqPart('SupportPyramid2', 'PYRA', [0, 0, H - sH], { XBOT: sW - 100, YBOT: sL - 100, XTOP: sW, YTOP: sL, HEIG: 100 })
      ];
    }
  },
  dish_top_cone_bottom: {
    desc: '立式上封头+下锥底+排净管嘴', src: 'BuildOne.dishTopConeBottom L1749', func: 'VerticalVessel',
    params: [ ['diameter', 1], ['dishHeight', 1], ['diameterBottom', 1], ['coneHeight', 1], ['height', 1], ['catref', 0, '/DICHTFL_C/DEZFBR0LL'] ],
    build(p) {
      const d = p.diameter, H = p.height, cH = p.coneHeight;
      return [
        eqPart('DishTop', 'DISH', [0, 0, H], { DIAM: d + 1, HEIG: p.dishHeight, THIC: 50 }, 'Y is N and Z is u'),
        eqPart('coneBottom', 'CONE', [0, 0, -cH / 2], { DTOP: d + 1, DBOT: p.diameterBottom, HEIG: cH }, 'Y is S and Z is U'),
        eqPart('Cylinder', 'CYLI', [0, 0, H / 2], { DIAM: d, HEIG: H }, 'Y is N and Z is U'),
        eqPart('N1', 'NOZZ', [0, 0, -cH - 150], { HEIG: 200, CATR: p.catref }, 'Y is N and Z is W')
      ];
    }
  },
  vertical_head_exchanger: {
    desc: '立式换热器（上封头+筒体+双法兰）', src: 'BuildOne.verticalHeadExchanger L1851', func: 'FlangeVessel',
    params: [ ['diameter', 1], ['dishHeight', 1], ['length', 1], ['headLength', 1], ['flangeThick', 1], ['flangeDiameter', 1] ],
    build(p) {
      const d = p.diameter, L = p.length, fT = p.flangeThick, fD = p.flangeDiameter;
      return [
        eqPart('DishFront', 'DISH', [0, 0, L], { DIAM: d + 1, HEIG: p.dishHeight, THIC: 50 }, 'Y is N and Z is u'),
        eqPart('Cylinder', 'CYLI', [0, 0, L / 2], { DIAM: d, HEIG: L }, 'Y is N and Z is u'),
        eqPart('flangeHead', 'CYLI', [0, 0, fT], { DIAM: fD, HEIG: fT * 2 }, 'Y is N and Z is u'),
        eqPart('flangeFront', 'CYLI', [0, 0, p.headLength], { DIAM: fD, HEIG: fT * 2 }, 'Y is N and Z is u')
      ];
    }
  },
  /* ---------- 塔: 轴向沿 U, 原点 = 下封头中心 ---------- */
  tower: {
    desc: '塔（变径圆台，代码自带 7 项默认值）', src: 'TowerImpl.UpdateModel L286', func: 'Tower',
    params: [ ['diameterTop', 0, 2500], ['heightTop', 0, 5000], ['dishTopHeight', 0, 500], ['coneHeight', 0, 500], ['diameterBottom', 0, 4000], ['heightBottom', 0, 8000], ['dishBottomHeight', 0, 800] ],
    build(p) {
      const cH = p.coneHeight, hT = p.heightTop, hB = p.heightBottom, dT = p.diameterTop, dB = p.diameterBottom;
      return [
        eqPart('DishTop', 'DISH', [0, 0, cH + hT + hB], { DIAM: dT + 1, HEIG: p.dishTopHeight, THIC: 50 }, 'Y is N and Z is u'),
        eqPart('CylinderTop', 'CYLI', [0, 0, cH + hB + hT / 2], { DIAM: dT, HEIG: hT }, 'Y is N and Z is U'),
        eqPart('Transfer', 'CONE', [0, 0, hB + cH / 2], { DTOP: dT, DBOT: dB, HEIG: cH }, 'Y is N and Z is U'),
        eqPart('CylinderBottom', 'CYLI', [0, 0, hB / 2], { DIAM: dB, HEIG: hB }, 'Y is N and Z is U'),
        eqPart('DishBottom', 'DISH', [0, 0, 0], { DIAM: dB + 1, HEIG: p.dishBottomHeight, THIC: 50 }, 'Y is N and Z is d')
      ];
    }
  },
  reducing_turn_tower: {
    desc: '异径塔（上封头+锥+双筒+下封头）', src: 'BuildOne.reducingTurnTower L1972', func: 'FlangeVessel',
    params: [ ['diameter', 1], ['dishHeight', 1], ['diameterBottom', 1], ['coneHeight', 1], ['height', 1], ['twinHeight', 1] ],
    build(p) {
      const d = p.diameter, H = p.height, cH = p.coneHeight, tw = p.twinHeight, dB = p.diameterBottom;
      return [
        eqPart('DishTop', 'DISH', [0, 0, H], { DIAM: d + 1, HEIG: p.dishHeight, THIC: 50 }, 'Y is N and Z is u'),
        eqPart('coneBottom', 'CONE', [0, 0, -cH / 2], { DTOP: d + 1, DBOT: dB, HEIG: cH }, 'Y is S and Z is U'),
        eqPart('Cylinder', 'CYLI', [0, 0, H / 2], { DIAM: d, HEIG: H }, 'Y is N and Z is U'),
        eqPart('twinCylinder', 'CYLI', [0, 0, -cH - tw / 2], { DIAM: dB, HEIG: tw }, 'Y is N and Z is U'),
        eqPart('DishBottom', 'DISH', [0, 0, -cH - tw], { DIAM: dB + 1, HEIG: p.dishHeight, THIC: 50 }, 'Y is N and Z is D')
      ];
    }
  },
  reducing_tower: {
    desc: '异径塔变体（锥台反向）', src: 'BuildOne.reducingTower L2099', func: 'FlangeVessel',
    params: [ ['diameter', 1], ['dishHeight', 1], ['diameterBottom', 1], ['coneHeight', 1], ['height', 1], ['twinHeight', 1] ],
    build(p) {
      const d = p.diameter, H = p.height, cH = p.coneHeight, tw = p.twinHeight, dB = p.diameterBottom;
      return [
        eqPart('DishTop', 'DISH', [0, 0, -H], { DIAM: d + 1, HEIG: p.dishHeight, THIC: 50 }, 'Y is N and Z is D'),
        eqPart('coneBottom', 'CONE', [0, 0, cH / 2], { DTOP: dB + 1, DBOT: d, HEIG: cH }, 'Y is S and Z is U'),
        eqPart('Cylinder', 'CYLI', [0, 0, -H / 2], { DIAM: d, HEIG: H }, 'Y is N and Z is U'),
        eqPart('twinCylinder', 'CYLI', [0, 0, cH + tw / 2], { DIAM: dB, HEIG: tw }, 'Y is N and Z is U'),
        eqPart('DishBottom', 'DISH', [0, 0, cH + tw], { DIAM: dB + 1, HEIG: p.dishHeight, THIC: 50 }, 'Y is N and Z is U')
      ];
    }
  },
  /* ---------- 泵: 中心线定位, 原点 = 泵底座参考点（定位高度=地板下边缘） ---------- */
  center_line_offset_pump: {
    desc: '卧式泵（排出管嘴偏心 offSetNozz）', src: 'BuildOne.centerLineOffestPUMP L2226', func: 'centerLineOffestPUMP',
    params: [ ['baseLength', 0, 510], ['baseWidth', 0, 1350], ['originBase', 0, 220], ['disSuctionNozz', 0, 160], ['bottomCL', 0, 340], ['disNozzHeight', 0, 150], ['offSetNozz', 0, 140], ['catrIn', 0, '/AAZFBD0JJ'], ['catrOut', 0, '/AAZFBD0NN'] ],
    build(p) {
      const bL = p.baseLength, bW = p.baseWidth, oB = p.originBase, bCL = p.bottomCL, dNH = p.disNozzHeight;
      return [
        eqPart('boxBase', 'BOX', [0, bL / 2 + oB, 55], { XLEN: bL, YLEN: bW, ZLEN: 110 }),
        eqPart('boxNozzle', 'BOX', [0, 0, bCL / 2 + 55], { XLEN: bL, YLEN: 100, ZLEN: bCL - 110 }),
        eqPart('elecCylinder', 'CYLI', [0, 0, bCL], { DIAM: 234, HEIG: 72 }, 'Y is D and Z is N'),
        eqPart('Cylinder', 'CYLI', [0, bL * 1.5, bCL], { DIAM: 370, HEIG: 450 }, 'Y is D and Z is N'),
        eqPart('N1', 'NOZZ', [0, -p.disSuctionNozz, bCL], { HEIG: 200, CATR: p.catrIn }, 'Y is W and Z is U'),
        eqPart('N2', 'NOZZ', [-p.offSetNozz, 0, bCL + dNH], { HEIG: 200, CATR: p.catrOut }, 'Y is N and Z is E')
      ];
    }
  },
  center_line_pump: {
    desc: '卧式泵（排出管嘴对中；offSetNozz 不生效，保留参数兼容）', src: 'BuildOne.centerLinePUMP L2342', func: 'centerLineOffestPUMP',
    params: [ ['baseLength', 0, 510], ['baseWidth', 0, 1350], ['originBase', 0, 220], ['disSuctionNozz', 0, 160], ['bottomCL', 0, 340], ['disNozzHeight', 0, 150], ['offSetNozz', 0, 140], ['catrIn', 0, '/AAZFBD0JJ'], ['catrOut', 0, '/AAZFBD0NN'] ],
    build(p) {
      const bL = p.baseLength, bW = p.baseWidth, oB = p.originBase, bCL = p.bottomCL, dNH = p.disNozzHeight;
      return [
        eqPart('boxBase', 'BOX', [0, bL / 2 + oB, 55], { XLEN: bL, YLEN: bW, ZLEN: 110 }),
        eqPart('boxNozzle', 'BOX', [0, 0, bCL / 2 + 45], { XLEN: bL, YLEN: 100, ZLEN: bCL - 110 }),
        eqPart('elecCylinder', 'CYLI', [0, 0, bCL], { DIAM: 234, HEIG: 72 }, 'Y is D and Z is N'),
        eqPart('Cylinder', 'CYLI', [0, bL * 1.5, bCL], { DIAM: 370, HEIG: 450 }, 'Y is D and Z is N'),
        eqPart('N1', 'NOZZ', [0, -p.disSuctionNozz, bCL], { HEIG: 200, CATR: p.catrIn }, 'Y is W and Z is U'),
        eqPart('N2', 'NOZZ', [0, 0, bCL + dNH], { HEIG: 200, CATR: p.catrOut }, 'Y is N and Z is E')
      ];
    }
  },
  center_vertical_pump: {
    desc: '立式泵（N 向 baseLength*1.5 处为电机端）', src: 'BuildOne.centerVerticalPUMP L2458', func: 'centerLineOffestPUMP',
    params: [ ['baseLength', 0, 510], ['baseWidth', 0, 1350], ['originBase', 0, 220], ['bottomCL', 0, 340], ['disNozzHeight', 0, 150], ['offSetXNozz1', 0, 150], ['offSetXNozz2', 0, 130], ['offSetYNozz', 0, 40], ['catrIn', 0, '/AAZFBD0JJ'], ['catrOut', 0, '/AAZFBD0NN'] ],
    build(p) {
      const bL = p.baseLength, bW = p.baseWidth, oB = p.originBase, bCL = p.bottomCL, dNH = p.disNozzHeight;
      return [
        eqPart('boxBase', 'BOX', [0, bL / 2 + oB, 55], { XLEN: bL, YLEN: bW, ZLEN: 110 }),
        eqPart('boxNozzle', 'BOX', [0, 0, bCL / 2 + 45], { XLEN: bL, YLEN: 100, ZLEN: bCL - 110 }),
        eqPart('Cylinder', 'CYLI', [0, 0, bCL], { DIAM: 234, HEIG: 72 }, 'Y is D and Z is N'),
        eqPart('EleCylinder', 'CYLI', [0, bL * 1.5, bCL], { DIAM: 370, HEIG: 450 }, 'Y is D and Z is N'),
        eqPart('N1', 'NOZZ', [-p.offSetXNozz1, 0, bCL + dNH], { HEIG: 200, CATR: p.catrIn }, 'Y is N and Z is E'),
        eqPart('N2', 'NOZZ', [p.offSetXNozz2, p.offSetYNozz, bCL + dNH], { HEIG: 200, CATR: p.catrOut }, 'Y is N and Z is E')
      ];
    }
  },
  /* ---------- 底座 / 辅助 ---------- */
  four_box_base: {
    desc: '四点底座', src: 'BuildOne.fourBoxBase L500', func: 'fourBoxBase',
    params: [ ['xLength', 1], ['yLengthBehind', 1], ['zLength', 1], ['offset', 1] ],
    build(p) {
      const q = [[1, 1], [-1, 1], [-1, -1], [1, -1]];
      return q.map((v, i) => eqPart('Box' + (i + 1), 'BOX', [v[0] * p.offset, v[1] * p.offset, 0], { XLEN: p.xLength, YLEN: p.yLengthBehind, ZLEN: p.zLength }));
    }
  },
  two_exchange_base: {
    desc: '两点底座（换热器；vendor 复用 fourBoxBase FUNC）', src: 'BuildOne.twoExchangeBase L585', func: 'fourBoxBase',
    params: [ ['xLength', 1], ['yLengthBehind', 1], ['zLength', 1], ['offset', 1] ],
    build(p) {
      return [
        eqPart('Box1', 'BOX', [0, p.offset, 0], { XLEN: p.xLength, YLEN: p.yLengthBehind, ZLEN: p.zLength }),
        eqPart('Box2', 'BOX', [0, -p.offset, 0], { XLEN: p.xLength, YLEN: p.yLengthBehind, ZLEN: p.zLength })
      ];
    }
  },
  two_skirt_base: {
    desc: '裙式底座', src: 'BuildOne.twoSkirtBase L662', func: 'twoSkirtBase',
    params: [ ['diameter', 1], ['height', 1] ],
    build(p) { return [ eqPart('Skirt', 'CYLI', [0, 0, 0], { DIAM: p.diameter, HEIG: p.height }) ]; }
  },
  two_prism_base: {
    desc: '双棱台支撑（HEIG=100 垫台，vendor 原样）', src: 'BuildOne.twoPrismBase L1620', func: 'twoPrismBase',
    params: [ ['supportLength', 1], ['supportWidth', 1], ['supportHeight', 1] ],
    build(p) {
      const sL = p.supportLength, sW = p.supportWidth, sH = p.supportHeight;
      return [
        eqPart('SupportPyramid', 'PYRA', [0, 0, -sH], { XBOT: sL - 100, YBOT: sW - 100, XTOP: sL, YTOP: sW, HEIG: 100 }),
        eqPart('SupportPyramid2', 'PYRA', [0, 0, -sH], { XBOT: sW - 100, YBOT: sL - 100, XTOP: sW, YTOP: sL, HEIG: 100 })
      ];
    }
  },
  maintenance_space: {
    desc: '检修空间', src: 'BuildOne.maintenanceSpace L1691', func: 'maintenanceSpace',
    params: [ ['length', 1], ['width', 1], ['height', 1] ],
    build(p) { return [ eqPart('Space', 'BOX', [0, 0, 0], { XLEN: p.length, YLEN: p.width, ZLEN: p.height }) ]; }
  }
};

function cpPartLines(pt, spco) {
  const L = ['  NEW ' + pt.type, "    NAME '" + pt.name + "'"];
  L.push('    ' + cpAt(pt.at[0], pt.at[1], pt.at[2]));
  if (pt.ori) {
    const o = Array.isArray(pt.ori) ? pt.ori : oriToOria(pt.ori);
    if (o && (o[0] !== 0 || o[1] !== 0 || o[2] !== 0)) L.push('    ORIA  ' + o.map(x => Number(x).toFixed(3)).join('  '));
    else if (typeof pt.ori === 'string' && !o) L.push("    ORI  '" + pt.ori + "'"); // 24 群外兜底（理论不达）
  }
  for (const k of Object.keys(pt.dims)) {
    const v = pt.dims[k];
    if (v === undefined || v === null || v === '') continue;
    L.push(k === 'CATR' ? '    CATR' + (spco ? '  SPCO' : '') + '  ' + v : '    ' + k + '  ' + cpNum(v));
  }
  L.push('  END');
  return L;
}

function pmlCreateEquipment(a) {
  const spec = EQUIP_CATALOG[a.type];
  const z = String(a.zone);
  const zone = z.charAt(0) === '/' ? z : '/' + z;
  const name = String(a.name);
  const pos = a.pos || {};
  const p = a.params;
  let parts;
  try { parts = spec.build(p); } catch (e) { throw new Error(spec.desc + ': ' + e.message); }
  if (a.noCatr) parts = parts.map(pt => { const d = Object.assign({}, pt.dims); delete d.CATR; return Object.assign({}, pt, { dims: d }); });
  // 附加管嘴（管嘴表导入语义）: 显式 pos 优先；否则 radius(SufFaceDis)+angle(NozzAngle, 0=N 90=E 俯视顺时针)+elevOffset(EleNozz)
  const oriSkipped = [];
  (a.nozzles || []).forEach((nz, i) => {
    let at, th = null;
    if (nz && nz.pos) at = [Number(nz.pos.east) || 0, Number(nz.pos.north) || 0, Number(nz.pos.up) || 0];
    else {
      const r = Number(nz && nz.radius) || 0;
      th = Math.round((((Number(nz && nz.angle) || 0) % 360) + 360) % 360);
      const rad = (th * Math.PI) / 180;
      at = [r * Math.sin(rad), r * Math.cos(rad), Number(nz && nz.elevOffset) || 0];
    }
    let ori = (nz && nz.ori) || null;
    if (!ori) {
      const onAxis = Math.abs(at[0]) <= 1 && Math.abs(at[1]) <= 1;
      if (!onAxis && th === null) th = Math.round((((Math.atan2(at[0], at[1]) * 180) / Math.PI) % 360 + 360) % 360);
      if (th !== null && RADIAL_ORIA[th]) ori = RADIAL_ORIA[th];
      else if (!onAxis) oriSkipped.push((nz && nz.name) || ('NA' + (i + 1)));
    }
    const catr = a.noCatr ? undefined : ((nz && nz.catr) || a.catref || undefined);
    parts.push(eqPart((nz && nz.name) || ('NA' + (i + 1)), 'NOZZ', at, { HEIG: (nz && nz.heig) || 200, CATR: catr }, ori));
  });
  const func = (a.func === undefined || a.func === null) ? spec.func : String(a.func);
  const L = pmlResultOpen().concat([
    'writefile $!F1 |===EQUI-START ' + name + '===|',
    '!Z1 = false',
    'CE ' + zone,
    'handle ANY',
    '  !Z1 = true',
    'endhandle',
    'if (!Z1) then',
    '  writefile $!F1 |ZONE_NOT_FOUND ' + zone + '|',
    'else',
    '  !X1 = false',
    '  CE ' + zone + '/EQUIP/' + name,
    '  handle ANY',
    '    !X1 = true',
    '  endhandle',
    '  if (!X1) then',
    '    writefile $!F1 |EQUIP_EXISTS ' + zone + '/EQUIP/' + name + '|',
    '  else',
    '    CE ' + zone,
    '    NEW EQUI /' + name,
    '      ' + cpAt(Number(pos.east) || 0, Number(pos.north) || 0, Number(pos.up) || 0)
  ]);
  const partSummary = parts.map(pt => ({ type: pt.type, name: pt.name }));
  if (func) L.push("      FUNC  '" + func + "'");
  for (const pt of parts) for (const line of cpPartLines(pt, a.spco)) L.push(line);
  L.push(
    '    END',
    '    CE ' + zone + '/EQUIP/' + name,
    '    !TN = READFAIL',
    '    handle ANY',
    '      !TN = !!ce.type',
    '    endhandle',
    '    writefile $!F1 |EQUIP_TYPE_$!TN|',
    '    !FN = READFAIL',
    '    handle ANY',
    '      !FN = !!ce.func',
    '    endhandle',
    '    writefile $!F1 |EQUIP_FUNC_$!FN|',
    '    var !L1 coll all for ce',
    '    handle ANY',
    '      var !L1 ARRAY()',
    '    endhandle',
    '    !C1 = !L1.size()',
    '    writefile $!F1 |MEMBERS_$!C1|',
    '    do !i indices !L1',
    '      !M1 = !L1[!i]',
    "      !MS1 = 'ERRSTR'",
    '      !MS1 = !M1.string()',
    '      handle ANY',
    '      endhandle',
    '      writefile $!F1 |DM_$!MS1|',
    '    enddo',
    '    writefile $!F1 |===EQUI-OK===|',
    '  endif',
    'endif',
    'closefile $!F1'
  );
  return { code: L.join('\n'), oriSkipped, parts: partSummary };
}

function pmlGetEquipment(refIn) {
  const ref = String(refIn);
  const r = ref.charAt(0) === '/' ? ref : '/' + ref;
  const L = pmlResultOpen().concat([
    'writefile $!F1 |===EQUI-GET ' + r + '===|',
    '!G1 = false',
    'CE ' + r,
    'handle ANY',
    '  !G1 = true',
    'endhandle',
    'if (!G1) then',
    '  writefile $!F1 |EQUIP_NOT_FOUND|',
    'else',
    '  !TN = READFAIL',
    '  handle ANY',
    '    !TN = !!ce.type',
    '  endhandle',
    '  writefile $!F1 |EQUIP_TYPE_$!TN|',
    '  !NM = READFAIL',
    '  handle ANY',
    '    !NM = !!ce.name',
    '  endhandle',
    '  writefile $!F1 |EQUIP_NAME_$!NM|',
    '  !FN = READFAIL',
    '  handle ANY',
    '    !FN = !!ce.func',
    '  endhandle',
    '  writefile $!F1 |EQUIP_FUNC_$!FN|',
    '  !PX = 0',
    '  handle ANY',
    '    !PX = !!ce.pos.x',
    '  endhandle',
    '  !PY = 0',
    '  handle ANY',
    '    !PY = !!ce.pos.y',
    '  endhandle',
    '  !PZ = 0',
    '  handle ANY',
    '    !PZ = !!ce.pos.z',
    '  endhandle',
    '  writefile $!F1 |EQUIP_POS_E_$!PX N_$!PY U_$!PZ|',
    '  var !L1 coll all for ce',
    '  handle ANY',
    '    var !L1 ARRAY()',
    '  endhandle',
    '  !C1 = !L1.size()',
    '  writefile $!F1 |MEMBERS_$!C1|',
    '  do !i indices !L1',
    '    !E1 = !L1[!i]',
    "    !T1 = 'ERRSTR'",
    '    !T1 = !E1.type',
    '    handle ANY',
    '    endhandle',
    "    !N1 = 'ERRSTR'",
    '    !N1 = !E1.name',
    '    handle ANY',
    '    endhandle'
  ]);
  const numRead = (varName, expr) => {
    L.push('    ' + varName + ' = 0', '    handle ANY', '      ' + varName + ' = ' + expr, '    endhandle');
  };
  numRead('!EX', '!E1.pos.x'); numRead('!EY', '!E1.pos.y'); numRead('!EZ', '!E1.pos.z');
  numRead('!V1', '!E1.diam'); numRead('!V2', '!E1.heig'); numRead('!V3', '!E1.thic');
  numRead('!V4', '!E1.xlen'); numRead('!V5', '!E1.ylen'); numRead('!V6', '!E1.zlen');
  numRead('!V7', '!E1.dtop'); numRead('!V8', '!E1.dbot');
  numRead('!V9', '!E1.xbot'); numRead('!V10', '!E1.ybot'); numRead('!V11', '!E1.xtop'); numRead('!V12', '!E1.ytop');
  numRead('!V13', '!E1.zlen');
  L.push("    !CS = 'ERRSTR'", '    handle ANY', '      !CS = !E1.catr', '    endhandle');
  // 两行拆分防 PML 行宽超限；Node 侧按 name+type 合并
  L.push(
    '    writefile $!F1 |MEM $!T1 $!N1 EX_$!EX EY_$!EY EZ_$!EZ DIA_$!V1 HEIG_$!V2 THIC_$!V3 XL_$!V4 YL_$!V5 ZL_$!V6|',
    '    writefile $!F1 |MEM $!T1 $!N1 DT_$!V7 DB_$!V8 XB_$!V9 YB_$!V10 XT_$!V11 YT_$!V12 NZL_$!V13 CATR_$!CS|',
    '  enddo',
    '  writefile $!F1 |===EQUI-OK===|',
    'endif',
    'closefile $!F1'
  );
  return L.join('\n');
}

function parseEquipCreate(output, zone, name) {
  const z = zone.charAt(0) === '/' ? zone : '/' + zone;
  const res = { ok: true, ref: z + '/EQUIP/' + name, created: false };
  for (const l of String(output || '').split(/\r?\n/)) {
    const s = l.trim();
    if (!s) continue;
    if (s.indexOf('ZONE_NOT_FOUND') >= 0) { res.ok = false; res.error = 'zone 不存在或 CE 失败: ' + s.slice(s.indexOf('ZONE_NOT_FOUND') + 15).trim(); }
    else if (s.indexOf('EQUIP_EXISTS') >= 0) { res.ok = false; res.exists = true; res.error = '设备已存在（未覆盖；请换名或先删除）: ' + res.ref; }
    else if (s.indexOf('EQUIP_TYPE_') === 0) res.type = s.slice(11);
    else if (s.indexOf('EQUIP_FUNC_') === 0) res.func = s.slice(11);
    else if (s.indexOf('MEMBERS_') === 0) res.memberCount = parseInt(s.slice(8), 10) || 0;
    else if (s.indexOf('DM_') === 0) (res.members = res.members || []).push(s.slice(3));
    if (s.indexOf('===EQUI-OK===') >= 0) { res.created = true; res.ok = true; }
  }
  if (!res.created && res.ok) { res.ok = false; res.error = '结果缺 ===EQUI-OK===（宏中途中止；已建部分成员可能残留，用 pdms_get_equipment 查看 + DELETE 处理）'; }
  return res;
}
function parseMemberLine(s) {
  const t = s.split(/\s+/);
  const m = { type: t[1], name: t[2] };
  for (const tok of t.slice(3)) {
    const i = tok.indexOf('_');
    if (i < 1) continue;
    const k = tok.slice(0, i), v = tok.slice(i + 1);
    if (k === 'CATR') { if (v && v !== 'ERRSTR') m.catref = v; continue; }
    const n = Number(v);
    if (!isNaN(n)) m[k.toLowerCase()] = n;
  }
  return m;
}
function toNozzRow(m) {
  // 管嘴表语义（对应 EquiTool.getNozzInfo）: SufFaceDis=平面径向距, EleNozz=标高, NozzAngle=自北顺时针俯视角(360→0)
  const e = m.ex || 0, n = m.ey || 0, u = m.ez || 0;
  let ang = Math.round(((Math.atan2(e, n) * 180) / Math.PI + 360) % 360);
  if (ang === 360) ang = 0;
  return {
    name: m.name, pos: { e, n, u },
    sufFaceDis: Math.round(Math.sqrt(e * e + n * n)), elev: u, nozzAngle: ang,
    catref: m.catref || null, heig: m.heig !== undefined ? m.heig : null, bore: m.dia !== undefined ? m.dia : null
  };
}
function parseEquipGet(output) {
  const res = { ok: true };
  for (const l of String(output || '').split(/\r?\n/)) {
    const s = l.trim();
    if (!s) continue;
    if (s.indexOf('EQUIP_NOT_FOUND') === 0) { res.ok = false; res.error = '设备不存在或 CE 失败'; }
    else if (s.indexOf('EQUIP_TYPE_') === 0) res.type = s.slice(11);
    else if (s.indexOf('EQUIP_NAME_') === 0) res.name = s.slice(11);
    else if (s.indexOf('EQUIP_FUNC_') === 0) res.func = s.slice(11);
    else if (s.indexOf('EQUIP_POS_') === 0) {
      const m1 = s.match(/E_([-\d.E+]+)/), m2 = s.match(/N_([-\d.E+]+)/), m3 = s.match(/U_([-\d.E+]+)/);
      if (m1 && m2 && m3) res.pos = { east: +m1[1], north: +m2[1], up: +m3[1] };
    }
    else if (s.indexOf('MEMBERS_') === 0) res.memberCount = parseInt(s.slice(8), 10) || 0;
    else if (s.indexOf('MEM ') === 0) {
      const m = parseMemberLine(s);
      res.members = res.members || [];
      const ex = res.members.find(x => x.name === m.name && x.type === m.type);
      if (ex) Object.assign(ex, m); else res.members.push(m);
    }
    if (s.indexOf('===EQUI-OK===') >= 0) res.readOk = true;
  }
  if (res.members) {
    const nz = res.members.filter(m => m.type === 'NOZZLE' || m.type === 'NOZZ');
    if (nz.length) res.nozzles = nz.map(toNozzRow);
  }
  return res;
}

/* ----------------------------- MCP 工具定义 ----------------------------- */
const TOOLS = [
  {
    name: 'pdms_status',
    description: '检查 PDMS 桥接是否在线（PDMS 是否运行且已加载 PdmsBridge 监听器）。返回 connected 布尔与监听地址。',
    inputSchema: { type: 'object', properties: {} }
  },
  {
    name: 'pdms_probe_read',
    description: '只读探针（kind:op，PDMS UI 线程执行；桥接 op 通道首跑冒烟 + 出图选元素用）：返回 unit、当前 CE、出图 world(/*) 顶层清单、/DRA 可解析性、设计 world 顶层元素（MDBMembers(Design)）及各自包围盒。纯读，不改模型。',
    inputSchema: { type: 'object', properties: {} }
  },
  {
    name: 'pdms_draw',
    description: '出图（kind:op，直调 C#；窗口生产链 button2_Click 的 1:1 无头移植，含管道布置图全套标注块：尺寸/设备标签/流向箭头/阀门符号/管道一览表/边框）。复用已验证进程内链：DEPT→REGI→DRWG→SHEET(createDrwgDraw)→VIEW(VFRCOL/VTYP/RRSF/HRSF/VSCA/THPO)→IDLIST(ADD ALL WITHIN 体积框选 + ADDENTRY)→IDLN 双绑→动态 SIZE/THPO→UPDATE all→标注。前提：DRAW 会话已挂设计库（有 3D 模型）。返回 SHEET/VIEW 名供导出。',
    inputSchema: {
      type: 'object',
      properties: {
        start: { type: 'string', description: '起点元素（窗口 CE 的等价物），默认 "/*"（出图 world）。按其类型分支：WORLD/DEPT/REGISTRY/DRWG/SHEET' },
        drawingType: { type: 'string', description: '图种（决定 ADD 类型与标注块）：管道布置图(默认) / 设备布置图 / 埋板布置图 / 管口方位图' },
        scaleDen: { type: 'number', description: '比例分母，默认 50（1:50）' },
        box: {
          type: 'object',
          description: '世界坐标体积框（mm；W=X N=Y U=Z）。缺省时按 elements 的包围盒自动计算',
          properties: {
            xmin: { type: 'number' }, xmax: { type: 'number' },
            ymin: { type: 'number' }, ymax: { type: 'number' },
            zmin: { type: 'number' }, zmax: { type: 'number' }
          }, required: ['xmin', 'xmax', 'ymin', 'ymax', 'zmin', 'zmax']
        },
        elements: { type: 'array', items: { type: 'string' }, description: '要画的元素名（窗口 listBox1 手选等价，走 ADDENTRY；box 缺省时也用它自动算框），如 ["/2001"]' },
        annotate: { type: 'boolean', description: '是否生成标注块，默认 true；false = 仅 SHEET+VIEW+模型投影' },
        backs: { type: 'string', description: '图纸背景，默认 /DRA/MAS/BACKS/MET/A0（名含 A4 时自动 210x297）' },
        rrsf: { type: 'string', description: '视图规则集，默认 /DRA/MAS/H&S/RULESETS/View1' },
        hrsf: { type: 'string', description: '剖面线规则集，默认 /DRA/PRJ/HRUL/GEN/Hatch-None' }
      }
    }
  },
  {
    name: 'pdms_export_dxf',
    description: 'SHEET 导出 DXF（kind:op，窗口 button11 逐行同款）：<sheet> LIEXEC /Draft_DXF_LI \'DXFOUT\' BLACKBG ON OWN OFF CUT ON OutputF <outDir><sheet名 斜杠转连字符>.dxf',
    inputSchema: {
      type: 'object',
      properties: {
        sheet: { type: 'string', description: 'SHEET 元素名（pdms_draw 返回的 SHEET 行）' },
        outDir: { type: 'string', description: '输出目录（须以 \\ 结尾），默认 D:\\pdmsAgent\\bridge\\out\\' }
      }, required: ['sheet']
    }
  },
  {
    name: 'pdms_export_pdf',
    description: 'SHEET 导出 PDF（kind:op，窗口 button11 逐行同款）：PLOT <sheet> PDF "..." \'PLOTSTYLE monochrome, REDUCETOFIT\' A1|A4 CUT OFF BORD 25 OVER（A4 判定 SIZE[0]==210）',
    inputSchema: {
      type: 'object',
      properties: {
        sheet: { type: 'string', description: 'SHEET 元素名（pdms_draw 返回的 SHEET 行）' },
        outDir: { type: 'string', description: '输出目录（须以 \\ 结尾），默认 D:\\pdmsAgent\\bridge\\out\\' }
      }, required: ['sheet']
    }
  },
  {
    name: 'pdms_exec',
    description: '在 PDMS 中执行一段 PML 代码或 PDMS 命令（经监听器在 PDMS 线程执行）。返回执行结果与错误。这是最核心的工具，智能体用它直接驱动 PDMS 出图。',
    inputSchema: {
      type: 'object',
      properties: {
        code: { type: 'string', description: '要执行的 PML / PDMS 命令文本' },
        timeout: { type: 'number', description: '可选，覆盖默认超时(ms)' }
      },
      required: ['code']
    }
  },
  {
    name: 'pdms_query',
    description: '在 PDMS 中求值一个 PML 表达式，结果经结果文件返回（唯一稳定回读通道；RunInPdms 内联回读不可靠）。例：!!ce.type / !!ce.vsca / !!ce.dbType / !!ce.name。',
    inputSchema: {
      type: 'object',
      properties: { expression: { type: 'string', description: 'PML 表达式，如 "!!ce.type"、"!!ce.vsca"' } },
      required: ['expression']
    }
  },
  {
    name: 'pdms_create_drawing',
    description: '高层工具：按 TTY 全链路实测通过的 CP 语法创建图纸链 REGI→DRWG→SHEE→VIEW(+VSCA/VTYP/SIZE/XYPS/DIR)→DLLB/IDLI/TRLB，并回读 VIEW 的 type/vsca/vtyp/dbType 验证。结果写入结果文件。',
    inputSchema: {
      type: 'object',
      properties: {
        regi: { type: 'string', description: 'REGISTRY 名，默认 AGT_<drawing>' },
        drawing: { type: 'string', description: 'DRWG 名，如 AGT001' },
        sheet: { type: 'string', description: 'SHEET 名，默认 S1' },
        view: { type: 'string', description: 'VIEW 名，默认 V1' },
        scale: { type: 'number', description: '比例分母（VSCA=1/scale），默认 50 即 1:50' },
        size: { type: 'array', items: { type: 'number' }, description: '纸张宽x高 mm，默认 [1189,841] A0' },
        xy: { type: 'array', items: { type: 'number' }, description: '视图中心 X/Y，默认 [500,400]' },
        addAll: { type: 'boolean', description: '是否 ADD ALL 体积框选入绘（ADD ALL 尚未实测，默认 false）' },
        addTypes: { type: 'string', description: 'ADD ALL 类型列表，默认 "BRAN EQUIP STEEL"' },
        range: {
          type: 'object',
          description: '世界坐标体积框（配合 addAll）',
          properties: {
            xmin: { type: 'number' }, xmax: { type: 'number' },
            ymin: { type: 'number' }, ymax: { type: 'number' },
            zmin: { type: 'number' }, zmax: { type: 'number' }
          }, required: ['xmin', 'xmax', 'ymin', 'ymax', 'zmin', 'zmax']
        }
      },
      required: ['drawing']
    }
  },
  {
    name: 'pdms_iso_generate',
    description: 'ISO 等轴测批量出图（2026-10-14 TTY 全链路实测通过）：对每根给定管线执行 CE→ISODRAFTMODE→options→DETAIL→isodraw 回读→EXIT，仅用自带模板/选项（零 UDA/服务器/XML）。返回每管线 status/plotfile。DXF 输出需传 dxf 版 optionsFile（File AND DXF）。',
    inputSchema: {
      type: 'object',
      properties: {
        pipes: { type: 'array', items: { type: 'string' }, description: '管线名列表，如 ["/2001","/2003"]（带不带前导 / 均可）' },
        optionsFile: { type: 'string', description: 'Isodraft 选项文件路径（建议 %ENV% 形式）。默认 %SAMISO%/std/previewisostd.txt（标准，出 .plt）；出 DXF 传 dxf 变体（File AND DXF 行）' },
        maxPipes: { type: 'number', description: '最多处理条数，默认 10' }
      },
      required: ['pipes']
    }
  },
  {
    name: 'pdms_list_drawlist',
    description: '列出指定 VIEW 的成员（绘制清单内容）。view 必须用 drawing-rooted 引用：/DRWG/SHEET/VIEW（含 registry 名会 (2,109)）。结果经结果文件返回。',
    inputSchema: {
      type: 'object',
      properties: { view: { type: 'string', description: '如 /AGT001/S1/V1' } },
      required: ['view']
    }
  },
  {
    name: 'pdms_create_equipment',
    description: '高层工具：参数化创建设备（移植方圆 TestAttribute 插件 22 类：水平/立式容器、换热器、塔、泵、底座、检修空间）。在目标 zone（设备部模型）下生成 CP（NEW EQUI + 嵌套基本体，官方 router_tutorial.dat 语法）执行并回读成员验证。需 DESIGN 模块会话；func 类/目录件在目标工程不存在时传 func:"" 或 noCatr:true 省略。参数表: skill pdms-parametric-equipment / equipment-catalog.md。',
    inputSchema: {
      type: 'object',
      properties: {
        zone: { type: 'string', description: '目标 zone（设备部模型），如 /PROJ/SYS/1；设备建其下，部门随模型走' },
        type: { type: 'string', description: '设备类型 key（22）: horizontal_vessel, dished_flan_with_nozzles, dished_both_ends_with_nozzles, dished_both_ends, flan_both_ends, dished_top_tank, flat_top_tank, flanged_dished_top, dish_top_and_bottom, dish_top_cone_bottom, vertical_head_exchanger, tower, reducing_turn_tower, reducing_tower, center_line_offset_pump, center_line_pump, center_vertical_pump, four_box_base, two_exchange_base, two_skirt_base, two_prism_base, maintenance_space' },
        name: { type: 'string', description: '设备名（必填；重名时返回 EQUIP_EXISTS 不覆盖）' },
        pos: { type: 'object', description: '设备原点世界坐标（默认全 0）', properties: { east: { type: 'number' }, north: { type: 'number' }, up: { type: 'number' } } },
        params: { type: 'object', description: '该类型尺寸参数（mm），如 {diameter, dishHeight, length, ...}；tower/泵类带代码默认值；catref=管嘴目录件' },
        func: { type: 'string', description: '设备 FUNC 类名（默认取该类型 vendor 类名，如 HorizontalVessel/Tower）；目标工程未定义该类时传 "" 省略' },
        catref: { type: 'string', description: '全局覆盖管嘴目录件（如 /AAZFBD0JJ）；须在工程目录中存在' },
        noCatr: { type: 'boolean', description: '测试模式：省略全部管嘴 CATR（工程无对应目录时用）' },
        spco: { type: 'boolean', description: 'CATR 加 SPCO（规格协调模式，同官方教程）' },
        nozzles: { type: 'array', description: '附加管嘴（管嘴表导入语义）', items: { type: 'object', properties: {
          name: { type: 'string', description: '管嘴名（默认 NA1, NA2...）' },
          pos: { type: 'object', description: '相对设备原点局部坐标（优先）', properties: { east: { type: 'number' }, north: { type: 'number' }, up: { type: 'number' } } },
          radius: { type: 'number', description: 'SufFaceDis: 到设备原点平面径向距离（未给 pos 时用）' },
          angle: { type: 'number', description: 'NozzAngle: 自北顺时针俯视角度 0-360（0=N 90=E，与 get_equipment 导出口径一致；未给 pos 时用）' },
          elevOffset: { type: 'number', description: 'EleNozz: 相对设备原点的标高偏移（radius+angle 模式用）' },
          catr: { type: 'string', description: '管嘴目录件' },
          ori: { type: 'string', description: "朝向串 'Y is S and Z is W'；缺省: 四正方向自动径向向外，其余自动朝上并告警" },
          heig: { type: 'number', description: '管嘴长（默认 200）' }
        } } },
        dryRun: { type: 'boolean', description: '只生成 CP/PML 返回、不下发 PDMS（首跑前审计用）' },
        debug: { type: 'boolean', description: '返回中附带生成的 PML/CP 全文（审计用）' }
      },
      required: ['zone', 'type', 'name', 'params']
    }
  },
  {
    name: 'pdms_get_equipment',
    description: '反向参数化（对应 vendor 插件 Callback/getNozzInfo）：读回设备 type/name/FUNC/定位(世界) + 全部基本体（名/局部位置/尺寸/朝向）+ 聚合管嘴表（SufFaceDis 平面径向距 / EleNozz 标高 / NozzAngle 俯视角度 / Catref）。用于建后验证、管嘴表导出。',
    inputSchema: {
      type: 'object',
      properties: {
        ref: { type: 'string', description: '设备全引用，如 /PROJ/SYS/1/EQUIP/V1' },
        dryRun: { type: 'boolean', description: '只生成 PML 返回、不下发 PDMS' },
        debug: { type: 'boolean', description: '返回中附带生成的 PML' }
      },
      required: ['ref']
    }
  },
  {
    name: 'pdms_select_module',
    description: '多模块并存时选择桥接目标模块（Design/Paragon/Draft 等同时打开 PDMS 时使用）。设置后后续所有 pdms_* 调用都指向该模块端口。传 module=空字符串可清除选择回退默认。返回当前可用模块清单与生效目标。',
    inputSchema: {
      type: 'object',
      properties: {
        module: { type: 'string', description: '目标模块名：DESIGN / PARAGON / DRAFT / MONITOR / ISO（不区分大小写，DRAW 等价于 DRAFT）。留空则清除选择。' }
      },
      required: []
    }
  }
];

/* ----------------------------- MCP 请求分发 ----------------------------- */
async function dispatchTool(name, args) {
  switch (name) {
    case 'pdms_status': {
      const code = pmlResultOpen().concat([
        'writefile $!F1 |PING_OK|',
        'closefile $!F1'
      ]).join('\n');
      const r = await callPdms(code);
      const online = !(r.error && /offline|timeout|closed without response/.test(r.error));
      const t = await resolveTarget();
      const inst = await discoverInstances();
      return { connected: online, transport: TRANSPORT, host: t.host, port: t.port, targetModule: t.module, via: t.via, modules: inst, queue: QUEUE_DIR, detail: r.error || 'ok', output: r.output };
    }
    case 'pdms_select_module': {
      const inst = await discoverInstances();
      const m = (args.module || '').trim().toUpperCase();
      if (m) {
        const norm = (m === 'DRAW') ? 'DRAFT' : m;
        const hit = inst.filter(i => String(i.module).toUpperCase() === norm)[0];
        if (!hit) {
          return { ok: false, error: '未找到模块 ' + m + ' 的实例。可用: ' + inst.map(i => i.module + '@' + i.port).join(', '), modules: inst };
        }
        state.targetModule = norm;
      } else {
        state.targetModule = null;
      }
      const t = await resolveTarget();
      return { ok: true, targetModule: t.module, port: t.port, via: t.via, modules: inst };
    }
    case 'pdms_probe_read': {
      // 只读 op：桥接 op 通道首跑冒烟（验证 UI 线程派发 + 跨线程 DB 读）+ 出图选元素
      const r = await callOp('probe_read', {}, 120000);
      return { ok: r.ok, output: r.output, error: r.error };
    }
    case 'pdms_draw': {
      const prm = { annotate: args.annotate === false ? 'false' : 'true' };
      if (args.start) prm.start = String(args.start);
      if (args.drawingType) prm.drawingType = String(args.drawingType);
      if (args.scaleDen) prm.scaleDen = String(args.scaleDen);
      if (args.elements) prm.elements = (Array.isArray(args.elements) ? args.elements.join(' ') : String(args.elements));
      if (args.box) {
        prm.xmin = String(args.box.xmin); prm.xmax = String(args.box.xmax);
        prm.ymin = String(args.box.ymin); prm.ymax = String(args.box.ymax);
        prm.zmin = String(args.box.zmin); prm.zmax = String(args.box.zmax);
      }
      if (args.backs) prm.backs = String(args.backs);
      if (args.rrsf) prm.rrsf = String(args.rrsf);
      if (args.hrsf) prm.hrsf = String(args.hrsf);
      const r = await callOp('create_drawing', prm, 600000);
      return { ok: r.ok, output: r.output, error: r.error };
    }
    case 'pdms_export_dxf': {
      if (!args.sheet) return { ok: false, error: 'sheet 必填（pdms_draw 返回的 SHEET 行）' };
      const prm = { sheet: String(args.sheet) };
      if (args.outDir) prm.outDir = String(args.outDir);
      const r = await callOp('export_dxf', prm, 300000);
      return { ok: r.ok, output: r.output, error: r.error };
    }
    case 'pdms_export_pdf': {
      if (!args.sheet) return { ok: false, error: 'sheet 必填（pdms_draw 返回的 SHEET 行）' };
      const prm = { sheet: String(args.sheet) };
      if (args.outDir) prm.outDir = String(args.outDir);
      const r = await callOp('export_pdf', prm, 300000);
      return { ok: r.ok, output: r.output, error: r.error };
    }
    case 'pdms_exec': {
      const text = String(args.code || '').trim();
      if (!text) return { ok: false, error: 'code is required' };
      const blocked = checkExecGuardrails(text);
      if (blocked) return { ok: false, error: 'GUARDRAIL 拒绝：' + blocked, blocked: true };
      const r = await callPdms(text);
      return { ok: r.ok, output: r.output, error: r.error };
    }
    case 'pdms_query': {
      // expression = PML 表达式（如 !!ce.type / !!ce.vsca / !!ce.dbType / !!ce.name）。
      // 结果写文件返回 —— RunInPdms 内联 PML 的变量回读不可靠（viewTest [3] 已证），
      // 文件落盘是唯一稳定回读通道（TTY p1–p15 全链验证）。
      // 赋值必须放在 handle ANY 块内：CE 为空/属性不存在时表达式抛错，
      // 若在块外宏会中止于 closefile 之前 → 结果文件永不落盘 → 桥接等待卡死（2026-10-14 实测教训）
      const code = pmlResultOpen().concat([
        '!Q1 = ERR_UNSET',
        'handle ANY',
        '!Q1 = ' + args.expression,
        'endhandle',
        'writefile $!F1 |RESULT_$!Q1|',
        'closefile $!F1'
      ]).join('\n');
      const r = await callPdms(code);
      return { ok: r.ok, value: r.output, error: r.error };
    }
    case 'pdms_create_drawing': {
      const code = pmlCreateDrawing(args);
      const r = await callPdms(code);
      return { ok: r.ok, output: r.output, error: r.error };
    }
    case 'pdms_list_drawlist': {
      // view = drawing-rooted 引用 /DRWG/SHEET/VIEW（已验证；含 registry 名会 (2,109)）
      const v = String(args.view);
      const ref = v.charAt(0) === '/' ? v : '/' + v;
      const code = pmlResultOpen().concat([
        '!F2 = false',
        'CE ' + ref,
        'handle ANY',
        '  !F2 = true',
        'endhandle',
        '-- 注意: if (!F2) 中 ! 是变量前缀，!F2 为 true 即 CE 失败（勿读成"非 F2"）',
        'if (!F2) then',
        '  writefile $!F1 |VIEW_NOT_FOUND|',
        'else',
        '  var !L1 coll all for ce',
        '  handle ANY',
        '    var !L1 ARRAY()',
        '  endhandle',
        '  !C1 = !L1.size()',
        '  writefile $!F1 |MEMBERS_$!C1|',
        '  do !i indices !L1',
        '    !M1 = !L1[!i]',
        "    !MS1 = 'ERRSTR'",
        '    !MS1 = !M1.string()',
        '    handle ANY',
        '    endhandle',
        '    writefile $!F1 |DM_$!MS1|',
        '  enddo',
        'endif',
        'writefile $!F1 |===LIST-END===|',
        'closefile $!F1'
      ]).join('\n');
      const r = await callPdms(code);
      return { ok: r.ok, output: r.output, error: r.error };
    }
    case 'pdms_iso_generate': {
      const code = pmlIsoGenerate(args);
      if (!code) return { ok: false, error: 'pipes 不能为空' };
      const r = await callPdms(code);
      const results = parseIsoResult(r.output);
      return { ok: r.ok, pipes: results, output: r.output, error: r.error };
    }
    case 'pdms_create_equipment': {
      const t = String(args.type || '');
      const spec = EQUIP_CATALOG[t];
      if (!spec) return { ok: false, error: '未知设备类型 "' + t + '"。可用: ' + Object.keys(EQUIP_CATALOG).join(', ') };
      if (!args.zone) return { ok: false, error: 'zone 必填（设备部模型的目标 zone，如 /PROJ/SYS/1）' };
      if (!args.name) return { ok: false, error: 'name 必填（设备名；智能体未获用户命名时按类型+序号建议，如 HV-01）' };
      const p = {};
      const missing = [];
      for (const kv of spec.params) {
        const k = kv[0], req = !!kv[1], def = kv[2];
        let v = (args.params && args.params[k] !== undefined && args.params[k] !== null && args.params[k] !== '') ? args.params[k] : def;
        if (v === undefined || v === null || v === '') {
          if (req && !(k === 'catref' && args.noCatr)) missing.push(k);
          continue;
        }
        if (typeof def !== 'string') {
          const n = Number(v);
          if (isNaN(n)) return { ok: false, error: '参数 ' + k + ' 须为数值: ' + v };
          v = n;
        }
        p[k] = v;
      }
      if (missing.length) return { ok: false, error: t + ' 缺必填参数: ' + missing.join(', ') + '（参数表: skill pdms-parametric-equipment references/equipment-catalog.md）' };
      if (args.catref) p.catref = args.catref;
      let built;
      try { built = pmlCreateEquipment({ zone: args.zone, type: t, name: String(args.name), pos: args.pos, params: p, func: args.func, noCatr: !!args.noCatr, spco: !!args.spco, nozzles: args.nozzles }); }
      catch (e) { return { ok: false, error: '参数计算失败: ' + e.message }; }
      if (args.dryRun) {
        const res = { ok: true, dryRun: true, type: t, name: String(args.name), parts: built.parts };
        if (built.oriSkipped.length) res.nozzleOriSkipped = built.oriSkipped;
        res.pml = built.code;
        return res;
      }
      const r = await callPdms(built.code);
      if (!r.ok) return { ok: false, error: r.error || 'PDMS 调用失败', output: r.output || '' };
      const res = parseEquipCreate(r.output, String(args.zone), String(args.name));
      if (built.oriSkipped.length) res.nozzleOriSkipped = built.oriSkipped; // 非四正方向附加管嘴未自动给径向朝向
      if (args.debug) res.pml = built.code;
      return res;
    }
    case 'pdms_get_equipment': {
      if (!args.ref) return { ok: false, error: 'ref 必填（设备全引用，如 /PROJ/SYS/1/EQUIP/V1）' };
      const code = pmlGetEquipment(args.ref);
      if (args.dryRun) return { ok: true, dryRun: true, pml: code };
      const r = await callPdms(code);
      if (!r.ok) return { ok: false, error: r.error || 'PDMS 调用失败', output: r.output || '' };
      const res = parseEquipGet(r.output);
      if (args.debug) res.pml = code;
      return res;
    }
    default:
      return { ok: false, error: 'unknown tool: ' + name };
  }
}

function toolResult(obj) {
  return { content: [{ type: 'text', text: JSON.stringify(obj, null, 2) }], isError: !!(obj && obj.ok === false && obj.error) };
}

/* ----------------------------- stdio JSON-RPC (frame-agnostic) ----------------------------- */
/* 同时兼容两种 stdio 传输，确保既能用轻量客户端测试，也能被标准 MCP 客户端（含
   WorkBuddy 内置的 @modelcontextprotocol/sdk）驱动：
   - 标准 MCP：Content-Length: N\r\n\r\n{json} 分帧
   - 轻量 NDJSON：逐行 JSON
   入站按首条消息自动识别；出站沿用客户端所用帧格式，保证对话同构。 */
let transport = null; // 'framed' | 'ndjson'
let inBuf = '';

function send(obj) {
  const str = JSON.stringify(obj);
  if (transport === 'framed') {
    const len = Buffer.byteLength(str, 'utf8');
    process.stdout.write('Content-Length: ' + len + '\r\n\r\n' + str);
  } else {
    process.stdout.write(str + '\n');
  }
}

function handleMessage(raw) {
  let msg;
  try { msg = JSON.parse(raw); } catch (e) { return; }
  if (Array.isArray(msg)) { msg.forEach(handleMessage); return; }
  const { id, method, params } = msg;

  if (method === 'initialize') {
    send({
      jsonrpc: '2.0', id,
      result: {
        protocolVersion: '2024-11-05',
        capabilities: { tools: {} },
        serverInfo: { name: 'pdms-bridge', version: '1.0.0' }
      }
    });
    return;
  }
  if (method === 'notifications/initialized' || method === 'initialized') return;
  if (method === 'tools/list') {
    send({ jsonrpc: '2.0', id, result: { tools: TOOLS } });
    return;
  }
  if (method === 'tools/call') {
    (async () => {
      try {
        const out = await runSerial(() => dispatchTool(params.name, params.arguments || {}));
        send({ jsonrpc: '2.0', id, result: toolResult(out) });
      } catch (e) {
        send({ jsonrpc: '2.0', id, result: { content: [{ type: 'text', text: JSON.stringify({ ok: false, error: e.message }) }], isError: true } });
      }
    })();
    return;
  }
  // 其他 method 忽略
}

function pump() {
  while (true) {
    if (transport === null) {
      if (/^content-length:/i.test(inBuf)) transport = 'framed';
      else if (inBuf.indexOf('\n') >= 0) transport = 'ndjson';
      else return; // 数据不足，等待下一块
    }
    if (transport === 'framed') {
      let headerEnd = inBuf.indexOf('\r\n\r\n'); let sepUse = 4;
      if (headerEnd < 0) { const a = inBuf.indexOf('\n\n'); if (a >= 0) { headerEnd = a; sepUse = 2; } }
      if (headerEnd < 0) return;
      const headerStr = inBuf.slice(0, headerEnd);
      const m = /content-length:\s*(\d+)/i.exec(headerStr);
      if (!m) { transport = 'ndjson'; continue; } // 头非法，回退 NDJSON
      const len = parseInt(m[1], 10);
      const bodyStart = headerEnd + sepUse;
      if (inBuf.length < bodyStart + len) return;
      const body = inBuf.slice(bodyStart, bodyStart + len);
      inBuf = inBuf.slice(bodyStart + len);
      handleMessage(body);
    } else { // ndjson
      const nl = inBuf.indexOf('\n');
      if (nl < 0) return;
      const line = inBuf.slice(0, nl);
      inBuf = inBuf.slice(nl + 1);
      if (line.trim()) handleMessage(line);
    }
  }
}

process.stdin.on('data', (chunk) => { inBuf += chunk.toString('utf8'); pump(); });
process.stdin.on('end', () => { /* 客户端关闭，进程自然退出 */ });

log('pdms-bridge-mcp started, target', HOST + ':' + PORT);

/* ----------------------------- 设备目录自检: node server.js --selftest-equip ----------------------------- */
if (require.main === module && process.argv[2] === '--selftest-equip') {
  let fail = 0;
  // 1) ORI 映射复现官方 dat 的 6 组 ORIA 证据
  const known = [
    ['Y IS N AND Z IS U', [0, 0, 0]],
    ['Y IS S AND Z IS U', [0, 0, 180]],
    ['Y IS N AND Z IS E', [0, 90, 0]],
    ['Y IS N AND Z IS W', [0, -90, 0]],
    ['Y IS U AND Z IS S', [90, 0, 0]],
    ['Y IS D AND Z IS N', [-90, 0, 0]]
  ];
  for (const [s, exp] of known) {
    const g = oriToOria(s);
    if (!g || g[0] !== exp[0] || g[1] !== exp[1] || g[2] !== exp[2]) {
      fail++; console.error('ORI mismatch: ' + s + ' → ' + JSON.stringify(g) + ' 期望 ' + JSON.stringify(exp));
    }
  }
  // 2) 22 类默认/必填参数（缺省 1000）全部可 build，且每个 ORI 串可解 ORIA
  for (const [t, spec] of Object.entries(EQUIP_CATALOG)) {
    const p = {};
    for (const kv of spec.params) p[kv[0]] = (kv[2] !== undefined) ? kv[2] : 1000;
    try {
      const parts = spec.build(p);
      const bad = parts.filter(pt => pt.ori && typeof pt.ori === 'string' && !oriToOria(pt.ori));
      if (bad.length) { fail++; console.error('ORI 无解: ' + t + ' ' + bad.map(b => b.name + ':' + b.ori).join(', ')); }
      console.error(t + ' → ' + parts.length + ' parts');
    } catch (e) { fail++; console.error('build 失败: ' + t + ' ' + e.message); }
  }
  console.error(fail === 0 ? 'SELFTEST-EQUIP PASS (' + Object.keys(EQUIP_CATALOG).length + ' 类)' : 'SELFTEST-EQUIP FAIL (' + fail + ')');
  process.exit(fail === 0 ? 0 : 1);
}
