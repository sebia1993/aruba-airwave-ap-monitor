"use strict";
// Allowlist only: NEVER copy arbitrary source text, settings, AP records, URLs, usernames or logs.
const outcomes = new Set(["Succeeded","Partial","Failed","Running","Interrupted","Unknown"]);
const phases = new Set(["Initializing","Idle","Preparing","Collecting","Finalizing","StorageRecovery","Stopping","Stopped"]);
const category = new Set(["Unverified","InvalidField","DuplicateIdentity","InvalidResponse","InvalidState","Unauthorized","Authentication","Forbidden","Timeout","ConnectionFailure","ResponseTooLarge","CredentialUnavailable","TlsValidation","Unknown"]);
const errorCode = new Set(["certificate-name-mismatch","certificate-trust-chain","pin-mismatch","pin-expired","pin-disallowed","certificate-validation-failed","certificate-expired","tls-handshake-or-certificate","none"]);
const tls = new Set(["validated","pinned-exception","failed","not-observed","not-applicable-demo"]);
const result = new Set(["success","partial","failed","unknown"]);
const number = (x,max=10000000) => Number.isSafeInteger(x) && x>=0 && x<=max ? x : null;
const state = (x,allowed) => allowed.has(x) ? x : "unknown";
const nonblank = x => typeof x === "string" && x.length>0;
const enumCategory = x => category.has(x) ? x : "Unknown";
function aggregateExcluded(evidence) {
  const byReason = Object.create(null);
  const records = Array.isArray(evidence?.excludedRecords) ? evidence.excludedRecords : [];
  for (const row of records) {
    const reason = enumCategory(row?.reason);
    byReason[reason] = (byReason[reason]||0)+1;
  }
  return Object.entries(byReason).sort(([a],[b])=>a.localeCompare(b))
    .map(([reason,count])=>({reason,count}));
}
function aggregateErrors(errors) {
  const byCategory=Object.create(null);
  for (const row of Array.isArray(errors)?errors:[]) {
    const code=enumCategory(row?.category);
    byCategory[code]=(byCategory[code]||0)+(number(row?.count,10000000)??1);
  }
  return Object.entries(byCategory).sort(([a],[b])=>a.localeCompare(b))
    .map(([category,count])=>({category,count}));
}
function detectOutcome(value) {
  if (typeof value === "number") return ["success","partial","failed"][value] || "unknown";
  return state(value,outcomes);
}
export function buildSafeDiagnostic({mode,version,health,run,report}={}) {
  const summary=run?.summary || {};
  const evidence=run?.sourceEvidence || {};
  const status = number(report?.httpStatus,599);
  const modeLabel=mode === "live" ? "live" : mode === "demo" ? "demo" : "unknown";
  const versionText = /^v?\d+\.\d+\.\d+(?:-preview\.\d+)?$/.test(version?.version??"") ? version.version : "unknown";
  const sourceCommit = /^[0-9a-f]{40}$/.test(version?.sourceCommit??"") ? version.sourceCommit : null;
  const reportErrors=aggregateErrors(report?.errors);
  const runErrors=aggregateErrors(run?.errors);
  const excluded=aggregateExcluded(evidence);
  return {
    schema:"airwave-monitor-safe-diagnostic/v1",
    disclosure:"allowlist-counts-only-no-network-addresses-or-identifiers",
    application:{version:versionText,sourceCommit,packaged:version?.packaged === true,mode:modeLabel},
    collector:{
      phase:state(health?.phase,phases),
      configured:health?.configured===true,
      paused:health?.paused===true,
      authenticationBlocked:health?.authenticationBlocked===true,
      updateDelayed:health?.updateDelayed===true,
      storageRecoveryPending:health?.storageRecoveryPending===true,
      lastOutcome:detectOutcome(health?.lastOutcome),
      hasLastValidCollection:!!health?.lastValidCommittedAtUtc,
      hasRecentFailure:health?.lastOutcome==="Failed"
    },
    collection:{
      available:!!run,
      outcome:detectOutcome(run?.outcome),
      lifecycle:state(run?.lifecycle,outcomes),
      observedTargetCount:number(summary.targetCount),
      confirmedUp:number(summary.confirmedUp),
      confirmedDown:number(summary.confirmedDown),
      unknownCount:number(summary.unknown),
      openIncidents:number(summary.openIncidents),
      parsedCount:number(evidence.parsedApCount),
      appliedCount:number(evidence.appliedStateCount),
      excludedCount:number(evidence?.excludedRecords?.length),
      excludedByReason:excluded,
      errorsByCategory:runErrors
    },
    connectionDiagnostic:{
      available:!!report,
      outcome:detectOutcome(report?.outcome),
      httpStatus:status,
      tlsResult:state(report?.tlsResult,tls),
      tlsFailureCode:state(report?.tlsFailureCode,errorCode),
      loginRequests:number(report?.loginRequests,1000),
      apRequests:number(report?.apRequests,1000),
      parsedRecords:number(report?.parsedRecords),
      excludedRecords:number(report?.excludedRecords),
      errorsByCategory:reportErrors
    }
  };
}
export function serializeSafeDiagnostic(value) {
  return JSON.stringify(value,null,2)+"\n";
}
