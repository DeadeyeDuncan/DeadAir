import wave
import numpy as np


def load_wav(path: str) -> np.ndarray:
    """Load a 16 kHz mono 16-bit WAV as float32 in [-1, 1]."""
    with wave.open(path, "rb") as w:
        if w.getframerate() != 16000 or w.getnchannels() != 1:
            raise ValueError(f"{path}: need 16kHz mono, got "
                             f"{w.getframerate()}Hz/{w.getnchannels()}ch")
        raw = w.readframes(w.getnframes())
    return np.frombuffer(raw, dtype=np.int16).astype(np.float32) / 32768.0


def to_wav_bytes(audio: np.ndarray, sr: int = 16000) -> bytes:
    import io
    buf = io.BytesIO()
    with wave.open(buf, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(sr)
        w.writeframes((np.clip(audio, -1, 1) * 32767).astype(np.int16).tobytes())
    return buf.getvalue()
