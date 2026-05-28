import io
import sys
import os
import pytest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..'))

from WhisperTranscriber import (
    convert_seconds_to_srt_time,
    seconds_to_hms,
    _is_hallucination,
    _pair_channels,
    write_srt_file,
)


# ── imports ───────────────────────────────────────────────────────────────────

def test_import_faster_whisper():
    from faster_whisper import WhisperModel  # noqa: F401

def test_import_sentence_transformers():
    from sentence_transformers import SentenceTransformer  # noqa: F401

def test_import_fugashi():
    import fugashi  # noqa: F401

def test_import_av():
    import av  # noqa: F401

def test_import_ctranslate2():
    import ctranslate2  # noqa: F401


# ── convert_seconds_to_srt_time ───────────────────────────────────────────────

def test_srt_time_zero():
    assert convert_seconds_to_srt_time(0) == "00:00:00,000"

def test_srt_time_milliseconds():
    assert convert_seconds_to_srt_time(1.5) == "00:00:01,500"

def test_srt_time_minutes():
    assert convert_seconds_to_srt_time(90) == "00:01:30,000"

def test_srt_time_hours():
    assert convert_seconds_to_srt_time(3661.25) == "01:01:01,250"

def test_srt_time_ms_rounding():
    assert convert_seconds_to_srt_time(0.001) == "00:00:00,001"


# ── seconds_to_hms ────────────────────────────────────────────────────────────

def test_hms_zero():
    assert seconds_to_hms(0) == "00:00:00"

def test_hms_one_hour():
    assert seconds_to_hms(3600) == "01:00:00"


# ── _is_hallucination ─────────────────────────────────────────────────────────

def test_hallucination_empty():
    assert _is_hallucination("") is True

def test_hallucination_whitespace():
    assert _is_hallucination("   ") is True

def test_hallucination_latin_3plus():
    assert _is_hallucination("ABC") is True

def test_hallucination_latin_2_ok():
    assert _is_hallucination("た2") is False

def test_hallucination_numeric_artifact():
    assert _is_hallucination("た20") is True

def test_hallucination_japanese_ok():
    assert _is_hallucination("こんにちは") is False

def test_hallucination_mixed_ok():
    assert _is_hallucination("ありがとう！") is False


# ── _pair_channels ────────────────────────────────────────────────────────────

def test_pair_channels_overlap():
    segs = [
        {'start': 0.0, 'end': 2.0, 'text': 'left',  '_ch': 'left'},
        {'start': 1.0, 'end': 3.0, 'text': 'right', '_ch': 'right'},
    ]
    result = _pair_channels(segs)
    assert len(result) == 1
    assert result[0]['text'] == 'left\nright'
    assert result[0]['start'] == 0.0
    assert result[0]['end'] == 3.0

def test_pair_channels_no_overlap():
    segs = [
        {'start': 0.0, 'end': 1.0, 'text': 'A', '_ch': 'left'},
        {'start': 2.0, 'end': 3.0, 'text': 'B', '_ch': 'right'},
    ]
    result = _pair_channels(segs)
    assert len(result) == 2

def test_pair_channels_same_channel_no_merge():
    segs = [
        {'start': 0.0, 'end': 2.0, 'text': 'A', '_ch': 'left'},
        {'start': 1.0, 'end': 3.0, 'text': 'B', '_ch': 'left'},
    ]
    result = _pair_channels(segs)
    assert len(result) == 2

def test_pair_channels_with_translation():
    segs = [
        {'start': 0.0, 'end': 2.0, 'text': 'L', '_ch': 'left',  'translation': 'tL'},
        {'start': 1.0, 'end': 3.0, 'text': 'R', '_ch': 'right', 'translation': 'tR'},
    ]
    result = _pair_channels(segs)
    assert result[0]['translation'] == 'tL\ntR'


# ── write_srt_file ────────────────────────────────────────────────────────────

def test_write_srt_basic(tmp_path):
    segs = [{'start': 0.0, 'end': 1.0, 'text': 'Hello'}]
    out = tmp_path / "out.srt"
    write_srt_file(str(out), segs)
    content = out.read_text(encoding='utf-8')
    assert "1\n" in content
    assert "00:00:00,000 --> 00:00:01,000" in content
    assert "Hello" in content

def test_write_srt_with_translation(tmp_path):
    segs = [{'start': 0.0, 'end': 1.0, 'text': 'Hello', 'translation': '你好'}]
    out = tmp_path / "out.srt"
    write_srt_file(str(out), segs)
    content = out.read_text(encoding='utf-8')
    assert "你好" in content
    assert "Hello" not in content

def test_write_srt_review_mode(tmp_path):
    segs = [{'start': 0.0, 'end': 1.0, 'text': 'Hello', 'translation': '你好'}]
    out = tmp_path / "review.srt"
    write_srt_file(str(out), segs, review=True)
    content = out.read_text(encoding='utf-8')
    assert "Hello" in content
    assert "你好" in content
