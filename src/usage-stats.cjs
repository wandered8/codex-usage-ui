'use strict';
const fs=require('node:fs');
const path=require('node:path');
const readline=require('node:readline');
const crypto=require('node:crypto');

// 只提取累计 Token 和模型；不保存会话正文、指令、账号或原始日志行。
async function parseUsageFile(filePath, cutoff) {
  const rows=new Map();let previous=null;let model='未知模型';let identity;
  const number=(usage,key)=>Number.isSafeInteger(usage?.[key])&&usage[key]>=0?usage[key]:0;
  for await(const line of readline.createInterface({input:fs.createReadStream(filePath),crlfDelay:Infinity})) {
    if(!/"type"\s*:\s*"(?:session_meta|turn_context|token_count)"/.test(line))continue;
    let event;try{event=JSON.parse(line);}catch{continue;}
    if(event.type==='session_meta'){identity=event.payload?.id;continue;}
    if(event.type==='turn_context'){model=typeof event.payload?.model==='string'?event.payload.model:'未知模型';continue;}
    if(event.type!=='event_msg'||event.payload?.type!=='token_count')continue;
    const usage=event.payload.info?.total_token_usage;
    if(!usage||!Number.isSafeInteger(usage.total_tokens))continue;
    const current={input:number(usage,'input_tokens'),output:number(usage,'output_tokens'),cached:number(usage,'cached_input_tokens'),total:number(usage,'total_tokens')};
    const reset=previous&&(current.total<previous.total||current.input<previous.input||current.output<previous.output);
    const delta={};for(const key of Object.keys(current))delta[key]=Math.max(0,current[key]-(reset?0:previous?.[key]??0));
    previous=current;
    const date=new Date(event.timestamp);
    if(!Number.isFinite(date.getTime())||date.getTime()<cutoff||!delta.total)continue;
    delta.cached=Math.min(delta.cached,delta.input);
    const day=[date.getFullYear(),String(date.getMonth()+1).padStart(2,'0'),String(date.getDate()).padStart(2,'0')].join('-');
    const key=day+'\0'+model;
    const row=rows.get(key)??{day,model,input:0,output:0,cached:0,total:0,records:0};
    for(const field of ['input','output','cached','total'])row[field]+=delta[field];row.records++;
    rows.set(key,row);
  }
  return {identity:typeof identity==='string'?crypto.createHash('sha256').update(identity).digest('hex'):null,rows:[...rows.values()]};
}

// 修改过的文件重新流式解析，未变文件复用结果；去重活动与归档中的同一会话。
async function scanUsage({roots,cachePath,now=Date.now()}) {
  const cutoff=new Date(now);cutoff.setHours(0,0,0,0);cutoff.setDate(cutoff.getDate()-29);
  const files=[];let unreadable=0;
  async function walk(directory){let entries;try{entries=await fs.promises.readdir(directory,{withFileTypes:true});}catch(error){if(error.code!=='ENOENT')unreadable++;return;}
    for(const entry of entries){const file=path.join(directory,entry.name);if(entry.isDirectory())await walk(file);else if(entry.isFile()&&entry.name.endsWith('.jsonl')){try{const stat=await fs.promises.stat(file);if(stat.mtimeMs>=cutoff.getTime())files.push({file,size:stat.size,mtime:stat.mtimeMs});}catch{unreadable++;}}}}
  for(const root of roots)await walk(root);
  let cache={};try{const loaded=JSON.parse(await fs.promises.readFile(cachePath,'utf8'));if(loaded.version===1)cache=loaded.files??{};}catch{}
  const next={};const sessions=new Map();
  for(const item of files){const key=crypto.createHash('sha256').update(item.file).digest('hex');let result=cache[key];
    if(!result||result.size!==item.size||result.mtime!==item.mtime){try{result={...await parseUsageFile(item.file,cutoff.getTime()),size:item.size,mtime:item.mtime};}catch{unreadable++;continue;}}
    next[key]=result;const id=result.identity??key;const existing=sessions.get(id);
    if(!existing||result.size>existing.size||(result.size===existing.size&&result.mtime>existing.mtime))sessions.set(id,result);
  }
  const grouped=new Map();const firstDay=[cutoff.getFullYear(),String(cutoff.getMonth()+1).padStart(2,'0'),String(cutoff.getDate()).padStart(2,'0')].join('-');
  for(const session of sessions.values())for(const row of session.rows){if(row.day<firstDay)continue;const key=row.day+'\0'+row.model;const target=grouped.get(key)??{day:row.day,model:row.model,input:0,output:0,cached:0,total:0,records:0};for(const field of ['input','output','cached','total','records'])target[field]+=row[field];grouped.set(key,target);}
  const write=async(file,value)=>{await fs.promises.writeFile(file+'.tmp',JSON.stringify(value));await fs.promises.rename(file+'.tmp',file);};
  await write(cachePath,{version:1,files:next});
  return {updatedAt:now,scope:'local-30-days',unreadableFiles:unreadable,sessionCount:sessions.size,rows:[...grouped.values()].sort((a,b)=>a.day.localeCompare(b.day)||a.model.localeCompare(b.model))};
}
module.exports={parseUsageFile,scanUsage};
