'use strict';
const {presentService}=require('./usage');
function element(doc,tag,cls,text) {const el=doc.createElement(tag);el.className=cls;if(text!==undefined)el.textContent=text;return el;}
class UsageToolbar {
  constructor(content,actions) {
    const doc=content.ownerDocument;
    this.content=content;this.parent=content.parentElement;this.actions=actions;
    this.root=element(doc,'section','aiut-toolbar');this.root.setAttribute('aria-label','AI 使用量');
    this.list=element(doc,'div','aiut-list');this.controls=element(doc,'div','aiut-controls');
    this.root.append(this.list,this.controls);this.rows=[];
    for(const name of ['Claude','Codex','Grok']) {
      const row=element(doc,'div','aiut-row');row.dataset.service=name.toLowerCase();
      const button=element(doc,'button','aiut-service',name);button.type='button';button.addEventListener('click',()=>actions.open());
      const indicator=element(doc,'span','aiut-indicator');indicator.setAttribute('aria-hidden','true');button.prepend(indicator);
      const windows=element(doc,'div','aiut-windows');const slots=[];
      for(const label of name==='Claude'?['5時間','週']:['週']) {
        const slot=element(doc,'span','aiut-window'),caption=element(doc,'span','aiut-label',label==='5時間'?'5h':label);
        const value=element(doc,'span','aiut-value','未取得'),track=element(doc,'span','aiut-track'),fill=element(doc,'span','aiut-fill'),reset=element(doc,'span','aiut-reset');
        track.setAttribute('aria-hidden','true');track.append(fill);slot.append(caption,value,track,reset);windows.append(slot);slots.push({slot,value,fill,reset});
      }
      const state=element(doc,'span','aiut-state'),time=element(doc,'span','aiut-time');row.append(button,windows,state,time);this.list.append(row);
      this.rows.push({row,button,indicator,slots,state,time});
    }
    this.toggle=this.control(doc,'縮小表示（1行）','⇡',()=>actions.toggle());
    this.refresh=this.control(doc,'使用量を更新','↻',()=>actions.refresh());
    this.open=this.control(doc,'アプリで詳細を開く','↗',()=>actions.open());
    this.parent.classList.add('aiut-host');this.parent.insertBefore(this.root,content);
  }
  control(doc,label,glyph,run) {
    const button=element(doc,'button','aiut-control',glyph);button.type='button';button.title=label;button.setAttribute('aria-label',label);button.addEventListener('click',run);this.controls.append(button);return button;
  }
  update(snapshot,mode,message,now=Date.now()) {
    const compact=mode==='compact';this.root.classList.toggle('is-compact',compact);
    this.toggle.textContent=compact?'⇣':'⇡';this.toggle.title=compact?'標準表示（3行）':'縮小表示（1行）';this.toggle.setAttribute('aria-label',this.toggle.title);this.toggle.setAttribute('aria-expanded',String(!compact));
    this.refresh.disabled=snapshot.refreshing===true;this.root.title=message || 'AI Usage Manager と連携中';
    snapshot.services.forEach((service,index)=> {
      const ui=this.rows[index],v=presentService(service,snapshot.connected,now);
      ui.row.dataset.severity=v.severity;ui.row.title=v.tooltip;
      ui.button.setAttribute('aria-label',`${v.tooltip}。アプリで詳細を開く`);
      ui.indicator.textContent=['warning','critical'].includes(v.severity)?'⚠':'●';
      ui.state.textContent=message || v.state;ui.state.title=message || v.tooltip;ui.time.textContent=`取得 ${v.stamp}`;
      v.windows.forEach((w,i)=> {
        const slot=ui.slots[i];slot.value.textContent=w.display;slot.fill.style.width=`${Math.min(100,w.percent||0)}%`;
        slot.slot.classList.toggle('is-high',w.active && w.percent>=80);slot.slot.classList.toggle('is-stale',!w.active);
        slot.slot.classList.toggle('is-unknown',w.percent===null);
        slot.reset.textContent=w.reset;slot.slot.title=`${w.label} ${w.display} / リセット ${w.reset}`;
      });
    });
  }
  destroy() {this.root.remove();if(!Array.from(this.parent.children).some(el=>el.classList.contains('aiut-toolbar')))this.parent.classList.remove('aiut-host');}
}
module.exports={UsageToolbar};
