import json
import os
import socket
import stat
import tempfile
import unittest
from unittest.mock import patch

from momentum_control import control


@unittest.skipUnless(hasattr(socket, 'AF_UNIX') and hasattr(socket, 'SO_PEERCRED'), 'Unix control socket is Linux-only')
class ControlTests(unittest.TestCase):
    def setUp(self):
        self.runtime = tempfile.mkdtemp()
        os.chmod(self.runtime, 0o700)
        environment = patch.dict(os.environ, {'XDG_RUNTIME_DIR': self.runtime})
        environment.start()
        self.addCleanup(environment.stop)

    def serve(self, handler):
        server = control.ControlServer(handler)
        self.addCleanup(server.close)
        return server

    def test_roundtrip_returns_exit_and_output(self):
        seen = []
        self.serve(lambda argv: (seen.append(argv), {'exit': 0, 'stdout': 'ok\n', 'stderr': ''})[1])
        self.assertEqual(control.forward(['status', '--json']), (0, 'ok\n', ''))
        self.assertEqual(seen, [['status', '--json']])

    def test_gui_without_headset_session_falls_back(self):
        self.serve(lambda argv: {'fallback': True})
        self.assertIsNone(control.forward(['status']))

    def test_no_running_gui_means_direct_connection(self):
        self.assertIsNone(control.forward(['status']))

    def test_busy_gui_is_retried(self):
        calls = []

        def handler(argv):
            calls.append(argv)
            return {'busy': True} if len(calls) < 3 else {'exit': 0, 'stdout': '', 'stderr': ''}

        self.serve(handler)
        self.assertEqual(control.forward(['status'], retry_delay=0.01), (0, '', ''))
        self.assertEqual(len(calls), 3)

    def test_socket_and_directory_are_private_and_removed(self):
        server = self.serve(lambda argv: {'fallback': True})
        path = control.socket_path()
        self.assertEqual(stat.S_IMODE(os.stat(os.path.dirname(path)).st_mode), 0o700)
        self.assertEqual(stat.S_IMODE(os.stat(path).st_mode), 0o600)
        server.close()
        self.assertFalse(os.path.exists(path))

    def test_malformed_request_is_rejected_without_handler(self):
        self.serve(lambda argv: self.fail('handler must not run'))
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as client:
            client.connect(control.socket_path())
            client.sendall(b'{"argv": 5}\n')
            reply = json.loads(client.makefile('r', encoding='utf-8').readline())
        self.assertEqual(reply['exit'], 2)

    def test_missing_runtime_dir_disables_forwarding(self):
        with patch.dict(os.environ, {'XDG_RUNTIME_DIR': ''}):
            self.assertIsNone(control.socket_path())
            self.assertIsNone(control.forward(['status']))

    def test_stale_socket_file_is_replaced(self):
        first = control.ControlServer(lambda argv: {'fallback': True})
        first.abandon()  # Datei bleibt liegen, wie nach einem Absturz
        self.serve(lambda argv: {'exit': 0, 'stdout': 'new', 'stderr': ''})
        self.assertEqual(control.forward(['status']), (0, 'new', ''))
