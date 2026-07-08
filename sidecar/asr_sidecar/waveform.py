"""PCM waveform events for the host's oscilloscope (replaces RMS levels)."""
import time

import numpy as np


def downsample_minmax(block: np.ndarray, bins: int = 8) -> list[float]:
    """Reduce an audio block to `2*bins` floats: (min,max) per contiguous bin.

    Gives the host a real (downsampled) oscilloscope envelope, not an RMS
    magnitude. Empty block -> zeros so the scope drains to a flat line.
    """
    if len(block) == 0:
        return [0.0] * (2 * bins)
    parts = np.array_split(block, bins)
    out: list[float] = []
    for p in parts:
        if len(p) == 0:
            out += [0.0, 0.0]
        else:
            out += [float(np.clip(p.min(), -1.0, 1.0)),
                    float(np.clip(p.max(), -1.0, 1.0))]
    return out


class WaveformEmitter:
    """Throttled waveform-event emitter; at most one event per min_interval_ms."""

    def __init__(self, emit_fn, bins: int = 8, min_interval_ms: int = 25,
                 now_fn=time.monotonic):
        self._emit = emit_fn
        self._bins = bins
        self._interval = min_interval_ms / 1000.0
        self._now = now_fn
        self._last = float("-inf")

    def on_block(self, block: np.ndarray) -> None:
        now = self._now()
        if now - self._last < self._interval:
            return
        self._last = now
        self._emit({"event": "waveform",
                    "samples": [round(v, 3) for v in
                                downsample_minmax(block, self._bins)]})
