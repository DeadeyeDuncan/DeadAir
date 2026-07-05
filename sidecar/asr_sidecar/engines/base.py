from abc import ABC, abstractmethod
import numpy as np


class AsrEngine(ABC):
    name: str = "base"

    @abstractmethod
    def transcribe(self, audio: np.ndarray, initial_prompt: str = "") -> str: ...

    def close(self) -> None:
        pass
