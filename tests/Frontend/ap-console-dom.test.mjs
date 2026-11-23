import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {test} from 'node:test';
import {runInNewContext} from 'node:vm';
import * as ui from '../../src/AirWaveApMonitor.App/wwwroot/ui-state.js';
import {summarizeCollectionWarnings} from '../../src/AirWaveApMonitor.App/wwwroot/run-warnings.js';

const js=(await readFile(new URL('../../src/AirWaveApMonitor.App/wwwroot/app.js',import.meta.url),'utf8')).replace(/\r\n/g,'\n');
const functions=js.split('\ndocument.querySelectorAll("[data-view]")')[0].replace(/^import .*;$/gm,'');
class Element {
  constructor(tag){this.tag=tag;this.childNodes=[];this.dataset={};this.handlers={};this.attrs={};this.value='';this.text='';}
  set textContent(v){this.text=String(v);this.childNodes=[];}
  get textContent(){return this.text+this.childNodes.map(el=>el.textContent).join(' ');}
  get firstChild(){return this.childNodes[0]||null;}
  append(...nodes){this.childNodes.push(...nodes);}
  replaceChildren(...nodes){this.text='';this.childNodes=nodes;}
  setAttribute(k,v){this.attrs[k]=v;}
  removeAttribute(k){delete this.attrs[k];}
  addEventListener(k,fn){this.handlers[k]=fn;}
  click(){return this.handlers.click?.({currentTarget:this});}
  focus(){} scrollIntoView(){}
}
function find(root,tag,text){return [root,...root.childNodes.flatMap(x=>flatten(x))].find(x=>x.tag===tag&&x.textContent.includes(text));}
function flatten(e){return [e,...e.childNodes.flatMap(flatten)];}
const selection={datasetId:'data',scopeVersion:2,runId:8,finalizedAtUtc:'2026-11-16T01:00:00Z'};
function incident(id,ap) {
  return {incidentId:id,apId:ap,sourceApId:ap,status:'Open',tracking:{firstDownAtUtc:'2026-11-16T00:00:00Z',firstDownRunId:5},
    detection:{runId:5,scopeVersion:2,device:{apId:ap,deviceGenerationId:'generation',metadata:{device:{name:ap},identity:{},location:{},upstream:{}}}},
    observationGaps:[]};
}
function detail(ap){
  return {selection,row:{apId:ap,sourceApId:ap,deviceGenerationId:'generation',currentState:'Down',classification:'ExistingDown',
    device:{name:ap,model:'AP-535'},identity:{},location:{},controller:{},fields:{},enrichmentDetail:{},openIncidentId:'current-'+ap},
    incidents:[incident('current-'+ap,ap),incident('previous-'+ap,ap)],observations:[],observationGaps:[]};
}
function deferred(){let resolve;const promise=new Promise(r=>{resolve=r});return {promise,resolve};}
const wait=()=>new Promise(r=>setImmediate(r));
function harness(load){
  const nodes=new Map(),contexts=[],work=[];
  const doc={getElementById(key){if(!nodes.has(key)){const n=new Element('div');n.id=key;nodes.set(key,n)}return nodes.get(key)},
    createElement:tag=>new Element(tag),querySelectorAll:()=>[]};
  const tag=(text)=>{const n=new Element('section');n.textContent=text;return n;};
  const ops={displayValue:(r,k,v)=>v,snapshotPanel:()=>tag('선택 회차의 대장'),actions:()=>tag('운영 작업'),show(){},
    writeWork:c=>work.push(c),hasSavedIncidentDraft:()=>false,incidentMemoForm:c=>tag('빠른 조치 입력 '+c.incidentId),async incidentEventsPanel(c){contexts.push(c);return tag('조치 메모 '+c.incidentId);}};
  const enrich={snapshotPanel:()=>tag('전력 측정 근거'),actions:()=>tag('전력 표본'),show(){}};
  const context={...ui,summarizeCollectionWarnings,document:doc,Node:Element,URL,URLSearchParams,AbortController,setTimeout,clearTimeout,transport:load,ops,enrich,
    createOperationsController:()=>ops,createEnrichmentController:()=>enrich};
  runInNewContext(functions+'\napi=transport; operations=ops; enrichment=enrich; globalThis.subject={showAp,showIncident,state};',context);
  const h=context.subject;h.state.selection=selection;
  return {...h,nodes,contexts,work,ops,panel:doc.getElementById('ap-console-detail')};
}
test('AP selection stays with the list; each of four tabs uses the same saved AP run',async()=>{
  const calls=[];const h=harness(path=>{calls.push(path);return detail('AP-A');});
  await h.showAp('AP-A',selection);
  assert.equal(h.state.view,'dashboard');
  for(const name of ['상태','장애·조치','연결·전력','대장'])assert.ok(find(h.panel,'button',name),name);
  assert.match(h.panel.textContent,/AP-A/);assert.doesNotMatch(h.panel.textContent,/전력 측정 근거/);
  await find(h.panel,'button','연결·전력').click();
  assert.match(h.panel.textContent,/전력 측정 근거/);assert.doesNotMatch(h.panel.textContent,/선택 회차의 대장/);
  await find(h.panel,'button','대장').click();assert.match(h.panel.textContent,/선택 회차의 대장/);
  assert.equal(calls.filter(path=>path.startsWith('/api/aps/')).length,1);
  assert.equal(new URL(calls[0],'http://local').searchParams.get('runId'),'8');
});
test('a stale AP read never replaces the currently selected AP',async()=>{
  const first=deferred();const h=harness(path=>path.includes('/AP-A?')?first.promise:detail('AP-B'));
  const loading=h.showAp('AP-A',selection);await h.showAp('AP-B',selection);first.resolve(detail('AP-A'));await loading;
  assert.match(h.panel.textContent,/AP-B/);assert.doesNotMatch(h.panel.textContent,/AP-A/);
});
test('two incidents of one AP have distinct notes and stable historical bounds',async()=>{
  const h=harness(()=>detail('AP-A'));h.state.historical=true;await h.showAp('AP-A',selection,'incident');
  await wait();assert.equal(h.contexts.at(-1)?.incidentId,'current-AP-A');assert.equal(h.contexts.at(-1)?.asOfRunId,8);
  const other=flatten(h.panel).find(n=>n.dataset.incidentId==='previous-AP-A');assert.ok(other);
  await other.click();await wait();
  assert.match(h.panel.textContent,/조치 메모 previous-AP-A/);assert.doesNotMatch(h.panel.textContent,/조치 메모 current-AP-A/);
  await find(h.panel,'button','이 사건에 작업·메모 기록').click();assert.equal(h.work.at(-1).incidentId,'previous-AP-A');
});

test('an open incident leads with a directly editable note bound to the selected AP and incident',async()=>{
  const h=harness(()=>detail('AP-A'));await h.showAp('AP-A',selection,'incident',false,'current-AP-A');
  assert.ok(h.panel.textContent.includes('AP-A · 미복구'));
  assert.ok(h.panel.textContent.indexOf('빠른 조치 입력 current-AP-A')<h.panel.textContent.indexOf('조치 메모 current-AP-A'));
  h.state.historical=true;await h.showAp('AP-A',selection,'incident',false,'current-AP-A');
  assert.doesNotMatch(h.panel.textContent,/빠른 조치 입력/);
  const recovered=detail('AP-A');recovered.incidents[0].status='Recovered';
  const other=harness(()=>recovered);await other.showAp('AP-A',selection,'incident');
  assert.doesNotMatch(other.panel.textContent,/빠른 조치 입력/);
  other.ops.hasSavedIncidentDraft=c=>c.incidentId==='current-AP-A';
  await other.showAp('AP-A',selection,'incident');assert.match(other.panel.textContent,/빠른 조치 입력 current-AP-A/);
});

test('a deep link picks the exact case even when older than the first page of cases',async()=>{
  const h=harness(path=>path.startsWith('/api/incidents/archived?') ? incident('archived','AP-A') : detail('AP-A'));
  await h.showAp('AP-A',selection,'incident',false,'archived');
  assert.equal(h.contexts.at(-1)?.incidentId,'archived');
  assert.doesNotMatch(h.panel.textContent,/조치 메모 current-AP-A/);
});
test('a deep link for an earlier hardware generation cannot attach work to the current generation',async()=>{
  const earlier=incident('archived','AP-A');earlier.detection.device.deviceGenerationId='old-generation';
  const h=harness(path=>path.startsWith('/api/incidents/archived?')?earlier:detail('AP-A'));
  await h.showAp('AP-A',selection,'incident',false,'archived');
  assert.equal(h.contexts.length,0);assert.match(h.panel.textContent,/장비 세대/);
});
