import numpy as np
from asr_sidecar.capture import MicCapture
from asr_sidecar.levels import LevelEmitter, rms_to_level


def test_rms_to_level_silence_is_zero():
    assert rms_to_level(np.zeros(640, dtype=np.float32)) == 0.0


def test_rms_to_level_full_scale_is_one():
    assert rms_to_level(np.ones(640, dtype=np.float32)) == 1.0


def test_rms_to_level_midrange():
    # rms = 0.01 -> (log10(0.01) + 4) / 4 = 0.5
    block = np.full(640, 0.01, dtype=np.float32)
    assert abs(rms_to_level(block) - 0.5) < 0.01


def test_emitter_throttles_and_shapes_event():
    events = []
    clock = [0.0]
    em = LevelEmitter(events.append, min_interval_ms=40,
                      now_fn=lambda: clock[0])
    block = np.full(640, 0.01, dtype=np.float32)
    em.on_block(block)          # t=0 -> emits
    clock[0] = 0.020
    em.on_block(block)          # 20ms later -> throttled
    clock[0] = 0.045
    em.on_block(block)          # 45ms later -> emits
    assert len(events) == 2
    assert events[0] == {"event": "level", "rms": 0.5}


def test_capture_on_block_fires_only_while_recording_and_swallows_errors():
    cap = MicCapture()
    seen = []
    cap.on_block = seen.append
    cap._on_frames(np.ones(160, dtype=np.float32))       # not recording
    cap._recording = True
    cap._on_frames(np.ones(160, dtype=np.float32))       # recording
    assert len(seen) == 1

    def boom(_):
        raise RuntimeError("must not escape")
    cap.on_block = boom
    cap._on_frames(np.ones(160, dtype=np.float32))       # swallowed
    assert len(cap.stop()) == 320                        # frames intact
