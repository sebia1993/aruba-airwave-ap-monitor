import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {runInNewContext} from 'node:vm';
import * as ui from '../../src/AirWaveApMonitor.App/wwwroot/ui-state.js';
import {powerQuery} from '../../src/AirWaveApMonitor.App/wwwroot/enrichment.js';

// Execute production query/render actions with only the network and DOM boundary replaced.
class Node {
  constructor(tag='div'){this.tagName=tag;this.childNodes=[];this.dataset={};this.value='';this._text='';this.listeners={};}
  get firstChild(){return this.childNodes[0]||null;}
  get textContent(){return this._text+this.childNodes.map(n=>n.textContent).join('');}
  set textContent(v){this._text=String(v);this.childNodes=[];}
  append(...nodes){this.childNodes.push(...nodes);}
  replaceChildren(...nodes){this._text='';this.childNodes=nodes;}
  setAttribute(){} removeAttribute(){} addEventListener(name,fn){this.listeners[name]=fn;}
  querySelector(){if(!this.firstChild)this.append(new Node("button"));return this.firstChild;} focus(){}
}
// Normalize the fixture's text boundaries so Windows CRLF checkout does not include application startup.
const source=(await readFile(new URL('../../src/AirWaveApMonitor.App/wwwroot/app.js',import.meta.url),'utf8')).replace(/\r\n/g,'\n');
const html=await readFile(new URL('../../src/AirWaveApMonitor.App/wwwroot/index.html',import.meta.url),'utf8');
const ids=new Set([...html.matchAll(/id="([^"]+)"/g)].map(match=>match[1]));
const functions=source.split('\ndocument.querySelectorAll("[data-view]")')[0].replace(/^import .*;$/gm,'');
function app(transport=async()=>{throw Error('Unexpected network request');}){
  const nodes=new Map(),document={getElementById(id){if(!ids.has(id))throw Error('Missing rendered HTML element: '+id);if(!nodes.has(id))nodes.set(id,new Node());return nodes.get(id);},createElement:tag=>new Node(tag),querySelectorAll:()=>[],addEventListener:()=>{}};
  const context={...ui,document,Node,URLSearchParams,AbortController,setTimeout,clearTimeout,transport,createOperationsController:()=>({}),createEnrichmentController:()=>({})};
  runInNewContext(`${functions}\napi=transport;globalThis.subject={state,dateText,loadIncidents,loadRuns,loadDashboard,applyPeriod:(...args)=>applyPeriod(...args),loadScope:(...args)=>loadScope(...args),saveScope:(...args)=>saveScope(...args),confirmBaseline:(...args)=>confirmBaseline(...args),scopeDraftChanged:(...args)=>scopeDraftChanged(...args),applySettings,renderDiagnostic};`,context);
  return {...context.subject,nodes,document,bind(){const rest=source.slice(source.indexOf('\ndocument.querySelectorAll("[data-view]")')).split('\nupdateControls();\nvoid loadDashboard')[0];runInNewContext(rest,context);}};
}
const selection={datasetId:'demo',runId:17,scopeVersion:3,startedAtUtc:'2026-11-15T00:00:00Z',finalizedAtUtc:'2026-11-15T00:05:00Z'};
const incidents=overrides=>({selection,rows:[],totalRows:40,distinctApCount:12,upToDownTransitionCount:33,initialDownCount:7,page:1,pageSize:50,...overrides});
const runs=overrides=>({datasetId:'demo',rows:[],totalRows:0,availableScopes:[3],throughRunId:31,page:1,pageSize:50,...overrides});
const settings=config=>({mode:'demo',profile:{datasetId:'demo',origin:'https://demo.invalid',configurationVersion:config,scopeVersion:config,credentialsVersion:1,settings:{apIntervalMinutes:5},proxy:{mode:0}},credentialStatus:'synthetic-memory',authenticationBlocked:false});
const scopeView=(config=1)=>({profile:{...settings(config).profile,monitoringScope:null,scopeRevisions:[],baselineStatus:'Unverified'},knownTargets:[{sourceApId:'ap-1',name:'first',ipAddress:null,lastValidRunId:11},{sourceApId:'ap-2',name:'second',ipAddress:null,lastValidRunId:11}],comparisonRun:{runId:10+config,configurationVersion:config,scopeVersion:config,targetCount:2,outcome:'Succeeded',baselineStatus:'Unverified',sourceApIds:['ap-1','ap-2']}});
const flush=async()=>{for(let i=0;i<6;i++)await new Promise(resolve=>setTimeout(resolve,0));};
function certificateView(checks, options = {}) {
  const x = app();
  x.state.mode = options.mode ?? "live";
  x.renderDiagnostic({
    diagnosticId:"safe-id", executionAccount:"test-user", connectionPath:"direct",
    httpStatus:null, loginRequests:1, apRequests:0, parsedRecords:0, excludedRecords:0,
    elapsedMilliseconds:3, outcome:2, tlsResult:"failed", tlsCertificateChecks:checks, ...options
  });
  const nodes = x.document.getElementById("diagnostic-details").childNodes;
  const rows = new Map();
  for (let index = 0; index < nodes.length; index += 2) rows.set(nodes[index].textContent, nodes[index + 1].textContent);
  return { rows, notes:x.document.getElementById("diagnostic-notes").textContent };
}
const cases=[
  ['Concurrent name and chain failures retain independent actions and only known reasons',async()=>{
    const {rows, notes} = certificateView({
      serverName:"mismatch", trustChain:"failed", chainReasons:["untrusted-root","partial-chain","internal-ca.example.invalid","__proto__"]
    }, {tlsFailureCode:"certificate-name-mismatch"});
    assert.match(rows.get("TLS 실패 원인"), /이름 불일치.*신뢰 체인 검증 실패/);
    assert.equal(rows.get("인증서 서버 이름"), "불일치");
    assert.equal(rows.get("인증서 신뢰 체인"), "검증 실패");
    assert.match(rows.get("신뢰 체인 상세"), /신뢰할 수 없는 루트/);
    assert.match(rows.get("신뢰 체인 상세"), /체인 불완전/);
    assert.match(notes, /접속 주소.*인증서/);
    assert.match(notes, /루트.*중간 CA/);
    assert.doesNotMatch([...rows.values(), notes].join(" "), /internal-ca|__proto__|\[object Object\]/);
  }],
  ['Certificate name and chain can have different results',async()=>{
    const {rows, notes} = certificateView({serverName:"matched",trustChain:"failed",chainReasons:["expired"]});
    assert.equal(rows.get("인증서 서버 이름"), "일치");
    assert.equal(rows.get("인증서 신뢰 체인"), "검증 실패");
    assert.match(rows.get("신뢰 체인 상세"), /만료/);
    assert.match(notes, /시각/);
    assert.doesNotMatch(notes, /접속 주소.*인증서/);
    const success = certificateView({serverName:"matched",trustChain:"valid",chainReasons:[]},{tlsResult:"validated"});
    assert.equal(success.rows.get("인증서 신뢰 체인"), "검증 통과");
    assert.equal(success.notes,"");
  }],
  ['An HTTPS proxy certificate failure points to the proxy and leaves the AirWave certificate unknown',async()=>{
    const checks = {peer:"proxy",serverName:"mismatch",trustChain:"failed",chainReasons:["partial-chain"]};
    const options = {connectionPath:"fixed-proxy",tlsFailureCode:"certificate-name-mismatch"};
    const {rows, notes} = certificateView(checks, options);
    assert.match(rows.get("TLS 실패 원인"), /프록시.*이름 불일치.*신뢰 체인 검증 실패/);
    assert.equal(rows.get("프록시 인증서 서버 이름"), "불일치");
    assert.equal(rows.get("프록시 인증서 신뢰 체인"), "검증 실패");
    assert.match(rows.get("프록시 신뢰 체인 상세"), /체인 불완전/);
    assert.match(rows.get("AirWave 인증서"), /미확인/);
    assert.equal(rows.has("인증서 서버 이름"), false);
    assert.match(notes, /프록시 접속 주소.*인증서/);
    assert.doesNotMatch(notes, /AirWave 접속 주소/);
    const single = certificateView({...checks,trustChain:"valid",chainReasons:[]}, options);
    assert.match(single.rows.get("TLS 실패 원인"), /프록시.*이름 불일치/);
    const target = certificateView({...checks,peer:"server"}, options);
    assert.equal(target.rows.get("인증서 서버 이름"), "불일치");
    assert.equal(target.rows.has("AirWave 인증서"), false);
    assert.match(target.notes, /AirWave 접속 주소/);
  }],
  ['Unobserved certificate checks remain unknown and legacy and Demo reports still render',async()=>{
    for (const serverName of ["unknown", "future-status"]) {
      const {rows, notes} = certificateView({peer:"unknown",serverName,trustChain:"unknown",chainReasons:[]});
      assert.equal(rows.get("인증서 서버 이름"), "미확인");
      assert.equal(rows.get("인증서 신뢰 체인"), "미확인");
      assert.equal(rows.has("신뢰 체인 상세"), false);
      assert.equal(notes, "");
    }
    const legacy = certificateView(undefined,{tlsFailureCode:"certificate-trust-chain"});
    assert.match(legacy.rows.get("TLS 실패 원인"), /사내 CA/);
    assert.equal(legacy.rows.has("인증서 신뢰 체인"), false);
    const demo = certificateView(null,{mode:"demo",tlsResult:"not-applicable-demo"});
    assert.match(demo.rows.get("TLS"), /가상 자료/);
    assert.equal(demo.rows.has("인증서 서버 이름"), false);
  }],
  ['TLS diagnostics show safe specific cause, proxy hint and no credential material',async()=>{
    const x=app();x.state.mode='live';
    x.renderDiagnostic({
      outcome:'Failed',diagnosticId:'safe-id',executionAccount:'test-user',connectionPath:'system-execution-account',
      tlsResult:'failed',tlsFailureCode:'certificate-trust-chain',proxyRouteHint:'system-proxy-selected',
      httpStatus:null,loginRequests:1,apRequests:0,parsedRecords:0,excludedRecords:0,elapsedMilliseconds:5,
      stages:[{name:'login',status:'failed',elapsedMilliseconds:5,message:'TLS 인증서 신뢰 체인 실패'}],
      errors:[{safeMessage:'TLS 인증서 신뢰 체인 실패. HTTP_STATUS=미수신'}],verificationNotes:[]
    });
    const details=x.document.getElementById('diagnostic-details').textContent;
    assert.match(details,/신뢰 체인.*사내 CA/);
    assert.match(details,/기본 프록시 선택/);
    assert.match(details,/미수신/);
    assert.match(x.document.getElementById('diagnostic-notes').textContent,/실제 네트워크 경유 성공을 보증하지 않습니다/);
    assert.doesNotMatch(details,/secret-token|password=/);
  }],
  ['Partial baseline with an unobserved target stays disabled and cannot submit',async()=>{
    let posts=0;const value=scopeView();value.comparisonRun.outcome='Partial';value.comparisonRun.sourceApIds=['ap-1'];
    const x=app(async(url,options)=>{if(options?.method==='POST')posts++;return value;});await x.loadScope();
    assert.equal(x.document.getElementById('baseline-confirm').disabled,true);
    assert.match(x.document.getElementById('scope-baseline').textContent,/미관측 대상 포함.*대조 등록 불가/);
    x.document.getElementById('baseline-count').value='2';x.document.getElementById('baseline-reason').value='inventory compared';
    await x.confirmBaseline();assert.equal(posts,0);
  }],
  ['Partial baseline remains eligible when every target has a valid observation',async()=>{
    const sent=[],value=scopeView();value.comparisonRun.outcome='Partial';
    const x=app(async(url,options)=>{if(options?.method==='POST')sent.push({url,body:options.body});return url==='/api/settings'?settings(1):value;});await x.loadScope();
    assert.equal(x.document.getElementById('baseline-confirm').disabled,false);
    assert.doesNotMatch(x.document.getElementById('scope-baseline').textContent,/미관측 대상 포함/);
    x.document.getElementById('baseline-count').value='2';x.document.getElementById('baseline-reason').value='inventory compared';
    await x.confirmBaseline();assert.equal(sent.length,1);assert.equal(sent[0].url,'/api/scope/confirm');
    assert.equal(sent[0].body.runId,11);assert.equal(sent[0].body.expectedConfigurationVersion,1);assert.equal(sent[0].body.comparedTargetCount,2);
  }],
  ['An explicit scope reload cannot erase inputs typed while that read was pending',async()=>{let release,reads=0;const x=app(async()=>++reads===1?scopeView():await new Promise(r=>release=r));await x.loadScope();const reloading=x.loadScope({resetScope:true,resetBaseline:true});x.document.getElementById('scope-reason').value='new scope draft';x.scopeDraftChanged('scope');x.document.getElementById('baseline-reason').value='new comparison draft';x.scopeDraftChanged('baseline');release(scopeView(2));await reloading;assert.equal(x.document.getElementById('scope-reason').value,'new scope draft');assert.equal(x.document.getElementById('baseline-reason').value,'new comparison draft');}],
  ['Rendered period form submission reaches the actual incident query handler',async()=>{let url;const x=app(async path=>{url=path;return incidents();});x.state.view='incidents';x.state.selection=selection;x.bind();x.document.getElementById('incident-from').value='2026-11-15T09:00';x.document.getElementById('incident-period-form').listeners.submit({preventDefault(){}});await flush();assert.equal(new URL(url,'http://local').searchParams.get('fromUtc'),'2026-11-15T00:00:00.000Z');x.document.getElementById('incident-minimum-elapsed').listeners.change({target:{value:'86400'}});await flush();assert.equal(new URL(url,'http://local').searchParams.get('minimumOpenElapsedSeconds'),'86400');}],
  ['Rendered scope form submission uses the selected IDs and reason without an automatic baseline confirmation',async()=>{const posts=[];const x=app(async(path,options)=>{if(options?.method==='POST')posts.push({path,body:options.body});return path==='/api/settings'?settings(2):scopeView(options?.method==='POST'?2:1);});x.bind();await x.loadScope();x.document.getElementById('scope-mode').value='SelectedApIds';x.document.getElementById('scope-mode').listeners.change();x.document.getElementById('scope-ids').value='ap-1';x.document.getElementById('scope-reason').value='chosen scope';x.document.getElementById('scope-form').listeners.submit({preventDefault(){}});await flush();assert.equal(posts.length,1);assert.equal(posts[0].path,'/api/scope');assert.equal(posts[0].body.reason,'chosen scope');assert.equal(x.document.getElementById('baseline-count').value,'');}],
  ['Scope edits reject duplicates and unknown IDs before sending a mutation',async()=>{let posts=0;const x=app(async(url,options)=>{if(options?.method==='POST')posts++;return scopeView();});await x.loadScope();x.document.getElementById('scope-mode').value='SelectedApIds';x.document.getElementById('scope-reason').value='changed';for(const ids of ['ap-1\nap-1','missing','']){x.document.getElementById('scope-ids').value=ids;await x.saveScope();}assert.equal(posts,0);}],
  ['Dirty scope CAS stays on its loaded version after a server refresh and conflict',async()=>{let config=1;const sent=[];const x=app(async(url,options)=>{if(options?.method==='POST'){sent.push(options.body);throw Object.assign(Error('conflict'),{status:409});}return scopeView(config);});await x.loadScope();x.document.getElementById('scope-mode').value='SelectedApIds';x.document.getElementById('scope-ids').value='ap-1';x.document.getElementById('scope-reason').value='my draft';x.scopeDraftChanged('scope');config=2;await x.loadScope();await x.saveScope();assert.equal(sent[0].expectedConfigurationVersion,1);assert.deepEqual(Array.from(sent[0].sourceApIds),['ap-1']);assert.equal(x.document.getElementById('scope-reason').value,'my draft');await x.loadScope({resetScope:true});assert.equal(x.document.getElementById('scope-reason').value,'');}],
  ['A pending scope mutation freezes both forms and keeps later draft revisions',async()=>{let release;const x=app(async(url,options)=>options?.method==='POST'?await new Promise(r=>release=r):url==='/api/settings'?settings(2):scopeView());await x.loadScope();x.document.getElementById('scope-mode').value='SelectedApIds';x.document.getElementById('scope-ids').value='ap-1';x.document.getElementById('scope-reason').value='save';x.scopeDraftChanged('scope');const saving=x.saveScope();assert.equal(x.document.getElementById('scope-ids').disabled,true);assert.equal(x.document.getElementById('baseline-reason').disabled,true);x.document.getElementById('scope-reason').value='later revision';x.scopeDraftChanged('scope');release(scopeView(2));await saving;assert.equal(x.document.getElementById('scope-reason').value,'later revision');assert.equal(x.document.getElementById('scope-save').disabled,false);}],
  ['Baseline confirmation requires explicit count and reason and pins the reviewed run',async()=>{let config=1;const sent=[];const x=app(async(url,options)=>{if(options?.method==='POST'){sent.push(options.body);throw Object.assign(Error('conflict'),{status:409});}return scopeView(config);});await x.loadScope();await x.confirmBaseline();assert.equal(sent.length,0);x.document.getElementById('baseline-count').value='2';x.document.getElementById('baseline-reason').value='inventory compared';x.scopeDraftChanged('baseline');config=2;await x.loadScope();await x.confirmBaseline();assert.equal(sent[0].expectedConfigurationVersion,1);assert.equal(sent[0].runId,11);assert.equal(sent[0].comparedTargetCount,2);assert.equal(x.document.getElementById('baseline-reason').value,'inventory compared');}],
  ['Current baseline comes from the comparison run rather than an older confirmed profile',async()=>{let value=scopeView();value.profile.baselineStatus='Confirmed';value.comparisonRun.baselineStatus='Unverified';const x=app(async()=>value);await x.loadScope();assert.match(x.document.getElementById('scope-baseline').textContent,/잠정/);value=scopeView(2);value.comparisonRun.configurationVersion=1;await x.loadScope();assert.match(x.document.getElementById('scope-baseline').textContent,/새 설정 관측 대기/);assert.equal(x.document.getElementById('baseline-confirm').disabled,true);}],
  ['Changing scope settings preserves a pinned historical AP snapshot and general dirty settings',()=>{const x=app();x.state.view='settings';x.state.settings=settings(1);x.state.historical=true;x.state.selection=selection;x.state.settingsDirty=true;x.state.draftBaseVersion=1;x.document.getElementById('interval').value='17';x.applySettings(settings(2),{fill:true});assert.equal(x.state.selection.runId,17);assert.equal(x.state.historical,true);assert.equal(x.document.getElementById('interval').value,'17');assert.equal(x.state.draftBaseVersion,1);}],
  ['Displayed observation time stays KST even when the host timezone differs',()=>{const x=app();assert.match(x.dateText('2026-11-15T00:00:00Z'),/09:00:00/);assert.match(x.dateText('2026-11-15T00:00:00Z'),/KST/);assert.equal(x.dateText(null),'미확인');}],
  ['KST input crosses the UTC date boundary and rejects normalized invalid dates',()=>{assert.equal(ui.kstInputToUtc('2026-11-15T00:10'),'2026-11-14T15:10:00.000Z');assert.equal(ui.utcToKstInput('2026-11-14T15:10:00Z'),'2026-11-15T00:10');assert.equal(ui.kstInputToUtc(''),null);for(const input of ['2026-02-30T09:00','2026-11-15T24:00','2026-11-15T09:00Z'])assert.throws(()=>ui.kstInputToUtc(input));}],
  ['Dashboard and CSV keep the same positive long-open condition without pagination in CSV',()=>{const filters={minimumOpenElapsedSeconds:'86400',page:3};const page=ui.dashboardQuery(filters,selection),csv=ui.dashboardQuery(filters,selection,true);assert.equal(page.get('minimumOpenElapsedSeconds'),'86400');assert.equal(csv.get('minimumOpenElapsedSeconds'),'86400');assert.equal(csv.has('page'),false);assert.equal(csv.get('runId'),'17');assert.equal(ui.dashboardQuery({minimumOpenElapsedSeconds:''},selection).has('minimumOpenElapsedSeconds'),false);for(const value of [-1,0,'junk'])assert.throws(()=>ui.dashboardQuery({minimumOpenElapsedSeconds:value},selection));}],
  ['Incident reads send period, long-open filter, pinned scope and current page together',async()=>{let url;const x=app(async request=>{url=request;return incidents();});x.state.view='incidents';x.state.selection=selection;Object.assign(x.state.incident,{fromUtc:'2026-11-14T15:00:00.000Z',toUtc:'2026-11-15T14:59:00.000Z',minimumOpenElapsedSeconds:3600,page:1,search:' AP '});await x.loadIncidents();const q=new URL(url,'http://local').searchParams;assert.equal(q.get('fromUtc'),'2026-11-14T15:00:00.000Z');assert.equal(q.get('toUtc'),'2026-11-15T14:59:00.000Z');assert.equal(q.get('minimumOpenElapsedSeconds'),'3600');assert.equal(q.get('asOfRunId'),'17');assert.equal(q.get('scopeVersion'),'3');assert.equal(q.get('search'),'AP');}],
  ['Repetition counts come from the entire filtered result and distinguish first Down from transitions',async()=>{const x=app(async()=>incidents());x.state.view='incidents';x.state.selection=selection;await x.loadIncidents();assert.match(x.document.getElementById('incident-count').textContent,/전체 사건 40건/);assert.match(x.document.getElementById('incident-count').textContent,/고유 AP 12대/);assert.match(x.document.getElementById('incident-count').textContent,/Up → Down 33회/);assert.match(x.document.getElementById('incident-count').textContent,/최초 확인 Down 7건/);}],
  ['Unavailable aggregate values remain unknown instead of page-derived zero',async()=>{const x=app(async()=>incidents({upToDownTransitionCount:null,initialDownCount:null}));x.state.view='incidents';x.state.selection=selection;await x.loadIncidents();assert.match(x.document.getElementById('incident-count').textContent,/Up → Down —회/);assert.match(x.document.getElementById('incident-count').textContent,/최초 확인 Down —건/);}],
  ['Run period paging keeps the captured watermark and scope',async()=>{let url;const x=app(async request=>{url=request;return runs({totalRows:100});});x.state.view='runs';Object.assign(x.state.runs,{fromUtc:'2026-11-14T15:00:00.000Z',toUtc:'2026-11-15T15:00:00.000Z',scopeVersion:'3',throughRunId:31,page:2});await x.loadRuns();const q=new URL(url,'http://local').searchParams;assert.equal(q.get('fromUtc'),'2026-11-14T15:00:00.000Z');assert.equal(q.get('toUtc'),'2026-11-15T15:00:00.000Z');assert.equal(q.get('throughRunId'),'31');assert.equal(q.get('scopeVersion'),'3');assert.equal(q.get('page'),'2');}],
  ['Applying an incident period resets paging but preserves the historical selection',async()=>{let url;const x=app(async request=>{url=request;return incidents();});x.state.view='incidents';x.state.selection=selection;x.state.incident.page=3;x.document.getElementById('incident-from').value='2026-11-15T00:00';x.document.getElementById('incident-to').value='2026-11-15T23:59';await x.applyPeriod('incident');assert.equal(x.state.incident.page,1);assert.equal(x.state.selection.runId,17);assert.equal(new URL(url,'http://local').searchParams.get('fromUtc'),'2026-11-14T15:00:00.000Z');}],
  ['An inverted period preserves the previous applied query and does not fetch',async()=>{let requests=0;const x=app(async()=>{requests++;return runs();});x.state.view='runs';x.state.runs.fromUtc='2026-11-13T15:00:00.000Z';x.state.runs.throughRunId=31;x.state.runs.page=2;x.document.getElementById('run-from').value='2026-11-16T10:00';x.document.getElementById('run-to').value='2026-11-15T10:00';await x.applyPeriod('run');assert.equal(requests,0);assert.equal(x.state.runs.fromUtc,'2026-11-13T15:00:00.000Z');assert.equal(x.state.runs.throughRunId,31);assert.equal(x.state.runs.page,2);assert.match(x.document.getElementById('global-error').textContent,/시작/);}],
  ['Applying a new run period drops the old watermark while leaving the selected AP run pinned',async()=>{let url;const x=app(async request=>{url=request;return runs();});x.state.view='runs';x.state.selection=selection;x.state.runs.throughRunId=20;x.document.getElementById('run-from').value='2026-11-15T09:00';await x.applyPeriod('run');assert.equal(new URL(url,'http://local').searchParams.has('throughRunId'),false);assert.equal(x.state.selection.runId,17);assert.equal(x.state.runs.throughRunId,31);}],
  ['A late previous period response cannot replace the newly applied incident counts',async()=>{let release,calls=0;const x=app(async()=>++calls===1?await new Promise(r=>release=r):incidents({upToDownTransitionCount:2}));x.state.view='incidents';x.state.selection=selection;const old=x.loadIncidents();x.document.getElementById('incident-from').value='2026-11-15T09:00';await x.applyPeriod('incident');release(incidents({upToDownTransitionCount:99}));await old;assert.match(x.document.getElementById('incident-count').textContent,/Up → Down 2회/);assert.doesNotMatch(x.document.getElementById('incident-count').textContent,/99/);}],
  ['Power history interprets read-time filters as KST and rejects inverted ranges',()=>{const q=powerQuery({runId:17,scopeVersion:3},{from:'2026-11-15T00:00',to:'2026-11-16T00:00'},0);assert.equal(q.get('fromUtc'),'2026-11-14T15:00:00.000Z');assert.equal(q.get('throughRunId'),'17');assert.equal(q.get('throughCommitSequence'),'0');assert.throws(()=>powerQuery(null,{from:'2026-11-16T00:00',to:'2026-11-15T00:00'},null));}]
];
const results=[];for(const [name,run]of cases){try{await run();results.push({name,passed:true});}catch(error){results.push({name,passed:false,message:error.message});}}
console.log(JSON.stringify(results,null,2));if(results.some(x=>!x.passed))process.exitCode=1;
