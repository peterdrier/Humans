"""Exercise the real wrapper with disposable Git repos and fake external tools."""
import json
import importlib.util
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import time
import unittest

SOURCE = Path(__file__).resolve().parents[1]


spec = importlib.util.spec_from_file_location("run_goal", SOURCE / "cron/run-goal.py")
run_goal = importlib.util.module_from_spec(spec)
spec.loader.exec_module(run_goal)


class QuotaPolicyTests(unittest.TestCase):
    def snapshot(self, used, days_left=5.5, secondary=False):
        week = {"usedPercent": used, "windowDurationMins": 10080,
                "resetsAt": 1000000 + days_left * 86400}
        return {"rateLimitsByLimitId": {"codex": {
            "primary": {"usedPercent": 0, "windowDurationMins": 300} if secondary else week,
            "secondary": week if secondary else None}}}

    def test_one_and_half_days_allows_thirty_percent_exactly(self):
        for secondary in (False, True):
            self.assertIsNone(run_goal.quota_stop_reason(self.snapshot(30, secondary=secondary), 1000000))
            self.assertIn("cutoff", run_goal.quota_stop_reason(self.snapshot(31, secondary=secondary), 1000000))

    def test_window_start_and_end_and_reset_rollover(self):
        self.assertIsNone(run_goal.quota_stop_reason(self.snapshot(0, 7), 1000000))
        self.assertIsNone(run_goal.quota_stop_reason(self.snapshot(12, 7), 1000000))
        self.assertIn("cutoff", run_goal.quota_stop_reason(self.snapshot(13, 7), 1000000))
        self.assertIsNone(run_goal.quota_stop_reason(self.snapshot(83, 0.01), 1000000))
        self.assertIn("cutoff", run_goal.quota_stop_reason(self.snapshot(85, 0.01), 1000000))
        self.assertIn("stale", run_goal.quota_stop_reason(self.snapshot(0, 0), 1000000))

    def test_reported_post_reset_stop_gets_daily_allowance(self):
        self.assertIsNone(run_goal.quota_stop_reason(self.snapshot(1, 6.9925), 1000000))

    def test_cutoff_stays_fixed_until_weekly_reset(self):
        policy = {}
        self.assertIsNone(run_goal.quota_stop_reason(self.snapshot(12, 7), 1000000, policy))
        # Advancing half a day must not release more quota during this run.
        self.assertIn("cutoff", run_goal.quota_stop_reason(
            self.snapshot(13, 7), 1000000 + 43200, policy))
        self.assertIsNone(run_goal.quota_stop_reason(
            self.snapshot(12, 7), 1000000 + 43200, policy))

    def test_weekly_reset_replaces_previous_runs_cutoff(self):
        policy = {}
        self.assertIsNone(run_goal.quota_stop_reason(self.snapshot(30), 1000000, policy))
        self.assertIsNone(run_goal.quota_stop_reason(
            self.snapshot(12, 12.5), 1000000 + 5.5 * 86400, policy))
        self.assertIn("cutoff", run_goal.quota_stop_reason(
            self.snapshot(13, 12.5), 1000000 + 5.5 * 86400, policy))

    def test_new_run_releases_next_day_and_keeps_weekly_reserve(self):
        self.assertIsNone(run_goal.quota_stop_reason(self.snapshot(24, 6), 1000000, {}))
        self.assertIn("cutoff", run_goal.quota_stop_reason(self.snapshot(25, 6), 1000000, {}))
        self.assertIsNone(run_goal.quota_stop_reason(self.snapshot(84, 1), 1000000, {}))
        self.assertIn("cutoff", run_goal.quota_stop_reason(self.snapshot(85, 1), 1000000, {}))

    def test_missing_malformed_and_other_exhausted_window(self):
        self.assertIn("unavailable", run_goal.quota_stop_reason({}, 1000000))
        for used in (None, "17", True, -1, 101, float("nan")):
            self.assertIn("invalid", run_goal.quota_stop_reason(self.snapshot(used), 1000000))
        snapshot = self.snapshot(0, secondary=True)
        snapshot["rateLimitsByLimitId"]["codex"]["primary"]["usedPercent"] = 100
        self.assertIn("exhausted", run_goal.quota_stop_reason(snapshot, 1000000))


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
        (seed / "docs/architecture").mkdir(parents=True)
        (seed / "docs/architecture/debt-ledger.yml").write_text(
            "inbox:\n  - id: CENTRAL-1\n  - id: CENTRAL-2\nthemes:\n  - id: some-theme\n")
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
        assert len(body.encode("utf-8")) <= 65536, "GitHub PR body limit exceeded"
        if scenario == "pr-create-failure":
            sys.exit(1)
        print("https://example.invalid/pull/1")
    if args[:2] == ["pr", "comment"]:
        (root / "pr-comment").write_text(pathlib.Path(args[args.index("--body-file") + 1]).read_text())
        if scenario == "comment-failure":
            sys.exit(1)
    sys.exit()
if name == "dotnet":
    assert os.environ["VSTestTestCaseFilter"] == "FullyQualifiedName!~Humans.Integration.Tests"
    if args[0] == "test":
        assert args[args.index("--filter") + 1] == os.environ["VSTestTestCaseFilter"]
    failed = scenario == args[0] + "-failure" or (
        scenario == "test-build-regression" and (
            (args[0] == "test" and not (root / "repaired").exists()) or
            (args[0] == "build" and (root / "repaired").exists()))) or (
        args[0] == "build" and (
            (scenario in ("build-repair", "giant-build-repair", "repair-dirty") and not (root / "repaired").exists()) or
            scenario == "giant-build-failure" or
            (scenario == "build-second-repair" and int((root / "sessions-started").read_text()) < 3)))
    if failed:
        print("Fixture.cs(1,1): error CS1234: repair this compilation failure")
        if scenario.startswith("giant-"):
            print("Long diagnostic starts: " + "x" * 1200000 + " :diagnostic ends")
    sys.exit(1 if failed else 0)
if args[:2] == ["login", "status"]:
    sys.exit()
assert args[:2] == ["app-server", "--stdio"]
assert args[args.index("--enable") + 1] == "goals"
helper_config = [args[i + 1] for i, arg in enumerate(args) if arg == "-c"]
assert "agents.enabled=true" in helper_config
assert 'agents.default_subagent_model="gpt-6-luna"' in helper_config
assert 'agents.default_subagent_reasoning_effort="medium"' in helper_config
count_file = root / "sessions-started"
count = int(count_file.read_text()) if count_file.exists() else 0
count_file.write_text(str(count + 1))
thread = "fixture-thread" if count == 0 else "fixture-repair" + (str(count) if count > 1 else "")
started = 0
quota_reads = 0
weekly_reset = time.time() + 5.5 * 86400
objective = None

def emit(method, params):
    print(json.dumps({"method": method, "params": dict(threadId=thread, **params)}), flush=True)

def commit(turn):
    path = pathlib.Path("src/Sections/Fixture/Docs/debt.yml" if scenario == "ledger" else "src/Fix" + str(turn) + ".cs")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(str(turn))
    subprocess.run(["git", "add", str(path)], check=True, stdout=sys.stderr)
    if turn == 1 and scenario not in ("ledger", "ledger-grows"):
        ledger = pathlib.Path("docs/architecture/debt-ledger.yml")
        ledger.write_text(ledger.read_text().replace("  - id: CENTRAL-2\\n", ""))
        subprocess.run(["git", "add", str(ledger)], check=True, stdout=sys.stderr)
    subprocess.run(["git", "commit", "-qm", "Fix " + str(turn)], check=True, stdout=sys.stderr)
    if scenario in ("dirty", "blocked-dirty"):
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
    elif method == "account/rateLimits/read":
        quota_reads += 1
        if scenario == "quota-error" or (scenario == "quota-error-after-round" and quota_reads > 1):
            print(json.dumps({"id": request["id"], "error": {"code": -32000, "message": "fixture quota unavailable"}}), flush=True)
            continue
        used = 31 if scenario == "quota-skip" or quota_reads > (2 if scenario == "quota-continue" else 1) else 0
        result = {"rateLimits": {"primary": {"usedPercent": used, "windowDurationMins": 10080,
                  "resetsAt": weekly_reset}}}
    elif method == "thread/start":
        assert params["sandbox"] == "danger-full-access"
        assert params["approvalPolicy"] == "never"
        result = {"thread": {"id": thread}}
    elif method == "thread/goal/set":
        assert "tokenBudget" not in params
        assert ("cap, not a target" in params["objective"]) == (thread.startswith("fixture-repair"))
        assert ("one coherent fix or batch per round" in params["objective"]) == (not thread.startswith("fixture-repair"))
        if objective is None:
            objective = params["objective"]
        else:
            assert params["objective"] == objective
            assert params["status"] == "active"
            assert params["threadId"] == thread
        result = {"goal": {"status": "active"}}
    elif method == "turn/start":
        started += 1
        if scenario.startswith("quota-"):
            print(json.dumps({"id": request["id"], "result": {}}), flush=True)
            emit("turn/started", {"turn": {"id": str(started)}})
            commit(started)
            emit("thread/goal/updated", {"goal": {"status": "complete"}})
            emit("item/completed", {"item": {"type": "agentMessage", "phase": "final_answer", "text": "Cumulative fixes: " + str(started)}})
            emit("turn/completed", {"turn": {"status": "completed"}})
            continue
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
        if thread.startswith("fixture-repair"):
            assert len(prompt.encode("utf-8")) < 30000
            assert "error CS1234: repair this compilation failure" in prompt
            assert "event-payload" not in prompt
            print(json.dumps({"id": request["id"], "result": {}}), flush=True)
            time.sleep(2)
            commit(3 + count)
            if scenario == "repair-dirty":
                pathlib.Path("src/Fix" + str(3 + count) + ".cs").write_text("unfinished repair")
            (root / "repaired").touch()
            (root / "clock").write_text(str(int(time.time()) + 2))
            emit("thread/goal/updated", {"goal": {"status": "complete"}})
            emit("item/completed", {"item": {"type": "agentMessage", "phase": "final_answer", "text": "Gate repaired"}})
            emit("turn/completed", {"turn": {"status": "completed"}})
            continue
        assert "__WORK_" not in prompt
        assert "get_goal" in prompt
        assert "wrapper checks live account quota" in prompt
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
    emit("thread/goal/updated", {"goal": {"status": "blocked" if scenario.startswith("blocked") else "complete"}})
    if scenario.startswith("giant-"):
        emit("item/completed", {"item": {"type": "commandExecution", "aggregatedOutput": "event-payload" * 100000}})
    if scenario != "missing-report":
        report = "Cumulative fixes: 2"
        if scenario in ("oversized-report", "giant-build-failure"):
            report += "\\n" + "é🐱" * 100000
        emit("item/completed", {"item": {"type": "agentMessage", "phase": "final_answer", "text": report}})
    if scenario == "early-deadline-race":
        time.sleep(max(0, int((root / "clock").read_text()) + 3 - time.time()) + 0.05)
    emit("turn/completed", {"turn": {"status": "completed"}})
''')
        script.chmod(0o755)
        for name in ("codex", "gh", "dotnet", "date"):
            (binary / name).symlink_to(script)
        calculator = self.root / "spend.py"
        calculator.write_text("import sys\nprint('Agent cost for ' + sys.argv[1])\n")
        self.env = dict(os.environ, SPEND_SCRIPT=str(calculator), PATH=f"{binary}:{os.environ['PATH']}",
                        FIXTURE=str(self.root), REPO_URL=str(self.remote),
                        WORK_DIR=str(self.clone), LOG_DIR=str(self.root / "logs"),
                        TIME_BUDGET="90s", CODEX_DANGEROUS="1",
                        GATE_REPAIR_ATTEMPTS="0", IGNORE_QUOTA_CUTOFF="1")

    @staticmethod
    def git(cwd, *args):
        return subprocess.run(["git", *args], cwd=cwd, check=True,
                              capture_output=True, text=True).stdout

    def run_scenario(self, scenario, runner=None):
        result = subprocess.run(["bash", str(runner or self.clone / ".codex/cron/run-daily-debt.sh")],
                                cwd=self.clone, env=dict(self.env, SCENARIO=scenario),
                                capture_output=True, text=True, timeout=20)
        calls = [json.loads(x) for x in self.calls.read_text().splitlines()] if self.calls.exists() else []
        published = [x for x in calls if x[:3] == ["gh", "pr", "create"]]
        return result, calls, published

    def test_quota_stop_publishes_committed_work_before_deadline(self):
        self.env["IGNORE_QUOTA_CUTOFF"] = "0"
        self.clock.write_text(str(int(time.time())))
        result, calls, published = self.run_scenario("quota-stop")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(len(published), 1)
        self.assertEqual([x[1] for x in calls if x[0] == "dotnet"], ["build", "test"])
        body = (self.root / "pr-body").read_text()
        self.assertIn("Cumulative fixes: 1", body)
        self.assertIn("weekly quota cutoff", body)
        self.assertNotIn("--draft", published[0])
        self.assertEqual(len(self.git(self.clone, "log", "--oneline", "origin/main..HEAD").splitlines()), 1)

    def test_quota_allows_next_round_in_same_thread_then_stops(self):
        self.env["IGNORE_QUOTA_CUTOFF"] = "0"
        self.clock.write_text(str(int(time.time())))
        result, _, published = self.run_scenario("quota-continue")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(len(published), 1)
        requests = [json.loads(x) for x in (self.root / "requests.jsonl").read_text().splitlines()]
        self.assertEqual(sum(x["method"] == "thread/start" for x in requests), 1)
        self.assertEqual(sum(x["method"] == "turn/start" for x in requests), 2)
        self.assertEqual(sum(x["method"] == "account/rateLimits/read" for x in requests), 3)
        self.assertIn("Cumulative fixes: 2", (self.root / "pr-body").read_text())

    def test_quota_unavailable_after_round_still_validates_and_publishes(self):
        self.env["IGNORE_QUOTA_CUTOFF"] = "0"
        self.clock.write_text(str(int(time.time())))
        result, calls, published = self.run_scenario("quota-error-after-round")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(len(published), 1)
        self.assertEqual([x[1] for x in calls if x[0] == "dotnet"], ["build", "test"])
        self.assertIn("weekly quota read failed", (self.root / "pr-body").read_text())

    def test_quota_cutoff_or_read_error_before_work_skips_without_publication(self):
        for scenario in ("quota-skip", "quota-error"):
            with self.subTest(scenario=scenario):
                if scenario != "quota-skip":
                    self.setUp()
                self.env["IGNORE_QUOTA_CUTOFF"] = "0"
                result, calls, published = self.run_scenario(scenario)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertEqual(published, [])
                self.assertFalse(any(x[0] == "dotnet" for x in calls))
                self.assertIn("exit_reason=skip-quota", result.stdout)
                requests = [json.loads(x) for x in (self.root / "requests.jsonl").read_text().splitlines()]
                self.assertFalse(any(x["method"] == "thread/start" for x in requests))

    def test_manual_override_does_not_query_quota(self):
        result, _, published = self.run_scenario("complete")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(len(published), 1)
        requests = [json.loads(x) for x in (self.root / "requests.jsonl").read_text().splitlines()]
        self.assertFalse(any(x["method"] == "account/rateLimits/read" for x in requests))

    def test_manual_environment_overrides_config_file(self):
        (self.clone / ".codex/cron/debt-runner.env").write_text(
            "TIME_BUDGET=13h\nIGNORE_QUOTA_CUTOFF=0\n")
        result, _, published = self.run_scenario("complete")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(len(published), 1)
        requests = [json.loads(x) for x in (self.root / "requests.jsonl").read_text().splitlines()]
        self.assertFalse(any(x["method"] == "account/rateLimits/read" for x in requests))

    def test_pinned_release_keeps_matching_client_and_prompt_after_refresh(self):
        release = self.root / "release/.codex"
        shutil.copytree(SOURCE / "cron", release / "cron")
        shutil.copytree(SOURCE / "prompts", release / "prompts")
        (self.clone / ".codex/cron/run-goal.py").write_text("raise RuntimeError('stale client')\n")
        (self.clone / ".codex/prompts/daily-debt.md").write_text("stale prompt\n")
        self.git(self.clone, "add", ".codex")
        self.git(self.clone, "commit", "-qm", "Old runner on remote")
        self.git(self.clone, "push", "origin", "main")
        result, _, published = self.run_scenario("complete", release / "cron/run-daily-debt.sh")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(len(published), 1)

    def test_time_cap_rejects_overrides_above_twelve_hours(self):
        self.env["TIME_BUDGET"] = "13h"
        result, calls, published = self.run_scenario("complete")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("cannot exceed 12h", result.stdout)
        self.assertFalse(published)
        self.assertFalse(any(x[:2] == ["codex", "app-server"] for x in calls))

    def test_native_goal_continues_in_one_session_and_publishes_once(self):
        self.env["CODEX_THREAD_ID"] = "unrelated-reporting-chat"
        self.env.pop("CODEX_MODEL", None)
        self.env.pop("CODEX_EFFORT", None)
        result, calls, published = self.run_scenario("complete")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(len(published), 1)
        self.assertIn("Agent cost for fixture-thread", (self.root / "pr-comment").read_text())
        self.assertEqual(sum(x[:3] == ["gh", "pr", "comment"] for x in calls), 1)
        requests = [json.loads(x) for x in (self.root / "requests.jsonl").read_text().splitlines()]
        self.assertEqual(sum(x["method"] == "turn/start" for x in requests), 1)
        self.assertEqual(sum(x["method"] == "thread/start" for x in requests), 1)
        thread = next(x["params"] for x in requests if x["method"] == "thread/start")
        self.assertEqual(thread["model"], "gpt-6.1-sol")
        self.assertEqual(thread["config"]["model_reasoning_effort"], "medium")
        self.assertEqual(sum(x[:2] == ["codex", "app-server"] for x in calls), 1)
        body = (self.root / "pr-body").read_text()
        self.assertIn("Cumulative fixes: 2", body)
        self.assertIn("Runner validation:", body)
        self.assertIn("Open rows: 2 → 1 (net -1).", body)
        self.assertNotIn("--draft", published[0])
        self.assertIn("Goal time: 90s; actual worker time: 1m 31s; total run through validation: 1m 31s.", body)
        self.assertNotIn("UNFILLED TEMPLATE", body)
        self.assertNotIn("- [ ]", body)
        self.assertEqual([x[1] for x in calls if x[0] == "dotnet"], ["build", "test"])
        self.assertEqual(len(self.git(self.clone, "log", "--oneline", "origin/main..HEAD").splitlines()), 2)

    def test_giant_events_and_diagnostics_do_not_block_gate_repair(self):
        self.env.update(GATE_REPAIR_ATTEMPTS="1", GATE_REPAIR_BUDGET="30s")
        result, calls, published = self.run_scenario("giant-build-repair")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(len(published), 1)
        self.assertNotIn("--draft", published[0])
        self.assertEqual([x[1] for x in calls if x[0] == "dotnet"], ["build", "build", "test"])
        prompt = next((self.root / "logs").glob("repair-prompt-*.md")).read_text()
        self.assertIn("diagnostic ends", prompt)
        self.assertIn("[line shortened]", prompt)
        self.assertNotIn("event-payload", prompt)

    def test_failed_gate_publishes_bounded_draft_even_with_giant_report_and_events(self):
        result, _, published = self.run_scenario("giant-build-failure")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(len(published), 1, result.stdout + result.stderr)
        self.assertIn("--draft", published[0])
        body = (self.root / "pr-body").read_text()
        self.assertLessEqual(len(body.encode("utf-8")), 60000)
        self.assertIn("## Automated gate warning", body)
        self.assertIn("- Build gate: fail", body)
        self.assertIn("- Test gate: skipped", body)
        self.assertIn("error CS1234", body)
        self.assertNotIn("event-payload", body)
        self.assertIn("Runner validation: build fail, tests skipped", body)
        self.assertIn("exit_reason=pushed-draft-build-failed", result.stdout)

    def test_oversized_green_report_still_publishes(self):
        result, _, published = self.run_scenario("oversized-report")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(len(published), 1)
        self.assertIn("Report shortened for GitHub", (self.root / "pr-body").read_text())
        self.assertIn("Runner validation: build pass, tests pass", (self.root / "pr-body").read_text())

    def test_recovered_oversized_report_preserves_warning_from_omitted_middle(self):
        result, _, _ = self.run_scenario("pr-create-failure")
        self.assertNotEqual(result.returncode, 0)
        report = next((self.root / "logs").glob("last-message-*.md"))
        report.write_text("Report start\n" + "a" * 100000 +
                          "\n## Automated gate warning\n\nThe build gate failed.\n\n- Build gate: fail\n- Test gate: skipped\n\nRelevant failure output:\n" +
                          "b" * 100000 + "\nReport end")
        result, _, published = self.run_scenario("complete")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("--draft", published[-1])
        body = (self.root / "pr-body").read_text()
        self.assertLessEqual(len(body.encode("utf-8")), 60000)
        self.assertIn("## Automated gate warning", body)
        self.assertIn("The build gate failed.", body)
        self.assertIn("- Build gate: fail", body)
        self.assertIn("- Test gate: skipped", body)
        self.assertIn("Report start", body)
        self.assertIn("Report end", body)

    def test_repair_cap_allows_second_attempt(self):
        self.env.update(GATE_REPAIR_ATTEMPTS="2", GATE_REPAIR_BUDGET="30s")
        result, calls, published = self.run_scenario("build-second-repair")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(len(published), 1)
        self.assertNotIn("--draft", published[0])
        self.assertEqual(sum(x[:2] == ["codex", "app-server"] for x in calls), 3)
        body = (self.root / "pr-body").read_text()
        self.assertIn("## Gate repair 1", body)
        self.assertIn("## Gate repair 2", body)

    def test_repair_attempts_stop_at_cap_and_publish_failed_draft(self):
        self.env.update(GATE_REPAIR_ATTEMPTS="2", GATE_REPAIR_BUDGET="30s")
        result, calls, published = self.run_scenario("build-failure")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(len(published), 1, result.stdout + result.stderr)
        self.assertIn("--draft", published[0])
        self.assertEqual(sum(x[:2] == ["codex", "app-server"] for x in calls), 3)
        self.assertIn("exit_reason=pushed-draft-build-failed", result.stdout)

    def test_test_repair_that_breaks_build_reports_latest_gate_results(self):
        self.env.update(GATE_REPAIR_ATTEMPTS="1", GATE_REPAIR_BUDGET="30s")
        result, calls, published = self.run_scenario("test-build-regression")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(len(published), 1, result.stdout + result.stderr)
        self.assertIn("--draft", published[0])
        self.assertEqual([x[1] for x in calls if x[0] == "dotnet"], ["build", "test", "build"])
        body = (self.root / "pr-body").read_text()
        self.assertIn("final build gate failed", body)
        self.assertIn("Runner validation: build fail, tests skipped", body)

    def test_dirty_repair_never_publishes(self):
        self.env.update(GATE_REPAIR_ATTEMPTS="2", GATE_REPAIR_BUDGET="30s")
        result, _, published = self.run_scenario("repair-dirty")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(published, [])
        self.assertIn("exit_reason=invalid-tree-after-gate-repair", result.stdout)

    def test_spend_includes_separate_repair_root(self):
        self.env.update(GATE_REPAIR_ATTEMPTS="1", GATE_REPAIR_BUDGET="30s")  # repair finishes early; must not be forced to the cap
        result, _, published = self.run_scenario("build-repair")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(len(published), 1)
        comment = (self.root / "pr-comment").read_text()
        self.assertIn("Cleanup — `fixture-thread`", comment)
        self.assertIn("Gate repair 1 — `fixture-repair`", comment)
        self.assertIn("Agent cost for fixture-repair", comment)

    def test_spend_failures_do_not_block_publication(self):
        for scenario in ("comment-failure", "missing-calculator", "calculator-failure"):
            with self.subTest(scenario=scenario):
                if scenario != "comment-failure":
                    self.setUp()
                if scenario == "missing-calculator":
                    self.env["SPEND_SCRIPT"] = str(self.root / "absent.py")
                elif scenario == "calculator-failure":
                    Path(self.env["SPEND_SCRIPT"]).write_text("raise RuntimeError('usage unavailable')")
                result, _, published = self.run_scenario(scenario)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertEqual(len(published), 1)
                if scenario != "comment-failure":
                    self.assertIn("Spend unavailable", (self.root / "pr-comment").read_text())
                else:
                    self.assertIn("WARNING: spend comment failed", result.stdout)

    def test_recovered_publication_uses_original_session_manifest(self):
        result, _, _ = self.run_scenario("pr-create-failure")
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse((self.root / "pr-comment").exists())
        result, calls, _ = self.run_scenario("complete")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(sum(x[:2] == ["codex", "app-server"] for x in calls), 1)
        self.assertIn("Agent cost for fixture-thread", (self.root / "pr-comment").read_text())

    def test_explicit_model_and_effort_override_reach_the_session(self):
        (self.clone / ".codex/cron/debt-runner.env").write_text(
            "CODEX_MODEL=gpt-6-luna\nCODEX_EFFORT=low\n")
        result, _, _ = self.run_scenario("complete")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        requests = [json.loads(x) for x in (self.root / "requests.jsonl").read_text().splitlines()]
        thread = next(x["params"] for x in requests if x["method"] == "thread/start")
        self.assertEqual(thread["model"], "gpt-6-luna")
        self.assertEqual(thread["config"]["model_reasoning_effort"], "low")

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

    def test_ledger_that_did_not_shrink_still_publishes(self):
        result, _, published = self.run_scenario("ledger-grows")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(len(published), 1)
        self.assertNotIn("--draft", published[0])
        body = (self.root / "pr-body").read_text()
        self.assertIn("Open rows: 2 → 2 (net +0).", body)

    def test_ledger_only_does_not_publish_or_run_dotnet(self):
        result, calls, published = self.run_scenario("ledger")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("exit_reason=no-substantive-fixes", result.stdout)
        self.assertEqual(published, [])
        self.assertFalse(any(x[0] == "dotnet" for x in calls))

    def test_blocked_goal_validates_commits_and_publishes_a_failed_draft(self):
        result, calls, published = self.run_scenario("blocked")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(len(published), 1)
        self.assertIn("--draft", published[0])
        self.assertEqual([x[1] for x in calls if x[0] == "dotnet"], ["build", "test"])
        self.assertIn("exit_reason=pushed-draft-goal-blocked commits=2 build=pass test=pass", result.stdout)
        body = (self.root / "pr-body").read_text()
        self.assertIn("The Codex goal stopped blocked", body)
        self.assertIn("Fix 1", body)
        self.assertIn("Fix 2", body)
        self.assertNotIn("Cumulative fixes: 2", body)
        self.assertIn("Agent cost for fixture-thread", (self.root / "pr-comment").read_text())

    def test_blocked_publication_retry_preserves_draft_warning(self):
        result, _, _ = self.run_scenario("blocked")
        self.assertNotEqual(result.returncode, 0)
        # The fake GitHub list returns no PR: exercise pushed-branch recovery.
        result, calls, published = self.run_scenario("complete")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(len(published), 2)
        self.assertTrue(all("--draft" in pr for pr in published))
        self.assertEqual(sum(x[:2] == ["codex", "app-server"] for x in calls), 1)
        self.assertIn("## Automated goal warning", (self.root / "pr-body").read_text())

    def test_failures_never_publish(self):
        for scenario in ("codex-failure", "missing-report", "blocked-dirty", "dirty", "build-failure", "test-failure"):
            with self.subTest(scenario=scenario):
                # Each scenario needs a fresh clone and log history.
                if scenario != "codex-failure":
                    self.setUp()
                result, _, published = self.run_scenario(scenario)
                self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
                if scenario in ("build-failure", "test-failure"):
                    self.assertEqual(len(published), 1)
                    self.assertIn("--draft", published[0])
                    self.assertNotIn("passed", (self.root / "pr-body").read_text())
                    self.assertIn("Open rows: 2 → 1 (net -1).", (self.root / "pr-body").read_text())
                    self.assertIn("Agent cost for fixture-thread", (self.root / "pr-comment").read_text())
                else:
                    self.assertEqual(published, [])
                if scenario == "codex-failure":
                    self.assertIn("exit_reason=codex-failed commits=1", result.stdout)
                if scenario == "blocked-dirty":
                    self.assertIn("exit_reason=dirty-tree-after-codex commits=2", result.stdout)


if __name__ == "__main__":
    unittest.main()
