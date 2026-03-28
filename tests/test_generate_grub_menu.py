from __future__ import annotations

import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


class GenerateGrubMenuTests(unittest.TestCase):
    def test_generated_menu_contains_failure_reasons(self) -> None:
        project_root = Path(__file__).resolve().parents[1]
        script = project_root / "build" / "scripts" / "generate_grub_menu.py"

        with tempfile.TemporaryDirectory() as tmp:
            media_root = Path(tmp) / "media"
            (media_root / "isos" / "windows").mkdir(parents=True)
            (media_root / "isos" / "linux").mkdir(parents=True)
            (media_root / "iso-library").mkdir(parents=True)
            (media_root / "isos" / "windows" / "Windows11.iso").write_bytes(b"x")
            (media_root / "isos" / "linux" / "mystery.iso").write_bytes(b"x")
            (media_root / "iso-library" / "random.iso").write_bytes(b"x")
            output = media_root / "EFI" / "CigerTool" / "grub.cfg"

            subprocess.run(
                [sys.executable, str(script), "--media-root", str(media_root), "--output", str(output)],
                check=True,
                cwd=project_root,
            )

            content = output.read_text(encoding="utf-8")
            self.assertIn("missing boot files", content)
            self.assertIn("unsupported kernel", content)
            self.assertIn("incompatible ISO type", content)


if __name__ == "__main__":
    unittest.main()
