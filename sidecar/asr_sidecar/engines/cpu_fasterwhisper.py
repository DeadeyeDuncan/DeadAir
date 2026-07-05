from faster_whisper import WhisperModel
from .base import AsrEngine


class CpuEngine(AsrEngine):
    """CPU-only. CTranslate2 has no AMD-GPU path on Windows — never 'gpu' here."""
    name = "cpu"

    def __init__(self, model_size: str = "small"):
        self._model = WhisperModel(model_size, device="cpu", compute_type="int8")

    def transcribe(self, audio, initial_prompt=""):
        segments, _ = self._model.transcribe(
            audio, initial_prompt=initial_prompt or None)
        return " ".join(s.text.strip() for s in segments).strip()
