// pdms_iso_generate 工具离线自测（不需要 PDMS 在线）
'use strict';
const path = 'D:/pdmsAgent/bridge/pdms-bridge-mcp/server.js';
// 提取函数做单测：直接 require 会启动 stdio 循环，故用文本截取 pmlIsoGenerate/parseIsoResult
const fs = require('fs');
const src = fs.readFileSync(path, 'utf8');
function pick(name) {
  const i = src.indexOf('function ' + name);
  if (i < 0) throw new Error(name + ' not found');
  let depth = 0, started = false, j = i;
  for (; j < src.length; j++) {
    if (src[j] === '{') { depth++; started = true; }
    else if (src[j] === '}') { depth--; if (started && depth === 0) break; }
  }
  return src.slice(i, j + 1);
}
const pmlPath = new Function('return (' + pick('pmlPath') + ')')();
const RESULT_FILE = 'D:/pdmsAgent/bridge/result.txt';
const pmlResultOpen = new Function('pmlPath', 'RESULT_FILE', 'return (' + pick('pmlResultOpen') + ')')(pmlPath, RESULT_FILE);
const pmlIsoGenerate = new Function('pmlResultOpen', 'return (' + pick('pmlIsoGenerate') + ')')(pmlResultOpen);
const parseIsoResult = new Function('return (' + pick('parseIsoResult') + ')')();

const code = pmlIsoGenerate({ pipes: ['/2001', '2003'] });
console.log('=== generated PML (first 40 lines) ===');
console.log(code.split('\n').slice(0, 40).join('\n'));
console.log('=== lines total: ' + code.split('\n').length);

const sample = [
  '===ISO-START pipes_2 opt_%SAMISO%/std/previewisostd.txt===',
  'PIPE1 /2001 STATUS=OK PLOTS=1 FILE=/%PDMSUSER%\\previewIsoStd015',
  'PIPE2 /2003 STATUS=CE_FAIL',
  '===ISO-END==='
].join('\n');
console.log('=== parse test ===');
console.log(JSON.stringify(parseIsoResult(sample), null, 2));
