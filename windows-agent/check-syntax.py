#!/usr/bin/env python3
"""Static sanity checker for the C# sources (no compiler available in the sandbox).

Strips comments/strings (regular, verbatim, interpolated, raw) and verifies that
braces/parens/brackets balance per file. Catches gross syntax breakage only — it is
not a compiler.
"""
import sys
from pathlib import Path

def strip_csharp(text):
    out = []
    i, n = 0, len(text)
    state = 'code'  # code, line_comment, block_comment, string, verbatim, raw
    while i < n:
        c = text[i]
        nxt = text[i+1] if i + 1 < n else ''
        if state == 'code':
            if c == '/' and nxt == '/':
                state = 'line_comment'; i += 2; continue
            if c == '/' and nxt == '*':
                state = 'block_comment'; i += 2; continue
            if c == '"':
                if text.startswith('"""', i):
                    state = 'raw'; i += 3; continue
                state = 'string'; i += 1; continue
            if c == "'":
                # char literal: skip until closing quote (handles escapes)
                j = i + 1
                while j < n:
                    if text[j] == '\\': j += 2; continue
                    if text[j] == "'": break
                    j += 1
                i = j + 1; continue
            out.append(c); i += 1; continue
        if state == 'line_comment':
            if c == '\n': state = 'code'; out.append(c)
            i += 1; continue
        if state == 'block_comment':
            if c == '*' and nxt == '/': state = 'code'; i += 2; continue
            if c == '\n': out.append(c)
            i += 1; continue
        if state == 'string':
            if c == '\\': i += 2; continue
            if c == '"': state = 'code'
            i += 1; continue
        if state == 'verbatim':
            if c == '"' and nxt == '"': i += 2; continue
            if c == '"': state = 'code'
            i += 1; continue
        if state == 'raw':
            if text.startswith('"""', i): state = 'code'; i += 3; continue
            i += 1; continue
    return ''.join(out)

def check(path):
    text = path.read_text(encoding='utf-8')
    stripped = strip_csharp(text)
    stack = []
    pairs = {')': '(', ']': '[', '}': '{'}
    line = 1
    for ch in stripped:
        if ch == '\n': line += 1; continue
        if ch in '([{':
            stack.append((ch, line))
        elif ch in ')]}':
            if not stack or stack[-1][0] != pairs[ch]:
                return f"unbalanced '{ch}' at line {line} (stack top: {stack[-1] if stack else 'empty'})"
            stack.pop()
    if stack:
        return f"unclosed '{stack[-1][0]}' from line {stack[-1][1]}"
    return None

def main():
    root = Path(sys.argv[1] if len(sys.argv) > 1 else '.')
    files = sorted(root.rglob('*.cs'))
    bad = 0
    for f in files:
        problem = check(f)
        if problem:
            print(f"FAIL {f}: {problem}")
            bad += 1
        else:
            print(f"ok   {f.relative_to(root)}")
    print(f"\n{len(files) - bad}/{len(files)} files passed balance check")
    sys.exit(1 if bad else 0)

if __name__ == '__main__':
    main()
