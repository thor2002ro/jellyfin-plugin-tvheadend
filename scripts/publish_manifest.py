#!/usr/bin/env python3

import argparse
import os
from pathlib import Path
import shutil
import subprocess


DEFAULT_MESSAGE = "Local: Update plugin repository manifest"


def run_git(arguments, *, capture=False, check=True, environment=None):
    result = subprocess.run(
        ["git", *arguments],
        check=False,
        text=True,
        capture_output=capture,
        env=environment,
    )
    if check and result.returncode != 0:
        if capture and result.stderr:
            print(result.stderr, end="")
        raise subprocess.CalledProcessError(result.returncode, result.args)
    return result


def git_value(*arguments):
    return run_git(arguments, capture=True).stdout.strip()


def copy_manifest(source):
    destination = Path.cwd() / "manifest.json"
    if source.resolve() != destination.resolve():
        shutil.copyfile(source, destination)


def publish_manifest(manifest, remote, branch, message):
    run_git(["config", "user.name", "github-actions[bot]"])
    run_git(["config", "user.email", "41898282+github-actions[bot]@users.noreply.github.com"])

    remote_branch = run_git(
        ["ls-remote", "--exit-code", "--heads", remote, branch],
        capture=True,
        check=False,
    )
    if remote_branch.returncode not in (0, 2):
        raise subprocess.CalledProcessError(remote_branch.returncode, remote_branch.args)

    if remote_branch.returncode == 2:
        run_git(["switch", "--orphan", branch])
        copy_manifest(manifest)
        run_git(["add", "--", "manifest.json"])
        run_git(["commit", "-m", message])
        run_git(["push", remote, f"HEAD:refs/heads/{branch}"])
        return

    run_git(["fetch", "--no-tags", remote, branch])
    old_tip = git_value("rev-parse", "FETCH_HEAD")
    commit_count = git_value("rev-list", "--count", old_tip)
    current_message = git_value("show", "-s", "--format=%s", old_tip)
    if commit_count != "1" or current_message != message:
        raise ValueError("The manifest branch must contain exactly the managed manifest commit")

    run_git(["switch", "--detach", old_tip])
    copy_manifest(manifest)
    run_git(["add", "--", "manifest.json"])
    if run_git(["diff", "--cached", "--quiet"], check=False).returncode == 0:
        print("Manifest is already current.")
        return

    author_date = git_value("show", "-s", "--format=%aI", old_tip)
    committer_date = git_value("show", "-s", "--format=%cI", old_tip)
    environment = {**os.environ, "GIT_COMMITTER_DATE": committer_date}
    run_git(
        ["commit", "--amend", "--no-edit", "--date", author_date],
        environment=environment,
    )
    run_git(
        [
            "push",
            f"--force-with-lease=refs/heads/{branch}:{old_tip}",
            remote,
            f"HEAD:refs/heads/{branch}",
        ]
    )


def main():
    parser = argparse.ArgumentParser(description="Publish one amended manifest commit")
    parser.add_argument("--manifest", required=True, type=Path, help="Generated manifest file")
    parser.add_argument("--remote", default="origin", help="Git remote to update")
    parser.add_argument("--branch", default="manifest", help="Manifest branch name")
    parser.add_argument("--message", default=DEFAULT_MESSAGE, help="Managed commit subject")
    args = parser.parse_args()

    if not args.manifest.is_file():
        parser.error(f"Manifest does not exist: {args.manifest}")
    publish_manifest(args.manifest, args.remote, args.branch, args.message)


if __name__ == "__main__":
    main()
