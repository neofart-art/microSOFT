"""Network Folder Audit — Python 3.10+, standard library only."""
import argparse
import calendar
import csv
import ctypes
import os
import queue
import stat
import threading
import time
from dataclasses import dataclass
from datetime import datetime, timedelta


GB = 1_000_000_000
ALL_GROUPS = 'Все папки'
AGE_GROUPS = (
    'Менее недели', 'От недели до 1 месяца', 'От 1 до 3 месяцев',
    'От 3 до 6 месяцев', 'От 6 до 9 месяцев', 'От 9 до 12 месяцев',
    'От 12 месяцев', 'Нет даты обращения', 'Дата в будущем', 'Неполные данные',
)


def months_ago(now, months):
    """Calendar month boundaries, including month ends and leap years."""
    year, month = divmod(now.year * 12 + now.month - 1 - months, 12)
    month += 1
    return now.replace(year=year, month=month,
                       day=min(now.day, calendar.monthrange(year, month)[1]))


def age_group(row, reference_time, cancelled=False):
    if cancelled or row.errors or row.skipped:
        return AGE_GROUPS[9]
    if row.access is None or row.access <= 0:
        return AGE_GROUPS[7]
    try:
        access = datetime.fromtimestamp(row.access)
        now = datetime.fromtimestamp(reference_time)
    except (ValueError, OverflowError, OSError):
        return AGE_GROUPS[7]
    if access > now:
        return AGE_GROUPS[8]
    if access > now - timedelta(days=7):
        return AGE_GROUPS[0]
    for index, months in enumerate((1, 3, 6, 9, 12), start=1):
        if access > months_ago(now, months):
            return AGE_GROUPS[index]
    return AGE_GROUPS[6]


def select_rows(rows, group, reference_time, cancelled=False):
    return [row for row in rows if group == ALL_GROUPS
            or age_group(row, reference_time, cancelled) == group]


def stamp(value):
    if value is None or value <= 0:
        return ''
    try:
        return datetime.fromtimestamp(value).astimezone().isoformat(timespec='seconds')
    except (ValueError, OverflowError, OSError):
        return ''


def owner(path):
    if os.name != 'nt':
        return 'Доступно только в Windows'
    from ctypes import wintypes as w
    api = ctypes.WinDLL('advapi32', use_last_error=True)
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    ptr = ctypes.c_void_p
    api.GetNamedSecurityInfoW.argtypes = [w.LPWSTR, w.DWORD, w.DWORD] + [ctypes.POINTER(ptr)] * 5
    api.GetNamedSecurityInfoW.restype = w.DWORD
    api.LookupAccountSidW.argtypes = [w.LPCWSTR, ptr, w.LPWSTR, ctypes.POINTER(w.DWORD),
                                     w.LPWSTR, ctypes.POINTER(w.DWORD), ctypes.POINTER(w.DWORD)]
    api.LookupAccountSidW.restype = w.BOOL
    kernel.LocalFree.argtypes = [ptr]
    kernel.LocalFree.restype = ptr
    sid, descriptor = ptr(), ptr()
    code = api.GetNamedSecurityInfoW(path, 1, 1, ctypes.byref(sid), None, None, None,
                                     ctypes.byref(descriptor))
    try:
        if code:
            return f'Недоступно (код {code})'
        n, d, kind = w.DWORD(), w.DWORD(), w.DWORD()
        api.LookupAccountSidW(None, sid, None, ctypes.byref(n), None, ctypes.byref(d), ctypes.byref(kind))
        code = ctypes.get_last_error()
        if code != 122:
            return f'Имя не определено (код {code})'
        name = ctypes.create_unicode_buffer(max(1, n.value))
        domain = ctypes.create_unicode_buffer(max(1, d.value))
        if not api.LookupAccountSidW(None, sid, name, ctypes.byref(n), domain,
                                    ctypes.byref(d), ctypes.byref(kind)):
            return f'Имя не определено (код {ctypes.get_last_error()})'
        return (domain.value + '\\' if domain.value else '') + name.value
    finally:
        if descriptor:
            kernel.LocalFree(descriptor)


@dataclass
class Folder:
    path: str
    parent: int = -1
    size: int = 0
    files: int = 0
    dirs: int = 0
    access: float | None = None
    modified: float | None = None
    latest: str = ''
    errors: int = 0
    skipped: int = 0
    folder_owner: str = ''
    latest_owner: str = ''
    scanned: bool = False


def is_link(st):
    return stat.S_ISLNK(st.st_mode) or bool(getattr(st, 'st_file_attributes', 0) & 0x400)


def scan(root, cancel=None, progress=None, owners=False):
    cancel = cancel or threading.Event()
    root = os.path.abspath(root)
    root_stat = os.stat(root, follow_symlinks=False)
    if is_link(root_stat) or not stat.S_ISDIR(root_stat.st_mode):
        raise ValueError('Выберите обычную папку, а не ссылку или файл.')
    rows, issues, stack = [Folder(root)], [], [0]
    last_update = 0
    while stack and not cancel.is_set():
        index = stack.pop()
        row = rows[index]
        try:
            with os.scandir(row.path) as entries:
                for entry in entries:
                    if cancel.is_set():
                        break
                    try:
                        info = entry.stat(follow_symlinks=False)
                        if is_link(info):
                            row.skipped += 1
                            issues.append((entry.path, 'Ссылка / reparse point: пропущено'))
                        elif stat.S_ISDIR(info.st_mode):
                            row.dirs += 1
                            rows.append(Folder(entry.path, index))
                            stack.append(len(rows) - 1)
                        elif stat.S_ISREG(info.st_mode):
                            row.files += 1
                            row.size += info.st_size
                            if info.st_atime > 0:
                                row.access = max(row.access or 0, info.st_atime)
                            if row.modified is None or info.st_mtime > row.modified:
                                row.modified, row.latest = info.st_mtime, entry.path
                    except OSError as exc:
                        row.errors += 1
                        issues.append((entry.path, str(exc)))
            row.scanned = not cancel.is_set()
        except OSError as exc:
            row.errors += 1
            issues.append((row.path, str(exc)))
        now = time.monotonic()
        if progress and now - last_update > 0.25:
            progress(f'Папок найдено: {len(rows):,}. Проверяется: {row.path}')
            last_update = now
    # Every child is appended after its parent; reverse order is a postorder.
    for row in reversed(rows):
        if row.parent < 0:
            continue
        parent = rows[row.parent]
        parent.size += row.size
        parent.files += row.files
        parent.dirs += row.dirs
        parent.errors += row.errors
        parent.skipped += row.skipped
        if row.access is not None:
            parent.access = max(parent.access or 0, row.access)
        if row.modified is not None and (parent.modified is None or row.modified > parent.modified):
            parent.modified, parent.latest = row.modified, row.latest
    cancelled = cancel.is_set()
    if owners and not cancelled:
        cache = {}
        for i, row in enumerate(rows):
            if cancel.is_set():
                cancelled = True
                break
            if progress and i % 10 == 0:
                progress(f'Получение владельцев: {i + 1:,} / {len(rows):,}')
            row.folder_owner = owner(row.path)
            if row.latest:
                if row.latest not in cache:
                    cache[row.latest] = owner(row.latest)
                row.latest_owner = cache[row.latest]
    return rows, issues, cancelled


HEADERS = ['Папка', 'Байт (с вложениями)', 'ГБ (1 ГБ = 1 000 000 000 байт)', 'Файлов (с вложениями)',
           'Подпапок (с вложениями)', 'Последнее обращение к файлам (по ФС)',
           'Последнее изменение файла', 'Самый недавно изменённый файл',
           'Владелец папки (не автор)', 'Владелец последнего файла (не автор)',
           'Ошибок в поддереве', 'Пропущено ссылок в поддереве', 'Статус', 'Дата отчёта',
           'Группа по давности обращения', 'Дата оценки давности']


def values(row, cancelled, report_time, reference_time=None):
    reference_time = time.time() if reference_time is None else reference_time
    status = 'ПРЕРВАНО: неполный результат' if cancelled else (
        'НЕПОЛНО: ошибки / пропуски' if row.errors or row.skipped else 'Готово')
    return [row.path, row.size, f'{row.size / GB:.3f}', row.files, row.dirs,
            stamp(row.access), stamp(row.modified), row.latest, row.folder_owner,
            row.latest_owner, row.errors, row.skipped, status, report_time,
            age_group(row, reference_time, cancelled), stamp(reference_time)]


def safe_cell(value):
    # Prevent Excel interpreting an arbitrary filename/account/error as a formula.
    if isinstance(value, str) and value.lstrip().startswith(('=', '+', '-', '@')):
        return "'" + value
    return value


def export_csv(destination, rows, issues, cancelled, reference_time=None):
    reference_time = time.time() if reference_time is None else reference_time
    report_time = stamp(time.time())
    with open(destination, 'w', newline='', encoding='utf-8-sig') as stream:
        writer = csv.writer(stream, delimiter=';')
        writer.writerow(HEADERS)
        for row in sorted(rows, key=lambda r: r.size, reverse=True):
            writer.writerow([safe_cell(v) for v in values(row, cancelled, report_time, reference_time)])
    log_path = os.path.splitext(destination)[0] + '_issues.csv'
    with open(log_path, 'w', newline='', encoding='utf-8-sig') as stream:
        writer = csv.writer(stream, delimiter=';')
        writer.writerow(['Путь', 'Ошибка / причина пропуска'])
        writer.writerows([[safe_cell(p), safe_cell(e)] for p, e in issues])
    return log_path


def gui(smoke_test=False, smoke_rows=None):
    import tkinter as tk
    from tkinter import ttk, filedialog, messagebox
    app = tk.Tk()
    app.title('Анализ сетевых папок • версия 1.1')
    app.geometry('1250x720')
    app.minsize(850, 480)
    frame = ttk.Frame(app, padding=12)
    frame.pack(fill='both', expand=True)
    path, owners = tk.StringVar(), tk.BooleanVar(value=False)
    status = tk.StringVar(value='Выберите сетевую папку или введите \\\\server\\share / Z:\\')
    ttk.Label(frame, text='Сетевая папка:').pack(anchor='w')
    bar = ttk.Frame(frame)
    bar.pack(fill='x', pady=6)
    ttk.Entry(bar, textvariable=path).pack(side='left', fill='x', expand=True)
    ttk.Button(bar, text='Обзор…', command=lambda: path.set(filedialog.askdirectory() or path.get())).pack(side='left', padx=6)
    ttk.Checkbutton(frame, text='Получать владельцев папок и последних файлов (медленнее)', variable=owners).pack(anchor='w')
    ttk.Label(frame, text='Размеры и даты учитывают все вложения. Дата обращения зависит от настроек сервера.\n'
              'Владелец ≠ создатель / загрузивший. Пустая дата означает отсутствие доступных данных.').pack(anchor='w', pady=8)
    actions = ttk.Frame(frame)
    actions.pack(fill='x', pady=5)
    events, cancellation = queue.Queue(), threading.Event()
    result = [None]
    reference_time = [None]
    selected_group = tk.StringVar(value=ALL_GROUPS)
    group_summary = tk.StringVar()
    running = [False]
    buttons = {}
    def start():
        target, need_owners = path.get().strip(), owners.get()
        if not target:
            messagebox.showwarning('Путь', 'Укажите папку.')
            return
        cancellation.clear()
        result[0] = None
        selected_group.set(ALL_GROUPS)
        group_summary.set('')
        group_picker['state'] = 'disabled'
        tree.delete(*tree.get_children())
        running[0] = True
        buttons['start']['state'] = 'disabled'
        buttons['export']['state'] = 'disabled'
        buttons['stop']['state'] = 'normal'
        status.set('Начало сканирования…')
        def work():
            try:
                data = scan(target, cancellation, lambda text: events.put(('progress', text)), need_owners)
                events.put(('done', data))
            except Exception as exc:
                events.put(('error', str(exc)))
        threading.Thread(target=work, daemon=True).start()
    def save():
        destination = filedialog.asksaveasfilename(defaultextension='.csv', filetypes=[('CSV', '*.csv')],
                         initialfile='network_folders_' + datetime.now().strftime('%Y%m%d_%H%M') + '.csv')
        if not destination:
            return
        try:
            rows, issues, cancelled = result[0]
            selected = select_rows(rows, selected_group.get(), reference_time[0], cancelled)
            log = export_csv(destination, selected, issues, cancelled, reference_time[0])
            messagebox.showinfo('Сохранено', f'Группа: {selected_group.get()}\nПапок: {len(selected)}\n'
                                f'Отчёт: {destination}\nЖурнал всего сканирования: {log}')
        except OSError as exc:
            messagebox.showerror('Ошибка сохранения', str(exc))
    for key, label, callback in [('start', 'Сканировать', start),
                                  ('stop', 'Остановить', cancellation.set),
                                  ('export', 'Сохранить CSV…', save)]:
        buttons[key] = ttk.Button(actions, text=label, command=callback)
        buttons[key].pack(side='left', padx=(0, 8))
    buttons['stop']['state'] = buttons['export']['state'] = 'disabled'
    filters = ttk.Frame(frame)
    filters.pack(fill='x', pady=(4, 6))
    ttk.Label(filters, text='Последнее обращение:').pack(side='left', padx=(0, 8))
    group_picker = ttk.Combobox(filters, textvariable=selected_group,
                               values=(ALL_GROUPS,) + AGE_GROUPS, state='disabled', width=32)
    group_picker.pack(side='left')
    ttk.Label(filters, text='CSV сохраняет выбранную группу. 1 ГБ = 1 млрд байт.').pack(side='left', padx=12)
    ttk.Label(frame, textvariable=group_summary).pack(anchor='w', pady=(0, 5))
    table = ttk.Frame(frame)
    table.pack(fill='both', expand=True)
    columns = ('path', 'size', 'files', 'group', 'access', 'modified', 'owner', 'latest_owner', 'errors')
    tree = ttk.Treeview(table, columns=columns, show='headings')
    labels = ['Папка', 'ГБ', 'Файлов', 'Давность обращения', 'Обращение (ФС)', 'Изменение файла', 'Владелец папки', 'Владелец последнего файла', 'Ошибки / пропуски']
    sorting = {}
    def sort(column):
        reverse = not sorting.get(column, False)
        sorting[column] = reverse
        ids = tree.get_children('')
        numeric = column in ('size', 'files', 'errors')
        def key(i):
            row = result[0][0][int(i)]
            return {'size': row.size, 'files': row.files,
                    'errors': row.errors + row.skipped}.get(column, tree.set(i, column)) if numeric else tree.set(i, column).casefold()
        for n, item in enumerate(sorted(ids, key=key, reverse=reverse)):
            tree.move(item, '', n)
    for col, label in zip(columns, labels):
        tree.heading(col, text=label, command=lambda c=col: sort(c))
        tree.column(col, width=350 if col == 'path' else 165, stretch=False)
    vertical = ttk.Scrollbar(table, orient='vertical', command=tree.yview)
    horizontal = ttk.Scrollbar(table, orient='horizontal', command=tree.xview)
    tree.configure(yscrollcommand=vertical.set, xscrollcommand=horizontal.set)
    tree.grid(row=0, column=0, sticky='nsew')
    vertical.grid(row=0, column=1, sticky='ns')
    horizontal.grid(row=1, column=0, sticky='ew')
    table.rowconfigure(0, weight=1)
    table.columnconfigure(0, weight=1)
    ttk.Label(frame, textvariable=status, wraplength=1150).pack(anchor='w', pady=(8, 0))
    ttk.Label(frame, text='Сканирование читает метаданные. Сохраняйте отчёт вне проверяемой папки.').pack(anchor='w')
    def render_rows():
        tree.delete(*tree.get_children())
        if result[0] is None:
            return
        rows, issues, cancelled = result[0]
        group = selected_group.get()
        ordered = sorted(((i, row) for i, row in enumerate(rows) if group == ALL_GROUPS
                          or age_group(row, reference_time[0], cancelled) == group),
                         key=lambda pair: pair[1].size, reverse=True)
        for i, row in ordered[:10000]:
            tree.insert('', 'end', iid=str(i), values=(row.path, f'{row.size / GB:.3f}', row.files,
                        age_group(row, reference_time[0], cancelled), stamp(row.access),
                        stamp(row.modified), row.folder_owner, row.latest_owner, row.errors + row.skipped))
        group_summary.set(f'{group}: {len(ordered):,} папок. '
                          + ('В окне первые 10 000, в CSV — вся группа. ' if len(ordered) > 10000 else '')
                          + 'Размер родителя уже включает вложенные папки.')
    group_picker.bind('<<ComboboxSelected>>', lambda event: render_rows())
    def poll():
        try:
            while True:
                kind, payload = events.get_nowait()
                if kind == 'progress':
                    status.set(payload)
                    continue
                running[0] = False
                buttons['start']['state'] = 'normal'
                buttons['stop']['state'] = 'disabled'
                if kind == 'error':
                    status.set('Не удалось выполнить сканирование.')
                    messagebox.showerror('Ошибка', payload)
                    continue
                result[0] = payload
                reference_time[0] = time.time()
                rows, issues, cancelled = payload
                group_picker['state'] = 'readonly'
                render_rows()
                buttons['export']['state'] = 'normal'
                summary = 'ОСТАНОВЛЕНО, результат неполный' if cancelled else 'Завершено'
                status.set(f'{summary}. Папок: {len(rows):,}; файлов: {rows[0].files:,}; '
                           f'объём: {rows[0].size / GB:.3f} ГБ; ошибок / пропусков: {len(issues):,}. '
                           + ('В окне первые 10 000 папок, все — в CSV.' if len(rows) > 10000 else ''))
        except queue.Empty:
            pass
        app.after(150, poll)
    def close():
        if running[0] and not messagebox.askyesno('Закрытие', 'Прервать сканирование и закрыть программу?'):
            return
        cancellation.set()
        app.destroy()
    app.protocol('WM_DELETE_WINDOW', close)
    app.after(150, poll)
    smoke_errors = []
    if smoke_test:
        def exercise_filters():
            try:
                result[0] = (smoke_rows, [], False)
                reference_time[0] = time.time()
                group_picker['state'] = 'readonly'
                render_rows()
                assert len(tree.get_children()) == len(smoke_rows)
                selected_group.set(AGE_GROUPS[0])
                render_rows()
                assert len(tree.get_children()) == len(smoke_rows)
                selected_group.set(AGE_GROUPS[6])
                render_rows()
                assert not tree.get_children()
            except Exception as exc:
                smoke_errors.append(exc)
            finally:
                app.after(200, app.destroy)
        app.after(500, exercise_filters)
    app.mainloop()
    if smoke_errors:
        raise smoke_errors[0]


def self_test(report_path):
    """Exercise the bundled GUI, scanner, CSV and Windows ownership API."""
    import json
    import tempfile
    from pathlib import Path
    try:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / 'data'
            (root / 'nested').mkdir(parents=True)
            (root / 'a.txt').write_bytes(b'abc')
            (root / 'nested' / 'b.txt').write_bytes(b'12345')
            rows, issues, cancelled = scan(str(root), owners=True)
            assert rows[0].size == 8 and rows[0].files == 2
            assert not issues and not cancelled
            assert rows[0].folder_owner and not rows[0].folder_owner.startswith(
                ('Недоступно', 'Имя не определено'))
            export_csv(str(Path(temporary) / 'report.csv'), rows, issues, cancelled)
            gui(smoke_test=True, smoke_rows=rows)
            report = {'ok': True, 'bytes': rows[0].size, 'files': rows[0].files,
                      'owner': rows[0].folder_owner, 'gui': 'opened, age filters checked, closed'}
        Path(report_path).write_text(json.dumps(report, ensure_ascii=False), encoding='utf-8')
        return 0
    except Exception as exc:
        Path(report_path).write_text(json.dumps({'ok': False, 'error': repr(exc)}), encoding='utf-8')
        return 1


def main():
    parser = argparse.ArgumentParser(description='Анализ размеров сетевых папок')
    parser.add_argument('--path', help='UNC-путь, сетевой диск или локальная папка')
    parser.add_argument('--csv', help='Путь отчёта вне проверяемой папки')
    parser.add_argument('--owners', action='store_true', help='Запрашивать владельцев в Windows')
    parser.add_argument('--self-test', metavar='REPORT_JSON', help=argparse.SUPPRESS)
    args = parser.parse_args()
    if args.self_test:
        return self_test(args.self_test)
    if args.path:
        if not args.csv:
            parser.error('--path требует --csv')
        result = scan(args.path, progress=print, owners=args.owners)
        export_csv(args.csv, *result)
        print('Отчёт сохранён:', args.csv)
        return 2 if result[0][0].errors else 0
    gui()


if __name__ == '__main__':
    raise SystemExit(main())
