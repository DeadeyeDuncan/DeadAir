from asr_sidecar.config import SidecarConfig


def test_defaults():
    c = SidecarConfig()
    assert c.engine == "auto" and c.cpu_model == "small" and c.dictionary == []


def test_from_cmd_ignores_unknown_keys():
    c = SidecarConfig.from_cmd({"cmd": "config", "engine": "cpu",
                                "dictionary": ["DeadMind"], "bogus": 1})
    assert c.engine == "cpu" and c.dictionary == ["DeadMind"]
