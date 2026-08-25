#!/bin/sh
# Rebuild CmdTerminal.exe from source (Linux/macOS host, targeting 64-bit Windows).
#
# Needs a Zig toolchain (https://ziglang.org/download/), or the pip package:
#     pip install ziglang        # then replace `zig` with `python3 -m ziglang` below
set -e
cd "$(dirname "$0")"
zig cc -target x86_64-windows-gnu -O2 main.c -o CmdTerminal.exe \
    -Wl,--subsystem,windows -lgdi32
echo "Built CmdTerminal.exe"
