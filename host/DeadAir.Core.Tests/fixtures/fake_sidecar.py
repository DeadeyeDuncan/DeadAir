import json
import sys

print(json.dumps({"event": "ready", "engine": "cpu", "model": "fake"}),
      flush=True)
for line in sys.stdin:
    cmd = json.loads(line)
    if cmd["cmd"] == "start":
        print(json.dumps({"event": "recording"}), flush=True)
    elif cmd["cmd"] == "stop":
        print(json.dumps({"event": "final", "text": "hello world", "ms": 5}),
              flush=True)
    elif cmd["cmd"] == "shutdown":
        break
