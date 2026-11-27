import contextlib
import io
import json
import unittest
from unittest.mock import patch

try:
    from momentum_control import cli
except ImportError:
    cli = None


class CliTests(unittest.TestCase):
    def setUp(self):
        self.assertIsNotNone(cli, 'Linux CLI implementation is required')

    def test_help_no_bluetooth(self):
        with patch.object(cli, 'discover', side_effect=AssertionError('unexpected Bluetooth discovery')):
            with contextlib.redirect_stdout(io.StringIO()), self.assertRaises(SystemExit) as result:
                cli.main(['--help'])
        self.assertEqual(result.exception.code, 0)

    def test_forwarded_command_skips_bluetooth(self):
        output = io.StringIO()
        with patch.object(cli.control, 'forward', return_value=(0, 'forwarded\n', '')) as forward, \
                patch.object(cli, 'discover', side_effect=AssertionError('unexpected Bluetooth discovery')), \
                contextlib.redirect_stdout(output):
            self.assertEqual(cli.main(['set', 'smart_pause', 'on']), 0)
        forward.assert_called_once_with(['set', 'smart_pause', 'on'])
        self.assertEqual(output.getvalue(), 'forwarded\n')

    def test_forwarded_failure_keeps_exit_code_and_stderr(self):
        errors = io.StringIO()
        with patch.object(cli.control, 'forward', return_value=(1, '', 'momentumctl: rejected\n')), \
                contextlib.redirect_stderr(errors):
            self.assertEqual(cli.main(['status']), 1)
        self.assertEqual(errors.getvalue(), 'momentumctl: rejected\n')

    def test_devices_and_invalid_calls_are_not_forwarded(self):
        device = cli.Device('00:11:22:33:44:55', 'Renamed', '/org/bluez/hci0/dev_00_11_22_33_44_55', False)
        with patch.object(cli.control, 'forward', side_effect=AssertionError('must not forward')), \
                patch.object(cli, 'discover', return_value=[device]), contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(cli.main(['devices']), 0)
            with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
                cli.main(['set', 'sidetone', '9'])

    def test_run_headset_writes_status_to_the_given_stream(self):
        snapshot = cli.Snapshot('M4AEBT Black', '3.38.3', 80, {'anc_enabled': True}, (1.0,), (90,), frozenset(), ())
        class Client:
            def snapshot(self):
                return snapshot
        args, value = cli.parse_args(['status', '--json'])
        output = io.StringIO()
        self.assertIs(cli.run_headset(args, value, Client(), output), snapshot)
        self.assertEqual(json.loads(output.getvalue())['battery'], 80)

    def test_precheck_rejects_what_the_session_cannot_run(self):
        peer = cli.PeerDevice(1, 'Phone', False, False)
        snapshot = cli.Snapshot('M4AEBT Black', '3.38.3', 80, {'anc_enabled': True, 'antiwind': 0}, (1.0, 2.0), (90, 325),
                                frozenset({'anc_enabled', 'antiwind', 'eq', 'connect_peer'}), (), choices={'antiwind': (0, 1)},
                                peers=(peer,), eq_range=(-6.0, 6.0))
        cases = [
            (['set', 'immersive_audio', 'on'], 'immersive_audio'),
            (['set', 'antiwind', '2'], 'antiwind'),
            (['eq', '5', '1.0'], 'band'),
            (['eq', '0', '7'], 'range'),
            (['rename', 'New'], 'rename'),
            (['connect-peer', '1', '--expected-name', 'Other'], 'peer'),
        ]
        for argv, word in cases:
            with self.subTest(argv=argv):
                args, value = cli.parse_args(argv)
                self.assertIn(word, cli.precheck(args, value, snapshot))
        for argv in (['status'], ['set', 'anc_enabled', 'off'], ['set', 'antiwind', '1'], ['eq', '1', '-2.5'],
                     ['connect-peer', '1', '--expected-name', 'Phone'], ['peers']):
            with self.subTest(argv=argv):
                args, value = cli.parse_args(argv)
                self.assertIsNone(cli.precheck(args, value, snapshot))

    def test_devices_json(self):
        device = cli.Device('00:11:22:33:44:55', 'Renamed', '/org/bluez/hci0/dev_00_11_22_33_44_55', False)
        output = io.StringIO()
        with patch.object(cli, 'discover', return_value=[device]), contextlib.redirect_stdout(output):
            self.assertEqual(cli.main(['devices', '--json']), 0)
        self.assertEqual(json.loads(output.getvalue())[0]['name'], 'Renamed')

    def test_ambiguous_device_rejected(self):
        device = cli.Device('00:11:22:33:44:55', 'Renamed', '/org/bluez/hci0/dev_00_11_22_33_44_55', False)
        with patch.object(cli, 'discover', return_value=[device, device]), contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(cli.main(['status']), 1)

    def test_no_device_error_actionable(self):
        output = io.StringIO()
        with patch.object(cli, 'discover', return_value=[]), contextlib.redirect_stderr(output):
            self.assertEqual(cli.main(['status']), 1)
        self.assertIn('pair', output.getvalue())

    def test_invalid_and_destructive_commands_rejected(self):
        for args in (['raw', '0040'], ['reset'], ['set', 'anc_strength', 'abc'], ['eq', '0', 'nan']):
            with self.subTest(args=args), patch.object(cli, 'discover', side_effect=AssertionError('invalid action reached Bluetooth')):
                with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as result:
                    cli.main(args)
                self.assertEqual(result.exception.code, 2)


class DongleCliTests(unittest.TestCase):
    def test_unconfirmed_update_is_rejected_before_any_hardware_or_network(self):
        output = io.StringIO()
        with patch.object(cli, 'discover', side_effect=AssertionError('Bluetooth access')), patch.object(cli, 'discover_dongles', side_effect=AssertionError('USB access')), patch.object(cli, 'check_dongle_update', side_effect=AssertionError('Firmware access')), contextlib.redirect_stderr(output):
            with self.assertRaises(SystemExit) as result:
                cli.main(['dongle-update'])
        self.assertEqual(result.exception.code, 2)
        self.assertIn('--confirm', output.getvalue())

    def test_status_works_without_bluetooth_and_does_not_leak_identity(self):
        from types import SimpleNamespace
        device = SimpleNamespace(identity='private-serial', control_path='/fake', usb_path='/private', upgrade_path='/fake2')
        status = SimpleNamespace(device=device, firmware='3.11.0', state=3, audio_mode=1,
            transport=1, codec_mask=4, sample_bits=24, sample_rate=96000)
        output = io.StringIO()
        with patch.object(cli, 'discover_dongles', return_value=[device], create=True), patch.object(cli, 'read_dongle_status', return_value=status, create=True), patch.object(cli, 'discover', side_effect=AssertionError('Bluetooth access')), contextlib.redirect_stdout(output):
            self.assertEqual(cli.main(['dongle-status', '--json']), 0)
        data = json.loads(output.getvalue())
        self.assertEqual(data['audio_state'], 'Streaming audio')
        self.assertEqual(data['firmware'], '3.11.0')
        self.assertNotIn('private', output.getvalue())

    def test_ambiguous_dongle_requires_explicit_index(self):
        from types import SimpleNamespace
        device = SimpleNamespace(identity='private', control_path='/fake')
        output = io.StringIO()
        with patch.object(cli, 'discover_dongles', return_value=[device, device], create=True), contextlib.redirect_stderr(output):
            self.assertEqual(cli.main(['dongle-check']), 1)
        self.assertIn('--dongle', output.getvalue())

    def test_latest_check_and_streaming_update_never_flash(self):
        from types import SimpleNamespace
        device = SimpleNamespace(identity='private', control_path='/fake')
        plan = SimpleNamespace(device=device, current=SimpleNamespace(firmware='3.11.0', config=1),
            target='3.11.0', available=False, image=None, reason='No newer firmware available')
        status = SimpleNamespace(device=device, firmware='3.11.0', state=3)
        with patch.object(cli, 'discover_dongles', return_value=[device], create=True), patch.object(cli, 'check_dongle_update', return_value=plan, create=True), patch.object(cli, 'read_dongle_status', return_value=status, create=True), patch.object(cli, 'apply_dongle_update', side_effect=AssertionError('Unexpected flash'), create=True):
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                self.assertEqual(cli.main(['dongle-check', '--json']), 0)
            self.assertFalse(json.loads(output.getvalue())['available'])
            with contextlib.redirect_stderr(io.StringIO()):
                self.assertEqual(cli.main(['dongle-update', '--confirm']), 1)

    def test_interrupted_update_retains_stage_recovery_error_and_exit130(self):
        from types import SimpleNamespace
        from momentum_control.firmware_update import UpdateError
        device = SimpleNamespace(identity='private', control_path='/fake')
        plan = SimpleNamespace(device=device, current=SimpleNamespace(firmware='3.11.0', config=1),
            target='3.12.0', available=True, image=object(), reason='New firmware')
        error = UpdateError('transfer', 'Interrupted by user', True, interrupted=True)
        output = io.StringIO()
        with patch.object(cli, 'discover_dongles', return_value=[device]), patch.object(cli, 'read_dongle_status', return_value=SimpleNamespace(state=2)), patch.object(cli, 'check_dongle_update', return_value=plan), patch.object(cli, 'apply_dongle_update', side_effect=error), contextlib.redirect_stderr(output):
            self.assertEqual(cli.main(['dongle-update', '--confirm']), 130)
        self.assertIn('transfer', output.getvalue())
        self.assertIn('recovery required', output.getvalue())
