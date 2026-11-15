# Mac 개발과 Demo 확인

macOS에서는 가상 자료로 화면·수집·로컬 저장·재시작을 확인한다. 실제 AirWave 계정은 입력하지 않는다. 설치본의 기능 범위, 테스트 수, 소스 버전과 Windows CI 결과는 [개발 진행 및 검증 기록](../DEVELOPMENT_STATUS.md)을 기준으로 확인한다. 이 안내의 명령이 있다는 사실만으로 해당 버전의 검증이 끝난 것은 아니다.

## 준비

- 정식 .NET 10 SDK **10.0.404**를 사용한다. `global.json`은 이 버전을 고정하며 다른 SDK로 자동 전환하지 않는다.
- 앱을 실행하는 데 Node나 프런트엔드 빌드가 필요하지 않다. 전체 개발 검증에는 **Node 24**와 **Python 3**가 추가로 필요하다.
- 현재 개발 호스트는 macOS Arm64다. Windows 실행·서비스·DPAPI 검증은 별도 Windows 환경에서 한다.

저장소 루트에서 다음을 확인한다. SDK를 사용자 폴더에 설치했다면 현재 터미널에만 PATH를 추가할 수 있다.

```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet --version
node --version
python3 --version
```

## 실행과 종료

```bash
./scripts/dev-mac.sh
```

브라우저에서 `http://127.0.0.1:8765`를 연다. 이 스크립트는 항상 Demo로 시작한다. 실제 서버 자격 증명을 읽거나 등록하지 않으며 Live 실패를 가상 성공으로 대체하지 않는다.

포트나 데이터 폴더를 바꿀 때는 다음처럼 실행한다. 같은 폴더를 다시 사용하면 기존 자료를 이어서 읽는다. 처음부터 별도로 확인하려면 기존 폴더를 지우지 말고 **새 빈 폴더**를 지정한다.

```bash
./scripts/dev-mac.sh --port 8877 --data-dir "$HOME/Library/Application Support/AirWaveAPMonitor/demo-check"
```

직접 실행할 때도 모드를 명시한다.

```bash
dotnet run --project src/AirWaveApMonitor.App -- --mode demo
```

터미널의 `Ctrl+C`는 수집 프로세스를 종료한다. 브라우저만 닫으면 프로세스는 계속 수집한다. 화면의 **일시정지**는 수집을 잠시 멈추는 기능이며 프로세스 종료나 데이터 폴더 잠금 해제가 아니다.

일반 실행 옵션은 `--mode demo|live`, `--data-dir`, `--port`, Windows 서비스용 `--service-name`이다. 같은 옵션의 중복, 지원하지 않는 옵션, 잘못된 포트는 시작 오류로 처리한다. Mac Demo에는 서비스 이름이 필요 없다. 포트를 바꾸어도 `127.0.0.1` 루프백만 사용한다. `--urls`나 환경 변수로 외부 수신 주소를 추가하지 않는다.

## 저장되는 자료

| 환경 | 기본 데이터 폴더 |
|---|---|
| macOS Demo | `~/Library/Application Support/AirWaveAPMonitor/demo` |
| macOS Live | `~/Library/Application Support/AirWaveAPMonitor/live` |
| Windows Demo | `%LocalAppData%\AirWaveAPMonitor\demo` |
| Windows Live | `%ProgramData%\AirWaveAPMonitor\live` |

앱은 이 전용 폴더에 모드·데이터셋 표시자, 설정, SQLite DB, 운영 로그와 백업을 보관한다. AP 수집 회차·현황·장애/복구 사건·관측 공백·운영 기록은 로컬 저장 자료다. 버전별 실행 파일 폴더와 데이터 폴더를 분리한다.

같은 데이터 폴더의 중복 프로세스 실행을 차단하며, 다른 모드의 폴더나 손상된 표시자를 자동으로 새 자료로 덮지 않는다. `runtime-mode.json`이나 `profile.json`을 지우거나 직접 편집하여 모드·대상을 바꾸지 않는다. WAL 사용 중인 `monitor.db` 하나만 복사해 백업으로 삼지 않는다. 백업·복원은 [업데이트와 복구](UPDATE_AND_RECOVERY.md)를 따른다.

macOS Live의 자격 증명 저장은 미지원이다. Windows에서는 **실제 수집 프로세스 계정**의 DPAPI 보호를 사용한다. Mac Demo 성공은 Windows 계정·서비스·예약 작업의 성공을 뜻하지 않는다.

## Demo에서 확인할 흐름

1. **다운 AP 현황**에서 Demo 표시, 선택 회차, 이번 Down·신규 Down·복구·미확인과 수집기 상태를 함께 확인한다. **새로고침**은 저장 자료를 다시 읽고, **지금 수집**은 새 수집을 요청한다.
2. 목록의 AP를 열어 이번 확인과 마지막 유효 관측을 구분한다. **장애·복구 이력**에서 감지/복구 스냅샷과 관측 공백을 확인한다.
3. **수집 기록**에서 이전 회차를 선택한다. 이후 수집이나 현재 이름·배치 변경으로 과거 회차와 CSV가 바뀌지 않는지 확인한다.
4. **운영 기록·대장**에서 수동 등록·설치 지점·작업·CSV 미리보기를 확인한다. 실제 적용 절차와 충돌 시 대응은 [운영 기록과 수동 대장](OPERATIONS.md)에 있다.
5. 연결·전력 기능이 포함된 버전은 [부가 관측 안내](ENRICHMENT.md)를 따른다. 기본값은 꺼짐이며 Demo에서 명시적으로 켜야 한다. 가상 관측의 제공 확인은 Live 제공 확인이 아니다.
6. 프로세스를 종료한 뒤 같은 데이터 폴더로 다시 실행하여 이전 회차·열린 사건·설정이 남는지 확인한다. 종료·재시작 시각과 새 회차 번호를 기록한다.

Demo AP 목록은 2,048대다. 새 데이터의 최초 표시 회차는 선행 관측을 거쳐 Down 8대, 신규 Down 2대, 복구 2대인 시나리오다. 이어지는 시나리오의 기대값은 다음과 같다. 재사용 폴더에서는 저장된 다음 단계에서 이어지므로 최초 화면과 같다고 가정하지 않는다.

| 순서 | 가상 장면 | 확인할 의미 |
|---|---|---|
| 최초 표시 | 11:05, Down 8 | 신규와 최초 확인 Down을 구분 |
| 다음 | 11:10, Down 10 | 새 AP 2대의 Down 관측 |
| 다음 | 11:15, Down 8 | AP 2·4의 복구 확인 |
| 다음 | 11:20, 전체 실패 | 현재 Up/Down은 미확인, 미확인 2,048, 열린 사건 8 유지 |
| 다음 | 11:25, 부분 누락 | Up 2,038 + Down 7 + 미확인 3 = 2,048; 열린 사건 8 |

11:05 등의 값은 원래 모의화면의 가상 장면이다. 실제 수집·저장 시각과 혼동하지 않는다. 마지막 장면 이후에는 마지막 시나리오를 반복한다. 실제 AP를 끄거나 실서버 오류를 만들 필요가 없다.

## 개발 검증

```bash
./scripts/verify-mac.sh
```

이 스크립트는 Release clean → restore → build → .NET 테스트, JavaScript 문법·프런트엔드 테스트, Python 패키지 테스트를 실행한다. Node가 PATH에 없다면 `NODE_BIN`에 설치된 Node 24 실행 파일의 절대 경로를 지정한다. 스크립트는 공유 빌드 출력을 정리하므로 같은 체크아웃에서 다른 빌드·테스트와 동시에 실행하지 않는다.

필요한 단계만 확인할 때는 다음 명령을 사용한다.

```bash
dotnet restore AirWaveApMonitor.slnx
dotnet build AirWaveApMonitor.slnx -c Release --no-restore
dotnet test AirWaveApMonitor.slnx -c Release --no-build
```

Windows 전용 테스트가 Mac에서 건너뛰어지면 **미실행**으로 기록한다. 테스트·fixture 통과, 실제 브라우저 확인, Windows CI, 사내 장비 확인은 각각 다른 근거다. 최신 결과와 남은 항목을 [검증 기록](../DEVELOPMENT_STATUS.md)에서 확인한다.

## Windows 반입 패키지 만들기

```bash
./scripts/package-win.sh
```

`package-win.sh`는 현재 `publish-win.sh`를 호출하여 `win-x64` self-contained Release를 게시하고 패키징한다. SDK와 Python 3가 필요하다. `artifacts/win-x64/` 아래 새 고유 폴더·ZIP·SHA256 파일을 만들며 이전 산출물을 덮어쓰지 않는다.

ZIP 전체, 버전/소스 정보와 해시를 함께 확인한다. 개발 DB·프로필·자격 증명·로그를 게시 폴더에 넣지 않는다. Mac 게시 성공은 그 ZIP을 Windows에서 실행했다는 뜻이 아니다. 실제 반입은 [Windows 안내](README_WINDOWS.md)와 [사내 확인표](ONSITE_VALIDATION.md)를 따른다.

[CONFIGURATION_EXAMPLE.json](CONFIGURATION_EXAMPLE.json)은 수집 주기와 기본 꺼짐 값을 보여 주는 **참고 예시**다. 앱이 자동으로 읽는 설정 파일도, API에 그대로 보낼 요청 본문도 아니다. `profile.json`이나 `appsettings.json`으로 이름을 바꾸어 사용하지 않는다. 실제 설정은 화면에서 현재 설정을 읽고 저장한다.
