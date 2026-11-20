import importlib.util
import os
from pathlib import Path
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


if __name__ == '__main__':
    unittest.main(verbosity=2)

