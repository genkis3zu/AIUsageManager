'use strict';
const {Plugin,PluginSettingTab,Setting,MarkdownView,Notice}=require('obsidian');
const {normalizeSettings,emptyServices}=require('./usage');
const {BridgeClient,launchApp}=require('./bridge');
const {UsageToolbar}=require('./toolbar');
class UsageSettings extends PluginSettingTab {
  display() {
    this.containerEl.empty();
    new Setting(this.containerEl).setName('AI Usage Manager の実行ファイル').setDesc('AIUsageManager.exe のフルパス。常駐アプリの表示データと連携します。').addText(input=>input.setPlaceholder('C:\\Apps\\AIUsageManager.exe').setValue(this.plugin.settings.appPath).onChange(value=>this.plugin.setPath(value)));
    new Setting(this.containerEl).setName('表示サイズ').setDesc('ノート上部に固定します。本文には書き込みません。').addDropdown(input=>input.addOption('standard','標準（3行）').addOption('compact','縮小（1行）').setValue(this.plugin.settings.mode).onChange(value=>this.plugin.setMode(value)));
  }
}
class UsagePlugin extends Plugin {
  async onload() {
    this.closed=false;this.settings=normalizeSettings(await this.loadData()||{});this.bridge=new BridgeClient();this.toolbars=new Map();
    this.snapshot={connected:false,refreshing:false,services:emptyServices()};this.message='';this.polling=false;
    this.addSettingTab(new UsageSettings(this.app,this));
    this.addCommand({id:'toggle-size',name:'表示サイズを切り替え',callback:()=>this.setMode(this.settings.mode==='compact'?'standard':'compact')});
    this.addCommand({id:'refresh',name:'使用量を更新',callback:()=>this.command('--refresh')});
    this.addCommand({id:'open',name:'アプリで詳細を開く',callback:()=>this.command('--show')});
    for(const event of ['layout-change','active-leaf-change','file-open'])this.registerEvent(this.app.workspace.on(event,()=>this.reconcile()));
    this.app.workspace.onLayoutReady(()=>{if(!this.closed){this.reconcile();this.poll();}});
    this.registerInterval(window.setInterval(()=>this.poll(),15000));
  }
  reconcile() {
    if(this.closed)return;const current=new Set();
    for(const leaf of this.app.workspace.getLeavesOfType('markdown')) {
      const view=leaf.view;if(!(view instanceof MarkdownView)||!view.file||!view.contentEl?.parentElement)continue;
      const content=view.contentEl;current.add(content);
      const previous=this.toolbars.get(content);if(previous&&!previous.root.isConnected){previous.destroy();this.toolbars.delete(content);}
      if(!this.toolbars.has(content))this.toolbars.set(content,new UsageToolbar(content,{toggle:()=>this.setMode(this.settings.mode==='compact'?'standard':'compact'),refresh:()=>this.command('--refresh'),open:()=>this.command('--show')}));
    }
    for(const [content,toolbar] of this.toolbars)if(!current.has(content)||!toolbar.root.isConnected){toolbar.destroy();this.toolbars.delete(content);}
    this.render();
  }
  render() {if(!this.closed)for(const toolbar of this.toolbars.values())toolbar.update(this.snapshot,this.settings.mode,this.message);}
  async poll() {
    if(this.closed||this.polling)return;
    if(!this.settings.appPath){this.message='設定でアプリの場所を指定';this.snapshot={connected:false,services:emptyServices()};this.render();return;}
    this.polling=true;const bridge=this.bridge;
    try {
      const snapshot=await bridge.read(this.settings.appPath);
      if(this.closed||bridge!==this.bridge)return;
      this.snapshot=snapshot;this.message=snapshot.connected?'':'アプリ未接続 · ↻で起動';
    } catch {
      if(this.closed||bridge!==this.bridge)return;
      this.snapshot={connected:false,services:emptyServices()};this.message='連携できません · 設定を確認';
    } finally {this.polling=false;this.render();}
  }
  async save() {try {await this.saveData(this.settings);}catch {if(!this.closed)new Notice('AI 使用量の設定を保存できませんでした');}}
  setMode(mode) {this.settings.mode=mode==='compact'?'compact':'standard';this.render();this.save();}
  setPath(value) {
    clearTimeout(this.settingsTimer);
    this.settingsTimer=setTimeout(async()=> {
      if(this.closed)return;this.bridge.dispose();this.bridge=new BridgeClient();this.settings=normalizeSettings({...this.settings,appPath:value.trim()});
      this.snapshot={connected:false,services:emptyServices()};await this.save();await this.poll();
    },500);
  }
  async command(action) {
    if(this.closed)return;
    if(!this.settings.appPath){new Notice('AI Usage Manager の設定で実行ファイルの場所を指定してください');return;}
    try {await launchApp(this.settings.appPath,action);if(action==='--refresh'){this.message='使用量を更新中…';this.render();clearTimeout(this.refreshTimer);this.refreshTimer=setTimeout(()=>this.poll(),1500);}}
    catch {new Notice('AI Usage Manager を起動できませんでした。設定の実行ファイルを確認してください');}
  }
  onunload() {this.closed=true;clearTimeout(this.settingsTimer);clearTimeout(this.refreshTimer);this.bridge?.dispose();for(const toolbar of this.toolbars?.values()||[])toolbar.destroy();this.toolbars?.clear();}
}
module.exports=UsagePlugin;
