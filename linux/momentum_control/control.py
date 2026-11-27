"""Hands momentumctl headset commands to a running GUI (like m4ctl's named pipe on Windows).

The GUI owns the Bluetooth control connection while it is connected; a second process would compete for the same
GAIA channel. The GUI serves a Unix stream socket in ``$XDG_RUNTIME_DIR/momentum-control`` (directory 0700, socket
0600, peer UID checked with SO_PEERCRED). One request per connection, one JSON line each way:

request ``{"argv": [...]}``; reply ``{"exit": n, "stdout": "...", "stderr": "..."}``, ``{"busy": true}`` (retry) or
``{"fallback": true}`` (the GUI has no headset session, momentumctl connects on its own).
"""

import json
import os
import socket
import stat
import struct
import threading
import time

_MAX_LINE = 64 * 1024
_SOCKET_NAME = 'control.sock'


def socket_path() -> str | None:
    runtime = os.environ.get('XDG_RUNTIME_DIR', '')
    if not runtime or not os.path.isabs(runtime) or not hasattr(socket, 'AF_UNIX'):
        return None
    return os.path.join(runtime, 'momentum-control', _SOCKET_NAME)


def _read_line(stream) -> bytes:
    line = stream.readline(_MAX_LINE + 1)
    if len(line) > _MAX_LINE or not line.endswith(b'\n'):
        raise ValueError('Control message too long or incomplete')
    return line


def _private_directory(path: str) -> bool:
    try:
        info = os.lstat(os.path.dirname(path))
    except OSError:
        return False
    return stat.S_ISDIR(info.st_mode) and info.st_uid == os.getuid() and stat.S_IMODE(info.st_mode) == 0o700


def forward(argv: list[str], timeout: float = 130.0, attempts: int = 20, retry_delay: float = 0.25):
    """Runs ``argv`` in the GUI; ``None`` means: no GUI session, connect directly."""
    path = socket_path()
    if path is None or not _private_directory(path):
        return None
    for _ in range(attempts):
        try:
            with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as client:
                client.settimeout(timeout)
                try:
                    client.connect(path)
                except OSError:
                    return None  # no (reachable) GUI: momentumctl connects on its own
                client.sendall(json.dumps({'argv': list(argv)}, ensure_ascii=True).encode('ascii') + b'\n')
                with client.makefile('rb') as stream:
                    reply = json.loads(_read_line(stream))
        except (OSError, ValueError) as error:
            raise RuntimeError(f'Running app did not answer: {error}') from error
        if type(reply) is not dict:
            raise RuntimeError('Running app sent an invalid reply')
        if reply.get('fallback') is True:
            return None
        if reply.get('busy') is True:
            time.sleep(retry_delay)
            continue
        code, out, err = reply.get('exit'), reply.get('stdout', ''), reply.get('stderr', '')
        if type(code) is not int or type(out) is not str or type(err) is not str:
            raise RuntimeError('Running app sent an invalid reply')
        return code, out, err
    raise RuntimeError('Running app stayed busy; try again')


class ControlServer:
    """Accepts forwarded commands on a background thread; ``handler(argv) -> dict`` may block until the GUI is done."""

    def __init__(self, handler):
        path = socket_path()
        if path is None:
            raise OSError('XDG_RUNTIME_DIR is not available')
        self.handler = handler
        self.path = path
        directory = os.path.dirname(path)
        os.makedirs(directory, mode=0o700, exist_ok=True)
        info = os.lstat(directory)
        if not stat.S_ISDIR(info.st_mode) or info.st_uid != os.getuid():
            raise OSError('Control directory is not owned by this user')
        os.chmod(directory, 0o700)
        if os.path.lexists(path):
            if not stat.S_ISSOCK(os.lstat(path).st_mode):
                raise OSError('Control path is not a socket')
            if self._alive(path):
                raise OSError('Another Momentum Control instance serves commands')
            os.unlink(path)
        self.listener = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        old_mask = os.umask(0o177)
        try:
            self.listener.bind(path)
        finally:
            os.umask(old_mask)
        os.chmod(path, 0o600)
        self.listener.listen(4)
        self.closed = threading.Event()
        self.thread = threading.Thread(target=self._serve, name='momentum-control-cli', daemon=True)
        self.thread.start()

    @staticmethod
    def _alive(path) -> bool:
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as probe:
            probe.settimeout(0.5)
            try:
                probe.connect(path)
                return True
            except OSError:
                return False

    def _serve(self):
        while not self.closed.is_set():
            try:
                connection, _ = self.listener.accept()
            except OSError:
                if self.closed.is_set():
                    return
                time.sleep(0.1)  # EMFILE, ECONNABORTED …: keep serving
                continue
            threading.Thread(target=self._handle, args=(connection,), daemon=True).start()

    def _handle(self, connection):
        with connection:
            try:
                credentials = connection.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, struct.calcsize('3i'))
                _, uid, _ = struct.unpack('3i', credentials)
                if uid != os.getuid():
                    return
                connection.settimeout(5)
                with connection.makefile('rb') as stream:
                    request = json.loads(_read_line(stream))
                argv = request.get('argv') if type(request) is dict else None
                if type(argv) is not list or not argv or not all(type(item) is str and len(item) <= 256 for item in argv) or len(argv) > 16:
                    reply = {'exit': 2, 'stdout': '', 'stderr': 'momentumctl: invalid forwarded command\n'}
                else:
                    connection.settimeout(None)
                    reply = self.handler(argv)
                connection.sendall(json.dumps(reply, ensure_ascii=True).encode('ascii') + b'\n')
            except (OSError, ValueError):
                return

    def abandon(self):
        """Stops serving but leaves the socket file behind (for tests of stale sockets after a crash)."""
        self.closed.set()
        try:
            # close() alone does not wake a thread blocked in accept() on Linux; shutdown() does.
            self.listener.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass
        self.listener.close()

    def close(self):
        if self.closed.is_set() and self.listener.fileno() == -1 and not os.path.lexists(self.path):
            return
        self.abandon()
        try:
            if stat.S_ISSOCK(os.lstat(self.path).st_mode):
                os.unlink(self.path)
        except FileNotFoundError:
            pass
