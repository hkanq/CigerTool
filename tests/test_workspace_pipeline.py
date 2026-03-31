from __future__ import annotations

import unittest
from pathlib import Path


class WorkspacePipelineTests(unittest.TestCase):
    def test_workspace_and_payload_layers_exist(self) -> None:
        project_root = Path(__file__).resolve().parents[1]

        self.assertTrue((project_root / "boot" / "README.md").exists())
        self.assertTrue((project_root / "workspace" / "README.md").exists())
        self.assertTrue((project_root / "workspace" / "payload" / "README.md").exists())
        self.assertTrue((project_root / "workspace" / "payload" / "Desktop").exists())
        self.assertTrue((project_root / "workspace" / "payload" / "ProgramFiles").exists())
        self.assertTrue((project_root / "workspace" / "payload" / "Tools").exists())
        self.assertTrue((project_root / "workspace" / "payload" / "Users").exists())
        self.assertTrue((project_root / "workspace" / "startup" / "Start-CigerToolWorkspace.ps1").exists())
        self.assertTrue((project_root / "workspace" / "startup" / "CigerToolWorkspace.Runtime.ps1").exists())
        self.assertTrue((project_root / "workspace" / "unattend" / "CigerToolWorkspace.Unattend.xml").exists())

    def test_unattend_contains_required_locale_and_oobe_suppression(self) -> None:
        project_root = Path(__file__).resolve().parents[1]
        unattend = (project_root / "workspace" / "unattend" / "CigerToolWorkspace.Unattend.xml").read_text(encoding="utf-8")

        self.assertIn("tr-TR", unattend)
        self.assertIn("<ComputerName>CigerTool</ComputerName>", unattend)
        self.assertIn("<SkipMachineOOBE>true</SkipMachineOOBE>", unattend)
        self.assertIn("<SkipUserOOBE>true</SkipUserOOBE>", unattend)
        self.assertIn("<Username>CigerTool</Username>", unattend)

    def test_prepare_workspace_script_uses_real_prepare_flow_primitives(self) -> None:
        project_root = Path(__file__).resolve().parents[1]
        script = (project_root / "build" / "internal" / "prepare_workspace_runtime.ps1").read_text(encoding="utf-8")

        self.assertIn("WorkspaceWimPath", script)
        self.assertIn("inputs\\workspace\\install.wim", script)
        self.assertIn("/Apply-Image", script)
        self.assertIn("bcdboot", script)
        self.assertIn("CigerToolWorkspace.vhdx", script)
        self.assertIn("Windows\\Panther\\Unattend.xml", script)
        self.assertIn("Ensure-VhdMounted", script)
        self.assertIn("Assert-SufficientFreeSpace", script)
        self.assertIn("AutoAdminLogon", script)
        self.assertIn("ForceAutoLogon", script)
        self.assertIn("AutoLogonCount", script)
        self.assertIn("PortableOperatingSystem", script)
        self.assertIn("CigerTool.workspace.json", script)
        self.assertIn("native-boot-vhdx", script)
        self.assertIn('"ProgramFiles"', script)
        self.assertIn('"Tools"', script)
        self.assertIn("workspace_stage_root", script)
        self.assertNotIn("Mount-DiskImage", script)
        self.assertNotIn("Resolve-WindowsInstallImage", script)

    def test_workspace_startup_exports_runtime_contract(self) -> None:
        project_root = Path(__file__).resolve().parents[1]
        startup = (project_root / "workspace" / "startup" / "Start-CigerToolWorkspace.ps1").read_text(encoding="utf-8")

        self.assertIn("CIGERTOOL_RUNTIME", startup)
        self.assertIn("CIGERTOOL_RUNTIME_ROOT", startup)
        self.assertIn("CIGERTOOL_TOOLS_ROOT", startup)
        self.assertIn("CIGERTOOL_ISOS_ROOT", startup)
        self.assertIn("workspace-status.json", startup)
        self.assertIn("workspace-startup.log", startup)


if __name__ == "__main__":
    unittest.main()
