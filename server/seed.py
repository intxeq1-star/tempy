"""Ninety school laptops, the app locker catalogue, and the first desk."""

from __future__ import annotations

import hashlib
import itertools
import random

from db import execute, nid, now, one

FIRST = [
    "Amira", "Ben", "Caleb", "Dina", "Eli", "Farah", "Gia", "Hassan",
    "Imani", "Jonah", "Keiko", "Leo", "Maya", "Noah", "Omar", "Priya",
    "Quinn", "Rosa", "Sami", "Talia", "Uma", "Victor", "Willa", "Yusef",
    "Zara", "Aiden", "Bea", "Chris", "Dahlia", "Evan", "Fatima", "Grace",
    "Hugo", "Iris", "Jamal", "Kara", "Luis", "Mina", "Nico", "Olive",
    "Pavel", "Rina", "Sofia", "Theo", "Uri", "Vera", "Wade", "Ximena",
    "Yara", "Zane", "Asha", "Blake", "Cora", "Dev", "Elena", "Felix",
    "Hana", "Ivan", "Jade", "Kenji", "Lila", "Mateo", "Noor", "Owen",
    "Pia", "Remy", "Sable", "Tomas", "Una", "Violet", "Wes", "Yasmin",
    "Zoe", "Arlo", "Briar", "Cass", "Drew", "Eden", "Freya", "Gideon",
    "Hope", "Isa", "Jules", "Kian", "Lina", "Micah",
]

LAST = [
    "Okoro", "Hale", "Nguyen", "Rahman", "Cole", "Santos", "Berg", "Diallo",
    "Park", "Vega", "Ito", "Khan", "Moreau", "Singh", "Okafor", "Reyes",
    "Adler", "Costa", "Hassan", "Petrov", "Walsh", "Kim", "Duarte", "Nasser",
    "Brooks", "Chaudhry", "Ellis", "Farouk", "Grau", "Huang",
]


def pw(raw: str) -> str:
    salt = "prefect.aula.v1"
    return hashlib.pbkdf2_hmac("sha256", raw.encode(), salt.encode(), 120_000).hex()


def seed() -> None:
    if one("SELECT id FROM users LIMIT 1"):
        return

    execute(
        "INSERT INTO users (id, username, password, display, role) VALUES (?, ?, ?, ?, ?)",
        (nid(), "head", pw("campus90"), "Head of Hall", "head"),
    )
    execute(
        "INSERT INTO users (id, username, password, display, role) VALUES (?, ?, ?, ?, ?)",
        (nid(), "desk", pw("campus90"), "Duty desk", "teacher"),
    )

    rooms = [
        ("lab-a", "Computer Lab A", "lab", "Ms. Rahman", 1),
        ("lab-b", "Computer Lab B", "lab", "Mr. Cole", 2),
        ("cart-1", "Trolley 1 — English", "cart", "Ms. Hale", 3),
        ("cart-2", "Trolley 2 — Science", "cart", "Mr. Okoro", 4),
        ("staff", "Staff machines", "staff", "Office", 5),
    ]
    for r in rooms:
        execute(
            "INSERT INTO rooms (id, name, kind, teacher, sort) VALUES (?, ?, ?, ?, ?)",
            r,
        )

    apps = [
        ("ppt", "PowerPoint", "POWERPNT.EXE", "POWERPNT", "Microsoft", "office", 1, 0, 10),
        ("paint", "Paint", "mspaint.exe", "mspaint", "Microsoft", "create", 1, 0, 20),
        ("paint3d", "Paint 3D", "PaintStudio.View.exe", "PaintStudio.View", "Microsoft", "create", 1, 0, 21),
        ("draw", "Microsoft Whiteboard", "MicrosoftWhiteboard.exe", "MicrosoftWhiteboard", "Microsoft", "create", 1, 0, 22),
        ("clipchamp", "Clipchamp", "Clipchamp.exe", "Clipchamp", "Microsoft", "create", 1, 0, 23),
        ("photos", "Photos", "Photos.exe", "Photos", "Microsoft", "media", 0, 0, 30),
        ("movies", "Films & TV", "Video.UI.exe", "Video.UI", "Microsoft", "media", 1, 0, 31),
        ("spotify", "Spotify", "Spotify.exe", "Spotify", "Spotify", "media", 1, 0, 32),
        ("xbox", "Xbox", "XboxPcApp.exe", "XboxPcApp", "Microsoft", "games", 1, 0, 40),
        ("solitaire", "Solitaire", "Solitaire.exe", "Solitaire", "Microsoft", "games", 1, 0, 41),
        ("minecraft", "Minecraft", "Minecraft.Windows.exe", "Minecraft.Windows", "Mojang", "games", 1, 0, 42),
        ("gamebar", "Xbox Game Bar", "GameBar.exe", "GameBar", "Microsoft", "games", 1, 0, 43),
        ("store", "Microsoft Store", "WinStore.App.exe", "WinStore.App", "Microsoft", "system", 1, 0, 50),
        ("camera", "Camera", "WindowsCamera.exe", "WindowsCamera", "Microsoft", "system", 1, 0, 51),
        ("discord", "Discord", "Discord.exe", "Discord", "Discord", "social", 1, 0, 60),
        ("word", "Word", "WINWORD.EXE", "WINWORD", "Microsoft", "office", 0, 1, 1),
        ("excel", "Excel", "EXCEL.EXE", "EXCEL", "Microsoft", "office", 0, 1, 2),
        ("onenote", "OneNote", "ONENOTE.EXE", "ONENOTE", "Microsoft", "office", 0, 1, 3),
        ("teams", "Teams", "ms-teams.exe", "ms-teams", "Microsoft", "office", 0, 1, 4),
        ("edge", "Edge", "msedge.exe", "msedge", "Microsoft", "web", 0, 1, 5),
        ("chrome", "Chrome", "chrome.exe", "chrome", "Google", "web", 0, 0, 6),
        ("notepad", "Notepad", "notepad.exe", "notepad", "Microsoft", "system", 0, 1, 7),
        ("calc", "Calculator", "CalculatorApp.exe", "CalculatorApp", "Microsoft", "system", 0, 1, 8),
    ]
    for a in apps:
        execute(
            """INSERT INTO apps
               (id, name, exe, process, publisher, category, default_locked, always_allowed, sort)
               VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)""",
            a,
        )

    scripts = [
        (
            "who",
            "Who is signed in",
            "Get-CimInstance Win32_ComputerSystem | Select-Object Name, UserName, Domain, Manufacturer, Model | Format-List",
            "Name, user, and machine model.",
            1,
        ),
        (
            "os",
            "Windows version",
            "(Get-ComputerInfo | Select-Object WindowsProductName, OsVersion, OsBuildNumber, CsName) | Format-List",
            "Edition and build.",
            2,
        ),
        (
            "disk",
            "Free disk",
            "Get-PSDrive -PSProvider FileSystem | Select-Object Name, @{N='UsedGB';E={[math]::Round($_.Used/1GB,1)}}, @{N='FreeGB';E={[math]::Round($_.Free/1GB,1)}} | Format-Table -AutoSize",
            "Used and free space on every drive.",
            3,
        ),
        (
            "battery",
            "Battery",
            "Get-CimInstance Win32_Battery | Select-Object EstimatedChargeRemaining, BatteryStatus, EstimatedRunTime | Format-List",
            "Charge left on trolley machines.",
            4,
        ),
        (
            "procs",
            "Running processes",
            "Get-Process | Sort-Object CPU -Descending | Select-Object -First 20 Name, Id, CPU, @{N='MB';E={[math]::Round($_.WorkingSet64/1MB,0)}} | Format-Table -AutoSize",
            "Top twenty by CPU.",
            5,
        ),
        (
            "startup",
            "Startup programs",
            "Get-CimInstance Win32_StartupCommand | Select-Object Name, Command, Location | Format-Table -AutoSize",
            "What launches at sign-in.",
            6,
        ),
        (
            "temp",
            "Clear user temp",
            "Remove-Item -Path $env:TEMP\\* -Recurse -Force -ErrorAction SilentlyContinue; Write-Output \"Temp cleared for $env:USERNAME\"",
            "Safe tidy. Does not touch documents.",
            7,
        ),
        (
            "time",
            "Resync clock",
            "w32tm /resync /force; Get-Date -Format o",
            "Fix clocks that drifted on the trolley.",
            8,
        ),
        (
            "explorer",
            "Restart Explorer",
            "Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue; Start-Process explorer; Write-Output 'Explorer restarted.'",
            "Unstick a frozen desktop.",
            9,
        ),
        (
            "inventory",
            "Installed programs",
            "Get-ItemProperty HKLM:\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*, HKLM:\\Software\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\* | Where-Object DisplayName | Select-Object DisplayName, DisplayVersion | Sort-Object DisplayName | Format-Table -AutoSize",
            "What is actually installed.",
            10,
        ),
    ]
    for s in scripts:
        execute(
            "INSERT INTO scripts (id, name, body, blurb, sort) VALUES (?, ?, ?, ?, ?)",
            s,
        )

    rng = random.Random(90)
    names = [f"{a} {b}" for a, b in zip(FIRST, itertools.cycle(LAST))]
    layouts = [
        ("lab-a", 24, "LAB-A", "10.20.1."),
        ("lab-b", 24, "LAB-B", "10.20.2."),
        ("cart-1", 18, "CART-1", "10.20.8."),
        ("cart-2", 18, "CART-2", "10.20.9."),
        ("staff", 6, "STAFF", "10.20.0."),
    ]
    grades = ["7", "8", "9", "10", "11", "12"]
    builds = ["22631.3880", "22631.4317", "26100.2894", "22621.4169"]
    n = 0
    t = now()
    for room_id, count, prefix, net in layouts:
        for i in range(1, count + 1):
            student = None if room_id == "staff" else names[n]
            grade = None if room_id == "staff" else rng.choice(grades)
            if room_id == "staff":
                staff = ["Ms. Rahman", "Mr. Cole", "Ms. Hale", "Mr. Okoro", "Office 1", "Office 2"]
                student = staff[i - 1]
            hostname = f"{prefix}-{i:02d}"
            offline = hostname in {"LAB-A-07", "CART-2-11", "LAB-B-19"}
            last = t - (rng.randint(40, 180) if not offline else rng.randint(3600, 28_000))
            execute(
                """INSERT INTO devices
                   (id, name, hostname, room_id, student, grade, os, build, ip, status,
                    last_seen, agent_token, simulated, battery, disk_free_gb, logged_on,
                    exam_mode, screen_locked, message, notes)
                   VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 1, ?, ?, ?, 0, 0, NULL, NULL)""",
                (
                    nid(),
                    hostname,
                    hostname,
                    room_id,
                    student,
                    grade,
                    "Windows 11 Pro Education",
                    rng.choice(builds),
                    f"{net}{20 + i}",
                    "offline" if offline else "online",
                    last,
                    "sim-" + hostname.lower(),
                    None if room_id in {"lab-a", "lab-b"} else rng.randint(18, 100),
                    round(rng.uniform(28.0, 186.0), 1),
                    student,
                ),
            )
            n += 1

    execute(
        "INSERT INTO audit (id, at, actor, action, detail) VALUES (?, ?, ?, ?, ?)",
        (nid(), now(), "system", "seed", "Campus of 90 machines ready. App locker on."),
    )
