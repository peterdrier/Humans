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
        "publishes it. Ledger/documentation cleanup is not a substantive fix."
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
                # Exactly one user turn. The native goal scheduler owns every
                # subsequent turn; this client only listens for notifications.
                send("turn/start", {"threadId": thread_id,
                     "input": [{"type": "text", "text": prompt}]}, 4)
                started = True
            params = event.get("params", {})
            if params.get("threadId") != thread_id or thread_id is None:
                continue
            if method == "thread/goal/updated":
                goal_status = params["goal"]["status"]
                if goal_status == "complete" and time.time() < deadline:
                    raise RuntimeError("Native goal completed before the work deadline")
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
