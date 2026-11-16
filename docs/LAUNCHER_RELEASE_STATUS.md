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
