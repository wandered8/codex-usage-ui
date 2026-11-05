'use strict';
const {test}=require('node:test');const assert=require('node:assert/strict');const fs=require('node:fs');const path=require('node:path');const {spawn}=require('node:child_process');const {setTimeout:delay}=require('node:timers/promises');
test('后台先于客户端启动不运行额度组件，收到启动信号再采集，停止后回到等待',async()=>{
  const root=fs.mkdtempSync(path.resolve('tests/wait-test-'));
  fs.copyFileSync('src/quota-worker.cjs',path.join(root,'quota-worker.cjs'));
  fs.copyFileSync('src/usage-stats.cjs',path.join(root,'usage-stats.cjs'));
  fs.writeFileSync(path.join(root,'agent.cjs'),`const fs=require('fs'),path=require('path');module.exports={resolveCodexBin:(explicit,appPath)=>{fs.writeFileSync(path.join(__dirname,'client-path'),appPath);return ''},AppServerClient:class extends require('events'){async start(){fs.writeFileSync(path.join(__dirname,'started'),'1');this.emit('rate-limits',{rateLimits:{primary:{windowDurationMins:300,usedPercent:40}}});}stop(){fs.writeFileSync(path.join(__dirname,'stopped'),'1');}async refresh(){}}};`);
  fs.writeFileSync(path.join(root,'sidebar-connection.cjs'),`module.exports={SidebarConnection:class{async update(){}async tick(){return 'WAITING_FOR_CODEX'}async stop(){}}};`);
  const child=spawn(process.execPath,[path.join(root,'quota-worker.cjs')],{stdio:['pipe','ignore','ignore'],env:{...process.env,CODEX_HOME:path.join(root,'home')},windowsHide:true});
  async function until(condition){for(let i=0;i<100;i++){if(condition())return;await delay(30);}throw Error('Worker timeout');}
  try {
    await until(()=>fs.existsSync(path.join(root,'quota-state.json')));
    assert.equal(JSON.parse(fs.readFileSync(path.join(root,'quota-state.json'))).status,'WAITING_FOR_CODEX');assert.equal(fs.existsSync(path.join(root,'started')),false);
    child.stdin.write('client-running\tC:\\Example Codex\\app\\ChatGPT.exe\n');await until(()=>fs.existsSync(path.join(root,'started')));
    assert.equal(fs.readFileSync(path.join(root,'client-path'),'utf8'),'C:\\Example Codex\\app\\ChatGPT.exe');
    child.stdin.write('client-stopped\n');await until(()=>fs.existsSync(path.join(root,'stopped')));
    await until(()=>JSON.parse(fs.readFileSync(path.join(root,'quota-state.json'))).status==='WAITING_FOR_CODEX');
    child.stdin.write('stop\n');await new Promise(resolve=>child.once('exit',resolve));
  } finally {if(child.exitCode===null)child.kill();}
});
