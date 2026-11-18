"""unity_busy: is a Unity Editor working on THIS project (unity_test_hdrp)? (laneUNITYCHECK v1)

Why: the staged apply scripts refused whenever ANY Unity.exe ran, so Armagedom's Editor
(%USERPROFILE%\\work\\armagedom-unity) blocked our installs. Same rule as pool\\backup.ps1:
only a Unity.exe whose command line contains 'unity_test_hdrp' (case-insensitive) counts, and if the
process list cannot be read we stay safe and report busy.

Use:
    sys.path.append(os.path.join(os.environ['FK_ROOT'], '_agents', 'pool')); import unity_busy
    if unity_busy.unity_busy(): ...refuse...
Or, for scripts that test 'unity.exe' in tasklist output, one line installs a shim (install_tasklist_shim):
a `subprocess.run(['tasklist', ...])` then lists 'unity.exe' only when unity_busy() is True.
Read only: never starts, stops or touches any process.
CLI: python -I unity_busy.py  -> prints BUSY/FREE and the matching process ids, exit 1 if busy.
"""
import json
import subprocess

MARKER = 'unity_test_hdrp'  # compared case-insensitively; also matches '/' and '\\' path forms
_PS = ("Get-CimInstance Win32_Process -Filter \"Name='Unity.exe'\" -ErrorAction Stop | "
       "Select-Object ProcessId,CommandLine | ConvertTo-Json -Compress")
_real_run = subprocess.run  # captured before any shim is installed


def unity_processes(marker=MARKER, _run=None):
    """Return (ok, matches, total). ok False = the process list could not be read.
    matches = [(pid, commandline)] of Unity.exe whose command line contains marker."""
    run = _run or _real_run
    try:
        r = run(['powershell', '-NoProfile', '-NonInteractive', '-Command', _PS],
                capture_output=True, text=True, timeout=60)
        if r.returncode != 0:
            return False, [], 0
        txt = (r.stdout or '').strip()
        if not txt:
            return True, [], 0  # no Unity.exe at all
        data = json.loads(txt)
        if isinstance(data, dict):
            data = [data]
        m = marker.lower().replace('\\', '/')
        hits = [(d.get('ProcessId'), d.get('CommandLine') or '') for d in data
                if m in (d.get('CommandLine') or '').lower().replace('\\', '/')]
        return True, hits, len(data)
    except Exception:
        return False, [], 0


def unity_busy(marker=MARKER):
    """True if a Unity.exe command line contains 'unity_test_hdrp', or if the process list is unreadable."""
    ok, hits, _ = unity_processes(marker)
    return (not ok) or bool(hits)


def filter_tasklist(text):
    """Rewrite tasklist output (lower or mixed case) so 'unity.exe' appears iff unity_busy()."""
    lines = [l for l in text.splitlines() if not l.lower().lstrip().startswith('unity.exe')]
    if unity_busy():
        lines.append('unity.exe                    0 Console                    1     0 K  (unity_busy: this project)')
    return '\n'.join(lines) + '\n'


def install_tasklist_shim():
    """Make subprocess.run(['tasklist', ...]) report unity.exe only for this project's Editor."""
    if getattr(subprocess, '_unity_busy_shim', False):
        return

    def run(*a, **k):
        args = a[0] if a else k.get('args')
        first = (args[0] if isinstance(args, (list, tuple)) and args else args) or ''
        is_tl = isinstance(first, str) and first.lower().replace('.exe', '').split()[:1] == ['tasklist']
        r = _real_run(*a, **k)
        if is_tl and isinstance(r.stdout, str):
            r.stdout = filter_tasklist(r.stdout)
        return r
    subprocess.run = run
    subprocess._unity_busy_shim = True


if __name__ == '__main__':
    import sys
    ok, hits, total = unity_processes()
    print('process list read: %s; Unity.exe processes: %d; for unity_test_hdrp: %d' % ('ok' if ok else 'FAILED', total, len(hits)))
    for pid, cl in hits:
        print('  pid %s %s' % (pid, cl[:160]))
    busy = (not ok) or bool(hits)
    print('BUSY' if busy else 'FREE')
    sys.exit(1 if busy else 0)
