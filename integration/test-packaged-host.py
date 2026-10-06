"""Check a signed, packaged host and its native SQLite library without a server."""
import json
import pathlib
import queue
import sqlite3
import subprocess
import sys
import tempfile
import threading
import uuid


with tempfile.TemporaryDirectory(prefix="clipharbor-packaged-") as directory:
    host = pathlib.Path(sys.argv[1]).resolve()
    process = subprocess.Popen([str(host)], cwd=host.parent, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True, encoding="utf-8")
    messages = queue.Queue()

    def read():
        for line in process.stdout:
            messages.put(json.loads(line))
        messages.put(None)

    threading.Thread(target=read, daemon=True).start()

    def send(value):
        process.stdin.write(json.dumps(value) + "\n")
        process.stdin.flush()

    def command(method, params):
        ident = str(uuid.uuid4())
        send({"id": ident, "method": method, "params": params})
        while True:
            response = messages.get(timeout=30)
            assert response is not None, "Packaged sync host exited unexpectedly"
            if response.get("id") == ident:
                assert "error" not in response, response.get("error")
                return response["result"]
            if "callId" in response:
                result = {"version": 1, "unlocked": True} if response["method"] == "clipboardState" else {}
                send({"callId": response["callId"], "result": result})

    try:
        session = {key: str(uuid.uuid4()) for key in ("accountId", "deviceId", "sessionId", "syncEpoch")}
        session.update(username="packaged-validation", accessToken="validation-access", refreshToken="validation-refresh", revokeToken="validation-revoke", accessExpiresAt="2099-01-01T00:00:00Z", refreshExpiresAt="2099-01-01T00:00:00Z")
        config = {"serverUrl": "http://127.0.0.1:1", "allowLocalHttp": True, "serverInstanceId": str(uuid.uuid4()), "deviceId": session["deviceId"], "enabled": False, "keepSignedIn": False}
        command("initialize", {"root": directory, "config": config, "session": session})
        # Escaping a valid text payload can make the private-pipe JSON exceed 4 MiB.
        command("capture", {"record": {"recordId": str(uuid.uuid4()), "kind": "text", "text": "\x01" * (768 * 1024)}, "paths": [], "version": 1, "real": False})
        databases = list(pathlib.Path(directory).rglob("sync.sqlite"))
        assert len(databases) == 1
        with sqlite3.connect(databases[0]) as database:
            assert database.execute("SELECT count(*) FROM outbox").fetchone()[0] == 1
        command("logout", {})
        print("PASS signed packaged sync host, native SQLite and private-pipe payload")
    finally:
        process.stdin.close()
        try:
            process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()
