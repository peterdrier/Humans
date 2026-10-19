#!/usr/bin/env python3
"""Stay attached to one Codex session until its native timed goal completes."""
import json
import math
import os
from pathlib import Path
import subprocess
import sys
import time


def quota_stop_reason(result, now):
    """Return a stop reason, or None while cumulative weekly allowance remains."""
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
    minimum = 100 - 12 * elapsed_days
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
    proc = subprocess.Popen(
        ["codex", "app-server", "--stdio", "--enable", "goals",
         "-c", "agents.enabled=true",
         "-c", 'agents.default_subagent_model="gpt-6-luna"',
         "-c", 'agents.default_subagent_reasoning_effort="medium"'],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, bufsize=1,
    )
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

    def send(method, params, request_id=None):
        message = {"method": method, "params": params}
        if request_id is not None:
            message["id"] = request_id
        proc.stdin.write(json.dumps(message) + "\n")
        proc.stdin.flush()

    try:
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
                          quota_stop_reason(event.get("result", {}), time.time()))
                if reason:
                    print(f"Stopping new work: {reason}", flush=True)
                    if quota_phase == "round":
                        if not final_message.strip():
                            raise RuntimeError("Completed round lacks cumulative report")
                        Path(report_file).write_text(final_message + f"\n\nStopped: {reason}.\n")
                    return 3
                if quota_phase == "startup":
                    send("thread/start", thread_params, 2)
                elif time.time() >= deadline:
                    if not final_message.strip():
                        raise RuntimeError("Completed round lacks cumulative report")
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
                    "text": (
                        f"The completed round has been accepted; continue with the next round. "
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
                if goal_status == "blocked":
                    print("Native goal blocked; returning committed work for draft validation", flush=True)
                    return 2
                if goal_status not in ("active", "complete"):
                    raise RuntimeError(f"Native goal stopped incomplete: {goal_status}")
            elif method == "thread/goal/cleared":
                raise RuntimeError("Native goal was cleared before completion")
            elif method == "turn/started":
                final_message = ""
            elif method == "item/completed":
                item = params["item"]
                if item["type"] == "agentMessage" and item.get("phase") == "final_answer":
                    final_message = item["text"]
            elif method == "turn/completed" and started:
                if params["turn"]["status"] != "completed":
                    raise RuntimeError(f"Codex turn failed: {params['turn']}")
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
                    Path(report_file).write_text(final_message)
                    return
                # A normal turn boundary is not completion. Keep this same
                # connection/session alive while Codex continues its own goal.
        raise RuntimeError(f"Codex disconnected before goal completion (status={goal_status})")
    finally:
        proc.stdin.close()
        proc.terminate()
        try:
            proc.wait(timeout=10)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.wait()


if __name__ == "__main__":
    try:
        sys.exit(run(sys.argv[1], sys.argv[2], int(sys.argv[3]), sys.argv[4:5] == ["repair"]) or 0)
    except (OSError, ValueError, KeyError, RuntimeError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        sys.exit(1)
