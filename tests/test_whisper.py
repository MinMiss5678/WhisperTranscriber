import io
import sys
import os
import pytest
import numpy as np
from unittest.mock import patch, MagicMock

sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..'))

from WhisperTranscriber import (
    convert_seconds_to_srt_time,
    seconds_to_hms,
    _is_hallucination,
    _pair_channels,
    write_srt_file,
    merged_segments_with_model,
    _googletrans_fallback,
)


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

def test_hallucination_ta_digit_flagged():
    assert _is_hallucination("た2") is True

def test_hallucination_numeric_artifact():
    assert _is_hallucination("た20") is True

def test_hallucination_japanese_ok():
    assert _is_hallucination("こんにちは") is False

def test_hallucination_mixed_ok():
    assert _is_hallucination("ありがとう！") is False

def test_hallucination_japanese_english_loanword_ok():
    assert _is_hallucination("YouTubeで見た") is False

def test_hallucination_japanese_english_brand_ok():
    assert _is_hallucination("ASMRの動画") is False

def test_hallucination_pure_latin_flagged():
    assert _is_hallucination("Thank you for watching") is True


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

def test_write_srt_utf8_japanese(tmp_path):
    segs = [{'start': 0.0, 'end': 2.0, 'text': 'よろしくお願いします'}]
    out = tmp_path / "jp.srt"
    write_srt_file(str(out), segs)
    raw = out.read_bytes()
    assert raw[:3] != b'\xef\xbb\xbf', "SRT should not have UTF-8 BOM"
    content = raw.decode('utf-8')
    assert 'よろしくお願いします' in content

def test_write_srt_multiple_segments(tmp_path):
    segs = [
        {'start': 0.0, 'end': 1.0, 'text': 'A'},
        {'start': 2.0, 'end': 3.0, 'text': 'B'},
        {'start': 4.0, 'end': 5.0, 'text': 'C'},
    ]
    out = tmp_path / "multi.srt"
    write_srt_file(str(out), segs)
    content = out.read_text(encoding='utf-8')
    assert "1\n" in content
    assert "2\n" in content
    assert "3\n" in content


# ── merged_segments_with_model ────────────────────────────────────────────────

def _make_mock_model(sim_value):
    """Build a mock sentence model that returns embeddings yielding the given cosine similarity."""
    mock_model = MagicMock()
    # Two orthogonal unit vectors; cosine sim = sim_value achieved via dot product
    # Simple approach: use [1,0] and [cos θ, sin θ] where cos θ = sim_value
    v1 = np.array([1.0, 0.0])
    angle = np.arccos(np.clip(sim_value, -1, 1))
    v2 = np.array([np.cos(angle), np.sin(angle)])
    mock_model.encode.return_value = np.array([v1, v2])
    return mock_model


def _seg(start, end, text):
    return {'start': start, 'end': end, 'text': text}


@patch('WhisperTranscriber._get_sentence_model')
def test_merge_happens_when_all_conditions_met(mock_get_model):
    mock_get_model.return_value = _make_mock_model(0.9)  # sim > 0.7
    segs = [_seg(0.0, 1.0, 'おはよう'), _seg(1.2, 2.0, 'ございます')]
    # gap=0.2 ≤ 1.0, duration=2.0 ≤ 6.0, chars ok
    result = merged_segments_with_model(segs)
    assert len(result) == 1
    assert result[0]['text'] == 'おはよう ございます'
    assert result[0]['start'] == 0.0
    assert result[0]['end'] == 2.0


@patch('WhisperTranscriber._get_sentence_model')
def test_no_merge_when_similarity_too_low(mock_get_model):
    mock_get_model.return_value = _make_mock_model(0.5)  # sim < 0.7
    segs = [_seg(0.0, 1.0, 'おはよう'), _seg(1.2, 2.0, 'さようなら')]
    result = merged_segments_with_model(segs)
    assert len(result) == 2


@patch('WhisperTranscriber._get_sentence_model')
def test_no_merge_when_gap_too_large(mock_get_model):
    mock_get_model.return_value = _make_mock_model(0.9)
    segs = [_seg(0.0, 1.0, 'おはよう'), _seg(2.5, 3.5, 'ございます')]
    # gap = 1.5 > 1.0
    result = merged_segments_with_model(segs)
    assert len(result) == 2


@patch('WhisperTranscriber._get_sentence_model')
def test_no_merge_when_combined_text_too_long(mock_get_model):
    mock_get_model.return_value = _make_mock_model(0.9)
    long_a = 'あ' * 25
    long_b = 'い' * 25  # combined = 51 chars (with space) > 45
    segs = [_seg(0.0, 1.0, long_a), _seg(1.2, 2.0, long_b)]
    result = merged_segments_with_model(segs)
    assert len(result) == 2


@patch('WhisperTranscriber._get_sentence_model')
def test_no_merge_when_combined_duration_too_long(mock_get_model):
    mock_get_model.return_value = _make_mock_model(0.9)
    segs = [_seg(0.0, 3.0, 'あいう'), _seg(3.5, 7.0, 'えお')]
    # proposed_duration = 7.0 > 6.0
    result = merged_segments_with_model(segs)
    assert len(result) == 2


@patch('WhisperTranscriber._get_sentence_model')
def test_long_segment_passes_through_without_merge(mock_get_model):
    mock_get_model.return_value = _make_mock_model(0.9)
    segs = [_seg(0.0, 7.0, 'あいうえおかきくけこ'), _seg(7.5, 8.5, 'さしすせそ')]
    # first segment duration = 7.0 > 6.0, should pass through directly
    result = merged_segments_with_model(segs)
    assert len(result) == 2
    assert result[0]['text'] == 'あいうえおかきくけこ'


# ── _googletrans_fallback ─────────────────────────────────────────────────────

@patch('WhisperTranscriber.asyncio.run')
def test_googletrans_fallback_sets_translation(mock_asyncio_run, capsys):
    chunk = [{'start': 0.0, 'end': 1.0, 'text': 'こんにちは', 'translation': ''}]
    mock_asyncio_run.return_value = [{'start': 0.0, 'end': 1.0, 'text': 'こんにちは', 'translation': 'Hello'}]
    _googletrans_fallback(chunk, 0, 1, 'en', 'test')
    captured = capsys.readouterr()
    assert '[Fallback]' in captured.out
    assert '1–1' in captured.out
