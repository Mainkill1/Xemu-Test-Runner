import copy
import importlib.util
import json
from pathlib import Path
import unittest


ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location(
    "check_game_test_record", ROOT / "scripts" / "check-game-test-record.py")
CHECKER = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(CHECKER)


class GameTestRecordChecks(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.record = json.loads((
            ROOT / "docs/game-testing/game-test-record.example.json").read_text())
        cls.request = json.loads((
            ROOT / "docs/game-testing/release-game-measurement.request.example.json"
        ).read_text())

    def test_reference_record_and_request_match(self):
        CHECKER.validate_record(copy.deepcopy(self.record))
        CHECKER.validate_request(copy.deepcopy(self.record),
                                 copy.deepcopy(self.request))

    def test_rejects_hdd_from_another_bios_key_domain(self):
        record = copy.deepcopy(self.record)
        record["identity"]["hdd"]["biosSha256"] = "1" * 64
        with self.assertRaisesRegex(ValueError, "HDD BIOS key domain"):
            CHECKER.validate_record(record)

    def test_rejects_instrumented_release_acceptance(self):
        record = copy.deepcopy(self.record)
        record["measurement"]["instrumented"] = True
        with self.assertRaisesRegex(ValueError, "cannot be instrumented"):
            CHECKER.validate_record(record)

    def test_rejects_segment_without_immediate_scene_gate(self):
        request = copy.deepcopy(self.request)
        plan = request["job"]["plan"]
        start = next(index for index, step in enumerate(plan)
                     if step["type"] == "segment_start")
        plan.insert(start, {"type": "wait", "delayMs": 1})
        with self.assertRaisesRegex(ValueError, "immediately follow"):
            CHECKER.validate_request(copy.deepcopy(self.record), request)

    def test_rejects_media_pin_drift(self):
        request = copy.deepcopy(self.request)
        assets = request["job"]["runtimeState"]["isolation"]["readOnlyAssets"]
        next(item for item in assets if item["field"] == "dvd_path")[
            "expectedSha256"] = "2" * 64
        with self.assertRaisesRegex(ValueError, "dvd_path hash"):
            CHECKER.validate_request(copy.deepcopy(self.record), request)


if __name__ == "__main__":
    unittest.main()
