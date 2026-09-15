#!/usr/bin/env python3

import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import re
from urllib.parse import quote
from urllib.request import Request, urlopen
import zipfile


PLUGIN_METADATA_KEYS = ("guid", "name", "description", "overview", "owner", "category")
VERSION_METADATA_KEYS = ("targetAbi",)
PLUGIN_ARCHIVE_PATTERN = re.compile(
    r"^TVHeadEnd_(?P<version>[0-9]+(?:\.[0-9]+){0,3})\.zip$",
    re.IGNORECASE,
)


def parse_metadata(text, required_keys):
    values = {}
    for line in text.splitlines():
        match = re.match(r"^([A-Za-z][A-Za-z0-9]*):\s*(.*?)\s*$", line)
        if not match:
            continue

        key, value = match.groups()
        if key not in required_keys or value in ("", "|-", "|"):
            continue

        if value.startswith('"') and value.endswith('"'):
            value = json.loads(value)
        elif value.startswith("'") and value.endswith("'"):
            value = value[1:-1].replace("''", "'")
        values[key] = value

    missing = [key for key in required_keys if key not in values]
    if missing:
        raise ValueError(f"Missing build metadata: {', '.join(missing)}")
    return values


def normalize_version(value):
    parts = value.split(".")
    if not 1 <= len(parts) <= 4 or any(not part.isdigit() for part in parts):
        raise ValueError(f"Plugin version must have one to four numeric components: {value}")
    return ".".join([*parts, *(["0"] * (4 - len(parts)))])


def version_key(value):
    return tuple(int(part) for part in value.split("."))


def clean_changelog(body, version):
    lines = [
        line.rstrip()
        for line in (body or "").splitlines()
        if not line.strip().startswith("**Full Changelog**")
    ]
    changelog = "\n".join(lines).strip()
    return changelog or f"Release {version}"


def validate_archive(contents, asset_name):
    try:
        with zipfile.ZipFile(io.BytesIO(contents)) as archive:
            files = [entry.filename for entry in archive.infolist() if not entry.is_dir()]
            if files != ["TVHeadEnd.dll"]:
                raise ValueError(f"{asset_name} must contain exactly TVHeadEnd.dll")
            if archive.getinfo("TVHeadEnd.dll").file_size == 0:
                raise ValueError(f"{asset_name} contains an empty TVHeadEnd.dll")
    except zipfile.BadZipFile as error:
        raise ValueError(f"{asset_name} is not a valid ZIP archive") from error


def build_manifest(repository, root_metadata_text, releases, fetch_bytes, fetch_tag_metadata):
    plugin_metadata = parse_metadata(root_metadata_text, PLUGIN_METADATA_KEYS)
    versions_by_number = {}

    for release in releases:
        if release.get("draft"):
            continue

        assets = [
            asset
            for asset in release.get("assets", [])
            if PLUGIN_ARCHIVE_PATTERN.fullmatch(asset.get("name", ""))
        ]
        if not assets:
            continue
        if len(assets) != 1:
            raise ValueError(f"{release['tag_name']} has multiple plugin ZIP assets")

        asset = assets[0]
        tagged_metadata = parse_metadata(fetch_tag_metadata(release["tag_name"]), VERSION_METADATA_KEYS)
        version = normalize_version(PLUGIN_ARCHIVE_PATTERN.fullmatch(asset["name"])["version"])

        contents = fetch_bytes(asset["browser_download_url"])
        validate_archive(contents, asset["name"])
        candidate = {
            "version": version,
            "changelog": clean_changelog(release.get("body"), version),
            "targetAbi": tagged_metadata["targetAbi"],
            "sourceUrl": asset["browser_download_url"],
            "checksum": hashlib.md5(contents).hexdigest(),
            "timestamp": release["published_at"],
        }

        existing = versions_by_number.get(version)
        if existing is None or candidate["timestamp"] > existing["timestamp"]:
            versions_by_number[version] = candidate

    versions = sorted(
        versions_by_number.values(),
        key=lambda item: version_key(item["version"]),
        reverse=True,
    )
    return [{**plugin_metadata, "versions": versions}]


def request_bytes(url, token=None):
    headers = {"Accept": "application/vnd.github+json", "User-Agent": "manifest-generator"}
    if token:
        headers["Authorization"] = f"Bearer {token}"
    with urlopen(Request(url, headers=headers)) as response:
        return response.read()


def fetch_releases(repository, token):
    releases = []
    page = 1
    while True:
        url = f"https://api.github.com/repos/{repository}/releases?per_page=100&page={page}"
        batch = json.loads(request_bytes(url, token))
        releases.extend(batch)
        if len(batch) < 100:
            return releases
        page += 1


def main():
    parser = argparse.ArgumentParser(description="Generate a Jellyfin plugin repository manifest")
    parser.add_argument("--repository", required=True, help="GitHub repository in owner/name form")
    parser.add_argument("--metadata", default="build.yaml", help="Path to the current build metadata")
    parser.add_argument("--output", default="manifest.json", help="Generated manifest path")
    args = parser.parse_args()

    token = os.environ.get("GITHUB_TOKEN")
    releases = fetch_releases(args.repository, token)

    def fetch_tag_metadata(tag):
        encoded_tag = quote(tag, safe="")
        url = f"https://raw.githubusercontent.com/{args.repository}/{encoded_tag}/build.yaml"
        return request_bytes(url, token).decode("utf-8")

    manifest = build_manifest(
        args.repository,
        Path(args.metadata).read_text(encoding="utf-8"),
        releases,
        lambda url: request_bytes(url, token),
        fetch_tag_metadata,
    )
    output_path = Path(args.output)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
