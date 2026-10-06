"""Exercise the macOS private-pipe adapter with the real service and shared host."""
import json
import os
import pathlib
import queue
import shutil
import subprocess
import tempfile
import threading
import time
import urllib.request
import uuid

url = os.environ.get("CLIPHARBOR_TEST_URL", "http://localhost:28080").rstrip("/")


def api(path, body=None, tokens=None):
    headers = {"Content-Type": "application/json"}
    if tokens:
        headers["Authorization"] = "Bearer " + tokens["accessToken"]
    request = urllib.request.Request(url + "/api/v1/" + path, data=None if body is None else json.dumps(body).encode(), headers=headers)
    with urllib.request.urlopen(request, timeout=20) as response:
        data = response.read()
        return json.loads(data) if data else None


def login(name):
    return api("auth/login", {"username": "alice", "password": os.environ.get("CLIPHARBOR_TEST_PASSWORD", "integration-test-only"), "deviceName": name, "deviceId": str(uuid.uuid4())})


tokens = login("native-pipe test")
other = login("native-pipe remote")
instance = api("meta")["instanceId"]
directory = pathlib.Path(tempfile.mkdtemp(prefix="clipharbor-pipe-"))
process = subprocess.Popen(["dotnet", "shared/ClipHarbor.SyncHost/bin/Release/net10.0/ClipHarbor.SyncHost.dll"], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True, encoding="utf-8", bufsize=1)
responses = {}
bindings = []
received = []
statuses = []
errors = []
version = 1
write_lock = threading.Lock()


def send(message):
    with write_lock:
        process.stdin.write(json.dumps(message) + "\n")
        process.stdin.flush()


def reader():
    global version
    try:
        for line in process.stdout:
            message = json.loads(line)
            if "id" in message:
                responses[message["id"]].put(message)
                continue
            method = message.get("method")
            params = message.get("params", {})
            result = {}
            if method == "clipboardState":
                result = {"version": version, "unlocked": True}
            elif method == "bind":
                bindings.append(params)
            elif method == "receive":
                if params["direct"]:
                    assert params["expected"] == version
                    version += 1
                received.append(params)
            elif method == "status":
                statuses.append(params["value"])
            elif method not in ("persist", "configuration", "space"):
                raise AssertionError("Unknown native callback")
            if "callId" in message:
                send({"callId": message["callId"], "result": result})
    except Exception as error:
        errors.append(type(error).__name__)


thread = threading.Thread(target=reader, daemon=True)
thread.start()


def command(method, params=None):
    ident = str(uuid.uuid4())
    result = responses[ident] = queue.Queue()
    send({"id": ident, "method": method, "params": params or {}})
    response = result.get(timeout=30)
    assert "error" not in response, response.get("error", "Native operation failed")
    return response["result"]


def wait(predicate):
    until = time.monotonic() + 30
    while not predicate() and time.monotonic() < until:
        assert not errors, "Native callback failure"
        time.sleep(0.1)
    assert predicate(), "Native bridge did not complete expected behavior"


try:
    config = {"serverUrl": url, "deviceId": tokens["deviceId"], "username": "alice", "enabled": True, "allowLocalHttp": True, "keepSignedIn": False, "serverInstanceId": instance}
    command("initialize", {"config": config, "root": str(directory), "session": tokens})
    wait(lambda: any("已连接" in status for status in statuses))
    ident = str(uuid.uuid4())
    command("capture", {"record": {"recordId": ident, "kind": "text", "text": "private pipe capture"}, "paths": [], "version": version, "real": True})
    wait(lambda: any(binding["record"].get("revision", 0) > 0 and binding["sourceId"] == ident for binding in bindings))
    record = next(binding["record"] for binding in reversed(bindings) if binding["record"].get("revision", 0) > 0 and binding["sourceId"] == ident)
    assert not any(item["direct"] for item in received), "Own copy must not loop back"
    api("sync/operations", {"syncEpoch": other["syncEpoch"], "operations": [{"operationId": str(uuid.uuid4()), "type": "capture", "recordId": record["recordId"], "record": record}]}, other)
    api("clipboard/events", {"clipboardEventId": str(uuid.uuid4()), "recordId": record["recordId"], "syncEpoch": other["syncEpoch"]}, other)
    wait(lambda: any(item["direct"] for item in received))
    assert version == 2, "Direct copy must be written once"
    command("sync")
    assert version == 2, "History catch-up must not promote clipboard"
    result = command("logout")
    assert result["config"]["enabled"] is False
    api("auth/logout", {"revokeToken": result["revokeToken"]})
    for path in directory.rglob("*"):
        if path.is_file():
            data = path.read_bytes()
            assert tokens["accessToken"].encode() not in data
            assert tokens["refreshToken"].encode() not in data
    print("PASS private-pipe native adapter, source binding, no echo, direct receive, logout and credential isolation")
finally:
    process.stdin.close()
    try:
        process.wait(timeout=15)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait()
    thread.join(timeout=2)
    shutil.rmtree(directory, ignore_errors=True)
