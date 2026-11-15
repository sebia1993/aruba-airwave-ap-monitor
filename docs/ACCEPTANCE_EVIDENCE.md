# 인수 검증 근거 A01–A20

기준일: 2026-11-16. 승인된 수용 검증표의 기대 동작, 실제 검사 내용과 검증 한계를 연결한다. **완료 여부·진행률·최종 검증 커밋은 [개발 진행 및 검증 기록](DEVELOPMENT_STATUS.md)을 기준으로 판단한다.** 실행 경위는 [개발 기록](../DEVELOPMENT_LOG.md), 현장 확인은 [사내 확인표](guides/ONSITE_VALIDATION.md)를 따른다.

아래 테스트 경로는 저장소 기준의 소스 위치다. 배포 ZIP에는 테스트 소스가 포함되지 않으므로 파일 링크로 표시하지 않았다. `파일::메서드`는 실제 assertion을 찾는 위치이며, 테스트 존재 자체가 최종 통합·실환경 성공을 뜻하지 않는다. 설치본의 제공 범위는 `VERSION.json`과 개발 검증 기록을 함께 확인한다.

## 확인된 실행 범위

**후속 전력 재독출 보완의 최신 Mac 통합:** clean Release 경고·오류0, 공통 **891 통과**, Windows전용 **9 Mac 제외**, Frontend **100**, Python **11 통과**. 신규 표본 재조회만 줄이고 기존 표본의 최초 귀속을 보존했다. actual SQLite 지점 이동/재독출 보존 회귀와 잘못된 귀속 mutation 실패, 관련272개·독립 검토 통과. 제품5초예산은 유지하며 Windows 원래 실패 원인과 보완 소스의 최종 Windows/ZIP은 별도 확인한다. 확정 진행률66%다.

**HTTP 직렬화 보완을 포함한 소스 `8ef2650`의 Mac 통합:** clean Release 경고·오류 0, 공통 **890 통과**, Windows 전용 **9 Mac 제외**, Frontend **100**, Python **11 통과**. Frontend는 UTC·미국 서부·서울 각각 100개 통과했다. 별도 clean 측정 소스 `ecca1d2`의 AP 2,000/5,000대 반복 장애 9회와 정상 유지 10회가 모두 전체 목록/집계·저장 구조 검사를 통과했다. 아래 802개 기록은 보완 전의 역사로 보존한다. 실제 브라우저/재시작도 확인했으며 최종 Windows/ZIP은 진행 중이고 확정 진행률은66%다.

- **기존 검증(Task 1–8):** Mac 공통 532·Node 43·Python 8 통과. Windows Actions `43749748457`, 소스 `ab9155b5fc929c876eaa2d589cea269b564cf9d2` 성공: 공통 528 통과·Mac/Unix 4 건너뜀, Windows 전용 9 통과, Node 43, Python 6 통과·Unix 2 건너뜀. 실행한 ZIP 내부 378개 파일 해시를 대조했다. 이 ZIP에는 Task 9–10이 없다.
- **초기 통합(Task 9):** Mac 788·Node 64·Python 9의 초기 검사 뒤 교체 후 귀속·회차 경계·버튼/중복 표시와 실제 2,048대의 전력 저장 시간 초과를 발견해 수정했다. 당시 결과와 실패 기록은 개발 기록에 보존하며 최종 인수 완료로 대체하지 않는다.
- **수정 후 Mac 통합:** 공통 **802 PASS**, Windows 전용 **9 Mac SKIP**, Node **76**, Python **9 PASS**, clean/restore/Release build 경고·오류 0. 실제 Demo 브라우저의 연결 2,048·전력 12,288 정상 확정과 AP 성공 시각 불변, 프로세스 재시작·과거 스냅샷 유지 확인. 소스 `4d9a822`의 2,000/5,000대 9프레임 합성 측정도 통과했다. [실제 측정](PERFORMANCE_MEASUREMENTS.md)을 참고하며 새 소스의 Windows CI·ZIP 검증은 대기 중이다.
- **실환경:** Windows CI는 Server 2025의 자동 검사다. Mac·fixture·DOM 검사와 실제 Windows 실행, 사내 Windows 11/AD·계정·AirWave·장비 확인을 구분한다. Live 연결·LLDP·PoE 어댑터는 미구현·미검증·비활성이며 Demo 자료로 제공 확인을 대체하지 않는다.

## 항목별 근거

표의 “기존 검증”은 위 Task 1–8의 해당 소스 실행 결과, “Mac 통합”은 위 수정 후 Task 9 공통/Demo 결과를 뜻한다. 모든 항목의 실제 장비·현장 자료 대조는 미확인이다.

| ID·기대 동작 | 구체적인 자동 검사와 핵심 assertion | 실행 범위·남은 확인 |
|---|---|---|
| A01 Up→Down→Down→Up | `tests/AirWaveApMonitor.Tests/Monitoring/StateTransitionEngineTests.cs::Up_Down_Down_Up_CreatesOneIncidentAndOneRecovery`: 사건·복구 각 1, 같은 사건 ID, 첫 Down 시각 유지, 마지막 열린 사건 없음. | 기존 검증. 실제 브라우저 신규·복구 표시 확인; 실장비 장애 시각 대조 미확인. |
| A02 최초 Down→Down→Up | `tests/AirWaveApMonitor.Tests/Monitoring/StateTransitionEngineTests.cs::FirstDown_HasUnknownActualStart`와 `StateTable_UsesPreviousValidState`: 실제 시작 미상·직전 Up 없음, 최초 InitialDown/반복 ExistingDown, Up 재관측은 복구 반복 없음. | 기존 검증. 실제 장애 시작 시각을 알아냈다는 의미가 아님. |
| A03 2,048대 전체 실패 | `tests/AirWaveApMonitor.Tests/Storage/ScenarioPersistenceTests.cs::Parsed2048ApScenario_PersistsFailureAndPartialWithoutLosingOpenIncident`: XML→SQLite→재열기 뒤 현재 Up/Down=null, 미확인 2,048·열린 사건 8 유지. | 기존 검증·실제 Mac 실패 화면 확인. 합성 대수 검사이며 성능 측정과 별개. |
| A04 AP 3·20·21 부분 누락 | `tests/AirWaveApMonitor.Tests/Monitoring/DemoTimelineTests.cs::Fixed2048ApTimeline_ReproducesCountsFromTransitionsAndPreservesApSpecificGaps`: Up 2,038·Down 7·미확인 3·미복구 8, AP3 공백/사건 유지·AP20 마지막 Up 유지. | 기존 검증·Mac 부분 누락/AP3 상세/과거 CSV 확인. 11:05–11:25는 가상 시각. |
| A05 부분 응답의 Down 반복 후 Up | `tests/AirWaveApMonitor.Tests/Storage/SqliteCollectionStoreTests.cs::PartialSuccess_DoesNotRepeatTransitions`: SQLite 사건 1, 신규·복구 반복 0, 마지막 완전 성공 회차는 첫 회차 유지. | 기존 검증. AP별 전환과 전체 성공 시각을 분리하며 실제 부분 응답 범위는 현장 확인 필요. |
| A06 재시도·커밋 전후 실패 | `tests/AirWaveApMonitor.Tests/Storage/SqliteCollectionStoreTests.cs::SameRunAppliedTwice_IsIdempotent`, `CommitFailure_RollsBackAllApChanges`, `CommittedRunRetried_DoesNotDuplicate`: 원래 receipt 재사용 또는 전체 롤백, 중복 사건/스냅샷 없음. | AP는 기존 검증. 부가 저장은 `tests/AirWaveApMonitor.Tests/EnrichmentStorage/EnrichmentStorageTests.cs::EnrichmentLostReceipt_ReplaysOnce_AndConflictingPayloadCannotRewriteResult`의 Mac 통합 근거. 물리 전원 손실 시험 아님. |
| A07 실패 후 재시작·Down·Up | `tests/AirWaveApMonitor.Tests/Acceptance/RestartObservationGapTests.cs::DownFailureRestartDownUp_PreservesOneIncidentItsGapAndFrozenDetection`: coordinator/SQLite 재열기 후 사건·첫 감지 유지, 공백은 재개 Down까지, 이후 Up에서 복구, 과거 탐지 스냅샷 불변. | 기존 검증. 실제 PC 전원 손실·재부팅과 구분. |
| A08 시리얼 교체·구장비 Down | `tests/AirWaveApMonitor.Tests/Monitoring/StateTransitionEngineTests.cs::HardwareReplacement_PrecedesRecovery`: 새 세대/감시 구간, 구사건 추적 종료·허위 복구 없음. `tests/AirWaveApMonitor.Tests/OperationsStorage/OperationsIntegrationTests.cs::Replacement_DoesNotInheritRegistration_AndOldWorkRetainsExactGeneration`: 대장 미상속·구세대 작업 유지. | 기존 검증. 실제 시리얼/MAC의 교체 의미와 부가 표본 귀속은 별도 확인. |
| A09 이름/IP·지점 이동·동일 시리얼 | `tests/AirWaveApMonitor.Tests/Monitoring/IdentityResolverTests.cs::SameNameAndIp_DoesNotMerge`; `tests/AirWaveApMonitor.Tests/OperationsStorage/OperationsStorageTests.cs::PlacementChange_DoesNotRewriteOldIncident_AndPointHistoryDoesNotMergeAps`; `tests/AirWaveApMonitor.Tests/OperationsStorage/OperationsImportTests.cs::ImportPreview_RechecksSerialUniquenessAfterNewCollection`: 병합 금지·사건 불변·모호한 CSV 적용 거부. | 기존 검증·Mac 지점 버전/배치/선택 CSV 적용 확인. 실제 대장·Windows Excel 미확인. |
| A10 설정 변경·늦은 구범위 응답 | `tests/AirWaveApMonitor.Tests/Storage/SqliteCollectionStoreTests.cs::FrozenContext_RejectsAlteration_AndOldScopeResultStaysOldScope`: 변조 context 거부·옛 응답에 새 대상 미포함. `tests/AirWaveApMonitor.Tests/OperationsStorage/OperationsIntegrationTests.cs::BeginFreezesManualPolicyAndPlacement_AndLaterEditsWaitForNextRun`: 시작 후 편집은 다음 회차 반영. | AP/운영 정보는 기존 검증. 부가 Current/HistoricalOnly·설정 버전 경계는 Mac 통합 검증. |
| A11 작업·정정·제외/재편입 | `tests/AirWaveApMonitor.Tests/OperationsStorage/OperationsStorageTests.cs::NoteCorrection_KeepsPreviousVersion_AndNotesDoNotRecoverAp`: 작업 3버전·이전 메모·열린 사건 보존. `PersistentExclusion_DoesNotRecoverOrReplaceAndRequiresExplicitReentry`: 일시 제외 후 동일 사건/구간. `tests/AirWaveApMonitor.Tests/OperationsStorage/OperationsIntegrationTests.cs::EndTracking_ReentryWithoutObservationStartsNewIntervalAndDoesNotRecoverOldIncident`: 명시 종료 후 새 구간·복구 0. | 기존 검증·Mac 예정/실시/취소·재시작 보존 확인. 실제 작업 이행은 자동 사실로 추정하지 않음. |
| A12 HTML 200·XML·중복·미상·0건 | `tests/AirWaveApMonitor.Tests/AirWave/AirWaveClientTests.cs::LoginHtml200_IsFailure`; `tests/AirWaveApMonitor.Tests/AirWave/ApListParserTests.cs::InvalidRoot_IsFailure`, `DuplicateIds_AreExcluded`, `UnknownState_IsNotDown`, `EmptyList_RequiresVerification`: 실패/부분 유효/확인 필요를 구분하고 미상을 Down으로 바꾸지 않음. | 기존 검증·Mac 실패 화면 확인. 실제 AirWave 버전/권한별 응답 계약 미확인. |
| A13 401·403·TLS·비밀 보호 | `tests/AirWaveApMonitor.Tests/AirWave/AirWaveClientTests.cs::ExpiredSession_RetriesOnce`, `Forbidden_DoesNotRelogin`, `InvalidCredentials_StopAutomaticLogin`, `Secrets_AreRedacted`, `TlsFailure_HasNoHttpStatus`: 제한 재로그인·자동 중단, Permission/TLS 구분, 비밀번호/token/cookie 비노출. | 기존 검증. 실제 계정·사내 인증서·프록시/PAC 정책은 미확인. |
| A14 부가 실패·오래된 LLDP·누락 전력 | `tests/AirWaveApMonitor.Tests/EnrichmentStorage/EnrichmentStorageTests.cs::EnrichmentFailure_DoesNotRollbackApRunOrItsSuccessClocks`: 실패 후 AP snapshot/성공 시각 동일. `tests/AirWaveApMonitor.Tests/Enrichment/CapabilityFreshnessTests.cs::RepeatedRead_DoesNotRefreshSourceTime`: 원천 시각/Delayed 유지. `tests/AirWaveApMonitor.Tests/Enrichment/PowerEvidenceTests.cs::MissingPower_IsNotZero`: null과 실제 0 구분. | 순수 정책·runner·SQLite·UI Mac 통합 근거. 최종 통합 891개에 포함; 실제 브라우저/전력3회 확인; Windows 진행 중; 실제 LLDP/PoE 필드 미확인. |
| A15 늦은 부가 응답·교체/이동 | `tests/AirWaveApMonitor.Tests/Enrichment/PowerEvidenceTests.cs::PowerSample_UsesHistoricalConnection`: 측정 시각의 구연결/세대 선택. `tests/AirWaveApMonitor.Tests/EnrichmentStorage/EnrichmentStorageTests.cs::LowerRunIdEnrichmentCommittedLater_NeverLeaksIntoPastApOrPowerQuery`: 늦은 커밋 전후 과거 AP/사건 동일·과거 표본에 후대 값 제외. | Mac 통합 근거. `EnrichmentReviewRegressionTests.cs::EnrichmentReview_ReplacementDuringPowerRun_DoesNotUseEndedIdentityAfterBoundary`, `EnrichmentReview_HigherRunCommittedFirst_IsExcludedFromApAndIncidentSnapshot` 회귀 포함 최신 통합 891개 통과(이전 802개 기록 별도 보존). 물리 연결 연속성 미확인. |
| A16 Demo↔Live 격리·가상 대체 금지 | `tests/AirWaveApMonitor.Tests/Runtime/ModeIsolationTests.cs::SameDataDirectoryForDifferentModes_IsRejected`, `Demo_DoesNotResolveLiveCredentials`: 모드 폴더 재사용·Live 비밀 접근 거부. `tests/AirWaveApMonitor.Tests/Settings/EnrichmentSettingsEndpointTests.cs::LiveRequestCannotEnableAnUnverifiedAdapter`: Live 활성화 요청 400·설정 불변. | 기존 모드 검증과 Task9 Mac 통합. Live 실패는 실패로 남으며 실서버 로그인 성공을 주장하지 않음. |
| A17 시계·재개·중복 실행 | `tests/AirWaveApMonitor.Tests/Monitoring/StateTransitionEngineTests.cs::ClockRollback_DoesNotProduceNegativeDuration`: 원래 시각·순서 보존, 기간 null/시간 확인 표시. `tests/AirWaveApMonitor.Tests/Collection/CollectionCoordinatorTests.cs::Resume_CollectsOnceWithoutCatchupStorm`, `ScheduledAndManualRequests_NeverOverlap`: 재개 1회·중복 요청 단일 수집. | 가상 시계·pause/resume 기존 검증. Windows 실제 중복 EXE 거부·SCM 강제 종료 후 66.039초 복구 확인. **실제 절전·재부팅·managed fatal SCM 주입은 미실행.** |
| A18 정리·쓰기/백업 실패·업데이트 | `tests/AirWaveApMonitor.Tests/Maintenance/RetentionServiceTests.cs::PruneAfter90Days_PreservesHistoricalRunsAndOpenIncidentGapDetails`: 역사/열린 참조 보존. `tests/AirWaveApMonitor.Tests/Maintenance/BackupServiceTests.cs::FailedNewBackup_KeepsSevenGoodBackups`, `Update_PreservesDatabaseAndSettingsAndSeparatePreUpdateBackup`: 정상 7개·사전 백업/설정 보존. | 기존 검증·Windows 정지 상태 백업/복원·RunId 상한/데이터 보존 확인. `tests/AirWaveApMonitor.Tests/EnrichmentStorage/EnrichmentStorageTests.cs::PowerRetention_ExpiresNumericDetailButKeepsPermanentIncidentEvidence`는 Mac 통합: 31일 뒤 원시 표본 만료·사건 불변. 실제 디스크 고장·운영 업데이트 미확인. |
| A19 서비스 A·UI B·계정 변경 | `tests/AirWaveApMonitor.WindowsTests/DpapiCredentialStoreTests.cs::CurrentProcessAccount_RoundTripsAndBindsToDatasetAndOrigin`, `CopiedCiphertext_CannotBeReboundToAnotherDatasetOrOrigin`, `TamperedCiphertext_RequiresReregistrationWithoutPlaintextFallback`: 현재 계정 DPAPI·출처/데이터셋 바인딩·복호화 오류 재등록. | Windows 동일 실행 계정 검사 통과. 서비스 smoke는 Demo LocalSystem. **서로 다른 실제 계정 A/B·계정 전환 후 재등록·사내 AD 권한은 미검증.** |
| A20 2,000/5,000대·전체 Down/미확인 | `tests/Performance/Program.cs`의 `Require`/`CheckRows`: 9프레임 집계·모든 500행 페이지의 고유 ID/상태·반복 사건·재열기/보존 검증, 파싱/저장/조회·메모리·DB 크기 측정. | 보완 후 2,000/5,000대 반복 장애 9프레임과 정상 유지 10프레임 각각 전체 목록 대조 통과. 반복 장애 총 13.726/45.638초, 정상 유지 총 14.536/51.147초. 정상 상세행 0·동일 정상 버전/대상 재사용을 직접 SQL로 확인했다. DB·메모리·잔여 회차 비용은 [측정 기록](PERFORMANCE_MEASUREMENTS.md). 순차 직접-store 도구이며 HTTP/스케줄러 동시부하·실서버 처리량을 측정하지 않음. |

## 최종 인수 시 갱신할 범위

Task9 수정 후 실제 SQLite·수집기·HTTP·브라우저 회귀를 통과했다. Windows CI 결과를 확정하고, 측정 소스와 실행 DLL 해시를 포함한 2,000/5,000대 결과를 보존한다. 최종 ZIP은 Windows에서 실행한 정확한 artifact와 내부 해시를 대조한다. 기존 Task8 ZIP이나 초기 통합 결과를 최종 전체 기능 검증으로 바꾸어 표시하지 않는다.

현장 검증에서는 실제 AirWave 목록·권한·분류, TLS/프록시, 서비스 계정·DPAPI, 절전/재부팅, 스위치/포트·LLDP 원천 시각·전력 단위, 장기 수집을 별도로 확인한다. 실제 계정·사내 원문 응답·운영 DB를 공개 산출물에 넣지 않는다.

## 원문 보완의 추가 검증

- `Storage/CompactHistoryTests.cs`: 정상 상세행 0·공유 버전/대상, 기존 schema5 업그레이드, 100ns/시각 역행·문자열/null/필드 품질, final 회차 불변성. 기존 history/운영/부가/복원 검사도 공통 resolver로 통과했다.
- `Settings/ScopeSettingsEndpointTests.cs`, `ScopeIndependentReviewTests.cs`, `Storage/ScopedRecordQualityTests.cs`: 선택 범위·CAS·복원·5,000 ID·기존 프로필 JSON 보존·사용자 기준 대조·새 발견 후 잠정 회귀·원천 전체와 적용 범위의 오류 분리.
- `Web/IncidentPeriodFilterTests.cs`, `DashboardElapsedFilterTests.cs`, `ExportQueryContextTests.cs`: 기간·최초 Down 제외 전환 수·선택 회차 시각의 미복구 경과·페이지와 동일한 CSV 필터·0대 QueryContext 행.
- `tests/Frontend/requirements-ui.test.mjs`: KST, 기간/페이지/과거 기준, 초안·늦은 응답/CAS, 미관측 대상의 대조 등록 금지와 전대상 관측 Partial 허용. DOM stub 자동 검사와 실제 브라우저 검증은 구분한다.

범위 성공응답의 문자열 enum 계약은 `ScopeSettingsEndpointTests.cs::SuccessfulScopeResponsesUseStringEnumsWithoutChangingStoredProfileSerialization`의 GET/POST/CONFIRM 3개 실제 HTTP 검사로 확인했다. 첫 브라우저 검사에서 발견한 숫자/문자열 불일치를 수정한 뒤 선택3대/대조기록과 재시작을 다시 확인했다.
