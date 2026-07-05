"""Newline-delimited JSON protocol. stdout is protocol-ONLY; logs go to stderr."""
import json
import sys
import threading

_emit_lock = threading.Lock()


def emit(obj: dict, stream=None) -> None:
    stream = stream if stream is not None else sys.stdout
    with _emit_lock:
        stream.write(json.dumps(obj, ensure_ascii=False) + "\n")
        stream.flush()


def read_commands(stream=None, err_stream=None):
    stream = stream if stream is not None else sys.stdin
    for line in stream:
        line = line.strip()
        if not line:
            continue
        try:
            yield json.loads(line)
        except json.JSONDecodeError:
            emit({"event": "error", "where": "ipc",
                  "message": f"bad json: {line[:80]}"},
                 stream=err_stream if err_stream is not None else sys.stdout)
