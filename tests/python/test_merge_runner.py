import importlib.util
import sys
from pathlib import Path


def load_runner():
    path = Path(__file__).parents[2] / "projects" / "Automatic Duplicate Merge" / "momentus_merge_runner.py"
    spec = importlib.util.spec_from_file_location("momentus_merge_runner", path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def test_pair_identity_does_not_depend_on_spreadsheet_row():
    runner = load_runner()
    first = runner.Pair(2, "00000001", "00000002")
    moved = runner.Pair(999, "00000001", "00000002")
    assert first.pair_id == moved.pair_id


def test_merge_graph_rejects_chain():
    runner = load_runner()
    pairs = [
        runner.Pair(2, "00000001", "00000002"),
        runner.Pair(3, "00000002", "00000003"),
    ]
    try:
        runner.validate_merge_graph(pairs)
    except ValueError as error:
        assert "both source and target" in str(error)
    else:
        raise AssertionError("Unsafe chain was accepted")
