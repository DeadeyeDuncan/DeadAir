"""DeadAir ASR sidecar. Protocol: spec.md §3. stdout = protocol only."""
import logging
import sys
import time
import numpy as np
from .audio import load_wav
from .capture import MicCapture
from .config import SidecarConfig
from .engines import CpuEngine, GpuEngineError, create_engine
from .ipc import emit, read_commands
from .waveform import WaveformEmitter
from .vad import extract_speech

logging.basicConfig(stream=sys.stderr, level=logging.INFO,
                    format="%(asctime)s %(levelname)s %(message)s")
log = logging.getLogger("sidecar")


def _finish(audio: np.ndarray, cfg: SidecarConfig, engine):
    """Transcribe one utterance. Returns the engine to use for the next one —
    normally the same, but a CPU engine if a crashed GPU server couldn't be
    recovered (words are never lost). Emits final/empty/error events."""
    t0 = time.monotonic()
    try:
        speech = extract_speech(audio) if len(audio) else None
    except Exception as e:
        log.exception("vad failed")
        emit({"event": "error", "where": "asr", "message": str(e)})
        return engine
    if speech is None:
        emit({"event": "empty"})
        return engine
    prompt = ", ".join(cfg.dictionary)
    try:
        text = engine.transcribe(speech, initial_prompt=prompt)
    except GpuEngineError as e:
        # The GPU server crashed and the engine's own respawn/retry couldn't
        # recover it. Unless the user pinned engine=gpu, drop to CPU so this
        # utterance (and later ones) still land instead of vanishing into a
        # WinError-10061 toast.
        if getattr(engine, "name", None) == "gpu" and cfg.engine != "gpu":
            emit({"event": "degraded", "engine": "cpu", "reason": str(e)})
            try:
                engine.close()
            except Exception:
                pass
            engine = CpuEngine(model_size=cfg.cpu_model)
            try:
                text = engine.transcribe(speech, initial_prompt=prompt)
            except Exception as e2:
                log.exception("cpu fallback failed")
                emit({"event": "error", "where": "asr", "message": str(e2)})
                return engine
        else:
            log.exception("asr failed")
            emit({"event": "error", "where": "asr", "message": str(e)})
            return engine
    except Exception as e:
        log.exception("asr failed")
        emit({"event": "error", "where": "asr", "message": str(e)})
        return engine
    ms = int((time.monotonic() - t0) * 1000)
    if not text:
        emit({"event": "empty"})
    else:
        emit({"event": "final", "text": text, "ms": ms})
    return engine


def main() -> None:
    cfg = SidecarConfig()
    engine = None
    cap = None
    try:
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
                    cap.on_block = WaveformEmitter(emit).on_block
                    emit({"event": "ready", "engine": engine.name,
                          "model": cfg.model if engine.name == "gpu"
                          else cfg.cpu_model})
                elif c == "start":
                    cap.start()
                    emit({"event": "recording"})
                elif c == "stop":
                    engine = _finish(cap.stop(), cfg, engine)
                elif c == "cancel":
                    cap.cancel()
                elif c == "transcribe_wav":  # test/debug hook (spec §9)
                    engine = _finish(load_wav(cmd["path"]), cfg, engine)
                elif c == "shutdown":
                    break
                else:
                    emit({"event": "error", "where": "ipc",
                          "message": f"unknown cmd: {c}"})
            except Exception as e:
                log.exception("command failed")
                emit({"event": "error", "where": "mic" if c in ("start", "stop")
                      else "asr", "message": str(e)})
    finally:
        if cap is not None:
            cap.cancel()
        if engine:
            engine.close()


if __name__ == "__main__":
    main()
