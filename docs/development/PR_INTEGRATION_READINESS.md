# Aruba AirWave AP Monitor — PR 통합 준비 상태

기준일: 2026-11-23 / 현장 검증 이전 / main 병합 보류

## 최신 개발 브랜치 계보

- PR #2 fix/tls-diagnostic-evidence → main: TLS 이름 및 신뢰 체인 분석
- PR #3 feature/airwave-temporary-certificate-pin → PR #2: IP·포트 접속 인증서 지문
- PR #4 feature/ap-centered-console → PR #3: AP 중심 C안 화면
- PR #5 feature/simplified-ap-monitoring → PR #4: 장애·미확인 우선 및 사건별 메모
- PR #6 feature/guided-live-connection → PR #5: 단계별 최초 Live 연결 안내
- PR #7(이번 작업) feature/airwave-stabilization-preview8 → PR #6: 원천 제외 오류 집계, 사용법, 안전 교체 안내

위 변경은 선행 PR을 기반으로 누적되어 있습니다. **중간 PR을 각각 별도 패치로 중복 적용하거나 main을 강제로 갱신하지 않습니다.** GitHub CI 통과가 곧 사내 Live 검증은 아닙니다.

## main 통합 전 필수 확인

1. PR #2~#7의 계보와 최신 브랜치의 포함 내용을 확인합니다. main의 변경 사항이 최신 커밋과 충돌하는지 승인 시점에 다시 확인합니다.
2. JS 문법/상태 테스트, 공통 .NET 테스트, Windows 전용·패키징 테스트, 추출 ZIP의 한글·공백 경로 실행 검사 모두 성공해야 합니다.
3. 저장된 수집 회차·원천 증거와 SourceApId, 사건별 조치 메모, 대장·CSV, 데이터셋/프로필, 암호화 계정 파일은 임의 변경하지 않습니다.
4. HTTPS 지문은 운영자 독립 확인 후에만 허용하고, HTTP 403 자동 재로그인은 하지 않습니다. Live LLDP/PoE 자동 수집은 검증되기 전까지 비활성입니다.
5. 현장 담당자가 Windows 11 PC에서 AirWave 로그인/조회·AP 대수·장애/미확인·메모·백업/복원을 검증합니다.
6. 검증 완료 및 별도 **main 병합 승인** 후 충돌·DB 스키마·서비스 경로를 재확인하고 정식 릴리스를 검토합니다.

## 의도적으로 해결하지 않은 현장 문제

- 원천 장비 2797, 3139, 2460 등이 포함된 AP 분류 미검증 284건은 실제 AP인지 아직 확인되지 않았습니다. 이번 변경은 **메시지 표시만 요약**하며 AP 분류 조건을 변경하지 않습니다.
- HTTP 403의 정확한 AirWave 권한/세션/프록시 원인은 미확정입니다. 기존 인증 차단·재시도 정책을 유지합니다.
- 스위치·전력·LLDP Live 상세 수집은 실제 원천 계약 검증 이전까지 비활성입니다.

통합 전 현장 조건은 [온사이트 검증](../guides/ONSITE_VALIDATION.md)과 [안전 업데이트 확인](../guides/UPDATE_CHECKLIST.md)에 기록합니다.
