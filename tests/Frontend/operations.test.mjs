import assert from 'node:assert/strict';
import { inventoryValue, previewSelection, workTargets, timelineQuery, commandForDraft, operationValue } from '../../src/AirWaveApMonitor.App/wwwroot/operations.js';
const cases = [
 ['Point evidence is decoded by action while placement identifiers and unknown shapes are preserved',()=>{
   const value='{"Name":"\\uAC00\\uC0C1", "Version":2,"Location":{"Floor":"2층"}}';
   assert.equal(operationValue({action:'PointCorrected',field:'installationPoint'},value),'가상 · 버전 2 · 층: 2층');
   const id='44444444-4444-4444-4444-444444444444';assert.equal(operationValue({action:'PlacementRegistered',field:'installationPoint'},id),id);
   for(const raw of ['{invalid','{"unknown":1}','null'])assert.equal(operationValue({action:'PointCorrected',field:'installationPoint'},raw),raw);
   assert.equal(operationValue({field:'work'},value),value);
 }],
 ['Policy and source choices remain distinct in readable timeline values',()=>{
   assert.equal(operationValue({field:'managementStatus'},'MonitoringExcluded/EndTracking'),'감시 제외 / 추적 종료');
   assert.equal(operationValue({field:'manualPreferredFields'},'Name,IpAddress'),'이름, IP');
   assert.equal(operationValue({field:'incident'},'TrackingEnded'),'추적 종료');
   assert.equal(operationValue({field:'managementStatus'},'future/Unknown'),'future / Unknown');
 }],
 ['Manual preference uses the selected immutable snapshot and preserves automatic text', () => {
   const row={device:{name:'automatic'},manualInventory:{version:2,source:'Import',recordedAtUtc:'2026-11-16T00:00:00Z',values:{Name:'registered'},manualPreferredFields:['Name']}};
   assert.deepEqual(inventoryValue(row,'Name'),{value:'registered',automatic:'automatic',manual:'registered',preferred:true,different:true,source:'Import',recordedAtUtc:'2026-11-16T00:00:00Z',version:2});
   assert.equal(row.device.name,'automatic');
 }],
 ['Absent preferred value does not erase automatic metadata',()=>assert.equal(inventoryValue({device:{name:'old'},manualInventory:{values:{},manualPreferredFields:['Name']}},'Name').value,'old')],
 ['A nonpreferred manual value remains a difference without becoming an observation',()=>{
   const x=inventoryValue({device:{name:'auto'},manualInventory:{values:{Name:'manual'},manualPreferredFields:[]}},'Name');
   assert.equal(x.value,'auto');assert.equal(x.different,true);assert.equal(x.preferred,false);
 }],
 ['CSV selection accepts only explicitly selected valid rows',()=>{
   const p={previewId:'p',errors:[],rows:[{rowNumber:2,target:{apId:'a'},errors:[]},{rowNumber:3,target:null,errors:[{code:'ambiguous'}]}]};
   assert.deepEqual(previewSelection(p,[2]),[2]);assert.throws(()=>previewSelection(p,[3]),/미리보기/);assert.throws(()=>previewSelection(p,[]),/선택/);
 }],
 ['CSV document errors and changed selection cannot be applied',()=>{
   assert.throws(()=>previewSelection({errors:[{}],rows:[]},[2]),/미리보기/);
   assert.throws(()=>previewSelection({errors:[],rows:[{rowNumber:2,target:{},errors:[]}]},[2,2]),/중복/);
 }],
 ['Historical work target keeps exact old generation and incident',()=>{
   const old={apId:'11111111-1111-1111-1111-111111111111',deviceGenerationId:'22222222-2222-2222-2222-222222222222',incidentId:'33333333-3333-3333-3333-333333333333'};
   assert.deepEqual(workTargets([{...old,installationPointId:''}]),[{...old,installationPointId:null}]);
   assert.throws(()=>workTargets([{apId:old.apId}]),/세대/);
 }],
 ['Work supports multiple explicit targets without name/IP inference',()=>{
   const point='44444444-4444-4444-4444-444444444444';assert.equal(workTargets([{installationPointId:point}])[0].installationPointId,point);
   assert.throws(()=>workTargets([{apId:'AP-1'}]),/식별/);assert.throws(()=>workTargets([]),/대상/);
 }],
 ['Timeline pins a watermark and exact target while paging',()=>{
   const q=timelineQuery({apId:'a',deviceGenerationId:'g',kind:'WorkAction',fromUtc:'2026-11-16T00:00:00Z'},2,99);
   assert.equal(q.get('throughEventId'),'99');assert.equal(q.get('apId'),'a');assert.equal(q.get('deviceGenerationId'),'g');assert.equal(q.get('page'),'2');
 }],
 ['Unchanged command payload reuses the id after an uncertain response',()=>{
   const draft={};let number=0;const id=()=>`command-${++number}`;
   assert.equal(commandForDraft(draft,{expectedVersion:3,name:'a'},id),'command-1');
   assert.equal(commandForDraft(draft,{expectedVersion:3,name:'a'},id),'command-1');
   assert.equal(commandForDraft(draft,{expectedVersion:3,name:'b'},id),'command-2');
 }]
];
let failed=0;
for (const [name,run] of cases) { try { await run(); console.log('PASS '+name); } catch(e) { failed++;console.error('FAIL '+name+': '+e.message); } }
if(failed)throw new Error(`${failed}/${cases.length} operations UI checks failed`);
console.log(`${cases.length} operations UI checks passed`);
