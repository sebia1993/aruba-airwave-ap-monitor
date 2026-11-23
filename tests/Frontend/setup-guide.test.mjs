import assert from "node:assert/strict";
import { test } from "node:test";
import { connectionSteps, determineSetupStage } from "../../src/AirWaveApMonitor.App/wwwroot/setup-guide.js";

const origin="https://192.0.2.10:8443";
const base={
  mode:"live",
  settings:{profile:{origin,configurationVersion:4},credentialStatus:"registered"},
  health:{configured:true,lastOutcome:null,lastValidCommittedAtUtc:null,paused:false},
  report:null,diagnosticValid:false
};
const check=overrides=>determineSetupStage({...base,...overrides});
const success={outcome:0,httpStatus:200,tlsResult:"pinned-exception",apRequests:1,loginRequests:1};
test("five steps are present",()=>{
  assert.deepEqual(connectionSteps,["서버 IP·포트","읽기 계정","HTTPS 인증서","로그인·AP 조회","첫 수집"]);
});
test("unconfigured Live asks to save IP and port",()=>{
  const r=check({settings:{profile:null,credentialStatus:"missing"},health:{configured:false}});
  assert.equal(r.key,"origin");assert.equal(r.target,"origin");assert.match(r.description,/https:\/\/IP:포트/);
});
test("credentials are required before diagnostics",()=>{
  const r=check({settings:{profile:{origin},credentialStatus:"missing"}});
  assert.equal(r.key,"credentials"); assert.equal(r.action,"jump"); assert.equal(r.target,"username");
});
test("healthy Live users avoid the setup process",()=>{
  const r=check({health:{configured:true,lastOutcome:"Succeeded",lastValidCommittedAtUtc:"2026-11-23T01:00:00Z"}});
  assert.equal(r.key,"done");
});
test("changed connection requires fresh diagnostics",()=>{
  const r=check({health:{configured:true,lastOutcome:"Succeeded",lastValidCommittedAtUtc:"2026-11-23T01:00:00Z"},requireDiagnostic:true});
  assert.equal(r.key,"diagnostic");
});
test("TLS failure never auto-approves a new certificate",()=>{
  const r=check({report:{...success,tlsResult:"failed",tlsFailureCode:"pin-mismatch",httpStatus:null},diagnosticValid:true});
  assert.equal(r.key,"tls"); assert.equal(r.action,"jump"); assert.match(r.description,/독립적인 방법/);
});
test("HTTP 403 is not retried automatically",()=>{
  const r=check({report:{...success,httpStatus:403,outcome:2},diagnosticValid:true});
  assert.equal(r.key,"http403"); assert.match(r.description,/자동 재로그인/);
});
test("partial or absent AP reads cannot count as success",()=>{
  assert.equal(check({report:{...success,outcome:1},diagnosticValid:true}).key,"ap-read");
  assert.equal(check({report:{...success,apRequests:0},diagnosticValid:true}).key,"ap-read");
});
test("HTTP 200 diagnostic is not a committed AP run",()=>{
  const r=check({report:success,diagnosticValid:true});
  assert.equal(r.key,"collect"); assert.equal(r.action,"collect");
});
test("active collection stays pending",()=>{
  const r=check({report:success,diagnosticValid:true,health:{configured:true,activeRunId:20,lastOutcome:null,lastValidCommittedAtUtc:null}});
  assert.equal(r.key,"waiting");
});
test("failed or partial runs do not show full success",()=>{
  const partial=check({report:success,diagnosticValid:true,health:{configured:true,lastOutcome:"Partial",lastValidCommittedAtUtc:"2026-11-23T01:00:00Z"}});
  const failed=check({report:success,diagnosticValid:true,health:{configured:true,lastOutcome:"Failed",lastValidCommittedAtUtc:"2026-11-23T00:00:00Z"}});
  assert.equal(partial.key,"partial");assert.equal(failed.key,"collect");
});
test("successful diagnostic and committed run complete setup",()=>{
  const r=check({report:success,diagnosticValid:true,health:{configured:true,lastOutcome:"Succeeded",lastValidCommittedAtUtc:"2026-11-23T01:00:00Z"}});
  assert.equal(r.key,"done");
});
test("blocked authentication does not auto-retry",()=>{
  const r=check({health:{configured:false,authenticationBlocked:true}});
  assert.equal(r.key,"auth-blocked");assert.equal(r.action,"jump");
});
