from __future__ import annotations

import unittest
from pathlib import Path


class ReleasePipelineTests(unittest.TestCase):
    def test_release_script_targets_standard_rufus_iso_contract(self) -> None:
        project_root = Path(__file__).resolve().parents[1]
        script = (project_root / "build" / "build_cigertool_release.ps1").read_text(encoding="utf-8")

        self.assertIn("CigerTool.iso", script)
        self.assertIn("CigerTool-debug.zip", script)
        self.assertIn("create_hybrid_iso.py", script)
        self.assertIn("standard-windows-hybrid-iso", script)
        self.assertIn("sources\\boot.wim", script)
        self.assertIn("sources\\install.wim", script)
        self.assertIn("boot\\etfsboot.com", script)
        self.assertIn("efi\\microsoft\\boot\\efisys.bin", script)
        self.assertIn("rufus_compatible = $true", script)
        self.assertIn("ISO boyutu hedefi asti", script)
        self.assertNotIn("CigerToolWorkspace.vhdx", script)

    def test_standard_media_prepare_script_avoids_vhd_and_services_wims(self) -> None:
        project_root = Path(__file__).resolve().parents[1]
        script = (project_root / "build" / "internal" / "prepare_standard_release_media.ps1").read_text(encoding="utf-8")

        self.assertIn("Export-WindowsImage", script)
        self.assertIn("dism.exe /Apply-Image", script)
        self.assertIn("startnet.cmd", script)
        self.assertIn("Apply-CigerToolImage.cmd", script)
        self.assertIn("create partition efi size=100", script)
        self.assertIn("boot\\etfsboot.com", script)
        self.assertIn("efi\\microsoft\\boot\\efisys.bin", script)
        self.assertIn("AutoAdminLogon", script)
        self.assertIn("Start-CigerToolWorkspace.ps1", script)
        self.assertNotIn(".vhdx", script)

    def test_release_plan_documents_standard_iso_flow(self) -> None:
        project_root = Path(__file__).resolve().parents[1]
        plan = (project_root / "docs" / "RELEASE_PLAN.md").read_text(encoding="utf-8")

        self.assertIn("CigerTool.iso", plan)
        self.assertIn("sources/install.wim", plan)
        self.assertIn("sources/boot.wim", plan)
        self.assertIn("Rufus", plan)
        self.assertIn("BIOS + UEFI", plan)
        self.assertNotIn("CigerToolWorkspace.vhdx", plan)

    def test_release_checklist_tracks_direct_desktop_boot_outcome(self) -> None:
        project_root = Path(__file__).resolve().parents[1]
        checklist = (project_root / "docs" / "RELEASE_CHECKLIST.md").read_text(encoding="utf-8")

        self.assertIn("CigerTool.iso", checklist)
        self.assertIn("media-root/sources/boot.wim", checklist)
        self.assertIn("media-root/sources/install.wim", checklist)
        self.assertIn("Rufus", checklist)
        self.assertIn("Dogrudan masaustu", checklist)
        self.assertNotIn("workspace-startup.log", checklist)


if __name__ == "__main__":
    unittest.main()
