'use strict';
const fs = require('node:fs');
const path = require('node:path');
const {AppServerClient, resolveCodexBin} = require('./agent.cjs');
const snapshotPath = path.join(__dirname, 'quota-state.json');

// 仅输出额度与重置时间，不保存账号、聊天或凭据。
function quotaSnapshot(response, updatedAt = Date.now()) {
  const selected = Object.hasOwn(response?.rateLimitsByLimitId ?? {}, 'codex')
    ? response.rateLimitsByLimitId.codex : response?.rateLimits;
  const quota = selected?.limitId == null || selected.limitId === 'codex' ? selected : null;
  const windows = [quota?.primary, quota?.secondary].filter(Boolean);
  const five = windows.find(window => window.windowDurationMins === 300);
  const week = windows.find(window => Number.isFinite(window.windowDurationMins) && window.windowDurationMins >= 10080 && window.windowDurationMins % 10080 === 0);
  const remaining = window => Number.isFinite(window?.usedPercent) ? Math.round(Math.max(0,Math.min(100,100-window.usedPercent))) : null;
  const credits=response?.rateLimitResetCredits;
  const available=Array.isArray(credits?.credits)?credits.credits.filter(card=>card.status==='available'&&card.resetType==='codexRateLimits'):null;
  const resetCardCount=Number.isSafeInteger(credits?.availableCount)&&credits.availableCount>=0?credits.availableCount:available?.length??null;
  const expirations=(available??[]).map(card=>card.expiresAt).filter(value=>Number.isFinite(value)&&value>0);
  const resetCardExpirations=resetCardCount===0?[]:(available??[]).map(card=>Number.isFinite(card.expiresAt)&&card.expiresAt>0?card.expiresAt:null).sort((a,b)=>(a??Infinity)-(b??Infinity));
  return {fiveHour:remaining(five),weekly:remaining(week),fiveResetAt:Number.isFinite(five?.resetsAt)?five.resetsAt:null,
    weekResetAt:Number.isFinite(week?.resetsAt)?week.resetsAt:null,resetCardCount,resetCardExpiresAt:resetCardCount>0&&expirations.length?Math.min(...expirations):null,resetCardExpirations,updatedAt,
    status:Number.isFinite(five?.usedPercent)||Number.isFinite(week?.usedPercent)?'OK':'UNAVAILABLE'};
}

// 管理窗口拥有本进程，通过标准输入发送 stop；退出时仅关闭自己的额度组件。
async function main() {
  let client;
  let appPath;
  let running=false;
  let busy = false;
  let stopped = false;
  let scanning = false;
  const updateStatistics = async () => {
    if(scanning||stopped)return;scanning=true;
    try {
      const home=process.env.CODEX_HOME||path.join(require('node:os').homedir(),'.codex');
      const value=await require('./usage-stats.cjs').scanUsage({roots:[path.join(home,'sessions'),path.join(home,'archived_sessions')],cachePath:path.join(__dirname,'usage-cache.json')});
      fs.writeFileSync(path.join(__dirname,'token-stats.json.tmp'),JSON.stringify(value));
      fs.renameSync(path.join(__dirname,'token-stats.json.tmp'),path.join(__dirname,'token-stats.json'));
    } catch { /* 保留上次快照，界面会显示更新时间。 */ }
    finally {scanning=false;}
  };
  const publish = response => {
    const temporaryPath = snapshotPath + '.tmp';
    const value=quotaSnapshot(response);if(!running)value.status="WAITING_FOR_CODEX";
    fs.writeFileSync(temporaryPath, JSON.stringify(value));
    fs.renameSync(temporaryPath, snapshotPath);
  };
  const refresh = async () => {
    if(busy || stopped || !running) return;
    busy = true;
    try {
      if(!client) {
        const current = new AppServerClient({command:resolveCodexBin(undefined,appPath)});
        client=current;
        current.on('rate-limits',value=>{if(!stopped && client===current)publish(value);});
        current.on('server-exit',()=>{if(client===current){client=null;publish(null);}});
        await current.start();
      } else await client.refresh();
    } catch {
      const old=client;client=null;old?.stop();publish(null);
    } finally {busy=false;}
  };
  const timer=setInterval(refresh,60000);
  const statisticsTimer=setInterval(updateStatistics,60000);
  updateStatistics();
  const stop=async()=>{if(stopped)return;stopped=true;clearInterval(timer);clearInterval(statisticsTimer);client?.stop();process.exit(0);};
  require('node:readline').createInterface({input:process.stdin}).on('line',line=>{if(line==='stop')stop();else if(line==='scan')updateStatistics();else if(line.startsWith('client-running\t')){appPath=line.slice('client-running\t'.length);running=true;refresh();}else if(line==='client-stopped'){running=false;const old=client;client=null;old?.stop();publish(null);}}).on('close',stop);
  process.on('SIGTERM',stop);process.on('SIGINT',stop);
  publish(null);
  await refresh();
}
module.exports={quotaSnapshot};
if(require.main===module)main().catch(()=>{process.exitCode=1;});
