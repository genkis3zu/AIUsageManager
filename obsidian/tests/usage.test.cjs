const { test } = require('node:test');
const assert = require('node:assert/strict');
const { parseSnapshot, presentService, percent, normalizeSettings } = require('../src/usage');
const now = Date.parse('2026-10-11T09:00:00Z');
function payload() { return {schemaVersion:1, connected:true, services:['Claude','Codex','Grok'].map(name=>({name,plan:'Test',source:'オンライン',status:'',recordedAt:new Date(now).toISOString(),windows:(name==='Claude'?['5時間','週']:['週']).map(label=>({label,percent:0,resetsAt:new Date(now+3600000).toISOString(),alert:null}))}))}; }
test('missing is distinct from acquired zero and percentages are bounded',()=> {
  const p=payload();p.services[1].windows[0].percent=null;p.services[2].windows[0].percent=-1;
  const s=parseSnapshot(JSON.stringify(p));
  assert.equal(percent(s.services[0].windows[0].percent),'0%');
  assert.equal(percent(s.services[1].windows[0].percent),'未取得');
  assert.equal(percent(s.services[2].windows[0].percent),'未取得');
});
test('unsupported schemas, duplicate services, oversized and malformed responses reject',()=> {
  assert.throws(()=>parseSnapshot('{'));assert.throws(()=>parseSnapshot('x'.repeat(65537)));
  const p=payload();p.schemaVersion=2;assert.throws(()=>parseSnapshot(JSON.stringify(p)));
  p.schemaVersion=1;p.services[1].name='Claude';assert.throws(()=>parseSnapshot(JSON.stringify(p)));
});
test('current high usage carries readable warnings and original observation time',()=> {
  const p=payload();p.services[1].windows[0].percent=96;
  const v=presentService(parseSnapshot(JSON.stringify(p)).services[1],true,now);
  assert.equal(v.severity,'critical');assert.match(v.state,/残り4%/);assert.match(v.tooltip,/96%/);assert.ok(v.stamp);
});
test('stale, failed, offline, future and expired data cannot retain active warnings',()=> {
  for(const mode of ['stale','failed','offline','future','expired']) {
    const p=payload(), s=p.services[1];s.windows[0].percent=100;s.windows[0].alert={title:'週：上限',detail:'推定',threshold:100};
    if(mode==='stale')s.recordedAt=new Date(now-16*60000).toISOString();
    if(mode==='failed')s.status='認証が必要です';
    if(mode==='future')s.recordedAt=new Date(now+2*60000).toISOString();
    if(mode==='expired')s.windows[0].resetsAt=new Date(now-1).toISOString();
    const v=presentService(parseSnapshot(JSON.stringify(p)).services[1],mode!=='offline',now);
    assert.equal(v.severity,'muted',mode);assert.doesNotMatch(v.state,/上限到達/,mode);
  }
});
test('fresh app forecast is displayed without recomputing usage history',()=> {
  const p=payload();p.services[2].windows[0].percent=40;p.services[2].windows[0].alert={title:'週：リセット前に上限の見込み',detail:'このペースでは上限（推定）',threshold:0,rapidIncrease:true};
  const v=presentService(parseSnapshot(JSON.stringify(p)).services[2],true,now);
  assert.equal(v.severity,'warning');assert.match(v.state,/上限の見込み/);assert.match(v.tooltip,/推定/);
});
test('settings preserve only supported mode and an absolute executable path',()=> {
  assert.deepEqual(normalizeSettings({mode:'invalid',appPath:'cmd /c bad',extra:'discard'}),{mode:'standard',appPath:''});
  assert.deepEqual(normalizeSettings({mode:'compact',appPath:'C:\\Apps\\AIUsageManager.exe'}),{mode:'compact',appPath:'C:\\Apps\\AIUsageManager.exe'});
});
module.exports={payload,now};

test('Grok login requirement hides legacy usage and reset information',()=> {
  const p=payload();const s=p.services[2];s.needsLogin=true;s.windows[0].percent=35;s.status='再ログインが必要です';
  const v=presentService(parseSnapshot(JSON.stringify(p)).services[2],true,now);
  assert.equal(v.windows[0].display,'未取得');assert.equal(v.windows[0].percent,null);assert.equal(v.stamp,'未取得');
  assert.doesNotMatch(v.tooltip,/35%/);assert.match(v.state,/ログイン/);
});

test('pace summary and observation basis remain visible without a warning',()=> {
  const p=payload();p.services[1].windows[0].percent=36;p.services[1].windows[0].pace={summary:'90%まで約4.5日 · +12pt/日（推定）',detail:'現在36%。直近30分で +0.25ポイント。リセットが先'};
  const v=presentService(parseSnapshot(JSON.stringify(p)).services[1],true,now);
  assert.equal(v.severity,'normal');assert.match(v.state,/90%まで約4.5日/);assert.match(v.tooltip,/直近30分/);
  const stale=presentService(parseSnapshot(JSON.stringify(p)).services[1],true,now+16*60000);
  assert.doesNotMatch(stale.tooltip,/4.5日|直近30分/);
});

test('fresh readings without enough history explain collection rather than inventing an ETA',()=> {
  const v=presentService(parseSnapshot(JSON.stringify(payload())).services[1],true,now);
  assert.match(v.state,/観測中/);assert.match(v.state,/10分/);assert.doesNotMatch(v.state,/90%まで約/);
});
