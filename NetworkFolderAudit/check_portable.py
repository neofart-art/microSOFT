"""Run the frozen executable with no Python on PATH; used by Windows CI."""
import json
import os
from pathlib import Path
import subprocess
import tempfile


executable = Path('dist/NetworkFolderAudit.exe').resolve()
environment = dict(os.environ)
environment.pop('PYTHONHOME', None)
environment.pop('PYTHONPATH', None)
environment['PATH'] = str(Path(environment['SystemRoot']) / 'System32')
with tempfile.TemporaryDirectory() as temporary:
    report = Path(temporary) / 'self_test.json'
    result = subprocess.run([str(executable), '--self-test', str(report)],
                            env=environment, cwd=temporary, timeout=90)
    if report.exists():
        payload = json.loads(report.read_text(encoding='utf-8'))
        print(json.dumps(payload, ensure_ascii=True))
    else:
        raise RuntimeError(f'EXE did not create a test report; exit code {result.returncode}')
    assert result.returncode == 0 and payload['ok'], payload
