"use strict";
// Pure guidance: no automatic certificate approval and no credential transmission.
const stage = (key, number, title, description, action, button, target = null) =>
  ({ key, number, title, description, action, button, target });

export const connectionSteps = ["서버 IP·포트", "읽기 계정", "HTTPS 인증서", "로그인·AP 조회", "첫 수집"];

export function determineSetupStage({
  mode, settings, health, report = null, diagnosticValid = false,
  settingsDirty = false, credentialsDirty = false, pinDirty = false, requireDiagnostic = false
} = {}) {
  if (!settings || !mode)
    return stage("loading", 0, "연결 설정 확인 중", "수집기에서 저장된 설정을 읽고 있습니다. 이전 결과를 성공으로 간주하지 않습니다.", "none", "확인 중");
  if (mode !== "live")
    return stage("demo", 0, "Demo 모드", "가상 자료를 이용하므로 AirWave 실제 연결과 계정 등록이 필요하지 않습니다.", "dashboard", "AP 현황 보기");
  const profile = settings.profile;
  if (!profile?.origin)
    return stage("origin", 0, "1. AirWave IP와 포트 저장", "평소 브라우저에 입력하는 https://IP:포트를 그대로 입력하세요. 프록시 경로와 수집 간격을 선택하고 [설정 저장]을 누르세요. 저장된 서버 주소 변경은 별도 데이터 폴더가 필요합니다.", "jump", "IP·포트 입력하기", "origin");
  if (settingsDirty)
    return stage("origin-draft", 0, "서버 입력 저장 필요", "아래 서버와 연결 경로에서 입력 내용을 검토하고 [설정 저장]을 누르세요. 저장 전에는 이전 진단 결과를 재사용하지 않습니다.", "jump", "서버 입력으로", "origin");
  if (settings.credentialStatus === "unsupported-platform")
    return stage("platform", 1, "Windows 실행 계정 필요", "Live 계정은 Windows DPAPI를 사용합니다. Windows 실행 계정으로 프로그램을 다시 실행하세요. 비밀번호를 평문 파일에 저장하지 않습니다.", "jump", "실행 계정 확인", "execution-account");
  if (settings.credentialStatus !== "registered" || settings.credentialRegistrationPending || credentialsDirty)
    return stage("credentials", 1, "2. AirWave 읽기 계정 등록", "AirWave 웹에서 AP 목록을 읽을 권한이 있는 계정으로 등록하세요. 비밀번호는 수집 PC의 Windows DPAPI로 보호합니다. 기존 실행 계정을 바꾸면 재등록이 필요할 수 있습니다.", "jump", "읽기 계정 입력하기", "username");
  if (pinDirty)
    return stage("pin-draft", 2, "인증서 입력 저장 또는 취소", "인증서 지문 입력 중입니다. 저장하거나 입력을 지운 뒤 연결 진단을 실행하세요.", "jump", "인증서 확인", "pin-title");
  if (health?.storageRecoveryPending)
    return stage("storage", 4, "저장소 확인 필요", "수집 저장소 복구 상태를 먼저 확인하세요. 이 상태에서는 성공한 첫 수집으로 표시하지 않습니다.", "jump", "수집기 상세 상태", "health-title");
  if (health?.authenticationBlocked)
    return stage("auth-blocked", 1, "인증 중단 확인", "권한/토큰 문제 또는 자격 증명 오류로 자동 인증이 중단됐습니다. HTTP 403은 자동 재로그인하지 않습니다. 계정·AirWave AP 목록 권한을 확인한 후 명시적으로 재시도하세요.", "jump", "인증 상태 확인", "retry-authentication");

  const fullyCommitted = !!health?.configured && !!health?.lastValidCommittedAtUtc &&
    health.lastOutcome === "Succeeded" && !health.updateDelayed;
  const partialCommitted = !!health?.configured && !!health?.lastValidCommittedAtUtc &&
    health.lastOutcome === "Partial";
  if (!diagnosticValid || !report) {
    if (!requireDiagnostic && fullyCommitted)
      return stage("done", 5, "연결 설정 완료", "유효한 AP 수집 회차가 저장돼 있습니다. 별도의 연결 진단을 반복할 필요가 없습니다. 최근 수집 시각은 AP 현황에서 확인하세요.", "dashboard", "AP 현황 보기");
    if (!requireDiagnostic && partialCommitted)
      return stage("partial", 4, "일부 AP만 확인됨", "최근 회차에 제외·미확인 레코드가 있습니다. 전체 정상으로 해석하지 말고 수집 기록에서 원인을 확인하세요.", "records", "수집 기록 확인");
    return stage("diagnostic", 2, "3–4. HTTPS와 AP 목록 연결 확인", "저장한 설정으로 [연결 진단 실행]을 누르세요. HTTPS, 로그인, AP 목록 응답을 확인하지만 이 진단만으로 AP 수집이 저장되지는 않습니다.", "diagnose", "연결 진단 실행", "run-diagnostics");
  }
  if (report.tlsResult === "failed") {
    const changed = report.tlsFailureCode === "pin-mismatch";
    return stage("tls", 2, changed ? "서버 인증서 변경 감지" : "HTTPS 인증서 확인 필요",
      (changed ? "등록된 인증서 지문과 현재 서버 지문이 다릅니다. 자동 승인하지 않습니다. " : "서버 인증서가 Windows 검증을 통과하지 못했습니다. ") +
      "IP·포트가 맞는지 확인하고, 관리 기록 등 독립적인 방법으로 SHA-256 지문을 대조한 다음에만 허용하세요. 인증서 갱신 또는 신뢰 CA 수정이 권장됩니다.", "jump", "인증서 확인으로", "pin-title");
  }
  if (Number(report.httpStatus) === 403)
    return stage("http403", 3, "AP 목록 조회 권한 확인", "AirWave가 HTTP 403을 반환했습니다. 자동 재로그인이나 지문 재등록으로 우회하지 않습니다. 계정의 /ap_list.xml 읽기 권한과 프록시 응답 출처를 확인하세요.", "jump", "진단 상세 보기", "diagnostic-result");
  if (report.tlsResult !== "validated" && report.tlsResult !== "pinned-exception")
    return stage("tls-unconfirmed", 2, "HTTPS 연결 미확인", "인증서 검증 결과가 확인되지 않았습니다. 진단 상세에서 TLS 실패 원인을 확인하세요.", "jump", "진단 상세 보기", "diagnostic-result");
  if (Number(report.outcome) !== 0 || Number(report.httpStatus) !== 200 || Number(report.apRequests) < 1)
    return stage("ap-read", 3, "AP 목록 단일 읽기 확인 필요", "진단 응답만으로 AP 목록 읽기를 성공이라고 판단할 수 없습니다. 제외 레코드·응답 형식과 오류 단계를 확인하세요.", "jump", "진단 상세 보기", "diagnostic-result");
  if (fullyCommitted)
    return stage("done", 5, "첫 수집까지 완료", "진단과 별개로 실제 AP 수집 회차가 유효하게 저장됐습니다. 정상 AP와 Down/미확인 AP를 현황에서 확인하세요.", "dashboard", "AP 현황 보기");
  if (partialCommitted)
    return stage("partial", 4, "수집됐지만 일부 확인 필요", "저장된 회차에 제외·미확인 레코드가 있습니다. 감시 대상이 전부 수집됐다고 간주하지 않습니다.", "records", "수집 기록 확인");
  if (health?.paused)
    return stage("paused", 4, "5. 수집 일시정지 해제", "수집기가 일시정지 중입니다. 명시적으로 재개해야 첫 AP 수집을 저장할 수 있습니다.", "resume", "수집 재개");
  if (health?.activeRunId || health?.pendingApQueueId ||
      (health?.phase === "Preparing" || health?.phase === "Collecting" || health?.phase === "Finalizing"))
    return stage("waiting", 4, "5. AP 수집 진행 중", "현재 회차가 완료되고 유효한 결과가 저장되면 다음 화면으로 이동할 수 있습니다. 저장 전에는 성공으로 표시하지 않습니다.", "records", "수집 기록 확인");
  return stage("collect", 4, "5. 실제 AP 수집과 저장 확인", "연결 진단은 통과했습니다. [첫 AP 수집 실행]을 눌러 저장 회차를 만든 뒤 결과를 확인하세요. 이 작업은 AirWave 읽기 요청만 수행합니다.", "collect", "첫 AP 수집 실행");
}
