"""Select a main-branch bundle whose server and native client checks passed."""
import json
import os
import re
import subprocess
import sys

repo = os.environ["GITHUB_REPOSITORY"]


def api(path):
    return json.loads(subprocess.check_output(["gh", "api", f"repos/{repo}/{path}"], text=True))


requested = sys.argv[1] if len(sys.argv) > 1 else ""
if requested and not requested.isdecimal():
    sys.exit("Build run ID must be numeric.")
candidates = [api(f"actions/runs/{requested}")] if requested else api("actions/workflows/sync.yml/runs?branch=main&per_page=20")["workflow_runs"]
required = {"server", "clients / build", "clients / Windows x64", "clients / Windows ARM64", "package"}
for run in candidates:
    if run["head_branch"] != "main" or run["status"] != "completed" or run["event"] not in ("push", "workflow_dispatch") or run["path"] != ".github/workflows/sync.yml":
        continue
    if not re.fullmatch(r"[0-9a-f]{40}", run["head_sha"]):
        continue
    ident = run["id"]
    jobs = api(f"actions/runs/{ident}/jobs?per_page=100")["jobs"]
    passed = {job["name"] for job in jobs if job["conclusion"] == "success"}
    if not required.issubset(passed):
        continue
    artifacts = api(f"actions/runs/{ident}/artifacts?per_page=100")["artifacts"]
    if not any(item["name"] == "ClipHarbor-server-bundle" and not item["expired"] for item in artifacts):
        continue
    with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
        output.write(f"run_id={ident}\nbuild_commit={run['head_sha']}\n")
    print(f"Using verified server bundle from run {ident}, commit {run['head_sha']}")
    break
else:
    sys.exit("No retained main-branch server bundle passed all required checks.")
