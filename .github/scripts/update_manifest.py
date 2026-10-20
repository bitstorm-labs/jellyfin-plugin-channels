#!/usr/bin/env python3
"""Add or replace a version entry in a Jellyfin plugin repository manifest.json.

Used by the release job in .github/workflows/build.yml, and safe to run by
hand to preview what a release will do to manifest.json.
"""
from __future__ import annotations

import argparse
import json
import sys
from datetime import datetime, timezone


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True, help="Path to manifest.json")
    parser.add_argument("--guid", default="7a3f1c92-5d64-4b08-9f21-6c8e5b0a4d73")
    parser.add_argument("--version", required=True, help="Plugin version, e.g. 1.0.0.0")
    parser.add_argument("--checksum", required=True, help="MD5 checksum of the release zip")
    parser.add_argument("--source-url", required=True, help="Download URL for the release zip")
    parser.add_argument("--target-abi", required=True, help="Minimum server ABI, e.g. 10.11.0.0")
    parser.add_argument("--changelog", required=True, help="Changelog text for this version")
    return parser.parse_args()


def main() -> int:
    args = parse_args()

    with open(args.manifest, encoding="utf-8") as f:
        manifest = json.load(f)

    entry = next((p for p in manifest if p.get("guid") == args.guid), None)
    if entry is None:
        print(f"error: no plugin with guid {args.guid} in {args.manifest}", file=sys.stderr)
        return 1

    versions = entry.setdefault("versions", [])

    # Drop any existing entry for this exact version (re-running a release
    # should replace, not duplicate) then add the new one.
    versions[:] = [v for v in versions if v.get("version") != args.version]

    versions.append(
        {
            "version": args.version,
            "changelog": args.changelog,
            "targetAbi": args.target_abi,
            "sourceUrl": args.source_url,
            "checksum": args.checksum,
            "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        }
    )

    # Newest version first, matching the convention used by other Jellyfin
    # plugin repositories.
    versions.sort(key=lambda v: [int(p) for p in v["version"].split(".")], reverse=True)

    with open(args.manifest, "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")

    print(f"added {args.version} to {args.manifest}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
