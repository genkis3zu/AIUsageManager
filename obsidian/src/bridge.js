'use strict';
const {execFile,spawn}=require('node:child_process');
const {parseSnapshot,validPath}=require('./usage');
class BridgeClient {
  constructor(execute=execFile) {this.execute=execute;this.pending=null;this.child=null;this.closed=false;}
  read(file) {
    if(this.closed || !validPath(file))return Promise.reject(new Error('Invalid app path'));
    if(this.pending)return this.pending;
    this.pending=new Promise((resolve,reject)=> {
      this.child=this.execute(file,['--obsidian-data'],{shell:false,windowsHide:true,encoding:'utf8',timeout:4000,maxBuffer:65536},(error,stdout)=> {
        this.child=null;
        if(error || this.closed){reject(new Error('Usage app unavailable'));return;}
        try {resolve(parseSnapshot(stdout));}catch {reject(new Error('Invalid usage response'));}
      });
    }).finally(()=>{this.pending=null;});
    return this.pending;
  }
  dispose() {this.closed=true;if(this.child)this.child.kill();this.child=null;}
}
function launchApp(file,action) {
  if(!validPath(file) || !['--show','--refresh'].includes(action))return Promise.reject(new Error('Invalid app command'));
  return new Promise((resolve,reject)=> {
    const child=spawn(file,[action],{shell:false,windowsHide:true,detached:true,stdio:'ignore'});
    child.once('error',()=>reject(new Error('Unable to launch app')));
    child.once('spawn',()=>{child.unref();resolve();});
  });
}
module.exports={BridgeClient,launchApp};
