"use strict";
(async () => {
  try {
    const [healthResponse, capabilitiesResponse] = await Promise.all([
      fetch("/api/health"), fetch("/api/capabilities/windows")
    ]);
    if (!healthResponse.ok || !capabilitiesResponse.ok) throw new Error("시작 상태를 읽을 수 없습니다.");
    const health = await healthResponse.json();
    const capabilities = await capabilitiesResponse.json();
    const demo = health.mode === "demo";
    document.getElementById("mode").textContent = demo ? "DEMO · 가상 자료" : "LIVE · 미설정";
    document.getElementById("mode-detail").textContent = demo ? "Demo — 실제 AirWave 연결 없음" : "Live — 서버·계정 미설정";
    document.getElementById("mode-description").textContent = demo
      ? "가상 자료를 사용하는 Demo 모드입니다. 실제 계정이나 운영 데이터에 접근하지 않습니다."
      : "실제 연결을 위한 Live 모드입니다. 연결이 설정되지 않았으며 가상 데이터로 대체하지 않습니다.";
    document.getElementById("scenario-detail").textContent = demo
      ? "2,048대 자료 · 정상 / 신규 Down / 복구 / 전체 실패 / 일부 누락 준비"
      : "Live 모드에서는 가상 공급자를 사용하지 않음";
    document.getElementById("windows-detail").textContent = capabilities.message;
  } catch (error) {
    const target = document.getElementById("error");
    target.textContent = error.message;
    target.hidden = false;
  }
})();
