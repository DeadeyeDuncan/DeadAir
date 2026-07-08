import numpy as np
from asr_sidecar.waveform import downsample_minmax, WaveformEmitter


def test_downsample_returns_two_floats_per_bin():
    block = np.linspace(-1.0, 1.0, 800, dtype=np.float32)
    out = downsample_minmax(block, bins=8)
    assert len(out) == 16
    assert out[0] == -1.0 and out[-1] == 1.0          # global min/max at the ends
    assert all(-1.0 <= v <= 1.0 for v in out)


def test_downsample_empty_block_is_zeros():
    assert downsample_minmax(np.zeros(0, dtype=np.float32), bins=8) == [0.0] * 16


def test_emitter_throttles_and_shapes_event():
    events = []
    clock = [0.0]
    em = WaveformEmitter(events.append, bins=8, min_interval_ms=25,
                         now_fn=lambda: clock[0])
    block = np.full(400, 0.5, dtype=np.float32)
    em.on_block(block)          # t=0 -> emits
    clock[0] = 0.010
    em.on_block(block)          # 10ms later -> throttled
    clock[0] = 0.030
    em.on_block(block)          # 30ms later -> emits
    assert len(events) == 2
    assert events[0]["event"] == "waveform"
    assert len(events[0]["samples"]) == 16
