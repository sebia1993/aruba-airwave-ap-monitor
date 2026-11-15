"use strict";
import { countText, createRequestGate, dashboardQuery, observationStatus, incidentElapsed, settingsIdentity, shouldFollowLatest, authenticationUiState } from "./ui-state.js";

const byId = id => document.getElementById(id);
const labels = { dashboard: "다운 AP 현황", incidents: "장애·복구 이력", runs: "수집 기록", settings: "연결 설정", detail: "상세 이력" };
const statusLabels = { NewDown: "신규 Down", InitialDown: "최초 확인 Down · 실제 시작 미상", ExistingDown: "기존 Down", Recovered: "이번 복구 확인", Up: "Up 확인", Unknown: "이번 미확인" };
const reasonLabels = { MissingRecord: "응답 목록 누락", UnknownState: "유효 상태 미제공", DuplicateIdentity: "중복 식별 제외", CollectionFailed: "수집 실패", Interrupted: "수집 중단", ScopeExcluded: "범위 일시 제외", TrackingEnded: "추적 종료", DatabaseRestored: "백업 복원으로 관측 공백" };
const managementLabels = { Operating: "운영", PlannedWork: "계획 작업", Decommissioned: "철거", MonitoringExcluded: "감시 제외" };
const incidentLabels = { Open: "미복구", Recovered: "복구 확인", TrackingEnded: "추적 종료" };
const triggerLabels = { Scheduled:"예약",Manual:"수동",Startup:"시작",Resume:"재개",DatabaseRestore:"백업 복원 · 관측 공백" };
const kindLabels = { ApStatus:"AP 상태",ConnectionNeighbor:"연결·이웃",Power:"전력" };
const sourceLabels = { AirWave: "AirWave", Demo: "가상 자료", Manual: "직접 입력", Import: "가져오기" };
const state = {
  view: "dashboard", backView: "dashboard", historical: false, selection: null, snapshot: null,
  filters: { search: "", filter: "OpenIncidents", sort: "FirstDownAt", descending: true, managementStatus: "", hidePlanned: false, page: 1, pageSize: 50 },
  incident: { status: "", search: "", page: 1, pageSize: 50 },
  runs: { scopeVersion: "", page: 1, pageSize: 50, throughRunId: null },
  health: null, maintenance: null, settings: null, mode: null, mutationBusy: false, settingsDirty: false, draftRevision: 0,
  token: null, tokenPromise: null, diagnosticIdentity: null, lastDashboardRead: null
};
const gates = { dashboard: createRequestGate(), incidents: createRequestGate(), runs: createRequestGate(), detail: createRequestGate(), settings: createRequestGate(), health: createRequestGate() };
let searchTimer, incidentSearchTimer;

function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text != null) node.textContent = String(text);
  return node;
}
function button(text, handler, className = "") {
  const node = el("button", className, text); node.type = "button"; node.addEventListener("click", handler); return node;
}
function badge(text, tone = "") { return el("span", `badge ${tone}`, text); }
function valueText(value) { return value == null || value === "" ? "미제공" : String(value); }
function dateText(value) {
  if (!value) return "미확인";
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? "시각 확인 필요" : new Intl.DateTimeFormat("ko-KR", { year:"numeric", month:"2-digit", day:"2-digit", hour:"2-digit", minute:"2-digit", second:"2-digit", hour12:false }).format(date);
}
function durationText(milliseconds) {
  if (milliseconds == null || !Number.isFinite(milliseconds) || milliseconds < 0) return "시간 확인 필요";
  const minutes = Math.floor(milliseconds / 60000);
  if (!minutes) return "1분 미만";
  const days = Math.floor(minutes / 1440), hours = Math.floor(minutes % 1440 / 60), rest = minutes % 60;
  return [days ? `${days}일` : "", hours ? `${hours}시간` : "", rest ? `${rest}분` : ""].filter(Boolean).join(" ");
}
function selectionText(selection) { return selection ? `회차 ${selection.runId} · 범위 ${selection.scopeVersion} · ${dateText(selection.finalizedAtUtc || selection.startedAtUtc)} 기준` : "기준 회차 없음"; }
function selectionOf(snapshot) { return snapshot && { datasetId:snapshot.datasetId, scopeVersion:snapshot.scopeVersion, runId:snapshot.runId, startedAtUtc:snapshot.startedAtUtc, finalizedAtUtc:snapshot.finalizedAtUtc, lifecycle:snapshot.lifecycle, outcome:snapshot.outcome }; }
function setMessage(text, error = false) {
  byId("global-message").hidden = error || !text; byId("global-message").textContent = error ? "" : text;
  byId("global-error").hidden = !error || !text; byId("global-error").textContent = error ? text : "";
}
function empty(container, title, description) { const heading = el(container.id === "detail-content" ? "h1" : "strong", "", title); if (container.id === "detail-content") heading.id = "detail-title"; container.replaceChildren(el("div", "empty")); container.firstChild.append(heading, el("p", "", description)); }
function notice(container, title, description, tone = "") {
  container.className = `notice ${tone}`; container.replaceChildren(el("strong", "", title));
  if (description) container.append(el("span", "", description));
}
function outcomeLabel(row) {
  if (row.lifecycle === "Running") return { label:"진행 중", tone:"blue" };
  if (row.lifecycle === "Interrupted") return { label:"중단", tone:"amber" };
  return ({ Succeeded:{label:"수집 성공",tone:"green"}, Partial:{label:"일부 확인",tone:"amber"}, Failed:{label:"수집 실패",tone:"red"} })[row.outcome] || { label:"미확인", tone:"amber" };
}
function buildTable(container, headings, rows, renderRow) {
  const wrap = el("div", "table-wrap"), table = el("table", "data-table"), head = el("thead"), headingRow = el("tr"), body = el("tbody");
  headings.forEach(label => { const th = el("th", "", label); th.scope = "col"; headingRow.append(th); });
  head.append(headingRow); rows.forEach(row => { const tr = el("tr"); renderRow(tr, row); body.append(tr); }); table.append(head, body); wrap.append(table); container.replaceChildren(wrap);
}
function cell(row, label, main, sub) {
  const td = el("td"); td.dataset.label = label;
  if (main instanceof Node) td.append(main); else td.append(el("span", "cell-main", main));
  if (sub != null) td.append(el("span", "cell-sub", sub)); row.append(td); return td;
}
function updatePager(prefix, total, page, pageSize, hidden = 0) {
  const pages = Math.max(1, Math.ceil(total / pageSize)), first = total ? (page - 1) * pageSize + 1 : 0, last = Math.min(page * pageSize, total);
  byId(`${prefix}-page-label`).textContent = `${countText(first)}–${countText(last)} / ${countText(total)}${prefix === "ap" ? "대" : "건"}${prefix === "ap" && state.filters.hidePlanned ? ` · 계획 작업 ${countText(hidden)}대 숨김` : ""}`;
  byId(`${prefix}-page-number`).textContent = `${page} / ${pages}`; byId(`${prefix}-prev`).disabled = page <= 1; byId(`${prefix}-next`).disabled = page >= pages;
}

class ApiError extends Error {
  constructor(response, payload) { super(payload?.message || payload?.title || `요청을 완료하지 못했습니다. (${response.status})`); this.status = response.status; this.code = payload?.code; }
}
async function api(path, { method = "GET", body, timeout = 15000 } = {}) {
  if (method !== "GET") await ensureToken();
  const controller = new AbortController(), timer = setTimeout(() => controller.abort(), timeout);
  try {
    const response = await fetch(path, { method, credentials:"same-origin", cache:"no-store", signal:controller.signal,
      headers: method === "GET" ? { Accept:"application/json" } : { Accept:"application/json", "Content-Type":"application/json", "X-CSRF-TOKEN":state.token },
      body: body === undefined ? undefined : JSON.stringify(body) });
    let payload; try { payload = await response.json(); } catch { payload = null; }
    if (!response.ok) throw new ApiError(response, payload);
    if (payload == null) throw new Error("서버 응답을 읽지 못했습니다.");
    return payload;
  } catch (error) {
    if (error.name === "AbortError") throw new Error(method === "GET" ? "화면 조회 시간이 초과되었습니다. 저장 상태를 확인한 뒤 새로고침하세요." : "요청 시간이 초과되었습니다. 서버 실행 상태를 확인한 뒤 다시 시도하세요.");
    throw error;
  } finally { clearTimeout(timer); }
}
async function ensureToken() {
  if (state.token) return;
  if (!state.tokenPromise) state.tokenPromise = api("/api/antiforgery").then(result => { state.token = result.token; }).finally(() => { state.tokenPromise = null; });
  await state.tokenPromise;
  if (!state.token) throw new Error("변경 요청 확인 토큰을 읽지 못했습니다. 화면을 새로고침하세요.");
}
function navigate(view, focus = true) {
  if (state.view !== view) {
    gates.dashboard.invalidate(); gates.incidents.invalidate(); gates.runs.invalidate(); gates.detail.invalidate(); state.dashboardLoading = false;
  }
  state.view = view;
  document.querySelectorAll("[data-screen]").forEach(node => { node.hidden = node.dataset.screen !== view; });
  document.querySelectorAll("[data-view]").forEach(node => {
    const active = node.dataset.view === view || view === "detail" && node.dataset.view === state.backView;
    node.classList.toggle("active", active); if (active) node.setAttribute("aria-current", "page"); else node.removeAttribute("aria-current");
  });
  byId("breadcrumb").textContent = labels[view]; document.title = `${labels[view]} · AirWave AP Monitor`;
  if (focus) byId("main").focus({ preventScroll:true });
}
async function show(view) {
  navigate(view);
  if (view === "dashboard") await loadDashboard();
  else if (view === "incidents") await loadIncidents();
  else if (view === "runs") await loadRuns();
  else if (view === "settings") await loadSettings(true);
}
async function currentDashboard() {
  state.historical = false; state.selection = null; state.snapshot = null; state.filters.page = 1;
  navigate("dashboard"); await loadDashboard(true);
}

async function loadDashboard(latest = false) {
  state.dashboardLoading = true;
  const ticket = gates.dashboard.begin(), querySelection = latest && !state.historical ? null : state.selection;
  const container = byId("ap-table"); container.setAttribute("aria-busy", "true"); byId("export-csv").disabled = true;
  try {
    const snapshot = await api(`/api/dashboard?${dashboardQuery(state.filters, querySelection)}`);
    if (!gates.dashboard.isCurrent(ticket) || state.view !== "dashboard") return;
    const pages = Math.max(1, Math.ceil(snapshot.totalRows / state.filters.pageSize));
    if (state.filters.page > pages) { state.filters.page = pages; state.selection = selectionOf(snapshot); setMessage("결과 범위에 맞춰 마지막 페이지로 이동했습니다."); await loadDashboard(); return; }
    state.snapshot = snapshot; state.selection = selectionOf(snapshot); state.lastDashboardRead = new Date(); renderDashboard(snapshot);
  } catch (error) {
    if (!gates.dashboard.isCurrent(ticket) || state.view !== "dashboard") return;
    state.snapshot = null; resetMetrics();
    const noRun = error.code === "no_final_run";
    notice(byId("dashboard-notice"), noRun ? "아직 확정된 AP 수집 회차가 없습니다" : "현황을 읽지 못했습니다", noRun ? "첫 수집이 완료되면 현황이 표시됩니다. 연결 설정과 수집기 상태를 확인하세요." : error.message, noRun ? "warning" : "error");
    empty(container, noRun ? "저장된 현황 없음" : "현재 조회 실패", "이 상태를 Down 0대 또는 전체 정상으로 표시하지 않습니다.");
    byId("list-count").textContent = ""; byId("dashboard-footnotes").replaceChildren(); updatePager("ap", 0, 1, state.filters.pageSize);
  } finally { if (gates.dashboard.isCurrent(ticket)) { container.removeAttribute("aria-busy"); state.dashboardLoading = false; } updateControls(); }
}
function resetMetrics() {
  ["metric-down","metric-new","metric-recovered","metric-unknown","tab-open","tab-new","tab-unknown","nav-count"].forEach(id => { byId(id).textContent = "—"; });
  byId("metric-open").textContent = "미복구 사건 확인 불가";
}
function renderDashboard(snapshot) {
  const focusedApId = document.activeElement?.dataset?.apId;
  const summary = snapshot.summary, failed = snapshot.outcome === "Failed" || snapshot.lifecycle === "Interrupted";
  byId("metric-down").textContent = summary.confirmedDown == null ? "미확인" : countText(summary.confirmedDown);
  byId("metric-new").textContent = countText(summary.newDown); byId("metric-recovered").textContent = countText(summary.recovered); byId("metric-unknown").textContent = countText(summary.unknown);
  byId("metric-open").textContent = `미복구 사건 ${countText(summary.openIncidents)}건`;
  byId("tab-open").textContent = countText(summary.openIncidents); byId("tab-new").textContent = countText(summary.newDown); byId("tab-unknown").textContent = countText(summary.unknown); byId("nav-count").textContent = countText(summary.openIncidents);
  const title = failed ? "이번 상태·전환 확인 불가" : snapshot.outcome === "Partial" ? "일부 AP 미확인" : "이번 수집 완료";
  const detail = failed ? `기존 미복구 ${countText(summary.openIncidents)}건 유지 · ${selectionText(snapshot)}` : `대상 ${countText(summary.targetCount)} · Up ${countText(summary.confirmedUp)} · Down ${countText(summary.confirmedDown)} · 미확인 ${countText(summary.unknown)} · ${selectionText(snapshot)}`;
  notice(byId("dashboard-notice"), title, detail, failed ? "error" : snapshot.outcome === "Partial" ? "warning" : "success");
  byId("history-banner").hidden = !state.historical; byId("history-label").textContent = `과거 회차 보기 · ${selectionText(snapshot)}`;
  byId("list-count").textContent = `· ${countText(snapshot.totalRows)}대`;
  document.querySelectorAll("[data-filter]").forEach(node => { const active = node.dataset.filter === state.filters.filter; node.classList.toggle("active", active); node.setAttribute("aria-pressed", String(active)); });
  byId("status-filter").value = state.filters.filter;
  if (!snapshot.rows.length) empty(byId("ap-table"), failed ? "이 회차에서 조건에 맞는 AP를 확인할 수 없습니다" : "조건에 맞는 AP가 없습니다", `${snapshot.hiddenPlannedCount ? `계획 작업 ${countText(snapshot.hiddenPlannedCount)}대가 숨겨져 있습니다. ` : ""}${failed ? "미복구 전체에서 보존된 사건을 확인하세요." : "검색이나 상태·관리 필터를 변경할 수 있습니다."}`);
  else buildTable(byId("ap-table"), ["AP 이름 / IP","위치","연결 원천 참조 / 포트","장애 감지 / 관측 경과","이번 확인"], snapshot.rows, (tr,row) => {
    const apButton = button(row.device.name || `AP ${row.sourceApId}`, () => showAp(row.apId, selectionOf(snapshot)), "ap-link"); apButton.dataset.apId = row.apId;
    cell(tr,"AP",apButton,row.device.ipAddress || "IP 미제공");
    const location = cell(tr,"위치",locationText(row.location), [row.location.folder,row.location.group].filter(Boolean).join(" · ") || "폴더·그룹 미제공");
    const tags = el("span","cell-badges"); if (row.manualManagement) tags.append(badge(managementLabels[row.manualManagement.status] || row.manualManagement.status,"blue")); if (row.sourceManagement?.maintenanceMode) tags.append(badge("원천 유지보수","amber")); location.append(tags);
    cell(tr,"연결 원천 참조",row.upstream?.sourceSwitchId || "스위치 참조 미제공", row.upstream?.portName || row.upstream?.portIndex || "포트 미제공");
    cell(tr,"장애 감지 / 관측 경과",row.firstDownAtUtc ? dateText(row.firstDownAtUtc) : "열린 사건 없음", row.firstDownAtUtc ? `${row.timeNeedsReview ? "시간 확인 필요" : durationText(incidentElapsed({ firstDownAtUtc:row.firstDownAtUtc }, snapshot.finalizedAtUtc))} · 회차 기준` : "—");
    const status = observationStatus(row), statusCell = cell(tr,"이번 확인",badge(status.label,status.tone), row.currentState == null ? `마지막 ${row.lastValidState || "상태 미확인"} · ${dateText(row.lastValidObservedAtUtc)}` : statusLabels[row.classification]);
    if (row.currentState == null && row.openIncidentId) statusCell.append(el("span","cell-sub","미복구 사건 유지"));
  });
  updatePager("ap",snapshot.totalRows,state.filters.page,state.filters.pageSize,snapshot.hiddenPlannedCount || 0);
  if (focusedApId) restoreRowFocus("apId",focusedApId);
  const notes = byId("dashboard-footnotes"); notes.replaceChildren();
  [`기준 목록: ${summary.baselineStatus === "Confirmed" ? "확인됨" : "잠정 · 사내 대수 대조 필요"}`,`최초 확인 Down ${countText(summary.initialDown)} · 기존 Down ${countText(summary.existingDown)}`,`감시 제외 ${countText(summary.excludedCount)}대`, `화면 조회 ${dateText(state.lastDashboardRead)}`].forEach(text => notes.append(el("span","",text)));
  (snapshot.errors || []).forEach(error => notes.append(el("span","amber",error.safeMessage)));
}
function locationText(location = {}) { return [location.site,location.building,location.floor,location.zone,location.detail].filter(Boolean).join(" · ") || location.airWaveRawLocation || "위치 미제공"; }

async function loadIncidents() {
  const ticket = gates.incidents.begin(), container = byId("incident-table");
  container.setAttribute("aria-busy", "true");
  try {
    const selection = state.selection || selectionOf(await api("/api/dashboard?pageSize=1&filter=OpenIncidents"));
    if (!gates.incidents.isCurrent(ticket) || state.view !== "incidents") return;
    state.selection = selection;
    const query = new URLSearchParams({ asOfRunId:selection.runId, scopeVersion:selection.scopeVersion, page:state.incident.page, pageSize:state.incident.pageSize });
    if (state.incident.status) query.set("status",state.incident.status);
    if (state.incident.search.trim()) query.set("search",state.incident.search.trim());
    const result = await api(`/api/incidents?${query}`);
    if (!gates.incidents.isCurrent(ticket) || state.view !== "incidents") return;
    const pages = Math.max(1,Math.ceil(result.totalRows / state.incident.pageSize));
    if (state.incident.page > pages) { state.incident.page = pages; await loadIncidents(); return; }
    notice(byId("incident-selection"),selectionText(result.selection),"이 회차의 대상 AP와 당시까지 저장된 사건·복구·관측 공백만 표시합니다.");
    byId("incident-count").textContent = `· ${countText(result.totalRows)}건 / AP ${countText(result.distinctApCount)}대`;
    if (!result.rows.length) empty(container,"조건에 맞는 사건이 없습니다","사건 상태나 검색 조건을 바꿀 수 있습니다. 다른 범위는 수집 기록에서 회차를 선택하세요.");
    else buildTable(container,["AP / 감지 당시 IP","사건 상태","Down 감지","복구·추적 종료","관측 경과"],result.rows,(tr,item) => {
      const metadata = item.detection.device.metadata;
      const incidentButton = button(metadata.device.name || `AP ${item.sourceApId}`,() => showIncident(item.incidentId,result.selection),"ap-link"); incidentButton.dataset.incidentId = item.incidentId;
      cell(tr,"AP",incidentButton,metadata.device.ipAddress || "IP 미제공");
      cell(tr,"사건 상태",badge(incidentLabels[item.status] || item.status,item.status === "Recovered" ? "green" : item.status === "Open" ? "red" : "amber"),item.tracking.isInitialDown ? "최초 확인 Down" : "Up → Down 관측");
      cell(tr,"Down 감지",dateText(item.tracking.firstDownAtUtc),"실제 시작 시각 미상");
      cell(tr,"복구·추적 종료",dateText(item.tracking.recoveredAtUtc || item.tracking.trackingEndedAtUtc),item.status === "Open" ? "미복구 · 사건 유지" : item.status === "Recovered" ? "Up 최초 관측" : "복구와 구분");
      cell(tr,"관측 경과",durationText(item.observedElapsedSeconds == null ? null : item.observedElapsedSeconds * 1000),item.tracking.hasObservationGap ? "관측 공백 포함" : "선택 회차 기준");
    });
    updatePager("incident",result.totalRows,state.incident.page,state.incident.pageSize);
  } catch (error) {
    if (!gates.incidents.isCurrent(ticket) || state.view !== "incidents") return;
    notice(byId("incident-selection"),error.code === "no_final_run" ? "아직 기준 수집 회차가 없습니다" : "이력을 읽지 못했습니다",error.message,"error");
    empty(container,"사건 조회 불가","이전 조회 결과를 현재 결과로 표시하지 않습니다."); byId("incident-count").textContent = ""; updatePager("incident",0,1,state.incident.pageSize);
  } finally { if (gates.incidents.isCurrent(ticket)) container.removeAttribute("aria-busy"); }
}
async function loadRuns() {
  const ticket = gates.runs.begin(), container = byId("run-table"); container.setAttribute("aria-busy","true");
  const query = new URLSearchParams({ page:state.runs.page, pageSize:state.runs.pageSize });
  if (state.runs.scopeVersion) query.set("scopeVersion",state.runs.scopeVersion);
  if (state.runs.throughRunId) query.set("throughRunId",state.runs.throughRunId);
  try {
    const result = await api(`/api/runs?${query}`);
    if (!gates.runs.isCurrent(ticket) || state.view !== "runs") return;
    state.runs.throughRunId = result.throughRunId;
    const scope = byId("run-scope"), options = [el("option","","모든 범위")]; options[0].value = "";
    result.availableScopes.forEach(number => { const option = el("option","",`범위 ${number}`); option.value = number; options.push(option); });
    scope.replaceChildren(...options); scope.value = state.runs.scopeVersion;
    if (!result.rows.length) empty(container,"저장된 수집 기록이 없습니다","수집이 시작되면 진행·성공·부분 확인·실패·중단 기록이 표시됩니다.");
    else buildTable(container,["회차 / 범위","시작 / 확정 시각","종류 / 결과","확인 대수","오류·검증 메모"],result.rows,(tr,row) => {
      const canOpen = row.kind === "ApStatus" && row.lifecycle !== "Running";
      const link = button(`회차 ${row.runId}`,() => selectRun(row,result.datasetId),"ap-link"); link.disabled = !canOpen;
      cell(tr,"회차 / 범위",link,`범위 ${row.scopeVersion} · ${triggerLabels[row.trigger] || row.trigger}`);
      cell(tr,"시작 / 확정 시각",dateText(row.startedAtUtc),row.finalizedAtUtc ? `확정 ${dateText(row.finalizedAtUtc)}` : "확정 전");
      const outcome = outcomeLabel(row); cell(tr,"종류 / 결과",badge(outcome.label,outcome.tone),row.kind === "ApStatus" ? "AP 상태" : `${row.kind} · 상세 현황 없음`);
      cell(tr,"확인 대수",row.summary ? `Up ${countText(row.summary.confirmedUp)} · Down ${countText(row.summary.confirmedDown)}` : "확정 대수 없음",row.summary ? `미확인 ${countText(row.summary.unknown)} · 미복구 ${countText(row.summary.openIncidents)}` : "진행 중 수치로 판정하지 않음");
      const errors = (row.errors || []).map(item => item.safeMessage).filter(Boolean);
      cell(tr,"오류·검증 메모",errors.join(" · ") || (row.completeness === "Complete" ? "응답 완전성 확인" : row.lifecycle === "Running" ? "수집 진행 중" : "완전성 미확인"),row.sourceEvidence ? `파싱 ${countText(row.sourceEvidence.parsedApCount)} · 적용 ${countText(row.sourceEvidence.appliedStateCount)} · 제외 원천 레코드 ${countText(row.sourceEvidence.excludedRecords?.length)}` : "원천 집계 없음");
    });
    updatePager("run",result.totalRows,state.runs.page,state.runs.pageSize);
  } catch (error) {
    if (!gates.runs.isCurrent(ticket) || state.view !== "runs") return;
    empty(container,"수집 기록을 읽지 못했습니다",error.message); updatePager("run",0,1,state.runs.pageSize);
  } finally { if (gates.runs.isCurrent(ticket)) container.removeAttribute("aria-busy"); }
}
async function selectRun(row,datasetId) {
  state.historical = true; state.selection = { ...selectionOf(row),datasetId }; state.snapshot = null; state.filters.page = 1;
  navigate("dashboard"); await loadDashboard();
}
function panel(title,wide = false) {
  const section = el("section",`panel padded${wide ? " wide" : ""}`); section.append(el("h2","",title)); return section;
}
function pairs(entries) {
  const dl = el("dl","pairs"); entries.forEach(([label,value]) => { dl.append(el("dt","",label)); const dd = el("dd"); if (value instanceof Node) dd.append(value); else dd.textContent = valueText(value); dl.append(dd); }); return dl;
}
function fieldValue(value,evidence) {
  const node = el("div"); node.append(el("span","",valueText(value)));
  const availability = { Provided:"제공됨",NotProvided:"미제공",Unsupported:"미지원",Unverified:"미확인" };
  const validity = { Valid:"유효",Invalid:"유효하지 않음",Unknown:"유효성 미확인" };
  const freshness = { Current:"수집 당시 최신",Delayed:"원천 지연",Unknown:"최신성 미확인" };
  node.append(el("small","evidence",evidence ? `${availability[evidence.availability] || "미확인"} · ${validity[evidence.validity] || "미확인"} · ${sourceLabels[evidence.source] || evidence.source} · ${freshness[evidence.freshness] || "최신성 미확인"}` : "출처·유효성 미확인"));
  if (evidence) node.append(el("small","evidence",`수집 ${dateText(evidence.collectedAtUtc)} · 원천 ${dateText(evidence.sourceAtUtc)}`));
  return node;
}
function metadataPanels(row) {
  const fields = row.fields || {}, general = panel("AP 정보"), location = panel("위치와 컨트롤러"), upstream = panel("연결 원천 참조"), quality = panel("원천 자료와 품질",true);
  general.append(pairs([["AP 원천 ID",row.sourceApId],["이름",fieldValue(row.device.name,fields.name)],["IP",fieldValue(row.device.ipAddress,fields.lan_ip)],["시리얼",fieldValue(row.identity.serialNumber,fields.serial_number)],["유선 MAC",fieldValue(row.identity.wiredMac,fields.lan_mac)],["모델",fieldValue(row.device.model,fields.model)],["펌웨어",fieldValue(row.device.firmware,fields.firmware)],["가동 시간 원문",fieldValue(row.rawSourceValues?.snmp_uptime,fields.snmp_uptime)],["재부팅 관측 시각",fieldValue(dateText(row.device.rebootedAtUtc),fields.last_reboot)]]));
  location.append(pairs([["구조화 위치",locationText({ ...row.location,airWaveRawLocation:null })],["AirWave 위치 원문",fieldValue(row.location.airWaveRawLocation,fields.syslocation)],["폴더",fieldValue(row.location.folder,fields.folder)],["그룹",fieldValue(row.location.group,fields.group)],["컨트롤러 원천 ID",fieldValue(row.controller.sourceId,fields.controller_id)],["컨트롤러 이름 / IP",[row.controller.name,row.controller.ipAddress].filter(Boolean).join(" / ") || "미제공"],["관리 상태",row.manualManagement ? `${managementLabels[row.manualManagement.status] || row.manualManagement.status} · ${row.manualManagement.reason} · ${dateText(row.manualManagement.recordedAtUtc)}` : "사용자 관리 상태 미등록"],["원천 유지보수",row.sourceManagement?.maintenanceMode == null ? "미제공" : row.sourceManagement.maintenanceMode ? "유지보수 표시 있음 · 사용자 계획 작업과 별도" : "유지보수 표시 없음"],["원천 관리 값",row.sourceManagement?.managementStatus]]));
  upstream.append(pairs([["스위치 원천 ID",fieldValue(row.upstream?.sourceSwitchId,row.upstream?.evidence || fields.upstream_device_id)],["포트 인덱스",fieldValue(row.upstream?.portIndex,fields.upstream_port_index)],["포트 이름",row.upstream?.portName],["AP 인터페이스",row.upstream?.apInterface],["LLDP / 현재 물리 링크","미확인 · AP 목록의 연결 참조만 제공"],["PoE 상태 / 전력 / 예산","미확인 · 저장된 검증 수치 없음"]]));
  const details = el("details"); details.append(el("summary","","필드별 출처·유효성·원문 보기"));
  Object.entries(fields).sort(([a],[b]) => a.localeCompare(b)).forEach(([key,evidence]) => { const item = el("div","source-field"); item.append(el("strong","",key),fieldValue(row.rawSourceValues?.[key],evidence)); details.append(item); });
  quality.append(pairs([["장비 세대",row.deviceGenerationId],["감시 구간",row.monitoringIntervalId],["원천 시각 (UTC 원문)",row.sourceAtUtc],["마지막 접촉 (UTC 원문)",row.lastContactedAtUtc]]),details);
  return [general,location,upstream,quality];
}
async function showAp(apId,selection) {
  if (!selection) return;
  if (state.view !== "detail") { state.backView = state.view; state.returnFocus = {key:"apId",value:apId}; }
  navigate("detail"); const ticket = gates.detail.begin(), container = byId("detail-content");
  empty(container,"AP 상세 조회 중",selectionText(selection)); container.setAttribute("aria-busy","true");
  try {
    const result = await api(`/api/aps/${encodeURIComponent(apId)}?${new URLSearchParams({runId:selection.runId,scopeVersion:selection.scopeVersion})}`);
    if (!gates.detail.isCurrent(ticket) || state.view !== "detail") return;
    const row = result.row, status = observationStatus(row), heading = el("div","detail-heading"), title = el("h1","",row.device.name || `AP ${row.sourceApId}`); title.id = "detail-title";
    heading.append(title,badge(status.label,status.tone));
    const meta = el("p","detail-meta",`${selectionText(result.selection)} · ${row.device.ipAddress || "IP 미제공"}`), statusPanel = panel("이번 관측과 미복구 사건",true);
    statusPanel.append(pairs([["이번 확인",statusLabels[row.classification] || status.label],["미확인 사유",row.currentState == null ? reasonLabels[row.unknownReason] || "원인 미확인" : "해당 없음"],["마지막 유효 관측",`${row.lastValidState || "미확인"} · ${dateText(row.lastValidObservedAtUtc)} · 회차 ${row.lastValidRunId ?? "미확인"}`],["열린 사건",row.openIncidentId ? "미복구 사건 유지" : "이 회차 기준 열린 사건 없음"],["시간 검토",row.timeNeedsReview ? "시계 역행 등으로 경과 시간 확인 필요" : "별도 시각 검토 표시 없음"]]));
    if (row.openIncidentId) statusPanel.append(button("미복구 사건 상세",() => showIncident(row.openIncidentId,result.selection)));
    const grid = el("div","detail-grid"); grid.append(statusPanel,...metadataPanels(row));
    const incidents = panel("장애·복구 사건",true);
    if (!result.incidents.length) incidents.append(el("p","footnote","선택 회차까지 이 장비 세대에 저장된 사건이 없습니다."));
    result.incidents.forEach(item => incidents.append(incidentSummary(item,result.selection)));
    if (result.moreIncidents) incidents.append(button("전체 사건 목록에서 보기",() => { state.selection = result.selection; state.incident.search = row.sourceApId; byId("incident-search").value = row.sourceApId; state.incident.page = 1; show("incidents"); }));
    const timeline = panel("수집 관측 타임라인",true), events = el("ol","timeline");
    result.observations.forEach(item => { const li = el("li"); li.append(el("strong","",`회차 ${item.runId} · ${dateText(item.finalizedAtUtc)}`),el("p","",`${statusLabels[item.classification] || "미확인"} · ${outcomeLabel(item).label}${item.unknownReason ? ` · ${reasonLabels[item.unknownReason] || item.unknownReason}` : ""}${item.timeNeedsReview ? " · 시간 확인 필요" : ""}`)); events.append(li); });
    if (!result.observations.length) events.append(el("li","","저장된 관측 없음")); timeline.append(events);
    if (result.moreObservations) timeline.append(el("p","footnote","최근 50개 관측을 표시합니다. 더 이전 관측은 수집 기록에서 해당 회차를 선택하세요."));
    const gaps = panel("관측 공백",true); appendGaps(gaps,result.observationGaps,result.moreObservationGaps);
    const notes = panel("조치 기록",true); notes.append(el("p","footnote","현재는 저장된 상태와 이력을 읽을 수 있습니다. 조치 메모·계획 작업 등록 기능은 아직 제공되지 않습니다."));
    grid.append(incidents,timeline,gaps,notes); container.replaceChildren(heading,meta,grid);
  } catch (error) { if (gates.detail.isCurrent(ticket) && state.view === "detail") empty(container,"AP 상세를 읽지 못했습니다",error.message); }
  finally { if (gates.detail.isCurrent(ticket)) container.removeAttribute("aria-busy"); }
}
function incidentSummary(item,selection) {
  const section = el("div","incident-summary"); section.append(button(`${incidentLabels[item.status] || item.status} · ${dateText(item.tracking.firstDownAtUtc)}`,() => showIncident(item.incidentId,selection),"text-button"),el("p","footnote",`${item.tracking.isInitialDown ? "최초 확인 Down · " : ""}관측 경과 ${durationText(item.observedElapsedSeconds == null ? null : item.observedElapsedSeconds * 1000)}${item.tracking.hasObservationGap ? " · 관측 공백 포함" : ""} · 실제 장애 시작 시각 미상`)); return section;
}
function appendGaps(container,gaps,more) {
  if (!gaps.length) { container.append(el("p","footnote","선택 회차까지 저장된 관측 공백이 없습니다.")); return; }
  const list = el("ol","timeline"); gaps.forEach(gap => { const li = el("li"); li.append(el("strong","",`${reasonLabels[gap.reason] || gap.reason} · ${dateText(gap.startedAtUtc)}`),el("p","",gap.resumedAtUtc ? `관측 재개 ${dateText(gap.resumedAtUtc)} · 복구 판정과 별도` : gap.trackingEndedAtUtc ? `추적 종료 ${dateText(gap.trackingEndedAtUtc)} · ${gap.trackingEndReason || "사유 미제공"}` : "선택 회차 기준 공백 지속")); list.append(li); }); container.append(list);
  if (more) container.append(el("p","footnote","최근 50개 공백을 표시합니다. 더 이전 정보는 과거 수집 회차에서 확인하세요."));
}
async function showIncident(incidentId,selection) {
  if (state.view !== "detail") { state.backView = state.view; state.returnFocus = {key:"incidentId",value:incidentId}; }
  navigate("detail"); const ticket = gates.detail.begin(), container = byId("detail-content"); empty(container,"사건 상세 조회 중",selectionText(selection)); container.setAttribute("aria-busy","true");
  try {
    const item = await api(`/api/incidents/${encodeURIComponent(incidentId)}?${new URLSearchParams({asOfRunId:selection.runId,scopeVersion:selection.scopeVersion})}`);
    if (!gates.detail.isCurrent(ticket) || state.view !== "detail") return;
    const heading = el("div","detail-heading"), title = el("h1","",`${item.detection.device.metadata.device.name || `AP ${item.sourceApId}`} · 장애 사건`); title.id = "detail-title"; heading.append(title,badge(incidentLabels[item.status] || item.status,item.status === "Recovered" ? "green" : "amber"));
    const meta = el("p","detail-meta",selectionText(selection)), grid = el("div","detail-grid"), tracking = panel("사건 관측 시각",true);
    tracking.append(pairs([["사건 ID",item.incidentId],["Down 감지",`${dateText(item.tracking.firstDownAtUtc)} · 회차 ${item.tracking.firstDownRunId}`],["직전 Up 관측",dateText(item.tracking.lastUpAtUtc)],["마지막 Down 관측",dateText(item.tracking.lastDownAtUtc)],["복구 확인",dateText(item.tracking.recoveredAtUtc)],["추적 종료",item.tracking.trackingEndedAtUtc ? `${dateText(item.tracking.trackingEndedAtUtc)} · ${item.tracking.trackingEndReason || "사유 미제공"}` : "해당 없음"],["관측 경과",durationText(item.observedElapsedSeconds == null ? null : item.observedElapsedSeconds * 1000)],["해석",`${item.tracking.isInitialDown ? "최초 확인부터 Down · " : ""}실제 장애 시작·복구 시각은 알 수 없습니다.${item.tracking.hasObservationGap ? " 관측 공백이 포함됩니다." : ""}${item.tracking.timeNeedsReview ? " 시계 역행 등으로 시간 확인이 필요합니다." : ""}`]])); grid.append(tracking);
    for (const [label,snapshot] of [["감지 당시 저장 정보",item.detection],["복구 당시 저장 정보",item.recovery]]) {
      const section = panel(label);
      if (!snapshot) section.append(el("p","footnote","선택 회차까지 복구 스냅샷이 없습니다."));
      else {
        const metadata = snapshot.device.metadata;
        section.append(pairs([["저장 회차",snapshot.runId],["저장 시각",dateText(snapshot.recordedAtUtc)],["이름 / IP",`${valueText(metadata.device.name)} / ${valueText(metadata.device.ipAddress)}`],["위치",locationText(metadata.location)],["시리얼 / MAC",`${valueText(metadata.identity.serialNumber)} / ${valueText(metadata.identity.wiredMac)}`],["연결 참조",`${valueText(metadata.upstream?.sourceSwitchId)} / ${valueText(metadata.upstream?.portName || metadata.upstream?.portIndex)}`],["장비 세대",snapshot.device.deviceGenerationId],["감시 구간",snapshot.device.monitoringIntervalId]]));
      } grid.append(section);
    }
    const gaps = panel("사건 중 관측 공백",true); appendGaps(gaps,item.observationGaps,item.moreObservationGaps); grid.append(gaps); container.replaceChildren(heading,meta,grid);
  } catch (error) { if (gates.detail.isCurrent(ticket) && state.view === "detail") empty(container,"사건을 읽지 못했습니다",error.message); }
  finally { if (gates.detail.isCurrent(ticket)) container.removeAttribute("aria-busy"); }
}

let healthInFlight = false;
async function loadHealth() {
  if (healthInFlight) return;
  healthInFlight = true; const ticket = gates.health.begin();
  try {
    const health = await api("/api/health");
    if (!gates.health.isCurrent(ticket)) return;
    const previousAuth = [state.health?.authenticationBlocked,state.health?.blockedReason === "credential-reregistration-required"];
    state.health = health.collector || null; state.maintenance = health.maintenance || null; state.mode = health.mode; renderMode(); renderHealth(state.health);
    const authChanged = previousAuth[0] !== state.health?.authenticationBlocked || previousAuth[1] !== (state.health?.blockedReason === "credential-reregistration-required");
    if (authChanged && state.settings) void loadSettings(false);
    if (shouldFollowLatest({ view:state.view,historical:state.historical,loading:state.dashboardLoading,runId:state.selection?.runId },state.health)) await loadDashboard(true);
  } catch (error) {
    if (!gates.health.isCurrent(ticket)) return;
    state.health = null; byId("collector-status").textContent = "수집기 상태 미확인"; byId("collector-status").className = "collector-badge amber";
    notice(byId("health-notice"),"수집기 상태 조회 실패",error.message,"error"); byId("health-details").replaceChildren(); byId("health-read-time").textContent = "· 마지막 조회 실패";
  } finally { healthInFlight = false; updateControls(); }
}
function renderMode() {
  byId("mode").textContent = state.mode === "demo" ? "DEMO · 가상 자료" : state.mode === "live" ? "LIVE 모드" : "모드 미확인";
  byId("mode").className = `badge ${state.mode === "demo" ? "blue" : ""}`;
  byId("mode-description").textContent = state.mode === "demo" ? "가상 XML을 사용하는 Demo 모드입니다. 실제 계정과 운영 데이터에 접근하지 않습니다." : state.mode === "live" ? "Windows 실행 계정에서 자격 증명을 등록하고 실제 연결 결과를 확인하세요." : "실행 모드를 확인하지 못했습니다.";
}
function renderHealth(health) {
  if (!health) { notice(byId("health-notice"),"수집기 상태 미제공","수집기 상태를 확인할 수 없습니다.","warning"); byId("collector-status").textContent = "수집기 미확인"; return; }
  const phases = { Initializing:"초기화 중",Idle:"수집 대기",Preparing:"수집 준비 중",Collecting:"수집 중",Finalizing:"저장 확정 중",StorageRecovery:"저장 복구 대기",Stopping:"종료 중",Stopped:"중지됨" };
  const blocked = health.authenticationBlocked || health.storageRecoveryPending || !health.configured;
  const label = health.storageRecoveryPending ? "저장 복구 대기" : health.authenticationBlocked ? "인증 중단" : health.paused ? "일시정지" : !health.configured ? "연결 설정 필요" : phases[health.phase] || "상태 미확인";
  byId("collector-status").textContent = label; byId("collector-status").className = `collector-badge ${blocked || health.updateDelayed || health.clockNeedsReview ? "amber" : ""}`;
  const maintenance = state.maintenance;
  const maintenanceErrors = [maintenance?.lastBackupError,maintenance?.lastRetentionError,maintenance?.lastBackupPruneError,maintenance?.lastLogError].filter(Boolean);
  const notes = [health.blockedReason,health.lastStorageError,health.lastDiagnosticFileError,...maintenanceErrors].filter(Boolean);
  if (health.updateDelayed) notes.push("최근 유효 수집 갱신 지연");
  if (health.clockNeedsReview) notes.push("시계 역행 또는 UTC 시각 검토 필요");
  if (health.lastOutcome === "Failed") notes.push(`최근 확정 수집 실패${health.lastCollectionError ? ` · ${health.lastCollectionError}` : ""}`);
  if (health.pendingApQueueId) notes.push("AP 수집 1건 대기 중");
  notice(byId("health-notice"),label,notes.join(" · ") || "수집은 서버에서 관리합니다. 브라우저의 새로고침은 저장된 결과만 조회합니다.",blocked || health.updateDelayed || health.clockNeedsReview || health.lastOutcome === "Failed" || maintenanceErrors.length ? "warning" : "");
  byId("health-read-time").textContent = `· ${dateText(health.recordedAtUtc)}`;
  const details = pairs([["진행 회차",health.activeRunId ? `회차 ${health.activeRunId} · ${kindLabels[health.activeKind] || health.activeKind}` : "없음"],["마지막 시도",`${dateText(health.lastAttemptAtUtc)}${health.lastAttemptRunId ? ` · 회차 ${health.lastAttemptRunId}` : ""}`],["마지막 유효 확정",dateText(health.lastValidCommittedAtUtc)],["마지막 완전 확정",dateText(health.lastCompleteCommittedAtUtc)],["다음 AP 수집",health.paused ? "일시정지" : dateText(health.nextApAtUtc)],["다음 상세 수집",health.nextEnrichmentAtUtc ? dateText(health.nextEnrichmentAtUtc) : "예정 없음"],["건너뛴 자동 시각",`AP ${countText(health.skippedApTicks)} · 상세 ${countText(health.skippedEnrichmentTicks)}`],["백업 상태",({initializing:"확인 중","not-yet-created":"첫 백업 대기",running:"백업 중",ready:"검증 완료",failed:"백업 실패"})[maintenance?.backupStatus] || "미확인"],["마지막 백업",dateText(maintenance?.lastBackupAtUtc)],["보존 처리",maintenance?.lastRetentionError || dateText(maintenance?.lastRetentionAtUtc)],["마지막 DB 복원",dateText(maintenance?.lastRestoreAtUtc)]]);
  const groups = [];
  while (details.firstChild) { const group = el("div"); group.append(details.firstChild); if (details.firstChild) group.append(details.firstChild); groups.push(group); }
  byId("health-details").replaceChildren(...groups);
}
function updateControls() {
  const health = state.health, stopping = health && ["Stopping","Stopped"].includes(health.phase);
  byId("collect-now").disabled = state.mutationBusy || !health || !health.configured || health.paused || health.authenticationBlocked || health.storageRecoveryPending || stopping;
  byId("pause-collector").disabled = state.mutationBusy || !health || stopping;
  byId("pause-collector").textContent = health?.paused ? "수집 재개" : "일시정지";
  byId("export-csv").disabled = state.mutationBusy || state.dashboardLoading || state.exportBusy || !state.snapshot;
  byId("save-settings").disabled = state.mutationBusy || !state.settings;
  const authentication = authenticationUiState(state.settings,health);
  byId("retry-authentication").disabled = state.mutationBusy || !authentication.canRetry;
  byId("authentication-status").textContent = authentication.label;
  byId("run-diagnostics").disabled = state.mutationBusy || !state.settings?.profile || state.settingsDirty || state.credentialsDirty;
  byId("credentials-form").querySelector("button").disabled = state.mutationBusy || !state.settings?.profile;
}
async function perform(action) {
  if (state.mutationBusy) return;
  state.mutationBusy = true; updateControls(); setMessage("");
  try { await action(); }
  catch (error) { setMessage(error.message,true); }
  finally { state.mutationBusy = false; updateControls(); }
}
async function collectNow() {
  await perform(async () => {
    const receipt = await api("/api/collector/requests",{method:"POST",body:{kind:"ApStatus",trigger:"Manual"}});
    setMessage(receipt.status === "Queued" ? "현재 작업 뒤 실행할 AP 수집 1건을 예약했습니다." : receipt.status === "Accepted" ? `AP 수집 요청을 접수했습니다${receipt.runId ? ` · 회차 ${receipt.runId}` : ""}. 결과는 수집 기록에서 확인하세요.` : receipt.message || `수집 요청 상태: ${receipt.status}`);
    await loadHealth();
  });
}
async function togglePause() {
  if (!state.health) return;
  const paused = !state.health.paused; gates.health.invalidate();
  await perform(async () => { state.health = await api("/api/collector/pause",{method:"POST",body:{paused}}); renderHealth(state.health); setMessage(paused ? "자동 수집을 일시정지했습니다. 진행 중인 수집은 저장 확정까지 계속됩니다." : "수집을 재개했습니다. AP 수집 요청 1건이 예약됩니다."); });
}
function invalidateDiagnostic(reason) {
  state.diagnosticIdentity = null; byId("diagnostic-result").hidden = true; byId("diagnostic-summary").textContent = "";
  ["diagnostic-details","diagnostic-stages","diagnostic-notes"].forEach(id => byId(id).replaceChildren());
  byId("diagnostic-validity").textContent = reason;
}
function invalidateDataForSettingsChange() {
  gates.dashboard.invalidate(); gates.incidents.invalidate(); gates.detail.invalidate();
  state.selection = null; state.snapshot = null; state.historical = false; state.filters.page = 1; state.incident.page = 1; resetMetrics();
  if (state.view === "dashboard") void loadDashboard(true);
  if (state.view === "incidents") void loadIncidents();
  if (state.view === "detail") empty(byId("detail-content"),"연결 설정이 변경되었습니다","현재 현황에서 새 기준 회차를 선택하세요.");
}
function applySettings(value,{fill = false} = {}) {
  const oldIdentity = state.settings && settingsIdentity(state.settings.profile), newIdentity = settingsIdentity(value.profile);
  if (oldIdentity && oldIdentity !== newIdentity) { invalidateDiagnostic("저장된 설정·자격 증명·범위가 변경되어 이전 진단은 유효하지 않습니다."); invalidateDataForSettingsChange(); }
  state.settings = value; state.mode = value.mode; renderMode();
  const profile = value.profile;
  if (fill && !state.settingsDirty) {
    byId("origin").value = profile?.origin || ""; byId("origin").readOnly = !!profile;
    byId("interval").value = profile?.settings.apIntervalMinutes || 5;
    byId("proxy-mode").value = ["System","Direct","Fixed"][profile?.proxy.mode || 0]; byId("proxy-address").value = profile?.proxy.address || "";
    byId("proxy-address").disabled = byId("proxy-mode").value !== "Fixed";
    state.draftBaseVersion = profile?.configurationVersion || 0;
  }
  byId("execution-account").textContent = value.executionAccount;
  byId("credential-status").textContent = ({ "synthetic-memory":"가상 메모리 자격 증명 · 실제 입력 안 받음","unsupported-platform":"현재 OS에서 Windows DPAPI 미지원 · Windows에서 등록 필요","missing":"미등록","registered":"현재 실행 계정에서 복호화 확인","reregistration-required":"복호화 실패 또는 재등록 필요" })[value.credentialStatus] || "상태 미확인";
  byId("credentials-form").hidden = state.mode !== "live" || value.credentialStatus === "unsupported-platform";
  updateControls();
}
async function loadSettings(fill = false) {
  const ticket = gates.settings.begin();
  try { const value = await api("/api/settings",{timeout:130000}); if (gates.settings.isCurrent(ticket)) applySettings(value,{fill}); }
  catch (error) { if (gates.settings.isCurrent(ticket)) { invalidateDiagnostic("설정 확인 실패 · 진단 유효성 미확인"); if (state.view === "settings") setMessage(error.message,true); } }
}
function draftChanged() {
  if (!state.settingsDirty) state.draftBaseVersion = state.settings?.profile?.configurationVersion || 0;
  state.settingsDirty = true; state.draftRevision++; invalidateDiagnostic("입력 내용이 변경되었습니다. 설정을 저장한 뒤 다시 진단하세요."); updateControls();
}
async function saveSettings() {
  const revision = state.draftRevision;
  const input = {origin:byId("origin").value,apIntervalMinutes:Number(byId("interval").value),proxyMode:byId("proxy-mode").value,proxyAddress:byId("proxy-mode").value === "Fixed" ? byId("proxy-address").value : null,expectedConfigurationVersion:state.draftBaseVersion ?? state.settings?.profile?.configurationVersion ?? 0};
  await perform(async () => {
    const value = await api("/api/settings",{method:"POST",body:input});
    gates.settings.invalidate(); if (revision === state.draftRevision) state.settingsDirty = false;
    applySettings(value,{fill:true}); invalidateDiagnostic("설정을 저장했습니다. 이 설정으로 연결 진단을 다시 실행하세요.");
    if (state.settingsDirty) state.draftBaseVersion = value.profile.configurationVersion;
    setMessage(state.settingsDirty ? "요청한 설정을 저장했습니다. 요청 이후 수정한 입력은 아직 저장하지 않았습니다." : "연결 설정을 저장했습니다. 실제 연결 성공 여부는 수집·진단 결과를 확인하세요."); void loadHealth();
  });
}
async function saveCredentials() {
  if (state.mutationBusy) return;
  const username = byId("username").value, password = byId("password").value, revision = state.draftRevision;
  byId("password").value = "";
  await perform(async () => {
    invalidateDiagnostic("자격 증명 등록 중 · 이전 진단 무효");
    const value = await api("/api/credentials",{method:"POST",body:{username,password}}); gates.settings.invalidate(); gates.health.invalidate(); state.health = null;
    if (revision === state.draftRevision) byId("username").value = "";
    state.credentialsDirty = !!(byId("username").value || byId("password").value);
    applySettings(value,{fill:!state.settingsDirty}); setMessage("실행 계정의 Windows DPAPI로 자격 증명을 등록했습니다. 연결 결과를 확인하세요."); void loadHealth();
  });
}
async function retryAuthentication() {
  if (!authenticationUiState(state.settings,state.health).canRetry) return;
  await perform(async () => {
    invalidateDiagnostic("명시적 인증 재시도 후 새 결과 확인 필요"); await api("/api/authentication/retry",{method:"POST"});
    gates.health.invalidate(); state.health = null;
    await loadSettings(false); await loadHealth(); setMessage("인증 중단 해제를 요청했습니다. 새 수집·진단 결과를 확인하세요.");
  });
}
async function runDiagnostics() {
  if (!state.settings?.profile || state.settingsDirty || state.credentialsDirty) return;
  const identity = settingsIdentity(state.settings.profile), revision = state.draftRevision;
  await perform(async () => {
    invalidateDiagnostic("진단 중 · 완료 후 설정 일치 여부 확인"); setMessage(state.mode === "demo" ? "합성 XML 단일 읽기를 진단하고 있습니다." : "연결 진단 중입니다. 서버의 전체 제한 시간은 120초입니다.");
    const report = await api("/api/diagnostics",{method:"POST",timeout:130000});
    const current = await api("/api/settings",{timeout:130000});
    if (revision !== state.draftRevision || identity !== settingsIdentity(current.profile) || identity !== settingsIdentity(state.settings?.profile)) {
      applySettings(current,{fill:false}); invalidateDiagnostic("진단 도중 설정 또는 입력이 변경되어 늦게 도착한 결과를 표시하지 않았습니다."); setMessage("설정을 저장·확인한 뒤 진단을 다시 실행하세요."); return;
    }
    applySettings(current,{fill:false}); state.diagnosticIdentity = identity; renderDiagnostic(report);
    byId("diagnostic-validity").textContent = `현재 저장 설정과 일치 · 설정 버전 ${current.profile.configurationVersion} · 자격 증명 버전 ${current.profile.credentialsVersion}`;
    setMessage("진단이 끝났습니다. 진단 결과는 운영 수집 회차·장애 이력으로 저장하지 않았습니다."); void loadHealth();
  });
}
function renderDiagnostic(report) {
  byId("diagnostic-result").hidden = false;
  byId("diagnostic-summary").textContent = `${state.mode === "demo" ? "가상 파서 진단" : "연결 진단"}: ${["읽기 성공","부분 유효 · 확인 필요","실패"][report.outcome] || "결과 미확인"}`;
  const tls = ({"not-applicable-demo":"해당 없음 · 가상 자료","validated":"HTTPS 연결 검증 통과","failed":"실패","not-observed":"미관측"})[report.tlsResult] || report.tlsResult;
  const route = ({"synthetic-memory":"가상 메모리","system-execution-account":"실행 계정의 시스템 프록시","direct":"직접 연결","fixed-proxy":"선택한 고정 프록시"})[report.connectionPath] || report.connectionPath;
  const details = pairs([["진단 ID",report.diagnosticId],["프로세스 실행 계정",report.executionAccount],["연결 경로",route],["TLS",tls],["HTTP_STATUS",report.httpStatus ?? "미수신"],["로그인 / AP 요청 수",`${report.loginRequests} / ${report.apRequests}`],["파싱 / 제외 레코드",`${report.parsedRecords} / ${report.excludedRecords}`],["소요 시간",`${report.elapsedMilliseconds} ms`]]); byId("diagnostic-details").replaceChildren(...details.childNodes);
  const stages = byId("diagnostic-stages"); stages.replaceChildren(); (report.stages || []).forEach(stage => stages.append(el("li","",`${stage.name} · ${stage.status} · ${stage.elapsedMilliseconds} ms · ${stage.message}`)));
  const notes = byId("diagnostic-notes"); notes.replaceChildren(); [...(report.errors || []).map(error => error.safeMessage),...(report.verificationNotes || [])].forEach(text => notes.append(el("li","",text)));
}
async function exportCsv() {
  if (!state.snapshot || state.dashboardLoading || state.exportBusy) return;
  const selection = selectionOf(state.snapshot), query = dashboardQuery(state.filters,selection,true);
  state.exportBusy = true; updateControls();
  const controller = new AbortController(), timer = setTimeout(() => controller.abort(),30000);
  try {
    const response = await fetch(`/api/export.csv?${query}`,{credentials:"same-origin",cache:"no-store",signal:controller.signal});
    if (!response.ok) { let payload; try { payload = await response.json(); } catch { payload = null; } throw new ApiError(response,payload); }
    if (response.headers.get("X-AirWave-Run-Id") !== String(selection.runId) || response.headers.get("X-AirWave-Scope-Version") !== String(selection.scopeVersion)) throw new Error("내보내기 응답의 기준 회차·범위가 일치하지 않습니다.");
    const blob = await response.blob();
    if (!state.snapshot || dashboardQuery(state.filters,selectionOf(state.snapshot),true).toString() !== query.toString()) throw new Error("내보내는 동안 조회 조건이 바뀌었습니다. 현재 조건에서 다시 내보내세요.");
    const url = URL.createObjectURL(blob), anchor = el("a"); anchor.href = url; anchor.download = `airwave-${state.mode === "demo" ? "demo" : "live"}-run-${selection.runId}.csv`; document.body.append(anchor); anchor.click(); anchor.remove(); setTimeout(() => URL.revokeObjectURL(url),1000);
    setMessage(`회차 ${selection.runId} · 범위 ${selection.scopeVersion}의 필터 결과 전체 ${response.headers.get("X-AirWave-Row-Count") || "미확인"}행을 내보냈습니다. 화면 페이지 제한은 적용하지 않았습니다.`);
  } catch (error) { setMessage(error.name === "AbortError" ? "CSV 조회 시간이 초과되었습니다. 다시 시도하세요." : error.message,true); }
  finally { clearTimeout(timer); state.exportBusy = false; updateControls(); }
}

function restoreRowFocus(key,value) {
  const rowButton = [...document.querySelectorAll(".ap-link")].find(node => node.dataset[key] === value);
  (rowButton || byId("main")).focus({preventScroll:true});
}
function filterChanged(filter) {
  clearTimeout(searchTimer); state.filters.filter = filter; state.filters.page = 1; void loadDashboard();
}
document.querySelectorAll("[data-view]").forEach(node => node.addEventListener("click",() => { clearTimeout(searchTimer); clearTimeout(incidentSearchTimer); setMessage(""); void show(node.dataset.view); }));
document.querySelectorAll("[data-action='current']").forEach(node => node.addEventListener("click",() => { clearTimeout(searchTimer); clearTimeout(incidentSearchTimer); setMessage(""); void currentDashboard(); }));
document.querySelectorAll("[data-filter]").forEach(node => node.addEventListener("click",() => filterChanged(node.dataset.filter)));
byId("status-filter").addEventListener("change",event => filterChanged(event.target.value));
byId("ap-search").addEventListener("input",event => {
  state.filters.search = event.target.value; state.filters.page = 1; gates.dashboard.invalidate(); state.dashboardLoading = true; updateControls();
  clearTimeout(searchTimer); searchTimer = setTimeout(() => { if (state.view === "dashboard") void loadDashboard(); },250);
});
byId("ap-sort").addEventListener("change",event => {
  const sorts = { recent:["FirstDownAt",true],oldest:["FirstDownAt",false],name:["Name",false],ip:["IpAddress",false],observed:["LastObservedAt",true] };
  [state.filters.sort,state.filters.descending] = sorts[event.target.value]; state.filters.page = 1; clearTimeout(searchTimer); void loadDashboard();
});
byId("management-filter").addEventListener("change",event => { state.filters.managementStatus = event.target.value; state.filters.page = 1; clearTimeout(searchTimer); void loadDashboard(); });
byId("hide-planned").addEventListener("change",event => { state.filters.hidePlanned = event.target.checked; state.filters.page = 1; clearTimeout(searchTimer); void loadDashboard(); });
byId("refresh-dashboard").addEventListener("click",() => { clearTimeout(searchTimer); void loadDashboard(!state.historical); });
byId("ap-prev").addEventListener("click",() => { state.filters.page = Math.max(1,state.filters.page - 1); void loadDashboard(); });
byId("ap-next").addEventListener("click",() => { state.filters.page++; void loadDashboard(); });
byId("incident-search").addEventListener("input",event => { state.incident.search = event.target.value; state.incident.page = 1; gates.incidents.invalidate(); clearTimeout(incidentSearchTimer); incidentSearchTimer = setTimeout(() => { if (state.view === "incidents") void loadIncidents(); },250); });
byId("incident-status").addEventListener("change",event => { state.incident.status = event.target.value; state.incident.page = 1; clearTimeout(incidentSearchTimer); void loadIncidents(); });
byId("incident-prev").addEventListener("click",() => { state.incident.page = Math.max(1,state.incident.page - 1); void loadIncidents(); });
byId("incident-next").addEventListener("click",() => { state.incident.page++; void loadIncidents(); });
byId("refresh-runs").addEventListener("click",() => { state.runs.page = 1; state.runs.throughRunId = null; void loadRuns(); });
byId("run-scope").addEventListener("change",event => { state.runs.scopeVersion = event.target.value; state.runs.page = 1; state.runs.throughRunId = null; void loadRuns(); });
byId("run-prev").addEventListener("click",() => { state.runs.page = Math.max(1,state.runs.page - 1); void loadRuns(); });
byId("run-next").addEventListener("click",() => { state.runs.page++; void loadRuns(); });
byId("detail-back").addEventListener("click",async () => { const target = state.returnFocus; await show(state.backView); if (state.view !== "detail" && target) restoreRowFocus(target.key,target.value); });
byId("collect-now").addEventListener("click",collectNow); byId("pause-collector").addEventListener("click",togglePause); byId("export-csv").addEventListener("click",exportCsv);
["origin","interval","proxy-mode","proxy-address"].forEach(id => byId(id).addEventListener("input",draftChanged));
byId("proxy-mode").addEventListener("change",() => { byId("proxy-address").disabled = byId("proxy-mode").value !== "Fixed"; });
["username","password"].forEach(id => byId(id).addEventListener("input",() => { state.draftRevision++; state.credentialsDirty = !!(byId("username").value || byId("password").value); invalidateDiagnostic("자격 증명 입력이 변경되었습니다. 등록을 마친 뒤 다시 진단하세요."); updateControls(); }));
byId("settings-form").addEventListener("submit",event => { event.preventDefault(); void saveSettings(); });
byId("credentials-form").addEventListener("submit",event => { event.preventDefault(); void saveCredentials(); });
byId("refresh-settings").addEventListener("click",() => { void loadSettings(true); if (state.settingsDirty) setMessage("서버 설정을 다시 읽습니다. 저장하지 않은 입력은 유지합니다."); });
byId("retry-authentication").addEventListener("click",retryAuthentication); byId("run-diagnostics").addEventListener("click",runDiagnostics);
document.addEventListener("visibilitychange",() => { if (!document.hidden) void loadHealth(); });

updateControls();
void loadDashboard(true); void loadHealth(); void loadSettings(true);
void ensureToken().catch(error => setMessage(error.message,true));
void api("/api/capabilities/windows").then(value => { byId("windows-detail").textContent = value.message; }).catch(() => { byId("windows-detail").textContent = "Windows 기능 정보를 읽지 못했습니다."; });
// This timer only reads health. Collection schedules and persistence belong to the server worker.
setInterval(() => { if (!document.hidden) void loadHealth(); },5000);
