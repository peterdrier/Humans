#!/usr/bin/env python3
"""Stay attached to one Codex session until its native timed goal completes."""
import json
import os
from pathlib import Path
import subprocess
import sys
import time


def run(prompt_file, report_file, deadline):
    prompt = Path(prompt_file).read_text()
    objective = (
        f"Follow {prompt_file} and actively fix substantive tech debt until Unix time "
        f"{deadline}, then finish, validate, and commit the current task. Time is the "
        "ONLY target, with no fix-count quota. Keep working across turn boundaries. "
        "Complete the goal only after that deadline AND current-task completion with "
        "a clean working tree. One PR will contain all fixes; the wrapper alone "
        "publishes it. Prioritize production-code fixes; tests may support those "
        "fixes but standalone coverage work is never an objective. "
        "Ledger/documentation cleanup and test-only work are not substantive fixes."
    )
    # These are the app-server equivalents of dangerous CLI mode, and remain
    # attached to the thread for every native goal continuation.
    dangerous = os.environ.get("CODEX_DANGEROUS", "1") == "1"
    proc = subprocess.Popen(
        ["codex", "app-server", "--stdio", "--enable", "goals"],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, bufsize=1,
    )
    thread_id = None
    goal_status = None
    final_message = ""
    started = False
    premature_completion = False
    resume_request = None
    next_request_id = 5

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
            if "error" in event and "id" in event:
                raise RuntimeError(f"Codex request failed: {event['error']}")
            if "id" in event and method:
                # Dangerous mode should not ask for shell/file approval. Never
                # hang waiting for interactive input or silently grant other access.
                send_error = {"id": event["id"], "error": {"code": -32601,
                              "message": "Unattended runner cannot answer interactive requests"}}
                proc.stdin.write(json.dumps(send_error) + "\n")
                proc.stdin.flush()
                raise RuntimeError(f"Unexpected interactive request: {method}")
            if event.get("id") == 1:
                send("initialized", {})
                send("thread/start", {
                    "cwd": os.environ["WORK_DIR"],
                    "model": os.environ.get("CODEX_MODEL", "gpt-5.6-terra"),
                    "approvalPolicy": "never",
                    "sandbox": "danger-full-access" if dangerous else "workspace-write",
                    "config": {"model_reasoning_effort": os.environ.get("CODEX_EFFORT", "medium")},
                }, 2)
            elif event.get("id") == 2:
                thread_id = event["result"]["thread"]["id"]
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
                        f"The goal was marked complete before its work deadline. "
                        f"Actual Unix time is {now}; the original deadline remains {deadline} "
                        f"({max(0, deadline - now)} seconds remaining). The same goal is active again. "
                        "Read get_goal and continue substantive fixes in this same branch/session "
                        "until the deadline, then finish and validate the current task. "
                        "Time is the only target; no fix-count quota. If the deadline has now "
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
                if goal_status == "complete" and time.time() < deadline:
                    # Remember when completion happened, even if the deadline
                    # passes before this turn finishes. Never interrupt its work.
                    premature_completion = True
                    print("Native goal completed early; will reactivate after this turn", flush=True)
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
                if premature_completion:
                    resume_request = next_request_id
                    next_request_id += 1
                    send("thread/goal/set", {"threadId": thread_id,
                         "objective": objective, "status": "active"}, resume_request)
                    continue
                if goal_status == "complete":
                    if time.time() < deadline or not final_message.strip():
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
        run(sys.argv[1], sys.argv[2], int(sys.argv[3]))
    except (OSError, ValueError, KeyError, RuntimeError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        sys.exit(1)
