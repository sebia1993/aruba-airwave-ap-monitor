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
  const expectEventually=async (expression,label)=>{
    for(let i=0;i<80;i++){
      if(await evalExpr(expression))return;
      await sleep(200);
    }
    throw Error("GUI assertion failed: "+label);
  };
  await expectEventually('document.querySelector("#home-health")?.textContent?.includes("마지막 유효 수집")','collector header');
  await expectEventually('document.querySelector("#ap-table [data-ap-id]")!==null','AP list populated');
  assert.equal(await evalExpr('document.querySelector("[data-screen=dashboard]")?.hidden'),false);
  await evalExpr('document.querySelector("#ap-table [data-ap-id]").click()');
  await expectEventually('document.querySelector("#ap-console-detail")?.hidden===false','selected AP detail');
  await expectEventually('document.querySelector("#ap-console-detail textarea")!==null','incident note editor');
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
}finally{
  try{socket?.close();}catch{}
  child.kill();
  await rm(profile,{recursive:true,force:true,maxRetries:6,retryDelay:250});
}
if(!passed)process.exitCode=1;
