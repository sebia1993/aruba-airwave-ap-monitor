import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
POWERSHELL = (str(Path(os.environ.get("SystemRoot", "C:/Windows")) /
                  "System32/WindowsPowerShell/v1.0/powershell.exe") if os.name == "nt"
              else shutil.which("pwsh"))


@unittest.skipUnless(POWERSHELL, "PowerShell is required for actual Windows tool path resolution")
class WindowsToolPathTests(unittest.TestCase):
    def test_tools_and_legacy_package_defaults_keep_the_backend_and_external_data(self):
        with tempfile.TemporaryDirectory(prefix="AirWave 도구 경로 ") as temporary:
            root = Path(temporary)
            for name, tools_subdirectory in (("새 core", True), ("이전 진단", False), ("tools", False)):
                with self.subTest(package=name):
                    package = root / name
                    tools = package / "tools" if tools_subdirectory else package
                    tools.mkdir(parents=True)
                    app = package / "app"
                    app.mkdir()
                    exe = app / "AirWaveApMonitor.App.exe"
                    exe.write_bytes(b"synthetic path fixture, never executed")
                    metadata = package / "metadata" if tools_subdirectory else package
                    metadata.mkdir(exist_ok=True)
                    (metadata / "VERSION.json").write_text("{}", encoding="utf-8")
                    for helper in ("Monitor-Common.ps1", "Monitor-ServiceRecovery.ps1"):
                        shutil.copyfile(ROOT / "scripts/windows" / helper, tools / helper)
                    probe = root / "probe.ps1"
                    probe.write_text(
                        "param([string]$ToolDirectory,[string]$DataDirectory)\n"
                        "$ErrorActionPreference='Stop'\n"
                        "[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)\n"
                        ". (Join-Path $ToolDirectory 'Monitor-Common.ps1')\n"
                        "$exe=Get-MonitorDefaultExecutable\n"
                        "$launch=Get-MonitorLaunchSpec -ExecutablePath $exe -DataDirectory $DataDirectory "
                        "-Mode demo -Port 18765 -RequireExecutable\n"
                        "$launch | ConvertTo-Json -Compress\n", encoding="utf-8-sig")
                    data = root / "외부 데이터"
                    result = subprocess.run([POWERSHELL, "-NoLogo", "-NoProfile", "-NonInteractive",
                                             "-File", str(probe), "-ToolDirectory", str(tools),
                                             "-DataDirectory", str(data)], cwd=root,
                                            capture_output=True, text=True, encoding="utf-8", timeout=30)
                    self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                    launch = json.loads(result.stdout)
                    self.assertEqual(str(exe), launch["Executable"])
                    self.assertEqual(str(app), launch["WorkingDirectory"])
                    self.assertEqual(str(data), launch["DataDirectory"])
                    self.assertEqual("AirWaveAPMonitor managed registration v1", launch["OwnershipMarker"])


if __name__ == "__main__":
    unittest.main()
