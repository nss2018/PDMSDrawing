#!/usr/bin/env node
/* 测试桩：起一个 mock PDMS TCP 监听器 + 驱动 server.js 跑通 MCP 链路 */
'use strict';
const net = require('net');
const { spawn } = require('child_process');

// 1) mock PDMS 监听器：收到 JSON 请求，回 {ok, output}
const mock = net.createServer((sock) => {
  let buf = '';
  sock.on('data', (d) => {
    buf += d.toString();
    let idx;
    while ((idx = buf.indexOf('\n')) >= 0) {
      const line = buf.slice(0, idx); buf = buf.slice(idx + 1);
      if (!line.trim()) continue;
      let req; try { req = JSON.parse(line); } catch (_) { continue; }
      const resp = JSON.stringify({ id: req.id, ok: true, output: 'MOCK PDMS executed: ' + req.code.slice(0, 40), error: null }) + '\n';
      sock.write(resp);
      sock.end();
    }
  });
});
mock.listen(18753, '127.0.0.1', () => runServer());

function runServer() {
  const srv = spawn('node', ['server.js'], { cwd: __dirname, stdio: ['pipe', 'pipe', 'pipe'] });
  let out = '';
  srv.stdout.on('data', (d) => { out += d.toString(); drain(); });
  srv.stderr.on('data', (d) => process.stderr.write('[server.err] ' + d));
  const send = (m) => srv.stdin.write(JSON.stringify(m) + '\n');
  const steps = [
    { m: { jsonrpc: '2.0', id: 1, method: 'initialize', params: {} }, label: 'initialize' },
    { m: { jsonrpc: '2.0', id: 2, method: 'tools/list', params: {} }, label: 'tools/list' },
    { m: { jsonrpc: '2.0', id: 3, method: 'tools/call', params: { name: 'pdms_status', arguments: {} } }, label: 'pdms_status' },
    { m: { jsonrpc: '2.0', id: 4, method: 'tools/call', params: { name: 'pdms_exec', arguments: { code: '!v = 1 + 1' } } }, label: 'pdms_exec' },
    { m: { jsonrpc: '2.0', id: 5, method: 'tools/call', params: { name: 'pdms_create_drawing', arguments: { range: { xmin: 0, xmax: 1000, ymin: 0, ymax: 2000, zmin: 0, zmax: 5000 }, scale: 100 } } }, label: 'pdms_create_drawing' }
  ];
  let i = 0; const responses = [];
  function drain() {
    let nl;
    while ((nl = out.indexOf('\n')) >= 0) {
      const line = out.slice(0, nl); out = out.slice(nl + 1);
      if (!line.trim()) continue;
      let r; try { r = JSON.parse(line); } catch (_) { continue; }
      responses.push(r);
      if (responses.length === steps.length) finish();
    }
  }
  function tick() {
    if (i < steps.length) { send(steps[i].m); i++; setTimeout(tick, 150); }
  }
  function finish() {
    console.log('=== MCP 链路测试结果 ===');
    responses.forEach((r, k) => {
      console.log(`#${k + 1} ${steps[k].label}:`, JSON.stringify(r).slice(0, 200));
    });
    srv.kill(); mock.close(); process.exit(0);
  }
  tick();
  setTimeout(() => { console.error('TEST TIMEOUT'); srv.kill(); mock.close(); process.exit(1); }, 8000);
}
