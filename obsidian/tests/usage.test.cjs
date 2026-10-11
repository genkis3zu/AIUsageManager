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
