# Windows Desktop Launcher Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans. Steps use checkbox syntax for tracking.

**Goal:** ZIP을 풀어 최상위 EXE를 더블클릭하면 감시 화면을 열고, 용도별 폴더와 Windows 검증된 v0.1.1 ZIP 하나를 게시한다.
**Architecture:** native Windows bootstrap → 기존 self-contained app의 명시적 desktop 시작 경로. 운영 스크립트는 backend 직접 실행을 유지한다.
**Tech Stack:** .NET 10/C#, Win32 C++/MSVC, Python zipfile, Windows PowerShell 5.1, GitHub Actions windows-latest.
**Spec:** `docs/superpowers/specs/2026-11-16-windows-desktop-launcher-design.md`

## Global Constraints

- 기존 사용자 변경, 원래 구현 계획/설계/모의 화면, 기존 v0.1.0 릴리즈 보존.
- Live 기본 ProgramData 데이터 경로 유지, 외부 바인딩/자동 서비스 등록/정책 우회 금지.
- Windows 성공은 전체 GitHub Windows CI 종료 결과로 판단. 실환경과 자동 검증 구분.

## Review Focus

- 한글·공백 경로 및 다른 cwd: 자기 EXE 위치 기준으로 backend를 찾는다.
- 반복 실행과 기존 서비스: 동일 데이터셋만 UI 재사용하고 수집기 중복 생성하지 않는다.
- 미설정 Live: AP 수집 성공을 기다리지 않고 설정 UI를 연다.
- tools 이동: 기본 경로 실행/등록 및 데이터 보존을 실제 ZIP에서 확인한다.
- 포트 충돌·누락 파일·시작 실패: 사용자에게 원인을 표시하고 unrelated 프로세스를 종료하지 않는다.

### Task 1: EXE 및 desktop 시작

**Files:** 새 `src/AirWaveApMonitor.Launcher/`, App Program/Runtime, 관련 common 테스트.
**Interfaces:** root `AirWaveAPMonitor.exe` → app executable `--desktop true` + forwarded CLI. `--no-browser true` 생략 시 브라우저/실패 안내. health product+dataset identity.
- [x] 먼저 desktop CLI 및 identity/브라우저 동작 실패 테스트 작성·실행.
- [x] Win32 bootstrap 및 C# desktop 흐름 구현; 기존 RuntimeOptions와 모니터링 동작 보존.
- [x] 관련 테스트 green 및 독립 코드 검토.

### Task 2: 목적별 ZIP 및 운영 경로

**Files:** scripts/package_initial.py, package_core.py, scripts/windows/*.ps1, tests/Packaging.
**Interfaces:** core packaging에 별도 compiled launcher 경로 입력. 위 spec 트리, metadata 내부 루트기준 체크섬. initial-diagnostics 기존 계약 유지.
- [x] 새 트리/필수 EXE/상대 링크/기존 아카이브 보존 실패 테스트 작성·실행.
- [x] 버전0.1.1·폴더 구성·상대 링크·tools 기본 app경로 구현.
- [x] Python 전체 검사, PS 구문 및 기본경로 검사; 독립 코드 검토. Windows 경로 별칭 비교 보정의 재검증은 Task 3에서 진행.

### Task 3: Windows 통합·문서·게시

**Files:** .github/workflows/windows-ci.yml, scripts/ci/*, scripts/publish-win.sh, docs/guides/*, 시작안내 원본, docs/LAUNCHER_RELEASE_STATUS.md.
**Interfaces:** Windows MSVC build가 launcher EXE 생성 → package_core.py 입력 → 실제 extracted ZIP native launcher smoke 및 기존 운영 smoke.
- [x] native EXE CI/추출ZIP launch·재실행·실패 검사 및 diagnostics 추가.
- [x] 간결한 시작 안내와 새 tools 명령/폴더 설명·업데이트 가이드 작성.
- [ ] 로컬 관련 검사 및 전체 Windows CI 결과 확인, 실제 ZIP inventory/hash/링크 확인.
- [ ] v0.1.1 릴리즈에 기능 변경·검증·미확인 항목과 ZIP 하나 게시하고 실제 다운로드 해시 확인.

## 결과 기록

각 task 결과는 `docs/LAUNCHER_RELEASE_STATUS.md`에 변경·실제 검증·다음 작업·진행률·실환경 미확인을 기록한다. 모든 게시 단계가 끝나기 전 완료를 주장하지 않는다.
