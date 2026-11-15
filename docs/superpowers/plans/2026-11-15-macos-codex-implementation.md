# AirWave AP Monitor — MacBook Codex Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**MacBook 실행 안내:** 위 스킬 지시는 해당 스킬이 제공되는 환경에 적용한다. MacBook Codex에 해당 스킬이 없으면 이 문서의 작업 순서·검증 기준·보고 형식에 따라 직접 진행한다. 별도 플러그인 설치를 구현의 선행 조건으로 요구하지 않으며, 현재 실행 환경의 상위 지침과 권한은 그대로 따른다.

**Goal:** 집의 MacBook에서 Codex로 AP 2,000대 이상의 Down 현황과 운영 이력을 추적하는 프로그램을 구현하고, 사내 Windows 11에서 사용자가 검증할 수 있는 배포 패키지와 진단 기능을 제공한다.

**Architecture:** ASP.NET Core 앱 한 프로세스에 백그라운드 수집기, SQLite 저장, 루프백 웹 UI를 둔다. 상태·이력 코어와 AirWave 어댑터를 분리하고, Windows DPAPI·자동 실행은 플랫폼 경계 뒤에 둔다. Mac에서는 실제 저장·조회·상태 처리 코드를 사용하는 Demo 모드로 개발하며, Windows 운영 모드와 데이터·설정을 분리한다.

**Tech Stack:** C# / .NET 10 LTS / ASP.NET Core / Microsoft.Data.Sqlite / SQLite WAL / 로컬 HTML·CSS·JavaScript / xUnit. Mac Apple Silicon에서는 Arm64 SDK, 운영 배포는 Windows 11 x64 self-contained를 기본 구현 기준으로 사용한다.

**Spec:** `docs/superpowers/specs/2026-11-15-local-ap-status-history-design.md`

**UI reference:** `docs/prototypes/airwave-ap-monitor-mockup.html`

**Repository:** https://github.com/sebia1993/aruba-airwave-ap-monitor

**문서 상태:** 2026-11-15 작성한 실행용 작업 지시서다. 아래 체크박스는 구현할 작업이며, 아직 수행·통과했다는 뜻이 아니다. 사용자가 MacBook의 Codex에서 개발하도록 선택했다. 기존 설계의 기술 제안값은 이 지시서의 구현 시작값으로 채택하되, 실제 API 제공 여부나 실환경 검증 완료로 해석하지 않는다.

## 0. Codex에게 내리는 작업 지시

저장소와 아래 기준 문서를 읽고 실제 구현을 시작하라. 계획 설명만 작성한 뒤 멈추지 말고, 현재 환경에서 가능한 구현·자동검증·배포물 준비·문서화를 진행하라. 작업 간 의존성은 지키고, 독립적인 검토·테스트 준비는 병렬화할 수 있다. 현재 Codex 환경의 상위 지침·조직 정책·권한을 우선하며, 승인이나 접근 제한을 우회하지 않는다.

### 먼저 읽을 파일

1. 현재 작업 위치와 상위 경로 및 저장소에 적용되는 `AGENTS.md`.
2. `README.md`.
3. 이 작업 지시서 전체.
4. `docs/superpowers/specs/2026-11-15-local-ap-status-history-design.md` 전체.
5. `docs/prototypes/airwave-ap-monitor-mockup.html`.
6. `DEVELOPMENT_LOG.md`.

요구사항 충돌 시 최신 사용자 지시를 우선하고, 판단 근거를 작업 기록에 남긴다. 모의화면은 화면 구성과 상태 표현의 참고이며 실제 제품 코드·API 검증 자료가 아니다. 모의화면에 없는 승인된 운영 이력을 삭제하지 않는다.

### 개발·검증 경계

- **집의 MacBook / Codex:** 구현, 가상 API 응답·실제 임시 SQLite를 이용한 테스트, Mac Demo 실행, Windows용 게시와 패키지 준비.
- **Windows 확인 환경이 있는 경우:** Windows 실행, DPAPI, 서비스·예약 작업, Windows PowerShell 스크립트를 확인한다. Windows가 없으면 해당 항목만 미실행으로 남기고 다른 작업을 계속한다.
- **사내 Windows 11 / 사용자:** 프로그램 반입, 실제 AirWave 계정 등록, PAC·인증서·API·대수·원천 필드·상시 수집 검증.
- 집에서 사내 AirWave에 연결하거나 사내 원문 XML·계정·인증서·DB를 외부로 제공하는 것을 요구하지 않는다.
- 사내 검증 대기를 구현의 중단 조건으로 삼지 않는다. 가상 검증·Windows용 빌드 성공을 Windows 실행 또는 사내 연동 성공으로 표시하지 않는다.
- 이번 개발의 산출물은 소스·테스트·Windows 반입 패키지·사용 안내다. 정식 공개 릴리즈 발행이나 운영 PC의 원격 변경은 이 지시의 자동 수행 대상에 포함하지 않는다.

### 저장소 작업

- 먼저 `git status --short --branch`, `git remote -v`, 현재 브랜치와 기존 구현을 확인한다. 사용자의 미커밋 변경을 보존한다.
- 기존 구현이 있으면 확장한다. 아래 파일 구조를 맞추기 위해 기존 코드를 전부 다시 작성하지 않는다. 대응 경로는 작업 기록에 남긴다.
- 작업 브랜치 기본 이름은 `codex/macos-v0.1`이다. 같은 작업 브랜치가 이미 있으면 상태를 확인해 이어가고, 관련 없는 변경이 있으면 별도 worktree/브랜치를 사용한다.
- 의미 있는 작업 단위로 커밋한다. 필요하면 인증된 원격 작업 브랜치에 진행 상황을 보존한다. 인증이 없으면 로컬 커밋과 수행하지 못한 원격 동작을 기록한다.
- 실제 자료, 비밀, DB·WAL·SHM, 로그, 빌드 캐시와 배포 ZIP은 소스 커밋에서 제외한다. 기존 파일의 무조건 삭제, 강제 push, 무관한 변경 되돌리기를 하지 않는다.

## Global Constraints

아래 값은 기존 설계에서 가져온 구현 기본값이다. 변경이 필요하면 이유와 영향을 기록하며 상태·이력 의미를 임의로 바꾸지 않는다.

| 항목 | 값·규칙 |
| --- | --- |
| 운영 범위 | AirWave 서버 1개, 사내 수집 PC 1대, 운영자 1명 |
| 실제 사용 OS | Windows 11; 기본 배포 RID `win-x64` |
| 로컬 UI | `127.0.0.1:8765`; 포트 변경 가능, 루프백 제한 유지 |
| 수집 프로세스 | 백그라운드 EXE 한 프로세스. 브라우저 탭에서 수집을 실행하지 않음 |
| AP 수집 주기 | 기본 5분, 설정 범위 1~60분 |
| 부가 수집 | 기본 15분 검토. 실제 필드·범위·비용을 확인한 기능만 활성화 |
| 요청 제한 | HTTP 요청 45초; 수집 단위 전체 120초; 로그인·재인증 포함 |
| 응답 크기 | 압축 해제 후 64 MiB 상한; 초과는 명시적 실패 |
| 동시 실행 | 네트워크 수집 단위 1개; 수동 수집도 같은 잠금 사용 |
| 재인증 | 세션 만료 증거가 있을 때 한 단위 내 재로그인·재조회 최대 1회 |
| 인증 거절 | 반복 로그인 중단. 명시적 재시도 또는 자격 증명 재등록까지 유지 |
| 상태 반영 | 유효한 관측 1회 기준; 임의로 Down 2회 대기 규칙을 추가하지 않음 |
| 갱신 지연 | 기본 수집 주기의 2배 이상 유효 관측 없음. 장애 확정과 구분 |
| 시간 | UTC 저장, 한국 표준시 표시. 실행 순서는 증가하는 실행 ID로 유지 |
| 영구 이력 | 사건·스냅샷·변경·연결·교체·작업·정정·회차별 현황은 자동 삭제 없음 |
| 상세 보존 | 수집 실행·종결 공백 상세 90일; 운영 로그·전력 수치 표본 각각 30일 |
| 원문 XML | 기본 저장 안 함 |
| 백업 | 하루 1회 온라인 백업, 검증된 최근 7개. 업데이트 전 백업 별도 |
| 운영 데이터 | 기본 `%ProgramData%\AirWaveAPMonitor\live`; 실행 파일 위치와 분리 |
| Demo 데이터 | Mac 사용자 데이터 경로의 `AirWaveAPMonitor/demo`; 운영 DB와 공유 금지 |
| 실제 자격 증명 | Windows 수집 프로세스의 실행 계정 범위 DPAPI. 평문 대체 금지 |
| 네트워크 | HTTPS 검증 유지; 실제 장비를 변경하는 요청 없음; UI 외부 CDN 없음 |

운영 API 로그인·AP 목록 수신은 아직 확인되지 않았다. 기존 진단에서 인증서 이름·신뢰 체인·유효기간 오류가 관측되었다. PoE·LLDP 공식 API의 실제 필드·단위·최신성도 미확인이다. 이를 성공으로 가정해 구현하지 않는다.

## Review Focus

다음 다섯 경계를 해당 작업의 자동검증에 반드시 연결한다.

1. **HTTP 200 로그인 HTML·잘린 XML·DTD·잘못된 루트·크기 초과:** AP 성공이나 Down 0으로 처리하지 않음 — Task 2, 4, 6.
2. **동일 AP ID의 장비 세대 교체·시리얼 충돌·명시적 추적 종료 후 재편입:** 구장비 허위 복구와 오장비 병합 없음 — Task 3, 8.
3. **커밋 직전/직후 종료·저장 불가·90일 상세 정리:** 중복 반영 없음, 과거 현황과 열린 사건 유지 — Task 4, 7, 10.
4. **수집 중 설정 변경·부분 성공 반복·늦은 부가 응답:** 범위·세대 혼합, 신규/복구 중복, 스냅샷 소급 변경 없음 — Task 4, 5, 9.
5. **브라우저 사용자와 서비스 계정 차이·한글/공백 경로·기존 데이터 업데이트:** 비밀은 실제 수집 계정에서 처리하고 데이터 보존 — Task 2, 7, 10; Windows 확인 여부 별도 표시.

---

## 1. 구현 구조와 공통 계약

### 프로젝트·파일 배치

기존 코드가 없는 경우 아래 이름을 사용한다. 빈 서비스·리포지터리 계층을 과도하게 만들지 말고 각 파일이 한 책임을 갖게 한다.

| 경로 | 책임 |
| --- | --- |
| `AirWaveApMonitor.slnx`, `global.json`, `Directory.Build.props` | 솔루션, 확인한 정식 .NET 10 SDK 고정, 공통 빌드 설정 |
| `src/AirWaveApMonitor.Core/Contracts/` | 수집·관측·실행 결과와 인터페이스 |
| `src/AirWaveApMonitor.Core/Monitoring/` | 장비 식별·세대·감시 구간·상태 전환·회차 분류 |
| `src/AirWaveApMonitor.Infrastructure/AirWave/` | 로그인·세션, AP XML 파싱, 실제 읽기 어댑터·진단 |
| `src/AirWaveApMonitor.Infrastructure/Demo/` | 가상 XML·시간순 시나리오 공급자 |
| `src/AirWaveApMonitor.Infrastructure/Storage/` | SQLite, 버전 마이그레이션, 원자적 반영·조회 |
| `src/AirWaveApMonitor.Infrastructure/Security/` | DPAPI 저장소, Demo용 메모리 자격 증명 |
| `src/AirWaveApMonitor.Infrastructure/Maintenance/` | 백업·복원·보존·데이터 경로·중복 실행 방지 |
| `src/AirWaveApMonitor.App/Program.cs` | 웹 호스트·DI·실행 모드·Windows 서비스 연결 |
| `src/AirWaveApMonitor.App/Collection/` | 수집 조정, 주기 실행, 수집기 건강 상태 |
| `src/AirWaveApMonitor.App/Endpoints/` | 현황·이력·설정·작업·CSV·진단 HTTP 진입점 |
| `src/AirWaveApMonitor.App/wwwroot/` | 로컬 UI; 모의화면을 바탕으로 실제 API와 연결 |
| `tests/AirWaveApMonitor.Tests/` | 상태·파서·실제 임시 SQLite·스케줄·HTTP 통합 검증 |
| `tests/AirWaveApMonitor.WindowsTests/` | DPAPI·Windows 실행 조건 검사; Mac에서는 미실행 표시 |
| `scripts/` | Mac 개발·검증·게시, Windows 실행·자동 시작 설치·제거 |
| `docs/DEVELOPMENT_STATUS.md` | 작업별 진행률·증거·남은 작업·검증 수준 |
| `docs/guides/` | Mac 개발, Windows 실행, 사내 확인, 백업·업데이트 안내 |

공통 앱과 코어는 `net10.0`으로 Mac에서 실행되게 한다. 앱 전체를 `net10.0-windows`로 묶지 않는다. Windows API는 플랫폼 확인 뒤 호출하며, 플랫폼 호환성 경고를 프로젝트 전체에서 무시하지 않는다. 프런트엔드는 우선 별도 Node 빌드가 필요 없는 로컬 HTML/CSS/JavaScript를 사용한다.

### 고정할 최소 계약

Task 1에서 `Core/Contracts/CollectionContracts.cs`, `ObservationContracts.cs`, `StorageContracts.cs`, `CredentialContracts.cs`에 아래 이름과 의미를 정의한다. 세부 속성은 설계의 저장 항목을 빠짐없이 반영하며 버전 있는 DTO로 관리한다.

```csharp
public interface IApSource
{
    Task<ApCollectionBatch> CollectAsync(CollectionContext context, CancellationToken ct);
}

public interface ICollectionStore
{
    Task<CollectionContext> BeginRunAsync(ProfileSnapshot profile, CollectionKind kind, RunTrigger trigger, CancellationToken ct);
    Task<CommitReceipt> ApplyApBatchAsync(CollectionContext context, ApCollectionBatch batch, CancellationToken ct);
    Task<EnrichmentCommitReceipt> ApplyEnrichmentBatchAsync(CollectionContext context, EnrichmentBatch batch, CancellationToken ct);
    Task<DashboardSnapshot> GetDashboardAsync(DashboardQuery query, CancellationToken ct);
}

public interface ICredentialStore
{
    Task SaveAsync(Guid datasetId, Uri origin, AirWaveCredentials credentials, CancellationToken ct);
    Task<AirWaveCredentials?> ReadAsync(Guid datasetId, Uri origin, CancellationToken ct);
    Task ClearAsync(Guid datasetId, Uri origin, CancellationToken ct);
}

public interface ICollectorControl
{
    Task<RunRequestReceipt> RequestAsync(CollectionKind kind, RunTrigger trigger, CancellationToken ct);
    Task SetPausedAsync(bool paused, CancellationToken ct);
}
```

- `CollectionKind`: `ApStatus`, `ConnectionNeighbor`, `Power`. `RunTrigger`: `Scheduled`, `Manual`, `Startup`, `Resume`. `RunOutcome`: 수집 결과인 `Succeeded`, `Partial`, `Failed`. 진행 중/비정상 종료를 나타내는 실행 생명주기는 결과와 별도로 보관한다.
- `ProfileSnapshot`: 인스턴스·데이터셋 ID, 정규화한 HTTPS 출처, 설정 버전, 범위 버전, 기대 대상과 기준 목록 확인 상태, 수집 설정의 불변 복사본이다.
- `CollectionContext`: 증가하는 `long RunId`, `ProfileSnapshot`, 수집 종류·요청 사유, UTC 시작 시각·기한을 포함한다. 네트워크 대기 중 DB 쓰기 트랜잭션을 유지하지 않는다.
- `ApCollectionBatch`: `RunOutcome`, 유효 AP 관측, 제외 레코드와 사유, 수집 시각, 오류 분류·건수를 포함한다. 실패의 빈 배열은 정상적으로 확인한 0대가 아니다.
- `ApObservation`: 원천 AP ID, `ApState?` (`Up`/`Down`/null), 장비 식별·기본정보, 원천 시각과 수집 시각, 필드별 제공·유효 상태다. AP ID 중복 등 귀속을 확정하지 못한 레코드는 상태뿐 아니라 해당 레코드의 자동 정보 변경에도 쓰지 않는다.
- `CommitReceipt`: `RunId`, `AlreadyApplied`, `Outcome`, `Summary`. UI에는 커밋된 관측 결과만 공개하며 저장 실패는 수집기 건강 상태에 따로 표시한다.
- `RunSummary`: `ConfirmedUp`, `ConfirmedDown`, `Unknown`, `NewDown`, `InitialDown`, `ExistingDown`, `Recovered`, `OpenIncidents`, 대상·제외 건수다. 전체 실패의 이번 Up/Down 및 변화 대수는 nullable로 두어 0과 구분한다. 기준 목록을 알면 전체 실패의 `Unknown`은 대상 수이며, 기준 목록 자체가 미확인이면 그 한계도 표현한다. 열린 사건 수는 마지막 저장 사실을 유지한다.
- `DashboardQuery`: 선택한 과거 `RunId`, 검색, 필터, 정렬, 페이지 조건이다. `DashboardSnapshot`은 같은 회차의 집계·당시 행·품질·기준 시각을 담고, 최신 수집기 건강 상태와 구분한다.
- `EnrichmentBatch`와 `EnrichmentCommitReceipt`: 연결·이웃 또는 전력 단위의 결과·관측·완전성·품질·독립 반영 여부다. AP 집계를 재계산하거나 AP 상태 트랜잭션을 되돌리는 용도로 사용하지 않는다. `(데이터셋, 수집 종류, RunId)`의 재적용을 막는다.
- `RunRequestReceipt`: 요청 수락·이미 실행 중·주기 건너뜀·인증 중단 등과 실제 실행 ID 또는 대기 식별자를 구분한다. 대기 요청을 이미 실행·저장한 회차처럼 표시하지 않는다.
- `AirWaveCredentials`는 비밀이 포함된 `ToString()`·직렬화·일반 로그 출력을 제공하지 않는 타입으로 만든다. 비밀번호를 담은 record의 자동 출력도 피한다.
- 시각은 `DateTimeOffset`과 주입한 `TimeProvider`를 사용한다. 테스트에서 실제 5분을 기다리지 않고 시간을 제어한다.

`AppMode`·설정/요청 DTO는 Task 1, `DiagnosticReport`는 Task 2, 식별·감시·전환 DTO는 Task 3, 불변 저장·스냅샷 DTO는 Task 4에서 정의한다. 작업·가져오기 DTO와 부가 관측의 상세 타입은 각각 Task 8·9에서 확장한다. 같은 의미의 타입을 계층마다 따로 만들어 의미가 달라지지 않게 한다.

상태 머신은 네트워크나 DB에 직접 접근하지 않는 결정 로직으로 만들고, 저장 계층은 트랜잭션 내부에서 최신 저장 상태를 읽어 그 결정을 적용한다. 구현 문서와 사용자 표시 문구는 한국어를 기본으로 한다.

### 주요 읽기·변경 경로

`/api/health`, `/api/dashboard`, `/api/aps/{id}`, `/api/incidents`, `/api/runs`, `/api/runs/{id}`, `/api/export.csv`를 읽기 기본 경로로 둔다. 수집 요청·중지·설정·자격 증명·메모·작업·가져오기는 POST/PUT 계열로 분리한다. 모든 변경 요청에 위조 요청 방지와 동일 출처·Host 검사를 적용한다. 실제 사용자 계정별 접근 분리 제품을 새로 만들지는 않으며, 신뢰하는 단일 운영자 PC라는 기존 범위를 유지한다.

현재 현황의 기본 회차는 **현재 데이터셋·범위에서 가장 최근에 최종 확정한 `ApStatus` 회차**다. 마지막 성공 회차나 더 늦게 끝난 부가 수집 회차를 대신 선택하지 않는다. 최신 AP 회차가 실패·중단이면 그 미확인 상태와 마지막 유효 관측을 표시한다. 진행 중 회차는 건강 상태에만 표시하며 아직 확정하지 않은 집계와 기존 집계를 섞지 않는다.

## 2. 반드시 보존할 상태·데이터 의미

### 상태 전환

| 직전 유효 상태 | 이번 유효 관측 | 결과 |
| --- | --- | --- |
| 없음 | Up | 기준 상태, 복구 없음 |
| 없음 | Down | 최초 확인 Down 사건, 실제 시작 미상 |
| Up | Down | 신규 사건 1개 |
| Down | Down | 기존 열린 사건 갱신 |
| Down | Up | 같은 장비 세대·감시 구간의 사건 복구 확인 1회 |
| Up | Up | 관측 갱신 |
| 어느 상태든 | 조회 실패·누락·불명 | 상태 전환·복구 없음, 마지막 상태·공백 유지 |

1. 기본 식별 키는 AirWave 인스턴스 + AP ID. 이름/IP로 병합하지 않는다. 세대 판단은 시리얼 우선, 사용할 수 없으면 유선 MAC이며 누락만으로 교체하지 않는다.
2. 세대 분리를 상태 전환보다 먼저 적용한다. 구장비 Down → 교체 장비 Up은 구장비 복구가 아니다. 구장비는 추적 종료, 새 장비는 기준 상태다. 자동 식별 변경은 물리 교체 확인과 구분한다.
3. 범위 일시 제외 후 재편입과 명시적 추적 종료 후 재편입을 구분한다. 후자는 새 감시 구간에서 시작하며 종료된 사건을 재개하지 않는다.
4. 부분 성공에서도 AP별 직전 유효 상태를 기준으로 신규·복구를 계산한다. 마지막 전체 성공 회차나 Down 대수 차이로 계산하지 않는다.
5. 기준 목록 수립 후 성공·부분 성공 회차는 `대상 = 유효 Up + 유효 Down + 미확인`, `유효 Down = 신규 + 기존 미복구 + 최초 확인 Down`을 만족한다. 복구는 Up의 일부다. 전체 실패·중단 회차는 이번 Up/Down·변화 대수를 null, 미확인을 기대 대상 수로 저장하며 위 수치 합계식의 적용 대상에서 제외한다. 기준 규모가 아직 미확인이면 전체 대상 확인이 끝난 것처럼 표시하지 않는다.
6. 전체 실패는 현재 Down 0 또는 전체 정상으로 표시하지 않는다. 마지막 미복구와 원래 확인 시각을 유지한다. 미복구 사건 수와 이번 Down 대수는 다를 수 있다.
7. AP 관측 시각, AirWave `last_contacted`, 연결·LLDP·전력의 원천 시각을 분리한다. 시계 역행은 실행 순서로 처리하고 음수 시간을 표시하지 않는다.
8. 사건 감지·복구와 회차 목록에는 당시 값·출처·시각·품질을 불변으로 저장한다. 현재 이름·IP·스위치·포트 또는 늦게 도착한 응답으로 과거를 다시 쓰지 않는다.
9. 화면 필터는 수집 범위를 바꾸지 않는다. 계획 작업의 Down도 관측으로 보존한다. 메모·작업·감시 제외는 자동 복구 사유가 아니다.
10. AP 상태, 연결·이웃, PoE는 각각의 실행 결과·성공 시각·트랜잭션을 가진다. 부가 실패가 이미 저장한 AP 상태를 취소하지 않는다.

### 저장해야 할 항목과 이력

- **AP:** 내부 ID, 인스턴스/AP ID, 장비 세대·감시 구간, 이름, IP, 유선 MAC, 시리얼, 모델, 펌웨어, 확보되는 가동시간·재부팅 참고 정보.
- **위치·소속:** 사업장·건물·층·구역·상세 위치·설치 지점 ID, AirWave 위치 원문·폴더·그룹, 컨트롤러 ID·이름·IP, 운영·작업·철거·감시 제외 상태와 사유.
- **연결:** SW 원천 ID·hostname·관리 IP, 실제 포트명·원천 인덱스, AP 인터페이스, 관측 방향, 출처, 수집·원천 시각, 품질. 복수 연결을 허용한다.
- **사건:** 마지막 Up, 첫/마지막 Down, 복구 확인, 최초 Down 여부, 공백, 추적 중지·종료 사유, 당시 장비·연결·부가 정보 스냅샷.
- **변경:** 기본정보·연결·장비 교체·펌웨어/컨트롤러 변경 전후 값과 근거. 장비 이력과 설치 지점의 배치·교체 이력은 별개다.
- **작업:** 예정/실제 시각, 기록 시각, 작업자(선택), 내용·결과, 확인된 원인/확인 중, 여러 대상/사건 연결, 메모 정정·취소와 이전 버전.
- **수동 정보:** 직접 입력·CSV의 출처와 자동값을 분리하고 차이를 표시한다. 이름/IP·중복 시리얼만으로 애매한 장비에 적용하지 않는다. 적용 전 미리보기와 행별 충돌 결과를 제공한다.
- **조건부 부가 관측:** LLDP 출처·원천 최신성·범위 완전성, 포트 상태, AP 소비전력·포트 실제/할당/최대 전력·공급 상태 등을 구분한다. W·Wh/kWh·dBm을 혼용하지 않고 값 누락은 null이다.

기본정보·장애/복구·연결·교체·펌웨어/컨트롤러·작업/조치의 승인 범위를 모두 구현한다. PoE·LLDP의 저장·표시·제공 상태 관리도 구현하되, 확인되지 않은 실환경 필드나 의미를 만들어내지 않는다.

## 3. 단계별 구현 작업

각 단계는 아래 자동검증과 해당 문서 갱신을 마친 뒤 커밋한다. 상태·저장·통신 경계의 검증은 필요하지만 단순 스타일이나 설정 파일의 내용을 그대로 되풀이하는 테스트는 만들지 않는다.

### Task 1: Mac 실행 골격·계약·Demo 분리 — 8%

**Files:** 솔루션·세 프로젝트·두 테스트 프로젝트, `global.json`, `.gitignore`, `Core/Contracts/*`, `App/Program.cs`, `Infrastructure/Maintenance/StoragePaths.cs`, `Infrastructure/Demo/ScenarioApSource.cs`, `scripts/dev-mac.sh`, `tests/AirWaveApMonitor.Tests/Runtime/ModeIsolationTests.cs`.

**Interfaces:** 위 공통 계약을 생성한다. 앱 옵션은 `--mode demo|live`, `--data-dir`, `--port`로 통일한다. `StoragePaths.Resolve(AppMode mode, string? overridePath)`는 OS·모드별 데이터 경로를 반환한다.

- [ ] `uname -m`, `dotnet --info`, `dotnet --list-sdks`, Git 상태를 확인한다. Apple Silicon이면 Arm64 정식 .NET 10 SDK를 선택하고 확인한 정확한 SDK 버전을 `global.json`에 기록한다. .NET 11이 설치되어 있다는 이유로 타깃을 변경하지 않는다.
- [ ] Demo/Live 경로가 같거나 다른 모드의 DB 표시자를 여는 경우 명시적으로 거부하는 테스트를 먼저 작성한다. 테스트 이름은 `SameDataDirectoryForDifferentModes_IsRejected`, `Demo_DoesNotResolveLiveCredentials`로 한다.
- [ ] 공통 계약과 앱 부트스트랩을 구현한다. Mac 기본 개발 명령은 명시적 Demo 모드다. Live의 미설정 상태를 가상 데이터로 대체하지 않는다. Mac의 Windows 전용 운영 기능 요청은 기능 미지원 안내를 반환한다.
- [ ] 데모용 2,048대 자료와 정상·신규 Down·복구·전체 실패·일부 누락의 시나리오 정의를 준비한다. 시나리오는 이후 실제 파서·상태·DB 경로에 투입한다. 제품 UI의 버튼만으로 숫자를 바꾸는 구현을 완성품으로 취급하지 않는다.
- [ ] `dotnet build AirWaveApMonitor.slnx` 및 `dotnet test tests/AirWaveApMonitor.Tests --filter ModeIsolationTests`를 실행한다. Mac에서 `dotnet run --project src/AirWaveApMonitor.App -- --mode demo`로 루프백 기본 화면과 모드 표시가 열리는지 확인한다.
- [ ] 개발 방법·채택 SDK·이 단계 결과를 문서화하고 `feat: bootstrap cross-platform monitor and demo isolation`로 커밋한다.

### Task 2: AirWave 읽기 어댑터·계정 보호 경계·연결 진단 — 12%

**Files:** `Infrastructure/AirWave/AirWaveClient.cs`, `AirWaveSession.cs`, `ApListParser.cs`, `ConnectionDiagnostics.cs`, `Infrastructure/Security/DpapiCredentialStore.cs`, `DemoCredentialStore.cs`, `App/Endpoints/SettingsEndpoints.cs`, `tests/AirWaveApMonitor.Tests/AirWave/*Tests.cs`, `tests/AirWaveApMonitor.WindowsTests/DpapiCredentialStoreTests.cs`, `scripts/publish-win.sh`.

**Interfaces:** `AirWaveClient : IApSource`; `ApListParser.Parse(Stream xml, DateTimeOffset observedAt)` returns `ApCollectionBatch`; `ConnectionDiagnostics.RunAsync(ProfileSnapshot profile, CancellationToken ct)` returns `DiagnosticReport`. `DiagnosticReport`는 단계, 실제 실행 계정, 연결 경로, TLS 결과, nullable HTTP 상태, 인증/조회/파싱 건수와 소요 시간을 구분한다.

- [ ] 가상 `HttpMessageHandler`와 문서 기반 XML fixture로 `LoginHtml200_IsFailure`, `InvalidRoot_IsFailure`, `DuplicateIds_AreExcluded`, `UnknownState_IsNotDown`, `ExpiredSession_RetriesOnce`, `Forbidden_DoesNotRelogin`, `InvalidCredentials_StopAutomaticLogin`, `Secrets_AreRedacted`를 작성한다. DTD/외부 엔터티·64 MiB 초과도 실패해야 한다.
- [ ] 공식 문서의 `POST /LOGIN`과 쿠키·`X-BISCOTTI`, `GET /ap_list.xml` 일괄 읽기를 구현한다. URL 인코딩된 폼 필드·응답 구조는 공식 원문을 대조한다. 문서 샘플에 있는 SSL 검증 해제를 복사하지 않는다. 자동 다른 출처 리다이렉트는 차단하고 로그인 HTML을 AP XML로 처리하지 않는다.
- [ ] AP 분류·ID·상태와 필드별 유효성을 확인한다. 컨트롤러·스위치를 AP 대수에 포함하지 않는다. 알 수 없는 분류·필드의 실제 지원 여부는 `미검증`으로 남기고 진단에 표시한다. 중복 ID는 상태뿐 아니라 해당 레코드의 식별·정보 변경에도 사용하지 않는다.
- [ ] 45초 요청·120초 단위·압축 해제 후 크기 제한·취소를 실제 코드로 적용한다. 401 등 세션 만료 증거에만 재인증 1회, 403은 별도 권한/토큰 오류, 인증 거절은 자동 로그인 중단으로 처리한다. 일반 통신 실패는 다음 주기에 재시도한다.
- [ ] 기본 프록시는 OS/실행 계정 설정을 사용하고 직접 연결·명시적 고정 프록시는 선택한 설정으로만 적용한다. PAC URL을 고정 프록시 서버로 취급하지 않는다. TLS 실패로 HTTP 응답이 없으면 `HTTP_STATUS=미수신`으로 표시한다. 인증서 검증 해제·임의 신뢰 등록·시스템 프록시 변경은 하지 않는다.
- [ ] 자격 증명 등록은 백그라운드 프로세스가 실제 실행 계정의 DPAPI로 처리한다. 브라우저 사용자의 계정으로 별도 암호화하지 않는다. Mac Demo는 가상 메모리 자격 증명만 사용한다. 비밀·토큰·쿠키·원문 응답은 일반 로그에 쓰지 않는다. Windows 계정 변경/복호화 실패 시 재등록이 필요하다고 표시한다.
- [ ] Mac에서 AirWave 테스트를 실행하고 Windows 전용 검사는 미실행으로 구분한다. `publish-win.sh`의 일반 self-contained 게시 기능을 먼저 만들어 **초기 연결 진단·단일 회차 조회용 패키지**를 준비한다. 해당 패키지에 아직 구현되지 않은 이력 기능이 있는 것처럼 표시하지 않는다.
- [ ] 초기 진단은 HTTP 전송·세션·파서를 재사용하는 별도 읽기 경로로 제공한다. 진단 ID와 결과만 반환하며 아직 없는 DB 저장 기능·운영 `RunId`를 흉내 내지 않는다. Task 4 이전에는 자격 증명 저장과 연결 진단만 제공하고 장애·복구 기록 완료로 표시하지 않는다.
- [ ] 실제 API 확인을 기다리는 동안 Task 3 이후를 계속 진행한다. 연결 단계·검증 상태와 초기 패키지 범위를 기록하고 `feat: add AirWave read adapter and connection diagnostics`로 커밋한다.

### Task 3: AP 식별·세대·감시 구간·상태 전환 — 14%

**Files:** `Core/Monitoring/IdentityResolver.cs`, `MonitoringSegmentPolicy.cs`, `StateTransitionEngine.cs`, `RunClassifier.cs`, `tests/AirWaveApMonitor.Tests/Monitoring/*Tests.cs`.

**Interfaces:** `IdentityResolver.Resolve(ApIdentitySnapshot? previous, ApObservation current)` returns `IdentityDecision`; `StateTransitionEngine.Evaluate(ApTrackingSnapshot? previous, ApObservation current, CollectionContext context)` returns `ApTransitionDecision`; `RunClassifier.Summarize(IReadOnlyList<ApTransitionDecision> decisions, IReadOnlySet<string> expectedIds, RunOutcome outcome)` returns `RunSummary`.

`previous == null`은 최초 관측이다. `decisions`에는 해당 범위의 기대 AP마다 한 결과를 두며 누락·전체 실패도 마지막 상태·열린 사건을 유지하는 미확인 결과로 표현한다. 같은 범위에서 유효하게 새로 발견한 AP는 기준 등록을 거쳐 해당 회차 대상에 포함하되, 누락 AP를 기대 목록에서 자동 삭제하지 않는다. 식별·상태 DTO에는 인스턴스/AP ID, 장비 세대, 감시 구간, 이전 유효 상태와 열린 사건 참조, 관측 품질을 포함한다.

최초 목록은 잠정 기준으로 저장하고, 조회된 대수와 사용자가 사내에서 대조한 기준 확인 상태를 분리한다. 잠정 상태에서도 유효 AP의 감시·개발을 진행할 수 있다. 설정의 범위 버전과 현재 알고 있는 기대 AP 집합은 다른 개념이며, 새 AP 발견만으로 과거 범위·회차의 목록을 다시 쓰지 않는다.

- [ ] 상태표의 모든 행을 theory로 작성한다. `Up_Down_Down_Up_CreatesOneIncidentAndOneRecovery`는 사건 1·복구 1·첫 감지 불변을, `FirstDown_HasUnknownActualStart`는 최초 Down이 신규 전환에 포함되지 않음을 검증한다.
- [ ] `HardwareReplacement_PrecedesRecovery`, `IdentityFieldMissing_DoesNotReplace`, `SameNameAndIp_DoesNotMerge`, `SerialCollision_DoesNotMergeDevices`를 작성한다. 시리얼이 애매한 경우 이름/IP나 첫 행으로 임의 해결하지 않는다.
- [ ] `ScopePauseAndReentry_ContinuesIncident`, `ExplicitEndAndReentry_StartsNewSegment`, `ObservationGap_PreservesIncident`, `ClockRollback_DoesNotProduceNegativeDuration`를 작성한다.
- [ ] 위 함수를 순수한 결정 로직으로 구현한다. 공백 이후 Down·Up의 의미, 실제 시작/복구 시각 미상, 장비 세대와 감시 구간을 구분한다. 사건 메모로 상태를 변경하는 경로를 제공하지 않는다.
- [ ] `dotnet test tests/AirWaveApMonitor.Tests --filter FullyQualifiedName~Monitoring`을 실행하고 기대값과 모든 불변 조건을 확인한다. `feat: implement AP identity and incident transitions`로 커밋한다.

### Task 4: SQLite 원자적 저장·회차별 불변 현황 — 14%

**Files:** `Infrastructure/Storage/SqliteCollectionStore.cs`, `SchemaMigrator.cs`, `Migrations/001_core.sql`, `Core/Monitoring/IncidentSnapshotBuilder.cs`, `tests/AirWaveApMonitor.Tests/Storage/*Tests.cs`.

**Interfaces:** `SqliteCollectionStore : ICollectionStore`; `IncidentSnapshotBuilder.Build(CommittedDeviceView view, CollectionContext context)` returns `IncidentSnapshot`. DB 반영은 저장 계층 안에서 최신 상태를 읽고 Task 3의 결정을 적용한다.

- [ ] 실제 임시 SQLite로 `SameRunAppliedTwice_IsIdempotent`, `CommitFailure_RollsBackAllApChanges`, `CommittedRunRetried_DoesNotDuplicate`, `ReopenDatabase_ContinuesOpenIncident`를 작성한다.
- [ ] 001 스키마에 데이터셋/설정·범위 버전, 장비·세대·감시 구간, AP 현재값, 실행, 회차 목록/전환, 사건/스냅샷, 관측 공백, 스키마 버전을 만든다. 현재값과 불변 표시 버전은 구분한다. 열린 사건의 유일성과 실행 재적용 방지를 DB 제약으로도 보장한다.
- [ ] AP 기본정보·상태·세대·사건·스냅샷·회차 집계/목록·전환 참조·최종 결과를 같은 트랜잭션에서 확정한다. 쓰기는 직렬화하고 SQLite WAL을 사용한다. 네트워크 요청 중 쓰기 트랜잭션을 잡지 않는다.
- [ ] `PartialSuccess_DoesNotRepeatTransitions`, `HistoricalRun_DoesNotUseCurrentMetadata`, `FailedRun_HasUnknownCountsAndPreservedOpenIncidents`를 작성한다. 실패한 실행의 유효 수 0을 정상 0대 현황으로 바꾸지 않는다.
- [ ] 파싱 결과와 실행 시작 당시 기대 목록·같은 범위의 신규 유효 대상을 대조해 최종 결과를 정한다. 파서가 정상이어도 기대 AP 일부가 누락되면 `Partial`로 낮추며, 유효 관측이 없거나 처음부터 0건이면 `Failed`와 대상/권한 확인 필요 이유를 남긴다. 기준 목록 검증 여부는 실행 결과와 별도로 유지한다.
- [ ] 실행 시작 후 종료되지 못한 기록은 재시작 때 `Interrupted`로 정리한다. 시작 당시 범위 기준의 회차 요약·미확인 목록을 원자적으로 확정하고, 이번 Up/Down·변화 대수는 null, 사건과 마지막 유효 상태는 유지한다. 중단 회차도 과거 현황 조회와 최신 AP 회차 선택에 포함한다. DB 저장 실패 시 성공 시각을 갱신하지 않으며 수집기 건강 상태에는 실패를 알린다.
- [ ] 자동 인증 중단 사유·발생 시각·대상 데이터셋/출처·자격 증명 버전을 프로필 상태로 영속화한다. 프로세스 재시작이 이 중단을 초기화하지 않게 한다. 명시적 재시도 또는 자격 증명 재등록만 해제 계기로 삼고, 다시 거절되면 중단 상태를 다시 저장한다.
- [ ] `dotnet test tests/AirWaveApMonitor.Tests --filter FullyQualifiedName~Storage`로 오류 주입·재시도·재개와 집계/목록 일치를 확인하고 `feat: persist atomic collection runs and immutable history`로 커밋한다.

### Task 5: 자동 수집·수동 수집·수집기 건강 상태 — 10%

**Files:** `App/Collection/CollectionCoordinator.cs`, `CollectionWorker.cs`, `CollectorHealth.cs`, `App/Endpoints/CollectorEndpoints.cs`, `tests/AirWaveApMonitor.Tests/Collection/*Tests.cs`.

**Interfaces:** `CollectionCoordinator : ICollectorControl`; `CollectorHealth.GetSnapshot()` returns `CollectorHealthSnapshot`. `TimeProvider`를 주입한다. 수동/예약/시작/재개 요청은 동일 조정기를 통과한다.

- [ ] `ScheduledAndManualRequests_NeverOverlap`, `LongApRun_SkipsNextTick`, `EnrichmentYieldsToOnePendingApRun`, `Resume_CollectsOnceWithoutCatchupStorm`, `AuthRejected_RemainsPausedAfterRestart`를 제어 가능한 시간과 대기 중인 가상 요청으로 작성한다.
- [ ] AP 수집 중 새 AP 주기는 건너뛴 횟수를 기록한다. 부가 요청 중 AP 시각이 오면 AP 대기 표시 하나만 두고 현재 요청 종료/시간 제한 후 AP 1회를 우선한다. 부가 요청을 무한 이어가거나 놓친 주기를 쌓지 않는다.
- [ ] 실행 시작 당시 프로필·범위·설정 버전을 고정하고 변경은 해당 실행 종료 후 적용한다. 구범위 응답을 새범위 누락 판정에 사용하지 않는다. 소스 출처 변경은 새 데이터셋·세션 폐기·자격 증명 재등록으로 처리한다.
- [ ] 마지막 시도·AP 유효 DB 반영·마지막 전체 대상 확인·다음 예정·일시 중지/인증 중단·DB/백업 오류를 구분한다. DB에 실패 로그를 쓸 수 없어도 메모리의 제한된 건강 상태와 파일 진단으로 표시한다.
- [ ] 테스트와 실제 짧은 Demo 실행으로 화면을 열지 않아도 수집·저장이 진행되는지 확인한다. 종료 시 취소·중복 실행 방지까지 검증하고 `feat: schedule collection with explicit health reporting`으로 커밋한다.

### Task 6: 실제 Down 화면·AP 상세·회차·CSV — 12%

**Files:** `App/Endpoints/MonitoringEndpoints.cs`, `ExportEndpoints.cs`, `LocalRequestGuard.cs`, `App/wwwroot/index.html`, `styles.css`, `app.js`, `tests/AirWaveApMonitor.Tests/Web/*Tests.cs`.

**Interfaces:** `ICollectionStore.GetDashboardAsync`의 한 회차 결과를 화면/내보내기에 함께 사용한다. `/api/dashboard?runId=...`와 `/api/runs/{id}`는 과거 스냅샷을 반환하고 현재 현황 복귀는 과거 회차 선택을 해제한다.

- [ ] API 통합검증에 `DashboardAndExport_UseSameRun`, `FailedCollection_IsNotZeroDown`, `UnknownAp_RemainsInOpenIncidentView`, `PastRun_ReturnToCurrent`, `Csv_UsesBomAndEscapesFormulaCells`를 작성한다.
- [ ] `Dashboard_UsesLatestFinalApRunIncludingFailure`로 최신 실패·중단 AP 회차를 마지막 성공이나 부가 회차로 대체하지 않는지 검증한다. 이전 범위의 현황은 선택 가능한 과거 이력으로 남긴다.
- [ ] 모의화면의 Down 중심 구성·색상·한국어를 실제 백엔드와 연결한다. 첫 설정 이후 첫 화면은 Down 현황이다. 이번 Down·신규·복구·미확인·미복구를 구분하고 대수/시각/품질을 함께 표시한다.
- [ ] AP 이름/IP/MAC/시리얼/위치/폴더/그룹/컨트롤러/SW/포트 검색, 상태·관리 상태 필터, 최근 감지/오래된 미복구 정렬과 서버 페이지 조회를 구현한다. 계획 작업 숨김은 숨긴 대수를 표시한다.
- [ ] AP 상세에 장비·연결 정보와 각각의 확인 시각, 장애/공백/복구를 표시한다. 메모·작업 입력과 수정 이력은 Task 8에서 연결하며 미구현 입력을 가짜 저장 성공으로 표시하지 않는다. 이미 복구된 사건의 경과는 복구 확인 시각에서 멈춘다. 열린 장애가 없는 미확인 AP를 미복구로 표현하지 않는다.
- [ ] 과거 회차·장애 이력, 설정·단계별 진단, 수집 기록을 구현한다. 검색 필터 유지·빈 목록/실패 구분·설정 수정 시 연결확인 결과 초기화를 포함한다. 화면의 서버 조회 실패도 현재 값처럼 표시하지 않는다.
- [ ] CSV는 화면 필터·관측 기준 시각·품질·당시 필드를 포함하고 UTF-8 BOM, 인용/줄바꿈, 수식 주입 방지를 적용한다. UI는 장비 이름·메모·원문 필드를 HTML로 실행하지 않게 한다.
- [ ] 변경 요청의 Host/동일 출처/위조 요청 방지 테스트를 실행한다. Mac 브라우저에서 주요 흐름과 좁은 창을 확인한다. 자동 브라우저가 차단되면 정확한 제한과 API/DOM 등 실제 수행한 대체 검증만 기록한다.
- [ ] `dotnet test tests/AirWaveApMonitor.Tests --filter FullyQualifiedName~Web` 및 Demo 다섯 상황을 확인하고 `feat: connect monitoring dashboard to persisted data`로 커밋한다.

### Task 7: Windows 운영·보존·백업·핵심 반입 패키지 — 10%

**Files:** `Infrastructure/Maintenance/BackupService.cs`, `RetentionService.cs`, `InstanceLock.cs`, `App/Program.cs`, `scripts/publish-win.sh`, `scripts/windows/Start-Monitor.ps1`, `Install-Service.ps1`, `Remove-Service.ps1`, `Install-ScheduledTask.ps1`, `Remove-ScheduledTask.ps1`, `Backup-Restore.ps1`, `docs/guides/WINDOWS_RUN.md`, `UPDATE_AND_RECOVERY.md`, `tests/AirWaveApMonitor.Tests/Maintenance/*Tests.cs`.

**Interfaces:** `BackupService.CreateAsync(CancellationToken ct)` returns `VerifiedBackup`; `RestoreAsync(string backupPath, CancellationToken ct)`는 수집 중지·스키마/무결성 검사 후 복원한다. `RetentionService.PruneAsync(DateTimeOffset now, CancellationToken ct)`는 영구 이력과 참조 중인 데이터를 보존한다.

- [ ] `PruneAfter90Days_PreservesHistoricalRuns`, `FailedNewBackup_KeepsSevenGoodBackups`, `Update_PreservesDatabaseAndSettings`, `IncompatibleDatabase_IsPreservedAndRejected`, `DiskWriteFailure_DoesNotAdvanceSuccess`를 작성한다.
- [ ] 하루 1회 SQLite 온라인 백업과 정상 최신 7개 보관, 업데이트 전 별도 백업, 정지 상태의 복원을 구현한다. WAL 사용 중 파일을 단순 복사하는 방식으로 백업하지 않는다. 복원 이후 공백을 표시한다.
- [ ] 상세 90일·로그/전력 표본 30일 정리를 구현하되 사건·스냅샷·변경·작업·회차 현황·열린 사건·미종결 공백·과거 조회에 필요한 참조를 삭제하지 않는다.
- [ ] 동일 앱의 대화형 실행과 Windows 서비스 호스팅을 지원한다. 설치는 별도 명시적 스크립트이며 앱 실행만으로 서비스를 등록하지 않는다. 권한·정책에 맞춘 예약 작업 대안도 준비한다. 실행 계정 변경과 DPAPI 재등록 안내를 포함한다.
- [ ] 서비스·예약 작업 제거는 자동 시작 등록만 해제하고 운영 DB·설정·백업을 보존한다. 선택한 실행 계정의 데이터 경로 접근 권한, 서비스와 대화형 앱의 중복 실행, 포트 사용 중 시작 실패를 명확히 처리한다. 실패를 피하려고 외부 인터페이스로 바인딩하지 않는다.
- [ ] PowerShell 스크립트는 Windows PowerShell 5.1 호환 구문, UTF-8 BOM, `$PSScriptRoot` 기반 경로와 정확한 인용을 사용한다. 한글/공백 경로, 실제 줄바꿈을 처리한다. 정책 우회·전역 실행정책/방화벽/인증서 변경은 넣지 않는다. 설치 권한이 필요한 동작과 일반 실행을 구분한다.
- [ ] 런타임·Windows SQLite 네이티브 파일·웹 자원·스크립트·안내를 포함한 배포 폴더와 ZIP을 생성한다. 일반 self-contained를 먼저 완성하며 물리적으로 모든 파일을 EXE 하나에 압축하는 작업은 초기 완료 조건으로 삼지 않는다.
- [ ] Windows가 있으면 실제 패키지를 인터넷 의존 없이 실행하고 브라우저 종료·재시작·데이터 유지·DPAPI를 확인한다. 없으면 Windows용 생성과 패키지 정적 확인만 기록한다. Windows 확인 대기로 Task 8을 중단하지 않는다.
- [ ] 핵심 감시 기능의 반입 범위와 아직 남은 전체 승인 범위를 표시하고 `feat: add Windows operation and recoverable deployment`로 커밋한다.

### Task 8: 승인된 운영 이력·설치 지점·수동 정보·CSV 가져오기 — 12%

**Files:** `Core/Operations/ChangeDetector.cs`, `PlacementPolicy.cs`, `ManualImportValidator.cs`, `Infrastructure/Storage/Migrations/002_operations.sql`, `App/Endpoints/OperationsEndpoints.cs`, `App/wwwroot/operations.js`, `tests/AirWaveApMonitor.Tests/Operations/*Tests.cs`.

**Interfaces:** `ChangeDetector.Compare(DeviceMetadata previous, DeviceMetadata current, ObservationEvidence evidence)` returns `IReadOnlyList<ChangeEvent>`; `ManualImportValidator.Preview(Stream csv, ImportMapping mapping)` returns `ImportPreview`. 적용은 미리보기의 버전·대상 식별을 다시 확인한 뒤 트랜잭션으로 수행한다.

- [ ] `InitialMetadata_IsBaselineNotChange`, `MissingField_DoesNotDeletePreviousValue`, `ManualAndAutomaticValues_AreKeptSeparately`, `AmbiguousSerialCsvRow_IsRejected`, `NoteCorrection_KeepsPreviousVersion`, `Notes_DoNotRecoverAp`, `PlacementChange_DoesNotRewriteOldIncident`를 작성한다.
- [ ] 기본정보·연결·교체·펌웨어/컨트롤러 변경의 이전값/새값/출처/감지·원천 시각을 저장한다. 장비 세대와 설치 지점 배치를 분리하고 동일 지점의 다른 AP 사건을 병합하지 않는다.
- [ ] 계획/실제 작업, 원인 확인 상태, 조치 결과, 대상/사건 복수 연결, 메모 정정·취소를 구현한다. 작업 등록이나 감시 제외가 AP 복구로 처리되지 않게 한다.
- [ ] 직접 입력과 CSV 가져오기는 미리보기·충돌·행별 결과·정확한 내부 키/유일한 시리얼·MAC 대조를 제공한다. 식별 불가·중복·충돌 행은 적용하지 않는다. CSV의 상태/전력/LLDP를 자동 관측으로 등록하지 않는다.
- [ ] 장비별·설치 지점별 타임라인, 이력 종류·기간 필터, 수동/자동 차이를 표시한다. 현재 값 변경 후에도 과거 회차와 사건 스냅샷이 동일한지 확인한다.
- [ ] 관련 테스트와 실제 UI 흐름을 확인하고 `feat: add operational history and reviewed inventory imports`로 커밋한다.

### Task 9: 연결·LLDP·PoE의 조건부 관측 지원 — 5%

**Files:** `Core/Enrichment/CapabilityStatus.cs`, `ConnectionEvidencePolicy.cs`, `PowerAttributionPolicy.cs`, `Infrastructure/AirWave/EnrichmentSource.cs`, `Infrastructure/Storage/Migrations/003_enrichment.sql`, `App/Endpoints/EnrichmentEndpoints.cs`, `tests/AirWaveApMonitor.Tests/Enrichment/*Tests.cs`.

**Interfaces:** `IEnrichmentSource.CollectAsync(CollectionContext context, CancellationToken ct)` returns `EnrichmentBatch`; `ConnectionEvidencePolicy.Evaluate(NeighborObservation observation)` returns `NeighborAssessment`; `PowerAttributionPolicy.Resolve(PowerSample sample, IReadOnlyList<ConnectionObservation> observations)` returns `PowerAttribution`.

`ICollectionStore.ApplyEnrichmentBatchAsync`에서 해당 단위의 관측·변경·품질·공백·실행 결과를 독립 트랜잭션으로 반영한다. 시작할 때의 장비 세대·연결 근거를 검증하고 중복 실행은 기존 반영 결과를 반환한다.

- [ ] `UnknownProtocol_IsNotLldpHealthy`, `RepeatedRead_DoesNotRefreshSourceTime`, `PartialNeighborResponse_DoesNotCloseConnection`, `MissingPower_IsNotZero`, `PowerSample_UsesHistoricalConnection`, `EnrichmentFailure_DoesNotRollbackApRun`를 작성한다.
- [ ] 복수 유선 연결, 발견 출처, 방향, 원천 시각과 조회 완전성, AP/포트 실제·할당·최대 전력의 의미별 저장·표시를 구현한다. 전력 수치 변화는 표본이며 매번 기본정보 변경 사건으로 만들지 않는다.
- [ ] 프로필·장비 유형·필드별 `미검증 / 제공 확인 / 미지원 확인`을 관리한다. 조회 실패·빈 값 한 번으로 미지원 확정하지 않는다. 실제 계약을 확인하지 못한 부가 어댑터는 비활성/미검증으로 유지한다.
- [ ] 공식 문서에서 확인한 경로와 필드만 구현하고 가능한 부분은 가상 응답으로 검증한다. 가상 테스트 통과가 실필드 제공 확인 상태를 올리지 않게 한다. 직접 스위치 SSH/SNMP·비공식 웹 요청을 기본 기능으로 추가하지 않는다.
- [ ] AP 소비전력과 포트 전력, W와 Wh/kWh·dBm을 구분한다. 포트 표본의 AP 귀속은 당시 연결 근거를 보존하고 새 장비로 소급 이전하지 않는다. upstream·포트 Up·PoE 공급을 LLDP 지속 정상으로 해석하지 않는다.
- [ ] 상태·품질·공백·트랜잭션·AP 우선 스케줄 검증을 실행한다. 코드 지원, 실제 필드 지원, 사내 확인 상태를 따로 적고 `feat: add capability-gated link and power observations`로 커밋한다.

### Task 10: 통합 검증·배포 확인·사용자 인수 자료 — 3%

**Files:** `scripts/verify-mac.sh`, `scripts/package-win.sh`, `tests/AirWaveApMonitor.Tests/Acceptance/*Tests.cs`, `docs/guides/MAC_DEVELOPMENT.md`, `ONSITE_VALIDATION.md`, `DATA_DICTIONARY.md`, `docs/DEVELOPMENT_STATUS.md`, `README.md`, `DEVELOPMENT_LOG.md`.

**Interfaces:** 위에서 생성한 실제 명령·앱 경로를 안내에 사용한다. 확인하지 않은 실행 결과나 다운로드 파일을 문서에 만들지 않는다.

- [ ] 아래 수용 검증표의 각 항목을 구현/테스트/실행 증거에 연결한다. 이미 충분히 확인한 항목을 이유 없이 반복하기보다 남은 실패 위험을 해결한다.
- [ ] 2,000대·5,000대, 전체 Down·전체 미확인, 반복 상태 변경의 파싱·집계·저장을 측정한다. 소요 시간·DB 증가량·메모리 결과는 실제 수치만 기록하고 임의 성능 보장을 쓰지 않는다.
- [ ] Mac clean restore/build/test, Demo 실행·재시작, `win-x64` publish, 패키지 구성·버전·SHA256을 확인한다. Windows 실제 실행이 없으면 패키지에 해당 미검증 상태를 함께 표시한다.
- [ ] 반입 패키지에 게시 결과 전체, 실행·자동 시작 스크립트, 버전/커밋 정보, 설정 예시, `README_WINDOWS.md`, `ONSITE_VALIDATION.md`, `SHA256SUMS.txt`를 포함한다. ZIP이 자기 자신을 포함하거나 Demo DB·비밀·개발 캐시가 들어가지 않게 확인한다.
- [ ] 사내 확인표는 사용자용 절차로 작성한다. 연결 단계, 실제 대상 대수 대조, 상태 비교, 자동 실행, 재부팅 후 이력 유지, 실패 시 버전·오류 단계 확인을 포함한다. 사내 원문 자료 외부 업로드를 요구하지 않는다.
- [ ] 작업 결과·다음 작업·진행률·검증 수준·알려진 제한을 정리하고 `docs: finalize development evidence and Windows handoff`로 커밋한다. 필요한 경우 준비된 작업 브랜치를 원격에 보존한다.

## 4. 수용 검증표

| ID | 입력·상황 | 기대 결과 | 소유 작업 |
| --- | --- | --- | --- |
| A01 | Up → Down → Down → Up | 사건 1, 복구 1, 첫 감지 유지 | 3, 4 |
| A02 | 최초 Down → Down → Up | 실제 시작 미상, 최초 Down은 최초 회차만 | 3, 4 |
| A03 | 2,048대·Down 8 후 전체 실패 | 현재 Up/Down 미확인, 미확인 2,048, 열린 사건 8 유지 | 4, 6 |
| A04 | 모의화면 11:25, AP 3·20·21 누락 | Up 2,038 + Down 7 + 미확인 3 = 2,048; 미복구 8 | 4, 6 |
| A05 | 부분 응답에서 Down, 다음에도 Down, 이후 Up | AP별 전환으로 사건 1·복구 1; 전체 성공 시각과 독립 | 3, 4 |
| A06 | 같은 실행 두 번, 커밋 전/후 종료 | 전부 롤백 또는 한 번만 확정; 중복 사건/표본 없음 | 4 |
| A07 | Down → 실패 → 재시작 → Down → Up | 동일 사건, 공백 유지, 재개 후 Up 시각에 복구 확인 | 3, 4, 5 |
| A08 | 동일 AP ID의 시리얼 교체, 구장비 Down | 세대 분리 먼저, 구장비 추적 종료, 허위 복구 없음 | 3, 8 |
| A09 | 이름/IP 변경·지점 이동·같은 시리얼 두 ID | 병합 없음, 변경/배치 이력, 애매한 CSV 적용 거부 | 3, 8 |
| A10 | 설정 변경 중 이전 범위 응답 도착 | 이전 버전으로 보존, 새 범위 누락/제외 판정에 사용 안 함 | 4, 5 |
| A11 | 계획 작업·메모 정정·감시 제외·재편입 | 자동 사실 유지, 수정 이력, 일시중지/명시종료 구분 | 3, 8 |
| A12 | HTTP 200 HTML, 잘못된 루트, 중복 ID, 불명 상태, 0건 | 전체 실패/부분 유효/대상 확인 필요를 구분; Down 0 정상 아님 | 2, 4, 6 |
| A13 | 401 재인증 실패, 403, TLS 오류 | 제한 재인증·로그인 중단·오류 분류, 비밀 유출 없음 | 2, 5 |
| A14 | AP 성공 후 부가 실패·오래된 LLDP·전력 누락 | AP 성공 유지, 원천 시각 보존, 0 W·LLDP 정상 오판 없음 | 5, 9 |
| A15 | 늦은 부가 응답·현재 포트/장비 변경 | 과거 스냅샷·전력 표본 귀속 불변 | 4, 9 |
| A16 | Demo↔Live, Live API 실패 | 모드별 DB/설정 분리; 가상 성공으로 대체 없음 | 1, 2, 6 |
| A17 | PC 시계 역행, 절전 후 재개, 중복 실행 | 음수 시간 없음, 실행 순서 유지, AP 1회 재개, 중복 방지 | 3, 5, 7 |
| A18 | 90일 정리·DB 쓰기 실패·백업 실패·업데이트 | 과거 회차/열린 사건 보존, 성공 위장 없음, 정상 백업 유지 | 4, 7 |
| A19 | 서비스 계정 A, UI 사용자 B, 계정 변경 | 비밀 처리 A 기준, 계정 변경시 재등록; Windows 확인 별도 | 2, 7 |
| A20 | 2,000/5,000대·전체 Down/미확인 | 정확한 대수/목록, 중첩 없음, 측정값 기록 | 10 |

Demo의 11:05→11:10→11:15→11:20→11:25는 가상 시각이다. 운영 프로그램의 현재 시각이나 수집 결과에 이 값을 고정하지 않는다. A04에서는 미확인 3대 중 1대가 마지막 Down이고 나머지 2대는 마지막 Up이다.

## 5. 실행·게시 명령의 계약

아래 명령은 프로젝트를 만든 뒤 실행할 기준이다. 이 문서 작성 시점에 해당 코드나 스크립트가 이미 존재하거나 명령이 통과했다는 뜻이 아니다.

```bash
dotnet --info
dotnet restore AirWaveApMonitor.slnx
dotnet build AirWaveApMonitor.slnx -c Release --no-restore
dotnet test tests/AirWaveApMonitor.Tests -c Release
dotnet run --project src/AirWaveApMonitor.App -- --mode demo
```

Windows 게시의 기본값:

```bash
dotnet publish src/AirWaveApMonitor.App/AirWaveApMonitor.App.csproj \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishAot=false \
  -p:PublishReadyToRun=false \
  -p:PublishTrimmed=false \
  -p:PublishSingleFile=false \
  -o artifacts/win-x64/app
```

- 운영 패키지는 Windows 실행 파일을 진입점으로 하고 필요한 파일을 함께 배포한다. Python·Node·.NET SDK 설치나 실행 중 NuGet/CDN 다운로드를 요구하지 않는다.
- 물리적 단일 EXE 패키징은 SQLite 네이티브 라이브러리, 웹 자원, 서비스 계정의 추출 경로를 확인한 후 별도 개선할 수 있다. 처음부터 NativeAOT·트리밍·R2R로 플랫폼 문제를 늘리지 않는다.
- Mac에서 Windows EXE를 생성한 결과는 `win-x64 게시 성공`으로만 기록한다. Mac Demo의 실행 파일과 Windows 반입 패키지를 구분한다.
- 빌드/검증 스크립트는 실패 시 0이 아닌 종료 코드를 반환한다. 상위 스크립트가 실패를 성공으로 덮지 않는다.
- 초기에는 로컬 빌드 스크립트를 우선한다. 매 커밋마다 불필요한 Windows 빌드·중복 아티팩트를 만드는 CI를 자동 추가하지 않는다. 필요 시 수동 실행과 짧은 보존 방식부터 검토한다.

## 6. 기록·진행률·작업 보고

매 작업 단위마다 `DEVELOPMENT_LOG.md`에 요청/문제, 작업 내용, 선택 이유, 실제 검증 명령·결과, 남은 제한, 다음 작업을 기록한다. `docs/DEVELOPMENT_STATUS.md`에는 아래 고정 가중치와 완료 증거를 관리한다.

| 작업 | 가중치 | 누적 |
| --- | ---: | ---: |
| 1. Mac 골격·계약·모드 분리 | 8 | 8 |
| 2. API·진단·계정 보호 경계 | 12 | 20 |
| 3. 식별·상태·사건 | 14 | 34 |
| 4. 원자적 저장·회차 현황 | 14 | 48 |
| 5. 자동 수집·건강 상태 | 10 | 58 |
| 6. 핵심 화면·CSV | 12 | 70 |
| 7. Windows 운영·백업·핵심 패키지 | 10 | 80 |
| 8. 승인된 운영 이력·가져오기 | 12 | 92 |
| 9. 조건부 부가 관측 구조 | 5 | 97 |
| 10. 통합 확인·인수 자료 | 3 | 100 |

이는 **개발 산출물 진행률**이며 실환경 운영 보장률이 아니다. 완료는 구현·해당 환경에서 가능한 의미 있는 검증·문서 증거를 갖춘 항목에만 부여한다. 단계 내 부분 진행은 최초에 고정한 실질 체크항목으로 계산하며, 쉬운 항목을 늘려 진행률을 부풀리지 않는다. Windows 전용 검사의 미실행은 별도 표에 남기고 Mac 대체 테스트를 실제 Windows 검증으로 계산하지 않는다. 실제 LLDP·PoE 필드가 미확인인 상태에서 Task 9의 개발 구조가 완료되더라도 실환경 기능 제공 확인으로 보고하지 않는다.

매 사용자 보고 형식:

```text
1. 이번 작업
   - 바뀐 기능과 파일, 바꾼 이유
2. 검증 결과
   - 실행한 명령과 실제 결과
   - Mac Demo / 공통 테스트 / win-x64 게시 / Windows 실행 / 사내 연동 상태
3. 다음 작업
   - 다음 구현 단계와 남은 문제
4. 개발 산출물 진행률
   - XX / 100% (완료 항목·근거)
5. 사용자 사내 검증
   - 대기 또는 실제 전달받은 결과; 개발 진행률과 분리
6. 산출물
   - 현재 커밋, 실제 생성한 패키지 경로·버전, 실행 방법
```

작업 종료 전에 저장된 파일, Git 변경, 주요 검증 결과, 실제 생성한 패키지를 읽어 확인한다. 수행하지 않은 빌드·테스트·실행·업로드를 성공이라고 기록하지 않는다. 모르는 값은 미확인으로 남기고 가능한 다음 작업을 계속한다.

## 7. 완료 시 전달할 것

- 실제 구현 소스와 의미 있는 자동검증.
- Mac에서 실행할 수 있는 Demo와 재현 가능한 검증 스크립트.
- Windows 11 x64 런타임 포함 반입 ZIP 및 SHA256·버전·커밋 정보.
- 연결 진단, 오류 단계·HTTP 미수신 표시, 비밀이 제거된 운영 로그.
- Windows 실행·서비스/예약 작업·계정 등록·업데이트·복구 안내.
- 사용자용 사내 확인표와 실환경 미확인 항목 목록.
- 승인된 범위의 구현 상태·개발 진행률·검증 수준이 일치하는 README·작업 기록.

## 8. 근거 문서와 적용 범위

버전·필드·API 사용은 실제 구현 시 아래 공식 원문과 현재 저장소를 대조한다. 공식 문서의 예제에 있는 인증서 검증 해제나 프로젝트와 무관한 관리 API는 이 지시서의 실행 지시가 아니다.

- [AirWave 8.2.14.0 API Guide](https://arubanetworking.hpe.com/techdocs/Archived/AirWave/82140/webhelp/Content/API%20Guide/API%20Guide.htm): 세션 인증과 AP XML 조회. 실제 설치 환경의 인증/권한/필드 확인은 별도.
- [.NET macOS 설치](https://learn.microsoft.com/en-us/dotnet/core/install/macos): Apple Silicon Arm64 SDK와 SDK/런타임 구분.
- [dotnet publish](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-publish): 대상 RID와 self-contained 게시.
- [.NET 단일 파일 배포](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview): OS/아키텍처별 게시, 네이티브 라이브러리·추출 경로 고려.
- [HttpClient.DefaultProxy](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclient.defaultproxy?view=net-10.0): OS·실행 계정의 기본 프록시 설정 차이.
- [Windows DPAPI](https://learn.microsoft.com/en-us/dotnet/standard/security/how-to-use-data-protection): 실제 프로세스 계정의 비밀 저장·복호화.
- [Windows BackgroundService](https://learn.microsoft.com/en-us/dotnet/core/extensions/windows-service): 대화형/Windows 서비스 호스팅.
- [SQLite WAL](https://www.sqlite.org/wal.html), [Online Backup](https://www.sqlite.org/backup.html): 로컬 저장과 일관된 백업.

나머지 운영 이력·원천 최신성·LLDP/PoE 판단의 상세 근거와 예외는 Spec §4~§9 및 S7/S8을 따른다. 구현하지 못한 실제 어댑터를 가짜 정상값으로 대체하거나 기존 승인 범위를 조용히 줄이지 않는다.
