# 개발 진행 및 검증 기록

기준일: 2026-11-16. 실행 기준은 `superpowers/plans/2026-11-15-macos-codex-implementation.md`다.

개발 산출물과 사내 운영 검증은 별개다. 아래 가중치는 최초 계획대로 고정하며 완료 증거가 있는 작업에만 점수를 부여한다.

승인된 수용 검증 20개와 구체적인 검사·한계는 [A01–A20 인수 근거표](ACCEPTANCE_EVIDENCE.md)에 연결한다.

| 작업 | 가중치 | 현재 상태 | 완료 근거 |
|---|---:|---|---|
| 1. Mac 골격·계약·모드 분리 | 8 | 완료 | `454dee4`, Mac 34 통과, Windows CI 성공, 독립 검토 통과 |
| 2. API·진단·계정 보호 | 12 | 완료 | `9366a71`, Mac 87 통과, Windows CI 성공, 초기 ZIP·독립 검토 PASS |
| 3. 식별·상태·사건 | 14 | 완료 | `e007bae`, Mac 167 통과, Windows CI 성공, 독립 재검토 PASS |
| 4. 원자적 저장·회차 현황 | 14 | 저장 구조 보완 중 | 초기 `0f15663` 검증은 보존. 최종 원문 감사에서 정상 AP 전량 사실 누적을 확인하여 Down/미확인 중심 구조 및 정확한 과거 조회를 보완 |
| 5. 자동 수집·건강 상태 | 10 | 완료 | `d66a3a2`, Mac 268 통과, Windows CI 자동 실행·재시작·중복 차단·독립 검토 PASS |
| 6. 핵심 화면·CSV | 12 | 최종 Windows 검증 대기 | `818e740`·`efb954b`, Mac 327·Node 16, 실제 브라우저·Windows CI·독립 검토 PASS |
| 7. Windows 운영·백업·패키지 | 10 | 완료 | `22b0f16`, Mac 378·Windows CI·실제 서비스/예약 작업/복원·검증 ZIP·독립 검토 PASS |
| 8. 운영 이력·가져오기 | 12 | 완료 | `ab9155b`, Mac 532·Node 43·Python 8, 실제 브라우저·Windows CI·검증 ZIP·독립 검토 PASS |
| 9. 조건부 부가 관측 | 5 | 최종 검증 중 | 공통/Demo 구현, Mac 890·Node 100·Python 11, 실제 2,048대·전력 3회·재시작·독립 검토 PASS; 최종 Windows 대기 |
| 10. 통합 확인·인수 자료 | 3 | 최종 검증 중 | clean 검증·2,000/5,000대 측정·스크립트·A01–A20·안내 PASS; 최종 Windows/ZIP 대기 |

**완료로 확인된 개발 진행률: 66 / 100%.** 이전 92점에서 미충족이 확인된 Task 4의 14점과 Task 6의 12점을 제외했다. Task 9~10 진행 중 작업도 아직 점수에 넣지 않았으며 실환경 운영 보장률이 아니다.

아래 Task별 결과는 각 단계 당시의 소스·패키지·검증 기록이다. 각 절의 진행률·다음 작업·당시 미구현 설명은 그 시점의 기록이며 현재 요약은 위 표와 최신 Task 결과를 따른다.

## Windows 재실행 — Demo 초기 관측 대기 진단

소스 `447f2ae`의 [Windows run 43769015284](https://github.com/sebia1993/aruba-airwave-ap-monitor/actions/runs/43769015284)는 Frontend/Restore/Build 성공 후 공통 **885 통과·1 실패·Mac/Unix 4 제외**로 실패했다. 실패는 `FailedSeed_StopsCurrentAdmissionWithoutBusyLoop`의 재시도 후 Idle 10초 대기이며 전체 테스트 시간22.559초에는 요청·대기·정리가 포함된다. Windows전용/Python/publish/native/ZIP은 미실행이며 최종 ZIP은 없다.

같은 실행의 정상 Demo 재시작 검사는 Idle 대기7.024/6.911초로 통과했다. 실패 건은 당시 Phase·요청 결과가 없어 지연과 저장 복구 상태를 구별하지 못한다. 120초 수집 예산은 이 fixture의 수동 시계로, 결과 반영/복구5초 예산은 실제 시계로 동작하므로 단순 총시간으로 원인을 단정하지 않는다.

제품과10초 대기 제한은 변경하지 않았다. 해당 검사에 요청 receipt·Stopwatch·정리 전 health/sourceCalls 진단을 추가하고 재시도 Accepted를 확인한다. Mac DemoPrehistory **12/12 통과**; 실패 seed는1회만 호출됐고 재시도 Accepted/회차4, 정상 Idle0.725초/sourceCalls4/저장 오류없음이었다. 다음은 진단을 포함한 새 전체 Windows 실행이다. 최초 원인은 미확정이며 확정66%를 유지한다.

## 최종 Windows 첫 실행 — 테스트 줄바꿈 보완

소스 `8ef2650`의 [Windows run 43768748800](https://github.com/sebia1993/aruba-airwave-ap-monitor/actions/runs/43768748800)는 Frontend 단계에서 실패했다. 새 요구사항 UI 검사가 LF 경계로 앱 시작 부분을 제외하도록 작성되어 Windows CRLF 체크아웃에서는 앱 시작 코드까지 fixture에서 실행했다. 실제 로그의 `enrichment?.renderHealth is not a function`을 제품 파일을 바꾸지 않은 CRLF 임시 복사본에서 동일하게 재현했다.

테스트가 읽은 소스의 줄바꿈만 LF로 정규화해 원래 의도한 함수/이벤트 범위로 실행하도록 보완했다. 제품 코드나 실패를 가리는 stub은 바꾸지 않았다. 해당23개 검사는 LF/CRLF 각각 UTC·미국 서부·서울 **6조합 모두 통과**했다. Windows 첫 실행의 백엔드·publish·ZIP·native 단계는 미실행이며 artifact도0개다. 다음은 새 소스의 기존 전체 workflow 재실행·정확한 ZIP 확인이다. 확정 진행률66%, Windows 성공 주장은 아직 없다.

## 최종 로컬 인수 — Windows 실행 대기

변경: 실제 브라우저에서 발견한 범위 API의 numeric enum/화면 문자열 불일치를 고쳤다. GET·POST·대조 결과의 3개 실제 HTTP 회귀 RED→GREEN을 추가했고 저장 프로필 형식은 유지했다. Task4·6 보완, Task9 조건부 관측, Task10 규모 측정·자료를 통합했다.

실제 검증: 최종 `scripts/verify-mac.sh` 성공. Release 경고 0·오류 0, 공통 **890 통과**, Windows 전용 **9 Mac 제외**, Frontend **100 통과**, Python **11 통과**. 실제 Mac 1280×720 브라우저에서 KST·기간·전환수·미복구 필터·0/8대 CSV 조건행·초안 보존·선택 AP 3대·사용자 합성 대조를 확인했다. 재시작 뒤 프로필/감사/과거 AP3 응답은 동일했고, 범위1의 전력 첫500개와 전체 **36,864표본** 수가 유지됐다. 보존 기준 시각은 시작 시 정리 작업으로 전진하며 고정된 과거 표본 값과 구분한다. 콘솔 오류/경고0, 테스트 프로세스 정상정지 확인.

실측: clean 소스 `ecca1d2`의 AP 2,000/5,000대 각각 반복 장애9회·정상 유지10회 검증. 별도 실제 Demo 연결1회·전력3회는 각12,288표본과 AP 성공 시각 불변을 확인했다. [측정 문서](PERFORMANCE_MEASUREMENTS.md)는 극단 시나리오의 시간/크기 증가, 정상 facts 재사용과 남은 운영 binding 비용, 전력 30일 산술 외삽을 구분한다. 최종 scope HTTP 직렬화 보완은 이 저장/측정 알고리즘을 바꾸지 않았다.

다음: 현재 작업 브랜치의 Windows 전체 workflow·native 실행·서비스/예약 작업/복원·실제 ZIP 해시 확인. 완료 점수 **66% 유지**. 원본 계획/설계/모의화면 변경 없음, 원문·A01–A20 독립 재검토에서 열린 P1/P2 없음. 실제 AirWave/Windows11·AD/실장비·장기 누적/실제 Windows GUI는 미확인이다.

## Task 4·6 보완 통합 — 최종 검증 진행 중

변경: schema 6은 정상 AP 상세값과 대상 집합을 공유하고 회차별 Down·미확인 상세만 추가한다. 기존 schema≤5와 과거 회차를 보존한다. KST·기간·전환 횟수·장기 미복구·CSV 조회조건과 감시 범위/기준 대조 UI를 연결했다. 복원 시 범위 보존, 5,000개 ID 요청, 범위 밖 오류의 원천/적용 결과 분리를 보완했다.

실제 검증: 통합 소스의 `scripts/verify-mac.sh`가 성공했다. Release 경고 0·오류 0, 공통 **887 통과**, Windows 전용 **9 Mac 제외**, Frontend **100 통과**, Python **11 통과**. Frontend는 UTC·미국 서부·서울에서 각각 100개가 통과했다. compact 저장과 UI/측정 도구 독립 검토에 열린 P1/P2는 없다. 부분 회차의 미관측 대상은 대조 등록을 막으며 전대상 관측 Partial은 허용한다.

다음: clean 소스/DLL 해시를 고정한 AP 2,000/5,000대 측정, 실제 2,048대 Demo 브라우저·전력 연속 수집/재시작, 최종 Windows 전체 실행과 정확한 ZIP 검증. 확정 진행률 **66% 유지**. 실제 AirWave/사내 Windows 11·AD/장비 및 장기 용량은 미확인이다.

## 최종 원문 감사에 따른 Task 4 보완

설계 §7은 AP 현재값·변경 사건과 회차 요약·Down/미확인 중심 저장을 요구한다. 모든 AP의 상세 facts를 매회차 누적하는 현재 구현은 이 요구를 충족하지 않는다. 이전의 “정보 손실 없는 구현 차이·후속 최적화” 판단을 정정하며, 완료를 유보하고 [보완 계획](superpowers/plans/2026-11-16-compact-history-correction.md)에 따라 additive schema6과 정확한 과거 조회 재구성을 구현한다. 기존 schema≤5 자료·사건 JSON·ID와 원본 계획/설계/모의화면은 보존한다.

실제 검증: 보완 전 소스 `bc4cd3`의 두 번째 [Windows CI 43758016206](https://github.com/sebia1993/aruba-airwave-ap-monitor/actions/runs/43758016206)는 공통 **798 통과·Mac/Unix 4 제외**, Windows 전용 **9**, Node **76**을 통과했다. Python은 **6 통과·1 실패·Unix 2 제외**로, 문서 링크 검사의 LF/CRLF 차이 때문에 전체 workflow가 실패하고 publish/ZIP 단계는 실행되지 않았다. 내용 검사를 개행과 독립적으로 만들고 원본 문서 바이트 보존 검사를 추가한 로컬 Python **9개가 통과**했다. 직전 Demo Idle 대기 검사는 제한 시간 변경 없이 통과했지만 최초 실패 원인을 확정하지 않는다.

다음: compact storage RED→GREEN·독립 검토, 전체 Mac/브라우저·2,000/5,000대 새 측정, Windows 전체 workflow와 정확한 CI ZIP 확인. 현재 확정 **78%**. 사내 Windows 11/AD·실제 AirWave/장비·실제 장기 용량은 미확인이다.

## 비저장 원문 감사에 따른 Task 6 보완

추가 확정 항목은 한국 표준시 고정 표시, 사건·회차 기간 UI, 기간 내 Up→Down 전환 수(최초 Down 제외), 장기 미복구 경과 필터, 명시적 감시 범위·기준 대조 결과 저장 경로다. 기존 API에 있는 기간 질의를 UI에 연결하며, 알려진 원천 AP ID로 선택 범위를 고정하고 기대 대상 집합과 화면 필터를 구분한다. 실제 AirWave 원격 폴더/그룹 API 필터 검증으로 확대하지 않는다. 보완 전 공통 테스트 통과는 이 누락 기능의 증거가 아니다. 현재 확정 **66%**, 다음은 병행 보완 통합·실제 사용 흐름·Windows 재검증이다.

## 착수 확인

- 작업 폴더가 비어 있어 계획에 지정된 정확한 저장소를 복제했다. 기준 커밋 `727ddb2`, 기존 미커밋 변경 없음. 작업 브랜치 `codex/macos-v0.1`.
- Drive 문서와 저장소 계획은 끝 개행을 제외하고 동일했다. 계획 487줄·설계 580줄·README·기존 DEVELOPMENT_LOG·모의화면 HTML 전체를 읽었다.
- 실제 Mac 브라우저에서 정상·전체 실패·부분 누락 모의화면을 확인했다. 전체 실패의 Down은 미확인, 미복구 8건 유지. 부분 누락은 Up 2,038·Down 7·미확인 3·미복구 8건이다.
- `uname -m`: `arm64`. 기존 .NET SDK 없음. 공식 배포 메타데이터의 SHA512와 일치하는 .NET SDK `10.0.404`를 사용자 경로 `~/.dotnet`에 설치했다. `dotnet --info`: `osx-arm64`, 런타임 `10.0.15`.
- 기존 Windows workflow 0개. 최신 사용자 AGENTS 지침에 따라 최소 `windows-latest` CI를 추가한다. 계획의 선택적 CI 문구보다 사용자 지침을 우선한다.

## Task 1 결과

변경: .NET 10 공통 솔루션/계약, 모드별 영속 표시자, 비밀 출력·Demo 접근 차단, 2,048대 시나리오와 선행 관측, 루프백 초기 화면, Mac 실행 스크립트, Windows CI. 상세 내용은 `../DEVELOPMENT_LOG.md`의 Task 1 기록에 있다.

| 검증 수준 | 실제 결과 |
|---|---|
| Mac Release 빌드 | `dotnet build AirWaveApMonitor.slnx -c Release`: 경고 0·오류 0 |
| Mac 공통 테스트 | `dotnet test AirWaveApMonitor.slnx -c Release --no-build`: 34 통과 |
| Mac의 Windows 전용 검사 | 2개 명시적 건너뜀; Windows 통과로 계산하지 않음 |
| Mac Demo | 기본 명령/브라우저 확인, DEMO 표시, 루프백 `127.0.0.1:8765`만 수신 |
| 초기 API | health `bootstrap`, 수집/저장 `not-implemented`; 미구현 dashboard 503 |
| Windows CI | [run 43725557307](https://github.com/sebia1993/aruba-airwave-ap-monitor/actions/runs/43725557307), 커밋 `454dee4`, 전체 성공 |
| Windows CI 테스트 | 공통 32 통과·Mac/Unix 전용 2 건너뜀 + Windows 경로 2 통과, 실패 0 |
| Windows 게시·실행 | `win-x64` self-contained 게시 성공. 한글·공백 경로에서 EXE 시작, HTTP 페이지/health, 루프백만 수신 확인 |
| 독립 코드 검토 | 요구 준수 PASS / 품질 PASS, Critical·Important 없음 |
| 사내 Windows 11 | 아직 미확인. CI의 실제 OS는 Windows Server 2025이며 사내 PC 확인을 대신하지 않음 |

TDD 실패→통과 증거와 전체 로그는 로컬 `.superpowers/sdd/2026-11-15-macos-codex-implementation/evidence/`에 보관한다. Git에는 실제 운영 로그·자료를 넣지 않는다.

### 다음 작업과 미완료 범위

- Task 2: 공식 AirWave 읽기 어댑터·안전 XML 파서·계정 보호·단계별 연결 진단과 초기 반입 패키지. 최초 변경 API부터 Host/동일 출처/위조 요청 방지를 적용한다.
- 검토 Minor 1건: `ProfileSnapshot`의 기본 System.Text.Json 역직렬화는 생성자 컬렉션 형식 차이로 실패한다. 현재 부트스트랩은 이를 호출하지 않는다. Task 2에서 실제 프로필 저장/복원을 연결하며 실패→통과 회귀 검사와 호환 생성자로 해결했다.
- 실제 AP 자동 수집·상태 전환·SQLite·운영 이력은 후속 작업이다. Task 1 화면은 정상 감시를 주장하지 않는다.
- 소스 커밋 `454dee4`를 원격 작업 브랜치에 보존했다. 정식 릴리즈·main 병합 없음. 이 단계에 전달용 반입 ZIP은 생성하지 않았으며 CI의 임시 게시 폴더를 다운로드 산출물로 표시하지 않는다.
- 실행: 저장소에서 `./scripts/dev-mac.sh`. 상세 안내: `guides/MAC_DEVELOPMENT.md`.

## Task 2 결과

변경: 공식 문서 기반 AirWave 로그인·AP 읽기, 안전 XML 파서, 세션·인증 중단·본문/전체 시간 제한, Windows DPAPI 계정 보호, 프로필 저장과 변경 API 보호, 실제 설정·진단 화면, 초기 self-contained Windows 패키지 생성. 세부 구현·검토 수정은 `../DEVELOPMENT_LOG.md`의 Task 2 기록에 있다.

| 검증 수준 | 실제 결과 |
|---|---|
| Mac Release 빌드 | 경고 0·오류 0 |
| Mac 공통 테스트 | 87 통과 |
| Mac의 Windows 전용 검사 | DPAPI 3·경로 2 = 5개 명시적 건너뜀 |
| 패키지 행동 검사 | Python 5 통과; 민감 자료 혼입·실패 시 기존 파일 보존 포함 |
| 실제 Mac 브라우저 | 설정 저장·새로고침 유지·PAC URL 거부·Demo 2,048대 파싱/제외 0, console error 0 |
| 독립 검토 | 요구 준수·품질·패키지 PASS; 지적 사항 회귀 재현 후 수정 |
| Windows CI | [run 43728301653](https://github.com/sebia1993/aruba-airwave-ap-monitor/actions/runs/43728301653), 소스 `9366a71`, 전체 성공 |
| Windows 실행 테스트 | 공통 84 통과·Mac/Unix 3 건너뜀, DPAPI/경로 5 통과, Python 3 통과·Unix 2 건너뜀 |
| Windows 패키지 | self-contained 게시, ZIP 압축 해제·355개 해시, PowerShell 5.1 launcher 구문, 한글/공백 경로 EXE·루프백·Origin 거부·Demo 2,048대 진단 통과 |
| 사내 Windows 11·AirWave | 미확인 |

진단 ID는 운영 RunId가 아니며 자동 감시·SQLite 이력·서비스 설치를 아직 제공하지 않는다. 초기 패키지 안내는 `guides/INITIAL_DIAGNOSTICS.md`, 현장 기록 양식은 `guides/ONSITE_INITIAL_CHECKLIST.md`다. 사내 검증은 개발 진행률과 별개다.

다음: Task 3 순수 AP 식별/세대/감시 구간/상태 전환. Task 2와 공유 파일이 충돌하지 않도록 별도 managed worktree에서 구현하고 통합 전에 검토한다. 완료 점수는 Task 1·2의 20점이다. Windows CI 실제 OS는 Server 2025이며 사내 Windows 11, launcher를 통한 직접 실행·서비스·재부팅은 별도 확인 항목이다.

### 초기 Windows 반입 산출물

- 로컬 위치: `artifacts/win-x64/AirWaveAPMonitor-0.1.0-diagnostics-9366a71c3447-20261115T150941Z-610013.zip` 및 같은 이름의 압축 해제 폴더, `.zip.sha256`.
- 버전 `0.1.0-diagnostics`, 소스 `9366a71c3447237e2be920a19838d32a0ba33539`, 미커밋 변경 없음, 사내 검증 완료 표시 없음.
- ZIP 크기 **48,707,315 bytes**, 내부 **355개 파일**의 SHA256과 ZIP SHA256을 실제 대조했다.
- ZIP SHA256: `91b0a395c14e99d79ed93c1e7c8be917b03e0dcbc42da8ab58c2ea565d802b3e`.
- Mac에서 생성한 ZIP이다. Windows CI에서는 같은 소스 커밋으로 별도 생성한 ZIP을 실행했다. Mac ZIP의 정확한 바이트를 Windows에서 실행한 검증과는 구분한다.
- 범위: 연결 설정·Windows 계정 보호·단일 읽기 진단. 자동 감시·SQLite 이력·서비스·최종 운영 패키지는 아직 없다. 정식 릴리즈 게시 없이 원격 작업 브랜치에 소스만 보존했다.

## Task 3 결과

변경: 순수 AP 식별·세대 판단·감시 구간·상태 전환·회차 집계 코어. 최초 Down/신규/기존/복구/미확인, 중복 제외와 시리얼 충돌, 관측 공백, 일시 제외와 명시 종료, 원래 시각·회차 순서를 구분한다. 결과를 실제 SQLite에 적용하고 화면에 연결하는 것은 Task 4~6이다.

| 검증 수준 | 실제 결과 |
|---|---|
| Task 3 순수 로직 | 80개 통과; 상태표와 요구 명명 사례, 2,048대 시나리오 포함 |
| root 통합 Mac Release | 경고 0·오류 0, 공통 167 통과, Windows 전용 5개 명시적 건너뜀 |
| 독립 검토 | 요구 준수·품질 PASS; 미관측 재편입 및 잘못된 enum 회귀 수정 |
| Windows CI | [run 43730366360](https://github.com/sebia1993/aruba-airwave-ap-monitor/actions/runs/43730366360), 소스 `e007bae`, 전체 성공 |
| Windows 실행 테스트 | 공통 164 통과·Mac/Unix 3 건너뜀, Windows 전용 5 통과, Python 3 통과·Unix 2 건너뜀 |
| Windows 패키지 | self-contained 게시·ZIP 무결성·PowerShell 5.1 launcher 구문·한글/공백 경로 EXE 실행·루프백·Demo 진단 통과 |
| 실제 저장·실환경 | SQLite·자동 수집·현황 UI는 아직 연결하지 않음. 실제 장비 미확인 |

다음은 Task 4의 원자적 SQLite 저장과 재시작 복원이다. 코어 결과의 새 ID를 저장 트랜잭션에서 할당하고, 같은 회차의 목록·집계·사건·스냅샷을 함께 확정해야 한다. 실패 회차는 마지막 성공 회차로 숨기지 않는다. Task 3까지 34%이며 Windows CI 실제 OS는 Server 2025다. 사내 Windows 11·실제 AirWave·운영 계정·서비스 실행을 확인한 것은 아니다.

## Task 4 결과

변경: 실제 SQLite의 원자적 AP 회차·상태·사건·스냅샷 저장, 불변 과거 조회, 중단 회차 복원, 인증 중단 영속화와 앱 시작 시 DB 검증을 구현했다. 범위가 달라도 같은 AP의 장비 세대·열린 사건은 유지하고, 과거 회차의 대상과 값은 고정한다. 진단 요청은 운영 회차를 만들지 않는다.

| 검증 수준 | 실제 결과 |
|---|---|
| root 통합 Mac Release | 경고 0·오류 0, 공통 240 통과, Windows 전용 5개 명시적 건너뜀 |
| 실제 SQLite 검사 | 저장 집중 37개; 트랜잭션 롤백·응답 유실 뒤 재시도·재열기·중단·범위 변경·과거 스냅샷·잘못된 스키마 거부 포함 |
| 2,048대 통합 시나리오 | 실제 XML 파서→SQLite→재열기. 전체 실패의 현재 대수 null, 부분 누락 Up 2,038·Down 7·미확인 3·미복구 8 확인 |
| 인증 영속화 | 재시작·헤더 거절 후 본문 오류·취소·저장 실패·재등록 실패·버전 격리 검사. Mac DPAPI 미지원은 상태 변경 전 거부 |
| 시작·진단 경계 | HTTP 제공 전 SQLite 준비, 손상 DB 원본 보존·시작 거부, 실행 중 DB 오류의 안전한 503, 진단의 운영 회차 생성 0 |
| 패키지 행동 검사 | Python 5 통과 |
| 독립 검토 | 저장 요구 준수·품질, 인증 및 통합 테스트 변경 재검토 PASS |
| Windows CI | [run 43732858917](https://github.com/sebia1993/aruba-airwave-ap-monitor/actions/runs/43732858917), 소스 `0f15663`, 전체 성공 |
| Windows 실행 테스트 | 공통 236 통과·Mac/Unix 4 건너뜀, Windows 전용 5 통과, Python 3 통과·Unix 2 건너뜀 |
| Windows 패키지 | self-contained 게시·ZIP 360개 해시·PowerShell 5.1 launcher 구문, 한글/공백 경로 EXE의 실제 SQLite 초기화·루프백·진단 통과 |

통합 첫 검사에서 기존의 ‘DB 파일 없음’ 가정 4건이 실패했다. 새 시작 동작에 맞춰 실제 운영 회차/인증 행이 생성되지 않는지 검사하고, 시작 전 손상과 실행 중 손상을 분리한 뒤 전체 240개가 통과했다. 원시 측정값의 근거 시각 회귀 검사는 최초 수정 이후 해당 갱신을 잠시 제거해 실패를 재현한 것으로, 최초 테스트 작성 순서와 구분한다.

다음은 Task 5 자동/수동 수집과 건강 상태다. 프로세스 중복 실행 차단을 DB 중단 복원보다 먼저 적용하고, Demo 프로필·시나리오 재시작을 이어서 구현한다. Task 4까지 완료 점수는 48%다. CI의 Windows Server 2025와 별개로 실제 전원 차단·디스크 고장·사내 Windows 11·AirWave·장비 검증은 수행하지 않았다.

## Task 5 결과

변경: 브라우저와 독립적인 자동/수동 수집 조정기, 예약 중첩 건너뜀과 부가 실행 뒤 AP 우선 대기, 일시 중지·재개, 호스트 종료 시 회차 최종화, 저장 오류 후 복구, 건강 상태를 구현했다. 전체 실행 동안 설정 변경과 진단의 경합을 차단하며, HTTP 요청이 취소되어도 수락한 수집은 계속한다. DB 복원보다 먼저 데이터 폴더의 프로세스 잠금을 확보하고, Demo 프로필과 확정된 시나리오 위치를 재시작 후 유지한다.

- root 통합 Release 빌드 경고 0·오류 0, 공통 **268개 통과**, Windows 전용 **5개 Mac에서 건너뜀**, Python 패키지 검사 **5개 통과**.
- 별도 macOS 프로세스에서 브라우저 없이 시작 회차와 실제 **60.05초 후 예약 회차** 저장, SIGTERM 종료 코드 0, 종료 후 Running 0을 확인했다. 마지막 내부 우선순위 리팩터링 전 시간 대기 결과이며 최종 코드의 시작·재시작은 자동 검사로 재확인했다.
- 실제 SQLite 쓰기 실패 뒤 중단 정리가 성공해도 저장 오류 원인을 유지한다. 커밋 후 응답 유실은 원래 결과로 해소한다. 인증 중단·두 성공 시각·수집 지연·시계 역행을 구분한다.
- 인증 차단 재시작 때 갱신 지연이 누락되던 P2를 실제 SQLite 회귀로 수정했다. 독립 재검토 PASS이며 소스 `d66a3a2`의 [Windows CI 43737258765](https://github.com/sebia1993/aruba-airwave-ap-monitor/actions/runs/43737258765)가 성공했다. Windows Server 2025에서 공통 **264 통과·Mac/Unix 4 건너뜀**, Windows 전용 **5 통과**, Python **3 통과·Unix 2 건너뜀**이다.
- self-contained ZIP 360개 해시와 PowerShell 5.1 launcher 구문, 한글·공백 경로 EXE의 브라우저 없는 자동 확정·Idle 프로세스 재시작 후 회차/프로필 보존·같은 폴더 중복 프로세스 거절·루프백·보호된 Demo 진단이 모두 통과했다. 실제 Windows 재부팅이나 서비스 실행을 검증한 것은 아니다.

Task 5까지 확정 진행률은 **58%**다. 다음은 Task 6의 실제 현황·과거 회차·상세·CSV 연결과 브라우저 검증이다. 실제 부가 관측 runner는 아직 미지원이며, 서비스·예약 작업·백업·실제 Windows 11 절전/재부팅·AirWave 계정/장비·장기 운영은 미확인이다.

## Task 6 결과

변경: 실제 저장된 회차의 Down 중심 현황, AP 상세·사건·관측 공백·회차 기록, 검색·필터·전체 결과 CSV를 연결했다. 최신 실패를 숨기지 않고 과거 회차의 상세·CSV는 같은 회차/범위를 유지한다. Demo는 남은 선행 관측을 실제 파서·저장 경로로 확정한 뒤 첫 정상 화면을 제공하며 재시작 시 반복하지 않는다.

| 검증 수준 | 실제 결과 |
|---|---|
| root Mac Release | 경고 0·오류 0, 공통 **327 통과**, Windows 전용 **5 Mac 건너뜀** |
| 프런트엔드 | 실제 Node 24.22.1 CLI **16 통과**, 두 JS 구문 검사 통과. 일부는 최소 DOM 스텁이며 브라우저 검사와 구분 |
| 실제 Mac 브라우저 | 정상·신규·복구·전체 실패·일부 누락, 다른 탭 새 회차 중 상세 고정, 과거 회차 유지·현재 복귀, CSV 8행/BOM, 설정 입력·저장 보존, Demo 진단 2,048대, 콘솔 오류 0 |
| Demo 시작/재시작 | 실제 초기 화면 Up 2,040·Down 8·신규 2·복구 2, 실제 호스트 재시작은 다음 신규 Down 시나리오로 진행 |
| 독립 검토 | 조회·UI·Demo 실행 경계·루트 배선 PASS; 손상 저장 데이터 503, 과거 값 누출 방지, CSV 수식 방어 포함 |
| 패키지 검사 | Python 5 통과 |
| Windows CI | [run 43739468205](https://github.com/sebia1993/aruba-airwave-ap-monitor/actions/runs/43739468205), `efb954b`, Windows Server 2025 성공 |
| Windows 실제 검사 | 공통 **323 통과·Mac/Unix 4 건너뜀**, Windows 전용 **5 통과**, Node **16 통과**, Python **3 통과·Unix 2 건너뜀** |
| Windows 패키지 | self-contained ZIP **363개 해시**, PS5.1 launcher 구문, 한글/공백 경로 EXE 자동 수집·상세·전체 필터 CSV·다음 시나리오 재시작·중복 프로세스 거부 통과 |

사건 조회의 반복 스캔을 한 번의 집계로 바꿨다. AP 2,000대×288회 합성 SQL에서 최신 전체 조회 12.244초→0.115초, 네 조회 결과 해시는 동일했다. 단일 AP 조회는 0.028초→0.107초이며 전체 앱·디스크·Windows 성능 결과는 아니다.

좁은 화면 검사는 브라우저 도구의 크기 변경이 적용되지 않아 실제 1280px만 확인했다. 378/620/850px 실제 렌더링, Windows GUI·Excel, 실서버·장비는 미확인이다. 다음은 Task 7 운영·백업·핵심 패키지 통합이다. 완료 점수는 **70%**다.

첫 Windows 실행 `43738855817`은 호스트 재시작 후 대기 10초를 초과했다. 제품 코드나 제한 시간을 바꾸지 않고 실패 시 건강 상태와 TRX 보존을 추가한 `efb954b`의 재실행이 성공했다. 최초 시간 초과의 원인은 확정하지 못했으며 제품 결함을 수정했다고 표현하지 않는다.

## Task 7 결과

변경: 일일 온라인 백업/정상 7개 보존, 정지 상태의 검증된 복원과 이전 DB 보존, 백업 이후 관측 공백·RunId 재사용 방지, 90일 상세/30일 로그 보존, Windows 서비스·예약 작업·운영 CLI와 핵심 패키지 구성을 연결했다. 현재 Live 계정은 복원 후 재등록하며 현재 설정 파일을 되돌리지 않는다.

- root Release 경고 0·오류 0, 공통 **378 통과**, Windows 전용 **9 Mac 건너뜀**, Node **17**, Python **8** 통과.
- Mac PowerShell 7.6.10의 운영 스크립트 8개 구문/helper 24 검사 통과. 아래 실제 Windows PS5.1·SCM/작업 스케줄러 결과와 구분한다.
- 실제 macOS 프로그램 진입점(`dotnet` + DLL)의 시작→사전 백업→새 관측→복원→재시작에서 복원 회차와 새 관측, 사라진 회차 404, 프로필 해시·이전 DB·백업 보존, 정상 종료 0을 확인했다.
- 독립 검토의 Running 백업 복원 공백 하한 P2와 동기 정리로 종료 예산이 지연되던 P2를 실패 재현 후 수정하고 재검토 PASS를 받았다. 구체적인 변경·증거는 `../DEVELOPMENT_LOG.md`에 있다.
- 첫 Task 7 Windows CI `43742073518`의 공통 검사는 361 통과·10 실패·4 제외였다. 종료 후에도 백업이 파일을 사용하는 문제를 재현해 worker 비동기 해제가 완료를 기다리도록 수정했다. 삭제 우회 없이 독립 재검토 PASS를 받았다.
- 수정 소스 `22b0f163afee7a3360b4e366dff204a6c6a96c9f`의 [Windows CI 43743037210](https://github.com/sebia1993/aruba-airwave-ap-monitor/actions/runs/43743037210)가 성공했다. Windows Server 2025에서 공통 **374 통과·Mac/Unix 4 제외**, Windows 전용 **9 통과**, Node **17 통과**, Python **6 통과·Unix 2 제외**다. 8개 운영 스크립트를 PS5.1로 구문 검사하고 한글·공백 경로의 실제 self-contained EXE를 실행했다.
- 실제 SCM 서비스 시작·루프백·회차 확정, 프로세스 강제 종료 뒤 **63.941초** 만에 새 PID/회차로 복구, 정지 상태 백업·복원, 사라진 회차 5의 404·복원 회차 6·새 관측 7과 RunId 상한 보존을 확인했다. 예약 작업 실행·중복 실행 금지·무제한 실행 시간·재시작 설정도 확인했다. 두 등록의 제거 후 데이터 보존 및 테스트 등록 정리가 성공했다. Demo LocalSystem 검사이며 실제 재부팅이나 managed fatal 오류를 SCM에서 주입한 검사는 아니다.
- 실제 Mac 브라우저에서 백업 검증 완료·백업 시각·보존 처리 시각이 AP 수집 성공과 구분되는 것을 확인했다. 화면 증거는 `../artifacts/task-7-maintenance-health.jpg`다.
- Windows에서 실행한 정확한 ZIP을 CI artifact로 보존하고 로컬에 내려받아 ZIP과 내부 **374개 파일** 해시를 모두 대조했다. 초기 진단 ZIP은 보존했다.

검증된 핵심 ZIP: `artifacts/win-x64/ci-43743037210/AirWaveAPMonitor-0.1.0-core-22b0f163afee-20261115T175316Z-17078e.zip` (50,190,173 bytes). SHA256: `4ec18a63f54ddd502b7aaa45a7648495e41a249a5670ee38ebc89474c54ef798`. 버전 `0.1.0-core`, 소스 미커밋 변경 없음, `onsite_verified=false`. Task 8~10은 이 ZIP에 포함되지 않는다.

다음: Task 8 운영 이력 저장·UI·가져오기 통합과 Task 9 조건부 관측. 확정 점수는 **80%**다. 전력 표본 30일 정리는 Task 9 저장소에 연결한다. 실제 Windows 11/AD·재부팅/절전·사내 계정/장비·물리 전원/디스크 손상·장기 운영은 미확인이다.

## Task 8 결과

실제 SQLite의 수동 대장·감시 정책·지점·배치·작업 버전·운영 이력과 HTTP/UI·CSV 가져오기를 연결했다. 수집 시작의 등록 버전을 고정하고 과거 회차를 보존한다. 복원 후 재사용된 버전에 오래된 편집을 덮어쓸 수 있던 경계는 복원 세대 검사와 화면 읽기 순서로 수정했다.

- root 공통 **532 PASS**, Windows 전용 **9 Mac SKIP**, Release 경고/오류 0. Node **43**, Python **8 PASS**. 순수 정책·실제 SQLite·HTTP/UI 독립 검토 PASS.
- 실제 Mac 브라우저: 수동/자동 분리·지점 v1/v2·배치·작업 예정/실시/취소 버전·CSV 1행 선택 적용·다음 회차 반영·과거 회차 보존·실제 CSV 10행/BOM/수식 방어 확인. 최종 소스 재시작 후 등록 보존·정책 저장·지점 이력 9건/기본정보 5건·한글 변경 요약·콘솔 오류 0을 확인했다.
- 이력의 구조화된 원본을 안전한 당시 값 요약으로 표시하며 이전 기록은 변경하지 않는다. 화면 증거 `../artifacts/task-8-point-history.jpg`, 사용 안내 `guides/OPERATIONS.md`.
- 첫 Windows CI `43748148374`는 공통 527 통과·1 실패·Mac/Unix 4 제외. 실행 중 DB의 일반 파일 읽기와 유지보수 쓰기가 충돌한 테스트를 SQLite 읽기로 수정했다. 제품 잠금/백업 변경 없이 Mac 관련 13개 PASS. 이 단계에서는 Windows 재실행과 정확한 ZIP 검증이 남아 있어 완료 점수 **80%**를 유지했다.

- 두 번째 Windows CI `43748984883`은 이전 검사를 통과했으나 인증 오류 테스트의 DB 손상 주입 단계가 백그라운드 작업과 충돌했다(527 통과·1 실패·4 제외). 해당 테스트만 worker 정상 종료 후 손상을 주입하도록 분리했다. HTTP 503·비밀 비노출 검증은 유지하며 관련 Mac 18개 PASS 뒤 세 번째 Windows 검증을 진행했다.

- 세 번째 [Windows CI 43749748457](https://github.com/sebia1993/aruba-airwave-ap-monitor/actions/runs/43749748457), 소스 `ab9155b5fc929c876eaa2d589cea269b564cf9d2`가 성공했다. 공통 **528 통과·Mac/Unix 4 제외**, Windows 전용 **9**, Node **43**, Python **6 통과·Unix 2 제외**다. PS5.1 구문, 한글/공백 경로 self-contained EXE, 실제 SCM 강제 종료 후 **66.039초** 복구, 정지 상태 백업/복원·RunId 상한·예약 작업·제거 후 데이터 보존과 정리가 통과했다. Demo LocalSystem 검사이며 실제 재부팅·managed fatal 예외 주입은 아니다.
- CI에서 실행한 정확한 ZIP과 내부 **378개 파일** SHA256을 로컬에서 모두 대조했다. `artifacts/win-x64/ci-43749748457/AirWaveAPMonitor-0.1.0-core-ab9155b5fc92-20261115T190455Z-2c7b41.zip`, **50,343,686 bytes**, SHA256 `3029aac8083ff1a19eccb1cfc64482b325e29dd4346143fc43fbfedf4f30f50d`. 소스 미커밋 변경 없음, `onsite_verified=false`; Task 9~10은 이 ZIP에 포함되지 않는다. 앞선 실패 두 건은 위에 보존했다.

다음: Task 9 연결/전력의 근거·시간·세대 경계, 저장·수집·조회/UI 통합과 Task 10 최종 인수. 확정 개발 점수는 **92%**다. 실환경 Windows 11/AD·실제 대장/장비·AirWave·Windows GUI/Excel·장기 운영은 미확인이다. 세부 변경과 검증 범위는 `../DEVELOPMENT_LOG.md`에 기록했다.

## Task 9 결과 — 최종 Windows 검증 대기

변경: AP 상태와 분리된 연결·이웃/전력 수집, 종류별 실행·성공 시각·오류·공백, capability 활성화와 설정 충돌 검사를 실제 SQLite·HTTP·화면에 연결했다. 복수 연결·방향·원천 시각, 포트 관리/운용 상태와 여섯 전력 지표·단위·당시 귀속을 보존한다. AP 우선 대기는 한 번으로 합치고, 과거 AP/사건은 회차와 실제 저장 순서의 상한을 모두 지킨다. 전력 원시 표본은 조회 시각 기준 30일 정리하며 사건 복사본은 보존한다.

- 최종 Mac `NODE_BIN=... ./scripts/verify-mac.sh`: clean/restore/Release build 경고·오류 0, 공통 **802 PASS**, Windows 전용 **9 Mac SKIP**, Node **76**, Python **9 PASS**. 실제 runner/Demo/SQLite/HTTP의 부분 전력·AP 성공 시각·과거 AP/사건 불변을 검사했다.
- 독립 검토에서 교체 후 구세대 귀속과 높은 RunId의 선행 커밋이 과거 AP에 섞이는 문제를 발견해 수정했다. 원래 낮은 RunId의 늦은 커밋, 재시도·중복·만료 경계와 함께 11개 추가 회귀 및 독립 재검토를 통과했다.
- 수정 직후 실제 2,048대 전력 수집은 5초 저장 예산 초과로 Interrupted/Failed·0표본이 됐다. 원인은 표본마다 모든 연결의 대상 기간을 SQL로 재검사한 것이었다. 양쪽 endpoint 색인과 트랜잭션 안에서만 사용하는 기간/회차 시각 캐시로 수정하고 **5초 예산을 그대로 유지**했다. 실제 runner 회귀는 실패 5.329초에서 정상 2.779초·12,288표본으로 바뀌었으며 후보 충돌·서로 다른 측정 시각 검사를 추가했다. 이는 전체 전력 단위 시간이며 SQL 단독 시간이나 처리량 보장이 아니다.
- 최종 새 폴더의 실제 Mac 브라우저: 기본 비활성→두 Demo 수집 활성화, 이동 없이 버튼/종류별 건강 상태 갱신, 미저장 입력 16분·펼친 필드 보존, 연결 회차 5의 2,048조회/2,048포트, 전력 회차 6의 **12,288표본/2,048공급 상태 정상 확정**. 실제 전력 단위 1.212초이며 AP 성공 회차 4/시각은 변하지 않았다.
- 대량 상세는 50건씩 표시하고 연결 마지막 41페이지·전력 마지막 246페이지 접근을 확인했다. 동일한 최신 연결의 retained 중복 표시는 제거하고 다른 시각/품질/대상·공백은 보존한다. 전체 회차 JSON 배열은 여전히 읽으므로 DOM 페이지화가 응답 크기 제한을 뜻하지 않는다.
- 실제 프로세스 정상 종료·재시작 후 설정과 연결/전력 원본 JSON 해시 동일, 원시 표본 12,288개 유지, 새 AP 회차 7의 AP별 전력 6개와 과거 AP 회차 4의 0개를 확인했다. 브라우저 콘솔 오류 0, 실제 확인 화면 폭 1280px. 화면 증거 `../artifacts/task-9-verified-enrichment.jpg`.
- 조건부 구조·Demo 경로를 구현했다. **Live 연결·LLDP·PoE 전송 어댑터는 미구현·미검증·비활성**이다. DemoFixture의 제공 확인을 실필드 확인으로 올리지 않으며, 현장 확인만으로 이 버전에서 켤 수 없다. 실제 필드·권한·시각·장비·물리 연결 연속성은 미확인이다.

다음: Task 9 최종 Windows CI와 Task 10 대규모 측정·정확한 ZIP·인수 기록. 현재 확정 점수는 **92%**를 유지한다.

## Task 10 결과 — 최종 Windows·패키지 인수 대기

변경: `verify-mac.sh`/`package-win.sh`, A01–A20 근거표, 자료 사전·설정 예시·Mac/Windows/부가 관측/사내 확인 안내를 정리했다. 기존 Task별 기록·초기 패키지와 원본 계획·설계·모의화면은 보존했다. 패키지에는 로컬 자원과 운영 스크립트·진행/인수 기록을 포함하고, 복사본의 문서 링크만 ZIP 내부 경로로 바꾼다. 원본 안내·PowerShell BOM은 보존한다.

- 위 최종 clean 검증이 완료됐다. 2,000/5,000대 측정 도구는 9프레임의 정확한 집계·모든 500행 페이지의 고유 대상/상태·반복 사건·재열기·90일 정리 후 역사 보존을 검사한다. 소스 `4d9a822a9a7305cf0b83341c5fc10f5836663f33`의 clean 작업 트리에서 2,000/5,000대를 각각 별도 프로세스로 실행해 각 9프레임·모든 목록 검사를 통과했다. 전체 측정 11.082/36.030초. [측정 기록](PERFORMANCE_MEASUREMENTS.md)에 단계별 시간·DB 증가·메모리 및 실행 DLL SHA256을 기록했다. Windows CI·ZIP 바이트 검증은 이어서 기록한다.
- 저장량 읽기 전용 검토: 2,000/5,000대 최종 DB는 **217.457/543.086 MiB**다. 전체 AP의 매회차 사실·사건 스냅샷·운영 사건이 약 74.5%를 차지한다. 과거 전체 AP/Up 조회를 위해 모든 AP의 회차를 보존하므로 설계의 Down/미확인 중심 저장보다 공간을 더 쓴다. 메타데이터 재사용은 적용했지만 공간 절감 최적화와 장기 용량 검증은 후속으로 남긴다. 이 정보 손실 없는 구현 차이와 수치는 [측정 기록](PERFORMANCE_MEASUREMENTS.md)에 명시했다.
- 첫 최종 [Windows CI 43756453922](https://github.com/sebia1993/aruba-airwave-ap-monitor/actions/runs/43756453922), 소스 `9ed76dd`, 공통 **797 통과·1 실패·Mac/Unix 4 제외**였다. `RestartedDemo_DoesNotReseedCommittedPrehistory`의 첫 호스트 visible 회차가 Idle이 되기를 기다리는 10초 조건이 실패했다. 전체 테스트 시간 51초에는 두 선행 회차의 동기 실행도 포함된다. 원래 로그만으로 지연·저장 오류·완료 단계는 확정할 수 없어 테스트의 상태/단계별 시간 진단을 보강한다. 제한 시간과 제품 코드는 바꾸지 않는다. 별도 2,048대 runner 검사는 Windows에서 12,288표본 정상 확정을 통과했으며 전체 Power 단위 9.120초는 저장 예산 5초와 다른 측정 범위다. 후속 Windows 전용·패키지 단계는 실행되지 않았으므로 완료 점수 92%를 유지한다.
- `./scripts/package-win.sh` Mac 교차 게시 성공: 소스 `9ed76dd`, 버전 `0.1.0`, ZIP **50,551,027 bytes**, 내부 **391개 파일** 해시·운영 자료 제외·PS BOM·문서 링크 확인. SHA256 `dcd7452062e5d2ed89a211942a75a7480dbbbba6692cb3708d3ed6cabf0ffaf3`. 이 Mac 생성 바이트를 Windows에서 실행했다는 의미는 아니다.
- 버전 `0.1.0`의 구현 범위와 `onsite_verified=false`를 명시한다. 구현 범위 표시가 Windows/사내 검증 성공 표시를 대신하지 않는다.

## 실환경 미확인

| 항목 | 상태 |
|---|---|
| 실제 AirWave 로그인·AP 목록·대수·분류·필드 | 미확인 |
| 사내 PAC/프록시·인증서 이름/신뢰/유효기간 | 미확인 |
| 실제 수집 계정의 DPAPI·권한 | 미확인 |
| 사내 계정의 서비스·예약 작업·재부팅·절전 복귀 | 미확인 |
| 실제 스위치/포트 매핑·LLDP 출처/시각·PoE 필드/단위 | 미확인, 자동 활성화하지 않음 |
| 사내 장기 상시 수집·실장비 상태 대조 | 미확인 |

사내 원문 XML·계정·인증서·DB의 외부 제공을 요구하지 않는다. Windows CI는 자동 실행 근거이며 사내 운영 PC 검증을 대신하지 않는다.
