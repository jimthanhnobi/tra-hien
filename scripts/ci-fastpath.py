#!/usr/bin/env python3
"""Conservative, dependency-free CI scope classifier and local docs validator.

Unrecognized changes and broken diffs never take the fast path.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import sys
from urllib.parse import unquote, urlsplit

_SHA = re.compile(r"[0-9a-fA-F]{40}\Z")
_COMMIT_SHA = re.compile(r"[0-9a-f]{40}(?:[0-9a-f]{24})?\Z")
_SHA256 = re.compile(r"[0-9a-f]{64}\Z")
_DRIVE_PREFIX = re.compile(r"^[A-Za-z]:")
_RAW_DIFF_META = re.compile(r"^:([0-7]{6}) ([0-7]{6}) [0-9a-f]+ [0-9a-f]+ ([A-Z][0-9]*)\Z")
_LS_FILES_META = re.compile(r"^([0-7]{6}) ([0-9a-f]+) ([0-3])\Z")
_LINK = re.compile(r"!?(?:\[[^\]]*\])\((<[^>]+>|[^)]+)\)")
_REF_DEF = re.compile(r"^\s{0,3}\[[^\]]+\]:\s*(<[^>]+>|\S+)")
# CommonMark permits multiline and blockquote reference definitions. We do not
# implement these contexts: reject them instead of silently approving docs-only.
# Broad detection is deliberately conservative (unsupported syntax => failed CI).
_REF_HEAD = re.compile(r"^[ \t]*(?P<quote>(?:>[ \t]*)*)\[[^\]\r\n]+\]:(?P<tail>.*)$")
_FENCE = re.compile(r"^\s{0,3}([\x60]{3,}|~{3,})")
_LOCAL_INVENTORY_SCHEMA = "local-candidate-v1"
_SECRET_EXTENSIONS = (".pfx", ".p12", ".pem", ".key", ".secret")


def is_safe_repo_relpath(path: str) -> bool:
    """Return True only for normalized POSIX relative paths inside the repo."""
    if not isinstance(path, str) or not path:
        return False
    if "\\" in path or path.startswith("/") or _DRIVE_PREFIX.match(path):
        return False
    if any(ord(char) < 32 or ord(char) == 127 for char in path):
        return False
    parts = path.split("/")
    if any(part in {"", ".", ".."} for part in parts):
        return False
    return True


def is_secret_path(path: str) -> bool:
    """Match the repository host-side secret filename/path exclusions."""
    normalized = path.replace("\\", "/")
    parts = [part for part in normalized.split("/") if part]
    if not parts:
        return False
    leaf = parts[-1]
    leaf_lower = leaf.lower()
    if (leaf_lower == ".env" or leaf_lower.startswith(".env.")) and not leaf_lower.endswith(".example"):
        return True
    if leaf_lower.endswith(_SECRET_EXTENSIONS):
        return True
    if any(part.lower() == "secrets" for part in parts[:-1]):
        return True
    return False


def _is_reparse_or_symlink(path: Path) -> bool:
    """Detect POSIX/Windows symlinks AND Windows NTFS junctions / reparse points."""
    try:
        if path.is_symlink():
            return True
    except OSError:
        return True
    isjunction = getattr(os.path, "isjunction", None)
    if isjunction is not None:
        try:
            if isjunction(path):
                return True
        except OSError:
            return True
    reparse_flag = getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0)
    if reparse_flag:
        try:
            st = os.lstat(path)
            if getattr(st, "st_file_attributes", 0) & reparse_flag:
                return True
        except FileNotFoundError:
            return False
        except OSError:
            return True
    return False


def _has_symlink_component(root: Path, rel_path: str) -> bool:
    """Return True if any path component strictly under root is a symlink, junction, or reparse point."""
    current = root
    for part in rel_path.split("/"):
        current = current / part
        if _is_reparse_or_symlink(current):
            return True
        if not current.exists():
            break
    return False


def _assert_safe_disk_boundary(root: Path, rel_path: str) -> None:
    if not is_safe_repo_relpath(rel_path):
        raise ValueError(f"{rel_path}: unsafe or invalid relative path")
    if _has_symlink_component(root, rel_path):
        raise ValueError(f"{rel_path}: symlink, junction, or reparse point is not allowed")
    target = root / rel_path
    if target.exists() and not target.resolve().is_relative_to(root):
        raise ValueError(f"{rel_path}: path resolves outside repository boundary")


def _parse_name_status_z(raw: bytes) -> list[tuple[str, str]]:
    if raw and not raw.endswith(b"\0"):
        raise ValueError("Incomplete NUL-delimited git diff")
    tokens = raw.split(b"\0")[:-1] if raw else []
    if len(tokens) % 2:
        raise ValueError("Malformed git diff status/path pairs")
    return [(os.fsdecode(tokens[i]), os.fsdecode(tokens[i + 1]))
            for i in range(0, len(tokens), 2)]


def _parse_raw_diff_z(raw: bytes) -> dict[str, tuple[str, str, str]]:
    """Return path -> (status, old_mode, new_mode) from `git diff --no-renames --raw -z`."""
    if raw and not raw.endswith(b"\0"):
        raise ValueError("Incomplete NUL-delimited git raw diff")
    tokens = raw.split(b"\0")[:-1] if raw else []
    if len(tokens) % 2:
        raise ValueError("Malformed git raw diff metadata/path pairs")
    result: dict[str, tuple[str, str, str]] = {}
    for i in range(0, len(tokens), 2):
        meta = os.fsdecode(tokens[i])
        rel_path = os.fsdecode(tokens[i + 1])
        m = _RAW_DIFF_META.match(meta)
        if not m:
            raise ValueError(f"Malformed git raw diff entry: {meta}")
        old_mode, new_mode, status = m.group(1), m.group(2), m.group(3)
        result[rel_path] = (status, old_mode, new_mode)
    return result


def _parse_ls_files_s_z(raw: bytes) -> list[tuple[str, str, str, str]]:
    """Return list of (mode, blob, stage, path) from `git ls-files -s -z`."""
    if raw and not raw.endswith(b"\0"):
        raise ValueError("Incomplete NUL-delimited git ls-files -s output")
    tokens = raw.split(b"\0")[:-1] if raw else []
    entries: list[tuple[str, str, str, str]] = []
    for tok in tokens:
        if not tok:
            continue
        line = os.fsdecode(tok)
        tab = line.find("\t")
        if tab <= 0:
            raise ValueError(f"Malformed git ls-files -s entry: {line}")
        m = _LS_FILES_META.match(line[:tab])
        if not m:
            raise ValueError(f"Malformed git ls-files -s metadata: {line[:tab]}")
        entries.append((m.group(1), m.group(2), m.group(3), line[tab + 1:]))
    return entries


def _parse_paths_z(raw: bytes) -> list[str]:
    if raw and not raw.endswith(b"\0"):
        raise ValueError("Incomplete NUL-delimited git path list")
    tokens = raw.split(b"\0")[:-1] if raw else []
    return [os.fsdecode(tok) for tok in tokens if tok]


def changed_files(event: str, base: str, head: str, before: str = "") -> list[tuple[str, str]]:
    """Return (A/M/D status, path). Moves expose old D + new A names."""
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

    result = subprocess.run(
        ["git", "diff", "--no-ext-diff", "--no-textconv", "--no-renames",
         "--name-status", "-z", revision, "--"],
        check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    )
    return _parse_name_status_z(result.stdout)


def changed_paths(event: str, base: str, head: str, before: str = "") -> list[str]:
    """Backwards-compatible names helper for scope/probe tests."""
    return [path for _, path in changed_files(event, base, head, before)]


def is_docs_only(paths: list[str]) -> bool:
    # Explicit conservative allowlist. schema.sql, CI config, AGENTS.md,
    # .agents, source code and all unknown paths run the full suite.
    return bool(paths) and all(
        is_safe_repo_relpath(p)
        and not is_secret_path(p)
        and (p == "README.md" or (p.startswith("docs/") and p.endswith(".md")))
        for p in paths
    )


def classify_scope(changes: list[tuple[str, str]]) -> str:
    """Only narrow, known paths may avoid unrelated heavy jobs."""
    if not changes or any(status not in {"A", "M", "D"} for status, _ in changes):
        return "full"
    paths = [path for _, path in changes]
    if any(not is_safe_repo_relpath(path) or is_secret_path(path) for path in paths):
        return "full"
    if is_docs_only(paths):
        return "docs"
    # Markdown is neutral only for scope selection; docs-only validation remains
    # unchanged. Unknown paths, config, dependencies and generated contracts are full.
    code = [path for path in paths if not is_docs_only([path])]
    if all(re.fullmatch(r"apps/(admin|store)/(src|tests|public)/.+", path)
           for path in code):
        return "frontend"
    if all(re.fullmatch(r"backend/(src|tests)/.+\.cs", path) for path in code):
        return "backend"
    return "full"


def validate_docs(paths: list[str], root: Path) -> None:
    root = root.resolve()
    errors: list[str] = []
    for name in paths:
        if not is_safe_repo_relpath(name):
            errors.append(f"{name}: unsafe or invalid relative path")
            continue
        if is_secret_path(name):
            errors.append(f"{name}: secret-like path refused")
            continue
        file = root / name
        if _has_symlink_component(root, name) or not file.exists() or _is_reparse_or_symlink(file):
            errors.append(f"{name}: missing or non-regular changed Markdown file")
            continue
        if not file.is_file() or not file.resolve().is_relative_to(root):
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
            targets = [match.group(1) for match in _LINK.finditer(line)]
            reference_head = _REF_HEAD.match(line)
            if reference_head:
                definition = _REF_DEF.match(line)
                if reference_head.group("quote"):
                    errors.append(f"{name}:{number}: unsupported blockquote reference definition")
                elif not definition:
                    errors.append(f"{name}:{number}: unsupported or multiline reference definition")
                else:
                    targets.append(definition.group(1))
            for raw in targets:
                raw = raw.strip()
                href = raw[1:-1] if raw.startswith("<") and raw.endswith(">") else raw.split(' "', 1)[0]
                if href.startswith(("#", "/", "//")):
                    continue
                parsed = urlsplit(href)
                if parsed.scheme or parsed.netloc or not parsed.path:
                    continue
                lexical_target = Path(os.path.normpath(str(file.parent / unquote(parsed.path))))
                if lexical_target.is_relative_to(root):
                    rel_lexical = lexical_target.relative_to(root).as_posix()
                    if rel_lexical != "." and _has_symlink_component(root, rel_lexical):
                        errors.append(f"{name}:{number}: local link traverses symlink/junction: {parsed.path}")
                        continue
                target = (file.parent / unquote(parsed.path)).resolve()
                if not target.is_relative_to(root) or not target.exists():
                    errors.append(f"{name}:{number}: missing/out-of-repo local link: {parsed.path}")
        if fence_char:
            errors.append(f"{name}: unterminated fenced code block")
    if errors:
        raise ValueError("Markdown checks failed:\n" + "\n".join(errors))


def validate_changed_docs(changes: list[tuple[str, str]], root: Path) -> None:
    """Validate additions/edits; deletions also recheck surviving links."""
    statuses = {"A", "M", "D"}
    if not changes or any(status not in statuses for status, _ in changes):
        raise ValueError("Unsupported change status for docs validation")

    root_resolved = root.resolve()
    for _, path in changes:
        if not is_safe_repo_relpath(path):
            raise ValueError(f"{path}: unsafe or invalid path in change inventory")
        if is_secret_path(path):
            raise ValueError(f"{path}: secret-like path refused")

    added_modified = [path for status, path in changes if status in {"A", "M"}]
    deleted = [path for status, path in changes if status == "D"]
    for path in deleted:
        if _has_symlink_component(root_resolved, path):
            raise ValueError(f"{path}: symlink or junction path refused for deletion")
        target = root_resolved / path
        if target.exists() or _is_reparse_or_symlink(target):
            raise ValueError(f"{path}: deletion not reflected in checkout")

    if deleted:
        # Old links can reside in untouched Markdown. Revalidate every surviving
        # document, not only the files listed by git diff. Root README is included.
        docs_dir = root_resolved / "docs"
        all_docs = sorted(p.relative_to(root_resolved).as_posix()
                          for p in docs_dir.rglob("*.md")) if docs_dir.exists() else []
        if (root_resolved / "README.md").exists():
            all_docs.insert(0, "README.md")
        validate_docs(all_docs, root_resolved)
        validate_docs(added_modified, root_resolved)
    else:
        validate_docs(added_modified, root_resolved)


def _walk_snapshot_files(root: Path) -> list[str]:
    """Return sorted POSIX relative paths of every entry under a snapshot root (excluding .git)."""
    root_resolved = root.resolve()
    rel_paths: list[str] = []
    for dirpath, dirnames, filenames in os.walk(root_resolved, followlinks=False):
        dirnames[:] = sorted(d for d in dirnames if d != ".git")
        current_dir = Path(dirpath)
        for d in list(dirnames):
            candidate = current_dir / d
            if _is_reparse_or_symlink(candidate):
                rel = candidate.relative_to(root_resolved).as_posix()
                raise ValueError(f"{rel}: symlink or junction directory is not allowed in snapshot")
        for filename in sorted(filenames):
            full = current_dir / filename
            rel = full.relative_to(root_resolved).as_posix()
            if _is_reparse_or_symlink(full):
                raise ValueError(f"{rel}: symlink or reparse file is not allowed in snapshot")
            rel_paths.append(rel)
    rel_paths.sort()
    return rel_paths


def compute_snapshot_paths_sha256(root: Path) -> tuple[int, str]:
    rel_paths = _walk_snapshot_files(root)
    payload = ("\n".join(rel_paths) + ("\n" if rel_paths else "")).encode("utf-8")
    return len(rel_paths), hashlib.sha256(payload).hexdigest()


def _sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(65536), b""):
            digest.update(chunk)
    return digest.hexdigest()


def validate_local_inventory(inventory_path: Path, root: Path) -> list[tuple[str, str]]:
    """Fail-closed validation of a local docs-only candidate against its snapshot."""
    root_resolved = root.resolve()
    if not root_resolved.is_dir():
        raise ValueError(f"Snapshot root does not exist or is not a directory: {root}")
    try:
        raw = json.loads(inventory_path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise ValueError(f"Malformed local candidate inventory: {exc}") from exc

    if not isinstance(raw, dict):
        raise ValueError("Malformed local candidate inventory: top-level JSON must be an object")
    if raw.get("schema") != _LOCAL_INVENTORY_SCHEMA:
        raise ValueError(f"Malformed local candidate inventory: unsupported schema '{raw.get('schema')}'")

    base = raw.get("base")
    if not isinstance(base, str) or not _COMMIT_SHA.fullmatch(base):
        raise ValueError("Malformed local candidate inventory: missing or invalid base SHA")

    manifest_sha = raw.get("manifest_sha256")
    if manifest_sha is not None:
        if not isinstance(manifest_sha, str) or (manifest_sha != "NONE" and not _SHA256.fullmatch(manifest_sha)):
            raise ValueError("Malformed local candidate inventory: invalid manifest_sha256")

    raw_changes = raw.get("changes")
    if not isinstance(raw_changes, list) or not raw_changes:
        raise ValueError("Malformed local candidate inventory: changes must be a non-empty list")

    # Reject secret-like files present in a non-git snapshot tree (defense in depth).
    snapshot_files: list[str] | None = None
    if not (root_resolved / ".git").exists():
        snapshot_files = _walk_snapshot_files(root_resolved)
        secret_hits = [rel for rel in snapshot_files if is_secret_path(rel)]
        if secret_hits:
            raise ValueError(f"Secret-like files present in snapshot: {', '.join(secret_hits)}")

    expected_count = raw.get("snapshot_file_count")
    expected_paths_sha = raw.get("snapshot_paths_sha256")
    if expected_count is not None or expected_paths_sha is not None:
        if not isinstance(expected_count, int) or isinstance(expected_count, bool) or expected_count < 0:
            raise ValueError("Malformed local candidate inventory: invalid snapshot_file_count")
        if not isinstance(expected_paths_sha, str) or not _SHA256.fullmatch(expected_paths_sha):
            raise ValueError("Malformed local candidate inventory: invalid snapshot_paths_sha256")
        if snapshot_files is None:
            snapshot_files = _walk_snapshot_files(root_resolved)
        actual_count = len(snapshot_files)
        payload = ("\n".join(snapshot_files) + ("\n" if snapshot_files else "")).encode("utf-8")
        actual_paths_sha = hashlib.sha256(payload).hexdigest()
        if actual_count != expected_count or actual_paths_sha != expected_paths_sha:
            raise ValueError(
                f"Snapshot/inventory mismatch: snapshot has {actual_count} files "
                f"(paths sha256={actual_paths_sha}) vs inventory {expected_count} "
                f"(paths sha256={expected_paths_sha})"
            )

    seen_paths: set[str] = set()
    changes: list[tuple[str, str]] = []
    base_keys = frozenset({"status", "path", "sha256"})
    mode_keys = frozenset({"status", "path", "sha256", "mode"})
    for idx, entry in enumerate(raw_changes):
        if not isinstance(entry, dict) or frozenset(entry.keys()) not in (base_keys, mode_keys):
            raise ValueError(f"Malformed local candidate inventory: invalid entry at index {idx}")
        status = entry["status"]
        rel_path = entry["path"]
        expected_sha = entry["sha256"]
        if not isinstance(status, str) or status not in {"A", "M", "D"}:
            raise ValueError(f"Malformed local candidate inventory: unsupported status '{status}'")
        if not isinstance(rel_path, str) or not is_safe_repo_relpath(rel_path):
            raise ValueError(f"Unsafe or invalid path in inventory: '{rel_path}'")
        if rel_path in seen_paths:
            raise ValueError(f"Malformed local candidate inventory: duplicate path '{rel_path}'")
        seen_paths.add(rel_path)

        if "mode" in entry:
            git_mode = entry["mode"]
            if status in {"A", "M"}:
                if git_mode == "120000":
                    raise ValueError(f"{rel_path}: Git symlink mode 120000 is not allowed for docs-only validation")
                if git_mode not in {"100644", "100755"}:
                    raise ValueError(f"Malformed local candidate inventory: unsupported git mode '{git_mode}' for {rel_path}")
            else:
                if git_mode is not None:
                    raise ValueError(f"Malformed local candidate inventory: deleted {rel_path} must have null mode")

        if is_secret_path(rel_path):
            raise ValueError(f"{rel_path}: secret-like path refused")
        if _has_symlink_component(root_resolved, rel_path):
            raise ValueError(f"{rel_path}: symlink or junction component escapes snapshot trust boundary")

        target = root_resolved / rel_path
        resolved_target = target.resolve()
        if not resolved_target.is_relative_to(root_resolved):
            raise ValueError(f"{rel_path}: path escapes snapshot root")

        if status in {"A", "M"}:
            if not isinstance(expected_sha, str) or not _SHA256.fullmatch(expected_sha):
                raise ValueError(f"Malformed local candidate inventory: {rel_path} requires 64-hex sha256")
            if not target.exists() or not target.is_file() or _is_reparse_or_symlink(target):
                raise ValueError(f"Snapshot/inventory mismatch: {rel_path} is missing or not a regular file")
            actual_sha = _sha256_file(target)
            if actual_sha != expected_sha:
                raise ValueError(
                    f"Snapshot/inventory mismatch: {rel_path} sha256 {actual_sha} != expected {expected_sha}"
                )
        else:
            if expected_sha is not None:
                raise ValueError(f"Malformed local candidate inventory: deleted {rel_path} must have null sha256")
            if target.exists() or _is_reparse_or_symlink(target):
                raise ValueError(f"Snapshot/inventory mismatch: deleted {rel_path} still exists in snapshot")

        changes.append((status, rel_path))

    mode = classify_scope(changes)
    if mode != "docs":
        raise ValueError(f"Local candidate is not docs-only (classified as '{mode}')")

    validate_changed_docs(changes, root_resolved)
    return changes


def _reject_disallowed_git_modes(diff_map: dict[str, tuple[str, str, str]], context: str) -> None:
    for rel_path, (_, old_mode, new_mode) in diff_map.items():
        if old_mode == "120000" or new_mode == "120000":
            raise ValueError(
                f"{rel_path}: Git symlink entry (mode 120000) is not allowed for docs-only validation ({context})"
            )
        if old_mode == "160000" or new_mode == "160000":
            raise ValueError(
                f"{rel_path}: Gitlink/submodule entry (mode 160000) is not allowed for docs-only validation ({context})"
            )


def collect_local_git_changes(base: str, root: Path) -> list[tuple[str, str]]:
    """Collect tracked modified, staged, deleted, and untracked-not-ignored changes vs base.

    Fail closed on Git symlink entries (mode 120000), filesystem symlinks/junctions/reparse points,
    unmerged index entries, secret-like files, and staged-vs-working-tree divergence.
    """
    if not isinstance(base, str) or not base.strip():
        raise ValueError("Missing or invalid local base ref/SHA")
    root_resolved = root.resolve()
    rev = subprocess.run(
        ["git", "-C", str(root_resolved), "rev-parse", "--verify", f"{base.strip()}^{{commit}}"],
        check=False, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
    )
    if rev.returncode != 0 or not _COMMIT_SHA.fullmatch(rev.stdout.strip()):
        raise ValueError(f"Base '{base}' does not resolve to a commit")
    base_sha = rev.stdout.strip()

    head_rev = subprocess.run(
        ["git", "-C", str(root_resolved), "rev-parse", "--verify", "HEAD^{commit}"],
        check=False, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
    )
    if head_rev.returncode != 0 or not _COMMIT_SHA.fullmatch(head_rev.stdout.strip()):
        raise ValueError("Repository HEAD does not resolve to a commit")

    unmerged = subprocess.run(
        ["git", "-c", "core.quotepath=false", "-C", str(root_resolved), "ls-files", "-u", "-z"],
        check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    )
    if unmerged.stdout:
        raise ValueError("Unresolved merge conflicts exist in the index")

    index_proc = subprocess.run(
        ["git", "-c", "core.quotepath=false", "-C", str(root_resolved), "ls-files", "-s", "-z"],
        check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    )
    for git_mode, _, stage, rel_path in _parse_ls_files_s_z(index_proc.stdout):
        if stage != "0":
            raise ValueError(f"{rel_path}: unmerged index stage {stage}")
        if git_mode == "120000":
            raise ValueError(
                f"{rel_path}: Git symlink entry (mode 120000) in index is not allowed for docs-only validation"
            )
        if git_mode == "160000":
            raise ValueError(
                f"{rel_path}: Gitlink/submodule entry (mode 160000) in index is not allowed for docs-only validation"
            )

    all_candidate = subprocess.run(
        ["git", "-c", "core.quotepath=false", "-C", str(root_resolved),
         "ls-files", "-co", "--exclude-standard", "-z"],
        check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    )
    for candidate_path in _parse_paths_z(all_candidate.stdout):
        _assert_safe_disk_boundary(root_resolved, candidate_path)
        if is_secret_path(candidate_path):
            disk = root_resolved / candidate_path
            if disk.exists() or _is_reparse_or_symlink(disk):
                raise ValueError(f"{candidate_path}: secret-like file refused in local candidate")

    wt_base_proc = subprocess.run(
        ["git", "-c", "core.quotepath=false", "-C", str(root_resolved),
         "diff", "--no-ext-diff", "--no-textconv", "--no-renames",
         "--raw", "-z", base_sha, "--"],
        check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    )
    cached_base_proc = subprocess.run(
        ["git", "-c", "core.quotepath=false", "-C", str(root_resolved),
         "diff", "--cached", "--no-ext-diff", "--no-textconv", "--no-renames",
         "--raw", "-z", base_sha, "--"],
        check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    )
    cached_head_proc = subprocess.run(
        ["git", "-c", "core.quotepath=false", "-C", str(root_resolved),
         "diff", "--cached", "--no-ext-diff", "--no-textconv", "--no-renames",
         "--raw", "-z", "HEAD", "--"],
        check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    )
    wt_index_proc = subprocess.run(
        ["git", "-c", "core.quotepath=false", "-C", str(root_resolved),
         "diff", "--no-ext-diff", "--no-textconv", "--no-renames",
         "--raw", "-z", "--"],
        check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    )
    untracked_proc = subprocess.run(
        ["git", "-c", "core.quotepath=false", "-C", str(root_resolved),
         "ls-files", "-o", "--exclude-standard", "-z"],
        check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    )

    wt_vs_base = _parse_raw_diff_z(wt_base_proc.stdout)
    cached_vs_base = _parse_raw_diff_z(cached_base_proc.stdout)
    cached_vs_head = _parse_raw_diff_z(cached_head_proc.stdout)
    wt_vs_index = _parse_raw_diff_z(wt_index_proc.stdout)
    untracked_paths = set(_parse_paths_z(untracked_proc.stdout))

    _reject_disallowed_git_modes(wt_vs_base, "worktree vs base")
    _reject_disallowed_git_modes(cached_vs_base, "index vs base")
    _reject_disallowed_git_modes(cached_vs_head, "index vs HEAD")
    _reject_disallowed_git_modes(wt_vs_index, "worktree vs index")

    # Reject any ambiguous staged-vs-working-tree divergence before reading/validating contents.
    for rel_path, (status, _, _) in cached_vs_head.items():
        if rel_path in wt_vs_index:
            raise ValueError(
                f"{rel_path}: staged index differs from working tree; reconcile or restage before local docs validation"
            )
        disk = root_resolved / rel_path
        if status == "D" and (disk.exists() or _is_reparse_or_symlink(disk) or rel_path in untracked_paths):
            raise ValueError(f"{rel_path}: staged deletion conflicts with file present in working tree")
        if status in {"A", "M"} and (not disk.is_file() or _is_reparse_or_symlink(disk)):
            raise ValueError(f"{rel_path}: staged change is not reflected as a regular file in working tree")

    for rel_path, (status, _, _) in cached_vs_base.items():
        if rel_path not in wt_vs_base:
            raise ValueError(
                f"{rel_path}: staged/index change relative to base is not reflected in working tree"
            )
        disk = root_resolved / rel_path
        if status == "D" and (disk.exists() or _is_reparse_or_symlink(disk) or rel_path in untracked_paths):
            raise ValueError(f"{rel_path}: staged/index deletion conflicts with file present in working tree")

    by_path: dict[str, str] = {}
    for rel_path, (status, _, _) in wt_vs_base.items():
        _assert_safe_disk_boundary(root_resolved, rel_path)
        by_path[rel_path] = status
    for rel_path in sorted(untracked_paths):
        _assert_safe_disk_boundary(root_resolved, rel_path)
        if rel_path not in by_path:
            by_path[rel_path] = "A"

    return [(by_path[p], p) for p in sorted(by_path.keys())]


def validate_local_git_candidate(base: str, root: Path) -> list[tuple[str, str]]:
    """Fail-closed validation of a local git working tree candidate against base."""
    root_resolved = root.resolve()
    changes = collect_local_git_changes(base, root_resolved)
    if not changes:
        raise ValueError("No changes relative to base; cannot classify empty diff as docs-only")
    for _, rel_path in changes:
        if not is_safe_repo_relpath(rel_path):
            raise ValueError(f"{rel_path}: unsafe or invalid path in local candidate")
        if is_secret_path(rel_path):
            raise ValueError(f"{rel_path}: secret-like path refused")
    mode = classify_scope(changes)
    if mode != "docs":
        raise ValueError(f"Local candidate is not docs-only (classified as '{mode}')")
    validate_changed_docs(changes, root_resolved)
    return changes


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--event", choices=["push", "pull_request", "workflow_dispatch"])
    parser.add_argument("--base", default="")
    parser.add_argument("--head", default="")
    parser.add_argument("--before", default="")
    parser.add_argument("--github-output", default="")
    parser.add_argument("--local-inventory", default="")
    parser.add_argument("--local-base", default="")
    parser.add_argument("--root", default=".")
    args = parser.parse_args()

    local_modes = sum(1 for value in (args.local_inventory, args.local_base) if value)
    if local_modes > 1 or (local_modes == 1 and args.event):
        print("CI scope error: --event, --local-inventory, and --local-base are mutually exclusive", file=sys.stderr)
        return 2

    try:
        if args.local_inventory:
            changes = validate_local_inventory(Path(args.local_inventory), Path(args.root))
            print(f"Local docs validation PASS: mode=docs ({len(changes)} candidate paths)")
            return 0
        if args.local_base:
            changes = validate_local_git_candidate(args.local_base, Path(args.root))
            print(f"Local docs validation PASS: mode=docs ({len(changes)} candidate paths)")
            return 0

        if not args.event or not args.head or not args.github_output:
            print("CI scope error: --event, --head, and --github-output are required in CI mode", file=sys.stderr)
            return 2

        if args.event == "workflow_dispatch":
            if not _SHA.fullmatch(args.head):
                raise ValueError("Missing or invalid manual run HEAD SHA")
            changes = []
            mode = "full"
        else:
            changes = changed_files(args.event, args.base, args.head, args.before)
            mode = classify_scope(changes)
        if mode == "docs":
            validate_changed_docs(changes, Path.cwd())
        # Only emit scope after all validation passes.
        with open(args.github_output, "a", encoding="utf-8") as output:
            output.write(f"mode={mode}\n")
            output.write(f"docs_only={'true' if mode == 'docs' else 'false'}\n")
        print(f"CI scope: {mode} ({len(changes)} changed paths)")
        return 0
    except (OSError, subprocess.CalledProcessError, ValueError) as exc:
        print(f"CI scope error (required checks must fail): {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
