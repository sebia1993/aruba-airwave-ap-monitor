export function countText(value) {
  return typeof value === "number" && Number.isFinite(value) ? value.toLocaleString("ko-KR") : "—";
}

export function createRequestGate() {
  let generation = 0;
  return {
    begin: () => ++generation,
    isCurrent: ticket => ticket === generation,
    invalidate: () => { generation++; }
  };
}

export function dashboardQuery(filters = {}, selection = null, exportAll = false) {
  if (exportAll && (!selection || !Number.isInteger(selection.runId) || selection.runId <= 0))
    throw new Error("내보낼 수집 회차를 먼저 조회하세요.");
  const params = new URLSearchParams();
  if (selection?.runId != null) params.set("runId", selection.runId);
  if (selection?.scopeVersion != null) params.set("scopeVersion", selection.scopeVersion);
  if (filters.search?.trim()) params.set("search", filters.search.trim());
  params.set("filter", filters.filter || "OpenIncidents");
  params.set("sort", filters.sort || "FirstDownAt");
  params.set("descending", String(filters.descending !== false));
  if (filters.managementStatus) params.set("managementStatus", filters.managementStatus);
  params.set("hidePlanned", String(!!filters.hidePlanned));
  if (!exportAll) {
    params.set("page", filters.page || 1);
    params.set("pageSize", filters.pageSize || 50);
  }
  return params;
}

export function observationStatus(row) {
  if (row.currentState === "Down") return { label: "Down 확인", tone: "red" };
  if (row.currentState === "Up") return { label: "Up 확인", tone: "green" };
  return { label: "이번 미확인", tone: "amber" };
}

export function incidentElapsed(tracking, referenceUtc) {
  if (!tracking || tracking.timeNeedsReview) return null;
  const end = tracking.recoveredAtUtc || tracking.trackingEndedAtUtc || referenceUtc;
  const startMs = Date.parse(tracking.firstDownAtUtc);
  const endMs = Date.parse(end);
  return Number.isFinite(startMs) && Number.isFinite(endMs) && endMs >= startMs ? endMs - startMs : null;
}

export function settingsIdentity(profile) {
  return profile ? JSON.stringify([profile.datasetId, profile.origin, profile.configurationVersion,
    profile.scopeVersion, profile.credentialsVersion]) : "unconfigured";
}

export function shouldFollowLatest(current, health) {
  return current.view === "dashboard" && !current.historical && !current.loading
    && Number.isInteger(health?.lastFinalRunId) && health.lastFinalRunId > 0
    && health.lastFinalRunId !== current.runId;
}


export function authenticationUiState(settings, health) {
  const needsRegistration = !!settings?.credentialRegistrationPending
    || settings?.credentialStatus === "reregistration-required"
    || health?.blockedReason === "credential-reregistration-required";
  const blocked = health?.authenticationBlocked ?? settings?.authenticationBlocked;
  const label = needsRegistration ? "자격 증명 재등록 필요 · 명시적 재시도로 해제할 수 없음"
    : blocked === true ? "인증 중단 · 자격 증명 확인 또는 명시적 재시도 필요"
    : blocked === false ? "인증 재시도 차단 없음 · 연결 성공은 수집·진단 결과 확인 필요"
    : "인증 상태 미확인";
  return { label, needsRegistration, canRetry: !!settings?.profile && blocked === true && !needsRegistration };
}
