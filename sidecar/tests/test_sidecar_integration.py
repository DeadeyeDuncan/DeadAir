import concurrent.futures
import json
import subprocess
import sys
from pathlib import Path
import pytest

FIXTURES = Path(__file__).parent / "fixtures"


def _readline_with_timeout(proc, timeout=60):
    with concurrent.futures.ThreadPoolExecutor(max_workers=1) as ex:
        fut = ex.submit(proc.stdout.readline)
        try:
            return fut.result(timeout=timeout)
        except concurrent.futures.TimeoutError:
            proc.kill()
            raise AssertionError(f"sidecar produced no output within {timeout}s")


@pytest.mark.integration
@pytest.mark.slow
def test_sidecar_end_to_end_cpu(tmp_path):
    proc = subprocess.Popen(
        [sys.executable, "-m", "asr_sidecar"],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL, text=True, encoding="utf-8",
        cwd=str(Path(__file__).parents[1]))

    def send(obj):
        proc.stdin.write(json.dumps(obj) + "\n")
        proc.stdin.flush()

    def events_until(name, limit=20):
        for _ in range(limit):
            line = _readline_with_timeout(proc)
            e = json.loads(line)
            if e["event"] == name:
                return e
        raise AssertionError(f"never saw {name}")

    try:
        send({"cmd": "config", "engine": "cpu", "cpu_model": "tiny"})
        assert events_until("ready")["engine"] == "cpu"
        send({"cmd": "transcribe_wav", "path": str(FIXTURES / "jfk.wav")})
        final = events_until("final")
        assert "country" in final["text"].lower()
        assert final["ms"] > 0
        send({"cmd": "shutdown"})
        proc.wait(timeout=10)
    finally:
        proc.kill()
