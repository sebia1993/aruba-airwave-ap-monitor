# Mac 개발 안내

## 준비 환경

이 프로젝트는 정식 .NET 10 SDK **10.0.404**와 `net10.0`을 사용한다. 2026-11-15 개발 호스트에서 macOS Arm64, SDK 10.0.404, .NET/ASP.NET Core 런타임 10.0.15를 확인했다. `global.json`은 자동 버전 전환과 미리 보기 SDK를 비활성화하여 10.0.404를 정확히 사용한다. .NET 11로 타깃을 변경하지 않는다.

이 호스트의 SDK 실행 파일은 `/Users/ilen/.dotnet/dotnet`이다. 전역 셸 설정을 바꾸지 않고 다음처럼 현재 터미널에서만 사용할 수 있다.

```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet --info
dotnet --list-sdks
```

## 실행

저장소 루트에서 실행한다. 별도 Node 설치나 프런트엔드 빌드는 필요하지 않다.

```bash
./scripts/dev-mac.sh
```

스크립트는 항상 `--mode demo`를 지정한다. 브라우저에서 `http://127.0.0.1:8765`를 연다. 프로세스는 터미널에서 실행되며 `Ctrl+C`로 종료한다. 브라우저를 닫는 동작과 프로세스 종료는 다르다.

같은 실행을 직접 호출할 수도 있다.

```bash
dotnet run --project src/AirWaveApMonitor.App -- --mode demo
```

포트나 데이터 폴더를 바꾸려면 다음처럼 지정한다. 한글·공백이 포함된 경로는 따옴표로 감싼다.

```bash
./scripts/dev-mac.sh --port 8877 --data-dir "/tmp/AirWave 가상 자료"
```

앱은 `--mode demo|live`, `--data-dir`, `--port`만 받는다. 모드는 생략할 수 없으며, 중복 옵션·알 수 없는 옵션·범위를 벗어난 포트는 시작 오류다. `--urls`와 환경 변수의 URL/Kestrel 엔드포인트로 외부 수신 주소를 추가할 수 없다. 포트를 바꾸어도 IPv4 루프백에만 바인딩한다.

## 데이터와 실행 모드

| 환경 | 기본 폴더 |
| --- | --- |
| macOS Demo | `~/Library/Application Support/AirWaveAPMonitor/demo` |
| macOS Live | `~/Library/Application Support/AirWaveAPMonitor/live` |
| Windows Live | `%ProgramData%\AirWaveAPMonitor\live` |
| Windows Demo | `%LocalAppData%\AirWaveAPMonitor\demo` |

첫 실행은 빈 데이터 폴더에 `runtime-mode.json`을 원자적으로 기록한다. 표시자에는 버전·모드·데이터셋 ID가 있고, 같은 모드로 다시 열 때 같은 ID를 사용한다. 다른 모드, 손상되거나 필수 필드가 빠진 표시자, 표시자 없이 기존 자료가 있는 폴더는 거부한다. 상대 경로나 심볼릭 링크로 같은 폴더를 가리켜도 표시자를 공유하므로 모드를 우회할 수 없다. 표시자를 지워서 기존 자료를 다른 모드에 편입하지 않는다.

**현재 Task 2 단계에서는 데이터 폴더에 모드 표시자와 저장한 프로필을 보관한다.** Windows Live에서 등록한 자격 증명은 해당 프로세스 계정의 DPAPI로 보호한다. AP DB·수집 결과·운영 이력은 아직 저장하지 않는다. `runtime-mode.lock`은 표시자 초기화를 보호하는 파일이며, 전체 수집 프로세스의 중복 실행 잠금은 후속 단계다.

Demo는 실제 자격 증명 공급자를 만들거나 읽지 않는다. 자격 증명 등록을 거부하며 로그인 없이 가상 XML을 실제 파서에 공급한다. Live는 미설정 상태를 표시하고 가상 AP 공급자를 등록하지 않는다. macOS의 Live 자격 증명 저장은 평문 대체 없이 미지원 응답이다. Windows DPAPI는 구현했으며 서비스·예약 작업은 후속 단계다.

## 현재 화면과 API

화면은 실행 모드와 준비 상태, 연결 설정, 실제 프로세스 실행 계정과 진단 결과를 표시한다. 자동 수집·상태 처리·SQLite 저장이 없음을 명시하고, 수집하지 않은 자료를 Down 0대나 전체 정상으로 표시하지 않는다. 모든 HTML/CSS/JavaScript는 앱에서 제공한다.

- `GET /api/health`: 실행 모드, `connection-diagnostics`, 자동 수집·이력 저장 미구현 상태, Demo 자료 대수.
- `GET /api/dashboard`: 저장된 현황이 없으므로 HTTP 503.
- `GET /api/capabilities/windows`: DPAPI 지원 여부와 미구현인 서비스·예약 작업의 구분.
- `GET /api/settings`, `GET /api/antiforgery`: 비밀을 제외한 설정·계정 보호 상태와 변경 요청용 토큰.
- `POST /api/settings`, `/api/credentials`, `/api/authentication/retry`, `/api/diagnostics`: 연결 설정, Windows 자격 증명 등록, 명시적 인증 재시도, 단일 읽기 진단.

모든 변경 요청은 허용된 로컬 Host, 정확한 Origin, 위조 요청 방지 쿠키·헤더 토큰을 검사한다. 진단은 자체 ID를 사용하며 DB의 운영 RunId를 만들거나 이력 저장 성공을 표시하지 않는다. 이미 저장한 서버 출처를 바꾸려면 새 전용 데이터 폴더로 시작한다. 로그인 거절 후에는 자격 증명 재등록 또는 명시적 재시도가 필요하다.

## 가상 시나리오

`ScenarioApSource`는 2,048개의 서로 다른 AP ID를 가진 가상 XML을 실제 `ApListParser`에 입력한다. 상태 머신·SQLite 연결은 후속 단계이며 현재 화면의 감시 집계로 직접 사용하지 않는다. Demo 연결 진단은 2,048대 파싱을 확인하며 실제 AirWave API 검증을 뜻하지 않는다.

| 순서 | 가상 시각 KST | 내용 |
| --- | --- | --- |
| 선행 1 | 10:55 | 상태 기준 관측. AP 6은 아직 상태 미제공 |
| 선행 2 | 11:00 | AP 1~5, 9~10 Down. AP 6은 상태 미제공 |
| 표시 1 | 11:05 | Down 8대. 신규 7·8, 최초 확인 6, 복구 9·10의 관측 근거 |
| 표시 2 | 11:10 | AP 11·12 신규 Down으로 Down 10대 |
| 표시 3 | 11:15 | AP 2·4 복구로 Down 8대 |
| 표시 4 | 11:20 | 전체 실패. 관측 배열은 비어 있으며 성공 0대가 아님 |
| 표시 5 | 11:25 | AP 3·20·21 누락. 유효 관측 2,045대 중 Down 7대 |

마지막 시나리오 이후에는 마지막 관측 시나리오를 반복한다. 자료의 원천 시각과 `TimeProvider`가 제공하는 수집 시각을 분리한다. 이 단계에서는 신규·복구 숫자를 계산하거나 저장한 것이 아니며, 표는 후속 상태 처리의 기대 동작이다.

## 검증

```bash
dotnet build AirWaveApMonitor.slnx
dotnet test AirWaveApMonitor.slnx
dotnet test tests/AirWaveApMonitor.Tests --filter ModeIsolationTests
```

최신 명령·대수·CI 링크는 `../DEVELOPMENT_STATUS.md`에 기록한다. 테스트에는 실제 임시 폴더·영구 표시자, Kestrel HTTP 요청, 외부 URL 설정 거부, XML·가상 HTTP/시간 제어, 인증 중단 및 계정 보호가 포함된다. Windows 전용 DPAPI·경로 검사는 Mac에서 명시적으로 건너뛰며, Unix 심볼릭 링크 검사는 Windows에서 권한 조건 때문에 건너뛴다.

초기 Windows 패키지는 다음 명령으로 만든다. 정식 SDK와 Python 3가 필요하며 Node 빌드가 필요하지 않다.

```bash
python3 -m unittest discover -s tests/Packaging -v
./scripts/publish-win.sh
```

`artifacts/win-x64/`에 소스 커밋·시각을 포함하는 고유 이름의 폴더와 ZIP·SHA256 파일을 생성한다. 이전 산출물은 덮어쓰지 않는다. 런타임과 로컬 웹 자원, 실행 안내, `VERSION.json`과 파일별 해시를 포함한다. 게시 폴더에 로컬 설정·프로필·DB·자격 증명·로그가 섞이면 패키징을 중단한다. 이 Mac 명령의 성공은 Windows 실행 확인이 아니다.

Windows 호환성은 저장소의 GitHub Actions Windows 작업 성공 결과로 별도 확인한다. Mac 빌드·테스트·가상 자료 테스트는 Windows 실행이나 사내 AirWave 연동 검증을 대신하지 않는다. 실제 계정, 원문 XML, 인증서, 운영 DB나 로그를 저장소에 넣지 않는다.
