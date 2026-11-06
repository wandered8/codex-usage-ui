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

// 最多保留150秒的最近成功读数；重置时间已过的窗口不继续沿用旧百分比。
function recoverySnapshot(response,lastGood,running,errorCode,now=Date.now()) {
  const current=quotaSnapshot(response,now);
  let value=current;
  if(!running)value.status='WAITING_FOR_CODEX';
  else if(current.status!=='OK'&&errorCode!=='AUTH_REQUIRED'&&lastGood&&now-lastGood.updatedAt<=150000) {
    value={...lastGood,status:'STALE'};
    if(value.fiveResetAt!=null&&value.fiveResetAt*1000<=now)value.fiveHour=null;
    if(value.weekResetAt!=null&&value.weekResetAt*1000<=now)value.weekly=null;
    if(value.fiveHour==null&&value.weekly==null)value.status='UNAVAILABLE';
  }
  return {...value,lastSuccessAt:current.status==='OK'?now:lastGood?.updatedAt??null,lastAttemptAt:now,errorCode:running&&current.status!=='OK'?(errorCode||'EMPTY_RESPONSE'):null};
}

// 仅保留固定错误类别，不落服务端报文、账户或凭据。
function failureCode(error) {
  if(/unauthorized|authentication|not authenticated|sign in|log in|401|403/i.test(error?.message||''))return 'AUTH_REQUIRED';
  if(/timed out|timeout/i.test(error?.message||''))return 'REQUEST_TIMEOUT';
  if(error?.code==='ENOENT')return 'CLIENT_NOT_FOUND';
  return 'QUERY_FAILED';
}

// 管理窗口拥有本进程，通过标准输入发送 stop；退出时仅关闭自己的额度组件。
async function main() {
  let client;
  let appPath;
  let running=false;
  let busy = false;
  let stopped = false;
  let scanning = false;
  let lastGood=null;
  let lastErrorCode=null,lastErrorAt=null;
  let refreshTimer;
  const scheduleRefresh=delay=>{clearTimeout(refreshTimer);if(running&&!stopped)refreshTimer=setTimeout(refresh,delay);};
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
  const publish = (response,errorCode) => {
    const temporaryPath = snapshotPath + '.tmp';
    if(!running||errorCode==='AUTH_REQUIRED')lastGood=null;
    const value=recoverySnapshot(response,lastGood,running,errorCode);
    if(value.status==='OK')lastGood=quotaSnapshot(response,value.updatedAt);
    if(value.errorCode){lastErrorCode=value.errorCode;lastErrorAt=value.lastAttemptAt;}
    value.lastErrorCode=lastErrorCode;value.lastErrorAt=lastErrorAt;
    fs.writeFileSync(temporaryPath, JSON.stringify(value));
    fs.renameSync(temporaryPath, snapshotPath);
    return value.status==='OK';
  };
  const refresh = async () => {
    if(busy || stopped || !running) return;
    busy = true;
    let succeeded=false;
    try {
      if(!client) {
        const current = new AppServerClient({command:resolveCodexBin(undefined,appPath),requestTimeoutMs:20000});
        client=current;
        current.on('rate-limits',value=>{if(!stopped && client===current){succeeded=publish(value);scheduleRefresh(succeeded?60000:5000);}});
        current.on('server-exit',error=>{if(client===current){client=null;publish(null,failureCode(error));scheduleRefresh(5000);}});
        await current.start();
      } else succeeded=publish(await client.refresh());
    } catch(error) {
      succeeded=false;const old=client;client=null;old?.stop();publish(null,failureCode(error));
    } finally {busy=false;scheduleRefresh(succeeded?60000:5000);}
  };
  const statisticsTimer=setInterval(updateStatistics,60000);
  updateStatistics();
  const stop=async()=>{if(stopped)return;stopped=true;clearTimeout(refreshTimer);clearInterval(statisticsTimer);client?.stop();process.exit(0);};
  require('node:readline').createInterface({input:process.stdin}).on('line',line=>{if(line==='stop')stop();else if(line==='scan'){updateStatistics();if(running)refresh();}else if(line.startsWith('client-running\t')){appPath=line.slice('client-running\t'.length);running=true;refresh();}else if(line==='client-stopped'){running=false;clearTimeout(refreshTimer);const old=client;client=null;old?.stop();publish(null);}}).on('close',stop);
  process.on('SIGTERM',stop);process.on('SIGINT',stop);
  publish(null);
  await refresh();
}
module.exports={quotaSnapshot,recoverySnapshot,failureCode};
if(require.main===module)main().catch(()=>{process.exitCode=1;});
