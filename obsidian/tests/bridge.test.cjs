const { test } = require('node:test');
const assert = require('node:assert/strict');
const { BridgeClient } = require('../src/bridge');
test('snapshot requests have fixed arguments, timeout, output bound and no shell',async()=> {
  let args;const b=new BridgeClient((...values)=> {args=values;queueMicrotask(()=>values[3](null,'{"schemaVersion":1,"connected":false,"services":[]}',''));return {kill(){}};});
  assert.equal((await b.read('C:\\Apps\\AIUsageManager.exe')).connected,false);
  assert.deepEqual(args[1],['--obsidian-data']);assert.equal(args[2].shell,false);assert.equal(args[2].windowsHide,true);assert.equal(args[2].maxBuffer,65536);assert.equal(args[2].timeout,4000);
});
test('untrusted command strings never launch a process',async()=> {
  let called=false;const b=new BridgeClient(()=>{called=true;});
  await assert.rejects(b.read('cmd /c bad'));assert.equal(called,false);
});
test('root-relative, network and device paths never launch a helper',async()=> {
  let called=false;const b=new BridgeClient(()=>{called=true;throw new Error('Unexpected helper launch');});
  for(const file of ['\\Apps\\AIUsageManager.exe','/Apps/AIUsageManager.exe','//server/share/AIUsageManager.exe','\\\\server\\share\\AIUsageManager.exe','\\\\?\\C:\\Apps\\AIUsageManager.exe','C:Apps\\AIUsageManager.exe'])await assert.rejects(b.read(file));
  assert.equal(called,false);
});
test('overlapping reads share one request, and dispose terminates the helper',async()=> {
  let callback,count=0,killed=false;const b=new BridgeClient((file,args,options,done)=>{count++;callback=done;return {kill(){killed=true;callback(new Error('cancel'));}};});
  const first=b.read('C:\\Apps\\AIUsageManager.exe');const second=b.read('C:\\Apps\\AIUsageManager.exe');
  assert.equal(count,1);b.dispose();await assert.rejects(first);await assert.rejects(second);assert.equal(killed,true);
});
