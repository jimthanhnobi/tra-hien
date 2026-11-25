import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('ci_fastpath', Path(__file__).with_name('ci-fastpath.py'))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


def cmd(*args, cwd):
    return subprocess.check_output(['git', *args], cwd=cwd, stderr=subprocess.DEVNULL).decode().strip()


class CiScopeTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.repo = Path(self.temp.name)
        for args in [('init', '-q'), ('config', 'user.email', 'ci-test@example.org'), ('config', 'user.name', 'CI tests')]:
            cmd(*args, cwd=self.repo)
        self.write('README.md', 'base\n')
        self.write('docs/guide.md', '# Intro\n')
        self.write('backend/App.cs', 'class A {}\n')
        cmd('add', '.', cwd=self.repo)
        cmd('commit', '-qm', 'base', cwd=self.repo)
        self.base = cmd('rev-parse', 'HEAD', cwd=self.repo)

    def write(self, name, content):
        path = self.repo / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding='utf-8')

    def changed(self, fn):
        fn()
        cmd('add', '-A', cwd=self.repo)
        cmd('commit', '-qm', 'change', cwd=self.repo)
        head = cmd('rev-parse', 'HEAD', cwd=self.repo)
        here = Path.cwd()
        os.chdir(self.repo)
        try:
            for event in ['pull_request', 'push']:
                paths = module.changed_paths(event, self.base, head, before=self.base)
                yield event, paths
        finally:
            os.chdir(here)

    def test_docs_only_and_readme(self):
        for _, paths in self.changed(lambda: self.write('docs/guide.md', '# Updated\n')):
            self.assertEqual(paths, ['docs/guide.md'])
            self.assertTrue(module.is_docs_only(paths))
            module.validate_docs(paths, self.repo)

    def test_code_change_is_full(self):
        for _, paths in self.changed(lambda: self.write('backend/App.cs', 'class B {}\n')):
            self.assertFalse(module.is_docs_only(paths))

    def test_sql_in_docs_is_full(self):
        for _, paths in self.changed(lambda: self.write('docs/spec/schema.sql', 'create table t (a int);\n')):
            self.assertFalse(module.is_docs_only(paths))

    def test_mixed_doc_code_is_full(self):
        def write():
            self.write('docs/guide.md', '# Updated\n')
            self.write('backend/App.cs', 'class B {}\n')
        for _, paths in self.changed(write):
            self.assertFalse(module.is_docs_only(paths))

    def test_rename_old_and_new_paths(self):
        def move():
            (self.repo / 'backend/App.cs').rename(self.repo / 'docs/App.md')
        for _, paths in self.changed(move):
            self.assertEqual(set(paths), {'backend/App.cs', 'docs/App.md'})
            self.assertFalse(module.is_docs_only(paths))

    def test_docs_broken_link_rejected(self):
        for _, paths in self.changed(lambda: self.write('docs/guide.md', '[broken](../not-found.md)\n')):
            self.assertTrue(module.is_docs_only(paths))
            with self.assertRaisesRegex(ValueError, 'missing'):
                module.validate_docs(paths, self.repo)

    def test_docs_unclosed_fence_rejected(self):
        for _, paths in self.changed(lambda: self.write('docs/guide.md', '# Intro\n```python\nx = 1\n')):
            with self.assertRaisesRegex(ValueError, 'unterminated'):
                module.validate_docs(paths, self.repo)

    def test_links_inside_fence_ignored(self):
        for _, paths in self.changed(lambda: self.write('docs/guide.md', '```md\n[not a link](missing.md)\n```\n')):
            module.validate_docs(paths, self.repo)

    def test_missing_changed_file_rejected(self):
        with self.assertRaisesRegex(ValueError, "missing"):
            module.validate_docs(["docs/absent.md"], self.repo)
        with self.assertRaisesRegex(ValueError, "missing"):
            module.validate_changed_docs([("M", "docs/absent.md")], self.repo)

    def test_deleted_link_target_rejected_both_events(self):
        self.write("README.md", "[Target](docs/target.md)\n")
        self.write("docs/target.md", "# Target\n")
        cmd("add", "-A", cwd=self.repo)
        cmd("commit", "-qm", "add target", cwd=self.repo)
        self.base = cmd("rev-parse", "HEAD", cwd=self.repo)
        for event, paths in self.changed(lambda: (self.repo / "docs/target.md").unlink()):
            self.assertEqual(paths, ["docs/target.md"])
            changes = module.changed_files(event, self.base, cmd("rev-parse", "HEAD", cwd=self.repo),
                                           before=self.base)
            self.assertEqual(changes, [("D", "docs/target.md")])
            with self.assertRaisesRegex(ValueError, "missing"):
                module.validate_changed_docs(changes, self.repo)

    def test_deleted_unlinked_markdown_allowed_both_events(self):
        self.write("docs/obsolete.md", "# Old\n")
        cmd("add", "-A", cwd=self.repo)
        cmd("commit", "-qm", "add obsolete", cwd=self.repo)
        self.base = cmd("rev-parse", "HEAD", cwd=self.repo)
        for event, _ in self.changed(lambda: (self.repo / "docs/obsolete.md").unlink()):
            changes = module.changed_files(event, self.base, cmd("rev-parse", "HEAD", cwd=self.repo),
                                           before=self.base)
            module.validate_changed_docs(changes, self.repo)

    def test_renamed_target_rechecks_unmodified_readme_both_events(self):
        self.write("README.md", "[Target](docs/old.md)\n")
        self.write("docs/old.md", "# Old\n")
        cmd("add", "-A", cwd=self.repo)
        cmd("commit", "-qm", "add rename target", cwd=self.repo)
        self.base = cmd("rev-parse", "HEAD", cwd=self.repo)
        def rename():
            (self.repo / "docs/old.md").rename(self.repo / "docs/new.md")
        for event, _ in self.changed(rename):
            changes = module.changed_files(event, self.base, cmd("rev-parse", "HEAD", cwd=self.repo),
                                           before=self.base)
            self.assertEqual(set(changes), {("D", "docs/old.md"), ("A", "docs/new.md")})
            with self.assertRaisesRegex(ValueError, "missing"):
                module.validate_changed_docs(changes, self.repo)

    def test_reference_link_definition_rejected_both_events(self):
        content = "[Missing][ref]\n\n[ref]: missing.md\n"
        for event, paths in self.changed(lambda: self.write("docs/guide.md", content)):
            changes = module.changed_files(event, self.base, cmd("rev-parse", "HEAD", cwd=self.repo),
                                           before=self.base)
            with self.assertRaisesRegex(ValueError, "missing"):
                module.validate_changed_docs(changes, self.repo)

    def test_reference_definition_inside_fence_ignored(self):
        fence = chr(96) * 3
        self.write("docs/guide.md", f"{fence}md\n[ref]: missing.md\n{fence}\n")
        module.validate_docs(["docs/guide.md"], self.repo)

    def test_valid_reference_definition_accepted(self):
        self.write("docs/guide.md", "[Target][ref]\n\n[ref]: <../README.md> \"title\"\n")
        module.validate_docs(["docs/guide.md"], self.repo)


    def assert_reference_rejected_for_both_events(self, content, with_deletion=False):
        if with_deletion:
            self.write("README.md", content)
            self.write("docs/target.md", "# target\n")
            cmd("add", "-A", cwd=self.repo)
            cmd("commit", "-qm", "reference baseline", cwd=self.repo)
            self.base = cmd("rev-parse", "HEAD", cwd=self.repo)
            mutate = lambda: (self.repo / "docs/target.md").unlink()
        else:
            mutate = lambda: self.write("docs/guide.md", content)

        for event, _ in self.changed(mutate):
            head = cmd("rev-parse", "HEAD", cwd=self.repo)
            changes = module.changed_files(event, self.base, head, before=self.base)
            with self.assertRaisesRegex(ValueError, "unsupported"):
                module.validate_changed_docs(changes, self.repo)

            output = self.repo / "scope-output.txt"
            output.write_text("", encoding="utf-8")
            result = subprocess.run([
                os.sys.executable, str(Path(__file__).with_name("ci-fastpath.py")),
                "--event", event, "--base", self.base, "--head", head,
                "--before", self.base, "--github-output", str(output)
            ], cwd=self.repo, capture_output=True, text=True, check=False)
            self.assertNotEqual(result.returncode, 0, (event, result.stdout, result.stderr))
            self.assertIn("unsupported", result.stderr)
            self.assertEqual("", output.read_text(encoding="utf-8"))

    def test_commonmark_multiline_reference_rejected_for_pr_and_push(self):
        self.assert_reference_rejected_for_both_events("[Missing][ref]\n\n[ref]:\n  missing.md\n")

    def test_commonmark_blockquote_reference_rejected_for_pr_and_push(self):
        self.assert_reference_rejected_for_both_events("[Missing][ref]\n\n> [ref]: missing.md\n")

    def test_nested_blockquote_reference_rejected_for_pr_and_push(self):
        self.assert_reference_rejected_for_both_events("> > [ref]: missing.md\n")

    def test_deletion_unmodified_readme_multiline_reference_rejected(self):
        self.assert_reference_rejected_for_both_events(
            "[Target][ref]\n\n[ref]:\n  docs/target.md\n", with_deletion=True
        )

    def test_deletion_unmodified_readme_blockquote_reference_rejected(self):
        self.assert_reference_rejected_for_both_events(
            "> [ref]: docs/target.md\n", with_deletion=True
        )

    def test_bad_sha_fail_closed(self):
        with self.assertRaises(ValueError):
            module.changed_paths('push', '', 'invalid', before='0'*40)

    def test_impact_matrix(self):
        cases = [
            ([("M", "apps/admin/src/app/page.tsx")], "frontend"),
            ([("A", "apps/store/tests/store.test.tsx")], "frontend"),
            ([("D", "apps/store/public/logo.svg"), ("M", "docs/plans/ui.md")], "frontend"),
            ([("M", "apps/admin/src/app/page.tsx"), ("M", "apps/store/src/app/page.tsx")], "frontend"),
            ([("M", "backend/src/TeaPremium.Application/Catalog/Products.cs")], "backend"),
            ([("M", "backend/tests/TeaPremium.UnitTests/ProductsTests.cs"), ("M", "docs/spec/05-api.md")], "backend"),
            ([("M", "backend/src/TeaPremium.Infrastructure/Persistence/Migrations/Example.cs")], "backend"),
            ([("M", "apps/admin/src/app/page.tsx"), ("M", "backend/src/App.cs")], "full"),
            ([("M", "packages/api-client/src/generated/schema.ts")], "full"),
            ([("M", "apps/admin/package.json")], "full"),
            ([("M", "apps/admin/next.config.ts")], "full"),
            ([("M", "pnpm-lock.yaml")], "full"),
            ([("M", "backend/Directory.Packages.props")], "full"),
            ([("M", "backend/src/App.csproj")], "full"),
            ([("M", "infra/postgres/preflight.sql")], "full"),
            ([("M", "docs/spec/schema.sql")], "full"),
            ([("M", ".github/workflows/ci.yml")], "full"),
            ([("M", "scripts/ci-fastpath.py")], "full"),
            ([("T", "apps/admin/src/app/page.tsx")], "full"),
            ([("M", "apps/admin/src/line\nbreak.ts")], "full"),
            ([("M", "apps/admin/src/back\\slash.ts")], "full"),
            ([("M", "apps/adminish/src/app/page.tsx")], "full"),
            ([("M", "README.md")], "docs"),
            ([], "full"),
        ]
        for changes, expected in cases:
            with self.subTest(changes=changes):
                self.assertEqual(module.classify_scope(changes), expected)

    def test_frontend_scope_both_events(self):
        def mutate():
            self.write("apps/admin/src/app/page.tsx", "export const x = 1;\n")
            self.write("docs/plans/ui.md", "# UI\n")
        for event, _ in self.changed(mutate):
            changes = module.changed_files(event, self.base, cmd("rev-parse", "HEAD", cwd=self.repo),
                                           before=self.base)
            self.assertEqual(module.classify_scope(changes), "frontend")

    def test_rename_from_backend_to_frontend_is_full(self):
        self.write("backend/src/Example.cs", "class Example {}\n")
        cmd("add", "-A", cwd=self.repo)
        cmd("commit", "-qm", "add backend", cwd=self.repo)
        self.base = cmd("rev-parse", "HEAD", cwd=self.repo)
        def mutate():
            target = self.repo / "apps/admin/src/Example.cs"
            target.parent.mkdir(parents=True)
            (self.repo / "backend/src/Example.cs").rename(target)
        for event, _ in self.changed(mutate):
            changes = module.changed_files(event, self.base, cmd("rev-parse", "HEAD", cwd=self.repo),
                                           before=self.base)
            self.assertEqual(module.classify_scope(changes), "full")

    def test_manual_full_and_invalid_sha(self):
        output = self.repo / "scope-output.txt"
        script = str(Path(__file__).with_name("ci-fastpath.py"))
        for sha, expected in [(self.base, 0), ("invalid", 1)]:
            output.write_text("", encoding="utf-8")
            result = subprocess.run([
                os.sys.executable, script, "--event", "workflow_dispatch",
                "--head", sha, "--github-output", str(output)
            ], cwd=self.repo, capture_output=True, text=True, check=False)
            self.assertEqual(result.returncode, expected, result.stderr)
            self.assertEqual(output.read_text(encoding="utf-8"),
                             "mode=full\ndocs_only=false\n" if expected == 0 else "")

    def test_empty_change_is_full(self):
        self.assertFalse(module.is_docs_only([]))

    def test_unknown_paths_full(self):
        self.assertFalse(module.is_docs_only(['.github/workflows/ci.yml']))
        self.assertFalse(module.is_docs_only(['AGENTS.md']))
        self.assertFalse(module.is_docs_only(['scripts/ci-fastpath.py']))
        self.assertFalse(module.is_docs_only(['docs/../README.md']))
        self.assertFalse(module.is_docs_only(['docs/secrets/note.md']))


class LocalDocsCandidateTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.repo = self.root / 'repo'
        self.repo.mkdir()
        for args in [('init', '-q'), ('config', 'user.email', 'ci-test@example.org'), ('config', 'user.name', 'CI tests')]:
            cmd(*args, cwd=self.repo)
        self.write('README.md', '# Root\n\nSee [Guide](docs/guide.md).\n')
        self.write('docs/guide.md', '# Guide\n')
        self.write('docs/obsolete.md', '# Obsolete\n')
        self.write('infra/.env.example', 'EXAMPLE=1\n')
        self.write('backend/App.cs', 'class A {}\n')
        cmd('add', '.', cwd=self.repo)
        cmd('commit', '-qm', 'base', cwd=self.repo)
        self.base = cmd('rev-parse', 'HEAD', cwd=self.repo)
        self.script = str(Path(__file__).with_name('ci-fastpath.py'))

    def write(self, name, content, target_root=None):
        base = target_root or self.repo
        path = base / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding='utf-8')
        return path

    def sha256(self, path):
        return hashlib.sha256(path.read_bytes()).hexdigest()

    def make_snapshot(self):
        snap = self.root / f'snap-{len(list(self.root.iterdir()))}'
        shutil.copytree(self.repo, snap, ignore=shutil.ignore_patterns('.git'))
        return snap

    def write_inventory(self, snap, changes, include_snapshot_hash=True, extra=None):
        entries = []
        for status, rel in changes:
            sha = self.sha256(snap / rel) if status in {'A', 'M'} and (snap / rel).is_file() else None
            entries.append({'status': status, 'path': rel, 'sha256': sha})
        payload = {
            'schema': 'local-candidate-v1',
            'base': self.base,
            'manifest_sha256': 'NONE',
            'changes': entries,
        }
        if include_snapshot_hash:
            count, digest = module.compute_snapshot_paths_sha256(snap)
            payload['snapshot_file_count'] = count
            payload['snapshot_paths_sha256'] = digest
        if extra:
            payload.update(extra)
        inv = self.root / f'inv-{len(list(self.root.iterdir()))}.json'
        inv.write_text(json.dumps(payload), encoding='utf-8')
        return inv

    def run_cli(self, *args, cwd=None):
        return subprocess.run(
            [os.sys.executable, self.script, *args],
            cwd=cwd or self.repo,
            capture_output=True,
            text=True,
            check=False,
        )

    def test_local_modified_staged_untracked_and_deleted_valid(self):
        # Modified (unstaged) + staged new + untracked new + deleted unlinked + Unicode path
        self.write('docs/guide.md', '# Guide v2\n\nLink to [New](staged-new.md).\n')
        self.write('docs/staged-new.md', '# Staged\n')
        cmd('add', 'docs/staged-new.md', cwd=self.repo)
        self.write('docs/hướng dẫn trà.md', '# Trà Hiên\n\nBack to [Root](../README.md).\n')
        (self.repo / 'docs/obsolete.md').unlink()

        changes = module.validate_local_git_candidate(self.base, self.repo)
        self.assertEqual(
            changes,
            [
                ('M', 'docs/guide.md'),
                ('A', 'docs/hướng dẫn trà.md'),
                ('D', 'docs/obsolete.md'),
                ('A', 'docs/staged-new.md'),
            ],
        )

        cli = self.run_cli('--local-base', self.base, '--root', str(self.repo))
        self.assertEqual(cli.returncode, 0, cli.stderr)
        self.assertIn('mode=docs', cli.stdout)

        snap = self.make_snapshot()
        inv = self.write_inventory(snap, changes)
        cli_inv = self.run_cli('--local-inventory', str(inv), '--root', str(snap))
        self.assertEqual(cli_inv.returncode, 0, cli_inv.stderr)

    def test_local_broken_link_in_untracked_markdown_rejected(self):
        self.write('docs/untracked-broken.md', '# Draft\n\n[Oops](does-not-exist.md)\n')
        with self.assertRaisesRegex(ValueError, 'missing'):
            module.validate_local_git_candidate(self.base, self.repo)

        snap = self.make_snapshot()
        inv = self.write_inventory(snap, [('A', 'docs/untracked-broken.md')])
        with self.assertRaisesRegex(ValueError, 'missing'):
            module.validate_local_inventory(inv, snap)

    def test_local_deleted_markdown_breaking_surviving_link_rejected(self):
        # README.md links to docs/guide.md; deleting docs/guide.md (unstaged or staged) must fail.
        (self.repo / 'docs/guide.md').unlink()
        with self.assertRaisesRegex(ValueError, 'missing'):
            module.validate_local_git_candidate(self.base, self.repo)

        snap = self.make_snapshot()
        inv = self.write_inventory(snap, [('D', 'docs/guide.md')])
        with self.assertRaisesRegex(ValueError, 'missing'):
            module.validate_local_inventory(inv, snap)

        # Same when deletion is staged.
        cmd('rm', '-q', 'docs/guide.md', cwd=self.repo)
        with self.assertRaisesRegex(ValueError, 'missing'):
            module.validate_local_git_candidate(self.base, self.repo)

    def test_local_non_doc_candidates_rejected(self):
        # Untracked code file alongside doc edit
        self.write('docs/guide.md', '# Updated\n')
        self.write('scripts/sneaky.ps1', 'Write-Output 1\n')
        with self.assertRaisesRegex(ValueError, 'not docs-only'):
            module.validate_local_git_candidate(self.base, self.repo)
        (self.repo / 'scripts/sneaky.ps1').unlink()

        # docs/spec/schema.sql is not docs-only
        self.write('docs/spec/schema.sql', 'select 1;\n')
        with self.assertRaisesRegex(ValueError, 'not docs-only'):
            module.validate_local_git_candidate(self.base, self.repo)
        (self.repo / 'docs/spec/schema.sql').unlink()

        # Cleanly staged code edit (matching disk) is rejected as not docs-only
        self.write('backend/App.cs', 'class B {}\n')
        cmd('add', 'backend/App.cs', cwd=self.repo)
        with self.assertRaisesRegex(ValueError, 'not docs-only'):
            module.validate_local_git_candidate(self.base, self.repo)

        # Staged code edit reverted on disk is rejected upfront as staged/working-tree divergence
        self.write('backend/App.cs', 'class A {}\n')
        with self.assertRaisesRegex(ValueError, 'staged index differs from working tree'):
            module.validate_local_git_candidate(self.base, self.repo)
        cmd('reset', '-q', 'HEAD', '--', 'backend/App.cs', cwd=self.repo)

        # Non-doc entry in local inventory rejected
        snap = self.make_snapshot()
        inv = self.write_inventory(snap, [('M', 'docs/guide.md'), ('M', 'backend/App.cs')])
        with self.assertRaisesRegex(ValueError, 'not docs-only'):
            module.validate_local_inventory(inv, snap)

    def test_local_staged_deletion_conflicting_with_worktree_rejected(self):
        cmd('rm', '-q', '--cached', 'docs/guide.md', cwd=self.repo)
        with self.assertRaisesRegex(ValueError, 'staged deletion conflicts'):
            module.validate_local_git_candidate(self.base, self.repo)

    def test_local_empty_change_rejected(self):
        with self.assertRaisesRegex(ValueError, 'No changes'):
            module.validate_local_git_candidate(self.base, self.repo)

    def test_local_secret_like_files_rejected(self):
        self.write('docs/guide.md', '# Clean doc\n')

        for rel in ['config/.env', 'keys/dev.pem', 'docs/secrets/leaked.md', 'docs/token.secret']:
            path = self.write(rel, 'SECRET=1\n')
            with self.assertRaisesRegex(ValueError, 'secret-like'):
                module.validate_local_git_candidate(self.base, self.repo)
            snap = self.make_snapshot()
            inv = self.write_inventory(snap, [('M', 'docs/guide.md')])
            with self.assertRaisesRegex(ValueError, 'Secret-like'):
                module.validate_local_inventory(inv, snap)
            path.unlink()

    def test_local_malformed_inventory_rejected(self):
        snap = self.make_snapshot()
        bad_json = self.root / 'bad.json'
        bad_json.write_text('{not-json', encoding='utf-8')
        with self.assertRaisesRegex(ValueError, 'Malformed'):
            module.validate_local_inventory(bad_json, snap)

        cases = [
            {'schema': 'wrong-v9', 'base': self.base, 'changes': []},
            {'schema': 'local-candidate-v1', 'base': 'not-a-sha', 'changes': [{'status': 'M', 'path': 'docs/guide.md', 'sha256': self.sha256(snap / 'docs/guide.md')}]},
            {'schema': 'local-candidate-v1', 'base': self.base, 'changes': []},
            {'schema': 'local-candidate-v1', 'base': self.base, 'changes': [{'status': 'R', 'path': 'docs/guide.md', 'sha256': self.sha256(snap / 'docs/guide.md')}]},
            {'schema': 'local-candidate-v1', 'base': self.base, 'changes': [
                {'status': 'M', 'path': 'docs/guide.md', 'sha256': self.sha256(snap / 'docs/guide.md')},
                {'status': 'M', 'path': 'docs/guide.md', 'sha256': self.sha256(snap / 'docs/guide.md')},
            ]},
            {'schema': 'local-candidate-v1', 'base': self.base, 'changes': [{'status': 'M', 'path': 'docs/guide.md', 'sha256': None}]},
            {'schema': 'local-candidate-v1', 'base': self.base, 'changes': [{'status': 'D', 'path': 'docs/obsolete.md', 'sha256': '0' * 64}]},
            {'schema': 'local-candidate-v1', 'base': self.base, 'changes': [{'status': 'M', 'path': 'docs/guide.md', 'sha256': self.sha256(snap / 'docs/guide.md'), 'extra': 1}]},
        ]
        for idx, body in enumerate(cases):
            inv = self.root / f'malformed-{idx}.json'
            inv.write_text(json.dumps(body), encoding='utf-8')
            with self.subTest(idx=idx):
                with self.assertRaisesRegex(ValueError, 'Malformed'):
                    module.validate_local_inventory(inv, snap)

    def test_local_unsafe_paths_and_traversal_rejected(self):
        snap = self.make_snapshot()
        outside = self.root / 'outside.md'
        outside.write_text('# Outside\n', encoding='utf-8')
        outside_sha = self.sha256(outside)

        unsafe_paths = [
            'docs\\guide.md',
            'docs/../README.md',
            '../outside.md',
            'docs/../../outside.md',
            '/etc/passwd',
            'C:docs/guide.md',
            'docs//guide.md',
            'docs/./guide.md',
            'docs/bad\nname.md',
        ]
        for rel in unsafe_paths:
            inv = self.root / f'unsafe-{abs(hash(rel))}.json'
            inv.write_text(json.dumps({
                'schema': 'local-candidate-v1',
                'base': self.base,
                'changes': [{'status': 'M', 'path': rel, 'sha256': outside_sha}],
            }), encoding='utf-8')
            with self.subTest(rel=rel):
                with self.assertRaisesRegex(ValueError, 'Unsafe|invalid'):
                    module.validate_local_inventory(inv, snap)

        # Symlink escaping snapshot root (enforced unconditionally on POSIX/Linux Docker/CI; best-effort on Windows)
        if os.name != 'nt':
            link_path = snap / 'docs/escape.md'
            os.symlink(outside, link_path)
            inv = self.root / 'symlink-escape.json'
            inv.write_text(json.dumps({
                'schema': 'local-candidate-v1',
                'base': self.base,
                'changes': [{'status': 'A', 'path': 'docs/escape.md', 'sha256': outside_sha}],
            }), encoding='utf-8')
            with self.assertRaisesRegex(ValueError, 'symlink'):
                module.validate_local_inventory(inv, snap)

    def test_local_snapshot_inventory_mismatch_rejected(self):
        self.write('docs/guide.md', '# V2\n')
        snap = self.make_snapshot()
        inv = self.write_inventory(snap, [('M', 'docs/guide.md')])

        # Tamper file content in snapshot after inventory was written -> sha256 mismatch
        self.write('docs/guide.md', '# Tampered\n', target_root=snap)
        with self.assertRaisesRegex(ValueError, 'Snapshot/inventory mismatch'):
            module.validate_local_inventory(inv, snap)

        # Restore content, then inject an unlisted extra file into snapshot -> snapshot_paths_sha256 mismatch
        self.write('docs/guide.md', '# V2\n', target_root=snap)
        self.write('backend/Sneaky.cs', 'class Evil {}\n', target_root=snap)
        with self.assertRaisesRegex(ValueError, 'Snapshot/inventory mismatch'):
            module.validate_local_inventory(inv, snap)
        (snap / 'backend/Sneaky.cs').unlink()

        # Deleted file still present in snapshot -> mismatch
        inv_del = self.write_inventory(snap, [('D', 'docs/obsolete.md')])
        with self.assertRaisesRegex(ValueError, 'Snapshot/inventory mismatch'):
            module.validate_local_inventory(inv_del, snap)

    def test_cli_mutually_exclusive_modes_exit_2(self):
        cli = self.run_cli('--event', 'push', '--local-base', self.base, '--head', self.base, '--github-output', 'out.txt')
        self.assertEqual(cli.returncode, 2)

    def test_f01_git_symlink_mode_120000_rejected_under_core_symlinks_false(self):
        cmd('config', 'core.symlinks', 'false', cwd=self.repo)
        target_text = '../outside-harmless.txt'
        proc = subprocess.run(
            ['git', 'hash-object', '-w', '--stdin'],
            cwd=self.repo,
            input=target_text.encode('utf-8'),
            capture_output=True,
            check=True,
        )
        blob = proc.stdout.decode('utf-8').strip()
        cmd('update-index', '--add', '--cacheinfo', f'120000,{blob},docs/symlink.md', cwd=self.repo)
        self.write('docs/symlink.md', target_text)

        with self.assertRaisesRegex(ValueError, '120000'):
            module.validate_local_git_candidate(self.base, self.repo)

        cli = self.run_cli('--local-base', self.base, '--root', str(self.repo))
        self.assertEqual(cli.returncode, 1)
        self.assertIn('120000', cli.stderr)

        # Inventory with explicit mode 120000 is also rejected even if file on disk is ordinary.
        snap = self.make_snapshot()
        inv = self.root / 'inv-mode-120000.json'
        inv.write_text(json.dumps({
            'schema': 'local-candidate-v1',
            'base': self.base,
            'manifest_sha256': 'NONE',
            'changes': [{
                'status': 'A',
                'path': 'docs/symlink.md',
                'sha256': self.sha256(snap / 'docs/symlink.md'),
                'mode': '120000',
            }],
        }), encoding='utf-8')
        with self.assertRaisesRegex(ValueError, '120000'):
            module.validate_local_inventory(inv, snap)

    def test_f01_junction_or_symlink_directory_outside_repo_rejected(self):
        outside_dir = self.root / 'outside-fixture'
        outside_dir.mkdir()
        (outside_dir / 'leaked.md').write_text('# Harmless outside fixture\n', encoding='utf-8')

        top_link = self.repo / 'docs' / 'ext-dir'
        nested_parent = self.repo / 'docs' / 'sub' / 'deep'
        nested_parent.mkdir(parents=True, exist_ok=True)
        nested_link = nested_parent / 'ext-nested'

        def make_dir_link(link_path, target_dir):
            if os.name == 'nt':
                res = subprocess.run(
                    ['cmd', '/c', 'mklink', '/J', str(link_path), str(target_dir)],
                    capture_output=True, text=True, check=False,
                )
                self.assertEqual(res.returncode, 0, res.stderr or res.stdout)
            else:
                os.symlink(target_dir, link_path, target_is_directory=True)

        def remove_dir_link(link_path):
            if os.name == 'nt':
                subprocess.run(['cmd', '/c', 'rmdir', str(link_path)], capture_output=True, check=False)
            elif link_path.is_symlink():
                link_path.unlink()

        make_dir_link(top_link, outside_dir)
        self.addCleanup(remove_dir_link, top_link)
        with self.assertRaisesRegex(ValueError, 'symlink|junction|reparse'):
            module.validate_local_git_candidate(self.base, self.repo)

        # Direct local inventory check against a tree containing a junction/symlink directory also fails.
        inv = self.root / 'inv-junc.json'
        inv.write_text(json.dumps({
            'schema': 'local-candidate-v1',
            'base': self.base,
            'manifest_sha256': 'NONE',
            'changes': [{
                'status': 'A',
                'path': 'docs/ext-dir/leaked.md',
                'sha256': self.sha256(outside_dir / 'leaked.md'),
                'mode': '100644',
            }],
        }), encoding='utf-8')
        with self.assertRaisesRegex(ValueError, 'symlink|junction|reparse'):
            module.validate_local_inventory(inv, self.repo)

        remove_dir_link(top_link)

        # Nested junction/symlink ancestor component is also rejected.
        make_dir_link(nested_link, outside_dir)
        self.addCleanup(remove_dir_link, nested_link)
        with self.assertRaisesRegex(ValueError, 'symlink|junction|reparse'):
            module.validate_local_git_candidate(self.base, self.repo)
        remove_dir_link(nested_link)

        # Normal in-repository Markdown still passes once unsafe link is removed.
        self.write('docs/guide.md', '# Clean Guide\n')
        changes = module.validate_local_git_candidate(self.base, self.repo)
        self.assertEqual(changes, [('M', 'docs/guide.md')])

    def test_f02_staged_vs_working_tree_mismatches_rejected(self):
        # 1. Exact G4 repro: staged broken Markdown + working tree reverted to valid base
        self.write('docs/guide.md', '# Broken\n\n[Missing](nope.md)\n')
        cmd('add', 'docs/guide.md', cwd=self.repo)
        self.write('docs/guide.md', '# Guide\n')
        with self.assertRaisesRegex(ValueError, 'staged index differs from working tree'):
            module.validate_local_git_candidate(self.base, self.repo)

        # 2. Staged broken Markdown + working tree edited to different valid Markdown
        self.write('docs/guide.md', '# Different Valid\n')
        with self.assertRaisesRegex(ValueError, 'staged index differs from working tree'):
            module.validate_local_git_candidate(self.base, self.repo)

        # 3. Staged valid Markdown + working tree edited to broken Markdown
        self.write('docs/guide.md', '# Valid Staged\n')
        cmd('add', 'docs/guide.md', cwd=self.repo)
        self.write('docs/guide.md', '# Broken WT\n\n[Missing](nope.md)\n')
        with self.assertRaisesRegex(ValueError, 'staged index differs from working tree'):
            module.validate_local_git_candidate(self.base, self.repo)

        # 4. Staged valid Markdown + working tree edited to another valid Markdown
        self.write('docs/guide.md', '# Another Valid WT\n')
        with self.assertRaisesRegex(ValueError, 'staged index differs from working tree'):
            module.validate_local_git_candidate(self.base, self.repo)
        cmd('checkout', 'HEAD', '--', 'docs/guide.md', cwd=self.repo)

        # 5. Staged addition followed by modification or deletion on disk
        self.write('docs/staged-add.md', '# Added v1\n')
        cmd('add', 'docs/staged-add.md', cwd=self.repo)
        self.write('docs/staged-add.md', '# Added v2\n')
        with self.assertRaisesRegex(ValueError, 'staged index differs from working tree'):
            module.validate_local_git_candidate(self.base, self.repo)
        (self.repo / 'docs/staged-add.md').unlink()
        with self.assertRaisesRegex(ValueError, 'staged index differs from working tree'):
            module.validate_local_git_candidate(self.base, self.repo)
        cmd('reset', '-q', 'HEAD', '--', 'docs/staged-add.md', cwd=self.repo)

        # 6. Staged deletion followed by file recreation on disk
        cmd('rm', '-q', 'docs/obsolete.md', cwd=self.repo)
        self.write('docs/obsolete.md', '# Recreated\n')
        with self.assertRaisesRegex(ValueError, 'staged deletion conflicts'):
            module.validate_local_git_candidate(self.base, self.repo)
        cmd('checkout', 'HEAD', '--', 'docs/obsolete.md', cwd=self.repo)

        # 7. Staged rename (clean PASS, post-rename edit REJECTED)
        cmd('mv', 'docs/obsolete.md', 'docs/renamed.md', cwd=self.repo)
        changes = module.validate_local_git_candidate(self.base, self.repo)
        self.assertEqual(changes, [('D', 'docs/obsolete.md'), ('A', 'docs/renamed.md')])
        self.write('docs/renamed.md', '# Edited after mv\n')
        with self.assertRaisesRegex(ValueError, 'staged index differs from working tree'):
            module.validate_local_git_candidate(self.base, self.repo)
        cmd('reset', '-q', 'HEAD', cwd=self.repo)
        (self.repo / 'docs/renamed.md').unlink()
        cmd('checkout', 'HEAD', '--', 'docs/obsolete.md', cwd=self.repo)

        # 8. Valid non-overlapping workflow: staged file matching disk + separate unstaged edit + untracked file
        self.write('docs/guide.md', '# Staged Clean\n')
        cmd('add', 'docs/guide.md', cwd=self.repo)
        self.write('docs/obsolete.md', '# Unstaged Clean\n')
        self.write('docs/untracked-clean.md', '# Untracked Clean\n')
        changes = module.validate_local_git_candidate(self.base, self.repo)
        self.assertEqual(changes, [
            ('M', 'docs/guide.md'),
            ('M', 'docs/obsolete.md'),
            ('A', 'docs/untracked-clean.md'),
        ])

    def test_f02_unmerged_index_rejected(self):
        # Create a real merge conflict in the index
        cmd('checkout', '-q', '-b', 'branch-a', cwd=self.repo)
        self.write('docs/guide.md', '# Branch A\n')
        cmd('add', 'docs/guide.md', cwd=self.repo)
        cmd('commit', '-qm', 'branch a', cwd=self.repo)
        cmd('checkout', '-q', self.base, cwd=self.repo)
        cmd('checkout', '-q', '-b', 'branch-b', cwd=self.repo)
        self.write('docs/guide.md', '# Branch B\n')
        cmd('add', 'docs/guide.md', cwd=self.repo)
        cmd('commit', '-qm', 'branch b', cwd=self.repo)
        subprocess.run(['git', 'merge', '--no-commit', 'branch-a'], cwd=self.repo, capture_output=True, check=False)
        with self.assertRaisesRegex(ValueError, 'Unresolved merge conflicts'):
            module.validate_local_git_candidate(self.base, self.repo)


if __name__ == '__main__':
    unittest.main(verbosity=2)

