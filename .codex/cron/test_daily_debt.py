"""Exercise the real wrapper with disposable Git repos and fake external tools."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import time
import unittest

SOURCE = Path(__file__).resolve().parents[1]


class DailyDebtTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="humans-debt-test-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.clone = self.root / "clone"
        self.calls = self.root / "calls.jsonl"
        self.clock = self.root / "clock"
        self.clock.write_text(str(int(time.time()) - 90))
        seed = self.root / "seed"
        seed.mkdir()
        (seed / ".github").mkdir()
        (seed / ".github/pull_request_template.md").write_text("UNFILLED TEMPLATE\n- [ ] placeholder\n")
        shutil.copytree(SOURCE / "cron", seed / ".codex/cron")
        shutil.copytree(SOURCE / "prompts", seed / ".codex/prompts")
        self.git(seed, "init", "-b", "main")
        self.git(seed, "config", "user.name", "Runner test")
        self.git(seed, "config", "user.email", "runner@example.invalid")
        self.git(seed, "add", ".")
        self.git(seed, "commit", "-qm", "Fixture")
        self.remote = self.root / "remote.git"
        self.git(self.root, "clone", "--bare", str(seed), str(self.remote))
        self.git(self.root, "clone", str(self.remote), str(self.clone))
        self.git(self.clone, "config", "user.name", "Runner test")
        self.git(self.clone, "config", "user.email", "runner@example.invalid")
        (self.clone / ".codex-runner-clone").touch()
        # The production marker/config are ignored; keep this fixture identical.
        with (self.clone / ".git/info/exclude").open("a") as f:
            f.write("\n.codex-runner-clone\n.codex/cron/debt-runner.env\n")
        binary = self.root / "bin"
        binary.mkdir()
        script = binary / "stub"
        script.write_text('''#!/usr/bin/env python3
import json, os, pathlib, subprocess, sys, time
name = pathlib.Path(sys.argv[0]).name
args = sys.argv[1:]
root = pathlib.Path(os.environ["FIXTURE"])
scenario = os.environ["SCENARIO"]
if name == "date":
    if args == ["-u", "+%s"]:
        print((root / "clock").read_text())
    else:
        os.execv("/bin/date", ["date", *args])
    sys.exit()
if "--version" in args:
    print(name + " fixture")
    sys.exit()
with (root / "calls.jsonl").open("a") as f:
    f.write(json.dumps([name, *args]) + "\\n")
if name == "gh":
    if args[:2] == ["pr", "create"]:
        body = pathlib.Path(args[args.index("--body-file") + 1]).read_text()
        (root / "pr-body").write_text(body)
        print("https://example.invalid/pull/1")
    sys.exit()
if name == "dotnet":
    assert os.environ["VSTestTestCaseFilter"] == "FullyQualifiedName!~Humans.Integration.Tests"
    if args[0] == "test":
        assert args[args.index("--filter") + 1] == os.environ["VSTestTestCaseFilter"]
    sys.exit(1 if scenario == args[0] + "-failure" else 0)
if args[:2] == ["login", "status"]:
    sys.exit()
assert args[:2] == ["app-server", "--stdio"]
assert args[args.index("--enable") + 1] == "goals"
thread = "fixture-thread"
started = 0
objective = None

def emit(method, params):
    print(json.dumps({"method": method, "params": dict(threadId=thread, **params)}), flush=True)

def commit(turn):
    path = pathlib.Path("src/Sections/Fixture/Docs/debt.yml" if scenario == "ledger" else "src/Fix" + str(turn) + ".cs")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(str(turn))
    subprocess.run(["git", "add", str(path)], check=True, stdout=sys.stderr)
    subprocess.run(["git", "commit", "-qm", "Fix " + str(turn)], check=True, stdout=sys.stderr)
    if scenario == "dirty":
        path.write_text("unfinished")

for line in sys.stdin:
    request = json.loads(line)
    method = request.get("method")
    params = request.get("params", {})
    with (root / "requests.jsonl").open("a") as f:
        f.write(json.dumps(request) + "\\n")
    if method == "initialized":
        continue
    if method == "initialize":
        result = {}
    elif method == "thread/start":
        assert params["sandbox"] == "danger-full-access"
        assert params["approvalPolicy"] == "never"
        result = {"thread": {"id": thread}}
    elif method == "thread/goal/set":
        assert "tokenBudget" not in params
        assert "ONLY target" in params["objective"]
        if objective is None:
            objective = params["objective"]
        else:
            assert params["objective"] == objective
            assert params["status"] == "active"
            assert params["threadId"] == thread
        result = {"goal": {"status": "active"}}
    elif method == "turn/start":
        started += 1
        if started > 1:
            assert scenario.startswith("early-")
            assert params["threadId"] == thread
            assert "deadline" in params["input"][0]["text"]
            assert not (root / "pr-body").exists()
            print(json.dumps({"id": request["id"], "result": {}}), flush=True)
            emit("turn/started", {"turn": {"id": "resumed"}})
            if scenario == "early-repeated" and started == 2:
                emit("thread/goal/updated", {"goal": {"status": "complete"}})
                emit("turn/completed", {"turn": {"status": "completed"}})
                continue
            deadline = int((root / "clock").read_text()) + 3
            time.sleep(max(0, deadline - time.time()) + 0.05)
            commit(3)
            (root / "clock").write_text(str(deadline + 1))
            emit("thread/goal/updated", {"goal": {"status": "complete"}})
            emit("item/completed", {"item": {"type": "agentMessage", "phase": "final_answer", "text": "Cumulative fixes: 3"}})
            emit("turn/completed", {"turn": {"status": "completed"}})
            continue
        prompt = params["input"][0]["text"]
        assert "__WORK_" not in prompt
        assert "get_goal" in prompt
        assert "Time is the only target" in prompt
        result = {}
    else:
        raise AssertionError(method)
    print(json.dumps({"id": request["id"], "result": result}), flush=True)
    if method != "turn/start":
        continue
    commit(1)
    emit("turn/started", {"turn": {"id": "first"}})
    emit("thread/goal/updated", {"goal": {"status": "active"}})
    emit("item/completed", {"item": {"type": "agentMessage", "phase": "final_answer", "text": "First fix done; goal active"}})
    emit("turn/completed", {"turn": {"status": "completed"}})
    # Simulate Codex's own next turn, without reading any new user request.
    assert not (root / "pr-body").exists()
    emit("turn/started", {"turn": {"id": "second"}})
    if scenario == "codex-failure":
        sys.exit(17)
    commit(2)
    if not scenario.startswith("early-"):
        (root / "clock").write_text(str(int((root / "clock").read_text()) + 91))
    emit("thread/goal/updated", {"goal": {"status": "blocked" if scenario == "blocked" else "complete"}})
    if scenario != "missing-report":
        emit("item/completed", {"item": {"type": "agentMessage", "phase": "final_answer", "text": "Cumulative fixes: 2"}})
    if scenario == "early-deadline-race":
        time.sleep(max(0, int((root / "clock").read_text()) + 3 - time.time()) + 0.05)
    emit("turn/completed", {"turn": {"status": "completed"}})
''')
        script.chmod(0o755)
        for name in ("codex", "gh", "dotnet", "date"):
            (binary / name).symlink_to(script)
        self.env = dict(os.environ, PATH=f"{binary}:{os.environ['PATH']}",
                        FIXTURE=str(self.root), REPO_URL=str(self.remote),
                        WORK_DIR=str(self.clone), LOG_DIR=str(self.root / "logs"),
                        TIME_BUDGET="90s", CODEX_DANGEROUS="1",
                        GATE_REPAIR_ATTEMPTS="0")

    @staticmethod
    def git(cwd, *args):
        return subprocess.run(["git", *args], cwd=cwd, check=True,
                              capture_output=True, text=True).stdout

    def run_scenario(self, scenario):
        result = subprocess.run(["bash", str(self.clone / ".codex/cron/run-daily-debt.sh")],
                                cwd=self.clone, env=dict(self.env, SCENARIO=scenario),
                                capture_output=True, text=True, timeout=20)
        calls = [json.loads(x) for x in self.calls.read_text().splitlines()]
        published = [x for x in calls if x[:3] == ["gh", "pr", "create"]]
        return result, calls, published

    def test_native_goal_continues_in_one_session_and_publishes_once(self):
        result, calls, published = self.run_scenario("complete")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(len(published), 1)
        requests = [json.loads(x) for x in (self.root / "requests.jsonl").read_text().splitlines()]
        self.assertEqual(sum(x["method"] == "turn/start" for x in requests), 1)
        self.assertEqual(sum(x["method"] == "thread/start" for x in requests), 1)
        self.assertEqual(sum(x[:2] == ["codex", "app-server"] for x in calls), 1)
        body = (self.root / "pr-body").read_text()
        self.assertIn("Cumulative fixes: 2", body)
        self.assertIn("Runner validation:", body)
        self.assertIn("Goal time: 90s; actual worker time: 1m 31s; total run through validation: 1m 31s.", body)
        self.assertNotIn("UNFILLED TEMPLATE", body)
        self.assertNotIn("- [ ]", body)
        self.assertEqual([x[1] for x in calls if x[0] == "dotnet"], ["build", "test"])
        self.assertEqual(len(self.git(self.clone, "log", "--oneline", "origin/main..HEAD").splitlines()), 2)

    def test_early_completion_resumes_same_goal_until_deadline(self):
        for scenario in ("early-complete", "early-repeated", "early-deadline-race"):
            with self.subTest(scenario=scenario):
                if scenario != "early-complete":
                    self.setUp()
                self.clock.write_text(str(int(time.time())))
                self.env["TIME_BUDGET"] = "3s"
                result, calls, published = self.run_scenario(scenario)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertEqual(len(published), 1)
                requests = [json.loads(x) for x in (self.root / "requests.jsonl").read_text().splitlines()]
                self.assertEqual(sum(x["method"] == "thread/start" for x in requests), 1)
                self.assertEqual(sum(x[:2] == ["codex", "app-server"] for x in calls), 1)
                goals = [x["params"] for x in requests if x["method"] == "thread/goal/set"]
                self.assertEqual(len(goals), 3 if scenario == "early-repeated" else 2)
                self.assertTrue(all(g["objective"] == goals[0]["objective"] for g in goals))
                ids = [x["id"] for x in requests if "id" in x]
                self.assertEqual(len(ids), len(set(ids)))
                self.assertIn("Cumulative fixes: 3", (self.root / "pr-body").read_text())
                self.assertEqual(len(self.git(self.clone, "log", "--oneline", "origin/main..HEAD").splitlines()), 3)

    def test_ledger_only_does_not_publish_or_run_dotnet(self):
        result, calls, published = self.run_scenario("ledger")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("exit_reason=no-substantive-fixes", result.stdout)
        self.assertEqual(published, [])
        self.assertFalse(any(x[0] == "dotnet" for x in calls))

    def test_failures_never_publish(self):
        for scenario in ("codex-failure", "missing-report", "blocked", "dirty", "build-failure", "test-failure"):
            with self.subTest(scenario=scenario):
                # Each scenario needs a fresh clone and log history.
                if scenario != "codex-failure":
                    self.setUp()
                result, _, published = self.run_scenario(scenario)
                self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
                if scenario in ("build-failure", "test-failure"):
                    self.assertEqual(len(published), 1)
                    self.assertIn("--draft", published[0])
                else:
                    self.assertEqual(published, [])


if __name__ == "__main__":
    unittest.main()
