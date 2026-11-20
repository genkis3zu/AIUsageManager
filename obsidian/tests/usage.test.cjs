const { test } = require('node:test');
const assert = require('node:assert/strict');
const { parseSnapshot, presentService, percent, normalizeSettings } = require('../src/usage');
const now = Date.parse('2026-10-11T09:00:00Z');
const W={'5時間':{shortLabel:'5h',span:'5h'},'週':{shortLabel:'週',span:'week'},'週（Fable）':{shortLabel:'Fable',span:'week'}};
const win=(label,percent=0)=>({label,...W[label],percent,resetsAt:new Date(now+3600000).toISOString(),alert:null});
function payload() { return {schemaVersion:2, connected:true, services:['Claude','Codex','Grok'].map(name=>({name,plan:'Test',source:'オンライン',status:'',recordedAt:new Date(now).toISOString(),windows:(name==='Claude'?['5時間','週']:['週']).map(label=>win(label))}))}; }
test('missing is distinct from acquired zero and percentages are bounded',()=> {
  const p=payload();p.services[1].windows[0].percent=null;p.services[2].windows[0].percent=-1;
  const s=parseSnapshot(JSON.stringify(p));
  assert.equal(percent(s.services[0].windows[0].percent),'0%');
  assert.equal(percent(s.services[1].windows[0].percent),'未取得');
  assert.equal(percent(s.services[2].windows[0].percent),'未取得');
});
test('unsupported schemas, duplicate services, oversized and malformed responses reject',()=> {
  assert.throws(()=>parseSnapshot('{'));assert.throws(()=>parseSnapshot('x'.repeat(65537)));
  const p=payload();p.schemaVersion=1;assert.throws(()=>parseSnapshot(JSON.stringify(p)));
  p.schemaVersion=2;p.services[1].name='Claude';assert.throws(()=>parseSnapshot(JSON.stringify(p)));
});
test('zero to four windows are accepted with their short labels and spans',()=> {
  for(const labels of [[],['週'],['週','週（Fable）'],['5時間','週','週（Fable）','週（Other）']]) {
    const p=payload();p.services[0].windows=labels.map(label=>label==='週（Other）'?{...win('週（Fable）'),label,shortLabel:'Other'}:win(label));
    const s=parseSnapshot(JSON.stringify(p)).services[0];
    assert.deepEqual(s.windows.map(w=>[w.label,w.shortLabel,w.span]),p.services[0].windows.map(w=>[w.label,w.shortLabel,w.span]));
  }
});
test('five windows, duplicate labels, unknown spans and unknown labels reject',()=> {
  const five=payload();five.services[0].windows=['5時間','週','週（Fable）','週（A）','週（B）'].map(label=>({...win('週（Fable）'),label}));assert.throws(()=>parseSnapshot(JSON.stringify(five)));
  const dup=payload();dup.services[0].windows=[win('週'),win('週')];assert.throws(()=>parseSnapshot(JSON.stringify(dup)));
  const span=payload();span.services[0].windows=[{...win('週'),span:'day'}];assert.throws(()=>parseSnapshot(JSON.stringify(span)));
  const label=payload();label.services[0].windows=[{...win('週'),label:'月'}];assert.throws(()=>parseSnapshot(JSON.stringify(label)));
  const short=payload();short.services[0].windows=[{...win('週'),shortLabel:''}];assert.throws(()=>parseSnapshot(JSON.stringify(short)));
});
test('presentService works with three Claude windows',()=> {
  const p=payload();p.services[0].windows=[win('5時間',10),win('週',20),win('週（Fable）',96)];
  const v=presentService(parseSnapshot(JSON.stringify(p)).services[0],true,now);
  assert.deepEqual(v.windows.map(w=>w.label),['5時間','週','週（Fable）']);assert.equal(v.severity,'critical');assert.match(v.tooltip,/週（Fable） 96%/);
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
test('the AIの状況 status is kept when valid; a bad page or tone never passes through',()=> {
  const status={tone:'critical',title:'Claude 障害',detail:'Claude Code',page:'https://status.claude.com/',line:'Claude 障害（Claude Code）'};
  const p=payload();p.status=status;
  assert.deepEqual(parseSnapshot(JSON.stringify(p)).status,{...status,providers:[]});
  assert.equal(parseSnapshot(JSON.stringify(payload())).status,null);
  for(const tone of ['purple','',null,1]){const q=payload();q.status={...status,tone};assert.equal(parseSnapshot(JSON.stringify(q)).status,null,String(tone));}
  for(const page of ['https://evil.example/','http://status.claude.com/','https://status.claude.com/x','javascript:alert(1)',1]){const q=payload();q.status={...status,page};assert.equal(parseSnapshot(JSON.stringify(q)).status.page,null,String(page));}
  const openai=payload();openai.status={...status,page:'https://status.openai.com/'};assert.equal(parseSnapshot(JSON.stringify(openai)).status.page,'https://status.openai.com/');
  const long=payload();long.status={...status,title:'x'.repeat(100),detail:'y'.repeat(300)};const s=parseSnapshot(JSON.stringify(long)).status;
  assert.equal(s.title.length,60);assert.equal(s.detail.length,180);
  for(const bad of ['text',[],null]){const q=payload();q.status=bad;assert.equal(parseSnapshot(JSON.stringify(q)).status,null);}
  assert.equal(parseSnapshot(JSON.stringify({schemaVersion:2,connected:false,services:[],status})).status,null);
  assert.deepEqual(parseSnapshot(JSON.stringify(p)).status.providers,[],'no company lines from an older tray app');
});
test('company lines of the AIの状況 status are validated one by one',()=> {
  const p=payload();
  p.status={tone:'normal',title:'正常',detail:'',page:null,line:'',providers:[{company:'Claude',tone:'normal',detail:''},{company:'OpenAI',tone:'warning',detail:'Codex'.repeat(50)},{company:'',tone:'normal'},{company:'X',tone:'reset'},null,{company:'Y',tone:'unknown',detail:1},{company:'Z',tone:'critical'}]};
  const s=parseSnapshot(JSON.stringify(p)).status;
  assert.deepEqual(s.providers.map(x=>[x.company,x.tone,x.detail.length]),[['Claude','normal',0],['OpenAI','warning',180],['Y','unknown',0]]);
  p.status.providers='x';assert.deepEqual(parseSnapshot(JSON.stringify(p)).status.providers,[]);
});
test('waiting list is kept when valid; unknown apps, reasons and services are dropped; an item with no app has no link',()=> {
  const item=(over={})=>({service:'Claude',place:'Fix login',reason:'question',since:new Date(now).toISOString(),app:'claude',...over});
  const p=payload();
  p.waiting={items:[item(),item({app:'https'}),item({reason:'other'}),item({service:'X'}),item({since:'later'}),null,item({service:'Codex',app:'vscode',reason:'finished',place:'x'.repeat(60)}),item({app:null,place:'obsidian'}),item({app:undefined,place:'none'})],unreadable:[{service:'Codex',reason:'記録を読めません'},{service:'X',reason:'y'}]};
  const w=parseSnapshot(JSON.stringify(p)).waiting;
  assert.deepEqual(w.items.map(i=>[i.service,i.reason,i.app]),[['Claude','question','claude'],['Codex','finished','vscode'],['Claude','question',null],['Claude','question',null]]);
  assert.equal(w.items[1].place.length,40);
  assert.deepEqual(w.unreadable,[{service:'Codex',reason:'記録を読めません'}]);
  const many=payload();many.waiting={items:Array.from({length:9},()=>item()),unreadable:[]};
  assert.equal(parseSnapshot(JSON.stringify(many)).waiting.items.length,8);
  assert.equal(parseSnapshot(JSON.stringify(payload())).waiting,null,'a tray app that sends no list');
  for(const bad of ['x',[],null]){const q=payload();q.waiting=bad;assert.equal(parseSnapshot(JSON.stringify(q)).waiting,null);}
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
  assert.match(v.state,/学習中/);assert.doesNotMatch(v.state,/90%まで約/);
});
