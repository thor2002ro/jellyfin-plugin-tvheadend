import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


SCRIPT_PATH = Path(__file__).parents[1] / "publish_manifest.py"


def git(*args, cwd=None, git_dir=None):
    command = ["git"]
    if git_dir is not None:
        command.extend(["--git-dir", str(git_dir)])
    command.extend(args)
    return subprocess.run(command, cwd=cwd, check=True, capture_output=True, text=True).stdout.strip()


class ManifestPublisherTests(unittest.TestCase):
    def test_repeated_updates_amend_the_only_manifest_commit(self):
        self.assertTrue(SCRIPT_PATH.exists(), "The manifest publisher script is missing.")

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            remote = root / "remote.git"
            checkout = root / "checkout"
            manifest = root / "generated.json"
            git("init", "--bare", str(remote))
            git("init", "--initial-branch=master", str(checkout))
            git("config", "user.name", "Test User", cwd=checkout)
            git("config", "user.email", "test@example.com", cwd=checkout)
            (checkout / "source.txt").write_text("source\n", encoding="utf-8")
            git("add", "source.txt", cwd=checkout)
            git("commit", "-m", "Source commit", cwd=checkout)
            git("remote", "add", "origin", str(remote), cwd=checkout)
            git("push", "origin", "master", cwd=checkout)

            manifest.write_text('[{"version": 1}]\n', encoding="utf-8")
            self.run_publisher(checkout, manifest)
            first_tip = git("rev-parse", "refs/heads/manifest", git_dir=remote)
            first_author_date = git("show", "-s", "--format=%aI", first_tip, git_dir=remote)
            first_committer_date = git("show", "-s", "--format=%cI", first_tip, git_dir=remote)

            manifest.write_text('[{"version": 2}]\n', encoding="utf-8")
            self.run_publisher(checkout, manifest)
            second_tip = git("rev-parse", "refs/heads/manifest", git_dir=remote)

            self.assertNotEqual(first_tip, second_tip)
            self.assertEqual("1", git("rev-list", "--count", second_tip, git_dir=remote))
            self.assertEqual(
                "Local: Update plugin repository manifest",
                git("show", "-s", "--format=%s", second_tip, git_dir=remote),
            )
            self.assertEqual(first_author_date, git("show", "-s", "--format=%aI", second_tip, git_dir=remote))
            self.assertEqual(
                first_committer_date,
                git("show", "-s", "--format=%cI", second_tip, git_dir=remote),
            )
            self.assertEqual(
                '[{"version": 2}]',
                git("show", f"{second_tip}:manifest.json", git_dir=remote),
            )

            self.run_publisher(checkout, manifest)
            self.assertEqual(second_tip, git("rev-parse", "refs/heads/manifest", git_dir=remote))

    def run_publisher(self, checkout, manifest):
        subprocess.run(
            [sys.executable, str(SCRIPT_PATH), "--manifest", str(manifest)],
            cwd=checkout,
            check=True,
            env={**os.environ, "GIT_TERMINAL_PROMPT": "0"},
            capture_output=True,
            text=True,
        )


if __name__ == "__main__":
    unittest.main()
