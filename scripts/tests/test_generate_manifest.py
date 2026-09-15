import hashlib
import importlib.util
import io
import json
from pathlib import Path
import unittest
import zipfile


SCRIPT_PATH = Path(__file__).parents[1] / "generate_manifest.py"


def load_generator():
    if not SCRIPT_PATH.exists():
        raise AssertionError("The manifest generator script is missing.")

    spec = importlib.util.spec_from_file_location("generate_manifest", SCRIPT_PATH)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def plugin_zip(*extra_files):
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED) as archive:
        plugin = zipfile.ZipInfo("TVHeadEnd.dll", (2026, 9, 15, 18, 0, 0))
        archive.writestr(plugin, b"plugin-binary")
        for name in extra_files:
            archive.writestr(name, b"unexpected")
    return output.getvalue()


class ManifestGeneratorTests(unittest.TestCase):
    def test_publishes_empty_version_list_after_last_zip_release_is_deleted(self):
        generator = load_generator()
        root_metadata = """\
name: "TVHeadend"
guid: "3fd018e5-5e78-4e58-b280-a0c068febee0"
owner: "jellyfin"
overview: "Manage TVHeadend from Jellyfin"
description: "Manage TVHeadend from Jellyfin"
category: "LiveTV"
"""

        try:
            manifest = generator.build_manifest(
                "owner/plugin",
                root_metadata,
                [],
                lambda _: self.fail("No asset should be downloaded"),
                lambda _: self.fail("No tag metadata should be downloaded"),
            )
        except Exception as error:
            self.fail(f"An empty release list was rejected: {error}")

        self.assertEqual([], manifest[0]["versions"])

    def test_lists_installable_zip_releases_newest_first(self):
        generator = load_generator()
        repository = "owner/plugin"
        root_metadata = """\
name: "TVHeadend"
guid: "3fd018e5-5e78-4e58-b280-a0c068febee0"
owner: "jellyfin"
overview: "Manage TVHeadend from Jellyfin"
description: "Manage TVHeadend from Jellyfin"
category: "LiveTV"
"""
        first_zip = plugin_zip()
        second_zip = plugin_zip()
        releases = [
            {
                "tag_name": "v1",
                "draft": False,
                "published_at": "2026-01-01T00:00:00Z",
                "body": "First release\n\n**Full Changelog**: ignored",
                "assets": [
                    {
                        "name": "TVHeadEnd_1.0.0.0.zip",
                        "browser_download_url": "https://example.test/TVHeadEnd_1.0.0.0.zip",
                    }
                ],
            },
            {
                "tag_name": "dll-only",
                "draft": False,
                "published_at": "2025-12-01T00:00:00Z",
                "body": "Not installable",
                "assets": [
                    {
                        "name": "TVHeadEnd.dll",
                        "browser_download_url": "https://example.test/TVHeadEnd.dll",
                    }
                ],
            },
            {
                "tag_name": "v2",
                "draft": False,
                "published_at": "2026-02-01T00:00:00Z",
                "body": "Second release",
                "assets": [
                    {
                        "name": "TVHeadEnd_2.0.0.0.zip",
                        "browser_download_url": "https://example.test/TVHeadEnd_2.0.0.0.zip",
                    }
                ],
            },
        ]
        downloads = {
            "https://example.test/TVHeadEnd_1.0.0.0.zip": first_zip,
            "https://example.test/TVHeadEnd_2.0.0.0.zip": second_zip,
        }
        tagged_metadata = {
            "v1": 'version: 1\ntargetAbi: "10.11.0.0"\n',
            "v2": 'version: "2.0.0.0"\ntargetAbi: "12.0.0.0"\n',
        }

        try:
            manifest = generator.build_manifest(
                repository,
                root_metadata,
                releases,
                downloads.__getitem__,
                tagged_metadata.__getitem__,
            )
        except Exception as error:
            self.fail(f"Valid release metadata was rejected: {error}")

        self.assertEqual("TVHeadend", manifest[0]["name"])
        self.assertEqual(
            ["2.0.0.0", "1.0.0.0"],
            [version["version"] for version in manifest[0]["versions"]],
        )
        self.assertEqual("12.0.0.0", manifest[0]["versions"][0]["targetAbi"])
        self.assertEqual("Second release", manifest[0]["versions"][0]["changelog"])
        self.assertEqual(
            hashlib.md5(first_zip).hexdigest(),
            manifest[0]["versions"][1]["checksum"],
        )
        self.assertEqual(
            "https://example.test/TVHeadEnd_1.0.0.0.zip",
            manifest[0]["versions"][1]["sourceUrl"],
        )

    def test_rejects_zip_with_unexpected_payload(self):
        generator = load_generator()
        root_metadata = """\
name: "TVHeadend"
guid: "3fd018e5-5e78-4e58-b280-a0c068febee0"
owner: "jellyfin"
overview: "Manage TVHeadend from Jellyfin"
description: "Manage TVHeadend from Jellyfin"
category: "LiveTV"
"""
        release = {
            "tag_name": "v1",
            "draft": False,
            "published_at": "2026-01-01T00:00:00Z",
            "body": "Release",
            "assets": [
                {
                    "name": "TVHeadEnd_1.0.0.0.zip",
                    "browser_download_url": "https://example.test/plugin.zip",
                }
            ],
        }

        with self.assertRaisesRegex(ValueError, "exactly TVHeadEnd.dll"):
            generator.build_manifest(
                "owner/plugin",
                root_metadata,
                [release],
                lambda _: plugin_zip("extra.txt"),
                lambda _: 'version: "1.0.0.0"\ntargetAbi: "12.0.0.0"\n',
            )


if __name__ == "__main__":
    unittest.main()
