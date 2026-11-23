'use strict';
// Hands a line from the いまやってること window (Ctrl+Enter) to Claudian's chat.
// The window leaves the text in %LOCALAPPDATA%\AIUsageManager\claudian-message.json with a one-time token and opens
// obsidian://ai-usage-manager-claudian?token=…. Only a request that carries that token sends anything, so a link on
// a web page or in a note cannot make Claudian run a prompt.
const fs=require('node:fs');
const path=require('node:path');
const ACTION='ai-usage-manager-claudian';
const VIEW_TYPE='claudian-view';
const MAX_AGE_MS=120000,MAX_FILE=16384,MAX_TEXT=1000;
function messagePath(env=process.env) {
  const base=env&&env.LOCALAPPDATA;
  return typeof base==='string'&&base?path.join(base,'AIUsageManager','claudian-message.json'):null;
}
// The text to send, or null. A file whose token matches is removed, whether or not it is still fresh.
function takeMessage(token,file,now=Date.now(),files=fs) {
  if(typeof token!=='string'||!/^[0-9a-f]{32}$/.test(token)||!file)return null;
  let data;
  try {
    const stat=files.statSync(file);
    if(!stat.isFile()||stat.size>MAX_FILE)return null;
    data=JSON.parse(files.readFileSync(file,'utf8'));
  } catch {return null;}
  if(!data||data.token!==token)return null;
  try {files.unlinkSync(file);} catch {}
  const created=data.created;
  if(typeof created!=='number'||!Number.isFinite(created)||now-created>MAX_AGE_MS||created-now>5000)return null;
  const text=typeof data.text==='string'?data.text.trim():'';
  return text&&text.length<=MAX_TEXT?text:null;
}
const wait=ms=>new Promise(resolve=>setTimeout(resolve,ms));
// Opens Claudian's chat and sends the text as a new message there, as if it had been typed into Claudian.
// Returns '' when sent, otherwise the reason to show. Claudian has no public way to receive text, so this uses
// the same parts its own Enter key uses; a Claudian update can change them, and then the reason says so.
async function sendToClaudian(app,text,pause=wait) {
  const claudian=app&&app.plugins&&app.plugins.plugins&&app.plugins.plugins.claudian;
  if(!claudian||typeof claudian.activateView!=='function')return 'Claudian が見つかりません。Claudian を入れて有効にしてください';
  try {await claudian.activateView();} catch {return 'Claudian の画面を開けませんでした';}
  // A chat that was just opened needs a moment before it can take a message.
  for(let i=0;i<30;i++) {
    const leaf=app.workspace.getLeavesOfType(VIEW_TYPE)[0];
    const view=leaf&&leaf.view;
    const tab=view&&typeof view.getActiveTab==='function'?view.getActiveTab():null;
    const input=tab&&tab.controllers&&tab.controllers.inputController;
    if(input&&typeof input.sendMessage==='function') {
      Promise.resolve().then(()=>input.sendMessage({content:text})).catch(()=>{});
      return '';
    }
    await pause(100);
  }
  return 'Claudian に送れませんでした。Claudian の版が変わった可能性があります';
}
module.exports={ACTION,messagePath,takeMessage,sendToClaudian,MAX_AGE_MS};
