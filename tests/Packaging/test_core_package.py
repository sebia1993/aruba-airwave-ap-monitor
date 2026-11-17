import hashlib
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import subprocess
import sys
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
        self.launcher = self.root / "compiled launcher.exe"
        self.launcher.write_bytes(b"synthetic native launcher fixture")
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
        (self.repository / "docs/PERFORMANCE_MEASUREMENTS.md").write_text("Synthetic measurement record", encoding="utf-8")
        (self.repository / "DEVELOPMENT_LOG.md").write_text("Synthetic log", encoding="utf-8")
        self.measurements = ("2026-11-16-ap-2000.json", "2026-11-16-ap-5000.json",
                             "2026-11-16-compact-cycles-2000.json", "2026-11-16-compact-cycles-5000.json",
                             "2026-11-16-compact-steady-up-2000.json", "2026-11-16-compact-steady-up-5000.json",
                             "2026-11-16-enrichment-storage.json")
        for name in self.measurements:
            measurement = self.repository / "docs/measurements" / name
            measurement.parent.mkdir(parents=True, exist_ok=True)
            measurement.write_text("{}", encoding="utf-8")
        self.corrections = self.repository / "docs/superpowers/plans/2026-11-16-compact-history-correction.md"
        self.corrections.parent.mkdir(parents=True, exist_ok=True)
        self.corrections.write_text("Synthetic implementation correction plan", encoding="utf-8")
        (self.repository / "docs/guides/GETTING_STARTED.txt").write_text("Synthetic getting started", encoding="utf-8")
        (self.repository / "docs/LAUNCHER_RELEASE_STATUS.md").write_text("Synthetic launcher status", encoding="utf-8")

    def build(self):
        return PACKAGE.create_package(self.publish, self.output, self.repository, "a" * 40, False,
                                      stage="core-monitoring", launcher=self.launcher)

    def test_core_rejects_missing_desktop_launcher_without_touching_existing_archives(self):
        self.output.mkdir()
        previous = self.output / "previous.zip"
        previous.write_bytes(b"preserve tested release")
        with self.assertRaises(ValueError):
            PACKAGE.create_package(self.publish, self.output, self.repository, "a" * 40, False,
                                   stage="core-monitoring")
        self.assertEqual(b"preserve tested release", previous.read_bytes())
        self.assertEqual([previous], list(self.output.iterdir()))

    def test_core_bundle_includes_launcher_dependencies_and_truthful_scope(self):
        archive = self.build()
        with zipfile.ZipFile(archive) as bundle:
            names = set(bundle.namelist())
            self.assertEqual({"AirWaveAPMonitor.exe", "시작하기.txt"}, {name for name in names if "/" not in name})
            self.assertEqual({"app", "tools", "docs", "metadata"}, {name.split("/", 1)[0] for name in names if "/" in name})
            self.assertEqual(self.launcher.read_bytes(), bundle.read("AirWaveAPMonitor.exe"))
            self.assertEqual("Synthetic getting started", bundle.read("시작하기.txt").decode("utf-8"))
            self.assertTrue({"tools/" + name for name in self.scripts}.issubset(names))
            self.assertTrue({"docs/guides/" + name for name in self.guides}.issubset(names))
            for record in ("DEVELOPMENT_STATUS.md", "DEVELOPMENT_LOG.md", "ACCEPTANCE_EVIDENCE.md",
                           "PERFORMANCE_MEASUREMENTS.md", "IMPLEMENTATION_CORRECTIONS.md", "LAUNCHER_RELEASE_STATUS.md"):
                self.assertIn("docs/development/" + record, names)
            self.assertTrue({"docs/measurements/" + name for name in self.measurements}.issubset(names))
            self.assertNotIn("2026-11-16-compact-history-correction.md", names)
            for name in self.measurements:
                self.assertEqual(b"{}", bundle.read("docs/measurements/" + name))
            self.assertIn("app/e_sqlite3.dll", names)
            self.assertIn("app/wwwroot/ui-state.js", names)
            version = json.loads(bundle.read("metadata/VERSION.json"))
            self.assertEqual("core-monitoring", version["stage"])
            self.assertEqual("0.1.2", version["version"])
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
            entries = bundle.read("metadata/SHA256SUMS.txt").decode().splitlines()
            for line in entries:
                digest, name = line.split("  ", 1)
                self.assertEqual(digest, hashlib.sha256(bundle.read(name)).hexdigest())
            self.assertEqual(names - {"metadata/SHA256SUMS.txt"}, {line.split("  ", 1)[1] for line in entries})

    def test_core_rejects_absent_empty_directory_or_link_launcher_before_packaging(self):
        directory = self.root / "launcher directory"
        directory.mkdir()
        empty = self.root / "empty.exe"
        empty.touch()
        candidates = [self.root / "missing.exe", directory, empty]
        if os.name != "nt":
            link = self.root / "linked.exe"
            link.symlink_to(self.launcher)
            candidates.append(link)
        for candidate in candidates:
            with self.subTest(path=str(candidate)), self.assertRaises(ValueError):
                PACKAGE.create_package(self.publish, self.output, self.repository, "a" * 40, False,
                                       stage="core-monitoring", launcher=candidate)
        self.assertFalse(self.output.exists())

    def test_missing_native_library_module_or_script_keeps_previous_archive(self):
        self.output.mkdir()
        previous = self.output / "previous.zip"
        previous.write_bytes(b"preserve previously tested bundle")
        for file in (self.publish / "e_sqlite3.dll", self.publish / "wwwroot/ui-state.js", self.publish / "wwwroot/operations.js", self.publish / "wwwroot/enrichment.js",
                     self.repository / "scripts/windows/Monitor-Common.ps1",
                     self.repository / "scripts/windows/Monitor-ServiceRecovery.ps1",
                     self.repository / "docs/guides/UPDATE_AND_RECOVERY.md",
                     self.repository / "docs/guides/GETTING_STARTED.txt",
                     self.repository / "docs/LAUNCHER_RELEASE_STATUS.md"):
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
        original_bytes = guide.read_bytes()
        with zipfile.ZipFile(self.build()) as bundle:
            # Text files use the publishing host's native newline. Link rewriting
            # must preserve content on both Windows and Unix, independently of EOL.
            def markdown(name):
                return bundle.read(name).decode("utf-8").replace("\r\n", "\n")

            self.assertEqual("[operations](../guides/OPERATIONS.md)\n", markdown("docs/development/DEVELOPMENT_STATUS.md"))
            self.assertEqual("[status](../development/DEVELOPMENT_STATUS.md#task-8) [log](../development/DEVELOPMENT_LOG.md)\n", markdown("docs/guides/README_WINDOWS.md"))
            self.assertIn("Synthetic verification record", markdown("docs/development/DEVELOPMENT_LOG.md"))
            self.assertEqual("[onsite](../guides/ONSITE_VALIDATION.md) [log](DEVELOPMENT_LOG.md)\n", markdown("docs/development/ACCEPTANCE_EVIDENCE.md"))
        self.assertEqual(original, guide.read_text(encoding="utf-8"))
        self.assertEqual(original_bytes, guide.read_bytes())

    def test_core_requires_final_measurements_and_corrections_without_replacing_previous_archives(self):
        required = [self.repository / "docs/measurements" / name for name in self.measurements[2:]]
        required.append(self.corrections)
        for index, file in enumerate(required):
            with self.subTest(path=str(file)):
                self.output = self.root / f"missing-evidence-{index}"
                self.output.mkdir()
                previous = self.output / "previous.zip"
                previous.write_bytes(b"preserve previously tested bundle")
                data = file.read_bytes()
                file.unlink()
                try:
                    with self.assertRaises(ValueError):
                        self.build()
                    self.assertEqual(b"preserve previously tested bundle", previous.read_bytes())
                    self.assertEqual([previous], list(self.output.glob("*.zip")))
                finally:
                    file.write_bytes(data)

    def test_compact_evidence_links_follow_the_new_folders_and_sources_are_unchanged(self):
        performance = self.repository / "docs/PERFORMANCE_MEASUREMENTS.md"
        guide = self.repository / "docs/guides/README_WINDOWS.md"
        correction_name = self.corrections.name
        performance.write_text(
            "".join(f"[measurement](measurements/{name})\n" for name in self.measurements) +
            f"[corrections](superpowers/plans/{correction_name}#storage)\n", encoding="utf-8")
        self.corrections.write_text(
            "[performance](../../PERFORMANCE_MEASUREMENTS.md#capacity)\n" +
            "".join(f"[measurement](../../measurements/{name})\n" for name in self.measurements) +
            "[external](https://example.invalid/evidence.json) [not bundled](unbundled.md)\n", encoding="utf-8")
        guide.write_text(f"[corrections](../superpowers/plans/{correction_name}#storage)\n", encoding="utf-8")
        originals = {file: file.read_bytes() for file in (performance, self.corrections, guide)}
        with zipfile.ZipFile(self.build()) as bundle:
            def markdown(name):
                return bundle.read(name).decode("utf-8").replace("\r\n", "\n")

            measurements = "".join(f"[measurement](../measurements/{name})\n" for name in self.measurements)
            self.assertEqual(measurements + "[corrections](IMPLEMENTATION_CORRECTIONS.md#storage)\n",
                             markdown("docs/development/PERFORMANCE_MEASUREMENTS.md"))
            self.assertEqual("[performance](PERFORMANCE_MEASUREMENTS.md#capacity)\n" + measurements +
                             "[external](https://example.invalid/evidence.json) [not bundled](unbundled.md)\n",
                             markdown("docs/development/IMPLEMENTATION_CORRECTIONS.md"))
            self.assertEqual("[corrections](../development/IMPLEMENTATION_CORRECTIONS.md#storage)\n", markdown("docs/guides/README_WINDOWS.md"))
        for file, original in originals.items():
            self.assertEqual(original, file.read_bytes())

    def test_core_packaging_preserves_existing_private_data_exclusion(self):
        (self.publish / "monitor.db-wal").write_bytes(b"private runtime data")
        with self.assertRaises(ValueError):
            self.build()
        self.assertFalse(self.output.exists())

    def test_core_cli_requires_an_explicit_launcher_argument(self):
        result = subprocess.run([sys.executable, str(ROOT / "scripts/package_core.py"),
                                 str(self.publish), str(self.output)], capture_output=True, text=True)
        self.assertEqual(2, result.returncode, result.stdout + result.stderr)
        self.assertIn("--launcher", result.stderr)
        self.assertFalse(self.output.exists())


if __name__ == "__main__":
    unittest.main()
