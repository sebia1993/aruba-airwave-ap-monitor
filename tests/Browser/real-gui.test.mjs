// Actual GUI regression: launch the Windows Demo app and operate Chrome/Edge via CDP.
// Uses Node 24 built-in WebSocket; no npm dependencies or live enterprise endpoints.
import {spawn} from "node:child_process";
import {existsSync,mkdirSync,mkdtempSync,readFileSync,readdirSync,rmSync} from "node:fs";
import {join,resolve} from "node:path";
import {tmpdir} from "node:os";
import assert from "node:assert/strict";

const exe=resolve(process.argv[2]||"");
assert.ok(existsSync(exe),"Pass the published Windows executable");
const browsers=[
  "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe",
  "C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe",
  "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe",
  "C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe"
];
const browserExe=browsers.find(existsSync);
assert.ok(browserExe,"Chrome or Edge required: real browser test must not silently downgrade to a mock");
const root=mkdtempSync(join(tmpdir(),"airwave-real-gui-"));
const downloads=join(root,"downloads");
mkdirSync(downloads);
const port=19076,origin="http://127.0.0.1:"+port;
let app=null,browser=null,socket=null,seq=0;
const pending=new Map();
const sleep=ms=>new Promise(done=>setTimeout(done,ms));
async function until(check,name,ms=90000){
  const start=Date.now();let reason="";
  while(Date.now()-start<ms){
    try {if(await check())return;}catch(e){reason=String(e.message||e).slice(0,180);}
    await sleep(180);
  }
  throw Error("Timed out: "+name+(reason?" / "+reason:""));
}
function readActivePort(){
  const p=join(root,"chrome","DevToolsActivePort");
  if(!existsSync(p))return null;
  const lines=readFileSync(p,"utf8").trim().split(/\r?\n/);
  if(!/^\d+$/.test(lines[0])||!lines[1]?.startsWith("/"))return null;
  return {port:Number(lines[0]),path:lines[1]};
}
async function command(method,params={},sessionId=null){
  const id=++seq;
  return new Promise((resolve,reject)=>{
    const timeout=setTimeout(()=>{pending.delete(id);reject(Error("CDP "+method+" timed out"));},20000);
    pending.set(id,{resolve,reject,timeout});
    socket.send(JSON.stringify({id,method,params,...(sessionId?{sessionId}:{})}));
  });
}
function handleMessage(event){
  const response=JSON.parse(event.data);
  if(!response.id)return;
  const item=pending.get(response.id);
  if(!item)return;
  pending.delete(response.id);clearTimeout(item.timeout);
  if(response.error)item.reject(Error(response.error.message||"CDP error"));
  else item.resolve(response.result);
}
let sessionId=null;
async function expression(code){
  const result=await command("Runtime.evaluate",{expression:code,returnByValue:true,awaitPromise:true},sessionId);
  if(result.exceptionDetails)throw Error("Browser evaluation failed: "+JSON.stringify(result.exceptionDetails).slice(0,600));
  return result.result?.value;
}
async function click(selector){
  const success=await expression("(()=>{const x=document.querySelector("+JSON.stringify(selector)+");if(!x||x.disabled)return false;x.click();return true})()");
  assert.equal(success,true,"Element unavailable: "+selector);
}
function terminate(proc){
  if(!proc||proc.exitCode!==null)return;
  try{proc.kill("SIGKILL");}catch{}
}
try {
  let processError="";
  app=spawn(exe,["--mode","demo","--data-dir",join(root,"demo-data"),"--port",String(port)],{
    cwd:join(exe,".."),windowsHide:true,stdio:["ignore","pipe","pipe"]});
  app.stderr?.on("data",b=>{processError=(processError+b.toString()).slice(-1500);});
  await until(async()=>{
    if(app.exitCode!==null)throw Error("Demo app ended before ready "+processError);
    const r=await fetch(origin+"/api/health");
    if(!r.ok)return false;
    const h=await r.json();
    return h.mode==="demo"&&!!h.collector?.lastValidCommittedAtUtc&&h.collector.phase==="Idle";
  },"initial Demo 2048 AP collection",110000);
  browser=spawn(browserExe,["--headless=new","--disable-gpu","--no-first-run","--no-default-browser-check",
    "--disable-extensions","--remote-debugging-port=0","--user-data-dir="+join(root,"chrome"),"about:blank"],{
    windowsHide:true,stdio:"ignore"});
  await until(()=>!!readActivePort(),"Chrome debugging ready",20000);
  const target=readActivePort();
  socket=new WebSocket("ws://127.0.0.1:"+target.port+target.path);
  await new Promise((resolve,reject)=>{socket.addEventListener("open",resolve,{once:true});socket.addEventListener("error",reject,{once:true});});
  socket.addEventListener("message",handleMessage);
  await command("Browser.setDownloadBehavior",{behavior:"allow",downloadPath:downloads,eventsEnabled:true});
  const created=await command("Target.createTarget",{url:origin});
  const attached=await command("Target.attachToTarget",{targetId:created.targetId,flatten:true});
  sessionId=attached.sessionId;
  await command("Runtime.enable",{},sessionId);
  await command("Page.enable",{},sessionId);
  await until(async()=>await expression("document.readyState==='complete'&&!!document.querySelector('#ap-table')"),"HTML document");
  await until(async()=>await expression("!!document.querySelector('#ap-table .console-ap-row')"),"AP inventory displayed");
  await click('[data-view="runs"]');
  await until(async()=>await expression("!document.querySelector('[data-screen=\"runs\"]').hidden && !!document.querySelector('#run-table tbody tr')"),"runs menu");
  await click("#safe-diagnostic-preview-action");
  await until(async()=>await expression("document.querySelector('#safe-diagnostic-preview').textContent.includes('\"schemaVersion\": 1')"),"sanitized JSON preview");
  const safe=await expression("document.querySelector('#safe-diagnostic-preview').textContent");
  const report=JSON.parse(safe);
  assert.equal(report.schemaVersion,1);
  assert.equal(report.collector.mode,"demo");
  assert.equal(report.application.packageMetadataPresent,false); // direct published app has no sibling metadata
  assert.ok(report.latestApRun?.runId>0);
  for(const privateWord of ["sourceApId","rawXml","password","ipAddress","fingerprint","safeMessage"]){
    assert.ok(!safe.includes(privateWord),"Private field in GUI export: "+privateWord);
  }
  await click("#safe-diagnostic-copy");
  await until(async()=>await expression("!document.querySelector('#safe-diagnostic-save').disabled"),"JSON save enabled after copy",15000);
  await click("#safe-diagnostic-save");
  await until(()=>readdirSync(downloads).some(n=>n==="airwave-safe-diagnostics.json"),"JSON browser download",20000);
  assert.equal(JSON.parse(readFileSync(join(downloads,"airwave-safe-diagnostics.json"),"utf8")).schemaVersion,1);
  await click('[data-view="dashboard"]');
  await until(async()=>await expression("!!document.querySelector('#ap-table .console-ap-row')"),"AP list returned");
  const down=await expression("(()=>{const items=[...document.querySelectorAll('#ap-table .console-ap-row')];const x=items.find(v=>v.textContent.includes('Down'));if(!x)return false;x.click();return true})()");
  assert.equal(down,true,"Demo Down AP required for incident GUI regression");
  await until(async()=>await expression("!!document.querySelector('#ap-console-detail .incident-quick-memo textarea[name=\"quickMemo\"]')"),"incident memo form",15000);
  const memoText="GUI DEMO incident note";
  await expression("(()=>{const x=document.querySelector('.incident-quick-memo textarea[name=\"quickMemo\"]');x.value="+JSON.stringify(memoText)+";x.dispatchEvent(new Event('input',{bubbles:true}));return true;})()");
  await until(async()=>await expression("!!document.querySelector('.incident-quick-memo button[type=\"submit\"]:not([disabled])')"),"incident note eligible",15000);
  await click('.incident-quick-memo button[type="submit"]');
  await until(async()=>await expression("document.body.textContent.includes('이 사건에 조치 메모를 저장했습니다.')||document.body.textContent.includes('GUI DEMO incident note')"),"incident memo persisted",20000);
  await click('[data-view="help"]');
  await until(async()=>await expression("!document.querySelector('[data-screen=\"help\"]').hidden && document.querySelector('#update-ui-version').textContent.length>0"),"in-app help");
  assert.ok(await expression("document.querySelector('#help-monitoring').open"),"Help topic is open");
  console.log("PASS: real Chrome/Edge GUI, AP selection, incident memo, safe preview/copy/JSON file and help");
} finally {
  for(const item of pending.values()){clearTimeout(item.timeout);item.reject(Error("Browser closed"));}
  pending.clear();
  try{socket?.close();}catch{}
  terminate(browser);terminate(app);
  await sleep(500);
  try{rmSync(root,{recursive:true,force:true,maxRetries:4,retryDelay:150});}catch{}
}
