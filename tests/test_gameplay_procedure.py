"""Contract checks for the shared parked PGR2 benchmark procedure."""

import importlib.util
from pathlib import Path
import sys
import unittest


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "scripts"))
SPEC = importlib.util.spec_from_file_location(
    "gameplay_procedure", ROOT / "scripts" / "gameplay_procedure.py")
assert SPEC and SPEC.loader
module = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(module)
FIXTURE = ROOT / "procedures" / "pgr2-parked-v1.json"


class ParkedProcedureChecks(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.contract = module.load_contract(FIXTURE)

    def job(self):
        prefix = [dict(step) for step in self.contract["planPrefix"]]
        return {
            "requireInput": True,
            "controllerInput": {"backend": "native-os-gamepad"},
            "plan": [
                {"type": "require_input"}, *prefix,
                {"type": "screenshot", "name": "recording-start"},
                {"type": "wait", "delayMs": 30000},
                {"type": "screenshot", "name": "recording-end"},
                {"type": "segment_end", "name": "stationary-start"},
                {"type": "quit"},
            ],
            "workload": {"analysis": {"segment": "stationary-start",
                                       "guestFramesPath": "guest-frames.log",
                                       "minimumFrameSamples": 160}},
        }

    def test_current_parked_course_matches_and_has_stable_identity(self):
        result = module.assess(self.job(), self.contract)
        self.assertTrue(result["ok"], result["errors"])
        self.assertEqual(len(result["procedureSha256"]), 64)
        self.assertEqual(result["buttonCount"], 11)

    def test_moving_route_cannot_masquerade_as_parked(self):
        job = self.job()
        job["plan"].insert(-2, {"type": "button", "button": "RTrigger",
                                "durationMs": 30000})
        self.assertFalse(module.assess(job, self.contract)["ok"])

    def test_early_menu_input_is_rejected(self):
        job = self.job()
        job["plan"][1]["delayMs"] = 15000
        self.assertFalse(module.assess(job, self.contract)["ok"])

    def test_long_right_press_is_rejected(self):
        job = self.job()
        right = next(step for step in job["plan"] if step.get("button") == "LStickRight")
        right["durationMs"] = 500
        self.assertFalse(module.assess(job, self.contract)["ok"])

    def test_measurement_must_start_after_final_input(self):
        job = self.job()
        start = next(i for i, step in enumerate(job["plan"])
                     if step.get("type") == "segment_start")
        job["plan"].insert(start, {"type": "wait", "delayMs": 2000})
        self.assertFalse(module.assess(job, self.contract)["ok"])

    def test_insufficient_frame_evidence_is_rejected(self):
        job = self.job()
        job["workload"]["analysis"]["minimumFrameSamples"] = 0
        self.assertFalse(module.assess(job, self.contract)["ok"])

    def test_missing_frame_log_or_scene_screenshot_is_rejected(self):
        job = self.job()
        job["workload"]["analysis"]["guestFramesPath"] = None
        self.assertFalse(module.assess(job, self.contract)["ok"])
        job = self.job()
        job["plan"] = [step for step in job["plan"]
                       if step.get("name") != "recording-start"]
        self.assertFalse(module.assess(job, self.contract)["ok"])


if __name__ == "__main__":
    unittest.main()
