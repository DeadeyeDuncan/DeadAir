"""DeadAir ASR sidecar. Protocol: spec.md §3. stdout = protocol only."""
import logging
import sys
import time
import numpy as np
from .audio import load_wav
from .capture import MicCapture
from .config import SidecarConfig
from .engines import create_engine
from .ipc import emit, read_commands
from .levels import LevelEmitter
from .vad import extract_speech

logging.basicConfig(stream=sys.stderr, level=logging.INFO,
                    format="%(asctime)s %(levelname)s %(message)s")
log = logging.getLogger("sidecar")


def _finish(audio: np.ndarray, cfg: SidecarConfig, engine) -> None:
    try:
        t0 = time.monotonic()
        speech = extract_speech(audio) if len(audio) else None
        if speech is None:
            emit({"event": "empty"})
            return
        text = engine.transcribe(speech,
                                 initial_prompt=", ".join(cfg.dictionary))
        ms = int((time.monotonic() - t0) * 1000)
        if not text:
            emit({"event": "empty"})
        else:
            emit({"event": "final", "text": text, "ms": ms})
    except Exception as e:
        log.exception("asr failed")
        emit({"event": "error", "where": "asr", "message": str(e)})


def main() -> None:
    cfg = SidecarConfig()
    engine = None
    cap = None
    for cmd in read_commands():
        c = cmd.get("cmd")
        try:
            if c == "config":
                cfg = SidecarConfig.from_cmd(cmd)
                if engine:
                    engine.close()
                engine = create_engine(cfg, emit)
                if cap is not None:
                    cap.cancel()
                cap = MicCapture(cfg.mic)
                cap.on_block = LevelEmitter(emit).on_block
                emit({"event": "ready", "engine": engine.name,
                      "model": cfg.model if engine.name == "gpu"
                      else cfg.cpu_model})
            elif c == "start":
                cap.start()
                emit({"event": "recording"})
            elif c == "stop":
                _finish(cap.stop(), cfg, engine)
            elif c == "cancel":
                cap.cancel()
            elif c == "transcribe_wav":  # test/debug hook (spec §9)
                _finish(load_wav(cmd["path"]), cfg, engine)
            elif c == "shutdown":
                break
            else:
                emit({"event": "error", "where": "ipc",
                      "message": f"unknown cmd: {c}"})
        except Exception as e:
            log.exception("command failed")
            emit({"event": "error", "where": "mic" if c in ("start", "stop")
                  else "asr", "message": str(e)})
    if engine:
        engine.close()


if __name__ == "__main__":
    main()
