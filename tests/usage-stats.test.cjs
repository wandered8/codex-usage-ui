'use strict';
const {test}=require('node:test');const assert=require('node:assert/strict');const fs=require('node:fs');const path=require('node:path');
const {parseUsageFile,scanUsage}=require('../src/usage-stats.cjs');
const root=fs.mkdtempSync(path.resolve('tests/stats-fixture-'));
const time='2026-11-04T03:00:00Z';
const token=(input,output,cached=0)=>({type:'event_msg',timestamp:time,payload:{type:'token_count',info:{total_token_usage:{input_tokens:input,output_tokens:output,cached_input_tokens:cached,total_tokens:input+output}}}});
function write(name,events){const file=path.join(root,name);fs.writeFileSync(file,events.map(value=>JSON.stringify(value)).join('\n'));return file;}
test('累计计数求增量，相同事件不能重复计费，缓存不额外加到总量',async()=>{
  const file=write('a.jsonl',[{type:'turn_context',payload:{model:'test-model'}},token(100,10,80),token(100,10,80),token(200,25,160)]);
  const value=await parseUsageFile(file,0);assert.deepEqual(value.rows[0],{day:'2026-11-04',model:'test-model',input:200,output:25,cached:160,total:225,records:2});
});
test('按当前模型归属增量，跨模型不重复累计旧计数',async()=>{
  const file=write('b.jsonl',[{type:'turn_context',payload:{model:'one'}},token(100,10),{type:'turn_context',payload:{model:'two'}},token(180,20)]);
  const value=await parseUsageFile(file,0);assert.equal(value.rows[1].total,90);
});
test('未写完的日志行会被忽略，下次可重新完整扫描',async()=>{
  const file=write('c.jsonl',[token(10,2)]);fs.appendFileSync(file,'\n{"type":"event_msg","payload":{"type":"token_count"');
  const value=await parseUsageFile(file,0);assert.equal(value.rows[0].total,12);
});
test('活动与归档同一会话去重，缓存刷新与追加记录正确',async()=>{
  const active=path.join(root,'active'),archive=path.join(root,'archive');fs.mkdirSync(active);fs.mkdirSync(archive);
  const events=[{type:'session_meta',payload:{id:'same-session'}},token(100,10)];
  fs.writeFileSync(path.join(active,'x.jsonl'),events.map(x=>JSON.stringify(x)).join('\n'));fs.writeFileSync(path.join(archive,'copy.jsonl'),events.map(x=>JSON.stringify(x)).join('\n'));
  const options={roots:[active,archive],cachePath:path.join(root,'cache.json'),now:new Date(time).getTime()};
  const first=await scanUsage(options);assert.equal(first.sessionCount,1);assert.equal(first.rows[0].total,110);
  fs.appendFileSync(path.join(active,'x.jsonl'),'\n'+JSON.stringify(token(200,30)));
  const next=await scanUsage(options);assert.equal(next.rows[0].total,230);assert.equal(next.sessionCount,1);
  assert.equal((await scanUsage(options)).rows[0].total,230);
});
