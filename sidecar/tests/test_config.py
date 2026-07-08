from asr_sidecar.config import SidecarConfig


def test_defaults():
    c = SidecarConfig()
    assert c.engine == "auto" and c.cpu_model == "small" and c.dictionary == []


def test_from_cmd_ignores_unknown_keys():
    c = SidecarConfig.from_cmd({"cmd": "config", "engine": "cpu",
                                "dictionary": ["DeadMind"], "bogus": 1})
    assert c.engine == "cpu" and c.dictionary == ["DeadMind"]


def test_partial_defaults():
    c = SidecarConfig()
    assert c.partials is True
    assert c.partial_interval_ms == 600
    assert c.partial_min_ms == 700
    assert c.partial_window_s == 30


def test_from_cmd_reads_partial_keys():
    c = SidecarConfig.from_cmd({"cmd": "config", "partials": False,
                                "partial_interval_ms": 400})
    assert c.partials is False and c.partial_interval_ms == 400
