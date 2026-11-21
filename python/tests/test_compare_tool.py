"""testdata/tools/compare.py: the matching rules of spec/testdata.md."""

from __future__ import annotations

import json


def _write(folder, name, text):
    folder.mkdir(parents=True, exist_ok=True)
    (folder / name).write_text(text, encoding="utf-8", newline="")


def test_json_rules(compare):
    assert compare.compare_json({"a": 1, "b": [1.5, "x", None, True]}, {"b": [1.5, "x", None, True], "a": 1.0}) == []
    assert compare.compare_json({"a": 0.1}, {"a": 0.1 + 1e-7}) == []
    assert compare.compare_json({"a": 1e9}, {"a": 1e9 + 0.5}) == []  # relative
    assert compare.compare_json({"a": 0.1}, {"a": 0.1 + 1e-5}) != []
    assert compare.compare_json({"a": "x"}, {"a": "y"}) != []
    assert compare.compare_json({"a": True}, {"a": 1}) != []
    assert compare.compare_json({"a": 1}, {"b": 1}) != []
    assert compare.compare_json([1, 2], [1]) != []
    assert compare.compare_json({"a": None}, {"a": 0}) != []


def test_integers_must_be_equal_exactly(compare):
    parse = lambda s: json.loads(s, parse_int=compare._Int)  # noqa: E731
    assert compare.compare_json(parse('{"units": 4}'), parse('{"units": 4}')) == []
    assert compare.compare_json(parse('{"units": 4}'), parse('{"units": 5}')) != []
    assert compare.compare_json(parse('{"x": 1}'), parse('{"x": 1.0}')) == []


def test_text_rules(compare):
    assert compare.compare_text("a\r\nb 1.0\r\n", "a\nb 1.0\n") == []
    assert compare.compare_text("| +4.4 °C | 0.1 °C |", "| +4.5 °C | 0.1 °C |") == []
    assert compare.compare_text("| 9.9 °C |", "| 10.0 °C |") == []
    assert compare.compare_text("| ±0.0 °C |", "| +0.1 °C |") == []
    assert compare.compare_text("| −0.1 °C |", "| ±0.0 °C |") == []
    assert compare.compare_text("| +4.4 °C |", "| +4.6 °C |") != []  # two units
    assert compare.compare_text("| 4.4 °C |", "| 4.40 °C |") != []  # different digits shown
    assert compare.compare_text("| 4.4 °C |", "| 4.5 °F |") != []  # the text differs
    assert compare.compare_text("| 4.4 °C |", "| 4.5 °C |", allow_last_digit=False) != []
    assert compare.compare_text("a\nb", "a") != []


def test_folders(compare, tmp_path):
    e, a = tmp_path / "e", tmp_path / "a"
    _write(e, "results.json", '{"x": 1.5}')
    _write(e, "report.md", "Mean 4.4 °C\r\n")
    _write(a, "results.json", '{"x": 1.5000000001}')
    _write(a, "report.md", "Mean 4.5 °C\n")
    assert compare.compare_dirs(str(e), str(a)) == []
    assert compare.main([str(e), str(a)]) == 0

    _write(a, "results.json", '{"x": 2.5}')
    diffs = compare.compare_dirs(str(e), str(a))
    # The JSON differs, so the one-digit allowance in the report doesn't apply either.
    assert any("results.json" in d for d in diffs) and any("report.md" in d for d in diffs)
    assert compare.main([str(e), str(a)]) == 1

    _write(a, "extra.md", "")
    assert any("extra.md: not expected" in d for d in compare.compare_dirs(str(e), str(a)))
