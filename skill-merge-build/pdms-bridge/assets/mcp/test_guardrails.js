#!/usr/bin/env node
/*
 * test_guardrails.js — pdms_exec 护栏冒烟测试（2026-10-14 P0 回归）
 * 用法: node test_guardrails.js
 * 不依赖 PDMS/网络：三条用例均在护栏层被拒（GUARDRAIL），不应触碰 TCP。
 */
'use strict';
const { spawn } = require('child_process');
const path = require('path');

const p = spawn(process.execPath, [path.join(__dirname, 'server.js')], { stdio: ['pipe', 'pipe', 'pipe'] });
let out = '';
p.stdout.on('data', (d) => { out += d; });
p.stderr.on('data', () => {});

const cases = [
  { id: 2, name: 'pdms_exec', arguments: { code: 'QUIT' }, expect: '命中黑名单' },
  { id: 3, name: 'pdms_exec', arguments: { code: "openfile /C:\\temp\\x.txt write !F1\nwritefile $!F1 |x|\nclosefile $!F1" }, expect: '沙箱外' },
  { id: 4, name: 'pdms_exec', arguments: { code: 'SET MDB /SAMPLE/SYS/DRA' }, expect: 'SET MDB' }
];

function send(o) { p.stdin.write(JSON.stringify(o) + '\n'); }
send({ jsonrpc: '2.0', id: 1, method: 'initialize', params: {} });
cases.forEach((c) => send({ jsonrpc: '2.0', id: c.id, method: 'tools/call', params: c }));

setTimeout(() => {
  try { p.kill(); } catch (_) {}
  let pass = 0;
  for (const line of out.split('\n')) {
    if (!line.trim()) continue;
    let msg; try { msg = JSON.parse(line); } catch (_) { continue; }
    const c = cases.find((x) => x.id === msg.id);
    if (!c) continue;
    const text = (msg.result && msg.result.content && msg.result.content[0].text) || '';
    const ok = text.includes(c.expect);
    console.log((ok ? 'PASS' : 'FAIL') + '  id=' + c.id + '  ' + c.expect + '  => ' + text.slice(0, 120));
    if (ok) pass++;
  }
  console.log(pass + '/' + cases.length + ' passed');
  process.exit(pass === cases.length ? 0 : 1);
}, 2000);
