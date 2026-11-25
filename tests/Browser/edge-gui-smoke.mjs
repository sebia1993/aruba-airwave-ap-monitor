// Actual Microsoft Edge DOM/interaction smoke; no external dependencies or screenshots.
import assert from "node:assert/strict";
import {spawn} from "node:child_process";
import {mkdtemp,rm} from "node:fs/promises";
import {tmpdir} from "node:os";
import {join} from "node:path";
import {createServer} from "node:net";

const base=process.argv[2]||"http://127.0.0.1:18765/";
if(!/^http:\/\/127\.0\.0\.1:\d+\/$/.test(base))throw Error("Only local demo service supported");
const edge=process.env.AIRWAVE_EDGE_PATH;
if(!edge)throw Error("AIRWAVE_EDGE_PATH required for real browser test");
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
const server=createServer();
await new Promise(r=>server.listen(0,"127.0.0.1",r));
const port=server.address().port;
await new Promise(r=>server.close(r));
const profile=await mkdtemp(join(tmpdir(),"airwave-edge-smoke-"));
const child=spawn(edge,["--headless=new","--disable-gpu","--no-first-run","--no-default-browser-check",
  "--disable-extensions","--remote-allow-origins=*","--remote-debugging-port="+port,"--user-data-dir="+profile,base],{
  stdio:"ignore",windowsHide:true
});
let socket;let passed=false;
try{
  let page;
  for(let i=0;i<100;i++){
    if(child.exitCode!==null)throw Error("Edge process exited before readiness");
    try {
      const resp=await fetch("http://127.0.0.1:"+port+"/json/list");
      if(resp.ok){const list=await resp.json();page=list.find(x=>x.type==="page"&&x.webSocketDebuggerUrl);if(page)break;}
    }catch{}
    await sleep(150);
  }
  if(!page)throw Error("Could not discover an Edge DevTools page");
  socket=new WebSocket(page.webSocketDebuggerUrl);
  await new Promise((resolve,reject)=>{socket.addEventListener("open",resolve,{once:true});socket.addEventListener("error",reject,{once:true});});
  let seq=0;const pending=new Map();
  socket.addEventListener("message",event=>{
    const payload=JSON.parse(event.data);
    if(!pending.has(payload.id))return;
    const {resolve,reject}=pending.get(payload.id);pending.delete(payload.id);
    payload.error?reject(Error(payload.error.message)):resolve(payload.result);
  });
  const cdp=(method,params={})=>new Promise((resolve,reject)=>{
    const id=++seq;pending.set(id,{resolve,reject});socket.send(JSON.stringify({id,method,params}));
  });
  const evalExpr=async expression=>{
    const result=await cdp("Runtime.evaluate",{expression,returnByValue:true,awaitPromise:true});
    if(result.exceptionDetails)throw Error(result.exceptionDetails.text||"Browser expression failed");
    return result.result?.value;
  };
  const expectEventually=async (expression,label,attempts=80)=>{
    for(let i=0;i<attempts;i++){
      if(await evalExpr(expression))return;
      await sleep(200);
    }
    throw Error("GUI assertion failed: "+label);
  };
  const getJson=path=>evalExpr('fetch('+JSON.stringify(path)+').then(async r=>{if(!r.ok)throw Error("HTTP "+r.status);return r.json()})');
  const clickText=label=>evalExpr('(()=>{const b=[...document.querySelectorAll("button")].find(n=>n.textContent=== '+JSON.stringify(label)+' && n.getClientRects().length);if(!b)throw Error("Button missing");b.click()})()');
  const followForm='form[data-draft-key^="follow-up:"]';
  const markInput=followForm+' input[name="marked"]';
  const reasonInput=followForm+' input[name="reason"]';
  const followSave=followForm+' button[type="submit"]';
  const followButton='document.querySelector("#ap-console-detail")?.textContent?.includes("관리 대상 지정·해제")';
  const openFollowUp=async()=>{
    await expectEventually(followButton,'selected AP follow-up action');
    await clickText("관리 대상 지정·해제");
    await expectEventually('!!document.querySelector('+JSON.stringify(markInput)+') && !document.querySelector('+JSON.stringify(followSave)+')?.disabled','registration form ready');
  };
  const submitFollowUp=async (mark)=>{
    assert.equal(await evalExpr('document.querySelector('+JSON.stringify(markInput)+').checked'),!mark);
    await evalExpr('document.querySelector('+JSON.stringify(markInput)+').click()');
    if(!mark){
      // The real form must reject an empty reason and keep the saved value.
      await evalExpr('document.querySelector('+JSON.stringify(followSave)+').click()');
      await expectEventually('!document.querySelector("#global-error")?.hidden && document.querySelector("#global-error")?.textContent.includes("해제 사유")','required clear reason');
      await evalExpr('(()=>{const n=document.querySelector('+JSON.stringify(reasonInput)+');n.value="synthetic Edge verification";n.dispatchEvent(new Event("input",{bubbles:true}))})()');
    }
    await evalExpr('document.querySelector('+JSON.stringify(followSave)+').click()');
    const savedText=mark?"현재 관리 대상으로 지정됨":"현재 관리 대상 미지정";
    await expectEventually('document.querySelector("#operations-content")?.textContent.includes('+JSON.stringify(savedText)+') && document.querySelector('+JSON.stringify(markInput)+')?.checked==='+mark+' && !document.querySelector('+JSON.stringify(followSave)+')?.disabled','saved follow-up state');
  };
  const selectAp=async apId=>{
    const selector='#ap-table [data-ap-id="'+apId+'"]';
    await expectEventually('!!document.querySelector('+JSON.stringify(selector)+')','expected AP visible in list');
    await evalExpr('document.querySelector('+JSON.stringify(selector)+').click()');
    await expectEventually('document.querySelector("#ap-console-detail")?.textContent?.includes("AP Up/Down 이력")','selected AP simple history');
  };
  const followUpPath=runId=>'/api/dashboard?filter=FollowUp&pageSize=500&runId='+runId;
  await expectEventually('document.querySelector("#home-health")?.textContent?.includes("마지막 유효 수집")','collector header');

  if(process.argv[3]==="follow-up-after-restart"){
    // windows-smoke verifies the normal restart scenario before running this branch.
    const initial=await getJson('/api/dashboard?filter=All&pageSize=1');
    const marked=await getJson(followUpPath(initial.runId));
    assert.equal(marked.totalRows,1);
    const row=marked.rows[0];
    assert.equal(row.sourceApId,"9");
    assert.equal(row.currentState,"Up");
    assert.equal(row.isFollowUp,true);
    await evalExpr('document.querySelector("[data-filter=FollowUp]").click()');
    await expectEventually('document.querySelector("[data-filter=FollowUp]")?.getAttribute("aria-pressed")==="true" && document.querySelector("#ap-table")?.textContent.includes("사용자 관리 대상")','marked AP displayed after restart');
    await selectAp(row.apId);
    await openFollowUp();
    await submitFollowUp(false);
    const registration=await getJson('/api/operations/aps/'+row.apId+'/registration');
    assert.notEqual(registration.registration.inventory?.values?.FollowUp,"true");
    assert.equal((await getJson(followUpPath(initial.runId))).totalRows,1,'clearing must not rewrite a committed run');
    await evalExpr('document.querySelector("[data-view=dashboard]").click()');
    await expectEventually('!document.querySelector("#collect-now")?.disabled && document.querySelector("[data-screen=dashboard]")?.hidden===false','manual collection available');
    await evalExpr('document.querySelector("#collect-now").click()');
    await expectEventually('fetch("/api/health").then(r=>r.json()).then(h=>h.collector.phase==="Idle" && h.collector.lastFinalRunId>'+initial.runId+')','new committed AP collection after clearing',225);
    const next=await getJson('/api/dashboard?filter=FollowUp&pageSize=500');
    assert.ok(next.runId>initial.runId);
    assert.equal(next.outcome,"Succeeded");
    assert.equal(next.totalRows,0);
    assert.equal((await getJson(followUpPath(initial.runId))).totalRows,1,'older selection retains marked AP');
    await evalExpr('document.querySelector("#refresh-dashboard").click()');
    await expectEventually('document.querySelector("#ap-table")?.textContent.includes("조건에 맞는 AP가 없습니다") && document.querySelector("[data-filter=FollowUp]")?.getAttribute("aria-pressed")==="true"','cleared AP disappears from management filter');
    console.log("PASS: actual Edge management filter persists across restart; reason-required clear is reflected only in a subsequent committed collection");
    passed=true;
  }else{

  await expectEventually('document.querySelector("#ap-table [data-ap-id]")!==null','AP list populated');
  assert.equal(await evalExpr('document.querySelector("[data-screen=dashboard]")?.hidden'),false);
  await evalExpr('document.querySelector("#ap-table [data-ap-id]").click()');
  await expectEventually('document.querySelector("#ap-console-detail")?.hidden===false','selected AP detail');
  await expectEventually('document.querySelector("#ap-console-detail")?.textContent?.includes("AP Up/Down 이력")','simple AP status history');
  await evalExpr('document.querySelector("#ap-console-detail [data-ap-tab=incident]").click()');
  await expectEventually('document.querySelector("#ap-console-detail textarea")!==null','incident note editor');
  const first=await getJson('/api/dashboard?filter=All&pageSize=1');
  const originalRun=first.runId;
  const expected=await getJson('/api/dashboard?runId='+originalRun+'&filter=RepeatSuspected&sort=RecentDownCount&descending=true&repeatThreshold=1&priorityThreshold=1&pageSize=500');
  const recovered=expected.rows.find(row=>row.sourceApId==="9");
  assert.ok(recovered && recovered.currentState==="Up" && recovered.statusHistory.newDownCount24Hours>=1,"Demo recovered AP must be included in repeat filter");
  assert.equal(await evalExpr('document.querySelector("#repeat-threshold").value'),"3");
  assert.equal(await evalExpr('document.querySelector("#priority-threshold").value'),"5");
  await evalExpr('document.querySelector("[data-filter=RepeatSuspected]").click()');
  await expectEventually('document.querySelector("[data-filter=RepeatSuspected]")?.getAttribute("aria-pressed")==="true"','repeat filter selected');
  await evalExpr('(()=>{document.querySelector("#ap-filter-details").open=true;document.querySelector("#repeat-threshold").value="1";document.querySelector("#priority-threshold").value="1";document.querySelector("#repeat-threshold-form").requestSubmit()})()');
  await expectEventually('document.querySelector("#metric-repeat")?.textContent==='+JSON.stringify(expected.repeatSuspectedCount.toLocaleString("ko-KR")+"대")+' && document.querySelector("#metric-repeat-priority")?.textContent==='+JSON.stringify(expected.repeatPriorityCount.toLocaleString("ko-KR"))+' && document.querySelector("#ap-sort")?.value==="repeat"','repeat threshold and server count');
  await selectAp(recovered.apId);
  await openFollowUp();
  await submitFollowUp(true);
  const registration=await getJson('/api/operations/aps/'+recovered.apId+'/registration');
  assert.equal(registration.registration.inventory?.values?.FollowUp,"true");
  assert.equal((await getJson(followUpPath(originalRun))).totalRows,0,'marking must not rewrite the first committed run');
  console.log("PASS: actual Edge repeat threshold, recovered AP selection, and management target registration; existing run stays immutable");
  await evalExpr('document.querySelector("[data-view=help]").click()');
  await expectEventually('document.querySelector("[data-screen=help]")?.hidden===false','help route');
  assert.equal(await evalExpr('document.querySelector("#help-update-title")?.textContent'),"업데이트 전 읽기 전용 점검");
  await evalExpr('document.querySelector("[data-view=settings]").click()');
  await expectEventually('document.querySelector("[data-screen=settings]")?.hidden===false','settings route');
  await expectEventually('document.querySelector("#safe-diagnostic-save")!==null','safe report action');
  assert.equal(await evalExpr('typeof document.querySelector("#safe-diagnostic-copy").onclick'),"object"); // listeners are attached via addEventListener
  const safety=await evalExpr('document.querySelector("#safe-diagnostic-summary")?.textContent');
  assert.ok(safety&&/상태|수집/.test(safety));
  console.log("PASS: actual headless Edge rendered dashboard, AP selection, incident memo editor, help and safe diagnostic UI");
  passed=true;
  }
}finally{
  try{socket?.close();}catch{}
  child.kill();
  await rm(profile,{recursive:true,force:true,maxRetries:6,retryDelay:250});
}
if(!passed)process.exitCode=1;
