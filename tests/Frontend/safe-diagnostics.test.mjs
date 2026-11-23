import {test} from "node:test";
import assert from "node:assert/strict";
import {createSafeDiagnostics,safeDiagnosticsJson} from "../../src/AirWaveApMonitor.App/wwwroot/safe-diagnostics.js";
const secret="PRIVATE-DO-NOT-EXPORT";
const sensitive={
  version:{version:"0.1.3-preview.9",sourceCommit:"a".repeat(40),packageMetadataPresent:true,
    origin:"https://10.0.0.15:8443",username:"admin"},
  health:{mode:"live",collector:{phase:"Idle",lastOutcome:"Partial",configured:true,paused:false,
    authenticationBlocked:false,lastCollectionError:"TLS "+secret,blockedReason:secret,origin:"https://10.0.0.15"}},
  run:{runId:42,kind:"ApStatus",outcome:"Partial",lifecycle:"Finalized",completeness:"Partial",
    summary:{hostname:secret},errors:[{safeMessage:secret}],
    sourceEvidence:{parsedApCount:2310,appliedStateCount:2026,
      excludedRecords:Array.from({length:284},(_,i)=>({reason:"Unverified",sourceApId:String(5000+i),
        name:secret,ipAddress:"10.0.0.15",rawXml:secret}))}},
  diagnostic:{outcome:1,httpStatus:403,tlsResult:"failed",tlsFailureCode:"certificate-trust-chain",loginRequests:1,apRequests:1,
    parsedRecords:2310,excludedRecords:284,elapsedMilliseconds:2023,username:secret,sha256:secret,stages:[{message:secret}]}
};
test("safe JSON omits addresses, account, fingerprint, AP ids and raw messages",()=>{
  const output=safeDiagnosticsJson({...sensitive,generatedAt:new Date("2026-11-23T01:00:00Z")});
  for(const bad of [secret,"10.0.0.15","5000","admin","sourceApId","safeMessage","rawXml","origin","sha256","blockedReason","password"]){
    assert.ok(!output.includes(bad),"private field escaped: "+bad);
  }
  const data=JSON.parse(output);
  assert.equal(data.latestApRun.runId,42);
  assert.equal(data.latestApRun.excludedRecordCount,284);
  assert.equal(data.latestApRun.excludedByReason.Unverified,284);
  assert.equal(data.connectionDiagnostic.httpStatus,403);
  assert.equal(data.application.sourceCommit,"a".repeat(12));
});
test("untrusted external string cannot escape whitelist",()=>{
  const bad=createSafeDiagnostics({version:{version:"https://internal.local",sourceCommit:secret},health:{mode:secret,collector:{phase:secret,lastOutcome:secret}},
    run:{kind:secret,outcome:secret,sourceEvidence:{excludedRecords:[{reason:secret,sourceApId:secret}]}}});
  assert.equal(bad.application.version,"unavailable");
  assert.equal(bad.collector.phase,"Unknown");
  assert.equal(bad.latestApRun.excludedByReason.Unknown,1);
  assert.ok(!JSON.stringify(bad).includes(secret));
});
test("missing evidence cannot become zero or successful status",()=>{
  const v=createSafeDiagnostics({diagnostic:{httpStatus:"403",outcome:-1,apRequests:"0",tlsResult:"unexpected"},
    run:{runId:"55",sourceEvidence:{excludedRecords:null}},health:{mode:"live"}});
  assert.equal(v.connectionDiagnostic.httpStatus,null);
  assert.equal(v.connectionDiagnostic.outcome,null);
  assert.equal(v.connectionDiagnostic.apRequests,null);
  assert.equal(v.latestApRun.runId,null);
  assert.equal(v.latestApRun.excludedRecordCount,null);
  assert.equal(v.collector.lastOutcome,"Unknown");
});
test("exported file is valid JSON and stable for a given clock",()=>{
  const output=safeDiagnosticsJson({version:{version:"0.1.3-preview.9",sourceCommit:"a".repeat(40)},
    generatedAt:new Date("2026-11-23T00:00:00Z")});
  assert.equal(JSON.parse(output).createdAtUtc,"2026-11-23T00:00:00.000Z");
  assert.ok(output.endsWith("\n"));
  assert.doesNotMatch(output,/[0-9]{1,3}(?:\.[0-9]{1,3}){3}/);
});
