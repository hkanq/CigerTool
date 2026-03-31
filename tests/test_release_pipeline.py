from __future__ import annotations

import unittest
from pathlib import Path


class ReleasePipelineTests(unittest.TestCase):
    def test_release_script_exists_and_validates_workspace_release_contract(self) -> None:
        project_root = Path(__file__).resolve().parents[1]
        script = (project_root / "build" / "build_cigertool_release.ps1").read_text(encoding="utf-8")

        self.assertIn("CigerTool-Workspace.iso", script)
        self.assertIn("CigerTool-Workspace-debug.zip", script)
        self.assertIn("IMAPI2FS.MsftFileSystemImage", script)
        self.assertIn("FreeMediaBlocks", script)
        self.assertIn("Get-DirectorySizeBytes", script)
        self.assertIn("bootmgfw.efi", script)
        self.assertIn("CigerToolWorkspace.vhdx", script)
        self.assertIn("Start-CigerToolWorkspace.ps1", script)
        self.assertIn("boot-manifest.json", script)
        self.assertIn("setup.exe", script)
        self.assertIn("boot.wim", script)
        self.assertIn("Get-FileHash", script)
        self.assertIn("Final artifact generation elevasyon gerektirir", script)
        self.assertIn("Assert-LockedWorkspaceDefaults", script)
        self.assertIn("PortableOperatingSystem", script)
        self.assertIn("HideOnlineAccountScreens", script)
        self.assertIn("workspace-startup.log", script)
        self.assertIn("PlanOnly", script)

    def test_release_plan_documents_primary_and_secondary_artifacts(self) -> None:
        project_root = Path(__file__).resolve().parents[1]
        plan = (project_root / "docs" / "RELEASE_PLAN.md").read_text(encoding="utf-8")

        self.assertIn("CigerTool-Workspace.iso", plan)
        self.assertIn("CigerTool-Workspace.iso.sha256", plan)
        self.assertIn("CigerTool-Workspace-debug.zip", plan)
        self.assertIn("ISO/extract mode", plan)
        self.assertIn("isos/windows", plan)
        self.assertIn("Start-CigerToolWorkspace.ps1", plan)

    def test_release_checklist_documents_manual_rc_validation(self) -> None:
        project_root = Path(__file__).resolve().parents[1]
        checklist = (project_root / "docs" / "RELEASE_CHECKLIST.md").read_text(encoding="utf-8")

        self.assertIn("CigerTool-Workspace.iso", checklist)
        self.assertIn("Windows Setup", checklist)
        self.assertIn("OOBE", checklist)
        self.assertIn("workspace-startup.log", checklist)
        self.assertIn("ISO Library", checklist)


if __name__ == "__main__":
    unittest.main()
