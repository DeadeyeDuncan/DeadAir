"""Silero VAD via faster-whisper's vendored copy (onnxruntime, no torch)."""
import numpy as np
from faster_whisper.vad import VadOptions, get_speech_timestamps


def extract_speech(audio: np.ndarray, sr: int = 16000,
                   pad_ms: int = 200) -> np.ndarray | None:
    ts = get_speech_timestamps(audio, VadOptions(min_silence_duration_ms=500))
    if not ts:
        return None
    pad = int(sr * pad_ms / 1000)
    # Apply padding and merge overlapping regions
    padded = [(max(0, t["start"] - pad), min(len(audio), t["end"] + pad)) for t in ts]
    # Merge overlapping intervals
    padded.sort()
    merged = []
    for start, end in padded:
        if merged and start <= merged[-1][1]:
            merged[-1] = (merged[-1][0], max(merged[-1][1], end))
        else:
            merged.append((start, end))
    chunks = [audio[s:e] for s, e in merged]
    return np.concatenate(chunks)
