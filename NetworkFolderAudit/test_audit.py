import csv
import os
from pathlib import Path
import tempfile
import threading
import unittest
from unittest.mock import patch

import audit


class AuditTests(unittest.TestCase):
    def test_nested_sizes_times_and_export(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp) / 'Данные'
            nested = root / 'Отдел' / 'Проект'
            nested.mkdir(parents=True)
            (root / 'Пустая').mkdir()
            (root / 'a.bin').write_bytes(b'abc')
            (nested / 'b.bin').write_bytes(b'12345')
            os.utime(root / 'a.bin', (1600000000, 1600000020))
            os.utime(nested / 'b.bin', (1700000000, 1700000020))
            rows, issues, cancelled = audit.scan(str(root))
            self.assertFalse(cancelled)
            self.assertEqual(issues, [])
            self.assertEqual((rows[0].size, rows[0].files, rows[0].dirs), (8, 2, 3))
            self.assertEqual(rows[0].access, 1700000000)
            self.assertEqual(rows[0].latest, str(nested / 'b.bin'))
            empty = next(r for r in rows if r.path.endswith('Пустая'))
            self.assertIsNone(empty.access)
            self.assertIsNone(empty.modified)
            report = str(Path(tmp) / 'report.csv')
            audit.export_csv(report, rows, issues, cancelled)
            with open(report, encoding='utf-8-sig', newline='') as f:
                parsed = list(csv.reader(f, delimiter=';'))
            self.assertEqual(len(parsed), len(rows) + 1)
            self.assertEqual(parsed[1][0], str(root))
            self.assertEqual(parsed[1][1], '8')

    def test_links_not_followed(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / 'data').write_bytes(b'1234')
            try:
                (root / 'loop').symlink_to(root, target_is_directory=True)
            except OSError:
                self.skipTest('No symlink permission')
            rows, issues, cancelled = audit.scan(tmp)
            self.assertEqual(len(rows), 1)
            self.assertEqual(rows[0].size, 4)
            self.assertEqual(rows[0].skipped, 1)
            self.assertEqual(len(issues), 1)

    def test_denied_child_propagates(self):
        with tempfile.TemporaryDirectory() as tmp:
            child = Path(tmp) / 'denied'
            child.mkdir()
            original = os.scandir
            def fake(path):
                if path == str(child):
                    raise PermissionError('Access denied')
                return original(path)
            with patch('audit.os.scandir', side_effect=fake):
                rows, issues, _ = audit.scan(tmp)
            self.assertEqual(rows[0].errors, 1)
            self.assertEqual(len(issues), 1)
            self.assertIn('НЕПОЛНО', audit.values(rows[0], False, '')[12])

    def test_cancel_and_csv_injection(self):
        with tempfile.TemporaryDirectory() as tmp:
            cancel = threading.Event()
            cancel.set()
            rows, _, cancelled = audit.scan(tmp, cancel)
            self.assertTrue(cancelled)
            self.assertIn('ПРЕРВАНО', audit.values(rows[0], cancelled, '')[12])
            self.assertEqual(audit.safe_cell(' =1+1'), "' =1+1")

    def test_reparse_detection(self):
        class Reparse:
            st_mode = 0o40755
            st_file_attributes = 0x400
        self.assertTrue(audit.is_link(Reparse()))


if __name__ == '__main__':
    unittest.main()
