import importlib.util
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import time
import unittest

RESOURCES = Path(__file__).resolve().parents[1] / "LightX2V.Windows/Backend"
sys.path.insert(0, str(RESOURCES))
from prompt_enhancement import ThinkingStream, parse_result, inference_request, command


class StreamTests(unittest.TestCase):
    def test_every_possible_thinking_tag_boundary_and_unicode(self):
        source = '<think>\n思考 🐈，保留构图。</think>\n{"rewritten_prompt":"a cat","wh_ratio":"1:1"}[end of text]'
        for width in range(1, 30):
            events = []
            stream = ThinkingStream(lambda kind, **fields: events.append({"type": kind, **fields}))
            for offset in range(0, len(source), width):
                stream.feed(source[offset:offset + width])
            stream.finish()
            self.assertTrue(stream.closed)
            self.assertEqual(stream.thinking.strip(), "思考 🐈，保留构图。")
            self.assertEqual(''.join(e['delta'] for e in events if e['type'] == 'enhancement_thinking').strip(), stream.thinking.strip())
            self.assertEqual(len([e for e in events if e['type'] == 'enhancement_phase']), 1)
            self.assertEqual(''.join(e['delta'] for e in events if e['type'] == 'enhancement_prompt'), 'a cat')

    def test_prompt_stream_decodes_all_json_escapes_at_every_boundary(self):
        prompt = '中文 🐈 “光影”\n"quoted" \\ path /\t\r\b\f End'
        for ascii_only in [False, True]:
            for first in [True, False]:
                fields = dict(rewritten_prompt=prompt, wh_ratio='3:2', ratio_follow='')
                if not first: fields = dict(wh_ratio='3:2', ratio_follow='', rewritten_prompt=prompt)
                answer = json.dumps(fields, ensure_ascii=ascii_only)
                answer = answer.replace('rewritten_prompt', r'rewritten_\u0070rompt')
                source = '分析</think>\n' + answer
                for width in range(1, 40):
                    events = []
                    stream = ThinkingStream(lambda kind, **kw: events.append(dict(type=kind, **kw)))
                    for offset in range(0, len(source), width):
                        stream.feed(source[offset:offset + width])
                    stream.finish()
                    actual = ''.join(e['delta'] for e in events if e['type'] == 'enhancement_prompt')
                    self.assertEqual(actual, prompt, (ascii_only, first, width))
                    # No dangling surrogate halves may reach NDJSON / Swift.
                    actual.encode('utf-8')

    def test_prompt_is_visible_before_json_and_model_output_complete(self):
        events = []
        stream = ThinkingStream(lambda kind, **fields: events.append(dict(type=kind, **fields)))
        stream.feed('思考</think>{"rewritten_prompt":"A quiet forest')
        self.assertEqual(''.join(e['delta'] for e in events if e['type'] == 'enhancement_prompt'), 'A quiet forest')
        self.assertFalse(any(e['type'] == 'enhancement_result' for e in events))
        with self.assertRaises(ValueError): parse_result(stream.raw, 't2i', 0)
        stream.feed(' at sunrise","wh_ratio":"1:1"}[end of text]')
        stream.finish()
        self.assertEqual(''.join(e['delta'] for e in events if e['type'] == 'enhancement_prompt'), 'A quiet forest at sunrise')

    def test_metadata_never_leaks_into_prompt_and_incomplete_escape_is_withheld(self):
        events = []
        stream = ThinkingStream(lambda kind, **fields: events.append(dict(type=kind, **fields)))
        stream.feed('思考</think>{"wh_ratio":"rewritten_prompt","rewritten_prompt":"A dog \\uD83D')
        stream.finish()
        self.assertEqual(''.join(e['delta'] for e in events if e['type'] == 'enhancement_prompt'), 'A dog ')
        stream.feed('\\uDC15","ratio_follow":""}')
        stream.finish()
        self.assertEqual(''.join(e['delta'] for e in events if e['type'] == 'enhancement_prompt'), 'A dog 🐕')

    def test_malformed_surrogates_and_duplicate_keys_are_rejected(self):
        for value in [r'\uD83D!', r'\uDC15', r'\uD83D\n']:
            stream = ThinkingStream(lambda *args, **kwargs: None)
            with self.subTest(value=value), self.assertRaises(ValueError):
                stream.feed('思考</think>{"rewritten_prompt":"' + value + '"}')
        with self.assertRaises(ValueError):
            parse_result('thought</think>{"rewritten_prompt":"one","rewritten_prompt":"two","wh_ratio":"1:1"}[end of text]', 't2i', 0)

    def test_incomplete_and_invalid_outputs_are_rejected(self):
        invalid = ["still thinking", 'thought</think>{"rewritten_prompt":"x","wh_ratio":"1:1"}',
                   'thought</think>{"rewritten_prompt":"","wh_ratio":"1:1"}[end of text]',
                   'thought</think>{"rewritten_prompt":"x","wh_ratio":"0:1"}[end of text]',
                   'thought</think>{"rewritten_prompt":"x","wh_ratio":"1:1","extra":1}[end of text]']
        for raw in invalid:
            with self.subTest(raw=raw), self.assertRaises(ValueError):
                parse_result(raw, 't2i', 0)
        for ratio, follow in [('1:1', '<image1>'), ('', ''), ('', '<image3>'), ('', '<image0>')]:
            raw = '思考</think>' + json.dumps(dict(rewritten_prompt='x', wh_ratio=ratio, ratio_follow=follow))
            with self.subTest(follow=follow), self.assertRaises(ValueError):
                parse_result(raw, 'i2i', 2)



if __name__ == "__main__": unittest.main()
