import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("package_initial", ROOT / "scripts/package_initial.py")
PACKAGE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PACKAGE)


class InitialPackageTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.publish = self.root / "게시 폴더"
        self.output = self.root / "output"
        self.repository = self.root / "repo"
        for relative in ("AirWaveApMonitor.App.exe", "AirWaveApMonitor.App.deps.json",
                         "AirWaveApMonitor.App.runtimeconfig.json", "coreclr.dll", "hostfxr.dll",
                         "hostpolicy.dll", "wwwroot/index.html", "wwwroot/app.js", "wwwroot/app.css"):
            target = self.publish / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text("synthetic packaging fixture", encoding="utf-8")
        for relative in ("scripts/windows/Start-Monitor.ps1", "docs/guides/INITIAL_DIAGNOSTICS.md",
                         "docs/guides/ONSITE_INITIAL_CHECKLIST.md"):
            target = self.repository / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text("synthetic guide", encoding="utf-8")

    def build(self):
        return PACKAGE.create_package(self.publish, self.output, self.repository, "a" * 40, False)

    def test_bundle_has_runtime_local_assets_and_verifiable_hashes(self):
        archive = self.build()
        self.assertIsInstance(archive, Path)
        with zipfile.ZipFile(archive) as bundle:
            names = set(bundle.namelist())
            self.assertIn("app/AirWaveApMonitor.App.exe", names)
            self.assertIn("app/coreclr.dll", names)
            self.assertIn("app/wwwroot/app.js", names)
            self.assertIn("Start-Monitor.ps1", names)
            self.assertIn("README_WINDOWS.md", names)
            self.assertIn("ONSITE_INITIAL_CHECKLIST.md", names)
            manifest = json.loads(bundle.read("VERSION.json"))
            self.assertEqual("a" * 40, manifest["source_commit"])
            self.assertFalse(manifest["working_tree_dirty"])
            self.assertEqual("initial-diagnostics", manifest["stage"])
            self.assertFalse(manifest["onsite_verified"])
            entries = bundle.read("SHA256SUMS.txt").decode().splitlines()
            for entry in entries:
                digest, name = entry.split("  ", 1)
                self.assertEqual(digest, hashlib.sha256(bundle.read(name)).hexdigest(), name)
            self.assertEqual(names - {"SHA256SUMS.txt"}, {line.split("  ", 1)[1] for line in entries})
            self.assertFalse(any(name.endswith(".zip") for name in names))
        self.assertEqual(hashlib.sha256(archive.read_bytes()).hexdigest(),
                         archive.with_suffix(".zip.sha256").read_text().split()[0])

    def test_private_runtime_files_are_rejected_not_silently_packaged(self):
        for name in ("monitor.db", "monitor.db-wal", "credentials.dpapi", "runtime-mode.json", "run.log",
                     "appsettings.Local.json", "profile.json", "profile.json.tmp", "runtime-mode.json.tmp",
                     "ABC123.dpapi.01234567-89ab-cdef-0123-456789abcdef.tmp"):
            with self.subTest(name=name):
                private = self.publish / name
                private.write_text("must never leave the machine")
                with self.assertRaises(ValueError):
                    self.build()
                self.assertEqual([], list(self.output.glob("*.zip")))
                private.unlink()

    @unittest.skipIf(os.name == "nt", "Unix symlink fixture; Windows runner tests the actual package")
    def test_symlink_cannot_import_files_from_outside_publish_directory(self):
        secret = self.root / "private.txt"
        secret.write_text("private")
        (self.publish / "wwwroot/reference.txt").symlink_to(secret)
        with self.assertRaises(ValueError):
            self.build()
        self.assertEqual([], list(self.output.glob("*.zip")))

    def test_missing_runtime_fails_without_destroying_previous_package(self):
        self.output.mkdir()
        previous = self.output / "previous.zip"
        previous.write_bytes(b"known previous package")
        (self.publish / "coreclr.dll").unlink()
        with self.assertRaises(ValueError):
            self.build()
        self.assertEqual(b"known previous package", previous.read_bytes())

    @unittest.skipIf(os.name == "nt", "Mac publish shell behavior; Windows CI publishes with dotnet directly")
    def test_failed_publish_returns_failure_and_keeps_existing_artifacts(self):
        scripts = self.root / "isolated repo" / "scripts"
        scripts.mkdir(parents=True)
        shutil.copyfile(ROOT / "scripts/publish-win.sh", scripts / "publish-win.sh")
        fake_bin = self.root / "tools"
        fake_bin.mkdir()
        dotnet = fake_bin / "dotnet"
        dotnet.write_text("#!/usr/bin/env bash\nexit 42\n")
        dotnet.chmod(0o755)
        artifacts = scripts.parent / "artifacts/win-x64"
        artifacts.mkdir(parents=True)
        previous = artifacts / "previous.zip"
        previous.write_bytes(b"preserve previous validated package")
        environment = dict(os.environ, PATH=str(fake_bin) + os.pathsep + os.environ["PATH"])
        result = subprocess.run(["bash", str(scripts / "publish-win.sh")], env=environment,
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        self.assertEqual(42, result.returncode, result.stdout + result.stderr)
        self.assertEqual(b"preserve previous validated package", previous.read_bytes())
        self.assertEqual([previous], list(artifacts.iterdir()))


if __name__ == "__main__":
    unittest.main()
