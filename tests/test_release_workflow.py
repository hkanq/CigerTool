from __future__ import annotations

import unittest
from pathlib import Path


class ReleaseWorkflowTests(unittest.TestCase):
    def test_release_workflow_uses_local_workspace_input_contract(self) -> None:
        project_root = Path(__file__).resolve().parents[1]
        workflow = (project_root / ".github" / "workflows" / "build-release.yml").read_text(encoding="utf-8")

        self.assertIn("push:", workflow)
        self.assertIn("workflow_dispatch:", workflow)
        self.assertIn("inputs\\workspace\\install.wim", workflow)
        self.assertIn("clean: false", workflow)
        self.assertIn("self-hosted", workflow)
        self.assertIn("inputs.build_mode == 'release'", workflow)

        self.assertNotIn("workspace_wim_url", workflow)
        self.assertNotIn("WORKSPACE_WIM_URL", workflow)
        self.assertNotIn("Invoke-WebRequest", workflow)


if __name__ == "__main__":
    unittest.main()
