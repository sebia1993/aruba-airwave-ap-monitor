"use strict";
import { kstInputToUtc, utcToKstInput } from "./ui-state.js";
export const inventoryFields = [
  ["Name","이름","device","name"],["IpAddress","IP","device","ipAddress"],["Model","모델","device","model"],["Firmware","펌웨어","device","firmware"],
  ["Site","사업장","location","site"],["Building","건물","location","building"],["Floor","층","location","floor"],["Zone","구역","location","zone"],["Detail","상세 위치","location","detail"],
  ["RawLocation","위치 원문","location","airWaveRawLocation"],["Folder","폴더","location","folder"],["Group","그룹","location","group"],
  ["ControllerId","컨트롤러 ID","controller","sourceId"],["ControllerName","컨트롤러 이름","controller","name"],["ControllerIp","컨트롤러 IP","controller","ipAddress"],
  ["SwitchName","스위치 이름",null,null],["SwitchIp","스위치 IP",null,null],["PortName","포트 이름","upstream","portName"],["ApInterface","AP 인터페이스","upstream","apInterface"],
  ["AssetTag","자산 번호",null,null],["Notes","대장 메모",null,null]
];
const kinds = { Incident:"장애·복구",BasicInformation:"기본정보",Connection:"연결 참조",HardwareReplacement:"장비 교체",FirmwareController:"펌웨어·컨트롤러",WorkAction:"작업·조치" };
const management = { Operating:"운영 · 감시 재편입",PlannedWork:"계획 작업 · 관측 유지",MonitoringExcluded:"감시 제외",Decommissioned:"철거 · 추적 종료" };
const sources = { AirWave:"AirWave",Demo:"가상 관측",Manual:"직접 입력",Import:"가져오기" };
const semantic = { BaselineRegistered:"기준 정보 등록",ValueChanged:"값 변경",ExplicitClear:"명시 해제",SourcePreferenceChanged:"표시 출처 변경",ReplacementDetected:"교체 감지 · 작업 확인 필요",ReplacementConfirmed:"교체 작업 확인",PointRegistered:"설치 지점 등록",PointCorrected:"설치 지점 정정",PlacementRegistered:"장비 배치 등록",PlacementEnded:"장비 배치 종료",TrackingPolicyRegistered:"관리 상태 등록",WorkRegistered:"작업 등록",WorkCorrected:"작업 정정",WorkCancelled:"작업 취소",Created:"장애 발생",Recovered:"복구 관측",TrackingEnded:"추적 종료" };
const workStatuses = { Planned:"예정",Completed:"실시",Cancelled:"취소" };
const causes = { Unknown:"미확인",Investigating:"확인 중",Confirmed:"확인됨" };
const guidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const nonEmptyGuid = x => guidPattern.test(x) && x !== "00000000-0000-0000-0000-000000000000";
const text = x => x == null || x === "" ? "미제공" : String(x);
const operationFields = {...Object.fromEntries(inventoryFields.map(([key,label])=>[key,label])),installationPoint:"설치 지점",manualPreferredFields:"수동 값 우선 표시",managementStatus:"관리 상태 / 감시 지시",incident:"장애 상태",work:"작업 내용",FollowUp:"사용자 관리 대상"};
export function operationValue(item, value) {
  if (value == null || value === "") return "미제공";
  if (item.field === "installationPoint" && (item.action === "PointRegistered" || item.action === "PointCorrected")) {
    try {
      const point=JSON.parse(value),get=(o,key)=>o?.[key]??o?.[key[0].toLowerCase()+key.slice(1)];
      if (point && typeof get(point,"Name")==="string" && Number.isSafeInteger(get(point,"Version"))) {
        const parts=[`${get(point,"Name")} · 버전 ${get(point,"Version")}`],key=get(point,"InventoryKey"),location=get(point,"Location");
        if(key)parts.push(`대장 키: ${key}`);
        for(const [name,label] of [["Site","사업장"],["Building","건물"],["Floor","층"],["Zone","구역"],["Detail","상세 위치"],["AirWaveRawLocation","위치 원문"],["Folder","폴더"],["Group","그룹"]]) {
          const field=get(location,name);if(field!=null&&field!=="")parts.push(`${label}: ${field}`);
        }
        return parts.join(" · ");
      }
    } catch { /* Older or unknown evidence remains available as literal text. */ }
  }
  if(item.field==="manualPreferredFields")return String(value).split(",").map(key=>operationFields[key.trim()]||key).join(", ");
  if(item.field==="managementStatus")return String(value).split("/").map(key=>({Operating:"운영",PlannedWork:"계획 작업",MonitoringExcluded:"감시 제외",Decommissioned:"철거",Observe:"관측 유지",TemporarilyExclude:"일시 제외",EndTracking:"추적 종료"})[key]||key).join(" / ");
  if(item.field==="FollowUp")return value==="true"?"지정":String(value);
  if(item.field==="incident")return ({Open:"미복구",Recovered:"복구 관측",TrackingEnded:"추적 종료"})[value]||String(value);
  return String(value);
}
export function inventoryValue(row, field) {
  const descriptor = inventoryFields.find(x => x[0] === field), automatic = descriptor?.[2] ? row?.[descriptor[2]]?.[descriptor[3]] ?? null : null;
  const snapshot = row?.manualInventory, manual = snapshot?.values?.[field] ?? null;
  const preferred = manual != null && (snapshot?.manualPreferredFields || []).includes(field);
  return {value:preferred ? manual : automatic,automatic,manual,preferred,different:manual != null && manual !== automatic,
    source:snapshot?.source,recordedAtUtc:snapshot?.recordedAtUtc,version:snapshot?.version};
}
export function previewSelection(preview, selected) {
  if (!preview || preview.errors?.length) throw new Error("미리보기 오류를 해결한 뒤 다시 검토하세요.");
  if (!selected.length) throw new Error("적용할 유효 행을 선택하세요.");
  if (new Set(selected).size !== selected.length) throw new Error("중복 행 선택은 적용할 수 없습니다.");
  const rows = new Map((preview.rows || []).map(row => [row.rowNumber,row]));
  if (selected.some(id => !rows.get(id)?.target || rows.get(id).errors?.length)) throw new Error("유효하지 않은 미리보기 행이 포함됐습니다.");
  return [...selected].sort((a,b) => a-b);
}
export function workTargets(rows) {
  if (!rows.length) throw new Error("작업 대상을 하나 이상 지정하세요.");
  const normalized = rows.map(row => {
    const target = Object.fromEntries(["apId","deviceGenerationId","incidentId","installationPointId"].map(key => [key,row[key]?.trim() || null]));
    if (Object.values(target).some(value => value && !nonEmptyGuid(value))) throw new Error("대상 식별자는 정확한 내부 ID여야 합니다.");
    if ((target.apId && !target.deviceGenerationId) || (!target.apId && (target.deviceGenerationId || target.incidentId))) throw new Error("AP 대상에는 해당 장비 세대가 필요합니다. 사건은 그 AP·세대에 연결하세요.");
    if (!target.apId && !target.installationPointId) throw new Error("AP 또는 설치 지점 대상을 지정하세요.");
    return target;
  });
  if (new Set(normalized.map(x=>JSON.stringify(x))).size !== normalized.length) throw new Error("중복 작업 대상을 제거하세요.");
  return normalized;
}
export function timelineQuery(filters, page = 1, through = null) {
  const query = new URLSearchParams({page:String(page),pageSize:"30"});
  for (const key of ["apId","deviceGenerationId","installationPointId","incidentId","asOfRunId","kind","fromUtc","toUtc"]) if (filters[key]) query.set(key,filters[key]);
  if (through != null && through > 0) query.set("throughEventId",String(through));
  return query;
}
export function commandForDraft(draft, payload, createId = () => crypto.randomUUID()) {
  const fingerprint = JSON.stringify(payload);
  if (draft.commandPayload !== fingerprint) { draft.commandPayload = fingerprint; draft.commandId = createId(); }
  return draft.commandId;
}

export function createOperationsController({api,onMessage,navigate,dateText,isCurrentView,onReturnAp}) {
  let root = document.getElementById("operations-content");
  const drafts = new Map(), pendingControls = new Map();
  const incidentMemos = new Map();
  let currentMemo = null;
  const historyTabs = [["timeline","작업·변경 이력"],["work","작업·메모"]];
  const ledgerTabs = [["registration","AP 대장"],["points","설치 지점"],["import","CSV 가져오기"]];
  const targetFields = ["apId","deviceGenerationId","incidentId","installationPointId"];
  function currentScreen(tab){return tab==="work"||tab==="timeline"?"incidents":"operations";}
  function targetOf(context){return Object.fromEntries(targetFields.filter(key=>context?.[key]).map(key=>[key,context[key]]));}
  function scopeOf(context){return {...targetOf(context),asOfRunId:context?.asOfRunId,kind:context?.kind};}
  const state = {tab:"registration",context:null,epoch:0,busy:false,version:null,registration:null,point:null,pointDraftOpen:false,work:null,preview:null,previewStale:false,selected:new Set(),file:null,
    timeline:{filters:{},page:1,through:null},points:{search:"",page:1},registry:{search:"",page:1,runId:null,scopeVersion:null}};
  const e = (tag,cls,value) => { const n=document.createElement(tag); if(cls)n.className=cls;if(value!=null)n.textContent=String(value);return n; };
  const b = (label,action,cls="") => {const n=e("button",cls,label);n.type="button";n.addEventListener("click",()=>void action());return n;};
  const note = value => e("p","footnote",value);
  const section = title => {const n=e("section","panel padded");n.append(e("h2","",title));return n;};
  const badge = (value,cls="")=>e("span",`badge ${cls}`,value);
  const field = (form,name,label,{type="text",value="",required=false,options=null,max=4096}={}) => {
    const wrap=e("label","operation-field"),caption=e("span","",type==="datetime-local"?`${label} (KST)`:label); let input;
    if(options) {input=e("select");for(const [key,title] of options){const option=e("option","",title);option.value=key;input.append(option);}}
    else input=e(type==="textarea"?"textarea":"input");
    if(!options&&type!=="textarea")input.type=type;
    input.name=name;input.id=`op-${name}-${crypto.randomUUID()}`;input.value=value??"";input.required=required;
    if(type!=="checkbox") input.maxLength=max;
    if(type==="textarea")input.rows=3;
    if(type==="checkbox"){input.checked=!!value;wrap.classList.add("check");wrap.append(input,caption);}else wrap.append(caption,input);
    form.append(wrap);return input;
  };
  const read = form => Object.fromEntries([...form.querySelectorAll("[name]")].map(n=>[n.name,n.type==="checkbox"?n.checked:n.value]));
  const restore = (form,values) => {for(const n of form.querySelectorAll("[name]")){if(Object.hasOwn(values,n.name)){if(n.type==="checkbox")n.checked=values[n.name];else n.value=values[n.name];}}};
  const tracked = (key,base,build) => {
    base={...base,expectedRestoreEpoch:state.version.restoreEpoch};
    let draft=drafts.get(key);if(!draft){draft={base,values:null,dirty:false};drafts.set(key,draft);}else if(!draft.dirty){draft.base=base;draft.values=null;}
    const form=e("form","operation-form");form.dataset.draftKey=key;build(form,draft);
    if(draft.values)restore(form,draft.values);
    if(state.busy)freezeControls(form.querySelectorAll("input,select,textarea,button"));
    const changed=()=>{draft.dirty=true;draft.values=read(form);};form.addEventListener("input",changed);form.addEventListener("change",changed);
    return {form,draft};
  };
  const epoch = () => ++state.epoch;
  const valid = ticket => ticket===state.epoch && isCurrentView(currentScreen(state.tab));
  const showMessage = (message,error=false)=>onMessage(message,error);
  async function mutate(form,draft,payload,path,after) {
    if(state.busy)return;const ticket=state.epoch;state.busy=true;setBusy();
    try {
      const versionedPayload={...payload,expectedRestoreEpoch:draft.base.expectedRestoreEpoch};
      const commandId=commandForDraft(draft,versionedPayload);
      await api(path,{method:"POST",body:{...versionedPayload,commandId},timeout:30000});
      drafts.delete(form.dataset.draftKey);showMessage("등록 내용을 저장했습니다. 관측 상태는 자동 수집 기록을 유지합니다.");if(valid(ticket))await after(()=>valid(ticket));
    } catch(error) {showMessage(error.status===409?`${error.message} 입력은 유지합니다.`:error.message,true);}
    finally{state.busy=false;setBusy();}
  }
  function freezeControls(controls){for(const input of controls){if(!pendingControls.has(input))pendingControls.set(input,input.disabled);input.disabled=true;}}
  function setBusy(){
    if(state.busy){const controls=[...root.querySelectorAll("form")].filter(form=>form.dataset.draftKey).flatMap(form=>[...form.querySelectorAll("input,select,textarea,button")]);controls.push(...[...root.querySelectorAll("[name]")].filter(input=>input.name.startsWith("row-")));freezeControls(controls);}
    else{for(const [input,disabled] of pendingControls)input.disabled=disabled;pendingControls.clear();}
    for(const button of root.querySelectorAll("button[data-mutation]"))button.disabled=state.busy||button.dataset.permanentlyDisabled==="true";
  }
  function submit(form,draft,label,path,payload,after){const save=e("button","primary",label);save.type="submit";save.dataset.mutation="true";form.append(save);form.addEventListener("submit",event=>{event.preventDefault();try{void mutate(form,draft,payload(read(form),draft.base),path,after);}catch(error){showMessage(error.message,true);}});}
  function resetDraft(key,refresh){if(state.busy){showMessage("저장 응답을 기다린 뒤 다시 읽어주세요.",true);return;}const draft=drafts.get(key);if(draft?.dirty){const confirm=e("div","notice warning");confirm.append(e("span","","저장하지 않은 입력이 있습니다. 서버 값으로 다시 채우면 이 입력이 사라집니다."),b("입력 유지",()=>confirm.remove()),b("서버 값으로 다시 채우기",()=>{drafts.delete(key);confirm.remove();void refresh();}));root.prepend(confirm);}else{drafts.delete(key);void refresh();}}
  function tabs(){
    const nav=e("div","tabs operations-tabs");
    const choices=currentScreen(state.tab)==="incidents"?historyTabs:ledgerTabs;
    for(const [key,title] of choices){
      const tab=b(title,()=>show(key),`tab${state.tab===key?" active":""}`);
      tab.setAttribute("aria-pressed",String(state.tab===key));nav.append(tab);
    }
    return nav;
  }
  function layout(){
    root.replaceChildren(tabs());
    const context=state.context;
    if(context?.returnTo||context?.selection&&onReturnAp)
      root.prepend(b("선택한 상세로 돌아가기",()=>context.returnTo?context.returnTo():onReturnAp(context)));
    const area=e("div","operations-area");root.append(area);setBusy();return area;
  }
  async function show(tab=state.tab,context=undefined){
    if(context!==undefined){
      state.context=context;
      state.timeline={filters:scopeOf(context),page:1,through:null};
      state.work=null;
      if(tab==="registration"&&!context) state.registry={search:"",page:1,runId:null,scopeVersion:null};
    }
    state.tab=tab;
    const screen=currentScreen(tab);
    navigate(screen);
    root=document.getElementById(screen==="incidents"?"incident-operations-content":"operations-content");
    const ticket=epoch(),area=layout();
    try{
      const version=await api("/api/operations/version");if(!valid(ticket))return;
      if(!Number.isSafeInteger(version.restoreEpoch)||version.restoreEpoch<0)throw new Error("등록 기준을 확인하지 못했습니다. 화면을 다시 읽어주세요.");
      state.version=version;
      if(tab==="registration")await registration(area,ticket);
      else if(tab==="points")await points(area,ticket);
      else if(tab==="work")await work(area,ticket);
      else if(tab==="import")renderImport(area);
      else await timeline(area,ticket);
    }catch(error){if(valid(ticket))area.append(e("div","notice error",error.message));}
    setBusy();
  }
  function utc(value){return kstInputToUtc(value);}
  function local(value){return utcToKstInput(value);}
  function pagination(area,total,page,change){const pages=Math.max(1,Math.ceil(total/30)),bar=e("div","pager");bar.append(e("span","",`${total}건 · ${page}/${pages}`));const actions=e("div","actions"),prev=b("이전",()=>change(page-1)),next=b("다음",()=>change(page+1));prev.disabled=page<=1;next.disabled=page>=pages;actions.append(prev,next);bar.append(actions);area.append(bar);}
  async function registry(area,ticket){
    const view=state.registry, form=e("form","toolbar");
    const search=field(form,"registrySearch","AP 이름·IP·위치 검색",{value:view.search});
    const submit=e("button","","검색");submit.type="submit";form.append(submit);
    form.addEventListener("submit",event=>{
      event.preventDefault();
      state.registry={search:search.value.trim(),page:1,runId:null,scopeVersion:null};
      void show("registration");
    });
    area.append(form);
    const query=new URLSearchParams({filter:"All",page:String(view.page),pageSize:"30"});
    if(view.search)query.set("search",view.search);
    if(view.runId!=null){query.set("runId",view.runId);query.set("scopeVersion",view.scopeVersion);}
    let data;
    try{data=await api(`/api/dashboard?${query}`);}
    catch(error){if(error.code!=="no_final_run")throw error;if(valid(ticket))area.append(note("첫 AP 상태 수집이 확정되면 이곳에서 장비를 선택할 수 있습니다."));return;}
    if(!valid(ticket))return;
    view.runId=data.runId;view.scopeVersion=data.scopeVersion;
    const list=section("AP 대장 목록");
    for(const row of data.rows){
      const name=inventoryValue(row,"Name").value||`AP ${row.sourceApId}`;
      const item=e("div","operation-item");
      item.append(b(name,()=>show("registration",{apId:row.apId,deviceGenerationId:row.deviceGenerationId}),"text-button"),
        note(`원천 ID ${row.sourceApId} · ${text(inventoryValue(row,"IpAddress").value)} · ${text(inventoryValue(row,"Model").value)}`));
      list.append(item);
    }
    if(!data.rows.length)list.append(note("조건에 맞는 AP가 없습니다."));
    area.append(list);pagination(area,data.totalRows,view.page,page=>{view.page=page;void show("registration");});
  }
  function workApPicker(ticket,add){
    const box=e("details","work-ap-picker"),toolbar=e("div","toolbar");
    box.append(e("summary","","목록에서 작업 대상 AP 선택"),note("현재 감시 범위의 AP를 이름·IP로 검색해 여러 대를 선택할 수 있습니다."));
    const wrap=e("label","operation-field"),search=e("input");
    search.type="search";wrap.append(e("span","","AP 이름·IP·위치"),search);
    const results=e("div","work-ap-results"),selection=new Map();
    let request=0,criteria="",run=null,scope=null;
    const apply=b("",()=>{
      if(state.busy||!valid(ticket))return;
      add([...selection.values()]);selection.clear();
      for(const input of results.querySelectorAll("input"))input.checked=false;
      updateCount();
    });
    function updateCount(){apply.textContent=`선택 AP 추가 (${selection.size}대)`;apply.disabled=state.busy||!selection.size;}
    async function load(page,fresh=false){
      if(state.busy)return;
      const revision=++request;
      if(fresh){criteria=search.value.trim();run=null;scope=null;}
      const q=new URLSearchParams({filter:"All",page,pageSize:30});
      if(criteria)q.set("search",criteria);
      if(run!=null){q.set("runId",run);q.set("scopeVersion",scope);}
      results.replaceChildren(note("AP 목록 조회 중"));
      try{
        const data=await api(`/api/dashboard?${q}`);
        if(!valid(ticket)||revision!==request)return;
        run=data.runId;scope=data.scopeVersion;results.replaceChildren();
        for(const row of data.rows){
          const target={apId:row.apId,deviceGenerationId:row.deviceGenerationId},key=row.deviceGenerationId;
          const label=e("label","check"),check=e("input");
          check.type="checkbox";check.dataset.pickAp=row.apId;check.checked=selection.has(key);
          check.addEventListener("change",()=>{
            if(state.busy)return;
            if(check.checked)selection.set(key,target);else selection.delete(key);
            updateCount();
          });
          label.append(check,e("span","",`${inventoryValue(row,"Name").value||row.sourceApId} · ${text(inventoryValue(row,"IpAddress").value)} · 원천 ID ${row.sourceApId}`));results.append(label);
        }
        if(!data.rows.length)results.append(note("조건에 맞는 AP가 없습니다."));
        pagination(results,data.totalRows,page,load);
        if(state.busy)freezeControls(results.querySelectorAll("input,button"));
      }catch(error){if(valid(ticket)&&revision===request)results.replaceChildren(note(error.message));}
    }
    search.addEventListener("keydown",event=>{if(event.key==="Enter"){event.preventDefault();void load(1,true);}});
    toolbar.append(wrap,b("AP 검색",()=>load(1,true)));updateCount();box.append(toolbar,results,apply);
    return box;
  }
  async function registration(area,ticket){
    if(!state.context?.apId){await registry(area,ticket);return;}
    area.append(b("AP 대장 목록",()=>{state.context=null;return show("registration");}));
    const target={...state.context},data=await api(`/api/operations/aps/${encodeURIComponent(target.apId)}/registration`);if(!valid(ticket))return;state.registration=data;const current=data.registration;
    area.append(e("div","notice",current.pendingNextCollection?"현재 등록 정보 · 다음 AP 수집 적용 대기":"현재 등록 정보 · 저장된 관측과 적용 상태 확인"),note(`현재 장비 세대 ${current.currentDeviceGenerationId} · 자동 정보 회차 ${data.automaticRunId} · ${dateText(data.automaticObservedAtUtc)}. 과거 회차·사건 정보는 변경하지 않습니다.`));
    if(target.deviceGenerationId&&target.deviceGenerationId!==current.currentDeviceGenerationId)area.append(e("div","notice warning","선택했던 과거 장비와 현재 장비 세대가 다릅니다. 아래 등록은 명시된 현재 세대에만 적용됩니다. 과거 사건 작업은 작업·메모에서 원래 세대를 유지합니다."));
    const grid=e("div","operations-grid");area.append(grid);const inventory=section("수동 대장과 표시 출처"),key=`inventory:${target.apId}`;
    const built=tracked(key,data,(form,draft)=>{const base=draft.base.registration,values=base.inventory?.values||{},preferred=base.inventory?.manualPreferredFields||[],table=e("div","inventory-editor");form.append(table);
      for(const [name,label] of inventoryFields){const row=e("div","inventory-entry"),raw=inventoryValue({...draft.base.automatic,manualInventory:base.inventory},name);const evidenceKey={Name:"name",IpAddress:"lan_ip",Model:"model",Firmware:"firmware",Site:"site",Building:"building",Floor:"floor",Zone:"zone",Detail:"detail",RawLocation:"syslocation",Folder:"folder",Group:"group",ControllerId:"controller_id",ControllerName:"controller_name",ControllerIp:"controller_ip",PortName:"upstream_port_name",ApInterface:"ap_interface"}[name],evidence=draft.base.automaticFields?.[evidenceKey];row.append(e("strong","",label),note(`자동: ${text(raw.automatic)} · ${sources[evidence?.source]||"근거 미제공"} · 수집 ${dateText(evidence?.collectedAtUtc)} · 원천 ${dateText(evidence?.sourceAtUtc)}`));field(row,`value-${name}`,"등록값",{type:name==="Notes"?"textarea":"text",value:values[name]||""});field(row,`prefer-${name}`,"수동 우선 표시",{type:"checkbox",value:preferred.includes(name)});field(row,`clear-${name}`,"등록값 명시 해제",{type:"checkbox"});table.append(row);}
      field(form,"clearReason","명시 해제 사유");form.append(note("빈 입력은 기존 등록값을 지우지 않습니다. 해제 선택과 사유로만 삭제하며, 수동값은 자동 관측을 변경하지 않습니다."));
      submit(form,draft,"대장 등록 저장","/api/operations/inventory",(v,base)=>{const setValues={},clearFields=[],manualPreferredFields=[];for(const [name]of inventoryFields){if(v[`clear-${name}`])clearFields.push(name);else if(v[`value-${name}`]?.trim())setValues[name]=v[`value-${name}`].trim();if(v[`prefer-${name}`]&&!v[`clear-${name}`])manualPreferredFields.push(name);}return {apId:target.apId,expectedCurrentGenerationId:base.registration.currentDeviceGenerationId,expectedVersion:base.registration.inventory?.version||0,setValues,clearFields,manualPreferredFields,clearReason:v.clearReason||null};},()=>show("registration"));
    });inventory.append(note(`편집 기준 장비 세대 ${built.draft.base.registration.currentDeviceGenerationId} · 등록 출처 ${sources[current.inventory?.source]||"미등록"} · 버전 ${current.inventory?.version||0} · ${dateText(current.inventory?.recordedAtUtc)}`),built.form,b("서버 등록 다시 읽기",()=>resetDraft(key,()=>show("registration"))));grid.append(inventory);
    const followUp=section("사용자 관리 대상"),followUpKey=`follow-up:${target.apId}`;
    const marked=current.inventory?.values?.FollowUp==="true";
    followUp.append(note(marked?"현재 관리 대상으로 지정됨":"현재 관리 대상 미지정"),note("장비 세대별 수동 지정입니다. 감시 제외·철거·복구 처리와 무관하며, 다음 AP 수집부터 현황 목록에 반영됩니다."));
    if(target.deviceGenerationId&&target.deviceGenerationId!==current.currentDeviceGenerationId) {
      followUp.append(note("과거 장비 세대에서는 관리 대상을 변경할 수 없습니다. 현재 AP 현황에서 다시 선택하세요."));
    } else {
      const edit=tracked(followUpKey,data,(form,draft)=>{
        const saved=draft.base.registration.inventory?.values?.FollowUp==="true";
        field(form,"marked","관리 대상으로 지정",{type:"checkbox",value:saved});
        field(form,"reason","해제 사유 (해제 시 필수)",{max:4096});
        submit(form,draft,"관리 대상 저장","/api/operations/inventory",(v,base)=>{
          const wasMarked=base.registration.inventory?.values?.FollowUp==="true";
          if(v.marked===wasMarked)throw Error("변경할 관리 대상 상태를 선택하세요.");
          if(!v.marked&&!v.reason.trim())throw Error("관리 대상 해제 사유를 입력하세요.");
          return {apId:target.apId,expectedCurrentGenerationId:base.registration.currentDeviceGenerationId,
            expectedVersion:base.registration.inventory?.version||0,setValues:v.marked?{FollowUp:"true"}:{},
            clearFields:v.marked?[]:["FollowUp"],clearReason:v.marked?null:v.reason.trim()};
        },()=>show("registration"));
      });
      followUp.append(edit.form,b("현재 관리 대상 다시 읽기",()=>resetDraft(followUpKey,()=>show("registration"))));
    }
    grid.prepend(followUp);
    const policy=section("관리 상태와 감시 의도"),trackingKey=`tracking:${target.apId}`;
    const tracking=tracked(trackingKey,data,(form,draft)=>{const saved=draft.base.registration.trackingPolicy;field(form,"status","관리 상태",{options:Object.entries(management),value:saved?.status||"Operating"});field(form,"directive","감시 제외 방식",{options:[["TemporarilyExclude","일시 제외 · 기존 사건 유지"],["EndTracking","명시 추적 종료 · 재편입 시 새 구간"]],value:saved?.directive==="EndTracking"?"EndTracking":"TemporarilyExclude"});field(form,"reason","변경 사유",{type:"textarea",required:true});form.append(note("계획 작업은 Down 관측을 유지합니다. 제외·철거는 복구가 아니며 다음 수집부터 반영합니다. 운영으로 저장해야 다시 감시합니다."));submit(form,draft,"관리 상태 저장","/api/operations/tracking",(v,base)=>({apId:target.apId,expectedCurrentGenerationId:base.registration.currentDeviceGenerationId,expectedVersion:base.registration.trackingPolicy?.version||0,status:v.status,reason:v.reason,directive:v.status==="MonitoringExcluded"?v.directive:v.status==="Decommissioned"?"EndTracking":"Observe"}),()=>show("registration"));});policy.append(note(`현재 ${management[current.trackingPolicy?.status]||"미등록"} · ${text(current.trackingPolicy?.reason)} · ${dateText(current.trackingPolicy?.recordedAtUtc)}`),tracking.form,b("현재 정책 다시 읽기",()=>resetDraft(trackingKey,()=>show("registration"))));grid.append(policy);
    const placement=section("설치 지점 배치"),placementKey=`placement:${target.apId}`;
    const place=tracked(placementKey,data,(form,draft)=>{const saved=draft.base.registration.placement;field(form,"pointId","설치 지점 ID (해제하려면 비움)",{value:saved?.installationPointId||""});field(form,"pointVersion","지점 등록 버전",{type:"number",value:saved?.pointVersion||""});field(form,"actual","실제 작업 시각 (선택)",{type:"datetime-local"});field(form,"workId","연결 작업 ID (선택)");field(form,"reason","배치 변경 사유",{required:true,type:"textarea"});form.append(note("설치 지점에서 ID와 현재 버전을 확인하세요. 현재 배치를 종료하고 새 관계를 기록하며, 사건을 합치지 않습니다."));submit(form,draft,"배치 변경 저장","/api/operations/placement",(v,base)=>({apId:target.apId,expectedCurrentGenerationId:base.registration.currentDeviceGenerationId,expectedVersion:base.placementVersion,installationPointId:v.pointId.trim()||null,expectedPointVersion:v.pointId.trim()?Number(v.pointVersion):null,reason:v.reason,actualWorkAtUtc:utc(v.actual),workId:v.workId.trim()||null}),()=>show("registration"));});placement.append(note(`현재 지점 ${current.placement?.installationPointId||"미지정"} · 배치 버전 ${data.placementVersion}`),place.form,b("설치 지점 목록",()=>show("points")),b("배치 다시 읽기",()=>resetDraft(placementKey,()=>show("registration"))));grid.append(placement);
  }
  async function points(area,ticket){const search=e("form","toolbar"),input=field(search,"pointSearch","지점 검색",{value:state.points.search}),go=e("button","","검색");go.type="submit";search.append(go,b("새 지점 등록",()=>{state.point=null;state.pointDraftOpen=true;showPointForm(area,null);}));search.addEventListener("submit",event=>{event.preventDefault();state.points.search=input.value;state.points.page=1;void show("points");});area.append(search);
    const page=await api(`/api/operations/points?${new URLSearchParams({search:state.points.search,page:state.points.page,pageSize:30})}`);if(!valid(ticket))return;const list=section("등록된 설치 지점");if(!page.rows.length)list.append(note("등록된 지점이 없습니다. 이름이나 위치 문자열에서 자동 생성하지 않습니다."));
    for(const point of page.rows){const item=e("div","operation-item");item.append(b(point.name,()=>loadPoint(point.installationPointId),"text-button"),note(`ID ${point.installationPointId} · 버전 ${point.version} · 대장 키 ${text(point.inventoryKey)}`));list.append(item);}area.append(list);pagination(area,page.totalRows,state.points.page,page=>{state.points.page=page;void show("points");});if(state.point){const selected=state.point.current.installationPointId;const snapshot=await api(`/api/operations/points/${encodeURIComponent(selected)}`);if(!valid(ticket))return;state.point=snapshot;showPointForm(area,snapshot);}else if(state.pointDraftOpen)showPointForm(area,null);
  }
  async function loadPoint(id){state.pointDraftOpen=false;state.point={current:{installationPointId:id}};await show("points");}
  async function reloadNewPoint(){state.point=null;state.pointDraftOpen=true;await show("points");}
  function showPointForm(area,snapshot){area.querySelector(".point-editor")?.remove();const point=snapshot?.current||{installationPointId:crypto.randomUUID(),version:0,location:{}},panel=section(snapshot?"설치 지점과 수정 이력":"설치 지점 등록");panel.classList.add("point-editor");const key=`point:${snapshot?point.installationPointId:"new"}`;
    const built=tracked(key,point,(form,draft)=>{const p=draft.base;field(form,"name","지점 이름",{value:p.name||"",required:true});field(form,"inventoryKey","기존 대장 고유 키 (선택)",{value:p.inventoryKey||""});for(const [name,label]of[["site","사업장"],["building","건물"],["floor","층"],["zone","구역"],["detail","상세 위치"]])field(form,name,label,{value:p.location?.[name]||""});field(form,"reason","정정 사유 (수정 시 필수)",{required:p.version>0});submit(form,draft,p.version?"지점 정정 저장":"지점 등록","/api/operations/points",(v,base)=>({installationPointId:base.installationPointId,expectedVersion:base.version,name:v.name,inventoryKey:v.inventoryKey||null,location:{site:v.site||null,building:v.building||null,floor:v.floor||null,zone:v.zone||null,detail:v.detail||null,installationPointId:base.installationPointId},correctionReason:v.reason||null}),async stillCurrent=>{if(!stillCurrent())return;state.point={current:{installationPointId:draft.base.installationPointId}};await show("points");});});panel.append(note(`지점 ID ${built.draft.base.installationPointId} · 버전 ${built.draft.base.version}`),built.form,b(snapshot?"지점 다시 읽기":"새 지점 입력 다시 작성",()=>resetDraft(key,()=>snapshot?loadPoint(point.installationPointId):reloadNewPoint())));
    if(snapshot){panel.append(b("이 지점 운영 이력",()=>show("timeline",{installationPointId:point.installationPointId})),b("이 지점 작업 등록",()=>{state.work=null;void show("work",{installationPointId:point.installationPointId});}));const history=e("details");history.append(e("summary","","지점 수정·장비 배치 이력"));for(const v of snapshot.versions)history.append(note(`버전 ${v.version} · ${dateText(v.recordedAtUtc)} · ${v.name} · ${text(v.correctionReason)}`));for(const p of snapshot.placements)history.append(note(`${p.recordedEndAtUtc?"종료된 등록":snapshot.currentDeviceGenerationIds==null?"현재 세대 적용 미확인":snapshot.currentDeviceGenerationIds.includes(p.deviceGenerationId)?"현재 장비 등록":"구 장비 세대 등록 이력 · 현재 적용 아님"} · AP ${p.apId} · 세대 ${p.deviceGenerationId} · 등록 ${dateText(p.recordedStartAtUtc)} → ${dateText(p.recordedEndAtUtc)} · 실제 작업 ${dateText(p.actualStartAtUtc)} → ${dateText(p.actualEndAtUtc)} · ${text(p.endReason)}`));panel.append(history);}area.append(panel);panel.scrollIntoView({block:"nearest"});
  }
  function writeWork(context){state.work=null;return show("work",context);}
  async function work(area,ticket){const lookup=e("form","toolbar"),id=field(lookup,"workLookup","작업 ID로 조회"),open=e("button","","조회");open.type="submit";lookup.append(open,b("새 작업·메모",()=>{state.work=null;void show("work");}));lookup.addEventListener("submit",event=>{event.preventDefault();void loadWork(id.value.trim());});area.append(lookup);
    if(state.work){const selected=state.work.current.workId;const snapshot=await api(`/api/operations/work/${selected}`);if(!valid(ticket))return;state.work=snapshot;}
    const snapshot=state.work,record=snapshot?.current||{workId:crypto.randomUUID(),version:0,content:{targets:state.context?[targetOf(state.context)]:[]}},key=`work:${snapshot?record.workId:"new:"+JSON.stringify(targetOf(state.context))}`,panel=section(snapshot?"작업·메모 정정 및 취소":"작업·메모 등록");
    const built=tracked(key,record,(form,draft)=>{const w=draft.base,c=w.content||{};
      field(form,"workType","작업 종류",{value:c.workType||"점검",required:true});field(form,"status","작업 상태",{options:Object.entries(workStatuses),value:c.status||"Planned"});
      field(form,"plannedStart","예정 시작 (선택)",{type:"datetime-local",value:local(c.plannedStartAtUtc)});field(form,"plannedEnd","예정 종료 (선택)",{type:"datetime-local",value:local(c.plannedEndAtUtc)});field(form,"actual","실제 작업 시각 (선택)",{type:"datetime-local",value:local(c.actualWorkAtUtc)});field(form,"worker","작업자 (선택)",{value:c.worker||""});
      field(form,"content","작업 내용",{type:"textarea",value:c.content||"",required:true});field(form,"result","결과",{type:"textarea",value:c.result||""});field(form,"causeStatus","원인 확인 상태",{options:Object.entries(causes),value:c.causeStatus||"Unknown"});field(form,"causeDetail","원인 근거·확인 내용",{type:"textarea",value:c.causeDetail||""});field(form,"notes","메모",{type:"textarea",value:c.notes||""});
      const targets=e("div","work-targets");form.append(e("h3","","연결 대상 · 정확한 장비/사건/지점"),note("AP 상세에서 시작하면 당시 장비 세대가 유지됩니다. 다른 대상을 추가할 때 내부 ID를 사용하세요. 사건 입력은 선택이며, 이름/IP로 대상을 추정하지 않습니다."),targets);
      let count=0;const addTarget=(target={},storedIndex=null)=>{const index=storedIndex??count;count=Math.max(count,index+1);const row=e("fieldset","work-target");row.dataset.targetIndex=String(index);row.append(e("legend","",`대상 ${index+1}`));for(const [name,label]of[["apId","AP ID"],["deviceGenerationId","장비 세대 ID"],["incidentId","사건 ID (선택)"],["installationPointId","설치 지점 ID (선택)"]])field(row,`target${index}-${name}`,label,{value:target[name]||""});row.append(b("이 대상 제거",()=>{row.remove();draft.values=read(form);draft.dirty=true;}));targets.append(row);};
      const storedTargets=draft.values?Object.keys(draft.values).filter(k=>/^target\d+-apId$/.test(k)).map(k=>{const prefix=k.split('-')[0];return {...Object.fromEntries(["apId","deviceGenerationId","incidentId","installationPointId"].map(name=>[name,draft.values[`${prefix}-${name}`]])),_index:Number(prefix.slice(6))};}):c.targets||[];
      (storedTargets.length?storedTargets:[{}]).forEach(target=>addTarget(target,target._index??null));
      form.append(workApPicker(ticket,picked=>{
        const present=new Set();
        for(const row of targets.querySelectorAll("fieldset")){
          const fields=read(row),prefix=`target${row.dataset.targetIndex}-`;
          if(targetFields.every(k=>!fields[prefix+k]?.trim()))row.remove();
          else present.add(`${fields[prefix+"apId"]}/${fields[prefix+"deviceGenerationId"]}`);
        }
        for(const target of picked)if(!present.has(`${target.apId}/${target.deviceGenerationId}`))addTarget(target);
        draft.dirty=true;draft.values=read(form);
      }));
      form.append(b("대상 추가",()=>{addTarget();draft.values=read(form);draft.dirty=true;}));field(form,"correctionReason","정정·취소 사유 (수정 시 필수)",{required:w.version>0});
      form.append(note("작업 상태와 자동 장애 상태는 별개입니다. 취소·정정은 이전 내용과 연결 대상을 이력으로 남기며 AP 복구를 만들지 않습니다."));
      submit(form,draft,w.version?"작업 버전 저장":"작업 기록 저장","/api/operations/work",(v,base)=>{const selected=[...targets.querySelectorAll("fieldset")].map(row=>{const n=row.dataset.targetIndex;return Object.fromEntries(["apId","deviceGenerationId","incidentId","installationPointId"].map(key=>[key,v[`target${n}-${key}`]]));});return {workId:base.workId,expectedVersion:base.version,content:{workType:v.workType,status:v.status,content:v.content,result:v.result||null,notes:v.notes||null,causeStatus:v.causeStatus,causeDetail:v.causeDetail||null,plannedStartAtUtc:utc(v.plannedStart),plannedEndAtUtc:utc(v.plannedEnd),actualWorkAtUtc:utc(v.actual),worker:v.worker||null,targets:workTargets(selected)},correctionReason:v.correctionReason||null};},async stillCurrent=>{if(!stillCurrent())return;state.work={current:{workId:draft.base.workId}};await show("work");});});
    panel.append(note(`작업 ID ${built.draft.base.workId} · 버전 ${built.draft.base.version}`),built.form,b("저장된 작업 다시 읽기",()=>resetDraft(key,()=>snapshot?loadWork(record.workId):show("work"))));area.append(panel);
    if(snapshot){const versions=section("이전 작업·메모 버전");for(const v of snapshot.versions){const item=e("details","work-version");item.append(e("summary","",`버전 ${v.version} · ${workStatuses[v.content.status]||v.content.status} · ${dateText(v.recordedAtUtc)}`),note(`정정 사유: ${text(v.correctionReason)}`),e("p","pre-wrap",v.content.content),note(`결과: ${text(v.content.result)}\n메모: ${text(v.content.notes)}`),note(`예정 ${dateText(v.content.plannedStartAtUtc)} → ${dateText(v.content.plannedEndAtUtc)} · 실제 ${dateText(v.content.actualWorkAtUtc)} · 작업자 ${text(v.content.worker)}`),note(`원인 ${causes[v.content.causeStatus]} · ${text(v.content.causeDetail)}`));for(const t of v.content.targets)item.append(note(`AP ${text(t.apId)} · 세대 ${text(t.deviceGenerationId)} · 사건 ${text(t.incidentId)} · 지점 ${text(t.installationPointId)}`));versions.append(item);}area.append(versions);}
  }
  async function loadWork(id){if(!nonEmptyGuid(id))return showMessage("정확한 작업 ID를 입력하세요.",true);state.work={current:{workId:id}};await show("work");}
  async function timeline(area,ticket){
    const filters=state.timeline.filters,form=e("form","toolbar operation-filters");
    for(const [key,label]of [["apId","AP ID"],["deviceGenerationId","장비 세대 ID"],["installationPointId","설치 지점 ID"],["incidentId","사건 ID"]]){
      const input=field(form,key,label,{value:filters[key]||""});
      if(state.context?.[key])input.readOnly=true;
    }
    field(form,"kind","이력 종류",{options:[["","전체 종류"],...Object.entries(kinds)],value:filters.kind||""});
    field(form,"from","기록 시각 시작",{type:"datetime-local",value:local(filters.fromUtc)});
    field(form,"to","기록 시각 종료",{type:"datetime-local",value:local(filters.toUtc)});
    const go=e("button","","조회 조건 적용");go.type="submit";form.append(go);
    form.addEventListener("submit",event=>{
      event.preventDefault();
      try{
        const values=read(form),targets={};
        for(const key of targetFields){
          const id=state.context?.[key]||values[key].trim();
          if(id&&!nonEmptyGuid(id))throw new Error("대상 ID를 확인하세요.");
          if(id)targets[key]=id;
        }
        state.timeline={
          filters:{...targets,asOfRunId:filters.asOfRunId,kind:values.kind,fromUtc:utc(values.from),toUtc:utc(values.to)},
          page:1,through:null
        };
        void show("timeline");
      }catch(error){showMessage(error.message,true);}
    });
    area.append(form,note("기록·관측 시각으로 조회합니다. 실제 작업 시각은 각 기록에서 별도로 확인할 수 있습니다."));
    const page=await api(`/api/operations/timeline?${timelineQuery(filters,state.timeline.page,state.timeline.through)}`);
    if(!valid(ticket))return;
    state.timeline.through=page.throughEventId;
    if(filters.asOfRunId)area.append(note(`회차 ${filters.asOfRunId} 확정 당시까지의 운영 이력`));
    if(page.historicalManualBoundaryUnavailable)area.append(historicalBoundaryNote());
    const list=e("ol","timeline operations-timeline");
    if(!page.rows.length)area.append(note("조건에 맞는 운영 이력이 없습니다."));for(const item of page.rows){const li=e("li"),change=item.automaticChange;li.append(e("time","",dateText(item.recordedAtUtc)),e("strong","",`${kinds[item.kind]||item.kind} · ${semantic[change?.semantics]||semantic[item.action]||item.action}`),badge(sources[item.source]||"출처 미확인"),note(`${operationFields[item.field]||item.field}: ${operationValue(item,item.beforeValue)} → ${operationValue(item,item.afterValue)}`));
      if(item.work)workSummary(li,item.work);if(item.reason)li.append(note(item.reason));if(item.runId)li.append(note(`관측 회차 ${item.runId}`));if(change)li.append(note(`이전 근거 수집 ${dateText(change.beforeEvidence?.collectedAtUtc)} / 원천 ${dateText(change.beforeEvidence?.sourceAtUtc)} · 새 근거 수집 ${dateText(change.afterEvidence?.collectedAtUtc)} / 원천 ${dateText(change.afterEvidence?.sourceAtUtc)}${change.hasObservationGap?" · 공백 뒤 발견, 실제 변경 시각 미상":""}${change.needsConfirmation?" · 물리 교체 작업 미확인":""}`));
      if(item.actualWorkAtUtc)li.append(note(`실제 작업 입력 ${dateText(item.actualWorkAtUtc)}`));if(item.timeNeedsReview||change?.timeNeedsReview)li.append(badge("시각 검토 필요","amber"));
      const targets=e("details");targets.append(e("summary","","정확한 대상 보기"));for(const target of item.targets||[])targets.append(note(`AP ${text(target.apId)} · 세대 ${text(target.deviceGenerationId)} · 사건 ${text(target.incidentId)} · 지점 ${text(target.installationPointId)}`));li.append(targets);if(item.work)li.append(b(`현재 작업 기록 열기 · ${item.work.workId}`,()=>loadWorkFromTimeline(item.work.workId)));if(change?.semantics==="ReplacementDetected")li.append(b("이 감지의 교체 작업 확인",()=>replacementForm(li,item)));if(item.replacementConfirmation){const c=item.replacementConfirmation;li.append(note(`원래 감지 ${c.replacementEventId} · 확인한 작업 ${c.workId} 버전 ${c.workVersion} · 이전 세대 ${c.previousDeviceGenerationId} → ${c.deviceGenerationId}`),b("확인 근거 작업 보기",()=>loadWorkFromTimeline(c.workId)));}list.append(li);}
    area.append(list);pagination(area,page.totalRows,state.timeline.page,page=>{state.timeline.page=page;void show("timeline");});
  }
  function replacementForm(container,item){
    container.querySelector(".replacement-confirm")?.remove();const change=item.automaticChange,panel=section("교체 작업 확인"),key=`replacement:${item.eventId}`;panel.classList.add("replacement-confirm");
    const built=tracked(key,item,(form,draft)=>{field(form,"workId","실시 완료 작업 ID",{required:true});field(form,"workVersion","작업 현재 버전",{type:"number",required:true});field(form,"actual","실제 교체 시각 (작업과 같아야 함)",{type:"datetime-local"});field(form,"reason","확인 근거·사유",{type:"textarea",required:true});submit(form,draft,"교체 확인 이력 저장","/api/operations/replacement/confirm",(v,base)=>({replacementEventId:base.eventId,previousDeviceGenerationId:base.automaticChange.previousDeviceGenerationId,deviceGenerationId:base.automaticChange.deviceGenerationId,workId:v.workId.trim(),expectedWorkVersion:Number(v.workVersion),reason:v.reason,actualWorkAtUtc:utc(v.actual)}),()=>{state.timeline.through=null;return show("timeline");});});
    panel.append(note(`감지 ${item.eventId} · AP ${change.apId} · 이전 ${change.previousDeviceGenerationId} → 이후 ${change.deviceGenerationId}`),note("두 세대를 모두 정확한 대상으로 연결한 실시 작업이 필요합니다. 원래 감지 이력과 장애 상태는 유지하며 별도 확인 이력을 남깁니다."),built.form);container.append(panel);
  }
  async function loadWorkFromTimeline(id){await loadWork(id);}
  function renderImport(area){
    const panel=section("CSV 대장 가져오기 · 검토 후 적용"),form=e("form","operation-form"),file=field(form,"csv","UTF-8 CSV 파일 (최대 4 MiB)",{type:"file",required:true});file.accept=".csv,text/csv";
    panel.append(note("내부 AP ID 또는 검증된 시리얼·유선 MAC으로 연결합니다. 이름·IP는 연결 기준이 아닙니다. 값이 비어 있으면 유지하며, 해제는 별도 열로 명시해야 합니다. 상태·LLDP·전력 관측을 가져오지 않습니다."));
    const previewButton=e("button","primary","파일 미리보기");previewButton.type="submit";previewButton.dataset.mutation="true";form.append(previewButton);panel.append(form);area.append(panel);
    form.addEventListener("submit",async event=>{event.preventDefault();if(state.busy)return;const selected=file.files?.[0];if(!selected)return;if(selected.size>4*1024*1024)return showMessage("CSV는 4 MiB까지 지원합니다.",true);
      const ticket=state.epoch;state.busy=true;setBusy();file.disabled=true;state.preview=null;state.selected.clear();state.previewStale=false;
      try{const result=await api("/api/operations/import/preview",{method:"POST",rawBody:selected,contentType:"text/csv",timeout:30000});if(!valid(ticket))return;state.file=selected.name;state.preview=result;state.importCommand={};showMessage("미리보기만 만들었습니다. 적용할 유효 행을 선택하고 검토하세요.");void show("import");}
      catch(error){if(valid(ticket))showMessage(error.message,true);}finally{state.busy=false;file.disabled=false;setBusy();}});
    if(!state.preview)return;
    const preview=state.preview,review=section(`미리보기: ${state.file}`);area.append(review);review.append(note(`생성 ${dateText(preview.createdAtUtc)} · 데이터셋 ${preview.version.datasetId} · 범위 ${preview.version.scopeVersion} · 설정 ${preview.version.configVersion}`));
    if(state.previewStale)review.append(e("div","notice warning","대상 또는 설정이 변경되어 이 미리보기는 적용할 수 없습니다. 파일을 다시 선택해 미리보기부터 검토하세요."));
    for(const problem of preview.errors||[])review.append(e("p","error",`${problem.code}: ${problem.message}`));
    for(const row of preview.rows){const item=e("details","import-row");item.open=true;const heading=e("summary","",`행 ${row.rowNumber} · ${row.errors.length||!row.target?"오류 · 적용 불가":"검토 가능"}`);item.append(heading);
      const choose=field(item,`row-${row.rowNumber}`,"이 행 적용",{type:"checkbox",value:state.selected.has(row.rowNumber)});choose.disabled=!!preview.errors.length||!!row.errors.length||!row.target||state.previewStale;
      choose.addEventListener("change",()=>{if(choose.checked)state.selected.add(row.rowNumber);else state.selected.delete(row.rowNumber);review.querySelector(".import-confirm")?.remove();});
      item.append(note(`연결 AP ${text(row.target?.apId)} · 세대 ${text(row.target?.deviceGenerationId)} · 대장 버전 ${text(row.target?.manualVersion)}`));
      for(const problem of row.errors)item.append(e("p","error",`${problem.column||"행"} · ${problem.code}: ${problem.message}`));
      for(const difference of row.differences)item.append(note(`${difference.field}: ${text(difference.beforeValue)} → ${text(difference.afterValue)}${difference.isExplicitClear?" · 명시 해제":""}`));
      if(!row.differences.length)item.append(note("표시할 변경 차이 없음"));review.append(item);
    }
    const inspect=b("선택한 행 적용 내용 검토",()=>{try{if(state.previewStale)throw new Error("파일 미리보기를 다시 만드세요.");const rows=previewSelection(preview,[...state.selected]);review.querySelector(".import-confirm")?.remove();const confirm=e("div","notice import-confirm");confirm.append(e("strong","",`${rows.length}개 행 (${rows.join(", ")})의 등록 변경을 적용합니다.`),note("목록 업로드와 실제 저장은 별개입니다. 이 버튼을 눌러야 저장되며 과거 관측은 바뀌지 않습니다."));const apply=b("검토한 행 적용",()=>applyImport(preview,rows));apply.dataset.mutation="true";confirm.append(apply,b("검토 취소",()=>confirm.remove()));review.append(confirm);setBusy();}catch(error){showMessage(error.message,true);}});
    inspect.disabled=state.previewStale||!!preview.errors.length;review.append(inspect);
  }
  async function applyImport(preview,rows){if(state.busy||state.preview!==preview||state.previewStale)return;const ticket=state.epoch;
    try{if(JSON.stringify(previewSelection(preview,[...state.selected]))!==JSON.stringify(rows))throw new Error("선택 행이 바뀌었습니다. 다시 검토하세요.");state.busy=true;setBusy();const payload={previewId:preview.previewId,expectedVersion:preview.version,rowNumbers:rows},commandId=commandForDraft(state.importCommand,payload);
      const receipt=await api("/api/operations/import/apply",{method:"POST",body:{...payload,commandId},timeout:30000});state.preview=null;state.selected.clear();
      showMessage(`${receipt.rows.filter(row=>row.applied).length}개 행 적용 완료${receipt.alreadyApplied?" · 이전 적용 결과 확인":""}. 다음 AP 수집 반영을 기다립니다.`);if(valid(ticket))void show("import");
    }catch(error){if(error.status===409){state.previewStale=true;if(valid(ticket))void show("import");}showMessage(error.status===409?`${error.message} 파일 미리보기를 다시 만드세요.`:error.message,true);}finally{state.busy=false;setBusy();}
  }
  function snapshotPanel(row){const panel=section("선택한 관측의 수동 대장 · 표시 근거"),snapshot=row.manualInventory;
    if(!snapshot){panel.append(note("이 관측 회차에 동결된 수동 대장 없음. 현재 등록 정보를 과거 회차에 채워 넣지 않습니다."));return panel;}
    panel.append(note(`대장 버전 ${snapshot.version} · ${sources[snapshot.source]||snapshot.source} · 기록 ${dateText(snapshot.recordedAtUtc)} · 장비 세대 ${snapshot.deviceGenerationId}`));
    const table=e("table","data-table"),head=e("thead"),hr=e("tr");for(const label of["필드","화면 표시","자동 관측","수동 등록","표시 출처"])hr.append(e("th","",label));head.append(hr);const body=e("tbody");
    for(const [key,label]of inventoryFields){const v=inventoryValue(row,key);if(v.manual==null)continue;const tr=e("tr");for(const value of[label,text(v.value),text(v.automatic),text(v.manual),`${v.preferred?"수동 우선":"자동 우선"}${v.different?" · 값 다름":""}`])tr.append(e("td","",value));body.append(tr);}table.append(head,body);const wrap=e("div","table-wrap");wrap.append(table);panel.append(wrap);
    if(snapshot.placement){const ref=snapshot.placement;panel.append(note(`이 관측의 설치 지점 ${ref.installationPointId} · 지점 버전 ${ref.pointVersion}`));const lookup=b("당시 지점 이름 확인",async()=>{lookup.disabled=true;try{const point=await api(`/api/operations/points/${ref.installationPointId}`),version=point.versions.find(v=>v.version===ref.pointVersion);lookup.replaceWith(note(version?`당시 지점: ${version.name} · 버전 ${version.version}`:"당시 지점 버전 미확인"));}catch(error){lookup.disabled=false;showMessage(error.message,true);}});panel.append(lookup);}
    return panel;
  }
  function historicalBoundaryNote(){
    return note("이전 회차에는 수동 작업의 저장 경계가 없어 당시 포함 여부를 확인할 수 있는 기록만 표시합니다.");
  }
  function workSummary(container,work){
    const content=work.content;
    container.append(note(`작업 버전 ${work.version} · ${workStatuses[content.status]||content.status}`),
      e("p","pre-wrap",content.content));
    if(content.result)container.append(note(`결과 · ${content.result}`));
    if(content.notes)container.append(note(`메모 · ${content.notes}`));
  }
  function hasActiveIncidentDraft(apId,incidentId) {
    const draft=currentMemo;
    return !!draft && draft.apId===apId && draft.incidentId===incidentId && draft.isCurrent() &&
      (draft.pending || !!draft.input.value || document.activeElement===draft.input);
  }
  function memoKey(context) {
    return JSON.stringify([context.selection.datasetId,context.apId,context.deviceGenerationId,context.incidentId]);
  }
  function hasSavedIncidentDraft(context) {
    return incidentMemos.has(memoKey(context));
  }
  function incidentMemoForm(context,isCurrent,afterSave) {
    const {apId,deviceGenerationId,incidentId}=context;
    const key=memoKey(context);
    const cached=incidentMemos.get(key);
    if(cached) {
      cached.isCurrent=isCurrent;
      cached.afterSave=afterSave;
      currentMemo=cached;
      return cached.form;
    }
    const form=e("form","operation-form incident-quick-memo");
    const input=field(form,"quickMemo","조치 내용",{type:"textarea",required:true});
    const save=e("button","primary","조치 메모 저장"), message=e("p","footnote");
    save.type="submit"; message.setAttribute("role","status"); form.append(save,message);
    const draft={form,input,apId,incidentId,isCurrent,afterSave,pending:false,workId:crypto.randomUUID(),restoreEpoch:null};
    currentMemo=draft;
    function controls() {
      input.readOnly=draft.pending;
      save.disabled=draft.pending || draft.restoreEpoch===null || !input.value.trim();
      save.textContent=draft.pending ? "저장 중" : "조치 메모 저장";
    }
    input.addEventListener("input",()=>{
      if(input.value)incidentMemos.set(key,draft);
      else incidentMemos.delete(key);
      controls();
    });
    async function readVersion(review=false){
      try {
        const version=await api("/api/operations/version");
        if(!Number.isSafeInteger(version.restoreEpoch)||version.restoreEpoch<0)throw new Error("기록 기준을 확인할 수 없습니다.");
        if(review) {
          const latest=await api("/api/dashboard?pageSize=1");
          if(latest.datasetId!==context.selection.datasetId)throw new Error("다른 데이터 폴더의 사건입니다. 작성한 내용을 확인하세요.");
          const query=new URLSearchParams({asOfRunId:latest.runId,scopeVersion:latest.scopeVersion});
          const record=await api(`/api/incidents/${incidentId}?${query}`);
          if(record.incidentId!==incidentId || record.apId!==apId || record.detection.device.deviceGenerationId!==deviceGenerationId)
            throw new Error("이 사건의 현재 상태나 대상이 변경됐습니다. 작성한 내용을 확인하고 사건 이력을 다시 여세요.");
          const status={Open:"미복구",Recovered:"복구 확인",TrackingEnded:"추적 종료"}[record.status]||"상태 미확인";
          message.textContent=`${record.detection.device.metadata.device.name || "선택 AP"} · ${status} · 장애 감지 ${dateText(record.tracking.firstDownAtUtc)}. 내용을 검토한 뒤 저장하세요.`;
        }else message.replaceChildren();
        draft.restoreEpoch=version.restoreEpoch;
        controls();
      }catch(error){
        message.replaceChildren(e("span","",error.message),b("저장 기준 다시 확인",()=>readVersion(review)));
      }
    }
    form.addEventListener("submit",async event=>{
      event.preventDefault();
      if(!draft.isCurrent() || draft.pending || draft.restoreEpoch===null || !input.value.trim())return;
      draft.pending=true;controls();message.textContent="";
      const payload={workId:draft.workId,expectedVersion:0,expectedRestoreEpoch:draft.restoreEpoch,
        content:{workType:"조치 메모",status:"Completed",content:input.value.trim(),causeStatus:"Unknown",
          targets:workTargets([{apId,deviceGenerationId,incidentId}])}};
      try{
        const commandId=commandForDraft(draft,payload);
        await api("/api/operations/work",{method:"POST",body:{...payload,commandId},timeout:30000});
      }catch(error){
        if(error.code==="operations_conflict"){
          draft.restoreEpoch=null;
          message.replaceChildren(e("span","",error.message),b("사건과 저장 기준 다시 확인",async()=>{
            try{
              await api(`/api/operations/work/${draft.workId}`);
              message.replaceChildren(note("이 메모가 이미 저장되었습니다. 내용을 바꾸려면 저장된 작업을 여세요."),
                b("저장된 작업 확인",()=>{state.context=context;return loadWork(draft.workId);}));
            }catch(readError){
              if(readError.status===404)await readVersion(true);
              else showMessage(readError.message,true);
            }
          }));
        }else message.textContent=error.message;
        return;
      }finally{
        draft.pending=false;controls();
      }
      input.value="";draft.workId=crypto.randomUUID();incidentMemos.delete(key);
      controls();message.textContent="이 사건에 조치 메모를 저장했습니다.";
      if(draft.isCurrent())await draft.afterSave();
    });
    controls();void readVersion();return form;
  }
  async function incidentEventsPanel(context){
    const panel=section("이 사건의 장애·조치 이력");
    if(!nonEmptyGuid(context?.incidentId)){panel.append(note("사건을 선택하면 연결된 이력이 표시됩니다."));return panel;}
    try{
      const page=await api(`/api/operations/timeline?${timelineQuery(scopeOf(context))}`);
      if(page.historicalManualBoundaryUnavailable)panel.append(historicalBoundaryNote());
      if(!page.rows.length)panel.append(note("이 사건에 연결된 운영 이력이 없습니다."));
      const list=e("ol","timeline operations-timeline");
      for(const item of page.rows){
        const entry=e("li");
        entry.append(e("time","",dateText(item.recordedAtUtc)),e("strong","",semantic[item.action]||kinds[item.kind]||item.action),
          badge(sources[item.source]||"출처 미확인"));
        if(item.work)workSummary(entry,item.work);
        if(item.actualWorkAtUtc)entry.append(note(`실제 작업 시각 · ${dateText(item.actualWorkAtUtc)}`));
        if(item.reason)entry.append(note(item.reason));
        list.append(entry);
      }
      panel.append(list);
      if(page.totalRows>page.rows.length)panel.append(b("이 사건의 전체 운영 이력",()=>show("timeline",context)));
    }catch(error){panel.append(e("div","notice error",error.message));}
    return panel;
  }
  function actions(context){const panel=section("등록 정보와 운영 작업"),buttons=e("div","actions");buttons.append(b("현재 등록 정보 편집",()=>show("registration",context)),b("이 대상의 운영 이력",()=>show("timeline",context)),b("이 대상에 작업·메모 기록",()=>writeWork(context)));panel.append(note(`선택 대상: AP ${text(context.apId)} · 장비 세대 ${text(context.deviceGenerationId)}${context.incidentId?` · 사건 ${context.incidentId}`:""}`),note("작업을 기록해도 장애가 자동 복구되지 않습니다. 과거 세대의 작업 대상은 그대로 보존합니다."),buttons);return panel;}
  return {show,leave:()=>epoch(),snapshotPanel,actions,writeWork,incidentMemoForm,hasActiveIncidentDraft,hasSavedIncidentDraft,incidentEventsPanel,displayValue:(row,field,fallback)=>inventoryValue(row,field).value??fallback};
}
