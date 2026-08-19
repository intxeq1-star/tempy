"""Prefect — the desk that holds ninety school laptops."""

from __future__ import annotations

import asyncio
import hashlib
import json
import os
import random
import re
import shutil
import sys
from pathlib import Path
from typing import Any

from fastapi import FastAPI, File, Header, HTTPException, Request, UploadFile
from fastapi.responses import FileResponse, JSONResponse
from fastapi.staticfiles import StaticFiles

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(Path(__file__).resolve().parent))

from db import FILES, ROOT as PROJECT, audit, dumps, execute, init, nid, now, one, query  # noqa: E402
from seed import pw, seed  # noqa: E402

WEB = PROJECT / "web"
AGENT = PROJECT / "agent"

app = FastAPI(title="Prefect", docs_url=None, redoc_url=None)
rng = random.Random()


# ---------------------------------------------------------------------------
# Auth
# ---------------------------------------------------------------------------

def actor_of(authorization: str | None) -> dict[str, Any]:
    if not authorization or not authorization.startswith("Bearer "):
        raise HTTPException(401, "Sign in.")
    token = authorization.split(" ", 1)[1].strip()
    row = one(
        """SELECT s.token, u.id, u.username, u.display, u.role
           FROM sessions s JOIN users u ON u.id = s.user_id
           WHERE s.token = ?""",
        (token,),
    )
    if not row:
        raise HTTPException(401, "Session ended.")
    return row


def agent_of(authorization: str | None) -> dict[str, Any]:
    if not authorization or not authorization.startswith("Bearer "):
        raise HTTPException(401, "Agent token missing.")
    token = authorization.split(" ", 1)[1].strip()
    row = one("SELECT * FROM devices WHERE agent_token = ?", (token,))
    if not row:
        raise HTTPException(401, "Unknown agent.")
    return row


# ---------------------------------------------------------------------------
# Domain helpers
# ---------------------------------------------------------------------------

def device_public(d: dict[str, Any]) -> dict[str, Any]:
    apps = locker_for(d["id"])
    locked_n = sum(1 for a in apps if a["locked"] and not a["always_allowed"])
    open_n = sum(1 for a in apps if (not a["locked"]) and a["default_locked"])
    return {
        **{k: d[k] for k in d if k != "agent_token"},
        "locked_apps": locked_n,
        "opened_apps": open_n,
        "apps": apps,
    }


def locker_for(device_id: str) -> list[dict[str, Any]]:
    apps = query("SELECT * FROM apps ORDER BY sort, name")
    overrides = {
        r["app_id"]: r
        for r in query("SELECT * FROM device_apps WHERE device_id = ?", (device_id,))
    }
    t = now()
    out = []
    for a in apps:
        ov = overrides.get(a["id"])
        locked = bool(a["default_locked"])
        until = None
        granted_by = None
        if ov:
            if ov["until"] and ov["until"] < t:
                execute(
                    "DELETE FROM device_apps WHERE device_id = ? AND app_id = ?",
                    (device_id, a["id"]),
                )
            else:
                locked = bool(ov["locked"])
                until = ov["until"]
                granted_by = ov["granted_by"]
        if a["always_allowed"]:
            locked = False
        out.append({**a, "locked": locked, "until": until, "granted_by": granted_by})
    return out


def target_devices(ids: list[str] | None, room_id: str | None = None) -> list[dict[str, Any]]:
    if ids:
        q = ",".join("?" * len(ids))
        found = query(f"SELECT * FROM devices WHERE id IN ({q})", tuple(ids))
        if len(found) != len(set(ids)):
            raise HTTPException(400, "One of those machines is not on the roll.")
        return found
    if room_id:
        return query("SELECT * FROM devices WHERE room_id = ?", (room_id,))
    return query("SELECT * FROM devices ORDER BY hostname")


def enqueue(
    actor: str,
    kind: str,
    payload: dict[str, Any],
    devices: list[dict[str, Any]],
    note: str = "",
) -> dict[str, Any]:
    cid = nid()
    t = now()
    target = "all" if len(devices) >= 90 else ",".join(d["hostname"] for d in devices[:8])
    if len(devices) > 8 and len(devices) < 90:
        target += f" +{len(devices) - 8}"
    execute(
        """INSERT INTO commands (id, created_at, actor, kind, payload, note, target)
           VALUES (?, ?, ?, ?, ?, ?, ?)""",
        (cid, t, actor, kind, dumps(payload), note, target),
    )
    for d in devices:
        execute(
            """INSERT INTO command_results
               (id, command_id, device_id, status, stdout, stderr, started_at, finished_at)
               VALUES (?, ?, ?, 'queued', NULL, NULL, NULL, NULL)""",
            (nid(), cid, d["id"]),
        )
    audit(actor, f"command:{kind}", f"{len(devices)} machine(s). {note}".strip())
    return one("SELECT * FROM commands WHERE id = ?", (cid,))  # type: ignore[return-value]


def command_view(c: dict[str, Any]) -> dict[str, Any]:
    results = query(
        """SELECT r.*, d.hostname, d.student, d.room_id
           FROM command_results r JOIN devices d ON d.id = r.device_id
           WHERE r.command_id = ? ORDER BY d.hostname""",
        (c["id"],),
    )
    counts = {"queued": 0, "running": 0, "ok": 0, "fail": 0, "offline": 0}
    for r in results:
        counts[r["status"]] = counts.get(r["status"], 0) + 1
    payload = json.loads(c["payload"]) if isinstance(c["payload"], str) else c["payload"]
    status = "done"
    if counts["queued"] or counts["running"]:
        status = "running"
    elif counts["fail"] and counts["ok"] == 0:
        status = "fail"
    elif counts["fail"] or counts["offline"]:
        status = "partial"
    return {**c, "payload": payload, "results": results, "counts": counts, "status": status}


def simulate_output(kind: str, payload: dict[str, Any], device: dict[str, Any]) -> tuple[str, str, str]:
    """Return stdout, stderr, status for a simulated agent."""
    host = device["hostname"]
    user = device["logged_on"] or "Student"
    if device["status"] == "offline":
        return "", "Host unreachable — last seen offline.", "offline"

    if kind == "powershell":
        body = (payload.get("script") or "").strip()
        low = body.lower()
        if "win32_computersystem" in low or "who is signed" in low or "username" in low:
            out = (
                f"\nName         : {host}\n"
                f"UserName     : CAMPUS\\{user.replace(' ', '.')}\n"
                f"Domain       : CAMPUS\n"
                f"Manufacturer : Dell Inc.\n"
                f"Model        : Latitude 3540\n"
            )
        elif "computerinfo" in low or "osversion" in low:
            out = (
                f"\nWindowsProductName : {device['os']}\n"
                f"OsVersion          : 10.0.{(device['build'] or '22631.3880').split('.')[0]}\n"
                f"OsBuildNumber      : {device['build']}\n"
                f"CsName             : {host}\n"
            )
        elif "psdrive" in low or "free" in low and "disk" in low:
            used = round(256 - float(device["disk_free_gb"] or 80), 1)
            out = (
                "\nName UsedGB FreeGB\n"
                "---- ------ ------\n"
                f"C     {used:<6} {device['disk_free_gb']}\n"
            )
        elif "battery" in low:
            if device["battery"] is None:
                out = "\n(No battery — this machine is on the lab bench.)\n"
            else:
                out = (
                    f"\nEstimatedChargeRemaining : {device['battery']}\n"
                    f"BatteryStatus            : 2\n"
                    f"EstimatedRunTime         : {max(12, int(device['battery'] or 50) * 3)}\n"
                )
        elif "get-process" in low:
            out = (
                "\nName                 Id      CPU   MB\n"
                "----                 --      ---   --\n"
                "msedge             4200   12.40  410\n"
                "WINWORD            1882    3.10  190\n"
                "explorer           1044    1.02  120\n"
                "PrefectAgent       2408    0.40   28\n"
                "OneDrive           3012    0.20   64\n"
            )
        elif "startupcommand" in low:
            out = (
                "\nName            Command                         Location\n"
                "----            -------                         --------\n"
                "OneDrive        OneDrive.exe /background        HKU\\...\\Run\n"
                "Prefect         PrefectAgent.ps1                HKLM\\...\\Run\n"
                "SecurityHealth  SecurityHealthSystray.exe       HKLM\\...\\Run\n"
            )
        elif "temp" in low:
            out = f"Temp cleared for {user}\n"
        elif "w32tm" in low or "resync" in low:
            out = f"The command completed successfully.\n{__import__('datetime').datetime.utcnow().isoformat()}Z\n"
        elif "explorer" in low:
            out = "Explorer restarted.\n"
        elif "uninstall" in low:
            out = (
                "\nDisplayName                         DisplayVersion\n"
                "-----------                         --------------\n"
                "Google Chrome                      128.0.6613.120\n"
                "Microsoft 365 Apps for enterprise  16.0.17928.20114\n"
                "Prefect Agent                      1.0.0\n"
                "Teams                              24295.605.3225.8804\n"
            )
        else:
            preview = body.replace("\n", " ")[:180]
            out = f"[{host}] script accepted ({len(body)} chars)\n{preview}\n\nDone.\n"
        return out, "", "ok"

    if kind == "download":
        name = payload.get("name") or "file.bin"
        dest = payload.get("dest") or r"C:\ProgramData\Prefect\inbox"
        run = payload.get("run")
        out = f"Fetched {name}\nSaved  {dest}\\{name}\n"
        if run:
            out += f"Launched with: {run}\n"
        return out, "", "ok"

    if kind == "message":
        return f"Shown to {user}: {payload.get('text', '')}\n", "", "ok"

    if kind == "lock_screen":
        return "Workstation locked. Lesson holds the room.\n", "", "ok"

    if kind == "unlock_screen":
        return "Screen released.\n", "", "ok"

    if kind == "reboot":
        return "Restart scheduled in 15 seconds.\n", "", "ok"

    if kind in {"lock_app", "unlock_app", "exam_on", "exam_off", "policy_sync"}:
        return f"Policy applied ({kind}).\n", "", "ok"

    if kind == "kill":
        return f"Stopped process {payload.get('process')}.\n", "", "ok"

    return f"Done ({kind}).\n", "", "ok"


def apply_side_effects(kind: str, payload: dict[str, Any], device: dict[str, Any], actor: str) -> None:
    did = device["id"]
    if kind == "lock_screen":
        execute(
            "UPDATE devices SET screen_locked = 1, message = ? WHERE id = ?",
            (payload.get("text") or "Eyes up. The lesson has the room.", did),
        )
    elif kind == "unlock_screen":
        execute("UPDATE devices SET screen_locked = 0, message = NULL WHERE id = ?", (did,))
    elif kind == "message":
        execute("UPDATE devices SET message = ? WHERE id = ?", (payload.get("text"), did))
    elif kind == "exam_on":
        execute("UPDATE devices SET exam_mode = 1, screen_locked = 0 WHERE id = ?", (did,))
        for a in query("SELECT * FROM apps WHERE always_allowed = 0 AND category != 'web'"):
            if a["id"] in {"word", "notepad", "calc", "onenote"}:
                continue
            upsert_override(did, a["id"], True, None, actor)
    elif kind == "exam_off":
        execute("UPDATE devices SET exam_mode = 0 WHERE id = ?", (did,))
    elif kind == "lock_app":
        upsert_override(did, payload["app_id"], True, None, actor)
    elif kind == "unlock_app":
        minutes = payload.get("minutes")
        until = now() + int(minutes) * 60 if minutes else None
        upsert_override(did, payload["app_id"], False, until, actor)
    elif kind == "reboot" and device["simulated"]:
        execute(
            "UPDATE devices SET status = 'online', last_seen = ?, screen_locked = 0 WHERE id = ?",
            (now() + 25, did),
        )


def upsert_override(device_id: str, app_id: str, locked: bool, until: int | None, actor: str) -> None:
    execute(
        "DELETE FROM device_apps WHERE device_id = ? AND app_id = ?",
        (device_id, app_id),
    )
    execute(
        """INSERT INTO device_apps (device_id, app_id, locked, until, granted_by)
           VALUES (?, ?, ?, ?, ?)""",
        (device_id, app_id, 1 if locked else 0, until, actor),
    )


# ---------------------------------------------------------------------------
# Simulator — the ninety breathe
# ---------------------------------------------------------------------------

async def simulator() -> None:
    tick = 0
    while True:
        await asyncio.sleep(1.1)
        tick += 1
        t = now()
        try:
            # Finish queued work on simulated machines.
            pending = query(
                """SELECT r.*, d.hostname, d.status AS dstatus, d.simulated, d.logged_on,
                          d.os, d.build, d.battery, d.disk_free_gb, d.id AS did,
                          c.kind, c.payload, c.actor
                   FROM command_results r
                   JOIN devices d ON d.id = r.device_id
                   JOIN commands c ON c.id = r.command_id
                   WHERE r.status IN ('queued', 'running')
                   LIMIT 24"""
            )
            for r in pending:
                if r["status"] == "queued":
                    if r["dstatus"] == "offline":
                        execute(
                            """UPDATE command_results
                               SET status = 'offline', stderr = ?, finished_at = ?
                               WHERE id = ?""",
                            ("Host unreachable — last seen offline.", t, r["id"]),
                        )
                        continue
                    execute(
                        "UPDATE command_results SET status = 'running', started_at = ? WHERE id = ?",
                        (t, r["id"]),
                    )
                    continue
                if r["started_at"] and t - int(r["started_at"]) < rng.choice((0, 0, 1, 1, 2)):
                    continue
                payload = json.loads(r["payload"]) if isinstance(r["payload"], str) else r["payload"]
                device = one("SELECT * FROM devices WHERE id = ?", (r["did"],))
                if not device:
                    continue
                stdout, stderr, status = simulate_output(r["kind"], payload, device)
                if device["simulated"]:
                    apply_side_effects(r["kind"], payload, device, r["actor"])
                execute(
                    """UPDATE command_results
                       SET status = ?, stdout = ?, stderr = ?, finished_at = ?
                       WHERE id = ?""",
                    (status, stdout, stderr, t, r["id"]),
                )

            # Heartbeats for online simulated fleet.
            if tick % 4 == 0:
                execute(
                    """UPDATE devices SET last_seen = ?
                       WHERE simulated = 1 AND status = 'online'""",
                    (t,),
                )
                # A trolley battery ticks down.
                execute(
                    """UPDATE devices SET battery = MAX(4, battery - 1)
                       WHERE simulated = 1 AND battery IS NOT NULL AND status = 'online'
                         AND ABS(RANDOM()) % 7 = 0"""
                )

            # Occasional student knocks on a locked door.
            if tick % 14 == 0 and rng.random() < 0.55:
                maybe_request()

            # Expire timed unlocks quietly.
            execute("DELETE FROM device_apps WHERE until IS NOT NULL AND until < ?", (t,))
        except Exception:
            # The desk must not fall over because a tick failed.
            pass


def maybe_request() -> None:
    open_count = one("SELECT COUNT(*) AS n FROM requests WHERE status = 'waiting'")
    if open_count and open_count["n"] >= 5:
        return
    device = one(
        """SELECT * FROM devices
           WHERE status = 'online' AND simulated = 1 AND room_id != 'staff'
           ORDER BY RANDOM() LIMIT 1"""
    )
    app = one(
        """SELECT * FROM apps
           WHERE default_locked = 1 AND always_allowed = 0
           ORDER BY RANDOM() LIMIT 1"""
    )
    if not device or not app:
        return
    exists = one(
        """SELECT id FROM requests
           WHERE device_id = ? AND app_id = ? AND status = 'waiting'""",
        (device["id"], app["id"]),
    )
    if exists:
        return
    notes = [
        "Need this for the assignment.",
        "Miss said we may use it this period.",
        "Working on the poster.",
        "Just for today's project.",
        "Group work — five minutes.",
        "I have to finish the slides.",
    ]
    execute(
        """INSERT INTO requests (id, created_at, device_id, app_id, note, status)
           VALUES (?, ?, ?, ?, ?, 'waiting')""",
        (nid(), now(), device["id"], app["id"], rng.choice(notes)),
    )


# ---------------------------------------------------------------------------
# Admin API
# ---------------------------------------------------------------------------

@app.post("/api/login")
async def login(body: dict[str, Any]) -> dict[str, Any]:
    username = (body.get("username") or "").strip().lower()
    password = body.get("password") or ""
    user = one("SELECT * FROM users WHERE username = ?", (username,))
    if not user or user["password"] != pw(password):
        raise HTTPException(401, "That desk does not know those keys.")
    token = nid() + nid()
    execute(
        "INSERT INTO sessions (token, user_id, created_at) VALUES (?, ?, ?)",
        (token, user["id"], now()),
    )
    audit(user["display"], "login", username)
    return {
        "token": token,
        "user": {"username": user["username"], "display": user["display"], "role": user["role"]},
    }


@app.post("/api/logout")
async def logout(authorization: str | None = Header(default=None)) -> dict[str, str]:
    if authorization and authorization.startswith("Bearer "):
        execute("DELETE FROM sessions WHERE token = ?", (authorization.split(" ", 1)[1],))
    return {"ok": "out"}


@app.get("/api/me")
async def me(authorization: str | None = Header(default=None)) -> dict[str, Any]:
    u = actor_of(authorization)
    return {"username": u["username"], "display": u["display"], "role": u["role"]}


@app.get("/api/stats")
async def stats(authorization: str | None = Header(default=None)) -> dict[str, Any]:
    actor_of(authorization)
    t = now()
    devices = query("SELECT status, exam_mode, screen_locked, room_id FROM devices")
    online = sum(1 for d in devices if d["status"] == "online")
    waiting = one("SELECT COUNT(*) AS n FROM requests WHERE status = 'waiting'")["n"]
    locked_apps = one("SELECT COUNT(*) AS n FROM apps WHERE default_locked = 1")["n"]
    running = one(
        "SELECT COUNT(*) AS n FROM command_results WHERE status IN ('queued','running')"
    )["n"]
    return {
        "total": len(devices),
        "online": online,
        "offline": len(devices) - online,
        "exam": sum(1 for d in devices if d["exam_mode"]),
        "held": sum(1 for d in devices if d["screen_locked"]),
        "waiting": waiting,
        "locked_apps": locked_apps,
        "running": running,
        "now": t,
    }


@app.get("/api/rooms")
async def rooms(authorization: str | None = Header(default=None)) -> list[dict[str, Any]]:
    actor_of(authorization)
    out = []
    for r in query("SELECT * FROM rooms ORDER BY sort"):
        ds = query("SELECT status FROM devices WHERE room_id = ?", (r["id"],))
        out.append(
            {
                **r,
                "count": len(ds),
                "online": sum(1 for d in ds if d["status"] == "online"),
            }
        )
    return out


@app.get("/api/devices")
async def devices(
    authorization: str | None = Header(default=None),
    room: str | None = None,
    q: str | None = None,
    status: str | None = None,
) -> list[dict[str, Any]]:
    actor_of(authorization)
    sql = """SELECT d.*, r.name AS room_name, r.teacher AS room_teacher, r.sort AS room_sort
           FROM devices d JOIN rooms r ON r.id = d.room_id
           WHERE 1=1"""
    args: list[Any] = []
    if room:
        sql += " AND d.room_id = ?"
        args.append(room)
    if status:
        sql += " AND d.status = ?"
        args.append(status)
    if q:
        sql += " AND (d.hostname LIKE ? OR d.student LIKE ? OR d.ip LIKE ?)"
        like = f"%{q}%"
        args.extend([like, like, like])
    sql += " ORDER BY r.sort, d.hostname"
    return [device_public(d) for d in query(sql, tuple(args))]


@app.get("/api/devices/{device_id}")
async def device_one(device_id: str, authorization: str | None = Header(default=None)) -> dict[str, Any]:
    actor_of(authorization)
    d = one(
        """SELECT d.*, r.name AS room_name, r.teacher AS room_teacher
           FROM devices d JOIN rooms r ON r.id = d.room_id
           WHERE d.id = ?""",
        (device_id,),
    )
    if not d:
        raise HTTPException(404, "Not on the roll.")
    recent = query(
        """SELECT r.*, c.kind, c.created_at, c.actor, c.note
           FROM command_results r JOIN commands c ON c.id = r.command_id
           WHERE r.device_id = ? ORDER BY c.created_at DESC LIMIT 12""",
        (device_id,),
    )
    reqs = query(
        """SELECT r.*, a.name AS app_name
           FROM requests r JOIN apps a ON a.id = r.app_id
           WHERE r.device_id = ? ORDER BY r.created_at DESC LIMIT 8""",
        (device_id,),
    )
    return {**device_public(d), "history": recent, "requests": reqs}


@app.get("/api/apps")
async def apps(authorization: str | None = Header(default=None)) -> list[dict[str, Any]]:
    actor_of(authorization)
    out = []
    for a in query("SELECT * FROM apps ORDER BY sort, name"):
        opened = one(
            """SELECT COUNT(*) AS n FROM device_apps
               WHERE app_id = ? AND locked = 0""",
            (a["id"],),
        )["n"]
        out.append({**a, "opened_on": opened})
    return out


@app.post("/api/apps/{app_id}/default")
async def app_default(
    app_id: str,
    body: dict[str, Any],
    authorization: str | None = Header(default=None),
) -> dict[str, Any]:
    u = actor_of(authorization)
    a = one("SELECT * FROM apps WHERE id = ?", (app_id,))
    if not a:
        raise HTTPException(404, "No such app.")
    if a["always_allowed"]:
        raise HTTPException(400, "The lesson needs this one. It stays open.")
    locked = 1 if body.get("locked") else 0
    execute("UPDATE apps SET default_locked = ? WHERE id = ?", (locked, app_id))
    execute("DELETE FROM device_apps WHERE app_id = ?", (app_id,))
    devices = query("SELECT * FROM devices")
    enqueue(
        u["display"],
        "policy_sync",
        {"app_id": app_id, "locked": bool(locked)},
        devices,
        f"{'Lock' if locked else 'Open'} {a['name']} on the whole campus.",
    )
    audit(u["display"], "app-default", f"{a['name']} locked={locked}")
    return {"ok": True}


@app.post("/api/commands")
async def create_command(
    body: dict[str, Any],
    authorization: str | None = Header(default=None),
) -> dict[str, Any]:
    u = actor_of(authorization)
    kind = body.get("kind") or "powershell"
    allowed = {
        "powershell",
        "download",
        "message",
        "lock_screen",
        "unlock_screen",
        "reboot",
        "lock_app",
        "unlock_app",
        "exam_on",
        "exam_off",
        "kill",
        "policy_sync",
    }
    if kind not in allowed:
        raise HTTPException(400, "Unknown order.")
    if kind == "powershell":
        script = (body.get("script") or "").strip()
        if not script:
            raise HTTPException(400, "Write a command first.")
        if re.search(r"format-volume|remove-item\s+-recurse\s+c:\\|rm\s+-rf\s+/|cipher\s+/w", script, re.I):
            raise HTTPException(400, "That command is refused. The desk will not wipe a disk.")
        payload = {"script": script}
        note = body.get("note") or script.splitlines()[0][:80]
    elif kind == "download":
        fid = body.get("file_id")
        f = one("SELECT * FROM files WHERE id = ?", (fid,)) if fid else None
        if not f:
            raise HTTPException(400, "Choose a file from the cupboard.")
        payload = {
            "file_id": f["id"],
            "name": f["name"],
            "dest": body.get("dest") or r"C:\ProgramData\Prefect\inbox",
            "run": body.get("run") or "",
        }
        note = f"Push {f['name']}"
    elif kind in {"lock_app", "unlock_app"}:
        app_id = body.get("app_id")
        a = one("SELECT * FROM apps WHERE id = ?", (app_id,))
        if not a:
            raise HTTPException(400, "No such app.")
        payload = {"app_id": app_id, "minutes": body.get("minutes")}
        note = f"{'Open' if kind == 'unlock_app' else 'Lock'} {a['name']}"
    elif kind == "message":
        text = (body.get("text") or "").strip()
        if not text:
            raise HTTPException(400, "Write the message.")
        payload = {"text": text}
        note = text[:80]
    elif kind == "kill":
        payload = {"process": body.get("process")}
        note = f"Stop {body.get('process')}"
    else:
        payload = {k: body[k] for k in body if k not in {"kind", "device_ids", "room_id"}}
        note = body.get("note") or kind.replace("_", " ")

    devices = target_devices(body.get("device_ids"), body.get("room_id"))
    if not devices:
        raise HTTPException(400, "No machines in that set.")
    cmd = enqueue(u["display"], kind, payload, devices, note)
    return command_view(cmd)


@app.get("/api/commands")
async def list_commands(authorization: str | None = Header(default=None)) -> list[dict[str, Any]]:
    actor_of(authorization)
    cmds = query("SELECT * FROM commands ORDER BY created_at DESC LIMIT 40")
    return [command_view(c) for c in cmds]


@app.get("/api/commands/{command_id}")
async def get_command(command_id: str, authorization: str | None = Header(default=None)) -> dict[str, Any]:
    actor_of(authorization)
    c = one("SELECT * FROM commands WHERE id = ?", (command_id,))
    if not c:
        raise HTTPException(404, "No such order.")
    return command_view(c)


@app.get("/api/scripts")
async def scripts(authorization: str | None = Header(default=None)) -> list[dict[str, Any]]:
    actor_of(authorization)
    return query("SELECT * FROM scripts ORDER BY sort")


@app.get("/api/requests")
async def list_requests(
    authorization: str | None = Header(default=None),
    status: str = "waiting",
) -> list[dict[str, Any]]:
    actor_of(authorization)
    if status == "all":
        rows = query(
            """SELECT r.*, d.hostname, d.student, d.room_id, a.name AS app_name, a.category
               FROM requests r
               JOIN devices d ON d.id = r.device_id
               JOIN apps a ON a.id = r.app_id
               ORDER BY r.created_at DESC LIMIT 80"""
        )
    else:
        rows = query(
            """SELECT r.*, d.hostname, d.student, d.room_id, a.name AS app_name, a.category
               FROM requests r
               JOIN devices d ON d.id = r.device_id
               JOIN apps a ON a.id = r.app_id
               WHERE r.status = ?
               ORDER BY r.created_at ASC""",
            (status,),
        )
    return rows


@app.post("/api/requests/{request_id}/decide")
async def decide_request(
    request_id: str,
    body: dict[str, Any],
    authorization: str | None = Header(default=None),
) -> dict[str, Any]:
    u = actor_of(authorization)
    req = one("SELECT * FROM requests WHERE id = ?", (request_id,))
    if not req:
        raise HTTPException(404, "No such knock.")
    if req["status"] != "waiting":
        raise HTTPException(400, "Already decided.")
    approve = bool(body.get("approve"))
    minutes = int(body.get("minutes") or 45)
    execute(
        """UPDATE requests SET status = ?, decided_at = ?, decided_by = ?, minutes = ?
           WHERE id = ?""",
        ("approved" if approve else "denied", now(), u["display"], minutes if approve else None, request_id),
    )
    device = one("SELECT * FROM devices WHERE id = ?", (req["device_id"],))
    app = one("SELECT * FROM apps WHERE id = ?", (req["app_id"],))
    if approve and device:
        enqueue(
            u["display"],
            "unlock_app",
            {"app_id": req["app_id"], "minutes": minutes},
            [device],
            f"Open {app['name']} for {device['student']} · {minutes} min",
        )
    else:
        audit(u["display"], "deny", f"{device['hostname'] if device else '?'} / {app['name'] if app else '?'}")
    return {"ok": True}


@app.post("/api/files")
async def upload_file(
    authorization: str | None = Header(default=None),
    file: UploadFile = File(...),
) -> dict[str, Any]:
    u = actor_of(authorization)
    raw = await file.read()
    if len(raw) > 40 * 1024 * 1024:
        raise HTTPException(400, "Forty megabytes is the line.")
    fid = nid()
    name = Path(file.filename or "upload.bin").name
    stored = f"{fid}_{name}"
    path = FILES / stored
    path.write_bytes(raw)
    execute(
        """INSERT INTO files (id, name, stored, size, uploaded_at, actor)
           VALUES (?, ?, ?, ?, ?, ?)""",
        (fid, name, stored, len(raw), now(), u["display"]),
    )
    audit(u["display"], "upload", f"{name} ({len(raw)} bytes)")
    return {"id": fid, "name": name, "size": len(raw)}


@app.get("/api/files")
async def list_files(authorization: str | None = Header(default=None)) -> list[dict[str, Any]]:
    actor_of(authorization)
    return query("SELECT id, name, size, uploaded_at, actor FROM files ORDER BY uploaded_at DESC")


@app.get("/api/audit")
async def list_audit(authorization: str | None = Header(default=None)) -> list[dict[str, Any]]:
    actor_of(authorization)
    return query("SELECT * FROM audit ORDER BY at DESC LIMIT 120")


# ---------------------------------------------------------------------------
# Real Windows agent
# ---------------------------------------------------------------------------

@app.post("/api/agent/enroll")
async def agent_enroll(body: dict[str, Any]) -> dict[str, Any]:
    hostname = (body.get("hostname") or "").strip().upper()
    if not hostname:
        raise HTTPException(400, "hostname required")
    existing = one("SELECT * FROM devices WHERE hostname = ?", (hostname,))
    token = "agt-" + nid() + nid()
    if existing:
        execute(
            """UPDATE devices SET agent_token = ?, simulated = 0, status = 'online',
               last_seen = ?, ip = ?, os = ?, build = ?, logged_on = ?
               WHERE id = ?""",
            (
                token,
                now(),
                body.get("ip"),
                body.get("os") or existing["os"],
                body.get("build"),
                body.get("user"),
                existing["id"],
            ),
        )
        device_id = existing["id"]
    else:
        device_id = nid()
        room = body.get("room_id") or "cart-1"
        if not one("SELECT id FROM rooms WHERE id = ?", (room,)):
            room = "cart-1"
        execute(
            """INSERT INTO devices
               (id, name, hostname, room_id, student, grade, os, build, ip, status,
                last_seen, agent_token, simulated, battery, disk_free_gb, logged_on,
                exam_mode, screen_locked, message, notes)
               VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, 'online', ?, ?, 0, ?, ?, ?, 0, 0, NULL, 'enrolled')""",
            (
                device_id,
                hostname,
                hostname,
                room,
                body.get("user"),
                body.get("grade"),
                body.get("os") or "Windows",
                body.get("build"),
                body.get("ip"),
                now(),
                token,
                body.get("battery"),
                body.get("disk_free_gb"),
                body.get("user"),
            ),
        )
    audit("agent", "enroll", hostname)
    return {"token": token, "device_id": device_id, "hostname": hostname}


@app.post("/api/agent/heartbeat")
async def agent_heartbeat(
    body: dict[str, Any],
    authorization: str | None = Header(default=None),
) -> dict[str, Any]:
    d = agent_of(authorization)
    execute(
        """UPDATE devices SET status = 'online', last_seen = ?, ip = ?,
           battery = COALESCE(?, battery), disk_free_gb = COALESCE(?, disk_free_gb),
           logged_on = COALESCE(?, logged_on), os = COALESCE(?, os),
           build = COALESCE(?, build)
           WHERE id = ?""",
        (
            now(),
            body.get("ip") or d["ip"],
            body.get("battery"),
            body.get("disk_free_gb"),
            body.get("user"),
            body.get("os"),
            body.get("build"),
            d["id"],
        ),
    )
    return {
        "ok": True,
        "apps": locker_for(d["id"]),
        "screen_locked": bool(d["screen_locked"]),
        "exam_mode": bool(one("SELECT exam_mode FROM devices WHERE id = ?", (d["id"],))["exam_mode"]),
        "message": one("SELECT message FROM devices WHERE id = ?", (d["id"],))["message"],
    }


@app.get("/api/agent/next")
async def agent_next(authorization: str | None = Header(default=None)) -> dict[str, Any]:
    d = agent_of(authorization)
    r = one(
        """SELECT r.*, c.kind, c.payload
           FROM command_results r JOIN commands c ON c.id = r.command_id
           WHERE r.device_id = ? AND r.status = 'queued'
           ORDER BY c.created_at ASC LIMIT 1""",
        (d["id"],),
    )
    if not r:
        return {"command": None}
    execute(
        "UPDATE command_results SET status = 'running', started_at = ? WHERE id = ?",
        (now(), r["id"]),
    )
    payload = json.loads(r["payload"]) if isinstance(r["payload"], str) else r["payload"]
    if r["kind"] == "download" and payload.get("file_id"):
        payload["url"] = f"/api/agent/file/{payload['file_id']}"
    return {
        "command": {
            "result_id": r["id"],
            "kind": r["kind"],
            "payload": payload,
        }
    }


@app.post("/api/agent/result")
async def agent_result(
    body: dict[str, Any],
    authorization: str | None = Header(default=None),
) -> dict[str, str]:
    d = agent_of(authorization)
    rid = body.get("result_id")
    r = one("SELECT * FROM command_results WHERE id = ? AND device_id = ?", (rid, d["id"]))
    if not r:
        raise HTTPException(404, "No such job.")
    status = body.get("status") or "ok"
    if status not in {"ok", "fail"}:
        status = "fail"
    execute(
        """UPDATE command_results
           SET status = ?, stdout = ?, stderr = ?, finished_at = ?
           WHERE id = ?""",
        (status, body.get("stdout") or "", body.get("stderr") or "", now(), rid),
    )
    cmd = one(
        """SELECT c.* FROM commands c JOIN command_results r ON r.command_id = c.id
           WHERE r.id = ?""",
        (rid,),
    )
    if cmd:
        payload = json.loads(cmd["payload"]) if isinstance(cmd["payload"], str) else cmd["payload"]
        apply_side_effects(cmd["kind"], payload, d, cmd["actor"])
    return {"ok": "noted"}


@app.post("/api/agent/request")
async def agent_request(
    body: dict[str, Any],
    authorization: str | None = Header(default=None),
) -> dict[str, Any]:
    d = agent_of(authorization)
    app_id = body.get("app_id")
    if not one("SELECT id FROM apps WHERE id = ?", (app_id,)):
        raise HTTPException(400, "Unknown app.")
    execute(
        """INSERT INTO requests (id, created_at, device_id, app_id, note, status)
           VALUES (?, ?, ?, ?, ?, 'waiting')""",
        (nid(), now(), d["id"], app_id, (body.get("note") or "")[:200]),
    )
    return {"ok": True}


@app.get("/api/agent/file/{file_id}")
async def agent_file(file_id: str, authorization: str | None = Header(default=None)) -> FileResponse:
    agent_of(authorization)
    f = one("SELECT * FROM files WHERE id = ?", (file_id,))
    if not f:
        raise HTTPException(404, "No such file.")
    path = FILES / f["stored"]
    if not path.exists():
        raise HTTPException(404, "File missing on the desk.")
    return FileResponse(path, filename=f["name"])


@app.get("/agent/PrefectAgent.ps1")
async def serve_agent() -> FileResponse:
    return FileResponse(AGENT / "PrefectAgent.ps1", media_type="text/plain")


@app.get("/agent/Install-PrefectAgent.ps1")
async def serve_install() -> FileResponse:
    return FileResponse(AGENT / "Install-PrefectAgent.ps1", media_type="text/plain")


# ---------------------------------------------------------------------------
# Pages
# ---------------------------------------------------------------------------

@app.get("/")
async def index() -> FileResponse:
    return FileResponse(WEB / "index.html")


@app.on_event("startup")
async def startup() -> None:
    init()
    seed()
    # Mark stale real agents offline; keep the simulated campus as seeded.
    execute(
        """UPDATE devices SET status = 'offline'
           WHERE simulated = 0 AND last_seen < ?""",
        (now() - 90,),
    )
    asyncio.create_task(simulator())


if (WEB / "css").exists():
    app.mount("/css", StaticFiles(directory=WEB / "css"), name="css")
if (WEB / "js").exists():
    app.mount("/js", StaticFiles(directory=WEB / "js"), name="js")
if (WEB / "img").exists():
    app.mount("/img", StaticFiles(directory=WEB / "img"), name="img")
