'use strict';
const {test}=require('node:test');const assert=require('node:assert/strict');
const {quotaSnapshot}=require('../src/quota-worker.cjs');
test('5小时与每周额度，取剩余额度并保留重置时间',()=>{
  const value=quotaSnapshot({rateLimits:{primary:{windowDurationMins:10080,usedPercent:7,resetsAt:200},secondary:{windowDurationMins:300,usedPercent:43,resetsAt:100}}},123);
  assert.deepEqual(value,{fiveHour:57,weekly:93,fiveResetAt:100,weekResetAt:200,resetCardCount:null,resetCardExpiresAt:null,resetCardExpirations:[],updatedAt:123,status:'OK'});
});
test('可用重置卡显示数量和最早到期时间，只统计Codex可用卡',()=>{
  const value=quotaSnapshot({rateLimitResetCredits:{availableCount:2,credits:[{status:'available',resetType:'codexRateLimits',expiresAt:300},{status:'used',resetType:'codexRateLimits',expiresAt:100},{status:'available',resetType:'codexRateLimits',expiresAt:200},{status:'available',resetType:'other',expiresAt:50}]}});
  assert.equal(value.resetCardCount,2);assert.equal(value.resetCardExpiresAt,200);assert.deepEqual(value.resetCardExpirations,[200,300]);
});
test('无重置卡和接口缺失保持区分，不伪造到期时间',()=>{
  assert.equal(quotaSnapshot({rateLimitResetCredits:{availableCount:0,credits:[]}}).resetCardCount,0);
  assert.equal(quotaSnapshot({rateLimitResetCredits:{availableCount:0,credits:[]}}).resetCardExpiresAt,null);
  assert.equal(quotaSnapshot(null).resetCardCount,null);
});
test('数据不可用时保留未知，不显示为0%',()=>{assert.equal(quotaSnapshot(null).fiveHour,null);assert.equal(quotaSnapshot(null).status,'UNAVAILABLE');});
test('额度耗尽的0%和未知不同',()=>{assert.equal(quotaSnapshot({rateLimits:{primary:{windowDurationMins:300,usedPercent:100}}}).fiveHour,0);});
test('指定Codex额度优先，其他模型桶不能混入',()=>{
  assert.equal(quotaSnapshot({rateLimits:{limitId:'spark',primary:{windowDurationMins:300,usedPercent:10}}}).fiveHour,null);
  assert.equal(quotaSnapshot({rateLimits:{primary:{windowDurationMins:300,usedPercent:10}},rateLimitsByLimitId:{codex:null}}).fiveHour,null);
});
