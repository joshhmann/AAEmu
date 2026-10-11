#!/usr/bin/env python3
"""Check recovered shell guards using scratch files; no servers, Docker or MySQL."""
import hashlib
from pathlib import Path
import subprocess
import tempfile
import unittest

REPO = Path(__file__).resolve().parents[2]
GUARD = REPO / "Scripts/e2e/ensure-log-caps.sh"
COMMON = REPO / "Scripts/e2e/e2e-common.sh"
CENSUS = REPO / "Scripts/quest_act_ref_missing_census.sh"


class RecoveredGuardTests(unittest.TestCase):
    def test_legacy_logs_are_capped_and_repeated_run_changes_nothing(self):
        with tempfile.TemporaryDirectory(prefix="aaemu guard ") as directory:
            root = Path(directory)
            config = root / "runtime/game/NLog.config"
            config.parent.mkdir(parents=True)
            config.write_text('<target archiveNumbering="Date" archiveDateFormat="yyyy-MM-dd" '
                              'archiveEvery="Day" maxArchiveFiles="9" />\n'
                              '<logger minlevel="Trace" maxlevel="Warn" writeTo="file" />\n')
            subprocess.run(["bash", str(GUARD), str(root)], check=True, capture_output=True)
            self.assertIn('archiveAboveSize="26214400"', config.read_text())
            self.assertIn('AAEMU_E2E_LOG_LEVEL:whenEmpty=Info', config.read_text())
            digest = hashlib.sha256(config.read_bytes()).hexdigest()
            subprocess.run(["bash", str(GUARD), str(root)], check=True, capture_output=True)
            self.assertEqual(digest, hashlib.sha256(config.read_bytes()).hexdigest())

    def test_unknown_uncapped_shape_is_refused(self):
        with tempfile.TemporaryDirectory() as directory:
            config = Path(directory) / "runtime/game/NLog.config"
            config.parent.mkdir(parents=True)
            config.write_text('<target archiveAboveSize="0" />')
            result = subprocess.run(["bash", str(GUARD), directory], capture_output=True)
            self.assertNotEqual(result.returncode, 0)

    def test_shell_guard_prefers_runtime_then_repository_and_refuses_missing(self):
        with tempfile.TemporaryDirectory(prefix="aaemu guard ") as directory:
            root = Path(directory)
            script = 'export E2E_ROOT="$1"; source "$2"; trap - EXIT; e2e_log_caps_guard'
            call = lambda: subprocess.run(["bash", "-c", script, "guard-test", str(root),
                                            str(COMMON)], capture_output=True, text=True)
            self.assertEqual(call().stdout.strip(), str(GUARD))
            runtime_guard = root / "ensure-log-caps.sh"
            runtime_guard.write_text("runtime override")
            self.assertEqual(call().stdout.strip(), str(runtime_guard))
            runtime_guard.unlink()
            missing = script.replace("e2e_log_caps_guard", 'SCRIPT_DIR="$E2E_ROOT"; e2e_log_caps_guard')
            result = subprocess.run(["bash", "-c", missing, "guard-test", str(root), str(COMMON)],
                                    capture_output=True, text=True)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("missing", result.stderr)

    def test_scope_forms_match_on_read_only_canonical_data(self):
        database = REPO / "AAEmu.Game/Data/compact.sqlite3"
        digest = hashlib.md5(database.read_bytes()).hexdigest()
        equals = subprocess.run(["bash", str(CENSUS), str(database), "--scope=0"], capture_output=True)
        spaced = subprocess.run(["bash", str(CENSUS), str(database), "--scope", "0"], capture_output=True)
        self.assertEqual(equals.returncode, spaced.returncode)
        self.assertEqual(equals.stdout, spaced.stdout)
        self.assertIn(equals.returncode, (0, 1))  # census verdict, never parser error
        self.assertEqual(digest, hashlib.md5(database.read_bytes()).hexdigest())

    def test_invalid_scope_is_refused_before_database_access(self):
        for arguments in (["--scope"], ["--scope", "-1"], ["--scope="],
                          ["--scope", "word"], ["--scope", "--apply-fix"], ["--unknown"]):
            with self.subTest(arguments=arguments):
                result = subprocess.run(["bash", str(CENSUS), "/nonexistent/db", *arguments],
                                        capture_output=True)
                self.assertEqual(result.returncode, 2)
                self.assertNotIn(b"census on", result.stdout)


if __name__ == "__main__":
    unittest.main()
