'use strict';
const path = require('node:path');
const names = ['Claude', 'Codex', 'Grok'];
const text = (v, max = 180) => typeof v === 'string' ? v.slice(0, max) : '';
const date = v => typeof v === 'string' && Number.isFinite(Date.parse(v)) ? v : null;
const validPath = v => typeof v === 'string' && /^[a-z]:[\\/]/i.test(v) && path.win32.basename(v.replace(/\//g, '\\')).toLowerCase() === 'aiusagemanager.exe' && !/[\r\n\0"]/u.test(v);
function normalizeSettings(v = {}) { return { mode: v.mode === 'compact' ? 'compact' : 'standard', appPath: validPath(v.appPath) ? v.appPath : '' }; }
// Windows vary by plan (e.g. Claude Ultra has no 5-hour window but a weekly Fable one), so each service carries 0–4.
const WINDOW_LABEL = /^(5時間|週|週（.{1,24}）)$/u;
// The tray app's AIの状況 summary is optional: a missing or malformed one is null and the usage values still show.
// Only the two fixed status pages may pass, because a key press opens the page in the browser.
const STATUS_TONES = ['critical', 'warning', 'reset', 'normal', 'unknown'];
const STATUS_PAGES = ['https://status.claude.com/', 'https://status.openai.com/'];
// Each company's own state; entries that do not fit are dropped, and at most three are kept.
const PROVIDER_TONES = ['critical', 'warning', 'normal', 'unknown'];
function parseProviders(list) {
  if(!Array.isArray(list)) return [];
  return list.filter(p=>p && typeof p==='object' && typeof p.company==='string' && p.company.length>0 && PROVIDER_TONES.includes(p.tone)).slice(0,3)
    .map(p=>({company:text(p.company,20),tone:p.tone,detail:text(p.detail)}));
}
function parseStatus(v) {
  if(!v || typeof v!=='object' || Array.isArray(v) || !STATUS_TONES.includes(v.tone)) return null;
  return {tone:v.tone,title:text(v.title,60),detail:text(v.detail),page:STATUS_PAGES.includes(v.page)?v.page:null,line:text(v.line),providers:parseProviders(v.providers)};
}
// The tray app's 対応待ち list: sessions waiting for the user. Only known services, reasons and the four app links pass.
const WAIT_SERVICES = ['Claude', 'Codex', 'Grok Bot'], WAIT_REASONS = ['question', 'confirm', 'finished'], WAIT_APPS = ['claude', 'codex', 'vscode', 'grokbot'];
function parseWaiting(v) {
  if(!v || typeof v!=='object' || Array.isArray(v)) return {items:[],unreadable:[]};
  const items=(Array.isArray(v.items)?v.items:[]).filter(i=>i && typeof i==='object' && WAIT_SERVICES.includes(i.service) && WAIT_REASONS.includes(i.reason) && WAIT_APPS.includes(i.app) && date(i.since)).slice(0,8)
    .map(i=>({service:i.service,place:text(i.place,40),reason:i.reason,since:i.since,app:i.app}));
  const unreadable=(Array.isArray(v.unreadable)?v.unreadable:[]).filter(u=>u && typeof u==='object' && WAIT_SERVICES.includes(u.service)).slice(0,3).map(u=>({service:u.service,reason:text(u.reason,40)}));
  return {items,unreadable};
}
function emptyServices() { return names.map(name => ({name, plan:'', source:'', status:'', recordedAt:null, needsLogin:false, windows:[]})); }
function parseSnapshot(json) {
  if(typeof json !== 'string' || Buffer.byteLength(json, 'utf8') > 65536) throw new Error('Invalid snapshot');
  const value=JSON.parse(json);
  if(!value || value.schemaVersion!==2 || typeof value.connected!=='boolean' || !Array.isArray(value.services)) throw new Error('Invalid snapshot');
  if(!value.connected) return {connected:false, refreshing:false, status:null, waiting:{items:[],unreadable:[]}, services:emptyServices()};
  if(value.services.length!==3 || names.some(name=>value.services.filter(s=>s?.name===name).length!==1)) throw new Error('Invalid services');
  return {connected:true, refreshing:value.refreshing===true, status:parseStatus(value.status), waiting:parseWaiting(value.waiting), services:names.map(name=> {
    const s=value.services.find(s=>s.name===name);
    if(!Array.isArray(s.windows) || s.windows.length>4) throw new Error('Invalid windows');
    const seen=new Set();
    return {name,plan:text(s.plan),source:text(s.source),status:text(s.status),needsLogin:s.needsLogin===true,recordedAt:date(s.recordedAt), windows:s.windows.map(w=> {
      if(!w || typeof w.label!=='string' || !WINDOW_LABEL.test(w.label) || seen.has(w.label)) throw new Error('Invalid windows');
      if(typeof w.shortLabel!=='string' || w.shortLabel.length<1 || Array.from(w.shortLabel).length>24 || !['5h','week'].includes(w.span)) throw new Error('Invalid windows');
      seen.add(w.label);
      const a=w.alert, p=w.pace;
      return {label:w.label,shortLabel:w.shortLabel,span:w.span,percent:typeof w.percent==='number' && Number.isFinite(w.percent) && w.percent>=0 && w.percent<=100000 ? w.percent : null,resetsAt:date(w.resetsAt),pace:p && typeof p==='object'?{summary:text(p.summary),detail:text(p.detail,700)}:null,alert:a && typeof a==='object' ? {title:text(a.title),detail:text(a.detail,700),threshold:[90,95,100].includes(a.threshold)?a.threshold:0,rapidIncrease:a.rapidIncrease===true,forecastAt:date(a.forecastAt)} : null};
    })};
  })};
}
function percent(value) {return value===null?'未取得':`${Math.round(value*10)/10}%`;}
function stamp(at) { if(!at)return '時刻不明';const d=new Date(at);return `${String(d.getHours()).padStart(2,'0')}:${String(d.getMinutes()).padStart(2,'0')}`; }
function reset(at, now) {
  if(!at)return '時刻不明';let minutes=Math.ceil((Date.parse(at)-now)/60000);
  if(minutes<=0)return '期限経過';if(minutes>=1440)return `${Math.floor(minutes/1440)}日${Math.floor(minutes%1440/60)}h`;
  return minutes>=60?`${Math.floor(minutes/60)}h${minutes%60}m`:`${minutes}m`;
}
function presentService(s,connected,now=Date.now()) {
  if(s.name==='Grok' && s.needsLogin)s={...s,recordedAt:null,source:'',windows:s.windows.map(w=>({...w,percent:null,resetsAt:null,pace:null,alert:null}))};
  const age=s.recordedAt?now-Date.parse(s.recordedAt):Infinity;
  const fresh=connected && s.source==='オンライン' && !s.status && !s.needsLogin && age>=-60000 && age<=900000;
  let severity=fresh?'normal':'muted';const alerts=[];
  const windows=s.windows.map(w=> {
    const active=fresh && w.percent!==null && (!w.resetsAt || Date.parse(w.resetsAt)>now);
    let alert=active?w.alert:null;
    if(active && !alert && w.percent>=90)alert={title:w.percent>=100?`${w.label}：上限到達`:`${w.label}：残り${Math.round((100-w.percent)*10)/10}%`,detail:''};
    if(alert){alerts.push(alert.title);if(w.percent>=95)severity='critical';else if(severity!=='critical')severity='warning';}
    return {...w,active,alert,pace:active?w.pace:null,display:percent(w.percent),reset:reset(w.resetsAt,now)};
  });
  if(severity==='normal' && windows.every(w=>!w.active))severity='muted';
  const ranked=windows.filter(w=>w.alert||w.pace).sort((a,b)=>((b.alert?.threshold||0)-(a.alert?.threshold||0))||Number(!!b.alert)-Number(!!a.alert)||Number(b.label==='週')-Number(a.label==='週'));
  const focus=ranked[0];
  let state=focus?(focus.pace && !(focus.alert?.threshold>=100)?`${s.windows.length>1?focus.label+'：':''}${focus.pace.summary}${focus.alert?.rapidIncrease?' · 急増':''}`:focus.alert?.title||''):'';
  if(!state)state=!connected?'アプリ未接続':s.status || (age>900000?'保存された値':age< -60000?'取得時刻を確認':s.windows.every(w=>w.percent===null)?'未取得':windows.some(w=>w.reset==='期限経過')?'期限経過・再取得待ち':fresh&&windows.some(w=>w.active)?'予測の学習中':s.source || '未取得');
  const tooltip=[`${s.name} · ${s.plan}`, ...windows.map(w=>`${w.label} ${w.display} / リセット ${w.reset}${w.alert?'\n'+w.alert.title+'\n'+w.alert.detail:w.pace?'\n'+w.pace.detail:''}`),`取得 ${s.recordedAt?new Date(s.recordedAt).toLocaleString():'時刻不明'} · ${s.source}`,state].join('\n');
  return {name:s.name,windows,severity,state,tooltip,stamp:s.recordedAt?stamp(s.recordedAt):'未取得'};
}
module.exports={parseSnapshot,presentService,percent,normalizeSettings,validPath,emptyServices,STATUS_PAGES};
