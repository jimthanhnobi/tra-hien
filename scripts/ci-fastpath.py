#!/usr/bin/env python3
"""Conservative, dependency-free CI docs-only fast path.

Unrecognized changes and broken diffs never take the fast path.
"""

from __future__ import annotations

import argparse
import os
from pathlib import Path
import re
import subprocess
import sys
from urllib.parse import unquote, urlsplit

_SHA = re.compile(r"[0-9a-fA-F]{40}\Z")
_LINK = re.compile(r"!?(?:\[[^\]]*\])\((<[^>]+>|[^)]+)\)")
_FENCE = re.compile(r"^\s{0,3}([\x60]{3,}|~{3,})")


def changed_paths(event: str, base: str, head: str, before: str = "") -> list[str]:
    if event == "pull_request":
        if not (_SHA.fullmatch(base) and _SHA.fullmatch(head)):
            raise ValueError("Missing or invalid pull request base/head SHA")
        revision = f"{base}...{head}"
    elif event == "push":
        if not (_SHA.fullmatch(before) and _SHA.fullmatch(head)) or before == "0" * 40:
            raise ValueError("Missing, initial or invalid push before/head SHA")
        revision = f"{before}..{head}"
    else:
        raise ValueError(f"Unsupported CI event: {event}")

    # --no-renames exposes BOTH old and new names for moves. A code->docs rename
    # must not be mistaken for documentation-only.
    result = subprocess.run(
        ["git", "diff", "--no-ext-diff", "--no-textconv", "--no-renames", "--name-only", "-z", revision, "--"],
        check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    )
    if not result.stdout.endswith(b"\0") and result.stdout:
        raise ValueError("Incomplete NUL-delimited git diff")
    return [os.fsdecode(raw) for raw in result.stdout.split(b"\0") if raw]


def is_docs_only(paths: list[str]) -> bool:
    # Explicit conservative allowlist. schema.sql, CI config, AGENTS.md,
    # .agents, source code and all unknown paths run the full suite.
    return bool(paths) and all(
        (p == "README.md" or (p.startswith("docs/") and p.endswith(".md")))
        and "\\" not in p and "\n" not in p and "\r" not in p
        for p in paths
    )


def validate_docs(paths: list[str], root: Path) -> None:
    root = root.resolve()
    errors: list[str] = []
    for name in paths:
        file = root / name
        if not file.exists() and not file.is_symlink():  # deleted source; skip
            continue
        if not file.is_file() or file.is_symlink() or not file.resolve().is_relative_to(root):
            errors.append(f"{name}: not an ordinary in-repository Markdown file")
            continue
        try:
            lines = file.read_text(encoding="utf-8").splitlines()
        except (OSError, UnicodeError) as exc:
            errors.append(f"{name}: cannot read UTF-8 Markdown: {exc}")
            continue
        fence_char = ""
        fence_len = 0
        for number, line in enumerate(lines, start=1):
            m = _FENCE.match(line)
            if m:
                marker = m.group(1)
                if not fence_char:
                    fence_char, fence_len = marker[0], len(marker)
                elif marker[0] == fence_char and len(marker) >= fence_len and not line[m.end():].strip():
                    fence_char, fence_len = "", 0
                continue
            if fence_char:
                continue
            if line.rstrip(" \t") != line:
                errors.append(f"{name}:{number}: trailing whitespace")
            # Check local file targets, skip HTTP/mailto, site-root paths and
            # fragment-only links. Anchors are intentionally left to G4 review
            # because GitHub heading slug rules differ from simple regexes.
            for match in _LINK.finditer(line):
                raw = match.group(1).strip()
                href = raw[1:-1] if raw.startswith("<") and raw.endswith(">") else raw.split(' "', 1)[0]
                if href.startswith(("#", "/", "//")):
                    continue
                parsed = urlsplit(href)
                if parsed.scheme or parsed.netloc or not parsed.path:
                    continue
                target = (file.parent / unquote(parsed.path)).resolve()
                if not target.is_relative_to(root) or not target.exists():
                    errors.append(f"{name}:{number}: missing/out-of-repo local link: {parsed.path}")
        if fence_char:
            errors.append(f"{name}: unterminated fenced code block")
    if errors:
        raise ValueError("Markdown checks failed:\n" + "\n".join(errors))


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--event", required=True, choices=["push", "pull_request"])
    parser.add_argument("--base", default="")
    parser.add_argument("--head", required=True)
    parser.add_argument("--before", default="")
    parser.add_argument("--github-output", required=True)
    args = parser.parse_args()
    try:
        paths = changed_paths(args.event, args.base, args.head, args.before)
        docs_only = is_docs_only(paths)
        if docs_only:
            validate_docs(paths, Path.cwd())
        # Only emit docs_only after *all* validation passes; if any error,
        # the classifier job fails and all required jobs fail closed.
        with open(args.github_output, "a", encoding="utf-8") as output:
            output.write(f"docs_only={'true' if docs_only else 'false'}\n")
        print(f"CI scope: {'docs-only fast path' if docs_only else 'full suite'} ({len(paths)} changed paths)")
        return 0
    except (OSError, subprocess.CalledProcessError, ValueError) as exc:
        print(f"CI scope error (required checks must fail): {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())

