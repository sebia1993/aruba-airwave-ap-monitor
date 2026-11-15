import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("package_core_builder", ROOT / "scripts/package_initial.py")
PACKAGE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PACKAGE)


class CorePackageTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.publish = self.root / "게시 폴더"
        self.repository = self.root / "repo"
        self.output = self.root / "보관 ZIP"
        for relative in ("AirWaveApMonitor.App.exe", "AirWaveApMonitor.App.deps.json",
                         "AirWaveApMonitor.App.runtimeconfig.json", "coreclr.dll", "hostfxr.dll",
                         "hostpolicy.dll", "e_sqlite3.dll", "wwwroot/index.html", "wwwroot/app.js",
                         "wwwroot/app.css", "wwwroot/ui-state.js", "wwwroot/operations.js"):
            file = self.publish / relative
            file.parent.mkdir(parents=True, exist_ok=True)
            file.write_text("synthetic publish fixture", encoding="utf-8")
        self.scripts = ("Start-Monitor.ps1", "Monitor-Common.ps1", "Monitor-ServiceRecovery.ps1", "Install-Service.ps1",
                        "Remove-Service.ps1", "Install-ScheduledTask.ps1",
                        "Remove-ScheduledTask.ps1", "Backup-Restore.ps1")
        self.guides = ("README_WINDOWS.md", "WINDOWS_RUN.md", "UPDATE_AND_RECOVERY.md", "ONSITE_VALIDATION.md", "OPERATIONS.md")
        for relative in [*("scripts/windows/" + name for name in self.scripts),
                         *("docs/guides/" + name for name in self.guides)]:
            file = self.repository / relative
            file.parent.mkdir(parents=True, exist_ok=True)
            file.write_text("synthetic guide or script", encoding="utf-8")

    def build(self):
        return PACKAGE.create_package(self.publish, self.output, self.repository, "a" * 40, False,
                                      stage="core-monitoring")

    def test_core_bundle_includes_launcher_dependencies_and_truthful_scope(self):
        archive = self.build()
        with zipfile.ZipFile(archive) as bundle:
            names = set(bundle.namelist())
            self.assertTrue(set(self.scripts + self.guides).issubset(names))
            self.assertIn("app/e_sqlite3.dll", names)
            self.assertIn("app/wwwroot/ui-state.js", names)
            version = json.loads(bundle.read("VERSION.json"))
            self.assertEqual("core-monitoring", version["stage"])
            self.assertEqual("0.1.0-core", version["version"])
            self.assertFalse(version["onsite_verified"])
            self.assertFalse(version["working_tree_dirty"])
            self.assertEqual("a" * 40, version["source_commit"])
            self.assertIn("운영 이력", version["scope"])
            self.assertNotIn("운영 이력 편집", version["remaining_scope"])
            self.assertIn("조건부 연결·LLDP·PoE", version["remaining_scope"])
            self.assertIn("app/wwwroot/operations.js", names)
            entries = bundle.read("SHA256SUMS.txt").decode().splitlines()
            for line in entries:
                digest, name = line.split("  ", 1)
                self.assertEqual(digest, hashlib.sha256(bundle.read(name)).hexdigest())
            self.assertEqual(names - {"SHA256SUMS.txt"}, {line.split("  ", 1)[1] for line in entries})

    def test_missing_native_library_module_or_script_keeps_previous_archive(self):
        self.output.mkdir()
        previous = self.output / "previous.zip"
        previous.write_bytes(b"preserve previously tested bundle")
        for file in (self.publish / "e_sqlite3.dll", self.publish / "wwwroot/ui-state.js", self.publish / "wwwroot/operations.js",
                     self.repository / "scripts/windows/Monitor-Common.ps1",
                     self.repository / "scripts/windows/Monitor-ServiceRecovery.ps1",
                     self.repository / "docs/guides/UPDATE_AND_RECOVERY.md"):
            with self.subTest(path=str(file)):
                data = file.read_bytes()
                file.unlink()
                try:
                    with self.assertRaises(ValueError):
                        self.build()
                    self.assertEqual(b"preserve previously tested bundle", previous.read_bytes())
                    self.assertEqual([previous], list(self.output.glob("*.zip")))
                finally:
                    file.write_bytes(data)

    def test_core_packaging_preserves_existing_private_data_exclusion(self):
        (self.publish / "monitor.db-wal").write_bytes(b"private runtime data")
        with self.assertRaises(ValueError):
            self.build()
        self.assertFalse(self.output.exists())


if __name__ == "__main__":
    unittest.main()
