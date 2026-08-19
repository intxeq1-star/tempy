"""Prefect persistence — SQLite, no ORM, one file, ninety machines."""

from __future__ import annotations

import json
import os
import sqlite3
import threading
import time
import uuid
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parent.parent
DATA = ROOT / "data"
DB_PATH = DATA / "prefect.db"
FILES = DATA / "files"

_local = threading.local()


def now() -> int:
    return int(time.time())


def nid() -> str:
    return uuid.uuid4().hex[:16]


def connect() -> sqlite3.Connection:
    conn = getattr(_local, "conn", None)
    if conn is None:
        DATA.mkdir(parents=True, exist_ok=True)
        FILES.mkdir(parents=True, exist_ok=True)
        conn = sqlite3.connect(DB_PATH, check_same_thread=False)
        conn.row_factory = sqlite3.Row
        conn.execute("PRAGMA journal_mode=WAL")
        conn.execute("PRAGMA foreign_keys=ON")
        _local.conn = conn
    return conn


def row(r: sqlite3.Row | None) -> dict[str, Any] | None:
    if r is None:
        return None
    return {k: r[k] for k in r.keys()}


def rows(cur: sqlite3.Cursor) -> list[dict[str, Any]]:
    return [row(r) for r in cur.fetchall()]  # type: ignore[misc]


def execute(sql: str, args: tuple = ()) -> sqlite3.Cursor:
    con = connect()
    cur = con.execute(sql, args)
    con.commit()
    return cur


def query(sql: str, args: tuple = ()) -> list[dict[str, Any]]:
    return rows(connect().execute(sql, args))


def one(sql: str, args: tuple = ()) -> dict[str, Any] | None:
    return row(connect().execute(sql, args).fetchone())


def init() -> None:
    execute(
        """
        CREATE TABLE IF NOT EXISTS users (
            id TEXT PRIMARY KEY,
            username TEXT UNIQUE NOT NULL,
            password TEXT NOT NULL,
            display TEXT NOT NULL,
            role TEXT NOT NULL
        )
        """
    )
    execute(
        """
        CREATE TABLE IF NOT EXISTS sessions (
            token TEXT PRIMARY KEY,
            user_id TEXT NOT NULL,
            created_at INTEGER NOT NULL
        )
        """
    )
    execute(
        """
        CREATE TABLE IF NOT EXISTS rooms (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            kind TEXT NOT NULL,
            teacher TEXT,
            sort INTEGER NOT NULL DEFAULT 0
        )
        """
    )
    execute(
        """
        CREATE TABLE IF NOT EXISTS devices (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            hostname TEXT NOT NULL,
            room_id TEXT NOT NULL,
            student TEXT,
            grade TEXT,
            os TEXT NOT NULL,
            build TEXT,
            ip TEXT,
            status TEXT NOT NULL,
            last_seen INTEGER NOT NULL,
            agent_token TEXT UNIQUE,
            simulated INTEGER NOT NULL DEFAULT 1,
            battery INTEGER,
            disk_free_gb REAL,
            logged_on TEXT,
            exam_mode INTEGER NOT NULL DEFAULT 0,
            screen_locked INTEGER NOT NULL DEFAULT 0,
            message TEXT,
            notes TEXT
        )
        """
    )
    execute(
        """
        CREATE TABLE IF NOT EXISTS apps (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            exe TEXT NOT NULL,
            process TEXT NOT NULL,
            publisher TEXT,
            category TEXT NOT NULL,
            default_locked INTEGER NOT NULL DEFAULT 1,
            always_allowed INTEGER NOT NULL DEFAULT 0,
            sort INTEGER NOT NULL DEFAULT 0
        )
        """
    )
    execute(
        """
        CREATE TABLE IF NOT EXISTS device_apps (
            device_id TEXT NOT NULL,
            app_id TEXT NOT NULL,
            locked INTEGER NOT NULL,
            until INTEGER,
            granted_by TEXT,
            PRIMARY KEY (device_id, app_id)
        )
        """
    )
    execute(
        """
        CREATE TABLE IF NOT EXISTS commands (
            id TEXT PRIMARY KEY,
            created_at INTEGER NOT NULL,
            actor TEXT NOT NULL,
            kind TEXT NOT NULL,
            payload TEXT NOT NULL,
            note TEXT,
            target TEXT NOT NULL
        )
        """
    )
    execute(
        """
        CREATE TABLE IF NOT EXISTS command_results (
            id TEXT PRIMARY KEY,
            command_id TEXT NOT NULL,
            device_id TEXT NOT NULL,
            status TEXT NOT NULL,
            stdout TEXT,
            stderr TEXT,
            started_at INTEGER,
            finished_at INTEGER
        )
        """
    )
    execute(
        """
        CREATE TABLE IF NOT EXISTS requests (
            id TEXT PRIMARY KEY,
            created_at INTEGER NOT NULL,
            device_id TEXT NOT NULL,
            app_id TEXT NOT NULL,
            note TEXT,
            status TEXT NOT NULL,
            decided_at INTEGER,
            decided_by TEXT,
            minutes INTEGER
        )
        """
    )
    execute(
        """
        CREATE TABLE IF NOT EXISTS files (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            stored TEXT NOT NULL,
            size INTEGER NOT NULL,
            uploaded_at INTEGER NOT NULL,
            actor TEXT NOT NULL
        )
        """
    )
    execute(
        """
        CREATE TABLE IF NOT EXISTS audit (
            id TEXT PRIMARY KEY,
            at INTEGER NOT NULL,
            actor TEXT NOT NULL,
            action TEXT NOT NULL,
            detail TEXT
        )
        """
    )
    execute(
        """
        CREATE TABLE IF NOT EXISTS scripts (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            body TEXT NOT NULL,
            blurb TEXT,
            sort INTEGER NOT NULL DEFAULT 0
        )
        """
    )


def audit(actor: str, action: str, detail: str = "") -> None:
    execute(
        "INSERT INTO audit (id, at, actor, action, detail) VALUES (?, ?, ?, ?, ?)",
        (nid(), now(), actor, action, detail),
    )


def dumps(obj: Any) -> str:
    return json.dumps(obj, ensure_ascii=False)
