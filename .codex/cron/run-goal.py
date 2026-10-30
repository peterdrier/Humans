#!/usr/bin/env python3
"""Stay attached to one Codex session until its native timed goal completes."""
import json
import math
import os
from pathlib import Path
import subprocess
import sys
import time


def quota_stop_reason(result, now, policy=None):
    """Check live usage against the run's fixed allowance, renewed on weekly reset."""
    buckets = result.get("rateLimitsByLimitId") or {}
    bucket = buckets.get("codex") or result.get("rateLimits") or {}
    windows = [bucket.get(key) for key in ("primary", "secondary")]
    weekly = [w for w in windows if isinstance(w, dict) and w.get("windowDurationMins") == 10080]
    if len(weekly) != 1:
        return "weekly quota unavailable"
    window = weekly[0]
    used, reset = window.get("usedPercent"), window.get("resetsAt")
    if (isinstance(used, bool) or not isinstance(used, (int, float)) or
            not math.isfinite(used) or not 0 <= used <= 100 or
            isinstance(reset, bool) or not isinstance(reset, (int, float)) or
            not math.isfinite(reset) or not 0 < reset - now <= 7 * 86400):
        return "weekly quota invalid or stale"
    elapsed_days = 7 - (reset - now) / 86400
    if policy is None:
        policy = {}
    if policy.get("reset") != reset:
        policy.update(reset=reset, minimum=max(16, 100 - 12 * (elapsed_days + 1)))
    minimum = policy["minimum"]
    remaining = 100 - used
    print(f"Weekly quota: {remaining:g}% remaining; cutoff {minimum:.2f}%; "
          f"reset {reset}; elapsed {elapsed_days:.3f} days", flush=True)
    if remaining < minimum:
        return f"weekly quota cutoff ({remaining:g}% remaining < {minimum:.2f}%)"
    # Respect another returned window too, without assuming which slot it uses.
    for other in windows:
        if other and other.get("usedPercent", 0) >= 100:
            return "account quota exhausted"
    if result.get("ordinaryUsageAllowed") is False or bucket.get("rateLimitReachedType"):
        return "account usage limit reached"
    return None


def run(prompt_file, report_file, deadline, repair=False):
    prompt = Path(prompt_file).read_text()
    # Repair mode: the deadline is a cap, not a target; finishing early is fine.
    objective = (
        f"Follow {prompt_file}: repair only the named gate failure, validate, commit, "
        "and finish with a concise report. Do no other debt work. Completing before "
        f"Unix time {deadline} is expected; that time is a cap, not a target."
    ) if repair else (
        f"Follow {prompt_file} and actively fix substantive tech debt until Unix time "
        f"{deadline}, unless the wrapper ends work at a weekly quota boundary. "
        "Complete one coherent fix or batch per round: validate, commit, then mark the "
        "goal complete and return a cumulative report. Do not start another batch in "
        "this turn. The wrapper checks quota after each completed round and reactivates "
        "this same goal if work may continue. There is no fix-count quota. Close existing debt-ledger rows "
        "first; the ledger must end smaller than it started. Keep working across turn boundaries. "
        "Complete each round only after current-task completion with "
        "a clean working tree. One PR will contain all fixes; the wrapper alone "
        "publishes it. Prioritize production-code fixes; tests may support those "
        "fixes but standalone coverage work is never an objective. "
        "Ledger/documentation cleanup and test-only work are not substantive fixes."
    )
    # These are the app-server equivalents of dangerous CLI mode, and remain
    # attached to the thread for every native goal continuation.
    dangerous = os.environ.get("CODEX_DANGEROUS", "1") == "1"
    proc = None
    thread_id = None
    goal_status = None
    final_message = ""
    started = False
    premature_completion = False
    resume_request = None
    next_request_id = 5
    quota_enabled = not repair and os.environ.get("IGNORE_QUOTA_CUTOFF", "0") != "1"
    quota_request = None
    quota_phase = None
    quota_policy = {}
    stop_reason = "Codex disconnected before goal completion"
    retry_history = []
    retrying = False
    turn_error = None
    last_report = ""

    def outcome(reason):
        nonlocal stop_reason
        stop_reason = " ".join(str(reason).split())[:1500]
        print(f"Worker outcome: {stop_reason}", flush=True)

    def send(method, params, request_id=None):
        message = {"method": method, "params": params}
        if request_id is not None:
            message["id"] = request_id
        proc.stdin.write(json.dumps(message) + "\n")
        proc.stdin.flush()

    try:
        proc = subprocess.Popen(
            ["codex", "app-server", "--stdio", "--enable", "goals",
             "-c", "agents.enabled=true",
             "-c", 'agents.default_subagent_model="gpt-6-luna"',
             "-c", 'agents.default_subagent_reasoning_effort="medium"'],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, bufsize=1,
        )
        send("initialize", {"clientInfo": {"name": "humans_debt_runner", "version": "1"},
                            "capabilities": {"experimentalApi": True}}, 1)
        for line in proc.stdout:
            event = json.loads(line)
            # Log events, but avoid a second copy of streamed token deltas.
            method = event.get("method", "")
            if not method.endswith("/delta"):
                print(line, end="", flush=True)
            if quota_request is not None and event.get("id") == quota_request:
                quota_request = None
                reason = ("weekly quota read failed" if "error" in event else
                          quota_stop_reason(event.get("result", {}), time.time(), quota_policy))
                if reason:
                    outcome(reason)
                    if quota_phase == "retry":
                        return 2
                    if quota_phase == "round":
                        if not final_message.strip():
                            raise RuntimeError("Completed round lacks cumulative report")
                        Path(report_file).write_text(final_message + f"\n\nStopped: {reason}.\n")
                    return 3
                if quota_phase == "startup":
                    send("thread/start", thread_params, 2)
                elif quota_phase == "retry":
                    if time.time() >= deadline:
                        outcome("Original deadline reached before capacity retry")
                        return 2
                    resume_request = next_request_id
                    next_request_id += 1
                    send("thread/goal/set", {"threadId": thread_id,
                         "objective": objective, "status": "active"}, resume_request)
                elif time.time() >= deadline:
                    if not final_message.strip():
                        raise RuntimeError("Completed round lacks cumulative report")
                    outcome("Gate repair completed" if repair else "Work deadline reached")
                    Path(report_file).write_text(final_message)
                    return
                else:
                    resume_request = next_request_id
                    next_request_id += 1
                    send("thread/goal/set", {"threadId": thread_id,
                         "objective": objective, "status": "active"}, resume_request)
                continue
            if "error" in event and "id" in event:
                raise RuntimeError(f"Codex request failed: {event['error']}")
            if "id" in event and method:
                send_error = {"id": event["id"], "error": {"code": -32601,
                              "message": "Unattended runner cannot answer interactive requests"}}
                proc.stdin.write(json.dumps(send_error) + "\n")
                proc.stdin.flush()
                raise RuntimeError(f"Unexpected interactive request: {method}")
            if event.get("id") == 1:
                send("initialized", {})
                thread_params = {
                    "cwd": os.environ["WORK_DIR"],
                    "model": os.environ.get("CODEX_MODEL", "gpt-6.1-sol"),
                    "approvalPolicy": "never",
                    "sandbox": "danger-full-access" if dangerous else "workspace-write",
                    "config": {"model_reasoning_effort": os.environ.get("CODEX_EFFORT", "medium")},
                }
                if quota_enabled:
                    quota_request = next_request_id
                    next_request_id += 1
                    quota_phase = "startup"
                    send("account/rateLimits/read", {}, quota_request)
                else:
                    send("thread/start", thread_params, 2)
            elif event.get("id") == 2:
                thread_id = event["result"]["thread"]["id"]
                manifest = os.environ.get("DEBT_SESSION_MANIFEST")
                if manifest:
                    try:
                        with Path(manifest).open("a") as sessions:
                            sessions.write(json.dumps({"thread_id": thread_id}) + "\n")
                    except OSError as error:
                        print(f"WARNING: could not record spend session: {error}", flush=True)
                send("thread/goal/set", {"threadId": thread_id, "objective": objective}, 3)
            elif event.get("id") == 3:
                goal_status = event["result"]["goal"]["status"]
                # Let native continuation drive normal turn boundaries.
                send("turn/start", {"threadId": thread_id,
                     "input": [{"type": "text", "text": prompt}]}, 4)
                started = True
            elif resume_request is not None and event.get("id") == resume_request:
                goal_status = event["result"]["goal"]["status"]
                if goal_status != "active":
                    raise RuntimeError(f"Could not reactivate timed goal: {goal_status}")
                resume_request = None
                premature_completion = False
                now = int(time.time())
                send("turn/start", {"threadId": thread_id, "input": [{
                    "type": "text",
                    "text": ("Retry the interrupted gate repair, preserving unfinished work. "
                             f"The original deadline remains {deadline}.\n" + prompt) if repair else (
                        ("Retry the interrupted batch after a model-capacity error. Inspect the "
                         "working tree and previous actions before continuing; preserve unfinished work. "
                         if retrying else "The completed round has been accepted; continue with the next round. ") +
                        f"Actual Unix time is {now}; the original deadline remains {deadline} "
                        f"({max(0, deadline - now)} seconds remaining). The same goal is active again. "
                        "Read get_goal and continue substantive fixes in this same branch/session "
                        "for one coherent batch, then validate, commit, complete the goal and "
                        "return the cumulative report so the wrapper can check quota. "
                        "No fix-count quota; the ledger must shrink. If the deadline has now "
                        "passed, finish and validate the current task. Complete the goal only "
                        "then, and return a cumulative PR report covering the entire run."
                    ),
                }]}, next_request_id)
                next_request_id += 1
                retrying = False
            params = event.get("params", {})
            if params.get("threadId") != thread_id or thread_id is None:
                continue
            if method == "thread/goal/updated":
                goal_status = params["goal"]["status"]
                if goal_status == "complete" and not repair and time.time() < deadline:
                    # Remember when completion happened, even if the deadline
                    # passes before this turn finishes. Never interrupt its work.
                    premature_completion = True
                    print("Round completed before deadline; will check continuation after this turn", flush=True)
                # The failure detail follows this notification in turn/completed.
                if goal_status not in ("active", "complete", "blocked"):
                    raise RuntimeError(f"Native goal stopped incomplete: {goal_status}")
            elif method == "thread/goal/cleared":
                raise RuntimeError("Native goal was cleared before completion")
            elif method == "error":
                if not params.get("willRetry"):
                    turn_error = params.get("error")
            elif method == "turn/started":
                final_message = ""
                turn_error = None
            elif method == "item/completed":
                item = params["item"]
                if item["type"] == "agentMessage" and item.get("phase") == "final_answer":
                    final_message = item["text"]
            elif method == "turn/completed" and started:
                turn = params["turn"]
                error = turn.get("error") or (turn_error if turn["status"] != "completed" else None) or {}
                if goal_status == "blocked" or turn["status"] != "completed":
                    detail = error.get("message") or final_message or "Native goal blocked without a reported reason"
                    code = error.get("codexErrorInfo")
                    if isinstance(code, dict):
                        code = next(iter(code), None)
                    outcome(f"{code + ': ' if code else ''}{detail}")
                    capacity = code == "serverOverloaded" or "model is at capacity" in detail.lower()
                    if capacity and len(retry_history) < 3 and time.time() + 180 < deadline:
                        retry_history.append(f"Capacity retry {len(retry_history) + 1}/3 after 180 seconds: {stop_reason}")
                        print(retry_history[-1], flush=True)
                        time.sleep(180)
                        if time.time() >= deadline:
                            outcome(stop_reason + "; original deadline reached during capacity retry wait")
                            return 2
                        retrying = True
                        if quota_enabled:
                            quota_request = next_request_id
                            quota_phase = "retry"
                            send("account/rateLimits/read", {}, quota_request)
                        else:
                            resume_request = next_request_id
                            send("thread/goal/set", {"threadId": thread_id,
                                 "objective": objective, "status": "active"}, resume_request)
                        next_request_id += 1
                        continue
                    if capacity:
                        outcome(stop_reason + ("; capacity retries exhausted" if len(retry_history) == 3
                                               else "; insufficient time before original deadline to retry"))
                    if goal_status == "blocked" or capacity:
                        return 2
                    raise RuntimeError(stop_reason)
                if final_message.strip():
                    last_report = final_message
                if quota_enabled and goal_status == "complete":
                    if not final_message.strip():
                        raise RuntimeError("Completed round lacks cumulative report")
                    if subprocess.check_output(["git", "status", "--porcelain"],
                                               cwd=os.environ["WORK_DIR"], text=True).strip():
                        raise RuntimeError("Completed round left uncommitted changes")
                    quota_request = next_request_id
                    next_request_id += 1
                    quota_phase = "round"
                    send("account/rateLimits/read", {}, quota_request)
                    continue
                if premature_completion:
                    resume_request = next_request_id
                    next_request_id += 1
                    send("thread/goal/set", {"threadId": thread_id,
                         "objective": objective, "status": "active"}, resume_request)
                    continue
                if goal_status == "complete":
                    if (not repair and time.time() < deadline) or not final_message.strip():
                        raise RuntimeError("Completed goal lacks elapsed work window or final report")
                    outcome("Gate repair completed" if repair else "Work deadline reached")
                    Path(report_file).write_text(final_message)
                    return
                # A normal turn boundary is not completion. Keep this same
                # connection/session alive while Codex continues its own goal.
        raise RuntimeError(f"Codex disconnected before goal completion (status={goal_status})")
    except (OSError, ValueError, KeyError, RuntimeError) as error:
        outcome(error)
        raise
    finally:
        if proc is not None:
            proc.stdin.close()
            proc.terminate()
            try:
                proc.wait(timeout=10)
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.wait()
        Path(str(report_file) + ".status.json").write_text(json.dumps({
            "reason": stop_reason, "retries": retry_history,
        }))
        print(f"Worker stopped: {stop_reason}", flush=True)
        report = Path(report_file)
        if last_report and (not report.exists() or not report.read_text().strip()):
            report.write_text(last_report)
        with report.open("a") as output:
            output.write("\n\n## Runner outcome\n\n" + stop_reason + "\n")
            for retry in retry_history:
                output.write("\n- " + retry + "\n")


if __name__ == "__main__":
    try:
        sys.exit(run(sys.argv[1], sys.argv[2], int(sys.argv[3]), sys.argv[4:5] == ["repair"]) or 0)
    except (OSError, ValueError, KeyError, RuntimeError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        sys.exit(1)
