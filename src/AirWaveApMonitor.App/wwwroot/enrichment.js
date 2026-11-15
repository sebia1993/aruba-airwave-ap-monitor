"use strict";
const kinds={ConnectionNeighbor:"연결·이웃",Power:"전력"};
const metrics={ApConsumption:"AP 소비전력",PortActual:"스위치 포트 실제 전력",Requested:"요청 전력",Allocated:"할당 전력",Reserved:"예약 전력",Maximum:"최대 전력"};
const availability={Unverified:"미검증",AvailableConfirmed:"제공 확인",UnsupportedConfirmed:"미지원 확인"};
const freshness={Current:"최신 원천 확인",Delayed:"원천 갱신 지연",Unknown:"원천 최신성 미상"};
const sources={Demo:"Demo 가상 자료",AirWave:"AirWave",Manual:"직접 입력",Import:"가져오기"};
const directions={Unknown:"관측 방향 미확인",LocalToRemote:"로컬 → 상대 관측",RemoteToLocal:"상대 → 로컬 관측"};
const protocols={Unknown:"발견 프로토콜 미확인",Lldp:"LLDP",Cdp:"CDP",OtherVerified:"기타 확인된 프로토콜",ApListReference:"AP 목록 참조"};
const unknown=value=>value==null||value===""?"미확인":String(value);
export function metricLabel(value){return metrics[value]||unknown(value);}
export function endpointText(value){if(!value)return "측정 대상 미확인";return `${({AccessPoint:"AP",Switch:"스위치",Controller:"컨트롤러",Other:"기타"})[value.role]||"역할 미확인"} ${unknown(value.sourceDeviceId)} / ${unknown(value.sourceInterfaceId)}`;}
export function powerText(sample,normalized){return {raw:sample?.value==null?"미제공":`${sample.value} ${unknown(sample.unit)}`,normalized:normalized?.watts==null?"미확인":`${normalized.watts} W`,reason:normalized?.reason||"Missing"};}
export function neighborText(assessment){if(assessment?.presence==="ExpectedNeighborNotObserved")return "기대 이웃 미관측";if(assessment?.presence!=="Observed")return "이웃 관측 미확인";return assessment.hasCurrentLldpObservation===true?"LLDP 최신 원천 관측":"이웃 관측 · 최신 LLDP 근거 없음";}
export function attributionText(attribution){if(attribution?.status==="Attributed"&&attribution.target)return `귀속 AP ${attribution.target.apId} · 장비 세대 ${attribution.target.deviceGenerationId}`;return ({Unknown:"AP 귀속 미확인",Ambiguous:"AP 귀속 후보 충돌",Stale:"과거 연결 근거 지연 · AP 귀속 미확인",OutsideBinding:"당시 대상 범위 밖 · AP 귀속 미확인"})[attribution?.status]||"AP 귀속 미확인";}
export function settingsInput(profile,values){const interval=Number(values.interval);if(!profile||!Number.isInteger(interval)||interval<1||interval>60)throw new Error("상세 수집 주기는 1–60분이어야 합니다.");return {expectedConfigurationVersion:profile.configurationVersion,enrichmentIntervalMinutes:interval,connectionCollectionEnabled:values.connection===true,powerCollectionEnabled:values.power===true};}

export function createEnrichmentController({api,onMessage,navigate,dateText,isCurrentView,onSettingsSaved,onHistory}){
  const e=(tag,cls,text)=>{const node=document.createElement(tag);if(cls)node.className=cls;if(text!=null)node.textContent=String(text);return node;};
  const button=(label,handler,cls="")=>{const node=e("button",cls,label);node.type="button";node.addEventListener("click",()=>void handler());return node;};
  const note=value=>e("p","footnote",value),badge=(value,tone="")=>e("span",`badge ${tone}`,value);
  const panel=title=>{const node=e("section","panel padded enrichment-panel");node.append(e("h2","",title));return node;};
  function pairs(items){const dl=e("dl","pairs");for(const [key,value]of items)dl.append(e("dt","",key),e("dd","",unknown(value)));return dl;}
  function evidence(value){return `${sources[value?.source]||"출처 미확인"} · ${value?.availability||"제공 여부 미확인"} · ${value?.validity||"유효성 미확인"} · ${freshness[value?.freshness]||freshness.Unknown} · 조회 ${dateText(value?.collectedAtUtc)} · 원천 ${dateText(value?.sourceAtUtc)}`;}
  function timing(value){return `조회 ${dateText(value?.readAtUtc)} · 원천 ${dateText(value?.sourceAtUtc)} · 시각 의미 ${value?.meaningVerified?({Measurement:"측정",LastObserved:"마지막 관측"})[value.meaning]||"미확인":"미확인"}`;}
  const state={epoch:0,tab:"overview",selection:null,capabilities:null,settings:null,draft:null,busy:false,history:null,healthRevision:0};
  const current=ticket=>ticket===state.epoch&&isCurrentView();
  function healthPanel(health){const box=e("div","enrichment-health");for(const kind of Object.keys(kinds)){const item=(health?.enrichmentKinds||[]).find(value=>value.kind===kind),part=panel(`${kinds[kind]} 수집`);if(!item){part.append(note("이 종류의 수집 상태 미제공. AP 수집 성공과 별도로 확인합니다."));box.append(part);continue;}
      part.append(badge(item.enabled?"사용 설정":"비활성",item.enabled?"blue":""),note(item.capabilityReason||"수집 가능 여부 근거 미제공"),pairs([["실행 가능",item.canCollect?"가능":"실행 안 함"],["최근 시도",`${dateText(item.lastAttemptAtUtc)} · 회차 ${unknown(item.lastAttemptRunId)}`],["최근 유효 확정",dateText(item.lastValidCommittedAtUtc)],["최근 완전 확정",dateText(item.lastCompleteCommittedAtUtc)],["최근 결과",`${unknown(item.lastOutcome)} · 완전성 ${unknown(item.completeness)} · 회차 ${unknown(item.lastFinalRunId)}`],["다음 수집",item.nextAtUtc?dateText(item.nextAtUtc):"예정 없음"],["오류 범주",item.lastError||"기록 없음"],["관측 품질 공백",item.hasQualityGap?"있음 · 이전 유효 값과 원래 시각 유지":"보고된 공백 없음"],["저장 복구 대기",item.storageRecoveryPending?"대기 중":"대기 표시 없음"]]));box.append(part);}return box;}
  function canRequest(kind){
    const decision=state.capabilities?.kinds?.find(item=>item.kind===kind)?.decision;
    const runtime=state.collector?.enrichmentKinds?.find(item=>item.kind===kind);
    return decision?.canCollect===true&&!!state.collector&&!state.collector.paused&&!state.collector.authenticationBlocked&&!state.collector.storageRecoveryPending&&runtime?.canCollect!==false&&!runtime?.storageRecoveryPending;
  }
  function refreshCollectionButtons(){
    const root=document.getElementById("enrichment-content");
    for(const control of root?.querySelectorAll(".enrichment-run-button")||[])control.disabled=state.busy||!canRequest(control.dataset.kind);
  }
  function refreshControlState(){
    const form=document.getElementById("enrichment-content")?.querySelector(".enrichment-form");
    if(form){for(const control of form.querySelectorAll("input")){const kind=({connection:"ConnectionNeighbor",power:"Power"})[control.name];const decision=state.capabilities?.kinds?.find(item=>item.kind===kind)?.decision;control.disabled=state.busy||(!!kind&&decision?.availability!=="AvailableConfirmed");}for(const control of form.querySelectorAll("button"))control.disabled=state.busy;}
    refreshCollectionButtons();
  }
  function setBusy(value){state.busy=value;refreshControlState();}
  function renderHealth(health){
    state.collector=health;state.healthRevision++;
    const footer=document.getElementById("enrichment-health");if(footer)footer.replaceChildren(healthPanel(health));
    if(isCurrentView()&&state.tab==="overview"){const main=document.getElementById("enrichment-content")?.querySelector(".enrichment-overview-health");if(main)main.replaceChildren(healthPanel(health));refreshCollectionButtons();}
  }
  function appendEvidence(container,title,rows,render){
    const pageSize=50;
    if(rows.length<=pageSize){for(const row of rows)container.append(render(row));return;}
    const section=panel(title);section.classList.add("enrichment-evidence-pages");section.dataset.evidenceKind=title;
    const range=note(""),items=e("div"),controls=e("div","pager"),jump=e("form","toolbar enrichment-filters");
    const pageCount=Math.ceil(rows.length/pageSize);let page=1;
    const number=field(jump,"evidencePage","페이지",{type:"number",value:1});number.min=1;number.max=pageCount;number.required=true;
    const go=e("button","","페이지 이동");go.type="submit";jump.append(go);
    const previous=button("이전 증거",()=>display(page-1)),next=button("다음 증거",()=>display(page+1));controls.append(previous,next);
    function display(selected){
      if(!Number.isInteger(selected)||selected<1||selected>pageCount){onMessage(`1–${pageCount} 페이지를 입력하세요.`,true);return;}
      page=selected;const start=(page-1)*pageSize;
      range.textContent=`총 ${rows.length}건 중 ${start+1}–${Math.min(start+pageSize,rows.length)}건 · ${page}/${pageCount}페이지 · 원래 저장 순서 유지`;
      items.replaceChildren(...rows.slice(start,start+pageSize).map(render));number.value=page;
      previous.disabled=page===1;next.disabled=page===pageCount;
    }
    jump.addEventListener("submit",event=>{event.preventDefault();display(Number(number.value));});
    section.append(range,jump,controls,items);container.append(section);display(1);
  }
  const displayedConnections=(observation,assessment)=>assessment?.observations||observation?.neighbors||[];
  // Exact payload equality only: reused IDs with changed clocks, quality or target evidence must remain visible.
  const connectionEvidenceKey=value=>JSON.stringify(value,(_,item)=>item&&typeof item==="object"&&!Array.isArray(item)?Object.fromEntries(Object.keys(item).sort().map(key=>[key,item[key]])):item);
  function distinctRetainedConnections(connection){
    const shown=new Set(displayedConnections(connection.observation,connection.assessment).map(connectionEvidenceKey));
    return (connection.retainedConnections||[]).filter(item=>!shown.has(connectionEvidenceKey(item)));
  }
  function connectionPanel(observation,assessment){const part=panel("연결·이웃 관측"),observations=displayedConnections(observation,assessment);part.append(badge(neighborText(assessment)),badge(freshness[assessment?.freshness]||freshness.Unknown,"amber"),note(`서버 판정: ${unknown(assessment?.reason)} · 조회 범위 완전성 ${unknown(observation?.completeness)} · 기대 포트 범위 ${observation?.scopeCovered?"포함 확인":"미확인"}`),note(timing(observation?.timing)));
    if(observation?.scope)part.append(note(`조회 대상 ${endpointText(observation.scope.local)} · 기대 상대 ${endpointText(observation.scope.expectedRemote)}`));
    if(!observations.length)part.append(note("이 응답에 표시할 이웃 레코드가 없습니다. 케이블 분리 또는 AP Down을 뜻하지 않습니다."));
    appendEvidence(part,"이웃 연결 레코드",observations,link=>{const item=e("div","enrichment-observation");item.append(e("h3","",`${endpointText(link.local)} · 상대 ${endpointText(link.remote)}`),badge(protocols[link.protocol]||"발견 프로토콜 미확인"),note(directions[link.direction]||directions.Unknown),note(`연결 관측 ${link.id} · 회차 ${link.runId} · ${sources[link.profile?.source]||"출처 미확인"}`),note(timing(link.timing)),note(`AP 내부 대상 매핑 ${link.identityMappingVerified?"확인":"미확인"} · ${evidence(link.evidence)}`));if(link.target)item.append(note(`저장된 대상 AP ${link.target.apId} · 세대 ${link.target.deviceGenerationId} · 감시 구간 ${link.target.monitoringIntervalId}`));return item;});
    part.append(note("한쪽 관측을 양방향 수신이나 무중단 서비스 정상으로 해석하지 않습니다. Upstream 참조·포트 Up·PoE 공급과 LLDP 관측은 별개입니다."));return part;
  }
  function portPanel(observation,assessment){const part=panel("포트 관리·운영 상태");part.append(note(`${endpointText(observation.endpoint)} · 회차 ${observation.runId}`),pairs([["관리 상태",assessment?.administrativeState],["운영 상태",assessment?.operationalState],["관리 원문",observation.rawAdministrativeState],["운영 원문",observation.rawOperationalState]]),note(`관리 근거 ${evidence(observation.administrativeEvidence)}`),note(`운영 근거 ${evidence(observation.operationalEvidence)}`),note("Unknown·Dormant·NotPresent·LowerLayerDown을 AP Down으로 바꾸지 않습니다."));return part;}
  function supplyPanel(observation,assessment){const part=panel("전원 공급 관측");part.append(note(`${endpointText(observation.endpoint)} · 회차 ${observation.runId}`),pairs([["공급 상태",assessment?.supplyState],["전원 유형",assessment?.supplyMethod],["오류 카운터",assessment?.errorCount],["LLDP 협상 지원",assessment?.lldpNegotiationSupported==null?"미확인":assessment.lldpNegotiationSupported?"지원 표시 있음 · 이웃 관측과 별개":"지원 안 함 표시"],["공급 원문",observation.rawSupplyState],["전원 유형 원문",observation.rawSupplyMethod]]),note(evidence(observation.supplyEvidence)),note(evidence(observation.methodEvidence)),note(evidence(observation.errorEvidence)),note(evidence(observation.negotiationEvidence)));return part;}
  function samplePanel(sample,normalized,attribution){const part=panel(metricLabel(sample.metric)),display=powerText(sample,normalized);part.append(note(`${endpointText(sample.endpoint)} · 표본 ${sample.id} · 회차 ${sample.runId}`),pairs([["원문 값·단위",display.raw],["검증된 W 값",display.normalized],["정규화 판정",display.reason],["원문 단위 확인",sample.unitVerified?"확인":"미확인"],["귀속 판정",attributionText(attribution)],["귀속 이유",attribution?.reason],["검증된 측정 시각",dateText(attribution?.measuredAtUtc)]]),note(timing(sample.timing)),note(evidence(sample.evidence)));
    if(attribution?.connection){const c=attribution.connection;part.append(note(`당시 선택 연결 ${c.id} · 회차 ${c.runId} · ${endpointText(c.local)} / ${endpointText(c.remote)} · ${directions[c.direction]||directions.Unknown}`),note(timing(c.timing)));}
    if(attribution?.target?.placement){const p=attribution.target.placement;part.append(note(`당시 지점 ${unknown(p.installationPointId)} · 지점 버전 ${unknown(p.pointVersion)} · 배치 버전 ${p.placementVersion}`));}return part;}
  function field(form,name,label,{type="text",value="",options=null}={}){const wrapper=e("label","enrichment-field"),control=e(options?"select":"input");control.name=name;if(options){for(const [key,text]of options){const option=e("option","",text);option.value=key;control.append(option);}}else control.type=type;control.value=value;if(type==="checkbox"){control.checked=value===true;wrapper.className+=" check";wrapper.append(control,e("span","",label));}else wrapper.append(e("span","",label),control);form.append(wrapper);return control;}
  const read=form=>Object.fromEntries([...form.querySelectorAll("[name]")].map(input=>[input.name,input.type==="checkbox"?input.checked:input.value]));
  function overviewControls(area){const settings=state.settings,profile=settings?.profile,capabilities=state.capabilities,box=panel("연결·전력 수집 설정");box.append(note(settings?.mode==="demo"?"Demo 가상 관측을 직접 켠 경우에만 수집합니다. 가상 제공 확인은 실제 AirWave 필드 제공 확인과 별개입니다.":"Live 데이터 계약은 현장 확인 전까지 미검증입니다. 화면 입력으로 미검증 수집을 활성화하지 않습니다."));
    if(!profile){box.append(note("서버 연결 프로필을 먼저 등록하세요."));area.append(box);return;}
    if(!state.draft||!state.draft.dirty)state.draft={base:profile,dirty:false,values:{interval:profile.settings.enrichmentIntervalMinutes,connection:profile.settings.connectionCollectionEnabled,power:profile.settings.powerCollectionEnabled}};
    const draft=state.draft,form=e("form","enrichment-form"),interval=field(form,"interval","상세 수집 주기 (분)",{type:"number",value:draft.values.interval});interval.min=1;interval.max=60;interval.required=true;
    const connection=field(form,"connection","연결·이웃 수집 사용",{type:"checkbox",value:draft.values.connection}),power=field(form,"power","전력 수집 사용",{type:"checkbox",value:draft.values.power});
    for(const [kind,input]of[["ConnectionNeighbor",connection],["Power",power]]){const decision=capabilities?.kinds?.find(item=>item.kind===kind)?.decision;input.disabled=state.busy||decision?.availability!=="AvailableConfirmed";}
    interval.disabled=state.busy;const save=e("button","primary","상세 수집 설정 저장");save.type="submit";save.disabled=state.busy;form.append(save,note(`편집 기준 설정 버전 ${draft.base.configurationVersion} · AP 상태 수집 설정은 별도로 유지합니다.`));
    const changed=()=>{draft.values=read(form);draft.dirty=true;};form.addEventListener("input",changed);form.addEventListener("change",changed);
    form.addEventListener("submit",async event=>{event.preventDefault();if(state.busy)return;const ticket=state.epoch;try{draft.values=read(form);draft.dirty=true;const payload=settingsInput(draft.base,draft.values);setBusy(true);await api("/api/enrichment/settings",{method:"POST",body:payload});state.draft=null;onMessage("상세 수집 설정을 저장했습니다. 수집 결과는 종류별 실행 기록에서 확인하세요.");onSettingsSaved?.();if(current(ticket))await show("overview");}catch(error){onMessage(error.status===409?`${error.message} 입력을 유지했습니다. 최신 설정을 다시 읽고 검토하세요.`:error.message,true);}finally{setBusy(false);}});
    box.append(form,button("저장된 상세 설정 다시 읽기",()=>{if(state.busy)return;if(state.draft?.dirty){const confirm=e("div","notice warning");confirm.append(note("저장하지 않은 상세 설정 입력을 버리고 서버 값으로 다시 채웁니다."),button("입력 유지",()=>confirm.remove()),button("서버 값으로 다시 채우기",()=>{state.draft=null;void show("overview");}));box.append(confirm);}else void show("overview");}));area.append(box);
  }
  async function overview(area,ticket){const healthRevision=state.healthRevision;const [capabilities,settings,health]=await Promise.all([api("/api/enrichment/capabilities"),api("/api/settings",{timeout:130000}),api("/api/health")]);if(!current(ticket))return;state.capabilities=capabilities;state.settings=settings;if(state.healthRevision===healthRevision)state.collector=health.collector;const mainHealth=e("div","enrichment-overview-health");mainHealth.append(healthPanel(state.collector));area.append(mainHealth);const controls=e("div","enrichment-overview-controls");area.append(controls);overviewControls(controls);
    for(const kind of capabilities.kinds||[]){const box=panel(`${kinds[kind.kind]||kind.kind} 제공 범위`);box.append(badge(availability[kind.decision?.availability]||availability.Unverified),note(`사용 설정 ${kind.enabled?"켜짐":"꺼짐"} · 실행 ${kind.decision?.canCollect?"가능":"안 함"} · 서버 판정 ${unknown(kind.decision?.reason)}`));for(const field of kind.fields||[]){const details=e("details","enrichment-capability"),evidence=field.evidence,key=evidence?.key;details.append(e("summary","",`${key?.field||"필드 미확인"} · ${availability[field.decision?.availability]||availability.Unverified}`),pairs([["장비 유형",key?.deviceFamily],["계약 버전",key?.contractRevision],["코드 지원",evidence?.implemented?"구현됨":"지원 코드 미확인"],["계약 검증",evidence?.contractVerified?"확인":"미검증"],["확인 방법",evidence?.verification],["근거 시각",dateText(evidence?.verifiedAtUtc)],["근거 설명",evidence?.safeReason],["근거 출처",sources[key?.profile?.source]],["설정·범위 버전",key?.profile?`${key.profile.configurationVersion} / ${key.profile.scopeVersion}`:null]]));box.append(details);}
      const run=button("이 종류 지금 수집",async()=>{if(state.busy||!canRequest(kind.kind))return;setBusy(true);try{const result=await api("/api/collector/requests",{method:"POST",body:{kind:kind.kind,trigger:"Manual"}});onMessage(`${kinds[kind.kind]} 수집 요청: ${result.status}. 완료 여부는 수집 기록에서 확인하세요.`);}catch(error){onMessage(error.message,true);}finally{setBusy(false);}},"enrichment-run-button");run.dataset.kind=kind.kind;run.disabled=state.busy||!canRequest(kind.kind);box.append(run);area.append(box);}

  }
  function snapshotPanel(snapshot,title="선택한 관측에 저장된 연결·전력"){
    const box=panel(title);box.append(note("이 관측을 저장할 때 반영되어 있던 부가 정보입니다. 후속 수집이나 현재 연결로 당시 값을 채우지 않습니다."));
    if(!snapshot){box.append(note("당시 저장된 연결·전력 자료 없음 · 미검증/미수집/미제공은 현재값으로 대체하지 않습니다."));return box;}
    box.append(note(`저장 ${dateText(snapshot.capturedAtUtc)} · 회차 상한 ${snapshot.throughRunId} · 저장 순서 ${snapshot.throughCommitSequence}`));
    if(snapshot.powerSamplesExpired)box.append(e("div","notice warning","당시 원시 전력 표본이 보존 기간 만료로 제거되었습니다. 사건에 복사한 스냅샷과 상태 전환 이력은 별도 보존합니다."));
    appendEvidence(box,"연결·이웃 조회",snapshot.connections||[],connection=>{const group=e("div"),previous=distinctRetainedConnections(connection);group.append(connectionPanel(connection.observation,connection.assessment));if(connection.hasQualityGap)group.append(note(previous.length?"연결 관측 공백 · 아래 이전 유효 연결은 원래 시각의 참고 정보입니다.":"연결 관측 공백 · 표시된 관측의 원래 시각과 품질을 확인하세요."));if(previous.length){const retained=connectionPanel(null,{presence:"Unknown",freshness:"Unknown",observations:previous});retained.prepend(e("h3","","이전 유효 연결 · 최신 확인 아님"));group.append(retained);}return group;});
    appendEvidence(box,"포트 상태 관측",snapshot.ports||[],item=>{const group=e("div");group.append(portPanel(item.observation,item.assessment));if(item.hasQualityGap)group.append(note("포트 관측 품질 공백 · 마지막 유효 상태의 근거 시각 확인"));return group;});
    appendEvidence(box,"전원 공급 상태 관측",snapshot.powerStates||[],item=>{const group=e("div");group.append(supplyPanel(item.observation,item.assessment));if(item.hasQualityGap)group.append(note("전원 상태 관측 품질 공백 · 마지막 유효 상태의 근거 시각 확인"));return group;});
    appendEvidence(box,"전력 수치 표본",snapshot.powerSamples||[],item=>samplePanel(item.sample,item.normalized,item.attribution));
    if(!(snapshot.connections?.length||snapshot.ports?.length||snapshot.powerStates?.length||snapshot.powerSamples?.length))box.append(note("이 스냅샷에 보존된 값이 없습니다."));
    appendEvidence(box,"관측 공백",snapshot.gaps||[],gap=>note(`관측 공백 ${kinds[gap.kind]||gap.kind||"종류 미확인"} · ${unknown(gap.reason)} · 시작 ${dateText(gap.firstMissingAtUtc)} · 재개 ${dateText(gap.resumedAtUtc)}`));return box;
  }
  function table(headers,rows,render){const wrap=e("div","table-wrap"),table=e("table","data-table"),head=e("thead"),tr=e("tr"),body=e("tbody");for(const label of headers){const th=e("th","",label);th.scope="col";tr.append(th);}head.append(tr);for(const row of rows){const tr=e("tr");render(tr,row);body.append(tr);}table.append(head,body);wrap.append(table);return wrap;}
  function cell(tr,value){const td=e("td");if(value?.tagName)td.append(value);else td.textContent=unknown(value);tr.append(td);}
  function pager(area,page,total,pageSize,load){const bar=e("div","pager"),prev=button("이전",()=>load(page-1)),next=button("다음",()=>load(page+1));prev.disabled=page<=1;next.disabled=page*pageSize>=total;bar.append(note(`${total}건 · ${page} / ${Math.max(1,Math.ceil(total/pageSize))}`),prev,next);area.append(bar);}
  function actions(selection){const box=panel("당시 범위의 전력 측정 이력");box.append(note("별도 표본 조회는 선택한 회차까지로 고정합니다. 사건에 저장된 스냅샷은 그대로 유지합니다."),button("보존 기간 내 전력 표본 보기",()=>show("power",selection)));if(onHistory)box.append(button("연결·전원 상태 전환 이력",()=>onHistory(selection)),note("상태 전환은 저장된 운영 이력에서 확인합니다. 전력 수치의 매번 변화는 변경 사건으로 만들지 않습니다."));return box;}
  async function show(tab=state.tab,selection=undefined){state.tab=tab;if(selection!==undefined){state.selection=selection;state.power={page:1,through:null,metric:"",from:"",to:""};}navigate("enrichment");const ticket=++state.epoch,root=document.getElementById("enrichment-content"),tabs=e("div","tabs enrichment-tabs");for(const [key,label]of[["overview","수집·제공 범위"],["runs","상세 수집 기록"],["power","전력 표본"]]){const b=button(label,()=>show(key),`tab${tab===key?" active":""}`);b.setAttribute("aria-pressed",String(tab===key));tabs.append(b);}const area=e("div","enrichment-area");root.replaceChildren(tabs,area);try{if(tab==="overview")await overview(area,ticket);else if(tab==="runs")await runs(area,ticket);else await powerHistory(area,ticket);}catch(error){if(current(ticket))area.append(e("div","notice error",error.message));}}
  // History readers below use server paging, recorded assessments and a pinned run boundary.
  async function runs(area,ticket){
    state.runs??={page:1,kind:"",scope:"",through:null};const value=state.runs,form=e("form","toolbar enrichment-filters");
    field(form,"kind","수집 종류",{value:value.kind,options:[["","연결·전력 전체"],...Object.entries(kinds)]});field(form,"scope","범위 버전 (선택)",{type:"number",value:value.scope});
    const search=e("button","","조건 적용 / 최신 기록");search.type="submit";form.append(search);form.addEventListener("submit",event=>{event.preventDefault();const v=read(form);state.runs={page:1,kind:v.kind,scope:v.scope,through:null};void show("runs");});area.append(form,note("상세 종류의 실행 기록입니다. AP 상태 수집의 성공·실패 시각과 분리하며, 페이지 이동은 같은 회차 상한을 유지합니다."));
    const query=new URLSearchParams({page:value.page,pageSize:30});if(value.kind)query.set("kind",value.kind);if(value.scope)query.set("scopeVersion",value.scope);if(value.through)query.set("throughRunId",value.through);
    const result=await api(`/api/enrichment/runs?${query}`);if(!current(ticket))return;value.through=result.throughRunId;
    area.append(table(["회차 / 종류","시작 / 확정","결과 / 완전성","적용 범위","오류"],result.rows||[],(tr,row)=>{cell(tr,button(`회차 ${row.runId} · ${kinds[row.kind]||row.kind}`,()=>showRun(row.runId)));cell(tr,`${dateText(row.startedAtUtc)} / ${dateText(row.finalizedAtUtc)}`);cell(tr,`${unknown(row.outcome)} / ${unknown(row.completeness)}`);cell(tr,`${unknown(row.disposition)} · 범위 ${row.scopeVersion}`);cell(tr,(row.errors||[]).map(error=>error.safeMessage||error.category).join(" · ")||"기록 없음");}));
    if(!result.rows?.length)area.append(note("표시할 상세 수집 기록이 없습니다."));pager(area,value.page,result.totalRows,30,page=>{value.page=page;void show("runs");});
  }
  async function showRun(runId){
    state.tab="runs";navigate("enrichment");const ticket=++state.epoch,root=document.getElementById("enrichment-content"),area=e("div","enrichment-area");root.replaceChildren(button("상세 수집 기록으로",()=>show("runs")),area);
    try{const detail=await api(`/api/enrichment/runs/${runId}`);if(!current(ticket))return;const run=detail.run;area.append(panel(`상세 수집 회차 ${run.runId}`),pairs([["수집 종류",kinds[run.kind]],["범위 / 설정 버전",`${run.scopeVersion} / ${run.configurationVersion}`],["출처",sources[run.source]],["시작",dateText(run.startedAtUtc)],["확정",dateText(run.finalizedAtUtc)],["결과 / 수명주기",`${unknown(run.outcome)} / ${run.lifecycle}`],["완전성",run.completeness],["적용 범위",run.disposition],["조회 수 / 포트 수",`${run.connectionQueryCount} / ${run.portCount}`],["전력 표본 / 공급 상태 수",`${run.powerSampleCount} / ${run.powerStateCount}`],["품질 공백",run.hasQualityGap?"있음":"표시 없음"]]));for(const error of run.errors||[])area.append(note(`${error.category} · ${error.safeMessage}`));area.append(snapshotPanel(detail.evidence,"이 상세 회차의 저장 관측"));}
    catch(error){if(current(ticket))area.append(e("div","notice error",error.message));}
  }
  async function powerHistory(area,ticket){
    state.power??={page:1,through:null,metric:"",from:"",to:"",sourceDeviceId:"",sourceInterfaceId:"",endpointRole:""};const value=state.power,selection=state.selection,form=e("form","toolbar enrichment-filters");
    if(selection)area.append(e("div","notice",`과거 조회 고정 · AP ${unknown(selection.apId)} · 장비 세대 ${unknown(selection.deviceGenerationId)} · 회차 ${selection.runId} · 범위 ${selection.scopeVersion}`),button("전체 대상의 최신 표본 조회",()=>show("power",null)));
    else area.append(button("최신 표본 기준 다시 읽기",()=>{state.power.through=null;state.power.scopeVersion=null;state.power.page=1;void show("power");}));
    field(form,"metric","전력 지표",{value:value.metric,options:[["","모든 지표 (합산 안 함)"],...Object.entries(metrics)]});field(form,"from","조회 시각 시작",{type:"datetime-local",value:value.from});field(form,"to","조회 시각 종료",{type:"datetime-local",value:value.to});
    if(!selection){field(form,"sourceDeviceId","원천 장비 ID",{value:value.sourceDeviceId});field(form,"sourceInterfaceId","원천 인터페이스 ID",{value:value.sourceInterfaceId});field(form,"endpointRole","측정 지점 종류",{value:value.endpointRole,options:[["","전체"],["AccessPoint","AP"],["Switch","스위치"],["Controller","컨트롤러"],["Other","기타"]]});}
    const search=e("button","","조건 적용");search.type="submit";form.append(search);form.addEventListener("submit",event=>{event.preventDefault();const v=read(form);state.power={...value,...v,page:1};void show("power");});area.append(form,note("필터와 30일 보존 기준은 수집기가 읽은 조회 시각입니다. 원천·검증된 측정 시각은 별도로 표시합니다. 실제·요청·할당·최대 전력을 합산하거나 누적 에너지로 환산하지 않으며 미확인 값은 0 W가 아닙니다."));
    if(!selection&&value.scopeVersion==null){const settings=await api("/api/settings",{timeout:130000});if(!current(ticket))return;if(!settings.profile)throw new Error("서버 연결 프로필을 먼저 등록하세요.");value.scopeVersion=settings.profile.scopeVersion;}
    const query=powerQuery(selection,value,value.through),result=await api(`/api/enrichment/power?${query}`);if(!current(ticket))return;value.through=result.throughCommitSequence;
    area.append(note(`수치 표본 보존 ${result.retentionDays}일 · 실제 만료 기준 ${dateText(result.expiredBeforeUtc)} · 저장 순서 상한 ${result.throughCommitSequence}${result.throughRunId?` · 선택 회차 ${result.throughRunId}`:""}`));if(result.mayIncludeExpiredSamples)area.append(e("div","notice warning","조회 범위에 만료 가능 표본이 있습니다. 표본이 없는 구간을 0 W 또는 공급 중단으로 해석하지 않습니다."));
    area.append(table(["원천 시각 / 조회","측정 지점","지표","원문 값","검증된 W","당시 AP 귀속 / 상세"],result.rows||[],(tr,item)=>{const sample=item.sample,display=powerText(sample,item.normalized);cell(tr,`${dateText(sample.timing?.sourceAtUtc)} / ${dateText(sample.timing?.readAtUtc)}`);cell(tr,endpointText(sample.endpoint));cell(tr,metricLabel(sample.metric));cell(tr,display.raw);cell(tr,`${display.normalized} · ${display.reason}`);const detail=e("details");detail.append(e("summary","",attributionText(item.attribution)),samplePanel(sample,item.normalized,item.attribution));cell(tr,detail);}));if(!result.rows?.length)area.append(note("조건에 맞는 보존 표본 없음 · 미측정/미제공/만료 여부는 위 보존 범위와 수집 기록을 확인하세요."));pager(area,value.page,result.totalRows,30,page=>{value.page=page;void show("power");});
  }
  return {show,showRun,leave:()=>++state.epoch,renderHealth,snapshotPanel,actions};
}
export function powerQuery(selection,filters,throughCommitSequence){
  const query=new URLSearchParams({page:filters.page||1,pageSize:30});
  for(const key of["scopeVersion","apId","deviceGenerationId","installationPointId"])if(selection?.[key])query.set(key,selection[key]);
  if(!selection&&filters.scopeVersion)query.set("scopeVersion",filters.scopeVersion);
  if(selection?.runId)query.set("throughRunId",selection.runId);
  if(throughCommitSequence!=null)query.set("throughCommitSequence",throughCommitSequence);
  for(const key of["metric","sourceDeviceId","sourceInterfaceId","endpointRole"])if(filters[key])query.set(key,filters[key]);
  for(const [input,key]of[["from","fromUtc"],["to","toUtc"]])if(filters[input]){const date=new Date(filters[input]);if(Number.isNaN(date.getTime()))throw new Error("조회 시각을 확인하세요.");query.set(key,date.toISOString());}
  return query;
}
