"""Short-handle client contracts; the server remains the only reference resolver."""
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
import urllib.parse

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
from runner_transport import ClientError
import runner_test_results
import runner_tests


class Api:
    def __init__(self, short=True, revision="a" * 64):
        self.calls = []
        self.short = short
        self.revision = revision

    def json(self, path, method="GET", body=None):
        self.calls.append((path, method, body))
        if path.startswith("/api/v1/help"):
            return {"capabilities": ["requestedTests", "executableHashResults"] + (["shortReferences"] if self.short else [])}
        if path.startswith("/api/v1/test-configs/"):
            return {"revision": self.revision}
        return {"path": path}


class ShortReferenceClientChecks(unittest.TestCase):
    def test_explicitly_advertised_short_baseline_is_forwarded(self):
        api = Api()
        runner_test_results.execute(api, SimpleNamespace(command="baseline", sha256="A" * 12))
        self.assertEqual(("/api/v1/baseline", "PUT", {"sha256": "a" * 12}), api.calls[-1])

    def test_old_server_is_not_sent_a_short_baseline(self):
        api = Api(short=False)
        with self.assertRaises(ClientError):
            runner_test_results.execute(api, SimpleNamespace(command="baseline", sha256="a" * 12))
        self.assertEqual(1, len(api.calls))

    def test_short_comparison_is_server_computed(self):
        api = Api()
        runner_test_results.execute(api, SimpleNamespace(command="compare", a="A" * 12, b="B" * 12,
                                                        format="json", json=True, out=None))
        query = urllib.parse.parse_qs(urllib.parse.urlsplit(api.calls[-1][0]).query)
        self.assertEqual(["a" * 12], query["A"])
        self.assertEqual(["b" * 12], query["B"])
        self.assertTrue(all(method == "GET" for _, method, _ in api.calls))

    def test_test_selection_resolves_full_identity_before_request(self):
        api = Api()
        selected = runner_tests.selection(api, ["smoke@" + "A" * 12], allow_short=True)
        self.assertEqual([("smoke", "a" * 64)], selected)
        self.assertEqual("/api/v1/test-configs/smoke/" + "a" * 12, api.calls[-1][0])
        self.assertTrue(all(method == "GET" for _, method, _ in api.calls))

    def test_mismatching_resolver_response_is_not_used(self):
        with self.assertRaises(ClientError):
            runner_tests.selection(Api(revision="b" * 64), ["smoke@" + "a" * 12], allow_short=True)

    def test_full_references_keep_legacy_client_support(self):
        api = Api(short=False)
        runner_test_results.execute(api, SimpleNamespace(command="baseline", sha256="A" * 64))
        self.assertEqual({"sha256": "a" * 64}, api.calls[-1][2])
        self.assertEqual([("smoke", "a" * 64)], runner_tests.selection(api, ["smoke@" + "A" * 64]))


if __name__ == "__main__":
    unittest.main()
