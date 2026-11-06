'use strict';
const {test}=require('node:test');const assert=require('node:assert/strict');
const {quotaSnapshot,recoverySnapshot,failureCode}=require('../src/quota-worker.cjs');
const response={rateLimits:{primary:{windowDurationMins:300,usedPercent:20,resetsAt:3000},secondary:{windowDurationMins:10080,usedPercent:30,resetsAt:9000}}};
test('短暂失败保留真实采集时间并标记延迟；150秒后不展示旧额度',()=>{
  const good=quotaSnapshot(response,1000000);
  const stale=recoverySnapshot(null,good,true,'REQUEST_TIMEOUT',1100000);
  assert.equal(stale.fiveHour,80);assert.equal(stale.weekly,70);assert.equal(stale.status,'STALE');assert.equal(stale.updatedAt,1000000);assert.equal(stale.lastAttemptAt,1100000);
  const expired=recoverySnapshot(null,good,true,'QUERY_FAILED',1150001);assert.equal(expired.fiveHour,null);assert.equal(expired.status,'UNAVAILABLE');
});
test('越过额度重置边界后不沿用上一周期数字；另一窗口可继续显示',()=>{
  const good=quotaSnapshot(response,2950000);const stale=recoverySnapshot(null,good,true,'QUERY_FAILED',3010000);
  assert.equal(stale.fiveHour,null);assert.equal(stale.weekly,70);
});
test('停止客户端或认证失败时清除旧数据；恢复成功后显示新值',()=>{
  const good=quotaSnapshot(response,1000000);
  assert.equal(recoverySnapshot(null,good,false,null,1010000).status,'WAITING_FOR_CODEX');
  assert.equal(recoverySnapshot(null,good,true,'AUTH_REQUIRED',1010000).fiveHour,null);
  assert.equal(recoverySnapshot(response,good,true,null,1010000).status,'OK');
  assert.equal(failureCode(new Error('account/rateLimits/read timed out')),'REQUEST_TIMEOUT');assert.equal(failureCode(new Error('401 unauthorized')),'AUTH_REQUIRED');
});
test('真实后台进程断线后五秒自动重连，保留延迟标记并记录脱敏原因',async()=>{
  const fs=require('node:fs'),path=require('node:path'),os=require('node:os');const {spawn}=require('node:child_process');const {setTimeout:delay}=require('node:timers/promises');
  const root=fs.mkdtempSync(path.join(__dirname,'recovery-test-'));
  for(const file of ['quota-worker.cjs','usage-stats.cjs'])fs.copyFileSync(path.join(__dirname,'../src',file),path.join(root,file));
  fs.writeFileSync(path.join(root,'agent.cjs'),`let count=0;module.exports={resolveCodexBin:()=>'',AppServerClient:class extends require('events'){constructor(options){super();if(options.requestTimeoutMs!==20000)throw Error('timeout');}async start(){const first=++count===1;this.emit('rate-limits',{rateLimits:{primary:{windowDurationMins:300,usedPercent:first?20:30}}});if(first)setTimeout(()=>this.emit('server-exit',new Error('account/rateLimits/read timed out')),100);}stop(){}async refresh(){}}};`);
  const child=spawn(process.execPath,[path.join(root,'quota-worker.cjs')],{windowsHide:true,stdio:['pipe','ignore','ignore'],env:{...process.env,CODEX_HOME:path.join(root,'empty')}});
  async function until(predicate){for(let i=0;i<120;i++){try{const value=JSON.parse(fs.readFileSync(path.join(root,'quota-state.json')));if(predicate(value))return value;}catch{}await delay(100);}throw Error('recovery timeout');}
  try{
    await until(v=>v.status==='WAITING_FOR_CODEX');child.stdin.write('client-running\tC:\\Example\\ChatGPT.exe\n');
    const good=await until(v=>v.status==='OK');const stale=await until(v=>v.status==='STALE');assert.equal(stale.fiveHour,80);assert.equal(stale.updatedAt,good.updatedAt);assert.equal(stale.lastErrorCode,'REQUEST_TIMEOUT');
    const recovered=await until(v=>v.status==='OK'&&v.fiveHour===70);assert.equal(recovered.lastErrorCode,'REQUEST_TIMEOUT');assert.ok(recovered.updatedAt-good.updatedAt<10000);
    child.stdin.write('stop\n');await new Promise(resolve=>child.once('exit',resolve));
  }finally{if(child.exitCode===null)child.kill();}
});
