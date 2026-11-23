"""Build the offline initial diagnostics bundle; no deployment or release publishing."""

import argparse
from datetime import datetime, timezone
import hashlib
import json
import posixpath
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import uuid
import zipfile


REQUIRED_FILES = (
    "AirWaveApMonitor.App.exe", "AirWaveApMonitor.App.deps.json",
    "AirWaveApMonitor.App.runtimeconfig.json", "coreclr.dll", "hostfxr.dll",
    "hostpolicy.dll", "wwwroot/index.html", "wwwroot/app.js",
)


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def validate_publish(published):
    if not published.is_dir() or published.is_symlink():
        raise ValueError("게시 폴더가 없거나 심볼릭 링크입니다.")
    for required in REQUIRED_FILES:
        if not (published / required).is_file():
            raise ValueError("Windows 런타임 또는 필수 앱 자원이 누락되었습니다: " + required)
    for item in published.rglob("*"):
        relative = item.relative_to(published)
        name = item.name.lower()
        if item.is_symlink():
            raise ValueError("게시 폴더 밖의 파일을 포함할 수 있는 링크는 패키징하지 않습니다.")
        if any(part.lower() in {".git", ".superpowers", "obj", "bin", "testresults", "node_modules"}
               for part in relative.parts):
            raise ValueError("개발 캐시가 게시 폴더에 있습니다.")
        if (re.search(r"\.(db|sqlite|sqlite3)(-wal|-shm|-journal)?$", name)
                or name.endswith((".dpapi", ".log", ".zip", ".pfx", ".p12", ".pem", ".key"))
                or re.search(r"\.dpapi(?:\.|$)", name)
                or name.startswith(("credentials.", "secrets.", ".env", "appsettings.",
                                    "profile.json", "runtime-mode."))
                or "\n" in str(relative) or "\r" in str(relative)):
            raise ValueError("운영 자료·비밀·로그 또는 잘못된 파일명이 게시 폴더에 있습니다.")


def create_package(published, destination, repository, commit, dirty, stage="initial-diagnostics", launcher=None):
    published, destination, repository = map(Path, (published, destination, repository))
    if stage not in {"initial-diagnostics", "core-monitoring"}:
        raise ValueError("지원하지 않는 패키지 범위입니다.")
    validate_publish(published)
    core = stage == "core-monitoring"
    if core:
        if launcher is None:
            raise ValueError("핵심 감시 패키지에는 컴파일된 Windows 실행기가 필요합니다.")
        launcher = Path(launcher)
        if not launcher.is_file() or launcher.is_symlink() or launcher.stat().st_size == 0:
            raise ValueError("Windows 실행기는 비어 있지 않은 일반 파일이어야 합니다.")
        for required in ("e_sqlite3.dll", "wwwroot/app.css", "wwwroot/ui-state.js", "wwwroot/operations.js", "wwwroot/enrichment.js", "wwwroot/safe-diagnostics.js"):
            if not (published / required).is_file():
                raise ValueError("핵심 감시 패키지의 필수 자원이 누락되었습니다: " + required)
    if not re.fullmatch(r"[0-9a-f]{40,64}", commit):
        raise ValueError("유효한 소스 커밋 식별자가 필요합니다.")
    if published.resolve() == destination.resolve() or published.resolve() in destination.resolve().parents:
        raise ValueError("패키지 출력은 게시 원본 폴더 밖이어야 합니다.")
    guides = {
        "scripts/windows/Start-Monitor.ps1": "Start-Monitor.ps1",
        "docs/guides/INITIAL_DIAGNOSTICS.md": "README_WINDOWS.md",
        "docs/guides/ONSITE_INITIAL_CHECKLIST.md": "ONSITE_INITIAL_CHECKLIST.md",
    }
    if core:
        scripts = ("Start-Monitor.ps1", "Monitor-Common.ps1", "Monitor-ServiceRecovery.ps1", "Install-Service.ps1",
                   "Remove-Service.ps1", "Install-ScheduledTask.ps1",
                   "Remove-ScheduledTask.ps1", "Backup-Restore.ps1")
        documents = ("README_WINDOWS.md", "WINDOWS_RUN.md", "UPDATE_AND_RECOVERY.md", "UPDATE_CHECKLIST.md", "QUICK_START.md", "FIRST_CONNECTION_KO.md", "ONSITE_VALIDATION.md", "TLS_PROXY_TROUBLESHOOTING.md", "OPERATIONS.md",
                     "ENRICHMENT.md", "DATA_DICTIONARY.md", "CONFIGURATION_EXAMPLE.json", "MAC_DEVELOPMENT.md")
        guides = {"scripts/windows/" + name: name for name in scripts}
        guides.update({"docs/guides/" + name: name for name in documents})
        guides.update({"docs/DEVELOPMENT_STATUS.md": "DEVELOPMENT_STATUS.md", "docs/ACCEPTANCE_EVIDENCE.md": "ACCEPTANCE_EVIDENCE.md",
                       "DEVELOPMENT_LOG.md": "DEVELOPMENT_LOG.md", "docs/PERFORMANCE_MEASUREMENTS.md": "PERFORMANCE_MEASUREMENTS.md",
                       "docs/measurements/2026-11-16-ap-2000.json": "2026-11-16-ap-2000.json",
                       "docs/measurements/2026-11-16-ap-5000.json": "2026-11-16-ap-5000.json",
                       "docs/measurements/2026-11-16-compact-cycles-2000.json": "2026-11-16-compact-cycles-2000.json",
                       "docs/measurements/2026-11-16-compact-cycles-5000.json": "2026-11-16-compact-cycles-5000.json",
                       "docs/measurements/2026-11-16-compact-steady-up-2000.json": "2026-11-16-compact-steady-up-2000.json",
                       "docs/measurements/2026-11-16-compact-steady-up-5000.json": "2026-11-16-compact-steady-up-5000.json",
                       "docs/measurements/2026-11-16-enrichment-storage.json": "2026-11-16-enrichment-storage.json",
                       "docs/superpowers/plans/2026-11-16-compact-history-correction.md": "IMPLEMENTATION_CORRECTIONS.md"})
        guides = {source: ("tools/" + target if source.startswith("scripts/windows/") else
                           "docs/guides/" + target if source.startswith("docs/guides/") else
                           "docs/measurements/" + target if source.startswith("docs/measurements/") else
                           "docs/development/" + target)
                  for source, target in guides.items()}
        guides["docs/guides/GETTING_STARTED.txt"] = "시작하기.txt"
        guides["docs/LAUNCHER_RELEASE_STATUS.md"] = "docs/development/LAUNCHER_RELEASE_STATUS.md"
    elif (repository / "scripts/windows/Monitor-Common.ps1").exists():
        # Later launchers share this dependency. Historic diagnostics generation must
        # not create a launcher that refers to a missing helper.
        guides["scripts/windows/Monitor-Common.ps1"] = "Monitor-Common.ps1"
        guides["scripts/windows/Monitor-ServiceRecovery.ps1"] = "Monitor-ServiceRecovery.ps1"
    for source in guides:
        if not (repository / source).is_file() or (repository / source).is_symlink():
            raise ValueError("실행 안내 또는 스크립트가 누락되었습니다: " + source)

    destination.mkdir(parents=True, exist_ok=True)
    now = datetime.now(timezone.utc)
    version = "0.1.3-preview.9" if core else "0.1.0-diagnostics"
    name = "AirWaveAPMonitor-{}-{}-{}-{}".format(
        version, commit[:12], now.strftime("%Y%m%dT%H%M%SZ"), uuid.uuid4().hex[:6])
    with tempfile.TemporaryDirectory(prefix=".bundle-", dir=destination) as temporary:
        staging = Path(temporary)
        payload = staging / "payload"
        shutil.copytree(published, payload / "app")
        if core:
            shutil.copyfile(launcher, payload / "AirWaveAPMonitor.exe")
        for source, target in guides.items():
            (payload / target).parent.mkdir(parents=True, exist_ok=True)
            if source.endswith(".md"):
                # Resolve links between their packaged locations. Only included files
                # are rewritten; repository documents and link anchors remain intact.
                content = (repository / source).read_text(encoding="utf-8")
                for linked_source, linked_target in guides.items():
                    relative = posixpath.relpath(linked_source, posixpath.dirname(source) or ".")
                    packaged_relative = posixpath.relpath(linked_target, posixpath.dirname(target) or ".")
                    pattern = r"(\]\()" + re.escape(relative) + r"(?=[)#])"
                    content = re.sub(pattern, lambda match: match.group(1) + packaged_relative, content)
                (payload / target).write_text(content, encoding="utf-8")
            else:
                shutil.copyfile(repository / source, payload / target)
        metadata = {
            "version": version, "stage": stage, "rid": "win-x64",
            "source_commit": commit, "working_tree_dirty": bool(dirty),
            "created_at_utc": now.isoformat(), "onsite_verified": False,
            "scope": ("AP 자동 감시·장애/복구·불변 회차/CSV·운영 이력·수동 대장/CSV 등록·설치 지점/작업·조건부 연결/전력 구조(Demo 검증, Live 미검증·비활성)·백업/복원·Windows 서비스/예약 작업."
                      if core else "초기 연결 진단 반입 범위. 최신 코드 기능 범위는 개발 기록을 확인하세요."),
            "remaining_scope": ([]
                                if core else ["핵심 감시 반입·전체 승인 범위의 검증은 별도 패키지/기록 참조"]),
            "verification": "포함 기능의 구현 범위이며 검증 완료 표시가 아닙니다. 이 소스의 Windows CI 결과와 사내 확인 여부는 개발 검증 기록을 참조하세요.",
        }
        metadata_directory = payload / "metadata" if core else payload
        metadata_directory.mkdir(parents=True, exist_ok=True)
        (metadata_directory / "VERSION.json").write_text(json.dumps(metadata, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        checksums = [sha256(item) + "  " + item.relative_to(payload).as_posix()
                     for item in sorted(payload.rglob("*")) if item.is_file()]
        (metadata_directory / "SHA256SUMS.txt").write_text("\n".join(checksums) + "\n", encoding="utf-8")
        temporary_zip = staging / "bundle.zip"
        with zipfile.ZipFile(temporary_zip, "x", zipfile.ZIP_DEFLATED, compresslevel=6) as bundle:
            for item in sorted(payload.rglob("*")):
                if item.is_file():
                    bundle.write(item, item.relative_to(payload).as_posix())
        archive = destination / (name + ".zip")
        checksum = staging / "archive.sha256"
        checksum.write_text(sha256(temporary_zip) + "  " + archive.name + "\n", encoding="utf-8")
        payload.rename(destination / name)
        temporary_zip.rename(archive)
        checksum.rename(archive.with_suffix(".zip.sha256"))
    return archive


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("published", type=Path)
    parser.add_argument("destination", type=Path)
    args = parser.parse_args()
    repository = Path(__file__).resolve().parent.parent
    commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=repository, text=True).strip()
    dirty = bool(subprocess.check_output(["git", "status", "--porcelain", "--untracked-files=normal"], cwd=repository, text=True).strip())
    print(create_package(args.published, args.destination, repository, commit, dirty))
