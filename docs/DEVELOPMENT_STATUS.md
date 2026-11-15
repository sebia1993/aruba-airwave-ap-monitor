# 개발 진행 및 검증 기록

기준일: 2026-11-16. 실행 기준은 `superpowers/plans/2026-11-15-macos-codex-implementation.md`다.

개발 산출물과 사내 운영 검증은 별개다. 아래 가중치는 최초 계획대로 고정하며 완료 증거가 있는 작업에만 점수를 부여한다.

| 작업 | 가중치 | 현재 상태 | 완료 근거 |
|---|---:|---|---|
| 1. Mac 골격·계약·모드 분리 | 8 | 완료 | `454dee4`, Mac 34 통과, Windows CI 성공, 독립 검토 통과 |
| 2. API·진단·계정 보호 | 12 | Windows 검증 대기 | Mac 87 통과, 패키지 5 통과, 독립 재검토 PASS |
| 3. 식별·상태·사건 | 14 | 별도 worktree에서 구현 중 | 순수 로직; 아직 완료 점수 없음 |
| 4. 원자적 저장·회차 현황 | 14 | 대기 | — |
| 5. 자동 수집·건강 상태 | 10 | 대기 | — |
| 6. 핵심 화면·CSV | 12 | 대기 | — |
| 7. Windows 운영·백업·패키지 | 10 | 대기 | — |
| 8. 운영 이력·가져오기 | 12 | 대기 | — |
| 9. 조건부 부가 관측 | 5 | 대기 | — |
| 10. 통합 확인·인수 자료 | 3 | 대기 | — |

**완료로 확인된 개발 진행률: 8 / 100%.** Task 1 전체 완료에만 가중치 8을 부여했다. 실환경 운영 보장률이 아니다.

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

## Task 2 결과 — Windows 검증 대기

변경: 공식 문서 기반 AirWave 로그인·AP 읽기, 안전 XML 파서, 세션·인증 중단·본문/전체 시간 제한, Windows DPAPI 계정 보호, 프로필 저장과 변경 API 보호, 실제 설정·진단 화면, 초기 self-contained Windows 패키지 생성. 세부 구현·검토 수정은 `../DEVELOPMENT_LOG.md`의 Task 2 기록에 있다.

| 검증 수준 | 실제 결과 |
|---|---|
| Mac Release 빌드 | 경고 0·오류 0 |
| Mac 공통 테스트 | 87 통과 |
| Mac의 Windows 전용 검사 | DPAPI 3·경로 2 = 5개 명시적 건너뜀 |
| 패키지 행동 검사 | Python 5 통과; 민감 자료 혼입·실패 시 기존 파일 보존 포함 |
| 실제 Mac 브라우저 | 설정 저장·새로고침 유지·PAC URL 거부·Demo 2,048대 파싱/제외 0, console error 0 |
| 독립 검토 | 요구 준수·품질·패키지 PASS; 지적 사항 회귀 재현 후 수정 |
| Windows CI | 새 소스 커밋에서 실행 예정; 아직 통과로 계산하지 않음 |
| 사내 Windows 11·AirWave | 미확인 |

진단 ID는 운영 RunId가 아니며 자동 감시·SQLite 이력·서비스 설치를 아직 제공하지 않는다. 초기 패키지 안내는 `guides/INITIAL_DIAGNOSTICS.md`, 현장 기록 양식은 `guides/ONSITE_INITIAL_CHECKLIST.md`다. 사내 검증은 개발 진행률과 별개다.

다음: Task 3 순수 AP 식별/세대/감시 구간/상태 전환. Task 2와 공유 파일이 충돌하지 않도록 별도 managed worktree에서 구현하고 통합 전에 검토한다. 현재 완료 점수는 Task 1의 8점이며 Windows CI 및 초기 패키지 확정 후 Task 2를 합산한다.

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
