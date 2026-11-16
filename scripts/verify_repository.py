#!/usr/bin/env python3
"""Repository bootstrap guard. Not a substitute for application tests."""
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
REQUIRED = [
    "README.md",
    "AGENTS.md",
    "docs/WORKFLOW.md",
    "docs/spec/README.md",
    ".agents/skills/spec-audit/SKILL.md",
    ".agents/skills/architecture-plan/SKILL.md",
    ".agents/skills/implementation-slice/SKILL.md",
    ".agents/skills/review-gate/SKILL.md",
    ".github/pull_request_template.md",
]
errors = []
for rel in REQUIRED:
    p = ROOT / rel
    if not p.is_file():
        errors.append(f"Missing required file: {rel}")
    elif not p.read_text(encoding="utf-8").strip():
        errors.append(f"Empty required file: {rel}")

for p in sorted((ROOT / ".agents" / "skills").glob("*/SKILL.md")):
    text = p.read_text(encoding="utf-8")
    if not text.startswith("---\n") or "\nname:" not in text or "\ndescription:" not in text:
        errors.append(f"Invalid skill frontmatter (name/description required): {p.relative_to(ROOT)}")

specs = sorted(p for p in (ROOT / "docs" / "spec").rglob("*.md")
               if p.name != "README.md" and p.is_file())
for p in specs:
    text = p.read_text(encoding="utf-8")
    if not text.strip():
        errors.append(f"Empty specification file: {p.relative_to(ROOT)}")
    if any(line.startswith(("<<<<<<< ", ">>>>>>> ")) for line in text.splitlines()):
        errors.append(f"Git conflict marker in spec: {p.relative_to(ROOT)}")

print(f"Repository guard: {len(specs)} real specification Markdown file(s) discovered.")
if not specs:
    print("NOTICE: business specs not yet imported. App implementation gate remains closed.")
if errors:
    for e in errors:
        print(f"ERROR: {e}", file=sys.stderr)
    sys.exit(1)
print("Repository structure guard: PASS (application tests not executed).")
