import threading
import numpy as np


class MicCapture:
    """Records the full utterance between start() and stop(). 16 kHz mono."""

    def __init__(self, device: str = "default", sr: int = 16000):
        self._sr = sr
        self._device = None if device in (None, "", "default") else device
        self._frames: list[np.ndarray] = []
        self._stream = None
        self._recording = False
        self._lock = threading.Lock()
        self.on_block = None  # optional per-block hook (level events); never breaks capture

    def _on_frames(self, mono: np.ndarray) -> None:
        with self._lock:
            recording = self._recording
            if recording:
                self._frames.append(mono)
        if recording and self.on_block is not None:
            try:
                self.on_block(mono)
            except Exception:
                pass  # level emission must never break capture

    def _callback(self, indata, frames, time_info, status):
        self._on_frames(indata[:, 0].copy())

    def start(self) -> None:
        import sounddevice as sd
        self._close_stream()
        with self._lock:
            self._frames = []
            self._recording = True
        self._stream = sd.InputStream(
            samplerate=self._sr, channels=1, dtype="float32",
            device=self._device, callback=self._callback)
        self._stream.start()

    def stop(self) -> np.ndarray:
        self._close_stream()
        with self._lock:
            self._recording = False
            frames, self._frames = self._frames, []
        return (np.concatenate(frames) if frames
                else np.zeros(0, dtype=np.float32))

    def cancel(self) -> None:
        self._close_stream()
        with self._lock:
            self._recording = False
            self._frames = []

    def _close_stream(self) -> None:
        if self._stream is not None:
            self._stream.stop()
            self._stream.close()
            self._stream = None
