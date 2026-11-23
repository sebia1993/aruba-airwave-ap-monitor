import assert from "node:assert/strict";
import {test} from "node:test";
import {summarizeCollectionWarnings} from "../../src/AirWaveApMonitor.App/wwwroot/run-warnings.js";

test("284 excluded Unverified devices are one concise warning with exact evidence count",()=>{
  const excludedRecords=Array.from({length:284},(_,i)=>({reason:"Unverified",sourceApId:String(2797+i),safeDetail:"AP 분류 미검증: 대상에서 제외"}));
  const errors=excludedRecords.map(rec=>({safeMessage:"제외된 레코드가 있습니다.",count:1,sourceApId:rec.sourceApId}));
  const run={sourceEvidence:{excludedRecords},errors};
  const summary=summarizeCollectionWarnings(run);
  assert.deepEqual(summary.labels,["AP 분류 미검증 284건"]);
  assert.equal(summary.excludedCount,284);
  assert.deepEqual(summary.exclusions[0].sampleIds,["2797","2798","2799"]);
  assert.equal(run.errors.length,284); // Original attribution still available.
});
test("different excluded reasons stay separate, not interpreted as Down AP count",()=>{
  const run={sourceEvidence:{excludedRecords:[
    {reason:"Unverified",sourceApId:"2797"},
    {reason:"DuplicateIdentity",sourceApId:"3139"},
    {reason:"Unverified",sourceApId:"2460"}]},
    errors:[{safeMessage:"제외된 레코드가 있습니다.",count:1}]};
  assert.deepEqual(summarizeCollectionWarnings(run).labels,["AP 분류 미검증 2건","중복 식별 제외 1건"]);
});
test("non-exclusion failures and proper error weights are preserved",()=>{
  const x=summarizeCollectionWarnings({errors:[
    {safeMessage:"권한 또는 토큰 오류입니다. HTTP 403은 자동 재로그인하지 않습니다.",count:1},
    {safeMessage:"필드 확인 필요",count:2},
    {safeMessage:"필드 확인 필요",count:3}]});
  assert.equal(x.labels.length,2);
  assert.match(x.labels[0],/HTTP 403/);
  assert.equal(x.labels[1],"필드 확인 필요 · 5건");
});
test("legacy runs without excludedRecords still summarize generic errors",()=>{
  const x=summarizeCollectionWarnings({errors:[
    {safeMessage:"제외된 레코드가 있습니다.",count:1},
    {safeMessage:"제외된 레코드가 있습니다.",count:1}]});
  assert.deepEqual(x.labels,["제외된 레코드가 있습니다. · 2건"]);
});
test("huge evidence retains only three sample IDs, never includes arbitrary text",()=>{
  const x=summarizeCollectionWarnings({sourceEvidence:{excludedRecords:Array.from({length:2000},(_,i)=>({
    reason:"Unverified",sourceApId:i===3?"10.0.0.1":String(i+1),safeDetail:"secret or raw XML"}))}});
  assert.equal(x.exclusions.length,1);
  assert.deepEqual(x.exclusions[0].sampleIds,["1","2","3"]);
  assert.equal(x.labels[0],"AP 분류 미검증 2,000건");
  assert.doesNotMatch(JSON.stringify(x),/secret|10\.0/);
});
test("no warnings is an empty summary",()=>{
  assert.deepEqual(summarizeCollectionWarnings({}),{labels:[],exclusions:[],details:[],excludedCount:0});
});
