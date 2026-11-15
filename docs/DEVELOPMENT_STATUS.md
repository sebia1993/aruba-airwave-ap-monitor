# 개발 진행 및 검증 기록

기준일: 2026-11-16. 실행 기준은 `superpowers/plans/2026-11-15-macos-codex-implementation.md`다.

개발 산출물과 사내 운영 검증은 별개다. 아래 가중치는 최초 계획대로 고정하며 완료 증거가 있는 작업에만 점수를 부여한다.

| 작업 | 가중치 | 현재 상태 | 완료 근거 |
|---|---:|---|---|
| 1. Mac 골격·계약·모드 분리 | 8 | 완료 | `454dee4`, Mac 34 통과, Windows CI 성공, 독립 검토 통과 |
| 2. API·진단·계정 보호 | 12 | 완료 | `9366a71`, Mac 87 통과, Windows CI 성공, 초기 ZIP·독립 검토 PASS |
| 3. 식별·상태·사건 | 14 | 완료 | `e007bae`, Mac 167 통과, Windows CI 성공, 독립 재검토 PASS |
| 4. 원자적 저장·회차 현황 | 14 | 완료 | `0f15663`, Mac 240 통과, Windows CI·실제 SQLite·2,048대 시나리오·독립 검토 PASS |
| 5. 자동 수집·건강 상태 | 10 | 구현 중 | 별도 worktree에서 수집 조정기·재시작 처리 착수 |
| 6. 핵심 화면·CSV | 12 | 대기 | — |
| 7. Windows 운영·백업·패키지 | 10 | 대기 | — |
| 8. 운영 이력·가져오기 | 12 | 대기 | — |
| 9. 조건부 부가 관측 | 5 | 대기 | — |
| 10. 통합 확인·인수 자료 | 3 | 대기 | — |

**완료로 확인된 개발 진행률: 48 / 100%.** Task 1의 8점, Task 2의 12점, Task 3·4의 각 14점을 합산했다. Task 5 이후 진행 중 작업은 아직 점수에 넣지 않았으며 실환경 운영 보장률이 아니다.

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

## 실환경 미확인

| 항목 | 상태 |
|---|---|
| 실제 AirWave 로그인·AP 목록·대수·분류·필드 | 미확인 |
| 사내 PAC/프록시·인증서 이름/신뢰/유효기간 | 미확인 |
| 실제 수집 계정의 DPAPI·권한 | 미확인 |
| 서비스·예약 작업·재부팅·절전 복귀 | 미확인 |
| 실제 스위치/포트 매핑·LLDP 출처/시각·PoE 필드/단위 | 미확인, 자동 활성화하지 않음 |
| 사내 장기 상시 수집·실장비 상태 대조 | 미확인 |

사내 원문 XML·계정·인증서·DB의 외부 제공을 요구하지 않는다. Windows CI는 자동 실행 근거이며 사내 운영 PC 검증을 대신하지 않는다.
