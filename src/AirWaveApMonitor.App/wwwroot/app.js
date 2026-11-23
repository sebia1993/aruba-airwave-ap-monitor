"use strict";
import { countText, createRequestGate, dashboardQuery, observationStatus, incidentElapsed, settingsIdentity, shouldFollowLatest, authenticationUiState, kstDateText, kstPeriod, addHistoryFilters } from "./ui-state.js";

import { determineSetupStage, connectionSteps } from "./setup-guide.js";
import { summarizeCollectionWarnings } from "./run-warnings.js";
import { createOperationsController } from "./operations.js";
import { createEnrichmentController } from "./enrichment.js";

let operations = null, enrichment = null;
const byId = id => document.getElementById(id);
const labels = { dashboard: "AP 현황", incidents: "장애·작업 이력", runs: "수집 기록", settings: "연결 설정", detail: "상세 이력", operations: "AP·설치 대장", enrichment: "연결·전력" };
const statusLabels = { NewDown: "신규 Down", InitialDown: "최초 확인 Down · 실제 시작 미상", ExistingDown: "기존 Down", Recovered: "이번 복구 확인", Up: "Up 확인", Unknown: "이번 미확인" };
const reasonLabels = { MissingRecord: "응답 목록 누락", UnknownState: "유효 상태 미제공", DuplicateIdentity: "중복 식별 제외", CollectionFailed: "수집 실패", Interrupted: "수집 중단", ScopeExcluded: "범위 일시 제외", TrackingEnded: "추적 종료", DatabaseRestored: "백업 복원으로 관측 공백" };
const managementLabels = { Operating: "운영", PlannedWork: "계획 작업", Decommissioned: "철거", MonitoringExcluded: "감시 제외" };
const incidentLabels = { Open: "미복구", Recovered: "복구 확인", TrackingEnded: "추적 종료" };
const triggerLabels = { Scheduled:"예약",Manual:"수동",Startup:"시작",Resume:"재개",DatabaseRestore:"백업 복원 · 관측 공백" };
const kindLabels = { ApStatus:"AP 상태",ConnectionNeighbor:"연결·이웃",Power:"전력" };
const sourceLabels = { AirWave: "AirWave", Demo: "가상 자료", Manual: "직접 입력", Import: "가져오기" };
const state = {
  view: "dashboard", backView: "dashboard", historical: false, selection: null, snapshot: null,
  selectedApId: null, apTab: "status", apDetailSelection: null, apIncidentId: null, incidentMode: "cases",
  filters: { search: "", filter: "NeedsAttention", sort: "FirstDownAt", descending: true, managementStatus: "", hidePlanned: false, minimumOpenElapsedSeconds: "", page: 1, pageSize: 50 },
  incident: { status: "", search: "", fromUtc: null, toUtc: null, minimumOpenElapsedSeconds: "", page: 1, pageSize: 50 },
  runs: { kind: "", scopeVersion: "", fromUtc: null, toUtc: null, page: 1, pageSize: 50, throughRunId: null },
  health: null, maintenance: null, settings: null, mode: null, mutationBusy: false, settingsDirty: false, draftRevision: 0, pinDraftBase: null, pinProbe: null,
  scope: {data:null,dirty:false,revision:0,baseVersion:null,baselineDirty:false,baselineRevision:0,baselineBase:null},
  token: null, tokenPromise: null, diagnosticIdentity: null, lastDiagnosticReport: null, guideRequiresFreshDiagnostic: false,
  setupGuideOpen: false, setupAutoHandled: false, lastDashboardRead: null
};
const gates = { dashboard: createRequestGate(), incidents: createRequestGate(), runs: createRequestGate(), detail: createRequestGate(), settings: createRequestGate(), scope: createRequestGate(), health: createRequestGate() };
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
  return kstDateText(value);
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
async function api(path, { method = "GET", body, rawBody, contentType = "application/json", timeout = 15000 } = {}) {
  if (method !== "GET") await ensureToken();
  const controller = new AbortController(), timer = setTimeout(() => controller.abort(), timeout);
  try {
    const response = await fetch(path, { method, credentials:"same-origin", cache:"no-store", signal:controller.signal,
      headers: method === "GET" ? { Accept:"application/json" } : { Accept:"application/json", "Content-Type":contentType, "X-CSRF-TOKEN":state.token },
      body: rawBody ?? (body === undefined ? undefined : JSON.stringify(body)) });
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
    if (state.view === "dashboard") state.apDetailSelection = null;
    if (state.view === "operations" || state.view === "incidents") operations?.leave();
    if (view === "enrichment") state.enrichmentOrigin = state.view;
    if (state.view === "enrichment") enrichment?.leave();
    gates.dashboard.invalidate(); gates.incidents.invalidate(); gates.runs.invalidate(); gates.detail.invalidate(); state.dashboardLoading = false;
  }
  state.view = view;
  if (view === "enrichment") byId("return-enrichment").hidden = state.enrichmentOrigin !== "settings";
  document.querySelectorAll("[data-screen]").forEach(node => { node.hidden = node.dataset.screen !== view; });
  const parent = view === "detail" ? state.backView : view === "enrichment" ? state.enrichmentOrigin : view;
  byId("admin-nav").open = ["runs","operations","settings"].includes(parent);
  document.querySelectorAll("[data-view]").forEach(node => {
    const active = node.dataset.view === parent;
    node.classList.toggle("active", active); if (active) node.setAttribute("aria-current", "page"); else node.removeAttribute("aria-current");
  });
  byId("breadcrumb").textContent = labels[view]; document.title = `${labels[view]} · AirWave AP Monitor`;
  if (focus) byId("main").focus({ preventScroll:true });
}
async function show(view) {
  navigate(view);
  if (view === "dashboard") await loadDashboard();
  else if (view === "incidents") { setIncidentMode("cases"); await loadIncidents(); }
  else if (view === "runs") await loadRuns();
  else if (view === "settings") await Promise.all([loadSettings(true),loadScope()]);
  else if (view === "operations") await operations?.show("registration", null);
  else if (view === "enrichment") await enrichment?.show();
}
async function currentDashboard() {
  state.historical = false; state.selection = null; state.snapshot = null; state.filters.page = 1;
  navigate("dashboard"); await loadDashboard(true);
}

function editingIncidentMemo() {
  return state.apTab === "incident" && operations?.hasActiveIncidentDraft?.(state.selectedApId,state.apIncidentId);
}
async function loadDashboard(latest = false, automatic = false) {
  state.dashboardLoading = true;
  const ticket = gates.dashboard.begin(), querySelection = latest && !state.historical ? null : state.selection;
  const container = byId("ap-table"); container.setAttribute("aria-busy", "true"); byId("export-csv").disabled = true;
  try {
    const snapshot = await api(`/api/dashboard?${dashboardQuery(state.filters, querySelection)}`);
    if (!gates.dashboard.isCurrent(ticket) || state.view !== "dashboard") return;
    if (automatic && editingIncidentMemo()) return;
    const pages = Math.max(1, Math.ceil(snapshot.totalRows / state.filters.pageSize));
    if (state.filters.page > pages) { state.filters.page = pages; state.selection = selectionOf(snapshot); setMessage("결과 범위에 맞춰 마지막 페이지로 이동했습니다."); await loadDashboard(); return; }
    state.noFinalRun = false; state.snapshot = snapshot; state.selection = selectionOf(snapshot); state.lastDashboardRead = new Date(); renderDashboard(snapshot);
  } catch (error) {
    if (!gates.dashboard.isCurrent(ticket) || state.view !== "dashboard") return;
    if (automatic && editingIncidentMemo()) return;
    state.snapshot = null; clearApConsole(); resetMetrics();
    byId("dashboard-notice").hidden = false;
    const noRun = error.code === "no_final_run"; state.noFinalRun = noRun;
    notice(byId("dashboard-notice"), noRun ? "아직 확정된 AP 수집 회차가 없습니다" : "현황을 읽지 못했습니다", noRun ? "첫 수집이 저장되면 여기에 AP 목록이 표시됩니다." : error.message, noRun ? "warning" : "error");
    empty(container, noRun ? "저장된 현황 없음" : "현재 조회 실패", "이 상태를 Down 0대 또는 전체 정상으로 표시하지 않습니다.");
    byId("list-count").textContent = ""; byId("dashboard-footnotes").replaceChildren(); updatePager("ap", 0, 1, state.filters.pageSize);
  } finally { if (gates.dashboard.isCurrent(ticket)) { container.removeAttribute("aria-busy"); state.dashboardLoading = false; } updateControls(); maybeAutoStartGuide(); }
}
function setIncidentMode(mode) {
  const work = mode === "work";
  if (!work && state.incidentMode === "work") operations?.leave();
  if (work) gates.incidents.invalidate();
  state.incidentMode = mode;
  byId("incident-cases-pane").hidden = work;
  byId("incident-operations-content").hidden = !work;
  byId("incident-mode-cases").setAttribute("aria-pressed",String(!work));
  byId("incident-mode-work").setAttribute("aria-pressed",String(work));
}

async function openUnifiedRuns(kind) {
  if (kind !== undefined && kind !== null) {
    Object.assign(state.runs,{kind,page:1,throughRunId:null});
  }
  byId("run-kind").value = state.runs.kind;
  await show("runs");
}

async function selectConsoleAp(apId, sourceApId, selection, tab = "status", incidentId = null) {
  if (!selection || !apId) return;
  const inList = state.snapshot?.runId === selection.runId && state.snapshot.rows.some(row => row.apId === apId);
  state.selection = selection;
  if (!inList) {
    state.beforeApSearch = null;
    Object.assign(state.filters,{search:`id:${sourceApId}`,filter:"All",page:1,managementStatus:"",minimumOpenElapsedSeconds:"",hidePlanned:false});
    byId("ap-search").value = state.filters.search;
    byId("management-filter").value = ""; byId("ap-minimum-elapsed").value = ""; byId("hide-planned").checked = false;
  }
  state.selectedApId = apId; state.apTab = tab; state.apIncidentId = incidentId;
  state.apDetailSelection = null;
  navigate("dashboard");
  await loadDashboard();
}

function syncApPanel(snapshot) {
  if (!state.selectedApId) return;
  const selected = snapshot.rows.find(row => row.apId === state.selectedApId);
  if (!selected) { clearApConsole(); return; }
  const last = state.apDetailSelection;
  if (!sameApRun(last,snapshot)) {
    void showAp(selected.apId, selectionOf(snapshot), state.apTab, false, state.apIncidentId);
  }
}

const filterLabels = { NeedsAttention:"확인할 AP", Down:"장애 AP", Unknown:"미확인 AP", All:"전체 AP", Up:"정상 AP",
  OpenIncidents:"미복구 사건 AP", NewDown:"신규 Down", InitialDown:"최초 확인 Down", ExistingDown:"기존 Down", Recovered:"이번 복구" };
function resetMetrics() {
  for (const id of ["metric-total","metric-down","metric-unknown","nav-count"]) byId(id).textContent = "—";
}
function renderDashboard(snapshot) {
  const focusedApId = document.activeElement?.dataset?.apId;
  const summary = snapshot.summary, failed = snapshot.outcome === "Failed" || snapshot.lifecycle === "Interrupted";
  byId("metric-down").textContent = summary.confirmedDown == null ? "미확인" : countText(summary.confirmedDown) + "대";
  byId("metric-total").textContent = countText(summary.targetCount) + "대";
  byId("metric-unknown").textContent = countText(summary.unknown) + "대";
  byId("nav-count").textContent = countText(summary.openIncidents);
  const banner = byId("dashboard-notice");
  banner.hidden = !failed && snapshot.outcome !== "Partial";
  if (!banner.hidden) {
    if (failed) {
      notice(banner, snapshot.lifecycle === "Interrupted" ? "AP 수집 중단" : "AP 수집 실패",
        `이번 장애 대수 확인 불가 · 기존 미복구 사건 ${countText(summary.openIncidents)}건 · 회차 ${snapshot.runId}`,"error");
      const details = el("details","home-failure-detail");
      const reasons = summarizeCollectionWarnings(snapshot).labels;
      details.append(el("summary","","실패 원인 보기"),el("p","",reasons.join(" · ") || "저장된 상세 오류가 없습니다."));
      banner.append(details);
    } else notice(banner,"일부 AP 미확인",`미확인 ${countText(summary.unknown)}대 · 회차 ${snapshot.runId}`,"warning");
  }
  byId("history-banner").hidden = !state.historical; byId("history-label").textContent = `과거 회차 보기 · ${selectionText(snapshot)}`;
  const filters = state.filters, advanced = !!(filters.managementStatus || filters.minimumOpenElapsedSeconds || filters.hidePlanned);
  byId("ap-list-heading").textContent = filters.search.trim() && filters.filter === "All" ? "전체 AP 검색 결과" : filterLabels[filters.filter];
  byId("list-count").textContent = `· ${countText(snapshot.totalRows)}대`;
  byId("active-ap-conditions").textContent = advanced ? " · 조건 적용 중" : "";
  document.querySelectorAll("[data-filter]").forEach(node => { const active = node.dataset.filter === filters.filter; node.classList.toggle("active", active); node.setAttribute("aria-pressed", String(active)); });
  byId("status-filter").value = filters.filter;
  if (!snapshot.rows.length) {
    const clear = !failed && snapshot.outcome === "Succeeded" && summary.targetCount > 0 && summary.confirmedDown === 0
      && summary.unknown === 0 && filters.filter === "NeedsAttention" && !advanced && !filters.search.trim();
    empty(byId("ap-table"),clear ? "이 수집 회차에서 장애·미확인 AP가 없습니다" : failed ? "이번 회차의 AP 상태를 확인할 수 없습니다" : "조건에 맞는 AP가 없습니다",
      clear ? "AP 이름이나 IP로 개별 장비를 검색할 수 있습니다." : failed ? "장애·작업 이력에서 기존 미복구 사건을 확인하세요." : "검색어나 상세 필터를 변경하세요.");
  }
  else {
    const list = el("div","console-ap-rows");
    snapshot.rows.forEach(row => {
      const item = button("", () => {
        if (state.selectedApId === row.apId) return showAp(row.apId,selectionOf(snapshot),state.apTab,true,state.apIncidentId);
        const incident = row.currentState === "Down" ? row.openIncidentId : null;
        return showAp(row.apId,selectionOf(snapshot),incident ? "incident" : "status",true,incident);
      }, "ap-link console-ap-row");
      item.dataset.apId = row.apId;
      item.setAttribute("aria-pressed",String(row.apId === state.selectedApId));
      const identity = el("span","ap-row-identity");
      identity.append(el("strong","",displayed(row,"Name",row.device.name) || `AP ${row.sourceApId}`),
        el("small","",`${displayed(row,"IpAddress",row.device.ipAddress) || "IP 미제공"} · ${displayedLocation(row)}`));
      const status = observationStatus(row);
      item.append(identity,badge(status.label,status.tone));
      if (row.manualManagement) identity.append(el("small","",managementLabels[row.manualManagement.status] || row.manualManagement.status));
      list.append(item);
    });
    byId("ap-table").replaceChildren(list);
  }
  syncApPanel(snapshot);
  updatePager("ap",snapshot.totalRows,state.filters.page,state.filters.pageSize,snapshot.hiddenPlannedCount || 0);
  if (focusedApId) restoreRowFocus("apId",focusedApId);
  const notes = byId("dashboard-footnotes"); notes.replaceChildren();
  [`기준 목록: ${summary.baselineStatus === "Confirmed" ? "확인됨" : "잠정 · 사내 대수 대조 필요"}`,`최초 확인 Down ${countText(summary.initialDown)} · 기존 Down ${countText(summary.existingDown)}`,`감시 제외 ${countText(summary.excludedCount)}대`, `화면 조회 ${dateText(state.lastDashboardRead)}`].forEach(text => notes.append(el("span","",text)));
  if (!failed) {
    for (const message of summarizeCollectionWarnings(snapshot).labels) notes.append(el("span","amber",message));
  }
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
    addHistoryFilters(query,state.incident);
    const result = await api(`/api/incidents?${query}`);
    if (!gates.incidents.isCurrent(ticket) || state.view !== "incidents") return;
    const pages = Math.max(1,Math.ceil(result.totalRows / state.incident.pageSize));
    if (state.incident.page > pages) { state.incident.page = pages; await loadIncidents(); return; }
    notice(byId("incident-selection"),selectionText(result.selection),"이 회차의 대상 AP와 당시까지 저장된 사건·복구·관측 공백만 표시합니다.");
    byId("incident-count").textContent = `· 전체 사건 ${countText(result.totalRows)}건 · 고유 AP ${countText(result.distinctApCount)}대 · Up → Down ${countText(result.upToDownTransitionCount)}회 · 최초 확인 Down ${countText(result.initialDownCount)}건`;
    byId("incident-period-applied").textContent = periodText(state.incident,"Down 최초 감지 시각");
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
  if (state.runs.kind === "connectionpower") query.set("enrichmentOnly","true");
  else if (state.runs.kind) query.set("kind",state.runs.kind);
  if (state.runs.scopeVersion) query.set("scopeVersion",state.runs.scopeVersion);
  if (state.runs.throughRunId) query.set("throughRunId",state.runs.throughRunId);
  addHistoryFilters(query,state.runs);
  try {
    const result = await api(`/api/runs?${query}`);
    if (!gates.runs.isCurrent(ticket) || state.view !== "runs") return;
    state.runs.throughRunId = result.throughRunId;
    byId("run-period-applied").textContent = periodText(state.runs,"수집 시작 시각");
    const scope = byId("run-scope"), options = [el("option","","모든 범위")]; options[0].value = "";
    result.availableScopes.forEach(number => { const option = el("option","",`범위 ${number}`); option.value = number; options.push(option); });
    scope.replaceChildren(...options); scope.value = state.runs.scopeVersion;
    if (!result.rows.length) empty(container,"저장된 수집 기록이 없습니다","수집이 시작되면 진행·성공·부분 확인·실패·중단 기록이 표시됩니다.");
    else buildTable(container,["회차 / 범위","시작 / 확정 시각","종류 / 결과","확인 대수","오류·검증 메모"],result.rows,(tr,row) => {
      const canOpen = row.lifecycle !== "Running" && (row.kind === "ApStatus" || enrichment);
      const link = button(`회차 ${row.runId}`,() => row.kind === "ApStatus" ? selectRun(row,result.datasetId) : enrichment?.showRun(row.runId),"ap-link"); link.disabled = !canOpen;
      cell(tr,"회차 / 범위",link,`범위 ${row.scopeVersion} · ${triggerLabels[row.trigger] || row.trigger}`);
      cell(tr,"시작 / 확정 시각",dateText(row.startedAtUtc),row.finalizedAtUtc ? `확정 ${dateText(row.finalizedAtUtc)}` : "확정 전");
      const outcome = outcomeLabel(row); cell(tr,"종류 / 결과",badge(outcome.label,outcome.tone),row.kind === "ApStatus" ? "AP 상태" : kindLabels[row.kind] || row.kind);
      cell(tr,"확인 대수",row.kind !== "ApStatus" ? "종류별 상세에서 확인" : row.summary ? `Up ${countText(row.summary.confirmedUp)} · Down ${countText(row.summary.confirmedDown)}` : "확정 대수 없음",row.summary ? `미확인 ${countText(row.summary.unknown)} · 미복구 ${countText(row.summary.openIncidents)}` : row.kind !== "ApStatus" ? "연결·전력 관측" : "확정 대수 없음");
      const warnings = summarizeCollectionWarnings(row);
      const summary = warnings.labels.join(" · ");
      const errorCell = cell(tr,"오류·검증 메모",
        summary || (row.completeness === "Complete" ? "응답 완전성 확인" : row.lifecycle === "Running" ? "수집 진행 중" : "완전성 미확인"),
        row.sourceEvidence ? `파싱 ${countText(row.sourceEvidence.parsedApCount)} · 적용 ${countText(row.sourceEvidence.appliedStateCount)} · 제외 원천 레코드 ${countText(row.sourceEvidence.excludedRecords?.length)}` : "원천 집계 없음");
      if (warnings.exclusions.length) {
        const details = el("details","run-warning-breakdown"), list = el("ul");
        for (const group of warnings.exclusions) {
          const item=el("li","",`${group.label}: ${countText(group.count)}건`);
          if (group.sampleIds.length) item.append(el("small","cell-sub",`원천 장비 ID 예시: ${group.sampleIds.join(", ")}`));
          list.append(item);
        }
        details.append(el("summary","","제외 사유·표본 ID 보기"),list,
          el("p","footnote","제외 건수는 Down AP 대수가 아닙니다. 실제 AP 포함 여부는 AirWave에서 대조해야 합니다."));
        errorCell.append(details);
      }
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
function displayed(row, field, fallback) { return operations?.displayValue(row,field,fallback) ?? fallback; }
function displayedLocation(row) { return locationText({...row.location,...Object.fromEntries([["Site","site"],["Building","building"],["Floor","floor"],["Zone","zone"],["Detail","detail"],["RawLocation","airWaveRawLocation"]].map(([field,key])=>[key,displayed(row,field,row.location[key])]))}); }
function automaticInventoryPanels(row) {
  const fields=row.fields || {}, general=panel("자동 AP 정보"), location=panel("자동 위치와 컨트롤러");
  general.append(pairs([["AP 원천 ID",row.sourceApId],["이름",fieldValue(row.device.name,fields.name)],["IP",fieldValue(row.device.ipAddress,fields.lan_ip)],["시리얼",fieldValue(row.identity.serialNumber,fields.serial_number)],["유선 MAC",fieldValue(row.identity.wiredMac,fields.lan_mac)],["모델",fieldValue(row.device.model,fields.model)],["펌웨어",fieldValue(row.device.firmware,fields.firmware)],["가동 시간 원문",fieldValue(row.rawSourceValues?.snmp_uptime,fields.snmp_uptime)],["재부팅 관측 시각",fieldValue(dateText(row.device.rebootedAtUtc),fields.last_reboot)]]));
  location.append(pairs([["구조화 위치",locationText({ ...row.location,airWaveRawLocation:null })],["AirWave 위치 원문",fieldValue(row.location.airWaveRawLocation,fields.syslocation)],["폴더",fieldValue(row.location.folder,fields.folder)],["그룹",fieldValue(row.location.group,fields.group)],["컨트롤러 원천 ID",fieldValue(row.controller.sourceId,fields.controller_id)],["컨트롤러 이름 / IP",[row.controller.name,row.controller.ipAddress].filter(Boolean).join(" / ") || "미제공"],["관리 상태",row.manualManagement ? `${managementLabels[row.manualManagement.status] || row.manualManagement.status} · ${row.manualManagement.reason} · ${dateText(row.manualManagement.recordedAtUtc)}` : "사용자 관리 상태 미등록"],["원천 유지보수",row.sourceManagement?.maintenanceMode == null ? "미제공" : row.sourceManagement.maintenanceMode ? "유지보수 표시 있음 · 사용자 계획 작업과 별도" : "유지보수 표시 없음"],["원천 관리 값",row.sourceManagement?.managementStatus]]));
  return [general,location];
}
function apUpstream(row) {
  const fields=row.fields || {}, upstream=panel("자동 연결 원천 참조",true);
  upstream.append(pairs([["스위치 원천 ID",fieldValue(row.upstream?.sourceSwitchId,row.upstream?.evidence || fields.upstream_device_id)],["포트 인덱스",fieldValue(row.upstream?.portIndex,fields.upstream_port_index)],["포트 이름",row.upstream?.portName],["AP 인터페이스",row.upstream?.apInterface],["LLDP / 현재 물리 링크","AP 목록 참조는 LLDP 확인 근거가 아님 · 별도 저장 관측 참조"],["PoE 상태 / 전력 / 예산","별도 전력 관측 패널에서 측정 근거 확인"]]));
  return upstream;
}
function apSourceQuality(row) {
  const fields=row.fields || {}, quality=panel("원천 자료와 품질",true);
  const details = el("details"); details.append(el("summary","","필드별 출처·유효성·원문 보기"));
  Object.entries(fields).sort(([a],[b]) => a.localeCompare(b)).forEach(([key,evidence]) => { const item = el("div","source-field"); item.append(el("strong","",key),fieldValue(row.rawSourceValues?.[key],evidence)); details.append(item); });
  quality.append(pairs([["장비 세대",row.deviceGenerationId],["감시 구간",row.monitoringIntervalId],["원천 시각 (UTC 원문)",row.sourceAtUtc],["마지막 접촉 (UTC 원문)",row.lastContactedAtUtc]]),details);
  return quality;
}
const apTabs = {status:"상태",incident:"장애·조치",link:"연결·전력",inventory:"대장"};
let savedApDetail = null;
function sameApRun(selection, other) {
  return !!selection && !!other && selection.runId === other.runId && selection.scopeVersion === other.scopeVersion && selection.datasetId === other.datasetId;
}
function clearApConsole() {
  gates.detail.invalidate(); savedApDetail = null;
  state.selectedApId = null; state.apDetailSelection = null; state.apIncidentId = null; state.apTab = "status";
  const container = byId("ap-console-detail");
  if (container) { container.hidden = true; container.removeAttribute("aria-busy"); container.replaceChildren(); }
}
async function restoreApContext(context) {
  state.historical = context.historical; state.selection = context.selection;
  state.selectedApId = context.apId; state.apTab = context.returnTab; state.apIncidentId = context.incidentId || null;
  state.apDetailSelection = null; savedApDetail = null;
  navigate("dashboard"); await loadDashboard();
}
function apActionContext(result,incidentId = null) {
  const context = {apId:result.row.apId,deviceGenerationId:result.row.deviceGenerationId,incidentId,
    selection:result.selection,returnTab:state.apTab,historical:state.historical};
  if (state.historical) context.asOfRunId = result.selection.runId;
  context.returnTo = () => restoreApContext(context);
  return context;
}
function apObservations(result) {
  const section = panel("상태 관측 추이",true);
  if (!result.observations.length) { section.append(el("p","footnote","저장된 관측이 없습니다.")); return section; }
  const recent = result.observations.slice(0,24).reverse(), bars = el("div","ap-observations");
  bars.setAttribute("role","img");
  bars.setAttribute("aria-label",recent.map(item => `회차 ${item.runId} ${item.currentState || "미확인"}`).join(", "));
  recent.forEach(item => {
    const tone = item.currentState === "Up" ? "up" : item.currentState === "Down" ? "down" : "unknown";
    const bar = el("span",`ap-sample ${tone}`); bar.title = `회차 ${item.runId} · ${dateText(item.finalizedAtUtc)} · ${item.currentState || "미확인"}`; bars.append(bar);
  });
  const label = el("div","ap-strip-legend");
  label.append(el("span","",`회차 ${recent[0].runId} → ${recent.at(-1).runId}`),el("span","","위 Up · 아래 Down · 가운데 미확인"));
  section.append(bars,label);
  const detail = el("details","ap-observation-list"), events=el("ol","timeline");
  detail.append(el("summary","",`수집 관측 타임라인 · ${result.observations.length}회`));
  result.observations.forEach(item => {
    const li = el("li");
    li.append(el("strong","",`회차 ${item.runId} · ${dateText(item.finalizedAtUtc)}`),el("p","",`${statusLabels[item.classification] || "미확인"} · ${outcomeLabel(item).label}${item.unknownReason ? ` · ${reasonLabels[item.unknownReason] || item.unknownReason}` : ""}${item.timeNeedsReview ? " · 시간 확인 필요" : ""}`)); events.append(li);
  });
  detail.append(events);
  if (result.moreObservations) detail.append(el("p","footnote","최근 50개 관측을 표시합니다. 이전 관측은 수집 기록에서 과거 회차를 선택하세요."));
  section.append(detail); return section;
}
function apStatusPane(result, chooseIncident) {
  const row=result.row, statusPanel=panel("선택 회차의 AP 상태",true);
  statusPanel.append(pairs([["모델",displayed(row,"Model",row.device.model)],["이번 확인",statusLabels[row.classification] || observationStatus(row).label],
    ["마지막 유효 관측",`${row.lastValidState || "미확인"} · ${dateText(row.lastValidObservedAtUtc)} · 회차 ${row.lastValidRunId ?? "미확인"}`],
    ["미확인 사유",row.currentState == null ? reasonLabels[row.unknownReason] || "원인 미확인" : "해당 없음"],
    ["열린 사건",row.openIncidentId ? "미복구 사건 유지" : "이 회차 기준 열린 사건 없음"],
    ["시간 검토",row.timeNeedsReview ? "시계 역행 등으로 경과 시간 확인 필요" : "순서 이상 표시 없음"]]));
  if (row.openIncidentId) statusPanel.append(button("미복구 사건 보기",() => chooseIncident(row.openIncidentId),"ap-primary-action"));
  const gaps=panel("관측 공백",true); appendGaps(gaps,result.observationGaps,result.moreObservationGaps);
  return [statusPanel,apObservations(result),gaps,apSourceQuality(row)];
}
function apInventoryPane(result) {
  const row=result.row, nodes=automaticInventoryPanels(row);
  if (!operations) return nodes;
  const context=apActionContext(result), controls=el("div","actions ap-panel-actions");
  controls.append(button("현재 등록 정보 편집",() => operations.show("registration",context)),button("이 장비의 운영 이력",() => operations.show("timeline",context)));
  return [controls,operations.snapshotPanel(row),...nodes];
}
function apLinksPane(result) {
  const nodes=[apUpstream(result.row)];
  if (enrichment) {
    const context=apActionContext(result);
    nodes.push(enrichment.snapshotPanel(result.row.enrichmentDetail),
      enrichment.actions({...context,...result.selection}));
  }
  return nodes;
}
async function apCasePane(result, ticket, container) {
  const context=apActionContext(result), picker=el("details","ap-saved-evidence wide"), items=el("div","ap-cases");
  picker.append(el("summary","","이 AP의 다른 사건과 작업"),items);
  if (operations) {
    const controls=el("div","actions ap-panel-actions");
    controls.append(button("이 AP의 전체 운영 이력",() => operations.show("timeline",context)),
      button("사건과 별개인 AP 작업 기록",() => operations.writeWork(context)));
    picker.append(controls);
  }
  const rows=[...result.incidents], requested=state.apIncidentId;
  let selected=rows.find(item=>item.incidentId===requested);
  if (requested && !selected) {
    const query=new URLSearchParams({asOfRunId:result.selection.runId,scopeVersion:result.selection.scopeVersion});
    const exact=await api(`/api/incidents/${encodeURIComponent(requested)}?${query}`);
    if (!gates.detail.isCurrent(ticket)) return;
    const generation=exact.deviceGenerationId || exact.detection?.device.deviceGenerationId;
    if (exact.incidentId!==requested || exact.apId!==context.apId || generation!==context.deviceGenerationId) {
      picker.append(el("p","notice warning","선택한 사건은 이 회차의 AP·장비 세대에 속하지 않습니다."),
        button("선택한 사건 상세 보기",()=>showIncident(requested,result.selection)));
      picker.open=true; container.append(picker); return;
    }
    rows.unshift(exact); selected=exact;
  }
  if (!rows.length) {
    container.append(el("p","notice","이 장비의 장애 사건이 없습니다."),picker); return;
  }
  selected ||= rows.find(item=>item.incidentId===result.row.openIncidentId) || rows[0];
  state.apIncidentId=selected.incidentId;
  const choose = async id => {
    await showAp(result.row.apId,result.selection,"incident",false,id);
    if (state.view==="dashboard" && state.selectedApId===result.row.apId && state.apTab==="incident" && state.apIncidentId===id) {
      const choice=document.querySelector?.(`#ap-console-detail [data-incident-id="${id}"]`);
      const group=choice?.closest("details");
      if(group)group.open=true;
      choice?.focus({preventScroll:true});
    }
  };
  rows.forEach(item => {
    const node=button(`${incidentLabels[item.status] || item.status} · ${dateText(item.tracking.firstDownAtUtc)} · 회차 ${item.tracking.firstDownRunId}`,() => choose(item.incidentId),"tab");
    node.dataset.incidentId=item.incidentId;node.setAttribute("aria-pressed",String(item.incidentId===selected.incidentId));items.append(node);
  });
  if (result.moreIncidents) {
    const more=button("이 장비 세대의 이전 사건 더 보기",async () => {
      more.disabled=true;
      try {
        const params=new URLSearchParams({apId:context.apId,deviceGenerationId:context.deviceGenerationId,scopeVersion:result.selection.scopeVersion,
          asOfRunId:result.selection.runId,page:Math.floor(result.incidents.length/50)+1,pageSize:50});
        const page=await api(`/api/incidents?${params}`);
        if (!gates.detail.isCurrent(ticket)) return;
        result.incidents.push(...page.rows); result.moreIncidents=result.incidents.length<page.totalRows;
        await choose(selected.incidentId);
      } catch(error){ if(gates.detail.isCurrent(ticket)){more.disabled=false;setMessage(error.message,true);} }
    });
    picker.append(more);
  }
  const incidentContext=apActionContext(result,selected.incidentId);
  await renderIncidentEvidence(container,selected,incidentContext,ticket);
  if(gates.detail.isCurrent(ticket))container.append(picker);
}
async function showAp(apId,selection,tab="status",focusDetail=false,incidentId=null) {
  if (!selection || state.view!=="dashboard") return;
  const container=byId("ap-console-detail"), ticket=gates.detail.begin();
  if (!container) return;
  container.hidden=false;
  const keepSelection=state.selectedApId===apId && (!state.apDetailSelection || sameApRun(state.apDetailSelection,selection));
  if (incidentId) state.apIncidentId=incidentId;
  else if (!keepSelection) state.apIncidentId=null;
  state.selectedApId=apId; state.apDetailSelection=selection; state.apTab=Object.hasOwn(apTabs,tab)?tab:"status";
  for(const node of document.querySelectorAll("#ap-table [data-ap-id]")) node.setAttribute("aria-pressed",String(node.dataset.apId===apId));
  const cached=savedApDetail?.row.apId===apId && sameApRun(savedApDetail.selection,selection);
  if (!cached) empty(container,"AP 상세 조회 중",selectionText(selection));
  container.setAttribute("aria-busy","true");
  try {
    const result=cached?savedApDetail:await api(`/api/aps/${encodeURIComponent(apId)}?${new URLSearchParams({runId:selection.runId,scopeVersion:selection.scopeVersion})}`);
    if (!gates.detail.isCurrent(ticket)||state.view!=="dashboard"||!sameApRun(state.selection,selection)) return;
    savedApDetail=result;
    const row=result.row, status=observationStatus(row),heading=el("div","detail-heading"),title=el("h2","",displayed(row,"Name",row.device.name)||`AP ${row.sourceApId}`);
    title.tabIndex=-1; heading.append(title,badge(status.label,status.tone),button("상세 닫기",() => {
      clearApConsole();
      for (const item of document.querySelectorAll("#ap-table [data-ap-id]")) item.setAttribute("aria-pressed","false");
      const rowButton = [...document.querySelectorAll("#ap-table [data-ap-id]")].find(item => item.dataset.apId === apId);
      if (rowButton) rowButton.focus(); else byId("ap-search").focus();
    },"close-ap-detail"));
    const identity=el("div","ap-identity");
    identity.append(heading,el("p","detail-meta",`${displayedLocation(row)} · ${displayed(row,"IpAddress",row.device.ipAddress)||"IP 미제공"}`),
      el("p","footnote",selectionText(result.selection)));
    const choices=el("div","tabs ap-console-tabs");choices.setAttribute("role","group");choices.setAttribute("aria-label","AP 상세 보기");
    Object.entries(apTabs).forEach(([key,label])=>{
      const choice=button(label,async () => {
        if(state.apTab!==key) await showAp(apId,selection,key);
        if(state.view==="dashboard"&&state.selectedApId===apId&&state.apTab===key)
          document.querySelector?.(`#ap-console-detail [data-ap-tab="${key}"]`)?.focus({preventScroll:true});
      },"tab");
      choice.dataset.apTab=key;choice.setAttribute("aria-pressed",String(state.apTab===key));choices.append(choice);
    });
    const content=el("div","ap-console-content"); container.replaceChildren(identity,choices,content);
    if(focusDetail){title.focus({preventScroll:true});title.scrollIntoView?.({block:"nearest"});}
    if(state.apTab==="incident") await apCasePane(result,ticket,content);
    else if(state.apTab==="inventory") content.append(...apInventoryPane(result));
    else if(state.apTab==="link") content.append(...apLinksPane(result));
    else content.append(...apStatusPane(result,id=>{state.apIncidentId=id;return showAp(apId,selection,"incident",true);}));
  } catch(error) {
    if(gates.detail.isCurrent(ticket)&&state.view==="dashboard"){
      state.apDetailSelection=null; savedApDetail=null;
      empty(container,"AP 상세를 읽지 못했습니다",error.message);
      container.append(button("AP 상세 다시 읽기",()=>showAp(apId,selection,tab)));
    }
  } finally {if(gates.detail.isCurrent(ticket))container.removeAttribute("aria-busy");}
}
function appendGaps(container,gaps,more) {
  if (!gaps.length) { container.append(el("p","footnote","선택 회차까지 저장된 관측 공백이 없습니다.")); return; }
  const list = el("ol","timeline"); gaps.forEach(gap => { const li = el("li"); li.append(el("strong","",`${reasonLabels[gap.reason] || gap.reason} · ${dateText(gap.startedAtUtc)}`),el("p","",gap.resumedAtUtc ? `관측 재개 ${dateText(gap.resumedAtUtc)} · 복구 판정과 별도` : gap.trackingEndedAtUtc ? `추적 종료 ${dateText(gap.trackingEndedAtUtc)} · ${gap.trackingEndReason || "사유 미제공"}` : "선택 회차 기준 공백 지속")); list.append(li); }); container.append(list);
  if (more) container.append(el("p","footnote","최근 50개 공백을 표시합니다. 더 이전 정보는 과거 수집 회차에서 확인하세요."));
}
function incidentDetails(item, context) {
  const tracking = panel("사건 관측 시각",true);
  tracking.append(pairs([["사건 ID",item.incidentId],["Down 감지",`${dateText(item.tracking.firstDownAtUtc)} · 회차 ${item.tracking.firstDownRunId}`],["직전 Up 관측",dateText(item.tracking.lastUpAtUtc)],["마지막 Down 관측",dateText(item.tracking.lastDownAtUtc)],["복구 확인",dateText(item.tracking.recoveredAtUtc)],["추적 종료",item.tracking.trackingEndedAtUtc ? `${dateText(item.tracking.trackingEndedAtUtc)} · ${item.tracking.trackingEndReason || "사유 미제공"}` : "해당 없음"],["관측 경과",durationText(item.observedElapsedSeconds == null ? null : item.observedElapsedSeconds * 1000)],["해석",`${item.tracking.isInitialDown ? "최초 확인부터 Down · " : ""}첫 Down과 복구 시각은 수집기가 관측한 시각입니다.${item.tracking.hasObservationGap ? " 관측 공백이 포함됩니다." : ""}${item.tracking.timeNeedsReview ? " 시계 역행 등으로 시간 확인이 필요합니다." : ""}`]]));
  const saved=el("details","ap-saved-evidence wide");saved.append(el("summary","","감지·복구 당시 장비 정보"));
  for (const [label,snapshot] of [["감지 당시 저장 정보",item.detection],["복구 당시 저장 정보",item.recovery]]) {
    const section = panel(label);
    if (!snapshot) section.append(el("p","footnote","선택 회차까지 복구 스냅샷이 없습니다."));
    else {
      const metadata = snapshot.device.metadata;
      section.append(pairs([["저장 회차",snapshot.runId],["저장 시각",dateText(snapshot.recordedAtUtc)],["이름 / IP",`${valueText(metadata.device.name)} / ${valueText(metadata.device.ipAddress)}`],["위치",locationText(metadata.location)],["시리얼 / MAC",`${valueText(metadata.identity.serialNumber)} / ${valueText(metadata.identity.wiredMac)}`],["연결 참조",`${valueText(metadata.upstream?.sourceSwitchId)} / ${valueText(metadata.upstream?.portName || metadata.upstream?.portIndex)}`],["장비 세대",snapshot.device.deviceGenerationId],["감시 구간",snapshot.device.monitoringIntervalId]]));
    }
    saved.append(section);
    if (snapshot&&operations) saved.append(operations.snapshotPanel(snapshot.device.metadata));
    if (snapshot&&enrichment) {
      const connectionContext={...context,incidentId:null,runId:snapshot.runId,asOfRunId:snapshot.runId,
        scopeVersion:snapshot.scopeVersion,apId:snapshot.device.apId,deviceGenerationId:snapshot.device.deviceGenerationId};
      saved.append(enrichment.snapshotPanel(snapshot.device.enrichmentDetail,`${label} · 연결·전력`),enrichment.actions(connectionContext));
    }
  }
  const gaps = panel("사건 중 관측 공백",true); appendGaps(gaps,item.observationGaps,item.moreObservationGaps);
  return [tracking,gaps,saved];
}
async function renderIncidentEvidence(container,item,context,ticket) {
  const activity=el("div","ap-case-activity wide"), history=el("div","ap-case-history");
  const current=()=>gates.detail.isCurrent(ticket);
  const historyGate=createRequestGate();
  async function refreshHistory() {
    const read=historyGate.begin();
    try {
      const events=await operations.incidentEventsPanel(context);
      if(current() && historyGate.isCurrent(read))history.replaceChildren(events);
    }catch(error){
      if(current() && historyGate.isCurrent(read))history.replaceChildren(el("p","notice warning",`사건 운영 이력을 읽지 못했습니다. ${error.message}`));
    }
  }
  const incidentName=item.detection.device.metadata.device.name || `AP ${item.sourceApId}`;
  const summary=panel(`${incidentName} · ${incidentLabels[item.status]||item.status}`,true);
  summary.append(el("p","detail-meta",`장애 감지 ${dateText(item.tracking.firstDownAtUtc)}`));
  if(item.tracking.recoveredAtUtc)summary.append(el("p","detail-meta",`복구 확인 ${dateText(item.tracking.recoveredAtUtc)}`));
  else if(item.tracking.lastDownAtUtc)summary.append(el("p","detail-meta",`최근 Down 관측 ${dateText(item.tracking.lastDownAtUtc)}`));
  if(context.asOfRunId)summary.append(el("p","footnote",`선택 회차 ${context.asOfRunId}까지의 기록입니다.`));
  else if(operations && (item.status==="Open" || operations.hasSavedIncidentDraft(context)))
    summary.append(operations.incidentMemoForm(context,current,refreshHistory));
  const extra=el("details","ap-saved-evidence wide");
  extra.append(el("summary","","사건 상세와 추가 작업"));
  if(operations){
    if(context.asOfRunId)extra.append(el("p","footnote","후속 작업은 현재 시각으로 저장됩니다."));
    extra.append(button("이 사건에 작업·메모 기록",()=>operations.writeWork(context)));
  }
  extra.append(...incidentDetails(item,context));
  activity.append(summary,history);container.append(activity,extra);
  if(operations)await refreshHistory();
}
async function showIncident(incidentId,selection) {
  if(!selection) return;
  if(state.view!=="detail"&&state.view!=="operations"&&state.view!=="enrichment"){state.backView=state.view;state.returnFocus={key:"incidentId",value:incidentId};}
  navigate("detail"); const ticket=gates.detail.begin(),container=byId("detail-content");
  empty(container,"사건 상세 조회 중",selectionText(selection));container.setAttribute("aria-busy","true");
  try {
    const item=await api(`/api/incidents/${encodeURIComponent(incidentId)}?${new URLSearchParams({asOfRunId:selection.runId,scopeVersion:selection.scopeVersion})}`);
    if(!gates.detail.isCurrent(ticket)||state.view!=="detail")return;
    const heading=el("div","detail-heading"),title=el("h1","",`${item.detection.device.metadata.device.name || `AP ${item.sourceApId}`} · 장애 사건`);title.id="detail-title";
    heading.append(title,badge(incidentLabels[item.status]||item.status,item.status==="Recovered"?"green":item.status==="Open"?"red":"amber"));
    const context={apId:item.apId,deviceGenerationId:item.deviceGenerationId||item.detection.device.deviceGenerationId,incidentId:item.incidentId,
      selection,returnTab:"incident",historical:state.historical,returnTo:()=>showIncident(incidentId,selection)};
    if(state.historical)context.asOfRunId=selection.runId;
    const meta=el("p","detail-meta",selectionText(selection)),grid=el("div","detail-grid");
    const open=button("AP 통합 현황에서 보기",async()=>{
      await selectConsoleAp(item.apId,item.sourceApId,selection,"incident",item.incidentId);
    });
    container.replaceChildren(heading,meta,open,grid);
    await renderIncidentEvidence(grid,item,context,ticket);
  } catch(error){if(gates.detail.isCurrent(ticket)&&state.view==="detail")empty(container,"사건을 읽지 못했습니다",error.message);}
  finally{if(gates.detail.isCurrent(ticket))container.removeAttribute("aria-busy");}
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
    if (!editingIncidentMemo() && shouldFollowLatest({ view:state.view,historical:state.historical,loading:state.dashboardLoading,runId:state.selection?.runId },state.health)) await loadDashboard(true,true);
  } catch (error) {
    if (!gates.health.isCurrent(ticket)) return;
    state.health = null; renderHomeHealth(null,"수집기 상태 조회 실패"); enrichment?.renderHealth(null); byId("collector-status").textContent = "수집기 상태 미확인"; byId("collector-status").className = "collector-badge amber";
    notice(byId("health-notice"),"수집기 상태 조회 실패",error.message,"error"); byId("health-details").replaceChildren(); byId("health-read-time").textContent = "· 마지막 조회 실패";
  } finally { healthInFlight = false; updateControls(); maybeAutoStartGuide(); }
}
function renderMode() {
  byId("mode").textContent = state.mode === "demo" ? "DEMO · 가상 자료" : state.mode === "live" ? "LIVE 모드" : "모드 미확인";
  byId("mode").className = `badge ${state.mode === "demo" ? "blue" : ""}`;
  byId("mode-description").textContent = state.mode === "demo" ? "가상 XML을 사용하는 Demo 모드입니다. 실제 계정과 운영 데이터에 접근하지 않습니다." : state.mode === "live" ? "Windows 실행 계정에서 자격 증명을 등록하고 실제 연결 결과를 확인하세요." : "실행 모드를 확인하지 못했습니다.";
}
function renderHomeHealth(health,label) {
  if (health) state.lastValidAt = health.lastValidCommittedAtUtc || null;
  const parts = [label];
  if (health?.lastOutcome === "Failed") parts.push("최근 AP 수집 실패");
  if (health?.updateDelayed) parts.push("갱신 지연");
  if (health?.clockNeedsReview) parts.push("수집기 시각 확인 필요");
  parts.push(`마지막 유효 수집 ${state.lastValidAt ? dateText(state.lastValidAt) : "미확인"}`);
  const node = byId("home-health"), value = parts.join(" · ");
  if (node.textContent !== value) node.textContent = value;
}
function renderHealth(health) {
  enrichment?.renderHealth(health);
  if (!health) { renderHomeHealth(null,"수집기 상태 미확인"); notice(byId("health-notice"),"수집기 상태 미제공","수집기 상태를 확인할 수 없습니다.","warning"); byId("collector-status").textContent = "수집기 미확인"; return; }
  const phases = { Initializing:"초기화 중",Idle:"수집 대기",Preparing:"수집 준비 중",Collecting:"수집 중",Finalizing:"저장 확정 중",StorageRecovery:"저장 복구 대기",Stopping:"종료 중",Stopped:"중지됨" };
  const blocked = health.authenticationBlocked || health.storageRecoveryPending || !health.configured;
  const label = health.storageRecoveryPending ? "저장 복구 대기" : health.authenticationBlocked ? "인증 중단" : health.paused ? "일시정지" : !health.configured ? "연결 설정 필요" : phases[health.phase] || "상태 미확인";
  renderHomeHealth(health,health.phase === "Idle" && !blocked && !health.paused ? "자동 수집 대기" : label);
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
function connectionGuideState() {
  if (typeof determineSetupStage !== "function") return null;
  const identity = state.settings?.profile && settingsIdentity(state.settings.profile);
  return determineSetupStage({
    mode:state.mode,settings:state.settings,health:state.health,
    report:state.lastDiagnosticReport,diagnosticValid:!!identity && state.diagnosticIdentity === identity,
    settingsDirty:state.settingsDirty,credentialsDirty:state.credentialsDirty,
    pinDirty:state.pinDraftBase !== null,requireDiagnostic:state.guideRequiresFreshDiagnostic
  });
}
function renderConnectionGuide() {
  const panel=byId("setup-guide");
  if (!panel) return;
  panel.hidden=!state.setupGuideOpen;
  const toggle=byId("setup-guide-toggle");
  toggle.setAttribute("aria-expanded",String(state.setupGuideOpen));
  toggle.textContent=state.setupGuideOpen ? "연결 안내 닫기" : "처음 연결 안내";
  if (!state.setupGuideOpen) return;
  const result=connectionGuideState();
  if (!result) return;
  const steps=byId("setup-guide-steps"); steps.replaceChildren();
  connectionSteps.forEach((label,index)=>{
    const item=el("li","setup-step",String(index+1)+" · "+label);
    const status=index<result.number ? "done" : index===result.number ? "current" : "pending";
    item.dataset.status=status;
    if (status==="current") item.setAttribute("aria-current","step");
    steps.append(item);
  });
  byId("setup-guide-stage-title").textContent=result.title;
  byId("setup-guide-stage-description").textContent=result.description;
  const action=byId("setup-guide-action");
  action.textContent=result.button;
  action.disabled=state.mutationBusy || result.action==="none" ||
    (result.action==="collect" && (!state.health || !state.health.configured || state.health.paused || state.health.authenticationBlocked || state.health.storageRecoveryPending));
  byId("setup-guide-progress").textContent=result.key==="done" ? "완료 · 실제 저장 회차 확인" :
    result.key==="partial" ? "일부 확인 · 수집 기록 검토" : "안내 "+Math.min(5,result.number+1)+" / 5";
}
function focusGuideField(id) {
  const node=byId(id);
  if (!node) return;
  const parent=node.closest?.("details");
  if (parent) parent.open=true;
  node.scrollIntoView?.({block:"center",behavior:"smooth"});
  node.focus?.({preventScroll:true});
}
async function openConnectionGuide() {
  state.setupAutoHandled=true;
  state.setupGuideOpen=true;
  await show("settings");
  renderConnectionGuide();
  byId("setup-guide-stage-title").focus({preventScroll:true});
}
function maybeAutoStartGuide() {
  if (state.setupAutoHandled || state.view!=="dashboard" || state.noFinalRun!==true ||
      !state.health || !state.settings || state.mode!=="live") return;
  state.setupAutoHandled=true;
  if (state.settings.profile?.origin && state.settings.credentialStatus==="registered") return;
  state.setupGuideOpen=true;
  void show("settings");
}
async function connectionGuideAction() {
  if (state.mutationBusy) return;
  const step=connectionGuideState();
  if (!step) return;
  if (step.action==="jump") { focusGuideField(step.target); return; }
  if (step.action==="diagnose") { await runDiagnostics(); renderConnectionGuide(); return; }
  if (step.action==="collect") { await collectNow(); renderConnectionGuide(); return; }
  if (step.action==="resume") { await togglePause(); renderConnectionGuide(); return; }
  if (step.action==="records") { state.setupGuideOpen=false; await show("runs"); return; }
  if (step.action==="dashboard") { state.setupGuideOpen=false; await currentDashboard(); }
}

function updateControls() {
  const health = state.health, stopping = health && ["Stopping","Stopped"].includes(health.phase);
  const setupMissing = health?.configured === false || state.settings?.credentialRegistrationPending === true
    || state.settings?.credentialStatus === "reregistration-required";
  byId("home-connect-setup").hidden = !(state.noFinalRun && setupMissing && !health?.activeRunId && !health?.pendingApQueueId);
  byId("collect-now").disabled = state.mutationBusy || !health || !health.configured || health.paused || health.authenticationBlocked || health.storageRecoveryPending || stopping;
  byId("pause-collector").disabled = state.mutationBusy || !health || stopping;
  byId("pause-collector").textContent = health?.paused ? "수집 재개" : "일시정지";
  byId("export-csv").disabled = state.mutationBusy || state.dashboardLoading || state.exportBusy || !state.snapshot;
  byId("save-settings").disabled = state.mutationBusy || !state.settings;
  const authentication = authenticationUiState(state.settings,health);
  byId("retry-authentication").disabled = state.mutationBusy || !authentication.canRetry;
  byId("authentication-status").textContent = authentication.label;
  byId("run-diagnostics").disabled = state.mutationBusy || !state.settings?.profile || state.settingsDirty || state.credentialsDirty || state.pinDraftBase !== null;
  byId("credentials-form").querySelector("button").disabled = state.mutationBusy || !state.settings?.profile;
  updateScopeControls();
  updateCertificatePinControls();
  renderConnectionGuide();
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
  state.lastDiagnosticReport = null; state.guideRequiresFreshDiagnostic = true;
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
  if (oldIdentity && (oldIdentity !== newIdentity || state.mode !== value.mode)) {
    clearPinProbe();
    invalidateDiagnostic("저장된 설정·자격 증명·범위가 변경되어 이전 진단은 유효하지 않습니다.");
    if (!state.historical || state.selection?.datasetId !== value.profile?.datasetId) invalidateDataForSettingsChange();
  }
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
  updateControls(); maybeAutoStartGuide();
}
async function loadSettings(fill = false) {
  const ticket = gates.settings.begin();
  try { const value = await api("/api/settings",{timeout:130000}); if (gates.settings.isCurrent(ticket)) applySettings(value,{fill}); }
  catch (error) { if (gates.settings.isCurrent(ticket)) { invalidateDiagnostic("설정 확인 실패 · 진단 유효성 미확인"); if (state.view === "settings") setMessage(error.message,true); } }
}
function draftChanged() {
  clearPinProbe();
  if (!state.settingsDirty) state.draftBaseVersion = state.settings?.profile?.configurationVersion || 0;
  state.settingsDirty = true; state.draftRevision++; invalidateDiagnostic("입력 내용이 변경되었습니다. 설정을 저장한 뒤 다시 진단하세요."); updateControls();
}
function certificatePinTarget(settings) {
  if (settings?.mode !== "live" || !settings.profile?.origin) return null;
  try {
    const url = new URL(settings.profile.origin);
    if (url.protocol !== "https:" || url.username || url.password || url.pathname !== "/" || url.search || url.hash) return null;
    const ip = url.hostname;
    if (!/^\d+\.\d+\.\d+\.\d+$/.test(ip) && !(ip.startsWith("[") && ip.endsWith("]"))) return null;
    return `${ip}:${url.port || "443"}`;
  } catch { return null; }
}
function normalizedCertificatePin(field = "pin-fingerprint") {
  const value = byId(field).value.trim().toUpperCase();
  if (/^[A-F0-9]{64}$/.test(value)) return value;
  if (/^[A-F0-9]{2}(?:[:\s-][A-F0-9]{2}){31}$/.test(value)) return value.replace(/[:\s-]/g, "");
  return null;
}
const pinComparisons = {
  matched:"기존 등록 지문과 일치합니다. 무기한으로 사용하려면 해제할 때까지 허용을 선택하세요.",
  persistent:"기존 등록 지문과 일치하며, 해제할 때까지 허용 중입니다.",
  changed:"서버 인증서 변경 감지 · 등록 지문과 다릅니다. 관리 경로에서 교체 여부를 확인하세요.",
  unregistered:"등록된 지문이 없습니다. 관리 기록이나 인증서 원본에서 확인한 지문을 아래에 입력하세요.",
  expired:"기존 지문의 허용 기간이 지났습니다. 관리 기록이나 인증서 원본에서 지문을 다시 확인해 입력하세요."
};
function clearPinProbe() {
  state.pinProbe = null;
  byId("pin-independent").value = "";
}
function needsIndependentPin(probe) {
  return probe?.status === "unregistered" || probe?.status === "expired";
}
function canApplyPinProbe(probe) {
  if (probe?.status === "matched") return true;
  return needsIndependentPin(probe) && normalizedCertificatePin("pin-independent") === probe.sha256;
}
function updateCertificatePinControls() {
  const profile = state.settings?.profile;
  const target = certificatePinTarget(state.settings);
  const pin = profile?.temporaryCertificatePin;
  const editing = state.pinDraftBase !== null;
  const stale = editing && state.pinDraftBase !== settingsIdentity(profile);
  const disabled = !target || state.settingsDirty || state.mutationBusy;
  const indefinite = pin?.expiresAtUtc === null;
  const probe = state.pinProbe;
  const independent = needsIndependentPin(probe);
  byId("pin-observed").textContent = probe?.sha256 ?? "—";
  let comparison = probe ? pinComparisons[probe.status] : "";
  if (independent && byId("pin-independent").value.trim()) {
    const verified = normalizedCertificatePin("pin-independent");
    if (!verified) comparison = "관리 기록에서 확인한 64자리 SHA-256 지문을 입력하세요.";
    else comparison = verified === probe.sha256
      ? "입력한 지문이 조회 결과와 일치합니다. 해제할 때까지 허용을 선택하세요."
      : "입력한 지문이 조회한 인증서와 다릅니다. 관리 기록과 서버 인증서를 다시 확인하세요.";
  }
  byId("pin-comparison").textContent = comparison;
  byId("pin-comparison").hidden = !probe;
  byId("pin-independent-label").hidden = !independent;
  byId("pin-independent").disabled = disabled || editing || !independent;
  byId("pin-probe").disabled = disabled || editing;
  byId("pin-persistent").disabled = disabled || editing || !canApplyPinProbe(probe);
  byId("pin-target").textContent = target ?? "Live 모드에서 저장한 HTTPS IP 필요";
  byId("pin-state").textContent = !pin ? "꺼짐" : indefinite ? "사용 중 · 해제할 때까지" : Date.parse(pin.expiresAtUtc) > Date.now() ? "사용 중" : "만료";
  byId("pin-expiry").textContent = indefinite ? "자동 만료 없음" : pin ? dateText(pin.expiresAtUtc) : "—";
  byId("pin-current").textContent = pin?.sha256 || "—";
  byId("pin-fingerprint").disabled = disabled || stale;
  byId("pin-hours").disabled = disabled || stale;
  byId("pin-save").disabled = disabled || stale || !editing;
  byId("pin-save").textContent = pin ? "새 지문·기간으로 저장" : "지문으로 임시 허용";
  byId("pin-remove").disabled = disabled || editing || !pin;
  byId("pin-reset").hidden = !editing;
  byId("pin-reset").disabled = state.mutationBusy;
  byId("pin-draft-note").textContent = stale ? "입력 중에 저장된 설정이 변경됐습니다. 현재 대상과 등록값을 확인하고 입력 지우기로 다시 시작하세요."
    : editing ? "작성 중인 지문은 저장한 뒤 연결 진단과 다음 수집에 적용됩니다." : "";
}
function certificatePinEdited() {
  clearPinProbe();
  if (state.pinDraftBase === null) state.pinDraftBase = settingsIdentity(state.settings?.profile);
  state.draftRevision++;
  invalidateDiagnostic("임시 허용 입력이 변경됐습니다. 저장하거나 입력을 지운 뒤 다시 진단하세요.");
  updateControls();
}
function resetCertificatePinDraft() {
  state.pinDraftBase = null;
  state.draftRevision++;
  byId("pin-fingerprint").value = "";
  byId("pin-hours").value = "24";
  updateControls();
}
async function probeCertificatePin() {
  const profile = state.settings?.profile;
  if (!certificatePinTarget(state.settings) || state.settingsDirty || state.pinDraftBase !== null || state.mutationBusy) return;
  const identity = settingsIdentity(profile), revision = state.draftRevision;
  clearPinProbe();
  await perform(async () => {
    try {
      const probe = await api("/api/settings/certificate-pin/probe", {method:"POST",timeout:130000,
        body:{expectedConfigurationVersion:profile.configurationVersion}});
      if (identity !== settingsIdentity(state.settings?.profile) || revision !== state.draftRevision || state.settingsDirty)
        throw Error("조회 중 설정 또는 입력이 변경됐습니다. 현재 설정에서 인증서를 다시 조회하세요.");
      if (probe.configurationVersion !== profile.configurationVersion || new URL(probe.origin).href !== new URL(profile.origin).href ||
          !Object.hasOwn(pinComparisons,probe.status) || !/^[0-9A-F]{64}$/.test(probe.sha256))
        throw Error("인증서 조회 결과를 확인하지 못했습니다. 설정을 확인한 뒤 다시 조회하세요.");
      state.pinProbe = probe;
    } catch (error) {
      await loadSettings(false);
      throw error;
    }
  });
}
async function applyPersistentPin() {
  const profile = state.settings?.profile, probe = state.pinProbe;
  if (!certificatePinTarget(state.settings) || state.settingsDirty || state.mutationBusy ||
      state.pinDraftBase !== null || !canApplyPinProbe(probe) || profile.configurationVersion !== probe.configurationVersion) return;
  const independentlyVerifiedSha256 = needsIndependentPin(probe) ? normalizedCertificatePin("pin-independent") : null;
  await perform(async () => {
    try {
      const value = await api("/api/settings/certificate-pin/persistent", {method:"POST",timeout:130000,
        body:{expectedConfigurationVersion:profile.configurationVersion,observedSha256:probe.sha256,independentlyVerifiedSha256}});
      gates.settings.invalidate();
      clearPinProbe();
      applySettings(value);
      invalidateDiagnostic("등록 지문의 허용 기간이 변경됐습니다. 새 연결 진단을 실행하세요.");
      setMessage("현재 인증서 지문을 해제할 때까지 허용합니다.");
    } catch (error) {
      clearPinProbe();
      await loadSettings(false);
      throw error;
    }
  });
}
async function saveCertificatePin(clear = false) {
  const profile = state.settings?.profile;
  if (state.mutationBusy || state.settingsDirty || !certificatePinTarget(state.settings)) return;
  if (clear ? !profile.temporaryCertificatePin || state.pinDraftBase !== null : state.pinDraftBase !== settingsIdentity(profile)) return;
  const sha256 = clear ? null : normalizedCertificatePin();
  const durationHours = clear ? null : Number(byId("pin-hours").value);
  if (!clear && !sha256) { setMessage("인증서의 SHA-256 지문 64자리를 확인하세요. 두 자리마다 공백, 콜론 또는 하이픈을 사용할 수 있습니다.", true); return; }
  if (!clear && (!Number.isInteger(durationHours) || durationHours < 1 || durationHours > 168)) { setMessage("허용 시간은 1시간부터 168시간까지 정수로 입력하세요.", true); return; }
  const revision = state.draftRevision;
  await perform(async () => {
    try {
      const value = await api("/api/settings/certificate-pin", {method:"POST", timeout:130000, body:{ expectedConfigurationVersion:profile.configurationVersion, sha256, durationHours }});
      gates.settings.invalidate();
      if (revision === state.draftRevision) resetCertificatePinDraft();
      applySettings(value);
      invalidateDiagnostic("임시 허용 설정이 변경됐습니다. 새 연결 진단을 실행하세요.");
      setMessage(clear ? "인증서 지문 허용을 해제했습니다." : "지정한 IP와 포트의 인증서 지문을 등록했습니다. 연결 진단 실행으로 확인하세요.");
    } catch (error) {
      await loadSettings(false);
      throw error;
    }
  });
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
    void loadScope();
  });
}
function updateScopeControls() {
  const unavailable = state.mutationBusy || !state.scope.data;
  for (const id of ["scope-mode","scope-reason","scope-save","baseline-count","baseline-reason"])
    byId(id).disabled = unavailable;
  byId("scope-ids").disabled = unavailable || byId("scope-mode").value !== "SelectedApIds";
  byId("baseline-confirm").disabled = unavailable || !state.scope.baselineBase?.eligible;
  byId("scope-reload").disabled = byId("baseline-reload").disabled = state.mutationBusy;
}
function scopeDraftChanged(target) {
  if (target === "scope") { state.scope.dirty = true; state.scope.revision++; }
  else { state.scope.baselineDirty = true; state.scope.baselineRevision++; }
  updateScopeControls();
}
function applyScope(value,{resetScope = false,resetBaseline = false} = {}) {
  const saved = state.scope, profile = value.profile, run = value.comparisonRun;
  saved.data = value;
  if (resetScope) saved.dirty = false;
  if (resetBaseline) saved.baselineDirty = false;
  if (!saved.dirty) {
    saved.baseVersion = profile.configurationVersion;
    byId("scope-mode").value = profile.monitoringScope?.mode || "AllDiscovered";
    byId("scope-ids").value = (profile.monitoringScope?.sourceApIds || []).join("\n"); byId("scope-reason").value = "";
  }
  const currentRun = run && run.configurationVersion === profile.configurationVersion && run.scopeVersion === profile.scopeVersion;
  const allTargetsObserved = run && run.sourceApIds.length === run.targetCount;
  const eligible = currentRun && run.outcome !== "Failed" && run.targetCount > 0 && allTargetsObserved;
  if (!saved.baselineDirty) {
    saved.baselineBase = run ? {configurationVersion:profile.configurationVersion,runId:run.runId,targetCount:run.targetCount,eligible:!!eligible} : null;
    byId("baseline-count").value = ""; byId("baseline-reason").value = "";
    byId("baseline-targets").value = (run?.sourceApIds || []).join("\n");
  }
  byId("scope-current").textContent = `현재 설정 ${profile.configurationVersion} · 범위 ${profile.scopeVersion} · ${profile.monitoringScope?.mode === "SelectedApIds" ? `선택 AP ${(profile.monitoringScope.sourceApIds || []).length}대` : "발견한 전체 AP"} · 입력 기준 설정 ${saved.baseVersion}${saved.dirty ? " (저장하지 않은 입력 유지)" : ""}`;
  byId("scope-known-count").textContent = `유효 관측이 있었던 원천 AP ID ${countText(value.knownTargets.length)}개 보기`;
  byId("scope-known").value = value.knownTargets.map(item=>`${item.sourceApId} | ${item.name || "이름 미제공"} | ${item.ipAddress || "IP 미제공"} | 마지막 유효 회차 ${item.lastValidRunId}`).join("\n");
  byId("scope-baseline").textContent = !run ? "기준 대조용 확정 회차 없음 · 다음 수집 대기" : !currentRun ? "새 설정 관측 대기 · 이전 대조 기록을 현재 기준 확인으로 확장하지 않습니다." : `현재 범위 회차 ${run.runId} · ${run.targetCount}대 · ${run.baselineStatus === "Confirmed" ? "사용자 대조 기록 확인됨" : "잠정 기준 · 사용자 대조 미확인"} · ${run.outcome === "Failed" ? "수집 실패 · 대조 등록 불가" : !allTargetsObserved ? "미관측 대상 포함 · 대조 등록 불가" : "실제 API 권한·물리 재고 확인은 사용자 대조 필요"}`;
  const base = saved.baselineBase;
  byId("baseline-reference").textContent = base ? `이 입력의 대조 기준: 회차 ${base.runId} · 설정 ${base.configurationVersion} · 대상 ${base.targetCount}대${saved.baselineDirty ? " (작성 중인 대조 기준 유지)" : ""}` : "대조 기준 회차 없음";
  byId("scope-history").value = [...(profile.scopeRevisions || [])].reverse().map(item=>`${dateText(item.changedAtUtc)} · ${item.action === "BaselineConfirmed" ? "사용자 기준 대조" : "범위 변경"} · 설정 ${item.configurationVersion} / 범위 ${item.scopeVersion} · ${item.definition.mode === "AllDiscovered" ? "발견 전체" : "선택 ID: " + item.definition.sourceApIds.join(", ")} · ${item.targetCount}대${item.comparedRunId ? ` · 대조 회차 ${item.comparedRunId}` : ""} · ${item.reason}`).join("\n") || "저장된 범위 변경·대조 기록 없음";
  updateScopeControls();
}
async function loadScope(options = {}) {
  const ticket = gates.scope.begin(), revision = state.scope.revision, baselineRevision = state.scope.baselineRevision;
  try {
    const value = await api("/api/scope");
    if (gates.scope.isCurrent(ticket)) applyScope(value,{resetScope:options.resetScope && revision === state.scope.revision,resetBaseline:options.resetBaseline && baselineRevision === state.scope.baselineRevision});
  }
  catch (error) { if (gates.scope.isCurrent(ticket)) { byId("scope-current").textContent = `범위 조회 실패 · ${error.message}`; updateScopeControls(); } }
}
async function saveScope() {
  if (state.mutationBusy || !state.scope.data) return;
  let input;
  try {
    const mode = byId("scope-mode").value, sourceApIds = mode === "AllDiscovered" ? [] : byId("scope-ids").value.split(/\r?\n/).map(id=>id.trim()).filter(Boolean);
    if (!["AllDiscovered","SelectedApIds"].includes(mode) || mode === "SelectedApIds" && !sourceApIds.length) throw new Error("감시 모드와 원천 AP ID를 선택하세요.");
    if (new Set(sourceApIds).size !== sourceApIds.length) throw new Error("원천 AP ID가 중복되었습니다.");
    const known = new Set(state.scope.data.knownTargets.map(item=>item.sourceApId));
    if (sourceApIds.some(id=>!known.has(id))) throw new Error("유효 관측이 있었던 알려진 원천 AP ID만 선택할 수 있습니다.");
    const reason = byId("scope-reason").value.trim(); if (!reason) throw new Error("범위 변경 사유를 입력하세요.");
    input = {expectedConfigurationVersion:state.scope.baseVersion,mode,sourceApIds,reason};
  } catch (error) { setMessage(error.message,true); return; }
  const revision = state.scope.revision;
  await perform(async()=>{
    try {
      const value = await api("/api/scope",{method:"POST",body:input}); gates.scope.invalidate();
      if (revision === state.scope.revision) state.scope.dirty = false;
      applyScope(value); void loadSettings(false);
      setMessage("감시 범위를 저장했습니다. 다음 수집부터 적용하며 이전 회차는 보존됩니다. 새 기준은 사용자 대조 전까지 잠정입니다.");
    } catch (error) { if (error.status === 409) throw new Error("범위 저장 충돌 또는 수집 작업 중입니다. 입력을 유지했습니다. 서버 범위를 다시 읽고 재검토하세요."); throw error; }
  });
}
async function confirmBaseline() {
  if (state.mutationBusy || !state.scope.data) return;
  let input;
  try {
    const base = state.scope.baselineBase, rawCount = byId("baseline-count").value.trim(), count = Number(rawCount), reason = byId("baseline-reason").value.trim();
    if (!base?.eligible) throw new Error("현재 설정의 유효한 대조 회차를 먼저 수집하세요.");
    if (!rawCount || !Number.isSafeInteger(count) || count !== base.targetCount) throw new Error("직접 대조한 대상 대수를 입력하세요. 저장된 회차 대수와 일치해야 합니다.");
    if (!reason) throw new Error("대조 방법과 확인 근거를 입력하세요.");
    input = {expectedConfigurationVersion:base.configurationVersion,runId:base.runId,comparedTargetCount:count,reason};
  } catch (error) { setMessage(error.message,true); return; }
  const revision = state.scope.baselineRevision;
  await perform(async()=>{
    try {
      const value = await api("/api/scope/confirm",{method:"POST",body:input}); gates.scope.invalidate();
      if (revision === state.scope.baselineRevision) state.scope.baselineDirty = false;
      applyScope(value); void loadSettings(false);
      setMessage("사용자의 기준 대조 결과를 기록했습니다. 이전 회차의 기준 상태는 그대로 보존됩니다.");
    } catch (error) { if (error.status === 409) throw new Error("대조 회차·설정이 변경되었거나 수집 작업 중입니다. 입력을 유지했습니다. 대조 기준을 다시 읽고 재검토하세요."); throw error; }
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
  if (!state.settings?.profile || state.settingsDirty || state.credentialsDirty || state.pinDraftBase !== null) return;
  const identity = settingsIdentity(state.settings.profile), revision = state.draftRevision;
  await perform(async () => {
    invalidateDiagnostic("진단 중 · 완료 후 설정 일치 여부 확인"); setMessage(state.mode === "demo" ? "합성 XML 단일 읽기를 진단하고 있습니다." : "연결 진단 중입니다. 서버의 전체 제한 시간은 120초입니다.");
    const report = await api("/api/diagnostics",{method:"POST",timeout:130000});
    const current = await api("/api/settings",{timeout:130000});
    if (revision !== state.draftRevision || identity !== settingsIdentity(current.profile) || identity !== settingsIdentity(state.settings?.profile)) {
      applySettings(current,{fill:false}); invalidateDiagnostic("진단 도중 설정 또는 입력이 변경되어 늦게 도착한 결과를 표시하지 않았습니다."); setMessage("설정을 저장·확인한 뒤 진단을 다시 실행하세요."); return;
    }
    applySettings(current,{fill:false}); state.diagnosticIdentity = identity; state.lastDiagnosticReport = report; state.guideRequiresFreshDiagnostic = false; renderDiagnostic(report);
    byId("diagnostic-validity").textContent = `현재 저장 설정과 일치 · 설정 버전 ${current.profile.configurationVersion} · 자격 증명 버전 ${current.profile.credentialsVersion}`;
    setMessage("진단이 끝났습니다. 진단 결과는 운영 수집 회차·장애 이력으로 저장하지 않았습니다."); void loadHealth();
  });
}
function certificateDiagnosticDetails(checks) {
  const rows = [];
  const actions = new Set();
  if (!checks) return { rows, actions };

  const isProxy = checks.peer === "proxy";
  const prefix = isProxy ? "프록시 " : "";
  const peer = isProxy ? "프록시" : "AirWave";
  const names = new Map([["matched", "일치"], ["mismatch", "불일치"]]);
  const chains = new Map([["valid", "검증 통과"], ["failed", "검증 실패"]]);
  rows.push([prefix + "인증서 서버 이름", names.get(checks.serverName) ?? "미확인"],
    [prefix + "인증서 신뢰 체인", chains.get(checks.trustChain) ?? "미확인"]);
  if (checks.serverName === "mismatch") {
    actions.add(`${peer} 접속 주소가 인증서의 SAN에 등록된 DNS 이름 또는 IP와 일치하는지 확인하세요.`);
  }
  if (checks.trustChain === "failed") {
    const issuerSource = isProxy ? "프록시가" : "서버 또는 프록시가";
    const checkIssuers = `수집 계정에서 사내 루트 CA를 신뢰하는지, ${issuerSource} 중간 CA 인증서를 제공하는지 확인하세요.`;
    const checkTime = "수집 PC의 시각과 인증서 체인의 유효기간을 확인하세요.";
    const checkChain = "수집 계정에서 인증 경로의 오류 상태를 확인하세요.";
    const labels = [];
    const reasons = new Set(Array.isArray(checks.chainReasons) ? checks.chainReasons : []);
    for (const [code, label, action] of [
      ["untrusted-root", "신뢰할 수 없는 루트 CA", checkIssuers],
      ["partial-chain", "인증서 체인 불완전", checkIssuers],
      ["expired", "인증서 유효기간 만료", checkTime],
      ["not-yet-valid", "인증서 유효기간 시작 전", checkTime],
      ["time-invalid", "인증서 체인의 유효기간 오류", checkTime],
      ["other-chain-error", "기타 인증서 체인 오류", checkChain]
    ]) {
      if (reasons.has(code)) {
        labels.push(label);
        actions.add(action);
      }
    }
    if (labels.length) rows.push([prefix + "신뢰 체인 상세", labels.join(" · ")]);
    else actions.add(checkChain);
  }
  if (isProxy) rows.push(["AirWave 인증서", "미확인 · HTTPS 프록시 연결 단계에서 중단됨"]);
  return { rows, actions };
}
function renderDiagnostic(report) {
  byId("diagnostic-result").hidden = false;
  byId("diagnostic-summary").textContent = `${state.mode === "demo" ? "가상 파서 진단" : "연결 진단"}: ${["읽기 성공","부분 유효 · 확인 필요","실패"][report.outcome] || "결과 미확인"}`;
  const pinConnection = state.settings?.profile?.temporaryCertificatePin?.expiresAtUtc === null
    ? "무기한 허용 · 등록 지문으로 연결" : "임시 허용 · 등록 지문으로 연결";
  const tls = ({"not-applicable-demo":"해당 없음 · 가상 자료","validated":"HTTPS 연결 검증 통과","pinned-exception":pinConnection,"failed":"실패","not-observed":"미관측"})[report.tlsResult] || report.tlsResult;
  const route = ({"synthetic-memory":"가상 메모리","system-execution-account":"실행 계정의 시스템 프록시","direct":"직접 연결","fixed-proxy":"선택한 고정 프록시"})[report.connectionPath] || report.connectionPath;
  const tlsCodes = {
    "pin-mismatch":"서버 인증서 변경 감지 · 등록 지문 불일치",
    "pin-expired":"등록한 인증서 지문의 임시 허용 기간 만료",
    "pin-disallowed":"인증서 오류가 임시 허용 범위를 벗어남",
    "certificate-name-mismatch":"서버 인증서 이름 불일치",
    "certificate-expired":"인증서 기간 만료",
    "certificate-not-yet-valid":"인증서 유효기간 시작 전",
    "certificate-missing":"서버 인증서 미제공",
    "certificate-trust-chain":"신뢰 체인/사내 CA 확인 필요",
    "certificate-validation-failed":"인증서 검증 실패",
    "tls-handshake-or-certificate":"TLS 협상 또는 인증서 검증 실패"
  };
  const proxyHints = {
    "direct-selected":"설정상 직접 연결",
    "fixed-proxy-selected":"설정된 고정 프록시",
    "system-proxy-selected":"실행 계정의 기본 프록시 선택",
    "system-direct-selected":"실행 계정의 기본 프록시 우회/직접 연결 선택",
    "system-unresolved":"실행 계정의 기본 프록시 경로 판정 실패"
  };
  const detailsRows = [["진단 ID",report.diagnosticId],["프로세스 실행 계정",report.executionAccount],["연결 경로",route]];
  if (report.proxyRouteHint) detailsRows.push(["프록시 경로 추정",proxyHints[report.proxyRouteHint] || "확인 불가"]);
  detailsRows.push(["TLS",tls]);
  const checks = state.mode === "demo" ? null : report.tlsCertificateChecks;
  let cause;
  if (report.tlsResult === "failed" && !report.tlsFailureCode?.startsWith("pin-") && checks?.serverName === "mismatch" && checks.trustChain === "failed") {
    cause = "서버 인증서 이름 불일치 및 신뢰 체인 검증 실패";
  } else if (report.tlsFailureCode) {
    cause = Object.hasOwn(tlsCodes, report.tlsFailureCode) ? tlsCodes[report.tlsFailureCode] : "TLS 상세 원인 미확인";
  }
  if (cause) detailsRows.push(["TLS 실패 원인", checks?.peer === "proxy" ? `HTTPS 프록시 · ${cause}` : cause]);
  const certificate = certificateDiagnosticDetails(checks);
  detailsRows.push(...certificate.rows);
  detailsRows.push(["HTTP_STATUS",report.httpStatus ?? "미수신"],["로그인 / AP 요청 수",`${report.loginRequests} / ${report.apRequests}`],
    ["파싱 / 제외 레코드",`${report.parsedRecords} / ${report.excludedRecords}`],["소요 시간",`${report.elapsedMilliseconds} ms`]);
  const details = pairs(detailsRows); byId("diagnostic-details").replaceChildren(...details.childNodes);
  const stages = byId("diagnostic-stages"); stages.replaceChildren(); (report.stages || []).forEach(stage => stages.append(el("li","",`${stage.name} · ${stage.status} · ${stage.elapsedMilliseconds} ms · ${stage.message}`)));
  const notes = byId("diagnostic-notes"); notes.replaceChildren(); [...(report.errors || []).map(error => error.safeMessage),...certificate.actions,...(report.verificationNotes || []),...(report.proxyRouteHint?.startsWith("system-") ? ["프록시 경로는 .NET 기본 선택값을 나타내며 실제 네트워크 경유 성공을 보증하지 않습니다. Windows/PAC 및 수집기 실행 계정을 확인하세요."] : [])].forEach(text => notes.append(el("li","",text)));
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
function applySearch(value) {
  const filters = state.filters;
  if (value.trim()) {
    if (!filters.search.trim()) state.beforeApSearch = {filter:filters.filter,managementStatus:filters.managementStatus,
      hidePlanned:filters.hidePlanned,minimumOpenElapsedSeconds:filters.minimumOpenElapsedSeconds};
    Object.assign(filters,{filter:"All",managementStatus:"",hidePlanned:false,minimumOpenElapsedSeconds:""});
  } else if (filters.search.trim()) {
    Object.assign(filters,state.beforeApSearch || {filter:"NeedsAttention"});
    state.beforeApSearch = null;
  }
  filters.search = value; filters.page = 1;
  byId("status-filter").value = filters.filter; byId("management-filter").value = filters.managementStatus;
  byId("hide-planned").checked = filters.hidePlanned; byId("ap-minimum-elapsed").value = filters.minimumOpenElapsedSeconds;
}
function quickFilter(filter) {
  byId("ap-search").value = ""; state.beforeApSearch = null;
  Object.assign(state.filters,{search:"",managementStatus:"",minimumOpenElapsedSeconds:"",hidePlanned:false});
  byId("management-filter").value = ""; byId("ap-minimum-elapsed").value = ""; byId("hide-planned").checked = false;
  filterChanged(filter);
}
function filterChanged(filter) {
  clearTimeout(searchTimer); state.filters.filter = filter; state.filters.page = 1; void loadDashboard();
}
function periodText(filters, label) {
  return `적용 기간 · ${label}: ${filters.fromUtc ? dateText(filters.fromUtc) : "시작 제한 없음"} ~ ${filters.toUtc ? dateText(filters.toUtc) : "종료 제한 없음"} · 입력을 비우고 적용하면 기간 조건 해제`;
}
async function applyPeriod(target) {
  try {
    const period = kstPeriod(byId(`${target}-from`).value,byId(`${target}-to`).value);
    setMessage("");
    if (target === "incident") {
      clearTimeout(incidentSearchTimer); Object.assign(state.incident,period,{page:1});
      await loadIncidents();
    } else {
      Object.assign(state.runs,period,{page:1,throughRunId:null}); await loadRuns();
    }
  } catch (error) { setMessage(error.message,true); }
}
document.querySelectorAll("[data-view]").forEach(node => node.addEventListener("click",() => { state.setupAutoHandled = true; clearTimeout(searchTimer); clearTimeout(incidentSearchTimer); setMessage(""); void show(node.dataset.view); }));
operations = createOperationsController({
  api,onMessage:setMessage,dateText,isCurrentView:view=>state.view === view,
  navigate:view=>{ navigate(view); if (view === "incidents") setIncidentMode("work"); },
  onReturnAp:context=>restoreApContext(context)
});
enrichment = createEnrichmentController({api,onMessage:setMessage,navigate,dateText,isCurrentView:()=>state.view === "enrichment",onRuns:openUnifiedRuns,onHistory:selection=>operations?.show("timeline",{...selection,asOfRunId:selection.asOfRunId ?? (state.historical ? selection.runId : undefined)}),onSettingsSaved:()=>{void loadSettings(false);void loadScope();void loadHealth();}});
document.querySelectorAll("[data-action='current']").forEach(node => node.addEventListener("click",() => { clearTimeout(searchTimer); clearTimeout(incidentSearchTimer); setMessage(""); void currentDashboard(); }));
document.querySelectorAll("[data-filter]").forEach(node => node.addEventListener("click",() => quickFilter(node.dataset.filter)));
byId("home-connect-setup").addEventListener("click",()=>{ void openConnectionGuide(); });
byId("status-filter").addEventListener("change",event => filterChanged(event.target.value));
byId("ap-search").addEventListener("input",event => {
  applySearch(event.target.value); gates.dashboard.invalidate(); state.dashboardLoading = true; updateControls();
  clearTimeout(searchTimer); searchTimer = setTimeout(() => { if (state.view === "dashboard") void loadDashboard(); },250);
});
byId("ap-sort").addEventListener("change",event => {
  const sorts = { recent:["FirstDownAt",true],oldest:["FirstDownAt",false],name:["Name",false],ip:["IpAddress",false],observed:["LastObservedAt",true] };
  [state.filters.sort,state.filters.descending] = sorts[event.target.value]; state.filters.page = 1; clearTimeout(searchTimer); void loadDashboard();
});
byId("management-filter").addEventListener("change",event => { state.filters.managementStatus = event.target.value; state.filters.page = 1; clearTimeout(searchTimer); void loadDashboard(); });
byId("hide-planned").addEventListener("change",event => { state.filters.hidePlanned = event.target.checked; state.filters.page = 1; clearTimeout(searchTimer); void loadDashboard(); });
byId("ap-minimum-elapsed").addEventListener("change",event => { state.filters.minimumOpenElapsedSeconds = event.target.value; state.filters.page = 1; clearTimeout(searchTimer); void loadDashboard(); });
byId("refresh-dashboard").addEventListener("click",() => { clearTimeout(searchTimer); void loadDashboard(!state.historical); });
byId("ap-prev").addEventListener("click",() => { state.filters.page = Math.max(1,state.filters.page - 1); void loadDashboard(); });
byId("ap-next").addEventListener("click",() => { state.filters.page++; void loadDashboard(); });
byId("incident-search").addEventListener("input",event => { state.incident.search = event.target.value; state.incident.page = 1; gates.incidents.invalidate(); clearTimeout(incidentSearchTimer); incidentSearchTimer = setTimeout(() => { if (state.view === "incidents") void loadIncidents(); },250); });
byId("incident-status").addEventListener("change",event => { state.incident.status = event.target.value; state.incident.page = 1; clearTimeout(incidentSearchTimer); void loadIncidents(); });
byId("incident-minimum-elapsed").addEventListener("change",event => { state.incident.minimumOpenElapsedSeconds = event.target.value; state.incident.page = 1; clearTimeout(incidentSearchTimer); void loadIncidents(); });
byId("incident-period-form").addEventListener("submit",event => { event.preventDefault(); void applyPeriod("incident"); });
byId("run-period-form").addEventListener("submit",event => { event.preventDefault(); void applyPeriod("run"); });
byId("incident-prev").addEventListener("click",() => { state.incident.page = Math.max(1,state.incident.page - 1); void loadIncidents(); });
byId("incident-next").addEventListener("click",() => { state.incident.page++; void loadIncidents(); });
byId("refresh-runs").addEventListener("click",() => { state.runs.page = 1; state.runs.throughRunId = null; void loadRuns(); });
byId("run-kind").addEventListener("change",event=>{ void openUnifiedRuns(event.target.value); });
byId("incident-mode-cases").addEventListener("click",()=>{ setIncidentMode("cases"); void loadIncidents(); });
byId("incident-mode-work").addEventListener("click",()=>{ setIncidentMode("work"); void operations?.show("timeline",null); });
byId("enrichment-settings-open").addEventListener("click",()=>{ void enrichment?.show("overview",null); });
byId("return-enrichment").addEventListener("click",()=>{ void show("settings"); });
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
byId("pin-probe").addEventListener("click",() => { void probeCertificatePin(); });
byId("pin-persistent").addEventListener("click",() => { void applyPersistentPin(); });
byId("pin-independent").addEventListener("input",updateCertificatePinControls);
["pin-fingerprint","pin-hours"].forEach(id => byId(id).addEventListener("input",certificatePinEdited));
byId("pin-form").addEventListener("submit",event => { event.preventDefault(); void saveCertificatePin(); });
byId("pin-remove").addEventListener("click",() => { void saveCertificatePin(true); });
byId("pin-reset").addEventListener("click",resetCertificatePinDraft);
byId("refresh-settings").addEventListener("click",() => { void loadSettings(true); void loadScope(); if (state.settingsDirty) setMessage("서버 설정을 다시 읽습니다. 저장하지 않은 입력은 유지합니다."); });
byId("setup-guide-toggle").addEventListener("click",() => { if (state.setupGuideOpen) { state.setupGuideOpen = false; renderConnectionGuide(); } else { void openConnectionGuide(); } });
byId("setup-guide-close").addEventListener("click",() => { state.setupGuideOpen = false; renderConnectionGuide(); byId("setup-guide-toggle").focus(); });
byId("setup-guide-action").addEventListener("click",() => { void connectionGuideAction(); });
["scope-mode","scope-ids","scope-reason"].forEach(id=>byId(id).addEventListener("input",()=>scopeDraftChanged("scope")));
byId("scope-mode").addEventListener("change",()=>scopeDraftChanged("scope"));
["baseline-count","baseline-reason"].forEach(id=>byId(id).addEventListener("input",()=>scopeDraftChanged("baseline")));
byId("scope-form").addEventListener("submit",event=>{event.preventDefault();void saveScope();});
byId("baseline-form").addEventListener("submit",event=>{event.preventDefault();void confirmBaseline();});
byId("scope-reload").addEventListener("click",()=>{void loadScope({resetScope:true});});
byId("baseline-reload").addEventListener("click",()=>{void loadScope({resetBaseline:true});});
byId("retry-authentication").addEventListener("click",retryAuthentication); byId("run-diagnostics").addEventListener("click",runDiagnostics);
document.addEventListener("visibilitychange",() => { if (!document.hidden) void loadHealth(); });

updateControls();
void loadDashboard(true); void loadHealth(); void loadSettings(true);
void ensureToken().catch(error => setMessage(error.message,true));
void api("/api/capabilities/windows").then(value => { byId("windows-detail").textContent = value.message; }).catch(() => { byId("windows-detail").textContent = "Windows 기능 정보를 읽지 못했습니다."; });
// This timer only reads health. Collection schedules and persistence belong to the server worker.
setInterval(() => { if (!document.hidden) void loadHealth(); },5000);
