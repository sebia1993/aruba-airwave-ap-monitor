import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { runInNewContext } from "node:vm";

// Pure production helpers plus focused production-function DOM stubs. No browser or dependencies.
const moduleUrl = new URL("../../src/AirWaveApMonitor.App/wwwroot/ui-state.js", import.meta.url);
const ui = await import(`${moduleUrl.href}?check=${Date.now()}`);
// These focused stubs execute the actual production functions, not a browser layout or DOM engine.
class StubNode {
  constructor(tag = "div") { this.tagName = tag; this.childNodes = []; this.dataset = {}; this.value = ""; this._text = ""; }
  get firstChild() { return this.childNodes[0] || null; }
  get textContent() { return this._text + this.childNodes.map(node => node.textContent).join(""); }
  set textContent(value) { this._text = String(value); this.childNodes = []; }
  append(...nodes) {
    for (const node of nodes) {
      if (node.parentNode) node.parentNode.childNodes = node.parentNode.childNodes.filter(child => child !== node);
      node.parentNode = this; this.childNodes.push(node);
    }
  }
  replaceChildren(...nodes) { this.childNodes.forEach(node => { node.parentNode = null; }); this.childNodes = []; this._text = ""; this.append(...nodes); }
  querySelector() { if (!this.firstChild) this.append(new StubNode("button")); return this.firstChild; }
}
const appSource = await readFile(new URL("../../src/AirWaveApMonitor.App/wwwroot/app.js", import.meta.url), "utf8");
const bindingStart = '\ndocument.querySelectorAll("[data-view]")';
assert.equal(appSource.split(bindingStart).length, 2, "Locate the production event-binding boundary");
const appFunctions = appSource.split(bindingStart)[0].replace(/^import .*;$/m, "");
function appStub() {
  const nodes = new Map();
  const document = { getElementById(id) { if (!nodes.has(id)) nodes.set(id, new StubNode()); return nodes.get(id); }, createElement(tag) { return new StubNode(tag); } };
  const context = { ...ui, document, Node:StubNode, URLSearchParams, AbortController, setTimeout, clearTimeout };
  runInNewContext(`${appFunctions}\nglobalThis.subject = { state, updateControls, applySettings, loadHealth, metadataPanels, retryAuthentication };`, context);
  return { ...context.subject, nodes, document, context };
}
const savedSettings = () => ({ mode:"live", profile:{datasetId:"dataset",origin:"https://example.invalid/",configurationVersion:1,scopeVersion:1,credentialsVersion:1,settings:{apIntervalMinutes:5},proxy:{mode:0}}, authenticationBlocked:false,credentialRegistrationPending:false,credentialStatus:"registered",executionAccount:"synthetic-account" });
const cases = [
  ["Failed counts stay unknown instead of becoming zero", () => {
    assert.equal(ui.countText(null), "—");
    assert.equal(ui.countText(undefined), "—");
    assert.equal(ui.countText(0), "0");
  }],
  ["A newer search rejects an older response", () => {
    const gate = ui.createRequestGate(); const first = gate.begin(); const second = gate.begin();
    assert.equal(gate.isCurrent(first), false); assert.equal(gate.isCurrent(second), true);
    gate.invalidate(); assert.equal(gate.isCurrent(second), false);
  }],
  ["Export pins the displayed run and every filter but excludes paging", () => {
    const params = ui.dashboardQuery({ search: "SW & 포트", filter: "OpenIncidents", sort: "FirstDownAt", descending: true,
      managementStatus: "PlannedWork", hidePlanned: true, page: 4, pageSize: 50 }, { runId: 12, scopeVersion: 3 }, true);
    assert.equal(params.get("runId"), "12"); assert.equal(params.get("scopeVersion"), "3");
    assert.equal(params.get("search"), "SW & 포트"); assert.equal(params.get("filter"), "OpenIncidents");
    assert.equal(params.get("managementStatus"), "PlannedWork"); assert.equal(params.get("hidePlanned"), "true");
    assert.equal(params.get("descending"), "true"); assert.equal(params.has("page"), false);
    assert.equal(params.has("pageSize"), false);
  }],
  ["Export before any displayed snapshot is rejected", () => {
    assert.throws(() => ui.dashboardQuery({}, null, true), /회차/);
  }],
  ["Current query removes old run and scope while retaining user filters", () => {
    const params = ui.dashboardQuery({ filter: "Unknown", search: "AP", page: 2, pageSize: 50 }, null);
    assert.equal(params.has("runId"), false); assert.equal(params.has("scopeVersion"), false);
    assert.equal(params.get("filter"), "Unknown"); assert.equal(params.get("page"), "2");
  }],
  ["Unknown last-Down AP is never rendered as confirmed Down", () => {
    const status = ui.observationStatus({ currentState: null, lastValidState: "Down", openIncidentId: "incident" });
    assert.equal(status.label, "이번 미확인"); assert.equal(status.tone, "amber");
  }],
  ["Recovered elapsed stops at recovery even in a later selected run", () => {
    const incident = { firstDownAtUtc: "2026-11-15T01:00:00Z", recoveredAtUtc: "2026-11-15T01:05:00Z" };
    assert.equal(ui.incidentElapsed(incident, "2026-11-16T01:00:00Z"), 300000);
  }],
  ["Open elapsed uses selected run and backward time stays unknown", () => {
    const incident = { firstDownAtUtc: "2026-11-15T01:00:00Z" };
    assert.equal(ui.incidentElapsed(incident, "2026-11-15T01:10:00Z"), 600000);
    assert.equal(ui.incidentElapsed(incident, "2026-11-15T00:59:00Z"), null);
    assert.equal(ui.incidentElapsed({ ...incident, timeNeedsReview: true }, "2026-11-15T01:10:00Z"), null);
  }],
  ["Current dashboard follows a newly finalized AP run", () => {
    assert.equal(ui.shouldFollowLatest({ view: "dashboard", historical: false, loading: false, runId: 11 }, { lastFinalRunId: 12 }), true);
    assert.equal(ui.shouldFollowLatest({ view: "dashboard", historical: false, loading: false, runId: null }, { lastFinalRunId: 1 }), true);
  }],
  ["Explicit historical view and open detail keep their selected run", () => {
    assert.equal(ui.shouldFollowLatest({ view: "dashboard", historical: true, loading: false, runId: 11 }, { lastFinalRunId: 12 }), false);
    assert.equal(ui.shouldFollowLatest({ view: "detail", historical: false, loading: false, runId: 11 }, { lastFinalRunId: 12 }), false);
  }],
  ["No final run, same run, or a pending query do not trigger refresh", () => {
    const current = { view: "dashboard", historical: false, loading: false, runId: 12 };
    assert.equal(ui.shouldFollowLatest(current, { lastFinalRunId: null }), false);
    assert.equal(ui.shouldFollowLatest(current, { lastFinalRunId: 12 }), false);
    assert.equal(ui.shouldFollowLatest({ ...current, loading: true }, { lastFinalRunId: 13 }), false);
  }],
  ["DOM stub: health auth-stop enables retry and updates label without overwriting dirty settings", async () => {
    const app = appStub(); app.state.view = "settings"; app.applySettings(savedSettings(),{fill:true});
    app.state.settingsDirty = true; app.document.getElementById("interval").value = "17";
    let settingsReads = 0;
    app.context.fetch = async path => ({ok:true,json:async () => {
      if (path === "/api/settings") { settingsReads++; return {...savedSettings(),authenticationBlocked:true}; }
      assert.equal(path,"/api/health"); return {mode:"live",collector:{phase:"Idle",configured:true,authenticationBlocked:true,blockedReason:"authentication-rejected"}};
    }});
    await app.loadHealth(); await new Promise(resolve => setImmediate(resolve));
    assert.equal(app.document.getElementById("retry-authentication").disabled,false);
    assert.match(app.document.getElementById("authentication-status").textContent,/인증 중단/);
    assert.equal(app.document.getElementById("interval").value,"17");
    assert.equal(app.state.settingsDirty,true); assert.equal(settingsReads,1);
  }],
  ["DOM stub: pending credential registration cannot be cleared by retry", async () => {
    const app = appStub(); app.state.settings = {...savedSettings(),authenticationBlocked:true,credentialRegistrationPending:true};
    app.state.health = {phase:"Idle",configured:true,authenticationBlocked:true}; app.updateControls();
    assert.equal(app.document.getElementById("retry-authentication").disabled,true);
    assert.match(app.document.getElementById("authentication-status").textContent,/재등록/);
    app.context.fetch = async () => { throw new Error("Retry must not send a request while registration is pending"); };
    await app.retryAuthentication();
    assert.equal(app.document.getElementById("global-error").textContent,"");
  }],
  ["DOM stub: fresh health clearing auth stop overrides stale settings", () => {
    const app = appStub(); app.state.settings = {...savedSettings(),authenticationBlocked:true};
    app.state.health = {phase:"Idle",configured:true,authenticationBlocked:false}; app.updateControls();
    assert.equal(app.document.getElementById("retry-authentication").disabled,true);
    assert.match(app.document.getElementById("authentication-status").textContent,/차단 없음/);
    app.state.health = {phase:"Idle",configured:false,authenticationBlocked:true,blockedReason:"credential-reregistration-required"}; app.updateControls();
    assert.equal(app.document.getElementById("retry-authentication").disabled,true);
    assert.match(app.document.getElementById("authentication-status").textContent,/재등록/);
  }],
  ["DOM stub: uptime main row displays raw value without inventing units and keeps evidence", () => {
    const app = appStub();
    const row = {sourceApId:"ap-1",device:{uptime:null},identity:{},location:{},controller:{},rawSourceValues:{snmp_uptime:"200"},fields:{snmp_uptime:{availability:"Provided",validity:"Valid",source:"Demo",freshness:"Unknown",collectedAtUtc:"2026-11-15T01:00:00Z"}}};
    const general = app.metadataPanels(row)[0], pairs = general.childNodes[1].childNodes;
    const labelIndex = pairs.findIndex(node => node.textContent === "가동 시간 원문");
    const value = pairs[labelIndex + 1].firstChild;
    assert.equal(value.firstChild.textContent,"200");
    assert.match(value.textContent,/제공됨.*유효.*가상 자료/);
    assert.match(value.textContent,/최신성 미확인/);
  }],
  ["A changed credential or configuration invalidates old diagnostic identity", () => {
    const profile = { datasetId: "data", origin: "https://demo.invalid/", configurationVersion: 2, scopeVersion: 1, credentialsVersion: 1 };
    assert.notEqual(ui.settingsIdentity(profile), ui.settingsIdentity({ ...profile, credentialsVersion: 2 }));
    assert.notEqual(ui.settingsIdentity(profile), ui.settingsIdentity({ ...profile, configurationVersion: 3 }));
    assert.notEqual(ui.settingsIdentity(profile), ui.settingsIdentity({ ...profile, scopeVersion: 2 }));
  }]
];
export const results = await Promise.all(cases.map(async ([name, run]) => {
  try { await run(); return { name, passed: true }; }
  catch (error) { return { name, passed: false, message: error.message }; }
}));
console.log(JSON.stringify(results, null, 2));
const failed = results.filter(result => !result.passed);
if (failed.length) throw new Error(`${failed.length} UI state behavior checks failed: ${failed.map(result => result.name).join("; ")}`);
