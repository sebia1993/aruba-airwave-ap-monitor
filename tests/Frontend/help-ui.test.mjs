import assert from "node:assert/strict";
import {readFile} from "node:fs/promises";
import {runInNewContext} from "node:vm";
import {test} from "node:test";

const root=new URL("../../src/AirWaveApMonitor.App/wwwroot/",import.meta.url);
const html=await readFile(new URL("index.html",root),"utf8");
const js=await readFile(new URL("app.js",root),"utf8");
const css=await readFile(new URL("help.css",root),"utf8");
const ids=new Set([...html.matchAll(/id="([^"]+)"/g)].map(m=>m[1]));
test("help page, quick-start routes and version-backed status fields are actually wired",()=>{
  for(const name of ["help-title","help-monitoring","help-incidents","help-runs","help-inventory","help-setup","help-update","update-ui-version","update-backup-status","help-context-button"])
    assert.ok(ids.has(name),name);
  assert.match(html,/data-view="help"/);
  assert.match(js,/function showHelp\(/);
  assert.match(js,/data-help-topic/);
  assert.match(js,/data-help-go/);
  assert.match(css,/help-update/);
});
test("read-only update inspection does not mutate settings, stop services or back up automatically",()=>{
  const a=js.indexOf("function renderUpdateSafety()"),b=js.indexOf("function showHelp(",a);
  assert.ok(a>=0&&b>a);
  const source=js.slice(a,b);
  assert.doesNotMatch(source,/\b(fetch|api|perform|POST)\s*\(/);
  const fields=new Map();
  const state={mode:"live",settings:{credentialStatus:"registered"},
    health:{phase:"Idle",configured:true,lastValidCommittedAtUtc:"2026-11-23T01:00:00Z"},
    maintenance:{backupStatus:"failed",lastBackupAtUtc:null}};
  const context={state,byId:id=>{
    if(!ids.has(id))throw Error("Missing status field "+id);
    if(!fields.has(id))fields.set(id,{textContent:""});
    return fields.get(id);
  },dateText:x=>x};
  runInNewContext(source+"\nglobalThis.render=renderUpdateSafety;",context);
  context.render();
  assert.match(fields.get("update-ui-version").textContent,/버전 미확인/);
  assert.match(fields.get("update-collector-status").textContent,/실행 중/);
  assert.match(fields.get("update-backup-status").textContent,/백업 실패/);
  state.maintenance={backupStatus:"ready",lastBackupAtUtc:"2026-11-22T00:00:00Z"};
  context.render();
  assert.match(fields.get("update-backup-status").textContent,/업데이트 전 별도 백업 필요/);
  state.health={phase:"Idle",storageRecoveryPending:true};
  context.render();
  assert.match(fields.get("update-collector-status").textContent,/교체 금지/);
});
test("help page identifies uncertainty and manual evidence requirements",()=>{
  assert.match(html,/HTTP 403은 자동 재로그인하지 않습니다/);
  assert.match(html,/업데이트 전 별도 백업/);
  assert.match(html,/제외 건수이지 Down AP 대수가 아닙니다/);
  assert.match(html,/인증서 지문은 반드시 별도 근거/);
});
