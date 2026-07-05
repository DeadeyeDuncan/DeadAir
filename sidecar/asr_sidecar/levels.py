"""Mic-level events for the host's recording indicator (spec: recording-indicator design)."""
import time

import numpy as np


def rms_to_level(block: np.ndarray) -> float:
    """Map a float32 [-1,1] block to a display level in [0,1].

    Log-ish curve so normal speech spans the visual range:
    level = clip((log10(max(rms, 1e-4)) + 4) / 4, 0, 1).
    """
    if len(block) == 0:
        return 0.0
    rms = float(np.sqrt(np.mean(np.square(block, dtype=np.float64))))
    level = (np.log10(max(rms, 1e-4)) + 4.0) / 4.0
    return float(np.clip(level, 0.0, 1.0))


class LevelEmitter:
    """Throttled level-event emitter; at most one event per min_interval_ms."""

    def __init__(self, emit_fn, min_interval_ms: int = 40,
                 now_fn=time.monotonic):
        self._emit = emit_fn
        self._interval = min_interval_ms / 1000.0
        self._now = now_fn
        self._last = float("-inf")

    def on_block(self, block: np.ndarray) -> None:
        now = self._now()
        if now - self._last < self._interval:
            return
        self._last = now
        self._emit({"event": "level", "rms": round(rms_to_level(block), 2)})
