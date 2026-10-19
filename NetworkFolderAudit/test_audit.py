import csv
import os
from pathlib import Path
import tempfile
import threading
import unittest
from datetime import datetime, timedelta
from unittest.mock import patch

import audit


class AuditTests(unittest.TestCase):
    def test_age_boundaries_are_disjoint(self):
        now = datetime(2026, 10, 19, 12, 30)
        reference = now.timestamp()
        row = audit.Folder('test')
        row.access = reference
        self.assertEqual(audit.age_group(row, reference), audit.AGE_GROUPS[0])
        boundaries = [now - timedelta(days=7)] + [audit.months_ago(now, m) for m in (1, 3, 6, 9, 12)]
        for i, boundary in enumerate(boundaries):
            with self.subTest(boundary=boundary):
                row.access = boundary.timestamp() + 1
                self.assertEqual(audit.age_group(row, reference), audit.AGE_GROUPS[i])
                row.access = boundary.timestamp()
                self.assertEqual(audit.age_group(row, reference), audit.AGE_GROUPS[i + 1])
                row.access -= 1
                self.assertEqual(audit.age_group(row, reference), audit.AGE_GROUPS[i + 1])

    def test_month_end_and_leap_year(self):
        self.assertEqual(audit.months_ago(datetime(2024, 3, 31), 1), datetime(2024, 2, 29))
        self.assertEqual(audit.months_ago(datetime(2024, 2, 29), 12), datetime(2023, 2, 28))

    def test_missing_future_and_incomplete_dates(self):
        reference = datetime(2026, 10, 19).timestamp()
        row = audit.Folder('test')
        self.assertEqual(audit.age_group(row, reference), 'Нет даты обращения')
        row.access = reference + 60
        self.assertEqual(audit.age_group(row, reference), 'Дата в будущем')
        row.access = reference - 50_000_000
        for field in ('errors', 'skipped'):
            setattr(row, field, 1)
            self.assertEqual(audit.age_group(row, reference), 'Неполные данные')
            setattr(row, field, 0)
        self.assertEqual(audit.age_group(row, reference, cancelled=True), 'Неполные данные')

    def test_filter_and_export_use_same_reference(self):
        reference = datetime(2026, 10, 19).timestamp()
        recent = audit.Folder('recent', size=1_000_000_000, access=reference - 60)
        old = audit.Folder('old', access=datetime(2025, 1, 1).timestamp())
        rows = [recent, old]
        self.assertEqual(audit.select_rows(rows, audit.ALL_GROUPS, reference), rows)
        selected = audit.select_rows(rows, 'От 12 месяцев', reference)
        self.assertEqual(selected, [old])
        self.assertEqual(audit.values(recent, False, '', reference)[2], '1.000')
        with tempfile.TemporaryDirectory() as tmp:
            destination = str(Path(tmp) / 'filtered.csv')
            audit.export_csv(destination, selected, [], False, reference)
            with open(destination, encoding='utf-8-sig', newline='') as f:
                parsed = list(csv.reader(f, delimiter=';'))
            self.assertEqual(len(parsed), 2)
            self.assertEqual(parsed[1][0], 'old')
            self.assertEqual(parsed[1][-2], 'От 12 месяцев')
            self.assertEqual(parsed[1][-1], audit.stamp(reference))

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
