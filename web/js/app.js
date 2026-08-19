const S = {
  user: null,
  view: "board",
  devices: [],
  rooms: [],
  apps: [],
  scripts: [],
  requests: [],
  commands: [],
  files: [],
  audit: [],
  stats: {},
  selected: new Set(),
  q: "",
  roomFilter: "",
  drawer: null,
  liveCommand: null,
  scriptBody: "Get-CimInstance Win32_ComputerSystem | Select-Object Name, UserName, Domain, Manufacturer, Model | Format-List",
  poll: null,
};

const VIEWS = [
  ["board", "Board"],
  ["fleet", "The roll"],
  ["console", "Console"],
  ["locker", "Locker"],
  ["requests", "Knocks"],
  ["files", "Cupboard"],
  ["gate", "Web gate"],
  ["agent", "The agent"],
  ["audit", "The book"],
];

const HEADS = {
  board: ["Board", "The hall at a glance"],
  fleet: ["The roll", "Ninety names. Touch one, or the row."],
  console: ["Console", "PowerShell, to one or to all"],
  locker: ["Locker", "Shut what the lesson does not need"],
  requests: ["Knocks", "A student asking for a key"],
  files: ["Cupboard", "Leave a file. Send it down the hall."],
  gate: ["Web gate", "Held for a later hour"],
  agent: ["The agent", "How a real laptop joins the roll"],
  audit: ["The book", "Every order, kept"],
};

function $(id) { return document.getElementById(id); }
function esc(s) {
  return String(s ?? "").replace(/[&<>"']/g, (c) => ({
    "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;",
  }[c]));
}
function ago(ts) {
  if (!ts) return "—";
  const s = Math.max(0, Math.floor(Date.now() / 1000 - ts));
  if (s < 10) return "just now";
  if (s < 60) return s + "s ago";
  if (s < 3600) return Math.floor(s / 60) + "m ago";
  if (s < 86400) return Math.floor(s / 3600) + "h ago";
  return Math.floor(s / 86400) + "d ago";
}
function toast(msg) {
  const el = document.createElement("div");
  el.className = "toast";
  el.textContent = msg;
  $("toast").appendChild(el);
  setTimeout(() => el.remove(), 4200);
}

function selectedDevices() {
  if (S.selected.size === 0) return [];
  return S.devices.filter((d) => S.selected.has(d.id));
}

function targetLabel() {
  const n = S.selected.size;
  if (n === 0) return "the whole campus (90)";
  if (n === 1) {
    const d = S.devices.find((x) => x.id === [...S.selected][0]);
    return d ? d.hostname + (d.student ? " · " + d.student : "") : "1 machine";
  }
  return n + " machines";
}

async function refresh() {
  const q = S.q ? "&q=" + encodeURIComponent(S.q) : "";
  const room = S.roomFilter ? "&room=" + encodeURIComponent(S.roomFilter) : "";
  const [stats, rooms, devices, apps, scripts, requests, commands, files, audit] = await Promise.all([
    api.get("/api/stats"),
    api.get("/api/rooms"),
    api.get("/api/devices?" + q + room),
    api.get("/api/apps"),
    api.get("/api/scripts"),
    api.get("/api/requests?status=waiting"),
    api.get("/api/commands"),
    api.get("/api/files"),
    api.get("/api/audit"),
  ]);
  S.stats = stats;
  S.rooms = rooms;
  S.devices = devices;
  S.apps = apps;
  S.scripts = scripts;
  const prevWait = S.requests.length;
  S.requests = requests;
  S.commands = commands;
  S.files = files;
  S.audit = audit;
  if (requests.length > prevWait && prevWait > 0) {
    const r = requests[requests.length - 1];
    toast((r.student || r.hostname) + " asks for " + r.app_name);
  }
  if (S.liveCommand) {
    const fresh = commands.find((c) => c.id === S.liveCommand.id);
    if (fresh) S.liveCommand = fresh;
  }
  render();
}

function render() {
  $("crumb").textContent = HEADS[S.view][0];
  $("headline").textContent = HEADS[S.view][1];
  $("who").innerHTML = `<b>${esc(S.user.display)}</b><span>${esc(S.user.role)}</span>`;
  $("nav").innerHTML = VIEWS.map(([id, label]) => {
    const badge = id === "requests" && S.requests.length ? ` · ${S.requests.length}` : "";
    return `<button type="button" data-view="${id}" class="${S.view === id ? "on" : ""}">${label}${badge}</button>`;
  }).join("");
  const main = $("main");
  const draw = {
    board: viewBoard,
    fleet: viewFleet,
    console: viewConsole,
    locker: viewLocker,
    requests: viewRequests,
    files: viewFiles,
    gate: viewGate,
    agent: viewAgent,
    audit: viewAudit,
  }[S.view];
  main.innerHTML = draw();
  bindView();
  renderDock();
  renderDrawer();
}

function viewBoard() {
  const s = S.stats;
  return `
    <div class="stats">
      <div class="stat"><b>${s.total ?? "—"}</b><span>on the roll</span></div>
      <div class="stat ok"><b>${s.online ?? "—"}</b><span>awake</span></div>
      <div class="stat ${s.offline ? "warn" : ""}"><b>${s.offline ?? "—"}</b><span>dark</span></div>
      <div class="stat ${s.waiting ? "warn" : ""}"><b>${s.waiting ?? "—"}</b><span>knocks</span></div>
      <div class="stat"><b>${s.locked_apps ?? "—"}</b><span>apps shut</span></div>
      <div class="stat ${s.held ? "warn" : ""}"><b>${s.held ?? "—"}</b><span>screens held</span></div>
    </div>
    <div class="split">
      <div class="card">
        <h3>Rooms</h3>
        ${S.rooms.map((r) => {
          const pct = r.count ? Math.round((r.online / r.count) * 100) : 0;
          return `<div class="room-row">
            <div><b>${esc(r.name)}</b><div class="muted">${esc(r.teacher || "")} · ${r.online}/${r.count}</div></div>
            <div class="bar"><i style="width:${pct}%"></i></div>
            <button class="btn small ghost" data-goto-room="${r.id}">Open</button>
          </div>`;
        }).join("")}
      </div>
      <div class="card">
        <h3>The last hour</h3>
        <div class="feed">
          ${S.audit.slice(0, 10).map((a) => `
            <div class="feed-item">
              <time>${ago(a.at)}</time>
              <b>${esc(a.actor)}</b> — ${esc(a.action)}
              <div class="muted">${esc(a.detail || "")}</div>
            </div>`).join("") || `<p class="empty">Still. The book is new.</p>`}
        </div>
      </div>
    </div>
    ${S.requests.length ? `
      <div class="card" style="margin-top:18px">
        <h3>Waiting at the door</h3>
        ${S.requests.slice(0, 3).map(reqHtml).join("")}
        <button class="btn ghost" data-view="requests">All knocks</button>
      </div>` : ""}
  `;
}

function machineCard(d) {
  const on = S.selected.has(d.id) ? " on" : "";
  const off = d.status !== "online" ? " offline" : "";
  const pills = [];
  if (d.screen_locked) pills.push(`<span class="pill hold">held</span>`);
  if (d.exam_mode) pills.push(`<span class="pill exam">exam</span>`);
  if (d.opened_apps) pills.push(`<span class="pill open">${d.opened_apps} open</span>`);
  return `<button type="button" class="machine${on}${off}" data-id="${d.id}">
    <span class="host"><i class="dot"></i>${esc(d.hostname)}</span>
    <span class="whois">${esc(d.student || "—")}</span>
    <span class="meta">${d.grade ? "Y" + esc(d.grade) + " · " : ""}${d.battery != null ? d.battery + "% · " : ""}${ago(d.last_seen)}</span>
    <span class="badges">${pills.join("")}</span>
  </button>`;
}

function viewFleet() {
  const rooms = S.roomFilter ? S.rooms.filter((r) => r.id === S.roomFilter) : S.rooms;
  return `
    <div class="row" style="margin-bottom:16px">
      <button class="btn small ghost ${!S.roomFilter ? "brass" : ""}" data-room="">All rooms</button>
      ${S.rooms.map((r) => `<button class="btn small ghost ${S.roomFilter === r.id ? "brass" : ""}" data-room="${r.id}">${esc(r.name)}</button>`).join("")}
    </div>
    <div class="rooms">
      ${rooms.map((r) => {
        const ds = S.devices.filter((d) => d.room_id === r.id);
        return `<section>
          <div class="room-head">
            <div>
              <h3>${esc(r.name)}</h3>
              <p>${esc(r.teacher || "")} · ${ds.filter((d) => d.status === "online").length} awake</p>
            </div>
            <button class="btn small ghost" data-select-room="${r.id}">Take the room</button>
          </div>
          <div class="grid">${ds.map(machineCard).join("") || `<p class="empty">No machines match.</p>`}</div>
        </section>`;
      }).join("")}
    </div>
  `;
}

function viewConsole() {
  const live = S.liveCommand;
  const term = live
    ? live.results.map((r) => {
        const cls = r.status === "ok" ? "ok" : r.status === "fail" || r.status === "offline" ? "fail" : "";
        const body = (r.stdout || r.stderr || r.status).trim();
        return `<span class="host">PS ${esc(r.hostname)} [${r.status}]</span>\n<span class="${cls}">${esc(body)}</span>\n`;
      }).join("\n")
    : "The desk is quiet. Choose a machine — or none, and you speak to all ninety.\n";
  const counts = live ? live.counts : null;
  return `
    <div class="console-wrap">
      <div>
        <h3 class="muted" style="margin:0 0 8px;font-size:12px;letter-spacing:.12em;text-transform:uppercase">The shelf</h3>
        <div class="script-list">
          ${S.scripts.map((s) => `<button type="button" data-script="${s.id}">
            ${esc(s.name)}<small>${esc(s.blurb)}</small>
          </button>`).join("")}
        </div>
      </div>
      <div>
        <div class="cmd-meta">
          <span>Target · <b>${esc(targetLabel())}</b></span>
          ${counts ? `<span>${counts.ok} ok · ${counts.offline} dark · ${counts.running + counts.queued} in flight</span>` : ""}
        </div>
        <textarea id="ps" class="term-input" spellcheck="false">${esc(S.scriptBody)}</textarea>
        <div class="row" style="margin:10px 0 14px">
          <button class="btn brass" id="run-ps">Run on ${S.selected.size || "all"}</button>
          <button class="btn ghost" id="clear-sel">Clear the pointing finger</button>
        </div>
        <div class="term" id="term">${term}</div>
        <div style="margin-top:16px">
          <h3 class="muted" style="font-size:12px;letter-spacing:.12em;text-transform:uppercase">Recent orders</h3>
          ${S.commands.slice(0, 8).map((c) => `
            <div class="feed-item">
              <time>${ago(c.created_at)} · ${esc(c.actor)} · ${esc(c.status)}</time>
              <b>${esc(c.kind)}</b> — ${esc(c.note || "")}
              <div class="muted">${esc(c.target)} · ${c.counts.ok}/${c.results.length} answered</div>
            </div>`).join("")}
        </div>
      </div>
    </div>
  `;
}

function viewLocker() {
  const cats = [...new Set(S.apps.map((a) => a.category))];
  return `
    <p class="muted" style="max-width:62ch;margin:0 0 18px">
      What the lesson does not need stays in the locker. A student may knock.
      You turn the key for a period, or for one machine, or for the hall.
      Word, Excel, Teams and the rest of the work stay out — they are the point.
    </p>
    ${cats.map((cat) => `
      <h3 style="font-family:var(--serif);margin:22px 0 10px;text-transform:capitalize">${esc(cat)}</h3>
      <div class="app-grid">
        ${S.apps.filter((a) => a.category === cat).map((a) => `
          <article class="app-card ${a.always_allowed ? "allowed" : ""}">
            <header>
              <div>
                <div class="cat">${esc(a.publisher)}</div>
                <h4>${esc(a.name)}</h4>
              </div>
              <div class="lock-face ${a.default_locked ? "" : "open"}">${a.always_allowed ? "·" : a.default_locked ? "🔒" : "○"}</div>
            </header>
            <div class="muted">${a.always_allowed ? "Always open — the work." : a.default_locked ? "Shut on the campus." : "Open unless you shut it."}
              ${a.opened_on ? ` · ${a.opened_on} exception${a.opened_on > 1 ? "s" : ""}` : ""}</div>
            ${a.always_allowed ? "" : `
              <div class="app-actions">
                <button class="btn small ${a.default_locked ? "moss" : "danger"}" data-default-app="${a.id}" data-lock="${a.default_locked ? 0 : 1}">
                  ${a.default_locked ? "Open for all" : "Shut for all"}
                </button>
                <button class="btn small ghost" data-unlock-sel="${a.id}">Open on selection · 45m</button>
                <button class="btn small ghost" data-lock-sel="${a.id}">Shut on selection</button>
              </div>`}
          </article>`).join("")}
      </div>`).join("")}
  `;
}

function reqHtml(r) {
  return `<article class="req-card" data-req="${r.id}">
    <div>
      <h4>${esc(r.student || r.hostname)} wants ${esc(r.app_name)}</h4>
      <p>${esc(r.hostname)} · ${esc(r.note || "No note.")}</p>
    </div>
    <div class="row">
      <button class="btn small moss" data-approve="${r.id}" data-min="45">45 minutes</button>
      <button class="btn small ghost" data-approve="${r.id}" data-min="15">A quarter hour</button>
      <button class="btn small danger" data-deny="${r.id}">Not today</button>
    </div>
  </article>`;
}

function viewRequests() {
  if (!S.requests.length) return `<p class="empty">No one at the door.</p>`;
  return S.requests.map(reqHtml).join("");
}

function viewFiles() {
  return `
    <div class="split">
      <div class="card">
        <h3>Leave a file</h3>
        <p class="muted">It sits in the cupboard. Then you push it to the machines you are pointing at — or to all of them — and, if you wish, run it.</p>
        <input type="file" id="file-in" />
        <div class="field">
          <span>Save as, on the laptop</span>
          <input id="file-dest" value="C:\\ProgramData\\Prefect\\inbox" />
        </div>
        <div class="field">
          <span>Then run (optional PowerShell)</span>
          <input id="file-run" placeholder="Start-Process 'C:\\ProgramData\\Prefect\\inbox\\pack.msi'" />
        </div>
        <p class="muted">Target · <b>${esc(targetLabel())}</b></p>
        <button class="btn brass" id="push-file" ${S.files[0] ? "" : "disabled"}>Push the last file</button>
      </div>
      <div class="card">
        <h3>On the shelf</h3>
        ${S.files.length ? `<table class="table">
          <thead><tr><th>Name</th><th>Size</th><th>By</th></tr></thead>
          <tbody>${S.files.map((f) => `<tr>
            <td>${esc(f.name)}</td><td>${Math.ceil(f.size / 1024)} KB</td><td>${esc(f.actor)} · ${ago(f.uploaded_at)}</td>
          </tr>`).join("")}</tbody></table>` : `<p class="empty">The cupboard is empty.</p>`}
      </div>
    </div>
  `;
}

function viewGate() {
  return `
    <div class="gate">
      <p class="eyebrow">Kept aside, as you asked</p>
      <h3>No site they should not see — not even in incognito.</h3>
      <p class="muted" style="max-width:62ch;line-height:1.55">
        DNS alone will not hold Chrome. A private window, or DNS-over-HTTPS, walks around a hosts file.
        When we pick this up, Prefect will write Chrome enterprise policy on every laptop:
        incognito shut, a block list you edit from this desk, and a school DNS as the belt under the braces.
        The locker and the console come first. The gate waits in the corridor.
      </p>
      <ul class="muted">
        <li>IncognitoModeAvailability = 1</li>
        <li>URLBlocklist you own</li>
        <li>DnsOverHttpsMode = off</li>
      </ul>
    </div>
  `;
}

function viewAgent() {
  const origin = location.origin;
  return `
    <div class="split">
      <div class="card">
        <h3>A real Windows laptop</h3>
        <p class="muted">The ninety you see now are the campus, breathing, so the desk can be learned.
          When a physical machine is ready, install the agent as Administrator. It reports in, takes orders, and keeps the locker.</p>
        <p class="mono">irm ${esc(origin)}/agent/Install-PrefectAgent.ps1 | iex</p>
        <p class="muted">Or download the two scripts from this desk and run <code>Install-PrefectAgent.ps1 -Server ${esc(origin)}</code>.</p>
        <div class="row">
          <a class="btn brass" href="/agent/Install-PrefectAgent.ps1">Install script</a>
          <a class="btn ghost" href="/agent/PrefectAgent.ps1">Agent</a>
        </div>
      </div>
      <div class="card">
        <h3>What it will do</h3>
        <p>Talk only to this desk. Pull an order. Run PowerShell. Fetch a file. Stop a locked app if a student opens it. Knock, when they ask for a key. Write a line in the book. Nothing hidden — the task is called Prefect, and a student can be told it is there.</p>
      </div>
    </div>
  `;
}

function viewAudit() {
  return `<table class="table">
    <thead><tr><th>When</th><th>Who</th><th>What</th><th>Detail</th></tr></thead>
    <tbody>${S.audit.map((a) => `<tr>
      <td class="mono">${ago(a.at)}</td>
      <td>${esc(a.actor)}</td>
      <td>${esc(a.action)}</td>
      <td class="muted">${esc(a.detail || "")}</td>
    </tr>`).join("")}</tbody>
  </table>`;
}

function renderDock() {
  const n = S.selected.size;
  const dock = $("dock");
  if (!n) {
    dock.hidden = true;
    dock.innerHTML = "";
    return;
  }
  dock.hidden = false;
  dock.innerHTML = `
    <div><b>${n} chosen</b><div class="muted">${esc(selectedDevices().slice(0, 3).map((d) => d.hostname).join(", "))}${n > 3 ? "…" : ""}</div></div>
    <div class="dock-actions">
      <button class="btn small brass" data-dock="console">Console</button>
      <button class="btn small" data-dock="lock">Hold screens</button>
      <button class="btn small" data-dock="unlock">Release</button>
      <button class="btn small" data-dock="exam">Exam on</button>
      <button class="btn small" data-dock="examoff">Exam off</button>
      <button class="btn small" data-dock="msg">Speak</button>
      <button class="btn small danger" data-dock="reboot">Restart</button>
      <button class="btn small ghost" data-dock="clear">Let go</button>
    </div>
  `;
}

function renderDrawer() {
  const el = $("drawer");
  if (!S.drawer) {
    el.hidden = true;
    el.innerHTML = "";
    return;
  }
  const d = S.devices.find((x) => x.id === S.drawer);
  if (!d) {
    el.hidden = true;
    return;
  }
  el.hidden = false;
  const apps = (d.apps || []).filter((a) => !a.always_allowed);
  el.innerHTML = `
    <p class="eyebrow">${esc(d.room_name || d.room_id)}</p>
    <h3>${esc(d.hostname)}</h3>
    <p class="muted">${esc(d.student || "")} ${d.grade ? "· Y" + esc(d.grade) : ""}</p>
    <dl class="kv">
      <dt>State</dt><dd>${esc(d.status)}${d.screen_locked ? " · held" : ""}${d.exam_mode ? " · exam" : ""}</dd>
      <dt>Seen</dt><dd>${ago(d.last_seen)}</dd>
      <dt>Address</dt><dd class="mono">${esc(d.ip || "—")}</dd>
      <dt>Windows</dt><dd>${esc(d.os)} ${esc(d.build || "")}</dd>
      <dt>Disk</dt><dd>${d.disk_free_gb ?? "—"} GB free</dd>
      <dt>Battery</dt><dd>${d.battery != null ? d.battery + "%" : "mains"}</dd>
    </dl>
    <div class="row">
      <button class="btn small brass" data-only="${d.id}">Only this one</button>
      <button class="btn small ghost" id="close-drawer">Close</button>
    </div>
    <h4 style="margin:22px 0 8px;font-family:var(--serif)">This locker</h4>
    ${apps.map((a) => `
      <div class="room-row">
        <div>${esc(a.name)}<div class="muted">${a.locked ? (a.until ? "opens " + ago(a.until).replace("ago", "from now") : "shut") : "open"}${a.granted_by ? " · " + esc(a.granted_by) : ""}</div></div>
        <button class="btn small ghost" data-dev-app="${a.id}" data-dev="${d.id}" data-lock="${a.locked ? 0 : 1}">${a.locked ? "Open 45m" : "Shut"}</button>
      </div>`).join("")}
  `;
}

function bindView() {
  $("nav").onclick = (e) => {
    const b = e.target.closest("[data-view]");
    if (!b) return;
    S.view = b.dataset.view;
    S.drawer = null;
    render();
  };
  document.querySelectorAll("[data-view]").forEach((b) => {
    if (b.closest("#nav")) return;
    b.onclick = () => { S.view = b.dataset.view; render(); };
  });
  document.querySelectorAll("[data-goto-room]").forEach((b) => {
    b.onclick = () => {
      S.roomFilter = b.dataset.gotoRoom;
      S.view = "fleet";
      render();
    };
  });
  document.querySelectorAll("[data-room]").forEach((b) => {
    b.onclick = () => {
      S.roomFilter = b.dataset.room;
      render();
    };
  });
  document.querySelectorAll(".machine").forEach((b) => {
    b.onclick = (ev) => {
      const id = b.dataset.id;
      if (ev.shiftKey || ev.metaKey || ev.ctrlKey) {
        if (S.selected.has(id)) S.selected.delete(id);
        else S.selected.add(id);
      } else {
        S.selected = new Set([id]);
        S.drawer = id;
      }
      render();
    };
  });
  document.querySelectorAll("[data-select-room]").forEach((b) => {
    b.onclick = () => {
      S.devices.filter((d) => d.room_id === b.dataset.selectRoom).forEach((d) => S.selected.add(d.id));
      render();
    };
  });
  const ps = $("ps");
  if (ps) {
    ps.oninput = () => { S.scriptBody = ps.value; };
    $("run-ps").onclick = () => runPs();
    $("clear-sel").onclick = () => { S.selected.clear(); render(); };
  }
  document.querySelectorAll("[data-script]").forEach((b) => {
    b.onclick = () => {
      const s = S.scripts.find((x) => x.id === b.dataset.script);
      if (s) {
        S.scriptBody = s.body;
        render();
      }
    };
  });
  document.querySelectorAll("[data-default-app]").forEach((b) => {
    b.onclick = async () => {
      try {
        await api.post("/api/apps/" + b.dataset.defaultApp + "/default", { locked: b.dataset.lock === "1" });
        toast("The hall locker moved.");
        await refresh();
      } catch (err) { toast(err.message); }
    };
  });
  document.querySelectorAll("[data-unlock-sel]").forEach((b) => {
    b.onclick = () => send({ kind: "unlock_app", app_id: b.dataset.unlockSel, minutes: 45 });
  });
  document.querySelectorAll("[data-lock-sel]").forEach((b) => {
    b.onclick = () => send({ kind: "lock_app", app_id: b.dataset.lockSel });
  });
  document.querySelectorAll("[data-approve]").forEach((b) => {
    b.onclick = async () => {
      await api.post("/api/requests/" + b.dataset.approve + "/decide", {
        approve: true, minutes: Number(b.dataset.min || 45),
      });
      toast("The key turned.");
      await refresh();
    };
  });
  document.querySelectorAll("[data-deny]").forEach((b) => {
    b.onclick = async () => {
      await api.post("/api/requests/" + b.dataset.deny + "/decide", { approve: false });
      toast("Not today.");
      await refresh();
    };
  });
  const fin = $("file-in");
  if (fin) {
    fin.onchange = async () => {
      if (!fin.files[0]) return;
      try {
        await api.upload(fin.files[0]);
        toast("On the shelf.");
        await refresh();
      } catch (err) { toast(err.message); }
    };
    $("push-file").onclick = async () => {
      if (!S.files[0]) return;
      await send({
        kind: "download",
        file_id: S.files[0].id,
        dest: $("file-dest").value,
        run: $("file-run").value,
      });
    };
  }
  $("dock").onclick = (e) => {
    const b = e.target.closest("[data-dock]");
    if (!b) return;
    const act = b.dataset.dock;
    if (act === "clear") { S.selected.clear(); render(); }
    if (act === "console") { S.view = "console"; render(); }
    if (act === "lock") send({ kind: "lock_screen", text: "Eyes up. The lesson has the room." });
    if (act === "unlock") send({ kind: "unlock_screen" });
    if (act === "exam") send({ kind: "exam_on" });
    if (act === "examoff") send({ kind: "exam_off" });
    if (act === "reboot") {
      if (confirm("Restart " + targetLabel() + "?")) send({ kind: "reboot" });
    }
    if (act === "msg") {
      const text = prompt("What should the screens say?", "Five minutes. Save your work.");
      if (text) send({ kind: "message", text });
    }
  };
  const close = $("close-drawer");
  if (close) close.onclick = () => { S.drawer = null; render(); };
  document.querySelectorAll("[data-only]").forEach((b) => {
    b.onclick = () => {
      S.selected = new Set([b.dataset.only]);
      S.view = "console";
      S.drawer = null;
      render();
    };
  });
  document.querySelectorAll("[data-dev-app]").forEach((b) => {
    b.onclick = () => {
      const lock = b.dataset.lock === "1";
      send({
        kind: lock ? "lock_app" : "unlock_app",
        app_id: b.dataset.devApp,
        minutes: lock ? undefined : 45,
        device_ids: [b.dataset.dev],
      });
    };
  });
}

async function send(body) {
  const payload = Object.assign({}, body);
  if (!payload.device_ids) {
    payload.device_ids = S.selected.size ? [...S.selected] : S.devices.map((d) => d.id);
  }
  try {
    const cmd = await api.post("/api/commands", payload);
    S.liveCommand = cmd;
    if (body.kind === "powershell" || S.view === "console") S.view = "console";
    toast("Order gone to " + cmd.results.length + ".");
    await refresh();
  } catch (err) {
    toast(err.message);
  }
}

async function runPs() {
  const script = ($("ps") ? $("ps").value : S.scriptBody).trim();
  if (!script) return toast("Write a command first.");
  S.scriptBody = script;
  await send({ kind: "powershell", script });
}

function bootApp() {
  $("login").hidden = true;
  $("app").hidden = false;
  $("q").oninput = debounce(async (e) => {
    S.q = e.target.value.trim();
    await refresh();
  }, 200);
  $("sel-online").onclick = () => {
    S.selected = new Set(S.devices.filter((d) => d.status === "online").map((d) => d.id));
    S.view = "fleet";
    render();
  };
  $("signout").onclick = async () => {
    try { await api.post("/api/logout", {}); } catch (_) {}
    api.token = "";
    localStorage.removeItem("prefect.token");
    location.reload();
  };
  refresh();
  S.poll = setInterval(refresh, 2200);
}

function debounce(fn, ms) {
  let t;
  return (...a) => {
    clearTimeout(t);
    t = setTimeout(() => fn(...a), ms);
  };
}

async function trySession() {
  if (!api.token) return false;
  try {
    S.user = await api.get("/api/me");
    return true;
  } catch (_) {
    api.token = "";
    localStorage.removeItem("prefect.token");
    return false;
  }
}

$("login-form").onsubmit = async (e) => {
  e.preventDefault();
  $("login-err").hidden = true;
  try {
    const out = await api.post("/api/login", {
      username: $("user").value,
      password: $("pass").value,
    });
    api.token = out.token;
    localStorage.setItem("prefect.token", out.token);
    S.user = out.user;
    bootApp();
  } catch (err) {
    $("login-err").hidden = false;
    $("login-err").textContent = err.message;
  }
};

trySession().then((ok) => { if (ok) bootApp(); });
