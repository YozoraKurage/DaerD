#!/usr/bin/env node
// NUnit3 の結果 XML を、端末に出す最小限のサマリへ落とす。
//
// Unity の batchmode ログは数万行あり、そのまま流すと読む側の負担が大きい。
// ここでは「合計・失敗した項目・そのメッセージ」だけを出し、詳細はログ
// ファイルへのポインタに留める。

const fs = require('fs');

const path = process.argv[2];
const maxFailures = Number(process.argv[3] || 25);

if (!path || !fs.existsSync(path)) {
  console.error(`結果 XML が無い: ${path}`);
  process.exit(3);
}

const xml = fs.readFileSync(path, 'utf8');

const attrs = (chunk) => {
  const out = {};
  const re = /([\w-]+)="([^"]*)"/g;
  let m;
  while ((m = re.exec(chunk)) !== null) out[m[1]] = m[2];
  return out;
};

const unescape = (s) =>
  s
    .replace(/<!\[CDATA\[/g, '')
    .replace(/\]\]>/g, '')
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&quot;/g, '"')
    .replace(/&apos;/g, "'")
    .replace(/&amp;/g, '&');

const runHeader = xml.match(/<test-run\b[^>]*>/);
const run = runHeader ? attrs(runHeader[0]) : {};

const total = Number(run.total || 0);
const passed = Number(run.passed || 0);
const failed = Number(run.failed || 0);
const skipped = Number(run.skipped || 0);
const inconclusive = Number(run.inconclusive || 0);
const duration = Number(run.duration || 0);

// 1 件目の子要素までを 1 チャンクとして扱う。失敗の <message>/<stack-trace> は
// その test-case の直後に来るので、これで拾える。
const chunks = xml.split('<test-case ').slice(1);
const failures = [];

for (const chunk of chunks) {
  const head = chunk.slice(0, chunk.indexOf('>') + 1);
  const a = attrs(head);
  if (a.result !== 'Failed') continue;

  const body = chunk.slice(0, chunk.indexOf('</test-case>') + 1);
  const msg = body.match(/<message>([\s\S]*?)<\/message>/);
  const stack = body.match(/<stack-trace>([\s\S]*?)<\/stack-trace>/);

  failures.push({
    name: a.fullname || a.name || '(unnamed)',
    message: msg ? unescape(msg[1]).trim() : '',
    stack: stack ? unescape(stack[1]).trim() : '',
  });
}

const parts = [`${total} 件`, `成功 ${passed}`, `失敗 ${failed}`];
if (skipped) parts.push(`スキップ ${skipped}`);
if (inconclusive) parts.push(`不確定 ${inconclusive}`);
console.log(`EditMode テスト: ${parts.join(' / ')}  (${duration.toFixed(1)}s)`);

for (const f of failures.slice(0, maxFailures)) {
  console.log('');
  console.log(`FAILED  ${f.name}`);
  for (const line of f.message.split('\n').slice(0, 12)) {
    console.log(`    ${line}`);
  }
  // スタックはテストコードに触れている最初の行だけあれば足りる。
  const frame = f.stack.split('\n').find((l) => l.includes('Tests')) || f.stack.split('\n')[0];
  if (frame) console.log(`    ${frame.trim()}`);
}

if (failures.length > maxFailures) {
  console.log('');
  console.log(`… 他 ${failures.length - maxFailures} 件の失敗（全件は結果 XML を参照）`);
}

process.exit(failed > 0 ? 1 : 0);
