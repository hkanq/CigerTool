from __future__ import annotations

import unittest
from pathlib import Path


class ReleaseWorkflowTests(unittest.TestCase):
    def test_release_workflow_uses_persistent_local_runner_repo(self) -> None:
        project_root = Path(__file__).resolve().parents[1]
        workflow = (project_root / ".github" / "workflows" / "build-release.yml").read_text(encoding="utf-8")

        self.assertIn("push:", workflow)
        self.assertIn("workflow_dispatch:", workflow)
        self.assertIn("inputs\\workspace\\install.wim", workflow)
        self.assertIn("self-hosted", workflow)
        self.assertIn("SELF_HOSTED_REPO_ROOT", workflow)
        self.assertIn("Prepare local release repository", workflow)
        self.assertIn("Validate local Python", workflow)
        self.assertIn("Validate runner elevation", workflow)
        self.assertIn("git clone", workflow)
        self.assertIn('git config --global --add safe.directory "%REPO_ROOT%"', workflow)
        self.assertIn("GITHUB_TOKEN: ${{ github.token }}", workflow)
        self.assertIn("shell: cmd", workflow)
        self.assertIn("powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File", workflow)
        self.assertIn("inputs.build_mode == 'release'", workflow)

        self.assertNotIn("workspace_wim_url", workflow)
        self.assertNotIn("WORKSPACE_WIM_URL", workflow)
        self.assertNotIn("Invoke-WebRequest", workflow)
        self.assertNotIn("clean: false", workflow)


if __name__ == "__main__":
    unittest.main()
