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
                         "wwwroot/app.css", "wwwroot/ui-state.js", "wwwroot/operations.js", "wwwroot/enrichment.js"):
            file = self.publish / relative
            file.parent.mkdir(parents=True, exist_ok=True)
            file.write_text("synthetic publish fixture", encoding="utf-8")
        self.scripts = ("Start-Monitor.ps1", "Monitor-Common.ps1", "Monitor-ServiceRecovery.ps1", "Install-Service.ps1",
                        "Remove-Service.ps1", "Install-ScheduledTask.ps1",
                        "Remove-ScheduledTask.ps1", "Backup-Restore.ps1")
        self.guides = ("README_WINDOWS.md", "WINDOWS_RUN.md", "UPDATE_AND_RECOVERY.md", "ONSITE_VALIDATION.md", "OPERATIONS.md",
                       "ENRICHMENT.md", "DATA_DICTIONARY.md", "CONFIGURATION_EXAMPLE.json", "MAC_DEVELOPMENT.md")
        for relative in [*("scripts/windows/" + name for name in self.scripts),
                         *("docs/guides/" + name for name in self.guides)]:
            file = self.repository / relative
            file.parent.mkdir(parents=True, exist_ok=True)
            file.write_text("synthetic guide or script", encoding="utf-8")

        (self.repository / "docs/DEVELOPMENT_STATUS.md").write_text("Synthetic status", encoding="utf-8")
        (self.repository / "docs/ACCEPTANCE_EVIDENCE.md").write_text("Synthetic acceptance evidence", encoding="utf-8")
        (self.repository / "DEVELOPMENT_LOG.md").write_text("Synthetic log", encoding="utf-8")

    def build(self):
        return PACKAGE.create_package(self.publish, self.output, self.repository, "a" * 40, False,
                                      stage="core-monitoring")

    def test_core_bundle_includes_launcher_dependencies_and_truthful_scope(self):
        archive = self.build()
        with zipfile.ZipFile(archive) as bundle:
            names = set(bundle.namelist())
            self.assertTrue(set(self.scripts + self.guides).issubset(names))
            self.assertIn("DEVELOPMENT_STATUS.md", names)
            self.assertIn("DEVELOPMENT_LOG.md", names)
            self.assertIn("ACCEPTANCE_EVIDENCE.md", names)
            self.assertIn("app/e_sqlite3.dll", names)
            self.assertIn("app/wwwroot/ui-state.js", names)
            version = json.loads(bundle.read("VERSION.json"))
            self.assertEqual("core-monitoring", version["stage"])
            self.assertEqual("0.1.0", version["version"])
            self.assertFalse(version["onsite_verified"])
            self.assertFalse(version["working_tree_dirty"])
            self.assertEqual("a" * 40, version["source_commit"])
            self.assertIn("운영 이력", version["scope"])
            self.assertNotIn("운영 이력 편집", version["remaining_scope"])
            self.assertIn("조건부 연결/전력 구조", version["scope"])
            self.assertIn("Live 미검증·비활성", version["scope"])
            self.assertEqual([], version["remaining_scope"])
            self.assertIn("검증 완료 표시가 아닙니다", version["verification"])
            self.assertIn("app/wwwroot/enrichment.js", names)
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
        for file in (self.publish / "e_sqlite3.dll", self.publish / "wwwroot/ui-state.js", self.publish / "wwwroot/operations.js", self.publish / "wwwroot/enrichment.js",
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

    def test_packaged_guides_link_to_the_included_verification_records_without_changing_sources(self):
        status = self.repository / "docs/DEVELOPMENT_STATUS.md"
        status.write_text("[operations](guides/OPERATIONS.md)\n", encoding="utf-8")
        (self.repository / "DEVELOPMENT_LOG.md").write_text("Synthetic verification record\n", encoding="utf-8")
        (self.repository / "docs/ACCEPTANCE_EVIDENCE.md").write_text("[onsite](guides/ONSITE_VALIDATION.md) [log](../DEVELOPMENT_LOG.md)\n", encoding="utf-8")
        guide = self.repository / "docs/guides/README_WINDOWS.md"
        original = "[status](../DEVELOPMENT_STATUS.md#task-8) [log](../../DEVELOPMENT_LOG.md)\n"
        guide.write_text(original, encoding="utf-8")
        with zipfile.ZipFile(self.build()) as bundle:
            self.assertEqual("[operations](OPERATIONS.md)\n", bundle.read("DEVELOPMENT_STATUS.md").decode())
            self.assertEqual("[status](DEVELOPMENT_STATUS.md#task-8) [log](DEVELOPMENT_LOG.md)\n", bundle.read("README_WINDOWS.md").decode())
            self.assertIn("Synthetic verification record", bundle.read("DEVELOPMENT_LOG.md").decode())
            self.assertEqual("[onsite](ONSITE_VALIDATION.md) [log](DEVELOPMENT_LOG.md)\n", bundle.read("ACCEPTANCE_EVIDENCE.md").decode())
        self.assertEqual(original, guide.read_text(encoding="utf-8"))

    def test_core_packaging_preserves_existing_private_data_exclusion(self):
        (self.publish / "monitor.db-wal").write_bytes(b"private runtime data")
        with self.assertRaises(ValueError):
            self.build()
        self.assertFalse(self.output.exists())


if __name__ == "__main__":
    unittest.main()
