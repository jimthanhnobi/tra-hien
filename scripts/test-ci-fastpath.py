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

    def test_bad_sha_fail_closed(self):
        with self.assertRaises(ValueError):
            module.changed_paths('push', '', 'invalid', before='0'*40)

    def test_empty_change_is_full(self):
        self.assertFalse(module.is_docs_only([]))

    def test_unknown_paths_full(self):
        self.assertFalse(module.is_docs_only(['.github/workflows/ci.yml']))
        self.assertFalse(module.is_docs_only(['AGENTS.md']))
        self.assertFalse(module.is_docs_only(['scripts/ci-fastpath.py']))


if __name__ == '__main__':
    unittest.main(verbosity=2)

