import io
import json
from asr_sidecar import ipc


def test_emit_writes_single_json_line():
    out = io.StringIO()
    ipc.emit({"event": "ready", "engine": "cpu"}, stream=out)
    lines = out.getvalue().splitlines()
    assert len(lines) == 1
    assert json.loads(lines[0]) == {"event": "ready", "engine": "cpu"}


def test_read_commands_parses_lines_and_skips_blanks():
    src = io.StringIO('{"cmd":"start"}\n\n{"cmd":"stop"}\n')
    cmds = list(ipc.read_commands(src))
    assert cmds == [{"cmd": "start"}, {"cmd": "stop"}]


def test_read_commands_bad_json_emits_error_and_continues():
    src = io.StringIO('not json\n{"cmd":"start"}\n')
    err_out = io.StringIO()
    cmds = list(ipc.read_commands(src, err_stream=err_out))
    assert cmds == [{"cmd": "start"}]
    err = json.loads(err_out.getvalue().splitlines()[0])
    assert err["event"] == "error" and err["where"] == "ipc"
