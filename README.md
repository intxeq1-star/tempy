# Prefect

One desk for a school of **ninety Windows laptops**.

You sit here. They sit in Lab A, Lab B, two trolleys, and the office. You point at one machine, or a room, or the whole hall — and you write an order. PowerShell runs. A file goes down the corridor. PowerPoint stays in the locker until a student knocks and you turn the key.

**Vérault is gone.** This hall is the work.

## What it does today

1. **The desk.** Remote PowerShell on every laptop at once, or on the one that needs it. A shelf of safe school scripts (who is signed in, disk, battery, startup, inventory). Push a file from the cupboard and, if you wish, run it. Hold screens. Speak to the room. Exam mode. Restart. Every order is written in the book.

2. **The locker.** Paint, PowerPoint, games, Store, Discord, camera — shut by default. Word, Excel, Teams, OneNote, Notepad stay open: they are the lesson. A student can knock. You open the app for a quarter hour, a period, or one machine.

3. **The web gate — held.** You asked to keep site blocking aside. DNS alone will not hold Chrome, and incognito walks around a hosts file. When we pick it up: Chrome enterprise policy (incognito shut, your block list) with school DNS underneath.

## Open the desk

```bash
./run.sh
```

That makes a local virtualenv if needed and opens the desk on port 8080. Sign in as **`head`** / **`campus90`**.

The ninety machines on the roll are a living campus so you can learn the desk today. Three start dark. Students knock. Orders complete in the console as if the trolley answered.

## A real laptop

On a school Windows PC, as Administrator:

```powershell
$env:PREFECT_SERVER = "https://your-desk"
irm https://your-desk/agent/Install-PrefectAgent.ps1 | iex
```

Or copy `agent/Install-PrefectAgent.ps1` and `agent/PrefectAgent.ps1` and run:

```powershell
.\Install-PrefectAgent.ps1 -Server https://your-desk -RoomId lab-a
```

The task is named **PrefectAgent**, runs as SYSTEM at startup, and writes to `C:\ProgramData\Prefect`. A student may be told the machine is managed. That is the point. To take it off: `Uninstall-PrefectAgent.ps1`.

The agent will not format a disk. The desk refuses wipe-class commands.

## Rooms on the seed roll

| Room | Machines |
| --- | --- |
| Computer Lab A | LAB-A-01 … 24 |
| Computer Lab B | LAB-B-01 … 24 |
| Trolley 1 — English | CART-1-01 … 18 |
| Trolley 2 — Science | CART-2-01 … 18 |
| Staff | STAFF-01 … 06 |

## Stack

Python 3, FastAPI, SQLite in `data/prefect.db`. The desk is one HTML page, no build step.

## License

Study and reuse the code. Use it only on machines your school owns.
