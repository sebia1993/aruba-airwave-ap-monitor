# v0.1.1 EXE 실행 및 패키지 개선 기록

기준: v0.1.0 사용자 변경과 기존 데이터·운영 계약을 보존한다. 이 문서는 이번 추가 요청만 추적한다.

| 작업 | 변경 내용 | 실제 검증 결과 | 다음 작업 | 진행률 | 실환경 미확인 |
|---|---|---|---|---|---|
| Task 1 EXE | 최상위 실행기·브라우저 열기·동일 데이터 재사용·데이터 경로 보호·포트 진단 구현 | 최종 macOS 공통 931/931, Frontend 100, Python 15 PASS. desktop/경로 40개 포함. Release 경고/오류 0. Windows 전용 9개는 Mac SKIP | Windows 네이티브 빌드·실행 재검증 | 구현 100%, Windows 검증 진행 중 | Windows 수동 더블클릭·기본 브라우저 GUI |
| Task 2 구성 | app/tools/docs/metadata 분리·문서 링크·도구 기본경로 구현 (e5205d3) | macOS Python 15/15, PS7.6.10 구문 8/8·동작 24 PASS | 독립 검토와 Windows 통합 | 구현 100%, Windows 검증 대기 | 기존 운영 PC 경로/권한 |
| Task 3 배포 | 시작 안내·CI 컴파일·추출 ZIP 실행/재사용/충돌/Live 최초 설정 검사 준비 | macOS PS7 구문 4개, 패키지 검증 fixture 4개 PASS. 독립 검토의 오류 안내·상대 경로 수정 완료 | Windows 전체 CI와 원본 ZIP 검증 후 릴리즈 게시 | 검증·게시 진행 중 | 실제 AirWave·AD·프록시·인증서·하드웨어 |

최종 Windows CI와 게시 결과는 [v0.1.1 릴리즈](https://github.com/sebia1993/aruba-airwave-ap-monitor/releases/tag/v0.1.1)의 검증 링크를 따른다. 전체 Windows CI 성공과 다운로드 ZIP 대조 후에만 릴리즈를 게시한다. ZIP 내부 문서는 패키지 생성 시점의 기록이며 최신 결과는 이 저장소 문서와 릴리즈를 따른다. Live 연결·전력 어댑터는 이번 범위에서 변경하지 않으며 미구현·미검증·비활성 상태다.

첫 Windows 실행 [43780557973](https://github.com/sebia1993/aruba-airwave-ap-monitor/actions/runs/43780557973)은 공통 908 통과·2 실패·4 제외였다. 두 실패는 새 실행기의 최초 HTTP 포트 확인에서 약 2초 뒤 충돌로 처리된 사례이며, 원래 예외가 기록되지 않아 정확한 OS 원인은 미확정이다. 네이티브 컴파일·Windows 전용·Python·ZIP 검사는 그 실행에서 진행되지 않았다. Windows의 수신 포트 목록과 HTTP 앱 식별을 분리하고 진단을 추가했으며, 제한 시간은 늘리지 않았다. 새 Windows 분기는 다음 전체 CI로 확인한다. Mac 통과를 그 분기의 검증으로 간주하지 않는다.

두 번째 Windows 실행 [43782764443](https://github.com/sebia1993/aruba-airwave-ap-monitor/actions/runs/43782764443)은 EXE 컴파일과 desktop 검사 39 통과·1 Unix 제외를 확인했다. 공통 931개 중 925 통과·1 실패·5 플랫폼 제외이며 desktop 검사는 그 합계에 포함된다. 실패는 기존 `AcceptedVisibleRun_StillSurvivesBrowserCancellation`의 10초 Idle 대기였고, 당시 상태가 없어 원인은 미확정이다. 해당 테스트에 단계별 시간·상태 진단만 추가했고 기존 순서·판정·제한은 유지했다. macOS 대상 1개 통과와 진단 출력을 확인했다. 다음 Windows 실행에서는 한 검사 실패 후에도 독립 검사 결과를 수집하되, 모든 검사 성공 전에는 검증 ZIP을 업로드하지 않는다.

세 번째 Windows 실행 [43784351062](https://github.com/sebia1993/aruba-airwave-ap-monitor/actions/runs/43784351062)은 전체 실패했다. 공통 925 통과·1 실패·5 제외이며, 이번 실패는 `PauseDuringSeed_FinalizesThatSeedAndDoesNotBeginAnother`의 재개 후 Idle 10초 대기다. 재개 요청은 10.892초였고, 대기 종료 시 회차3은 Finalizing, 마지막 확정은 회차2, 저장 복구·저장 오류는 없었다. 이전 취소 검사는 통과했지만 그 이전 실패 원인이 소급 확정된 것은 아니다. Python은 12 통과·2 제외·1개 검사 실패(3개 하위 사례)로, 동일한 Windows 경로의 `RUNNER~1`과 `runneradmin` 문자열 차이를 잘못 비교했다.

같은 세 번째 실행에서 Windows 전용 9개, 네이티브 EXE 10개 사례, 실제 추출 ZIP의 기존 수집기·재시작·서비스·예약 작업·백업/복원 검사는 통과했다. EXE의 초기 Demo/Live 설정 화면 준비는 각각 1.536초/1.583초였으며, 별도 수집기 전체 확정은 최초 34.139초·재시작 16.914초였다. 이 개별 성공을 전체 Windows 통과로 표시하지 않는다. 전체 실패 때문에 검증 ZIP 업로드와 릴리즈 게시를 보류했으며, 경로 비교 수정과 수집 완료 대기 진단을 진행한다.

후속 검증 보정: 경로 검사는 존재하는 EXE·폴더의 실제 동일성과 미존재 데이터 경로의 상위 경로 정규화를 비교하며 데이터 미생성도 확인한다. 2,048대·실제 SQLite를 사용하는 Demo 선행 이력 통합 검사에는 임의의 10초 완료 성능 보장 대신 현재 프로필의 수집 한도(기본 120초)를 완료 대기 상한으로 적용한다. 조건 충족 즉시 끝나고 10초 경과·최종 상태를 기록하며 저장 복구 상태는 즉시 실패한다. 제품의 수집·결과 반영 제한, 5초 제어 게이트, 회차·결과 검증은 유지한다. 이는 기능 검사의 대기 기준 보정이며 성능 개선이나 이전 Windows 실패의 OS 원인 확정을 뜻하지 않는다. 새 전체 Windows CI 성공이 필요하다.

보정 후 macOS에서 Demo 선행 이력 통합 검사 12/12, Python 패키지 검사 15/15가 통과했다. 실제 PowerShell 경로 별칭 실험에서도 같은 파일·폴더와 미존재 데이터 경로를 올바르게 구분하고 다른 대상은 거부했다. Windows 8.3 경로와 변경된 통합 검사의 최종 근거는 다음 전체 Windows 실행으로 남긴다.
