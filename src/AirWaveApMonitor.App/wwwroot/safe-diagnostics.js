// Closed export schema: never serialize a source object, raw error, device identity, secret or URL.
const allowed = Object.freeze({
  phase:["Initializing","Idle","Preparing","Collecting","Finalizing","StorageRecovery","Stopping","Stopped"],
  outcome:["Succeeded","Partial","Failed"],
  lifecycle:["Running","Finalized","Interrupted"],
  kind:["ApStatus","ConnectionNeighbor","Power"],
  completeness:["Complete","Partial","Unknown"],
  tls:["validated","pinned-exception","failed","not-observed","not-applicable-demo"],
  tlsCode:["pin-mismatch","pin-expired","pin-disallowed","certificate-name-mismatch",
    "certificate-expired","certificate-not-yet-valid","certificate-missing",
    "certificate-trust-chain","certificate-validation-failed","tls-handshake-or-certificate"],
  reason:["Unverified","DuplicateIdentity","InvalidField","InvalidResponse","Permission",
    "ResponseTooLarge","Network","Timeout","Tls","Authentication","CredentialUnavailable",
    "InvalidConfiguration","Interrupted"]
});
const oneOf = (kind,value) => typeof value==="string" && allowed[kind].includes(value) ? value : "Unknown";
const natural = value => Number.isSafeInteger(value)&&value>=0&&value<=100000000 ? value : null;
const httpCode = value => Number.isInteger(value)&&value>=100&&value<=599 ? value : null;
const bool = value => typeof value==="boolean" ? value : null;
function exclusions(run) {
  const rows=Array.isArray(run?.sourceEvidence?.excludedRecords)?run.sourceEvidence.excludedRecords:[];
  const result={};
  for(const row of rows){
    const reason=oneOf("reason",row?.reason);
    result[reason]=(result[reason]||0)+1;
  }
  return Object.fromEntries(Object.entries(result).sort(([a],[b])=>a.localeCompare(b,"en")));
}
export function createSafeDiagnostics({version,health,run,diagnostic,generatedAt}={}) {
  const collector=health?.collector||health||{};
  const build=version||{};
  const buildVersion=typeof build.version==="string"&&/^(?:\d+\.){2,3}\d+(?:[-+][0-9A-Za-z.-]+)?$/.test(build.version)&&build.version.length<=48
    ? build.version : "unavailable";
  const sourceCommit=typeof build.sourceCommit==="string"&&/^[a-f0-9]{40}$/.test(build.sourceCommit) ? build.sourceCommit.slice(0,12) : null;
  const timestamp=generatedAt instanceof Date&&!Number.isNaN(generatedAt.getTime())?generatedAt.toISOString():new Date().toISOString();
  return {
    schemaVersion:1,
    createdAtUtc:timestamp,
    redaction:"allowlisted-metadata-only; excludes addresses, credentials, certificates, identifiers, raw logs and error messages",
    application:{version:buildVersion,sourceCommit,packageMetadataPresent:bool(build.packageMetadataPresent)},
    collector:{
      mode:health?.mode==="live"?"live":health?.mode==="demo"?"demo":"unknown",
      phase:oneOf("phase",collector.phase),
      configured:bool(collector.configured),
      paused:bool(collector.paused),
      authenticationBlocked:bool(collector.authenticationBlocked),
      updateDelayed:bool(collector.updateDelayed),
      lastOutcome:oneOf("outcome",collector.lastOutcome)
    },
    latestApRun:run?{
      runId:natural(run.runId),
      kind:oneOf("kind",run.kind),
      outcome:oneOf("outcome",run.outcome),
      lifecycle:oneOf("lifecycle",run.lifecycle),
      completeness:oneOf("completeness",run.completeness),
      parsedApCount:natural(run.sourceEvidence?.parsedApCount),
      appliedStateCount:natural(run.sourceEvidence?.appliedStateCount),
      excludedRecordCount:natural(run.sourceEvidence?.excludedRecords?.length),
      excludedByReason:exclusions(run)
    }:null,
    connectionDiagnostic:diagnostic?{
      outcome:natural(diagnostic.outcome),
      httpStatus:httpCode(diagnostic.httpStatus),
      tlsResult:oneOf("tls",diagnostic.tlsResult),
      tlsFailureCode:oneOf("tlsCode",diagnostic.tlsFailureCode),
      loginRequests:natural(diagnostic.loginRequests),
      apRequests:natural(diagnostic.apRequests),
      parsedRecords:natural(diagnostic.parsedRecords),
      excludedRecords:natural(diagnostic.excludedRecords),
      elapsedMilliseconds:natural(diagnostic.elapsedMilliseconds)
    }:null
  };
}
export function safeDiagnosticsJson(input) { return JSON.stringify(createSafeDiagnostics(input),null,2)+"\n"; }
