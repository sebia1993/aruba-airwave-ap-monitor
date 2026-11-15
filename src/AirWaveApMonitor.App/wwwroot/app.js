"use strict";
(async () => {
  const byId = id => document.getElementById(id);
  let token, current, demo = true, busy = false;
  const message = (text, error = false) => { byId("error").hidden = !error; byId("error").textContent = error ? text : ""; byId("message").textContent = error ? "" : text; };
  const setBusy = value => { busy = value; document.querySelectorAll("button").forEach(button => { button.disabled = value; }); };
  async function request(path, body) {
    const response = await fetch(path, { method: "POST", headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": token }, body: body === undefined ? undefined : JSON.stringify(body) });
    let value; try { value = await response.json(); } catch { value = null; }
    if (!response.ok) throw new Error(value?.message || value?.title || "요청을 완료하지 못했습니다. 화면을 새로고침한 뒤 다시 시도하세요.");
    return value;
  }
  async function loadSettings() {
    const response = await fetch("/api/settings"); if (!response.ok) throw new Error("설정을 읽지 못했습니다.");
    current = await response.json(); const profile = current.profile;
    byId("origin").value = profile?.origin || "";
    byId("origin").readOnly = !!profile;
    byId("interval").value = profile?.settings.apIntervalMinutes || 5;
    byId("proxy-mode").value = ["System", "Direct", "Fixed"][profile?.proxy.mode || 0];
    byId("proxy-address").value = profile?.proxy.address || "";
    byId("proxy-address").disabled = byId("proxy-mode").value !== "Fixed";
    byId("execution-account").textContent = current.executionAccount;
    byId("credential-status").textContent = ({ "synthetic-memory": "가상 메모리 자격 증명만 사용 · 실제 입력 안 받음", "unsupported-platform": "현재 OS에서 Windows DPAPI 미지원 · Windows에서 등록 필요", "missing": "미등록", "registered": "현재 실행 계정에서 복호화 확인", "reregistration-required": "복호화 실패 · 자격 증명 재등록 필요" })[current.credentialStatus];
    byId("authentication-status").textContent = current.authenticationBlocked ? "중단됨 · 자격 증명 확인 또는 명시적 재시도 필요" : "진단 요청 시 허용 · 자동 수집은 미구현";
    byId("credentials-form").hidden = demo || current.credentialStatus === "unsupported-platform";
    byId("mode").textContent = demo ? "DEMO · 가상 자료" : profile ? "LIVE · 연결 미검증" : "LIVE · 미설정";
  }
  async function perform(action) { if (busy) return; setBusy(true); message(""); try { await action(); } catch (error) { message(error.message, true); } finally { setBusy(false); } }
  byId("proxy-mode").addEventListener("change", () => { byId("proxy-address").disabled = byId("proxy-mode").value !== "Fixed"; });
  byId("settings-form").addEventListener("submit", event => { event.preventDefault(); perform(async () => {
    await request("/api/settings", { origin: byId("origin").value, apIntervalMinutes: Number(byId("interval").value), proxyMode: byId("proxy-mode").value, proxyAddress: byId("proxy-mode").value === "Fixed" ? byId("proxy-address").value : null, expectedConfigurationVersion: current.profile?.configurationVersion || 0 });
    await loadSettings(); message("연결 설정을 저장했습니다. 연결 성공 여부는 진단으로 확인하세요.");
  }); });
  byId("credentials-form").addEventListener("submit", event => { event.preventDefault(); perform(async () => {
    const username = byId("username").value, password = byId("password").value; byId("password").value = "";
    await request("/api/credentials", { username, password }); byId("username").value = "";
    await loadSettings(); message("실행 계정의 DPAPI로 자격 증명을 등록했습니다.");
  }); });
  byId("retry-authentication").addEventListener("click", () => perform(async () => { await request("/api/authentication/retry"); await loadSettings(); message("명시적 재시도를 허용했습니다. 연결 진단을 실행하세요."); }));
  byId("run-diagnostics").addEventListener("click", () => perform(async () => {
    message(demo ? "합성 XML을 파싱하고 있습니다." : "연결 진단 중입니다. 전체 제한 시간은 120초입니다.");
    const report = await request("/api/diagnostics"); byId("diagnostic-result").hidden = false;
    byId("diagnostic-summary").textContent = `${demo ? "가상 파서 진단" : "연결 진단"}: ${["읽기 성공", "부분 유효 · 확인 필요", "실패"][report.outcome]}`;
    const details = byId("diagnostic-details"); details.replaceChildren();
    const tls = ({ "not-applicable-demo": "해당 없음 · 가상 자료", "validated": "HTTPS 연결 검증 통과", "failed": "실패", "not-observed": "미관측" })[report.tlsResult] || report.tlsResult;
    const route = ({ "synthetic-memory": "가상 메모리", "system-execution-account": "실행 계정의 시스템 프록시", "direct": "직접 연결", "fixed-proxy": "선택한 고정 프록시" })[report.connectionPath];
    for (const [label, value] of [["진단 ID", report.diagnosticId], ["프로세스 실행 계정", report.executionAccount], ["연결 경로", route], ["TLS", tls], ["HTTP_STATUS", report.httpStatus ?? "미수신"], ["로그인 / AP 요청 수", `${report.loginRequests} / ${report.apRequests}`], ["파싱 / 제외 레코드", `${report.parsedRecords} / ${report.excludedRecords}`], ["소요 시간", `${report.elapsedMilliseconds} ms`]]) { const row = document.createElement("div"), dt = document.createElement("dt"), dd = document.createElement("dd"); dt.textContent = label; dd.textContent = value; row.append(dt, dd); details.append(row); }
    const stages = byId("diagnostic-stages"); stages.replaceChildren(); report.stages.forEach(stage => { const li = document.createElement("li"); li.textContent = `${stage.name} · ${stage.status} · ${stage.elapsedMilliseconds} ms · ${stage.message}`; stages.append(li); });
    const notes = byId("diagnostic-notes"); notes.replaceChildren(); [...report.errors.map(error => error.safeMessage), ...report.verificationNotes].forEach(text => { const li = document.createElement("li"); li.textContent = text; notes.append(li); });
    await loadSettings(); message("진단이 끝났습니다. 이력 DB에는 저장하지 않았습니다.");
  }));
  try {
    const [healthResponse, capabilitiesResponse, csrfResponse] = await Promise.all([fetch("/api/health"), fetch("/api/capabilities/windows"), fetch("/api/antiforgery")]);
    if (!healthResponse.ok || !capabilitiesResponse.ok || !csrfResponse.ok) throw new Error("시작 상태를 읽을 수 없습니다.");
    const health = await healthResponse.json(), capabilities = await capabilitiesResponse.json(); token = (await csrfResponse.json()).token; demo = health.mode === "demo";
    byId("mode-detail").textContent = demo ? "Demo · 실제 AirWave 연결 없음" : "Live · 실제 연결은 진단 결과 확인 필요";
    byId("mode-description").textContent = demo ? "가상 자료를 사용하는 Demo 모드입니다. 실제 계정과 운영 데이터에 접근하지 않습니다." : "초기 연결·읽기 진단을 위한 Live 모드입니다. Windows 실행 계정에서 자격 증명을 등록하세요.";
    byId("scenario-detail").textContent = demo ? "2,048대 합성 XML · 실제 AP 파서 사용" : "Live 모드에서는 가상 공급자를 사용하지 않음";
    byId("windows-detail").textContent = capabilities.message; await loadSettings();
  } catch (error) { message(error.message, true); setBusy(true); }
})();
