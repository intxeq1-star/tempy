# CmdTerminal

A small native Windows app that gives you a window over your laptop's
command prompt (`cmd.exe`).

Type a command in the box at the bottom, press **Enter** — the command is
sent to a real `cmd.exe` process and everything it prints back appears in
the scrollable pane above.

![feature list: type command -> enter -> output below]

## Get it

`CmdTerminal.exe` in this folder is the ready-to-run program
(64-bit, ~155 KB, no installer, no dependencies, portable).
Copy it anywhere on your laptop and double-click it.

> Requires Windows 10 or Windows 11 (uses system DLLs that always ship with them).

## How to use

1. Double-click `CmdTerminal.exe`.
2. Type any command in the bottom box — e.g. `dir`, `ipconfig`,
   `echo hello`, `cd Desktop`, `python --version`.
3. Press **Enter** (or click **Run**). The output appears above.

### Features

- **Persistent shell** — it's one real `cmd.exe` behind the scenes,
  so `cd`, `set VAR=...`, etc. carry over to your next command.
- **History** — Up / Down arrows cycle through your previous commands.
- **Clear button** — clears the window (typing `clear` or `cls` also works).
- **Restart Shell button** — kills the cmd.exe underneath and starts a
  fresh one (useful if a command hung or you typed `exit`).
- Output can be selected and copied with Ctrl+C as usual.
- Output pane auto-trims after ~3 million characters so memory stays flat.

### Notes & limitations

- Interactive, full-screen programs (e.g. `edit`, `more`, anything that
  draws a text UI) won't work well in a pipe — this window is for
  commands that print text output.
- There's no Ctrl+C to interrupt a running command; use **Restart Shell**
  to get unstuck.
- Commands needing administrator rights only work if you start
  `CmdTerminal.exe` itself as administrator (right-click -> *Run as
  administrator*).
- Because the exe is unsigned, Windows SmartScreen may show a blue
  warning on first run — click *More info -> Run anyway*.

## Source & rebuilding

`main.c` is the whole app (plain C, Win32 API). Rebuild on any OS with
Zig (https://ziglang.org):

```sh
./build.sh
# or: zig cc -target x86_64-windows-gnu -O2 main.c -o CmdTerminal.exe \
#       -Wl,--subsystem,windows -lgdi32
```
