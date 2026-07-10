"""GPU-only live partial-decode loop for the recording pill. Best-effort:
isolated from the authoritative final decode (see Global Constraints)."""
import logging
import threading

log = logging.getLogger("sidecar")


class PartialLoop:
    def __init__(self, cap, engine, emit_fn, lock, prompt: str = "",
                 min_ms: int = 700, window_s: int = 30, sr: int = 16000):
        self._cap = cap
        self._engine = engine
        self._emit = emit_fn
        self._lock = lock
        self._prompt = prompt
        # Host-supplied config is untrusted: a non-positive min/window must not
        # zero out the guards (audio[-0:] would select the WHOLE buffer).
        self._min_samples = max(int(min_ms * sr / 1000), 1)
        self._window_samples = max(int(window_s * sr), 1)
        self._last_len = 0
        self._seq = 0
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None

    def tick(self) -> bool:
        if self._stop.is_set():
            return False
        try:
            audio = self._cap.snapshot()
            if len(audio) < self._min_samples or len(audio) <= self._last_len:
                return False
            self._last_len = len(audio)
            window = audio[-self._window_samples:]
            with self._lock:
                text = self._engine.try_partial(window, self._prompt)
            if self._stop.is_set() or not text:
                return False
            self._seq += 1
            self._emit({"event": "partial", "text": text, "seq": self._seq})
            return True
        except Exception:
            log.exception("partial tick failed")   # stderr only, never emit error
            return False

    def start(self, interval_ms: int = 600) -> None:
        # Clamp: a non-positive interval must not busy-spin the loop thread.
        self._interval_s = max(interval_ms, 50) / 1000.0

        def run():
            while not self._stop.wait(self._interval_s):
                self.tick()

        self._thread = threading.Thread(target=run, daemon=True)
        self._thread.start()

    def stop(self) -> None:
        self._stop.set()
        if self._thread is not None:
            self._thread.join(timeout=2.0)
            self._thread = None
