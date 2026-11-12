#!/usr/bin/env node
/*
 * first_run_check.js — 进程内桥接首跑验证
 * 覆盖 ROADMAP.md §2 首跑清单 1–5 + MCP-GENERIC-TOOLS.md §6 项 6–10
 *
 * 用法:
 *   node first_run_check.js                # Phase A 只读（不改数据库）
 *   node first_run_check.js --write        # + Phase B 写入: 建图(草稿 AGT_REGI) + CRUD 闭环(自清理)
 *
 * 参数:
 *   --module <ref>      只读目标模块，默认 /SAMPLE/SYSTEM/1（改成实际设计模块，如登录所用）
 *   --crud-type <T>     CRUD 闭环元素类型，默认 FRAM（视项目实际可试 VALVE / PIPESPEC）
 *   --limit <n>         遍历/导出行数上限，默认 50
 *   --set-ref <ref> --set-attr <a> --set-value <v>
 *                       可选: 属性写验证(B8)，必须用户指定安全目标（草稿/测试模块元素）
 *
 * 证据: D:/pdmsAgent/bridge/first_run_<ts>.log（每步入参 + 原始输出 + PASS/FAIL/WARN/SKIP）
 * 依赖: PDMS 已启动且加载 drawingPDMS(PdmsBridge)；与 GUI 当前状态无关（除 A5 人工目视）
 */
'use strict';
const { spawn } = require('child_process');
const fs = require('fs');
const path = require('path');

// ---------------- 参数 ----------------
const argv = process.argv.slice(2);
function getArg(name, dflt) { const i = argv.indexOf(name); return i >= 0 ? argv[i + 1] : dflt; }
const HAS_WRITE = argv.includes('--write');
const MODULE = getArg('--module', '/SAMPLE/SYSTEM/1');
const CRUD_TYPE = getArg('--crud-type', 'FRAM');
const LIMIT = parseInt(getArg('--limit', '50'), 10);
const SET_REF = getArg('--set-ref', '');
const SET_ATTR = getArg('--set-attr', '');
const SET_VALUE = getArg('--set-value', '');

const BRIDGE_DIR = 'D:/pdmsAgent/bridge';
const RESULT_FILE = 'D:/pdmsAgent/bridge/result.txt';
const TS = new Date().toISOString().replace(/[:.]/g, '-').slice(0, 19);
const LOG_PATH = BRIDGE_DIR + '/first_run_' + TS + '.log';
const logLines = [];
function log(s) { process.stdout.write(s + '\n'); logLines.push(s); }

// ---------------- PML 模板（已验证语法 + [未验证] 标注，见 MCP-GENERIC-TOOLS.md） ----------------
function pmlPath(jsPath) { return '/' + jsPath.replace(/\//g, '\\'); }
function pmlResultOpen() {
  const p = pmlPath(RESULT_FILE);
  return [
    'openfile ' + p + ' write !F1',
    'handle ANY',
    '  openfile ' + p + ' overwrite !F1',
    'endhandle'
  ];
}
const pmlSearch = (mod, limit) => pmlResultOpen().concat([
  '!N1 = 0',
  '!TR1 = false',
  'do !E1 element ' + mod,          // [未验证] 模块遍历
  '  !T1 = !E1.type',               // [未验证] 句柄 .type
  '  if (!N1 < ' + limit + ') then',
  '    !S1 = !E1.string()',
  '    writefile $!F1 |ROW$!S1|',
  '    !N1 = !N1 + 1',
  '  else',
  '    !TR1 = true',
  '  endif',
  'enddo',
  'writefile $!F1 |SEARCH_END rows_$!N1|',
  'if (!TR1) then',
  '  writefile $!F1 |TRUNCATED|',
  'endif',
  'closefile $!F1'
]).join('\n');

const pmlHandle = (ref) => pmlResultOpen().concat([
  '!S1 = ERR_UNSET',
  '!E1 = ' + ref,                   // [未验证] 元素句柄赋值
  'handle ANY',
  'endhandle',
  '!S2 = ERR_UNSET',
  '!S2 = !E1.string()',
  'handle ANY',
  'endhandle',
  'writefile $!F1 |HANDLE$!S2|',
  'closefile $!F1'
]).join('\n');

const pmlAttrs = (ref) => pmlResultOpen().concat([
  '!E1 = ' + ref,
  'handle ANY',
  'endhandle',
  '!A1 = ERR_UNSET',
  '!A1 = !E1.type',
  'handle ANY',
  'endhandle',
  '!B1 = ERR_UNSET',
  '!B1 = !E1.name',
  'handle ANY',
  'endhandle',
  '!C1 = ERR_UNSET',
  '!C1 = !E1.desc',
  'handle ANY',
  'endhandle',
  'writefile $!F1 |ATTR type_$!A1|',
  'writefile $!F1 |ATTR name_$!B1|',
  'writefile $!F1 |ATTR desc_$!C1|',
  'closefile $!F1'
]).join('\n');

const pmlExport = (mod, limit) => pmlResultOpen().concat([
  'writefile $!F1 |TAG,TYPE,DIA,WALL|',
  '!N1 = 0',
  '!TR1 = false',
  'do !E1 element ' + mod,
  '  if (!N1 < ' + limit + ') then',
  '    !S1 = !E1.name',
  '    !T1 = !E1.type',
  '    !A1 = 0',
  '    !A1 = !E1.dia',
  '    handle ANY',
  '    endhandle',
  '    !B1 = 0',
  '    !B1 = !E1.wall',
  '    handle ANY',
  '    endhandle',
  '    writefile $!F1 |$!S1,$!T1,$!A1,$!B1|',
  '    !N1 = !N1 + 1',
  '  else',
  '    !TR1 = true',
  '  endif',
  'enddo',
  'writefile $!F1 |===EXPORT_END rows_$!N1===|',
  'if (!TR1) then',
  '  writefile $!F1 |TRUNCATED|',
  'endif',
  'closefile $!F1'
]).join('\n');

const pmlCrud = (mod, type) => pmlResultOpen().concat([
  '!F2 = false',
  'CE ' + mod,
  'handle ANY',
  '  !F2 = true',
  'endhandle',
  'if (!F2) then',
  '  writefile $!F1 |CRUD_ABORT module not found|',
  'else',
  '  !F3 = false',
  '  CE ' + mod + '/' + type + '/AGT_T1',
  '  handle ANY',
  '    !F3 = true',
  '  endhandle',
  '  if (!F3) then',
  '    writefile $!F1 |CRUD_ABORT target exists|',
  '  else',
  '    CE ' + mod,
  '    NEW ' + type + ' /AGT_T1',
  '    END',
  '    !F4 = false',
  '    CE ' + mod + '/' + type + '/AGT_T1',
  '    handle ANY',
  '      !F4 = true',
  '    endhandle',
  '    if (!F4) then',
  '      writefile $!F1 |CRUD_CREATE_FAIL|',
  '    else',
  '      !T1 = !!ce.type',
  '      writefile $!F1 |CRUD_CREATED type_$!T1|',
  '      CE ' + mod + '/' + type + '/AGT_T1',
  '      DELETE ' + type,
  '      !F5 = false',
  '      CE ' + mod + '/' + type + '/AGT_T1',
  '      handle ANY',
  '        !F5 = true',
  '      endhandle',
  '      if (!F5.not()) then',
  '        writefile $!F1 |CRUD_OK deleted|',
  '      else',
  '        writefile $!F1 |CRUD_DELETE_FAIL still exists|',
  '      endif',
  '    endif',
  '  endif',
  'endif',
  'closefile $!F1'
]).join('\n');

const pmlSetAttr = (ref, attr, value) => {
  const v = /^-?\d+(\.\d+)?$/.test(value) ? value : "'" + value + "'";
  return pmlResultOpen().concat([
    '!E1 = ' + ref,
    'handle ANY',
    'endhandle',
    '!E1.' + attr + ' = ' + v,       // [未验证] 属性写
    '!R1 = ERR_UNSET',
    '!R1 = !E1.' + attr,
    'handle ANY',
    'endhandle',
    'writefile $!F1 |SET_OK ' + attr + '_$!R1|',
    'closefile $!F1'
  ]).join('\n');
};

// ---------------- MCP stdio 客户端 ----------------
let server;
let seq = 0;
const pending = new Map();
function mcpStart() {
  server = spawn(process.execPath, [path.join(__dirname, 'server.js')], { stdio: ['pipe', 'pipe', 'pipe'] });
  let buf = '';
  server.stdout.on('data', (d) => {
    buf += d.toString();
    let idx;
    while ((idx = buf.indexOf('\n')) >= 0) {
      const line = buf.slice(0, idx); buf = buf.slice(idx + 1);
      if (!line.trim()) continue;
      let m; try { m = JSON.parse(line); } catch (_) { continue; }
      if (m.id != null && pending.has(m.id)) { const fn = pending.get(m.id); pending.delete(m.id); fn(m); }
    }
  });
  server.stderr.on('data', (d) => { log('  [server] ' + d.toString().trim()); });
}
function mcpCall(name, args) {
  const id = ++seq;
  return new Promise((resolve) => {
    const timer = setTimeout(() => { pending.delete(id); resolve({ error: 'client timeout 60s' }); }, 60000);
    pending.set(id, (m) => {
      clearTimeout(timer);
      let parsed = { error: 'no result' };
      try {
        const text = m.result && m.result.content && m.result.content[0] && m.result.content[0].text;
        if (text) parsed = JSON.parse(text);
      } catch (e) { parsed = { error: 'parse failed: ' + e.message }; }
      resolve(parsed);
    });
    server.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method: 'tools/call', params: { name, arguments: args } }) + '\n');
  });
}

// ---------------- 步骤执行器 ----------------
const steps = [];
async function runStep(id, name, input, fn) {
  log('');
  log('[' + id + '] ' + name);
  if (input !== undefined) log('  INPUT : ' + JSON.stringify(input));
  const t0 = Date.now();
  let r;
  try { r = await fn(); }
  catch (e) { r = { status: 'FAIL', detail: 'exception: ' + e.message, raw: '' }; }
  const verdict = r.status || 'FAIL';
  log('  RESULT: ' + verdict + ' (' + (Date.now() - t0) + 'ms) ' + (r.detail || ''));
  if (r.raw) {
    log('  RAW   :');
    for (const l of String(r.raw).split('\n')) { if (l.trim()) log('    | ' + l); }
  }
  steps.push({ id, name, verdict, detail: r.detail || '' });
  return r;
}
function linesOf(r) { return String((r && r.output) || '').split('\n'); }

// ---------------- 主流程 ----------------
async function main() {
  log('=== 首跑验证 ' + TS + ' ===');
  log('module=' + MODULE + '  write=' + HAS_WRITE + '  crud-type=' + CRUD_TYPE + '  limit=' + LIMIT);
  mcpStart();

  // A1: 桥接在线
  const a1 = await runStep('A1', 'pdms_status → PING_OK', {}, async () => {
    const r = await mcpCall('pdms_status', {});
    const ok = r.connected === true && /PING_OK/.test(r.output || '');
    return { status: ok ? 'PASS' : 'FAIL', detail: ok ? '桥接在线' : 'detail=' + (r.detail || r.error || '无 PING_OK'), raw: r.output || '' };
  });
  if (a1.status !== 'PASS') {
    log('');
    log('A1 失败 → 后续步骤跳过。检查: PDMS 是否已启动？drawingPDMS.dll(含 PdmsBridge) 是否已加载？端口 18753 是否监听？');
    finish(1);
    return;
  }

  // A2: 当前 CE 上下文（信息性）
  await runStep('A2', 'pdms_query !!ce.type → 当前上下文', { expression: '!!ce.type' }, async () => {
    const r = await mcpCall('pdms_query', { expression: '!!ce.type' });
    const m = /RESULT_(.*)/.exec(String(r.value || ''));
    const v = m ? m[1] : String(r.value || r.error || '');
    return { status: /ERR_UNSET/.test(v) ? 'WARN' : 'PASS', detail: '当前 CE 上下文 = ' + v, raw: String(r.value || '') };
  });

  // A7: 模块遍历（先于 A6，产出首个元素引用）
  let firstRef = null;
  const a7 = await runStep('A7', 'pdms_exec 模块遍历 do element [首跑#7]', { module: MODULE, limit: LIMIT }, async () => {
    const r = await mcpCall('pdms_exec', { code: pmlSearch(MODULE, LIMIT) });
    const lines = linesOf(r);
    const mEnd = lines.map((l) => /SEARCH_END rows_(\d+)/.exec(l)).find(Boolean);
    const rows = lines.filter((l) => l.startsWith('ROW'));
    const n = mEnd ? parseInt(mEnd[1], 10) : -1;
    const hasTrunc = lines.some((l) => l.trim() === 'TRUNCATED');
    if (!mEnd) return { status: 'FAIL', detail: '无 SEARCH_END（宏中止? error=' + (r.error || '无') + '）', raw: r.output || r.error || '' };
    firstRef = rows.length ? rows[0].slice(3) : null;
    const ok = n >= 1 && rows.length === n;
    if (!ok) return { status: 'FAIL', detail: 'rows=' + n + ' 实际 ROW 行数=' + rows.length + '（不一致或为空）', raw: r.output };
    const d = '遍历 ' + n + ' 行' + (hasTrunc ? '（TRUNCATED，模块元素 > ' + LIMIT + '）' : '') + '；首个引用=' + firstRef;
    return { status: 'PASS', detail: d, raw: r.output };
  });

  // A6: 元素句柄赋值 + string() 回读 [首跑#6]
  if (firstRef) {
    await runStep('A6', 'pdms_exec 元素句柄 !E1=<ref> + string() [首跑#6]', { ref: firstRef }, async () => {
      const r = await mcpCall('pdms_exec', { code: pmlHandle(firstRef) });
      const m = /HANDLE(.*)/.exec(String(r.output || ''));
      const v = m ? m[1] : '';
      const ok = v === firstRef;
      return { status: ok ? 'PASS' : 'FAIL', detail: ok ? '句柄回读一致' : '回读=' + (v || '(空)') + ' 期望=' + firstRef + ' error=' + (r.error || ''), raw: r.output || r.error || '' };
    });
  }

  // A8r: 属性读（默认值+handle ANY 保护）[首跑#8 读侧]
  if (firstRef) {
    await runStep('A8r', 'pdms_exec 属性读 type/name/desc [首跑#8-读]', { ref: firstRef }, async () => {
      const r = await mcpCall('pdms_exec', { code: pmlAttrs(firstRef) });
      const lines = linesOf(r);
      const gt = lines.map((l) => /ATTR (type|name|desc)_(.*)/.exec(l)).filter(Boolean);
      const bad = gt.filter((m) => m[2] === 'ERR_UNSET');
      const detail = gt.map((m) => m[1] + '=' + m[2]).join('  ');
      if (bad.length === 2 && /ATTR type_ERR/.test(detail)) return { status: 'FAIL', detail: 'type/name 均读取失败（句柄/属性语法?）', raw: r.output || r.error || '' };
      return { status: 'PASS', detail: detail + '（desc 允许 ERR_UNSET）', raw: r.output || '' };
    });
  }

  // A9: export_table 首跑 [首跑#9]
  await runStep('A9', 'pdms_exec 属性表导出(无类型过滤) [首跑#9]', { module: MODULE, limit: LIMIT }, async () => {
    const r = await mcpCall('pdms_exec', { code: pmlExport(MODULE, LIMIT) });
    const lines = linesOf(r);
    const mEnd = lines.map((l) => /===EXPORT_END rows_(\d+)===/.exec(l)).find(Boolean);
    const data = lines.filter((l) => l.includes(',') && !l.startsWith('TAG') && !l.startsWith('===') && !/^(SEARCH_END|TRUNCATED|ROW)/.test(l));
    if (!mEnd) return { status: 'FAIL', detail: '无 EXPORT_END（宏中止? error=' + (r.error || '无') + '）', raw: r.output || r.error || '' };
    const n = parseInt(mEnd[1], 10);
    const ok = n >= 1 && data.length === n;
    if (!ok) return { status: n === 0 ? 'WARN' : 'FAIL', detail: 'rows=' + n + ' 数据行=' + data.length + (n === 0 ? '（模块为空或遍历问题）' : ''), raw: r.output || '' };
    return { status: 'PASS', detail: 'CSV ' + n + ' 行（表头 TAG,TYPE,DIA,WALL），P1 export_table 数据链路成立', raw: r.output };
  });

  // A5: GUI 人工目视（无法自动化）
  log('');
  log('[A5] GUI 目视确认：在 PDMS 界面核对当前模块/图纸状态，截图留证（人工步骤）');
  steps.push({ id: 'A5', name: 'GUI 目视确认（人工）', verdict: 'MANUAL', detail: '截图留证' });

  if (HAS_WRITE) {
    // B3: 建图（草稿 AGT_REGI，验证含 ^^ 判定）
    const b3 = await runStep('B3', 'pdms_create_drawing AGT001（含 ^^ 污染判定）', { drawing: 'AGT001' }, async () => {
      const r = await mcpCall('pdms_create_drawing', { drawing: 'AGT001' });
      const lines = linesOf(r);
      const get = (k) => { const m = lines.map((l) => new RegExp(k + '_(.*)').exec(l)).find(Boolean); return m ? m[1] : undefined; };
      const t = get('VIEW_TYPE'), vsca = get('VIEW_VSCA'), vtyp = get('VIEW_VTYPE'), db = get('VIEW_DB');
      const errTxt = String(r.error || '');
      if (/^\^/.test(errTxt) || errTxt.includes('^^')) {
        return { status: 'FAIL', detail: '★重大发现: 进程内 $M 的 CP 行被 ^^ 污染（CRUD 路径需纯 PML 等价方案）', raw: errTxt };
      }
      if (t !== 'VIEW') return { status: 'FAIL', detail: 'type=' + t + ' error=' + errTxt, raw: r.output || errTxt };
      const d = 'type=VIEW vsca=' + vsca + ' vtyp=' + vtyp + ' db=' + db;
      const st = /READFAIL/.test(String(vsca) + String(vtyp)) ? 'WARN' : (db === 'PADD' ? 'WARN' : 'PASS');
      const note = db === 'PADD' ? '（会话级存储: 需 SET MDB 决策, ROADMAP §2-3）' : '';
      return { status: st, detail: d + note + errTxt, raw: r.output || '' };
    });
    if (b3.status === 'FAIL' && /污染/.test(b3.detail)) {
      log('');
      log('B3 命中 ^^ 污染 → B8/B10（CP 写链）跳过，待纯 PML 等价方案。');
      steps.push({ id: 'B8', name: '属性写', verdict: 'SKIP', detail: '被 ^^ 污染判定阻塞' });
      steps.push({ id: 'B10', name: 'CRUD 闭环', verdict: 'SKIP', detail: '被 ^^ 污染判定阻塞' });
    } else {
      // B8: 属性写（需用户指定安全目标）
      if (SET_REF && SET_ATTR && SET_VALUE !== '') {
        await runStep('B8', 'pdms_exec 属性写 + 回读 [首跑#8-写]', { ref: SET_REF, attr: SET_ATTR, value: SET_VALUE }, async () => {
          const r = await mcpCall('pdms_exec', { code: pmlSetAttr(SET_REF, SET_ATTR, SET_VALUE) });
          const m = /SET_OK\s+\w+_(.*)/.exec(String(r.output || ''));
          const v = m ? m[1] : '';
          const ok = v !== '' && v !== 'ERR_UNSET' && v.includes(SET_VALUE);
          return { status: ok ? 'PASS' : 'FAIL', detail: '回读=' + (v || '(空)') + ' error=' + (r.error || ''), raw: r.output || r.error || '' };
        });
      } else {
        log('');
        log('[B8] 属性写验证未启用（需 --set-ref <元素引用> --set-attr <属性> --set-value <值>，指定安全目标）');
        steps.push({ id: 'B8', name: '属性写 [首跑#8-写]', verdict: 'SKIP', detail: '未指定 --set-ref' });
      }
      // B10: CRUD 闭环（自清理）[首跑#10]
      await runStep('B10', 'pdms_exec CRUD 闭环 NEW→回读→DELETE→确认消失 [首跑#10]', { module: MODULE, type: CRUD_TYPE, name: 'AGT_T1' }, async () => {
        const r = await mcpCall('pdms_exec', { code: pmlCrud(MODULE, CRUD_TYPE) });
        const out = String(r.output || '');
        let st, d;
        if (out.includes('CRUD_OK')) { st = 'PASS'; d = '建→回读→删→确认消失 全链通过'; }
        else if (out.includes('CRUD_ABORT target exists')) { st = 'FAIL'; d = '目标已存在（安全中止，未写入）：' + MODULE + '/' + CRUD_TYPE + '/AGT_T1，先清理或换名'; }
        else if (out.includes('CRUD_ABORT module not found')) { st = 'FAIL'; d = '模块不存在: ' + MODULE; }
        else if (out.includes('CRUD_CREATE_FAIL')) { st = 'FAIL'; d = 'NEW ' + CRUD_TYPE + ' 失败（类型在此上下文无效? 试 --crud-type VALVE/PIPSPEC）'; }
        else if (out.includes('CRUD_DELETE_FAIL')) { st = 'FAIL'; d = '★删除失败，元素残留需人工清理: ' + MODULE + '/' + CRUD_TYPE + '/AGT_T1'; }
        else { st = 'FAIL'; d = '无预期标记（宏中止? error=' + (r.error || '无') + '）'; }
        return { status: st, detail: d, raw: out || r.error || '' };
      });
    }
  } else {
    log('');
    log('（Phase B 未启用：加 --write 运行建图 + CRUD 闭环；属性写需另加 --set-ref/--set-attr/--set-value）');
  }

  // 汇总
  log('');
  log('=== 首跑汇总 ===');
  for (const s of steps) log('  ' + s.id.padEnd(4) + ' ' + s.verdict.padEnd(6) + ' ' + s.name + (s.detail ? ' — ' + s.detail : ''));
  const fails = steps.filter((s) => s.verdict === 'FAIL').length;
  log('');
  log('FAIL=' + fails + '  证据日志: ' + LOG_PATH);
  finish(fails === 0 ? 0 : 1);
}

function finish(code) {
  try { fs.writeFileSync(LOG_PATH, logLines.join('\n'), 'utf8'); } catch (e) { console.error('write log failed: ' + e.message); }
  try { if (server) server.kill(); } catch (_) {}
  setTimeout(() => process.exit(code), 100);
}

main();
