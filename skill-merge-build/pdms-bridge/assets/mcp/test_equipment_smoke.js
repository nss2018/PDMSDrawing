#!/usr/bin/env node
/*
 * test_equipment_smoke.js — 设备工具冒烟（不依赖 PDMS 在线）
 * 1) tools/list 含 pdms_create_equipment / pdms_get_equipment
 * 2) create 缺必填参数 → 校验错误
 * 3) create 合法参数 + dryRun → 返回生成的 CP/PML，抽查关键行
 * 4) create 合法参数（非 dryRun，桥离线）→ 干净报 offline（不崩）
 * 5) get dryRun → 返回 PML
 * 运行: node test_equipment_smoke.js
 */
'use strict';
/* 铁律: 本冒烟测试只允许 dryRun / 校验错误 / 假端口(9) 的离线调用，
 * 严禁向真实 18753 桥接下发非 dryRun 写操作（2026-10-14 教训）。 */
const { spawn } = require('child_process');
const path = require('path');

const cp = spawn(process.execPath, [path.join(__dirname, 'server.js')], { cwd: __dirname });
let buf = '';
const got = {};
let pass = 0, fail = 0;
function check(name, cond, extra) {
  if (cond) { pass++; console.error('PASS ' + name + (extra ? '  [' + extra + ']' : '')); }
  else { fail++; console.error('FAIL ' + name + (extra ? '  [' + extra + ']' : '')); }
}
function send(obj) { cp.stdin.write(JSON.stringify(obj) + '\n'); }
// 离线用例: 独立子进程指向关闭端口 9 → ECONNREFUSED，不碰真实桥
let cpOff = null, offBuf = '', offDone = false;
function sendOffline(obj, onResp) {
  cpOff = cpOff || spawn(process.execPath, [path.join(__dirname, 'server.js')], {
    cwd: __dirname, env: Object.assign({}, process.env, { PDMS_BRIDGE_PORT: '9', PDMS_BRIDGE_TIMEOUT: '3000' })
  });
  const child = cpOff;
  child.stdout.on('data', (d) => {
    offBuf += d.toString();
    let idx;
    while ((idx = offBuf.indexOf('\n')) >= 0) {
      const line = offBuf.slice(0, idx); offBuf = offBuf.slice(idx + 1);
      if (!line.trim()) continue;
      let m; try { m = JSON.parse(line); } catch (_) { continue; }
      if (m.id === obj.id) onResp(m.result.content[0].text);
    }
  });
  child.stderr.on('data', () => {});
  child.stdin.write(JSON.stringify(obj) + '\n');
}

cp.stderr.on('data', () => {});
cp.stdout.on('data', (d) => {
  buf += d.toString();
  let idx;
  while ((idx = buf.indexOf('\n')) >= 0) {
    const line = buf.slice(0, idx); buf = buf.slice(idx + 1);
    if (!line.trim()) continue;
    let m; try { m = JSON.parse(line); } catch (_) { continue; }
    const id = m.id;
    if (id === 1) {
      const names = m.result.tools.map(t => t.name);
      check('tools-list 含 2 个设备工具', names.includes('pdms_create_equipment') && names.includes('pdms_get_equipment'), names.length + ' 工具');
      send({ jsonrpc: '2.0', id: 2, method: 'tools/call', params: { name: 'pdms_create_equipment', arguments: { zone: '/SAMPLE/SYSTEM/1', type: 'horizontal_vessel', name: 'HV-TEST' } } });
    } else if (id === 2) {
      const t = JSON.parse(m.result.content[0].text);
      check('缺参校验', t.ok === false && /缺必填参数/.test(t.error || ''), (t.error || '').slice(0, 60));
      send({ jsonrpc: '2.0', id: 3, method: 'tools/call', params: { name: 'pdms_create_equipment', arguments: {
        zone: '/SAMPLE/SYSTEM/1', type: 'horizontal_vessel', name: 'HV-TEST', dryRun: true,
        pos: { east: 10000, north: -2000, up: 200 },
        params: { diameter: 2000, dishHeight: 250, length: 6000, xLength: 1500, yLengthBehind: 600, zLength: 400, offset: 1500 }
      } } });
    } else if (id === 3) {
      const t = JSON.parse(m.result.content[0].text);
      const pml = t.pml || '';
      check('dryRun 返回 PML', t.ok === true && t.dryRun === true && pml.length > 200);
      check('CP: NEW EQUI + AT 符号轴(N-2000→S)', pml.includes('NEW EQUI /HV-TEST') && /AT  E10000\.000  S2000\.000  U200\.000/.test(pml));
      check('CP: FUNC 行', pml.includes("FUNC  'HorizontalVessel'"));
      check('CP: DISH DIAM=d+1', pml.includes('DIAM  2001.000'));
      check('CP: DISH THIC 50', pml.includes('THIC  50.000'));
      check('CP: ORIA 90 0 0（Y is u and Z is s）', pml.includes('ORIA  90.000  0.000  0.000'));
      check('CP: 封头背面 ORIA -90 0 0', pml.includes('ORIA  -90.000  0.000  0.000'));
      check('CP: 存在性预检 CE /EQUIP/', pml.includes('CE /SAMPLE/SYSTEM/1/EQUIP/HV-TEST'));
      check('CP: 回读 type/func/成员', pml.includes('EQUIP_TYPE_') && pml.includes('EQUIP_FUNC_') && pml.includes('var !L1 coll all for ce'));
      check('CP: 回读赋值均在 handle 内（防卡桥）', /handle ANY\n\s+!TN = !!ce\.type/.test(pml) && /handle ANY\n\s+!FN = !!ce\.func/.test(pml));
      check('parts 摘要', Array.isArray(t.parts) && t.parts.length === 5, (t.parts || []).map(p => p.type + '/' + p.name).join(','));
      console.error('\n----- 生成的 CP（dryRun 摘录）-----');
      console.error(pml.split('\n').slice(0, 26).join('\n') + '\n  ...');
      // 离线用例: 假端口 9（独立子进程，绝不触达真实桥）
      sendOffline({ jsonrpc: '2.0', id: 40, method: 'tools/call', params: { name: 'pdms_create_equipment', arguments: {
        zone: '/SAMPLE/SYSTEM/1', type: 'center_line_offset_pump', name: 'P-1',
        params: {}, noCatr: true
      } } }, (text) => {
        const t = JSON.parse(text);
        check('桥离线时干净报错（假端口）', t.ok === false && /offline|connect failed|timeout/.test(t.error || ''), (t.error || '').slice(0, 60));
        if (cpOff) cpOff.kill();
      });
      send({ jsonrpc: '2.0', id: 5, method: 'tools/call', params: { name: 'pdms_get_equipment', arguments: { ref: '/SAMPLE/SYSTEM/1/EQUIP/HV-TEST', dryRun: true } } });
    } else if (id === 5) {
      const t = JSON.parse(m.result.content[0].text);
      const pml = t.pml || '';
      check('get dryRun PML 含成员属性回读', t.ok === true && pml.includes('!E1.diam') && pml.includes('!E1.catr') && pml.includes('MEM '));
      check('get PML 回读赋值在 handle 内（防卡桥）', /handle ANY\n\s+!TN = !!ce\.type/.test(pml));
      setTimeout(done, 500); // 给离线子进程回包留窗口
    }
  }
});
function done() {
  cp.kill();
  console.error(fail === 0 ? 'SMOKE PASS (' + pass + '/' + (pass + fail) + ')' : 'SMOKE FAIL (' + fail + ' 失败)');
  process.exit(fail === 0 ? 0 : 1);
}
send({ jsonrpc: '2.0', id: 1, method: 'tools/list' });
setTimeout(() => { console.error('SMOKE FAIL (timeout)'); cp.kill(); process.exit(1); }, 15000);
