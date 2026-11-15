# Windows 핵심 감시 운영

이 안내는 AP 현황·장애/복구 이력·자동 수집·Windows 실행·백업을 위한 핵심 패키지의 운영 절차다. 패키지의 `VERSION.json`과 개발 검증 기록으로 실제 포함 범위를 확인한다. 운영 이력·수동 대장·CSV 등록은 `OPERATIONS.md`를 따른다. 조건부 연결·전력 화면과 Demo 검증 절차는 [ENRICHMENT.md](ENRICHMENT.md)를 따른다. Live LLDP/PoE 어댑터는 미구현·미검증이며 비활성이다. [개발 검증 기록](../DEVELOPMENT_STATUS.md)에서 실제 Windows 검사 결과와 완료 범위를 확인한다.

[A01–A20 인수 근거표](../ACCEPTANCE_EVIDENCE.md)는 자동 검사가 확인한 동작과 현장에 남은 확인을 연결한다.

## 실행 전

- Windows x64용 ZIP 전체와 `.zip.sha256`를 함께 보관한다. `Get-FileHash -Algorithm SHA256 .\패키지.zip` 결과를 대조한 뒤 새 버전 폴더에 푼다. `app` 내부 파일은 따로 옮기지 않는다. `SHA256SUMS.txt`는 내부 파일의 무결성 목록이며 전자서명·사내 검증 완료 증명은 아니다.
- 전용 데이터 폴더를 버전 폴더 밖의 로컬 디스크에 둔다. 예: `C:\ProgramData\AirWaveAPMonitor\live`. UNC·네트워크 드라이브는 사용하지 않는다. DB·설정·DPAPI 파일·로그·백업이 이 폴더에 남는다.
- 사내 정책이 허용하는 자동 시작 방식 하나와 수집 계정을 정한다. 선택 계정에는 데이터 폴더의 수정 권한, 필요한 프록시·인증서 접근, 해당 방식의 서비스/배치 로그온 권한이 필요하다. 설치 스크립트는 계정 권한·실행 정책·방화벽·인증서·절전 정책을 전역 변경하지 않는다.
- 모든 예시는 압축을 푼 패키지 루트에서 실행한다. PowerShell 5.1을 사용하며 실행 정책이 차단하면 조직의 허용 절차를 따른다. 정책 우회 옵션을 사용하지 않는다.

## 대화형 실행과 최초 설정

```powershell
.\Start-Monitor.ps1 -Mode live -DataDirectory 'C:\ProgramData\AirWaveAPMonitor\live' -Port 8765
```

`http://127.0.0.1:8765`에서 실제 실행 계정·서버 주소·프록시를 확인하고 AirWave 자격 증명을 등록한다. 브라우저 종료는 수집을 멈추지 않는다. 콘솔 종료는 수집기를 멈춘다. 화면의 일시 중지는 새 수집을 막지만 프로세스와 이력 조회는 유지한다.

자격 증명은 **입력한 브라우저 사용자**가 아니라 **실제 수집기 실행 계정**의 DPAPI로 보호된다. 서비스 계정으로 운영할 때는 서비스를 시작한 뒤 그 서비스의 설정 화면에서 등록할 수 있다. 대화형 실행에서 다른 계정으로 이미 등록했다면 다시 등록해야 한다. 다른 PC로 데이터/DB를 복원해도 재등록이 필요하다. 자동 인증 차단은 문제 확인 후 명시적 재시도로 해제하며, 복원 뒤 재등록 필요 상태는 재시도로 우회하지 않는다.

Demo는 가상 출처와 별도 데이터 폴더를 사용한다. 실제 장비의 연결·대수·정상 상태를 검증하지 않는다.

## 서비스 등록

데이터 폴더가 없으면 먼저 전용 폴더를 만들고, 폴더 속성 → 보안에서 선택한 수집 계정에 이 폴더와 하위 파일의 수정 권한을 부여한다. 기존 상속과 다른 계정의 권한은 유지한다. 서비스/배치 로그온 권한은 조직의 계정 정책에 맞춰 준비한다. 스크립트가 등록에 성공해도 실제 계정의 파일 접근까지 입증한 것은 아니므로 시작 후 health와 실행 계정을 확인한다.

관리자 PowerShell에서 수집 계정을 입력한다. Windows 계정 암호를 명령줄 문자열·스크립트·기록 파일에 넣지 않는다.

```powershell
$collectorCredential = Get-Credential -Message 'Windows 수집 계정'
.\Install-Service.ps1 -ServiceName AirWaveAPMonitor -Mode live `
  -DataDirectory 'C:\ProgramData\AirWaveAPMonitor\live' -Port 8765 `
  -Credential $collectorCredential -StartNow
$collectorCredential = $null
```

`-StartNow`를 생략하면 등록만 한다. 등록은 자동 시작이며 비정상 종료 시 1분 간격으로 최대 3회 재시작하고 그 다음 실패부터 동작 없음으로 멈춘다. 실패가 없는 24시간 뒤 실패 횟수를 초기화한다. 등록 후 SCM의 실제 복구 동작을 다시 읽어 확인한다. 사용자의 정상 중지는 장애 재시작 조건이 아니다. 앱 실행 자체는 서비스를 설치하지 않는다.

서비스 이름은 영문·숫자·밑줄·하이픈 1~80자다. 이미 있는 같은 이름을 덮어쓰지 않는다. 기존 등록이 있거나 시작이 실패하면 서비스의 실행 경로·계정·데이터 폴더 권한·서비스 로그온 권한·포트 충돌과 이벤트 로그를 확인한다. 등록 후 회복 정책 또는 시작이 실패할 수 있으므로 오류 발생 시 등록 존재 여부도 확인한다. 다른 서비스를 삭제하지 않는다.

`-DemoLocalSystem`은 **Demo 검사용으로 명시적으로 선택할 때만** 허용한다. Live 운영 계정을 대신하는 기본값이 아니다.

## 예약 작업 대안

서비스가 허용되지 않을 때 관리자 PowerShell에서 다음처럼 등록한다.

```powershell
$collectorCredential = Get-Credential -Message 'Windows 수집 계정'
.\Install-ScheduledTask.ps1 -TaskName AirWaveAPMonitor -Mode live `
  -DataDirectory 'C:\ProgramData\AirWaveAPMonitor\live' -Port 8765 `
  -Credential $collectorCredential -StartNow
$collectorCredential = $null
```

작업 경로는 `\AirWaveAPMonitor\`다. 시스템 시작 트리거, 로그온과 독립된 암호 로그온, 중복 실행 무시, 실행 시간 제한 없음, 실패 후 1분 간격 3회 재시작, 놓친 시작의 재실행을 설정한다. 작업 계정은 일반 권한으로 실행하며 별도의 관리자 승격을 요구하지 않는다. Windows 계정 암호는 작업 스케줄러 API 호출에만 일시적으로 전달되고 앱 인수나 출력에 넣지 않는다. 계정의 배치 로그온·네트워크 접근과 암호 변경 뒤 등록 갱신은 실제 PC에서 확인한다.

예약 작업의 강제 종료는 정상적인 서비스 중지와 다를 수 있다. 저장 중이던 회차는 다음 시작에 중단/미확인으로 복원되며 관측을 만들어 채우지 않는다. 절전 복귀·전원 정책·로그아웃/재부팅 후 시작은 실제 PC에서 검증한다.

## 수집 확인과 제거

```powershell
Invoke-RestMethod 'http://127.0.0.1:8765/api/health'
```

`collector.lastFinalRunId`, `lastAttemptAtUtc`, `lastValidCommittedAtUtc`, `lastCompleteCommittedAtUtc`, `authenticationBlocked`, `storageRecoveryPending`를 각각 확인한다. 서버가 응답한다고 AP 확인이 성공한 것은 아니다. 인증 차단·수집 지연·저장 오류는 현황과 함께 확인한다.

같은 데이터 폴더의 두 수집기는 포트가 달라도 실행할 수 없다. 다른 프로그램이 포트를 점유하면 시작에 실패하며 외부 인터페이스로 바꾸지 않는다. 기존 프로세스를 확인한 뒤 정해진 운영 방식 하나만 시작한다.

등록 제거는 관리자 PowerShell에서 **설치 때의 실행 파일·데이터 폴더·모드·포트·이름과 동일한 값**으로 실행한다. 원래 버전 폴더에서 실행하거나 `-ExecutablePath`를 명시한다.

```powershell
.\Remove-Service.ps1 -ServiceName AirWaveAPMonitor -Mode live `
  -DataDirectory 'C:\ProgramData\AirWaveAPMonitor\live' -Port 8765
# 예약 작업을 선택한 경우:
.\Remove-ScheduledTask.ps1 -TaskName AirWaveAPMonitor -Mode live `
  -DataDirectory 'C:\ProgramData\AirWaveAPMonitor\live' -Port 8765
```

스크립트는 앱 소유 설명과 실행 대상 전체가 일치할 때만 중지·등록 해제를 수행한다. DB·설정·자격 증명·백업·프로그램 파일은 삭제하지 않는다. 업데이트·복원 절차는 [UPDATE_AND_RECOVERY.md](UPDATE_AND_RECOVERY.md), 현장 확인표는 [ONSITE_VALIDATION.md](ONSITE_VALIDATION.md)를 따른다.

.NET의 서비스 호스팅 방식은 [Microsoft Windows Service 안내](https://learn.microsoft.com/en-us/dotnet/core/extensions/windows-service), 작업 실행 설정은 [ScheduledTasks 공식 문서](https://learn.microsoft.com/en-us/powershell/module/scheduledtasks/new-scheduledtasksettingsset?view=windowsserver2025-ps)를 참고했다. 문서의 기능 지원과 실제 사내 PC에서의 성공 결과는 구분한다.
