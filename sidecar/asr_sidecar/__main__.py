"""DeadAir ASR sidecar. Protocol: spec.md §3. stdout = protocol only."""
import logging
import sys
import threading
import time
import numpy as np
from .audio import load_wav
from .capture import MicCapture
from .config import SidecarConfig
from .engines import CpuEngine, GpuEngineError, create_engine
from .ipc import emit, read_commands
from .partials import PartialLoop
from .vad import extract_speech
from .waveform import WaveformEmitter

logging.basicConfig(stream=sys.stderr, level=logging.INFO,
                    format="%(asctime)s %(levelname)s %(message)s")
log = logging.getLogger("sidecar")


def _finish(audio: np.ndarray, cfg: SidecarConfig, engine, lock):
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
        with lock:
            text = engine.transcribe(speech, initial_prompt=prompt)
    except GpuEngineError as e:
        # The GPU server crashed and the engine's own respawn/retry couldn't
        # recover it. Unless the user pinned engine=gpu, drop to CPU so this
        # utterance (and later ones) still land instead of vanishing into a
        # WinError-10061 toast.
        if getattr(engine, "name", None) == "gpu" and cfg.engine != "gpu":
            # Secure the fallback BEFORE closing the GPU engine: if the CPU
            # engine can't be built (model not cached + offline), keep the GPU
            # engine bound — its respawn self-heal may recover a later
            # utterance, whereas a closed engine wedges the session.
            try:
                cpu = CpuEngine(model_size=cfg.cpu_model)
            except Exception as e2:
                log.exception("cpu fallback engine failed to construct")
                emit({"event": "error", "where": "asr",
                      "message": f"{e} (cpu fallback unavailable: {e2})"})
                return engine
            emit({"event": "degraded", "engine": "cpu", "reason": str(e)})
            try:
                engine.close()
            except Exception:
                pass
            engine = cpu
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


def _maybe_start_partials(cfg, engine, cap, emit_fn, lock):
    """Start a PartialLoop iff partials are enabled AND the engine is GPU.
    Returns the running loop, or None."""
    if not cfg.partials or getattr(engine, "name", None) != "gpu":
        return None
    loop = PartialLoop(cap, engine, emit_fn, lock,
                       prompt=", ".join(cfg.dictionary),
                       min_ms=cfg.partial_min_ms,
                       window_s=cfg.partial_window_s)
    loop.start(interval_ms=cfg.partial_interval_ms)
    return loop


def main() -> None:
    cfg = SidecarConfig()
    engine = None
    cap = None
    partial = None
    server_lock = threading.Lock()

    def stop_partial():
        nonlocal partial
        if partial is not None:
            partial.stop()
            partial = None

    try:
        for cmd in read_commands():
            c = cmd.get("cmd")
            try:
                if c == "config":
                    stop_partial()
                    cfg = SidecarConfig.from_cmd(cmd)
                    if engine:
                        try:
                            engine.close()
                        finally:
                            # Even if close() raises: never keep dictating
                            # against a dead engine — None makes start/stop
                            # answer clearly until a config succeeds.
                            engine = None
                    # Cancel the old capture BEFORE create_engine can raise:
                    # a failed reconfig mid-recording must not leave the mic
                    # hot (the stop guard below never reaches cap.stop()).
                    if cap is not None:
                        cap.cancel()
                    engine = create_engine(cfg, emit)
                    cap = MicCapture(cfg.mic)
                    cap.on_block = WaveformEmitter(emit).on_block
                    emit({"event": "ready", "engine": engine.name,
                          "model": cfg.model if engine.name == "gpu"
                          else cfg.cpu_model})
                elif c == "start":
                    if engine is None:
                        emit({"event": "error", "where": "asr", "message":
                              "engine not configured (previous config "
                              "failed) — fix settings and save again"})
                        continue
                    stop_partial()
                    cap.start()
                    emit({"event": "recording"})
                    partial = _maybe_start_partials(cfg, engine, cap, emit,
                                                    server_lock)
                elif c == "stop":
                    if engine is None:
                        emit({"event": "error", "where": "asr", "message":
                              "engine not configured (previous config "
                              "failed) — fix settings and save again"})
                        continue
                    stop_partial()
                    engine = _finish(cap.stop(), cfg, engine, server_lock)
                elif c == "cancel":
                    stop_partial()
                    cap.cancel()
                elif c == "transcribe_wav":  # test/debug hook (spec §9)
                    engine = _finish(load_wav(cmd["path"]), cfg, engine,
                                     server_lock)
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
        stop_partial()
        if cap is not None:
            cap.cancel()
        if engine:
            engine.close()


if __name__ == "__main__":
    main()
