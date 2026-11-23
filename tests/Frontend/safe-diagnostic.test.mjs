import assert from "node:assert/strict";
import {test} from "node:test";
import {buildSafeDiagnostic,serializeSafeDiagnostic} from "../../src/AirWaveApMonitor.App/wwwroot/safe-diagnostic.js";
test("a hostile report cannot leak private IP, credentials, certificate fingerprint, AP identifiers or raw logs",()=>{
 const secret=["10.175.193.12","aruba.internal","svc_airwave","password-VERY-SECRET","0123456789abcdef".repeat(4),"2797","raw certificate chain"];
 const obj={mode:"live",version:{version:"v0.1.3-preview.9",sourceCommit:"a".repeat(40),packaged:true},
 health:{phase:"Idle",configured:true,lastOutcome:"Partial",lastValidCommittedAtUtc:"2026-11-23T01:00:00Z",credentials:secret},
 run:{outcome:"Partial",summary:{targetCount:2048,confirmedDown:8,confirmedUp:1756,unknown:1,openIncidents:9,unsafe:secret},
   sourceEvidence:{parsedApCount:2048,appliedStateCount:1764,excludedRecords:Array.from({length:284},(_,i)=>({
    sourceApId:i===0?"2797":String(i),reason:"Unverified",safeDetail:secret.join(" ")}))},
   errors:Array.from({length:284},()=>({category:"Unverified",safeMessage:secret.join(" "),sourceApId:"2797",count:1}))},
 report:{outcome:1,httpStatus:403,tlsResult:"failed",tlsFailureCode:"pin-mismatch",loginRequests:1,apRequests:1,
   parsedRecords:1764,excludedRecords:284,origin:"https://10.175.193.12",executionAccount:"svc_airwave",
   errors:[{category:"Unverified",safeMessage:secret.join(" ")}],verificationNotes:secret}
 };
 const json=serializeSafeDiagnostic(buildSafeDiagnostic(obj));
 for(const s of secret) assert.equal(json.includes(s),false,s);
 assert.match(json,/"excludedCount": 284/);
 assert.match(json,/"confirmedDown": 8/);
 assert.match(json,/"httpStatus": 403/);
 assert.match(json,/"sourceCommit": "a{40}"/);
});
test("unknown fields/secret strings are never copied, invalid count is null",()=>{
 const json=serializeSafeDiagnostic(buildSafeDiagnostic({mode:"secret-IP",version:{version:"secret"},run:{summary:{confirmedDown:"192.168.1.1",targetCount:-3},sourceEvidence:{excludedRecords:[{reason:"secret"}]}},report:{tlsResult:"malicious",httpStatus:"403; password"}}));
 assert.match(json,/"confirmedDown": null/);
 assert.match(json,/"httpStatus": null/);
 assert.match(json,/"reason": "Unknown"/);
 assert.doesNotMatch(json,/192\.168|password|malicious|secret-IP/);
});
test("no source data is reported as unavailable rather than zero AP",()=>{
 const x=buildSafeDiagnostic({});
 assert.equal(x.collection.available,false);
 assert.equal(x.collection.confirmedDown,null);
 assert.equal(x.connectionDiagnostic.available,false);
 assert.equal(x.connectionDiagnostic.httpStatus,null);
});
