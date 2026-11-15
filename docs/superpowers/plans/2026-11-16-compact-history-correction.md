# Compact Historical Storage Correction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 원래 Task 4의 Down/미확인 중심 영구 저장 요구를 충족하고 이미 제공한 과거 조회의 정확한 값을 유지한다.

**Architecture:** schema 6을 추가하여 새 회차의 상세 `run_rows`는 Down/Unknown에만 남긴다. 정상 AP는 불변 대상·표시/사실 버전과 회차 공통 관측 근거를 재사용하며 실제로 다른 값은 정확한 예외로 보존한다. 기존 schema≤5 자료를 다시 쓰지 않는 공통 historical resolver가 모든 조회에 같은 `ApSnapshotRow` 의미를 제공한다.

**Tech Stack:** .NET 10, Microsoft.Data.Sqlite, SQLite WAL, xUnit, 기존 macOS 검증과 GitHub Actions Windows 작업.

**Spec:** `../specs/2026-11-15-local-ap-status-history-design.md` §7·§8 및 `2026-11-15-macos-codex-implementation.md` Task 4·6·10. 원본 계획/설계/모의화면은 수정하지 않는다. 이 문서는 승인 범위의 누락을 보완하는 실행 절차다.

## Global Constraints

- “AP 상태·기본정보는 현재값과 변경 사건을 저장하고, 회차별 현황은 요약과 Down/확인 불가 대상에 필요한 정보만 보관한다.”
- “동일한 목록·표시 정보는 불변 버전을 참조해 재사용할 수 있지만 회차별 시각·품질·분류를 생략하지 않는다.”
- “전체 AP가 Down 또는 미확인인 경우의 용량도 측정하며 임의로 대상을 잘라 저장하지 않는다.”
- 회차 원자성·receipt replay·세대/구간·사건/스냅샷·상세 정리 후 영구 참조 보존은 유지한다.
- 실제값이 공통 시각/회차와 정확히 같을 때만 참조 토큰으로 인코딩한다. 현재값으로 과거를 채우거나 AP 전체 facts를 다른 table/blob으로 옮기는 것으로 완료하지 않는다.
- 사내 Windows 11/AD·실제 AirWave/API·장비 검증을 로컬/Windows CI로 대체하지 않는다.

## Review Focus

1. 필드별 서로 다른 시각, stale 값, source clock rollback, null과 quality를 codec round-trip에서 보존한다.
2. 같은 표시의 Up 반복은 전체 상세 facts가 N×R로 늘지 않지만 각 회차의 정확한 시각·대상·history는 유지한다.
3. 복구·교체·범위/정책 제외·재편입·수동 우선값·늦은 enrichment commit은 선택한 과거 run의 결과를 바꾸지 않는다.
4. legacy schema5 자료와 새 compact 자료가 혼재해도 Dashboard/CSV/detail/history/incident membership 및 legacy backfill이 일치한다.
5. commit failure/replay/reopen/backup/restore/retention은 새 참조를 포함해 원자성·불변성을 유지한다.

---

### Task C1: 저장 표현과 공통 historical resolver

**Files:** Create `src/AirWaveApMonitor.Infrastructure/Storage/Migrations/006_compact_history.sql`, `SqliteCollectionStore.History.cs`(필요하면 codec/read/write 책임별 partial로 분리), `tests/AirWaveApMonitor.Tests/Storage/CompactHistoryTests.cs`; modify `SchemaMigrator.cs`, `SqliteCollectionStore.Apply.cs`, `.Persist.cs`, `.Query.cs`, `.ReadModels.cs`, `.Operations.Apply.cs`와 실제 스키마를 검사하는 관련 테스트.

**Interfaces:** 외부 `ICollectionStore`·`ICollectionReadStore`·`ApSnapshotRow`·HTTP/CSV 계약을 유지한다. `ReadSnapshotRows(SqliteConnection, SqliteTransaction, long runId, CancellationToken)`는 legacy와 compact 자료에서 같은 당시 행을 반환한다. AP별 historical lookup/membership도 같은 해석을 공유한다. private schema/helper의 구체적 이름은 store owner가 구현 전 기록하고 일관되게 적용한다.

- [ ] 실제 SQLite에 2,000개 정상 AP의 시각이 다른 반복 회차를 적용하는 RED 테스트를 작성한다. 새 상세 `run_rows`의 Up 행 0, 변하지 않은 정상 facts/membership 재사용, 전체 대상 및 개별 회차 시각/품질/history 정확성을 함께 검사한다.
- [ ] 위 테스트를 기존 소스에서 실행해 기대한 저장 구조 실패를 기록한다. 기존 baseline 실패와 혼동하지 않는다.
- [ ] additive migration6·불변 membership/facts versions·exact run context/exception codec 및 공통 resolver를 구현한다. legacy 행은 변경/삭제하지 않는다. 완성된 membership/frame은 최종 AP 트랜잭션에서 함께 확정한다.
- [ ] Review Focus 1~5의 actual SQLite 회귀를 작성하고 실행한다. 과거 Dashboard 모든 filter/page와 detail/history/incident/CSV의 의미, full Down/Unknown, up/down/recovery, manual/placement/generation/scope/enrichment 경계, rollback/replay/reopen/upgrade/backup/restore/retention을 증명한다. 기존 검사를 skip하거나 의미를 약화하지 않는다.
- [ ] `/Users/ilen/.dotnet/dotnet test tests/AirWaveApMonitor.Tests -c Release --filter 'FullyQualifiedName~Storage|FullyQualifiedName~ReadModel|FullyQualifiedName~Maintenance|FullyQualifiedName~Enrichment'` 및 전체 공통 테스트를 실행한다. RED/GREEN·패치·제한을 root에 전달하고 독립 검토를 받는다.

### Task C2: 통합·실측·문서 정정

**Files:** `README.md`, `docs/DEVELOPMENT_STATUS.md`, `DEVELOPMENT_LOG.md`, `docs/PERFORMANCE_MEASUREMENTS.md`, `docs/ACCEPTANCE_EVIDENCE.md`, `tests/Performance/Program.cs`, 새 `docs/measurements/*compact*.json`; packaging에 새 자료가 필요하면 `scripts/package_initial.py`와 해당 검사.

**Interfaces:** 기존 2,000/5,000대 9프레임 측정은 변경 전 증거로 보존한다. 새 결과에는 실행 source commit·DLL 해시·새 프로세스/빈 폴더·정상 무변화 반복의 물리 행/버전 증가를 기록한다.

- [ ] 현재 완료 점수를 78%로 정정하고 Task4를 재개한다. 이전 단계의 성공 수치는 역사로 보존하되 원문 미충족을 단순 후속 최적화로 표현하지 않는다.
- [ ] 검토된 C1 패치를 root에 통합하고 `./scripts/verify-mac.sh`를 실행한다. packaging LF/CRLF 검사 보완도 포함하되 원본 Markdown 바이트 보존을 검증한다.
- [ ] 변경된 저장 표현에서 실제 Demo 브라우저/프로세스 재시작·과거 조회를 확인한다. 기존 enrichment snapshot 및 dirty UI 동작이 영향을 받으면 관련 흐름도 재확인한다.
- [ ] 깨끗한 새 source commit에서 2,000/5,000대 full Down/Unknown 시나리오와 정상 반복을 다시 측정한다. 변경 전후 수치와 범위를 분리하고 대수별 전체 행·집계·시각 정확성을 확인한다.
- [ ] 요구사항/품질 독립 검토를 마치고 변경·실제 검증·다음 작업·개발 진행률·실환경 미확인 항목을 기록한다.

### Task C3: Windows 및 최종 반입 산출물

**Files:** 기존 `.github/workflows/windows-ci.yml`, 최종 개발/인수 기록, 생성 ZIP manifest·SHA256.

**Interfaces:** 기존 `windows-latest` workflow 전체 성공이 Windows 자동 검증의 기준이다. 최종 source의 실제 CI ZIP을 내려받아 manifest/해시/포함 자료/실행 근거를 대조한다.

- [ ] 작업 브랜치를 푸시하고 기존 workflow를 완료까지 추적한다. 실패하면 원인·직접 증거를 보존하고 수정 후 필요한 검사를 재실행한다.
- [ ] 공통/Windows 전용/Node/Python·native EXE·서비스/예약 작업·백업/복원·패키지 단계 결과를 분리 기록한다. skip/미실행을 PASS로 표현하지 않는다.
- [ ] 정확한 CI ZIP의 SHA256/manifest/가이드/측정 JSON과 민감자료 미포함을 확인한다.
- [ ] 전체 원문 필수조건과 A01–A20를 다시 대조하고 원본 계획/설계/모의화면 보존을 확인한다. Task4/9/10의 완료 근거를 확보한 후에만 전체100% 및 goal 완료를 기록한다.

### Task C4: 최종 비저장 감사에서 확인한 Task 6 누락 보완

**Files:** `Core/Contracts/{MonitoringReadContracts,StorageContracts,CollectionContracts}.cs`, 새 `Core/Contracts/ScopeSettingsContracts.cs`, `Infrastructure/Settings/ProfileStore.cs`, `Infrastructure/Storage/SqliteCollectionStore.{cs,Apply.cs,Query.cs,ReadModels.cs}`와 새 `.Scope.cs`, `App/Endpoints/{MonitoringEndpoints,SettingsEndpoints,ScopeSettingsEndpoints}.cs`, `App/wwwroot/{app.js,index.html,app.css,ui-state.js}`, 관련 Settings/Web/Frontend tests.

**Interfaces:** 모든 표시 시각을 `Asia/Seoul`로 고정한다. 사건·회차 UI는 기존 `fromUtc`/`toUtc`를 연결한다. Dashboard/Incident query에는 양수 `minimumOpenElapsedSeconds`를 추가하고 선택 회차 확정 시각의 열린 사건만 계산한다. `IncidentPage.upToDownTransitionCount`/`initialDownCount`는 모든 페이지에 걸친 현재 필터의 전환/최초 사건 수이며 `totalRows`·`distinctApCount`와 구분한다.

범위는 기본 `AllDiscovered`와 검증된 원천 AP ID의 `SelectedApIds`를 구분한다. `ProfileSnapshot.MonitoringScope`와 `ScopeRevisions`는 새 nullable 속성으로 legacy JSON에서는 생략한다. 기존 `ExpectedTargets`는 여전히 누락을 감지하는 기대 집합이며 표시 필터로 사용하지 않는다. 저장소는 실행 시작 때 범위와 대상을 고정하고, 선택 밖의 반환 AP를 해당 범위에 자동 추가하지 않는다. 범위 변경은 config/scope version을 증가시키고 기준을 잠정으로 되돌리며 사유·시각·당시 정의를 원자적 profile JSON에 보존한다. 설정/계정/부가 변경 시 이 속성들을 보존한다.

`GET /api/scope`는 현재 설정, 실제 유효 관측이 있었던 알려진 AP ID와 현재 범위의 마지막 확정 회차를 반환한다. `POST /api/scope`는 CAS·명시 모드·ID 목록·사유를 받아 알려진 ID만 허용한다. `POST /api/scope/confirm`은 CAS·비교 회차·대조 대수·사유로 사용자의 기준 대조 결과를 기록한다. 이전 범위 또는 오래된 대조 회차를 거부한다. 새로운 AP 발견 뒤에는 기존 확인을 새 목록의 확인으로 확장하지 않는다. 모든 변경은 기존 동일 출처/CSRF/OperationGate를 따른다. 실제 AirWave 원격 폴더 필터를 확인했다고 표현하지 않는다.

- [ ] 원문 KST/기간/전환/장기 미복구/범위·대조 저장 미충족을 실제 함수·HTTP·SQLite RED로 재현한다. Task 6을 재개하여 확정 점수를66%로 정정한다.
- [ ] read query/filter/집계와 CSV, KST 및 기간 UI를 병행 구현하고 페이지·과거 범위·늦은 응답·0/미상·최초 Down/교체 의미를 검증한다.
- [ ] 범위/대조 profile 계약·설정 HTTP·실행 시작 고정·로컬 선택 범위 적용을 구현한다. 변경 중 회차, 부분/전체실패, 미지ID/중복/빈선택, 재편입, 과거회차 불변, 설정원자성/재시작/CAS, 신규 발견 후 잠정 회귀를 확인한다.
- [ ] UI로 범위·대조 작업을 제공하고 다음 관측 적용/과거 기준 보존을 실제 브라우저에서 확인한다. 범위 설정은 보기 필터와 별개임을 표시한다.
- [ ] C1과 최소 diff로 통합하고 독립 검토·Mac 전체·새 측정·Windows 전체/ZIP을 다시 통과한다. Task6 점수는 해당 완료 근거를 확보한 뒤 회복한다.
