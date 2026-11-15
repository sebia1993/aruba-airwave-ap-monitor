"""Build the offline initial diagnostics bundle; no deployment or release publishing."""

import argparse
from datetime import datetime, timezone
import hashlib
import json
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


def create_package(published, destination, repository, commit, dirty):
    published, destination, repository = map(Path, (published, destination, repository))
    validate_publish(published)
    if not re.fullmatch(r"[0-9a-f]{40,64}", commit):
        raise ValueError("유효한 소스 커밋 식별자가 필요합니다.")
    if published.resolve() == destination.resolve() or published.resolve() in destination.resolve().parents:
        raise ValueError("패키지 출력은 게시 원본 폴더 밖이어야 합니다.")
    guides = {
        "scripts/windows/Start-Monitor.ps1": "Start-Monitor.ps1",
        "docs/guides/INITIAL_DIAGNOSTICS.md": "README_WINDOWS.md",
        "docs/guides/ONSITE_INITIAL_CHECKLIST.md": "ONSITE_INITIAL_CHECKLIST.md",
    }
    for source in guides:
        if not (repository / source).is_file() or (repository / source).is_symlink():
            raise ValueError("실행 안내 또는 스크립트가 누락되었습니다: " + source)

    destination.mkdir(parents=True, exist_ok=True)
    now = datetime.now(timezone.utc)
    name = "AirWaveAPMonitor-0.1.0-diagnostics-{}-{}-{}".format(
        commit[:12], now.strftime("%Y%m%dT%H%M%SZ"), uuid.uuid4().hex[:6])
    with tempfile.TemporaryDirectory(prefix=".bundle-", dir=destination) as temporary:
        staging = Path(temporary)
        payload = staging / "payload"
        shutil.copytree(published, payload / "app")
        for source, target in guides.items():
            shutil.copyfile(repository / source, payload / target)
        metadata = {
            "version": "0.1.0-diagnostics", "stage": "initial-diagnostics", "rid": "win-x64",
            "source_commit": commit, "working_tree_dirty": bool(dirty),
            "created_at_utc": now.isoformat(), "onsite_verified": False,
            "scope": "연결 진단·단일 조회. 자동 감시·SQLite 이력·서비스 설치는 아직 미구현.",
            "verification": "이 패키지의 Windows CI 결과와 사내 확인 여부는 별도 작업 기록을 참조하세요.",
        }
        (payload / "VERSION.json").write_text(json.dumps(metadata, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        checksums = [sha256(item) + "  " + item.relative_to(payload).as_posix()
                     for item in sorted(payload.rglob("*")) if item.is_file()]
        (payload / "SHA256SUMS.txt").write_text("\n".join(checksums) + "\n", encoding="utf-8")
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
