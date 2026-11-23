// Present aggregate, non-sensitive error summaries without changing stored evidence.
const reasonLabels = Object.freeze({
  Unverified:"AP 분류 미검증",
  DuplicateIdentity:"중복 식별 제외",
  InvalidField:"AP ID·필드 유효성 제외",
  InvalidResponse:"응답 형식 오류",
  Permission:"권한 오류"
});
const excludedGeneric = new Set(["제외된 레코드가 있습니다.","수집 기록에서 제외된 레코드가 있습니다."]);
export function summarizeCollectionWarnings(run = {}) {
  const excluded = Array.isArray(run?.sourceEvidence?.excludedRecords) ? run.sourceEvidence.excludedRecords : [];
  const grouped = new Map();
  for (const entry of excluded) {
    const reason = typeof entry?.reason === "string" && entry.reason ? entry.reason : "Unknown";
    let group = grouped.get(reason);
    if (!group) { group = { reason, label:reasonLabels[reason] || "기타 분류 제외", count:0, sampleIds:[] }; grouped.set(reason,group); }
    group.count++;
    const id = entry?.sourceApId;
    if (typeof id === "string" && /^[0-9]{1,20}$/.test(id) && group.sampleIds.length < 3 && !group.sampleIds.includes(id))
      group.sampleIds.push(id);
  }
  const other = new Map();
  for (const item of Array.isArray(run?.errors) ? run.errors : []) {
    const message = typeof item?.safeMessage === "string" ? item.safeMessage.trim() : "";
    if (!message || (excluded.length && excludedGeneric.has(message))) continue;
    const weight = Number.isSafeInteger(item.count) && item.count > 0 ? item.count : 1;
    other.set(message,(other.get(message) || 0) + weight);
  }
  const exclusions = [...grouped.values()].sort((a,b)=>b.count-a.count || a.label.localeCompare(b.label,"ko"));
  const details = [...other].map(([message,count])=>({message,count}));
  const labels = [
    ...exclusions.map(e=>e.label+" "+e.count.toLocaleString("ko-KR")+"건"),
    ...details.map(e=>e.count > 1 ? e.message+" · "+e.count.toLocaleString("ko-KR")+"건" : e.message)
  ];
  return {labels,exclusions,details,excludedCount:excluded.length};
}
