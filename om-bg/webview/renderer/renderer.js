function $(id) { return document.getElementById(id); }

// ---- API bridge: WebView2 messaging, with rich MOCK fallback for browser preview ----
const hasWebview = typeof window !== 'undefined' && window.chrome && window.chrome.webview && window.chrome.webview.postMessage;

let mockUnlocked = false;
let mockRules = [
  { id: "RULE-01", application: "notepad.exe", friendlyName: "Windows Notepad (Test Target)", action: "BLOCK", enabled: false, updatedAt: new Date().toISOString() },
  { id: "RULE-02", application: "calc.exe", friendlyName: "Windows Calculator", action: "BLOCK", enabled: true, updatedAt: new Date().toISOString() },
  { id: "RULE-03", application: "discord.exe", friendlyName: "Discord Messenger", action: "BLOCK", enabled: true, updatedAt: new Date().toISOString() },
  { id: "RULE-04", application: "game.exe", friendlyName: "Game Executable", action: "BLOCK", enabled: true, updatedAt: new Date().toISOString() }
];

let mockTestLog = ["[Ready] Select target and click '1. Block Test App' to begin test phase."];
let mockTestActive = false;

const MOCK = {
  status: async () => ({
    service: 'Running',
    status: {
      state: 'RUNNING', processId: 4182, lastScriptPid: 5120,
      lastHeartbeatUtc: new Date().toISOString(), lastRunAtUtc: new Date().toISOString(), lastRunStatus: 'OK',
      scriptUseCount: 142, blockedAppsCount: mockRules.filter(r => r.action === 'BLOCK' && r.enabled).length,
      lastOutput: 'Computer : DESKTOP-OM\\nUser     : NT AUTHORITY\\\\SYSTEM\\nOS       : Windows 10/11 (23H2)\\nRAM free : 8.2 GB of 16.0 GB\\nEnforcing App Control: Active rules applied\\nworker finished cycle at ' + new Date().toLocaleTimeString()
    }
  }),
  install: async () => ({ code: 0, out: '[SC] CreateService SUCCESS\\nDeployed worker to C:\\\\ProgramData\\\\OMAgent\\\\OMAgent.exe' }),
  start: async () => '', stop: async () => '', uninstall: async () => '',
  runCommand: async (cmd, shell, asUser) => ({
    code: 0,
    Output: 'Windows 10 Pro\\n' + (asUser ? 'USER (interactive)' : 'ADMIN (SYSTEM)') + ' / ' + shell + ' ran:\\n' + cmd + '\\n[exit code 0]'
  }),
  log: async () => '2026-08-27 10:20:11.000 [INFO ] Worker cycle finished (exit=0, timedOut=False).\\n2026-08-27 10:20:11.000 [INFO ] Enforcing AppControl: Active IFEO & DisallowRun rules\\n2026-08-27 10:20:26.000 [INFO ] Heartbeat 10:20:26',
  scriptPath: async () => 'C:\\\\ProgramData\\\\OMAgent\\\\worker.ps1',
  openFolder: async () => '', openLog: async () => '',
  pathInfo: async () => ({ dataDir: 'C:\\\\ProgramData\\\\OMAgent', script: 'worker.ps1', log: 'Logs', appControl: 'appcontrol.json' }),

  // App Control Mock
  appControlGet: async () => ({
    config: {
      version: 1,
      enforcementMode: 'Active (IFEO + DisallowRun + Real-time Watcher)',
      rules: mockRules,
      testPhase: { active: mockTestActive, targetApp: 'notepad.exe', lastTestLog: mockTestLog }
    },
    isWindows: true
  }),
  appControlCheckPass: async (pass) => ({ valid: pass === 'om' }),
  appControlAddRule: async ({ password, application, friendlyName, action }) => {
    if (password !== 'om') return { success: false, error: "Access Denied: Incorrect password. Use 'om' to edit rules." };
    const exe = application.toLowerCase().endsWith('.exe') ? application.toLowerCase() : application.toLowerCase() + '.exe';
    mockRules.push({
      id: 'RULE-' + Math.random().toString(36).substring(2, 8).toUpperCase(),
      application: exe,
      friendlyName: friendlyName || exe,
      action: action || 'BLOCK',
      enabled: true,
      updatedAt: new Date().toISOString()
    });
    return { success: true, message: `Rule for '${exe}' saved and enforced!` };
  },
  appControlToggleRule: async ({ password, id, enabled }) => {
    if (password !== 'om') return { success: false, error: "Access Denied: Password 'om' required." };
    const r = mockRules.find(x => x.id === id);
    if (r) { r.enabled = enabled; return { success: true, rule: r }; }
    return { success: false, error: "Rule not found." };
  },
  appControlDeleteRule: async ({ password, id }) => {
    if (password !== 'om') return { success: false, error: "Access Denied: Password 'om' required." };
    const idx = mockRules.findIndex(x => x.id === id);
    if (idx >= 0) {
      const removed = mockRules.splice(idx, 1)[0];
      return { success: true, message: `Rule for '${removed.application}' deleted.` };
    }
    return { success: false, error: "Rule not found." };
  },
  appControlTestStart: async ({ password, targetApp }) => {
    if (password !== 'om') return { success: false, error: "Access Denied: Password 'om' required to start test phase." };
    const exe = targetApp.toLowerCase().endsWith('.exe') ? targetApp.toLowerCase() : targetApp.toLowerCase() + '.exe';
    mockTestActive = true;
    const now = new Date().toLocaleTimeString();
    mockTestLog = [
      `[${now}] Starting Test Phase for: ${exe}`,
      `[${now}] [IFEO] Registry debugger redirect set -> systray.exe`,
      `[${now}] [DisallowRun] Policy rule registered in Explorer`,
      `[${now}] [Process] Active watcher monitoring ${exe}`,
      `[${now}] TEST BLOCK ACTIVE: Windows will now refuse to open ${exe}!`
    ];
    return { success: true, target: exe, message: `Test Block ACTIVE for ${exe}! Try opening ${exe} on your PC to verify.`, log: mockTestLog };
  },
  appControlTestLaunch: async ({ targetApp }) => {
    const exe = targetApp.toLowerCase().endsWith('.exe') ? targetApp.toLowerCase() : targetApp.toLowerCase() + '.exe';
    const now = new Date().toLocaleTimeString();
    mockTestLog.push(`[${now}] Attempting automated execution of '${exe}'...`);
    mockTestLog.push(`[${now}] [IFEO Intercept] Windows detected debugger hook: systray.exe`);
    mockTestLog.push(`[${now}] [BLOCKED] Process creation prevented: Access is denied.`);
    mockTestLog.push(`[${now}] [SUCCESS] ${exe} was successfully prevented from opening!`);
    return {
      success: true,
      blocked: true,
      verdict: "PASSED: Application is BLOCKED from opening!",
      detail: "Windows prevented execution via IFEO Debugger and DisallowRun.",
      log: mockTestLog
    };
  },
  appControlTestEnd: async ({ password, targetApp }) => {
    if (password !== 'om') return { success: false, error: "Access Denied: Password 'om' required." };
    const exe = targetApp.toLowerCase().endsWith('.exe') ? targetApp.toLowerCase() : targetApp.toLowerCase() + '.exe';
    mockTestActive = false;
    const now = new Date().toLocaleTimeString();
    mockTestLog.push(`[${now}] Test Phase ENDED. Removed block for ${exe}.`);
    mockTestLog.push(`[${now}] [RESTORED] ${exe} can now be opened normally.`);
    return { success: true, target: exe, message: `Test Phase ended. ${exe} restored to normal execution.`, log: mockTestLog };
  }
};

let _seq = 0;
const _pending = {};

function bridge(type, payload) {
  return new Promise((resolve, reject) => {
    const id = 'm' + (++_seq);
    _pending[id] = { resolve, reject };
    window.chrome.webview.postMessage({ id, type, payload });
    setTimeout(() => {
      if (_pending[id]) {
        delete _pending[id];
        resolve({ error: 'timeout' });
      }
    }, 60000);
  });
}

if (hasWebview) {
  window.chrome.webview.addEventListener('message', (e) => {
    const m = e.data;
    if (!m || !m.id) return;
    const p = _pending[m.id];
    if (!p) return;
    delete _pending[m.id];
    if (m.error) p.reject(new Error(m.error));
    else p.resolve(m.data);
  });
}

function makeApi() {
  if (hasWebview) {
    return {
      status: () => bridge('status'),
      install: () => bridge('install'),
      start: () => bridge('start'),
      stop: () => bridge('stop'),
      uninstall: () => bridge('uninstall'),
      runCommand: (cmd, shell, asUser) => bridge('run-command', { cmd, shell, asUser }),
      log: () => bridge('log'),
      scriptPath: () => bridge('script-path'),
      openFolder: () => bridge('open-folder'),
      openLog: () => bridge('open-log'),
      pathInfo: () => bridge('path-info'),
      // App Control
      appControlGet: () => bridge('appcontrol-get'),
      appControlCheckPass: (password) => bridge('appcontrol-check-pass', { password }),
      appControlAddRule: (data) => bridge('appcontrol-add-rule', data),
      appControlToggleRule: (data) => bridge('appcontrol-toggle-rule', data),
      appControlDeleteRule: (data) => bridge('appcontrol-delete-rule', data),
      appControlTestStart: (data) => bridge('appcontrol-test-start', data),
      appControlTestLaunch: (data) => bridge('appcontrol-test-launch', data),
      appControlTestEnd: (data) => bridge('appcontrol-test-end', data)
    };
  }
  return MOCK;
}

const api = makeApi();

// State
let isUnlocked = false;
let currentPassword = '';
let currentRules = [];

// Toast notification
function toast(msg, isError = false) {
  const t = $('toast');
  if (!t) return;
  t.textContent = msg;
  t.style.borderColor = isError ? 'var(--danger)' : 'var(--line)';
  t.classList.add('show');
  setTimeout(() => t.classList.remove('show'), 2800);
}

// Navigation Tabs
document.querySelectorAll('.nav-item').forEach(b => {
  b.onclick = () => switchTab(b.dataset.tab);
});

function switchTab(name) {
  document.querySelectorAll('.nav-item').forEach(b => b.classList.toggle('active', b.dataset.tab === name));
  document.querySelectorAll('.tab').forEach(t => t.classList.toggle('active', t.id === 'tab-' + name));
  if (name === 'log') refreshLog();
  if (name === 'appcontrol') refreshAppControl();
}

// Dashboard Refresh
async function refresh() {
  try {
    const r = await api.status();
    const s = r.status || {};
    const svc = r.service || 'unknown';
    $('cService').textContent = svc;
    $('cState').textContent = s.state || '—';
    $('cPid').textContent = (s.processId || '-') + (s.lastScriptPid > 0 ? ' / script ' + s.lastScriptPid : '');
    $('cHeart').textContent = s.lastHeartbeatUtc ? new Date(s.lastHeartbeatUtc).toLocaleTimeString() : '—';
    $('cLast').textContent = (s.lastRunAtUtc ? new Date(s.lastRunAtUtc).toLocaleString() : '—') + (s.lastRunStatus ? ' · ' + s.lastRunStatus : '');
    $('cBlocked').textContent = (s.blockedAppsCount !== undefined ? s.blockedAppsCount : (currentRules.filter(x => x.action === 'BLOCK' && x.enabled).length)) + ' active';

    $('output').textContent = s.lastOutput || '(none yet)';
    const pill = $('svcPill');
    const running = /running/i.test(svc);
    pill.textContent = running ? '● Service running' : (svc === 'not installed' ? '○ Not installed' : '○ ' + svc);
    pill.className = 'svc-pill ' + (running ? 'ok' : 'bad');
  } catch (e) { }
}

$('btnInstall').onclick = async () => {
  toast('Installing worker service…');
  const r = await api.install();
  toast((r && (r.code === 0 || r === 0)) ? 'Worker service installed successfully!' : 'Install failed: ' + (r && (r.out || r.code)));
  refresh();
};
$('btnStart').onclick = async () => { toast('Starting worker…'); await api.start(); refresh(); };
$('btnStop').onclick = async () => { toast('Stopping worker and child processes…'); await api.stop(); refresh(); };
$('btnScriptPath').onclick = async () => { toast('Script: ' + (await api.scriptPath())); };

// Commands Tab
$('btnRun').onclick = runCmd;
$('cmdInput').addEventListener('keydown', e => { if (e.key === 'Enter') runCmd(); });
async function runCmd() {
  const cmd = $('cmdInput').value.trim();
  if (!cmd) { toast('Enter a command first.', true); return; }
  const shell = $('shellSel').value;
  const asUser = $('ctxSel').value === 'user';
  $('cmdOut').textContent = 'Running… (' + (asUser ? 'USER' : 'ADMIN') + ' / ' + shell + ')\n\n';
  try {
    const r = await api.runCommand(cmd, shell, asUser);
    const out = (r && (r.Output || r.out)) || '';
    const code = r ? (r.ExitCode !== undefined ? r.ExitCode : r.code) : '?';
    $('cmdOut').textContent = out + '\n\n── exit code: ' + (code ?? '?') + ' ──';
  } catch (e) {
    $('cmdOut').textContent = 'Error: ' + e.message;
  }
}

// Logs Tab
async function refreshLog() {
  try { $('logOut').textContent = await api.log(); } catch (e) { $('logOut').textContent = String(e); }
}
$('btnRefreshLog').onclick = refreshLog;
$('btnOpenLog').onclick = () => api.openLog();

// ==========================================
// APP CONTROL & BLOCKING LOGIC
// ==========================================

async function refreshAppControl() {
  try {
    const res = await api.appControlGet();
    if (!res || !res.config) return;
    currentRules = res.config.rules || [];
    renderRulesTable(currentRules);

    // Update test phase status
    const tp = res.config.testPhase || {};
    if (tp.active) {
      $('testStatusPill').textContent = 'TEST BLOCK ACTIVE';
      $('testStatusPill').className = 'test-status-pill active';
      $('testNotice').innerHTML = `⚠️ <strong>TEST BLOCK ACTIVE:</strong> <code>${tp.targetApp || 'notepad.exe'}</code> is currently blocked! Try opening it via Start or Run to verify.`;
    } else {
      $('testStatusPill').textContent = 'Test: Ready';
      $('testStatusPill').className = 'test-status-pill idle';
    }

    if (tp.lastTestLog && tp.lastTestLog.length > 0) {
      $('testLog').textContent = tp.lastTestLog.join('\n');
    }
  } catch (e) {
    console.error(e);
  }
}

function updateLockUI() {
  const badge = $('lockBadge');
  const btnUnlock = $('btnUnlockEdit');
  const btnLock = $('btnLockEdit');
  const addPanel = $('addRulePanel');

  if (isUnlocked) {
    badge.className = 'lock-status-badge unlocked';
    badge.innerHTML = '<span class="lock-icon">&#128275;</span> Unlocked (Admin Mode)';
    btnUnlock.style.display = 'none';
    btnLock.style.display = 'inline-flex';
    addPanel.style.display = 'block';
  } else {
    badge.className = 'lock-status-badge locked';
    badge.innerHTML = '<span class="lock-icon">&#128274;</span> Locked (View Only)';
    btnUnlock.style.display = 'inline-flex';
    btnLock.style.display = 'none';
    addPanel.style.display = 'none';
  }
  renderRulesTable(currentRules);
}

function renderRulesTable(rules) {
  const tbody = $('rulesTableBody');
  const countLabel = $('rulesCountLabel');
  if (!tbody) return;

  const blockedCount = rules.filter(r => r.action === 'BLOCK' && r.enabled).length;
  countLabel.textContent = `${rules.length} rules (${blockedCount} actively blocked)`;

  if (rules.length === 0) {
    tbody.innerHTML = '<tr><td colspan="5" style="text-align:center; padding:24px;" class="muted">No application rules configured. Click "Unlock to Edit" to add a rule.</td></tr>';
    return;
  }

  tbody.innerHTML = '';
  rules.forEach(rule => {
    const tr = document.createElement('tr');

    // Status
    const tdStatus = document.createElement('td');
    if (rule.enabled) {
      if (rule.action === 'BLOCK') {
        tdStatus.innerHTML = '<span class="badge-block">&#128683; BLOCKED</span>';
      } else {
        tdStatus.innerHTML = '<span class="badge-allow">&#10004; ALLOWED</span>';
      }
    } else {
      tdStatus.innerHTML = '<span class="muted" style="font-size:11px;">(Disabled)</span>';
    }
    tr.appendChild(tdStatus);

    // Application
    const tdApp = document.createElement('td');
    tdApp.innerHTML = `<span class="app-name-code">${escapeHtml(rule.application)}</span>`;
    tr.appendChild(tdApp);

    // Description
    const tdDesc = document.createElement('td');
    tdDesc.textContent = rule.friendlyName || '—';
    tr.appendChild(tdDesc);

    // Action
    const tdAct = document.createElement('td');
    tdAct.textContent = rule.action;
    tdAct.style.fontWeight = '600';
    tdAct.style.color = rule.action === 'BLOCK' ? 'var(--danger)' : 'var(--accent2)';
    tr.appendChild(tdAct);

    // Actions
    const tdActions = document.createElement('td');
    tdActions.className = 'table-actions';

    const btnToggle = document.createElement('button');
    btnToggle.className = 'btn sm ghost';
    btnToggle.textContent = rule.enabled ? 'Disable' : 'Enable';
    btnToggle.disabled = !isUnlocked;
    btnToggle.onclick = () => toggleRule(rule.id, !rule.enabled);
    tdActions.appendChild(btnToggle);

    const btnDel = document.createElement('button');
    btnDel.className = 'btn sm danger';
    btnDel.innerHTML = '&#128465;';
    btnDel.title = 'Delete Rule';
    btnDel.disabled = !isUnlocked;
    btnDel.onclick = () => deleteRule(rule.id);
    tdActions.appendChild(btnDel);

    tr.appendChild(tdActions);
    tbody.appendChild(tr);
  });
}

function escapeHtml(s) {
  return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

// Password Modal Handling
let pendingAction = null;

function requestPassword(action) {
  if (isUnlocked) {
    action(currentPassword);
    return;
  }
  pendingAction = action;
  $('pwdInput').value = '';
  $('pwdError').style.display = 'none';
  $('pwdModal').style.display = 'flex';
  setTimeout(() => $('pwdInput').focus(), 100);
}

$('btnUnlockEdit').onclick = () => {
  requestPassword(() => {
    isUnlocked = true;
    updateLockUI();
    toast('Editing unlocked with administrator password "om"!');
  });
};

$('btnLockEdit').onclick = () => {
  isUnlocked = false;
  currentPassword = '';
  updateLockUI();
  toast('App Control locked.');
};

$('btnCancelModal').onclick = () => {
  $('pwdModal').style.display = 'none';
  pendingAction = null;
};

$('btnSubmitModal').onclick = verifyModalPassword;
$('pwdInput').addEventListener('keydown', e => { if (e.key === 'Enter') verifyModalPassword(); });

async function verifyModalPassword() {
  const p = $('pwdInput').value.trim();
  const res = await api.appControlCheckPass(p);
  if (res && res.valid) {
    currentPassword = p;
    isUnlocked = true;
    $('pwdModal').style.display = 'none';
    updateLockUI();
    if (pendingAction) {
      const act = pendingAction;
      pendingAction = null;
      act(currentPassword);
    }
  } else {
    $('pwdError').style.display = 'block';
    $('pwdError').textContent = "Incorrect password! Use 'om' to edit rules.";
    $('pwdInput').select();
  }
}

$('btnRefreshRules').onclick = () => {
  refreshAppControl();
  toast('Application rules refreshed.');
};

// Add Rule
$('btnAddRule').onclick = async () => {
  const app = $('newAppExe').value.trim();
  const friendly = $('newAppLabel').value.trim();
  const act = $('newAppAction').value;

  if (!app) {
    toast('Please enter an application executable (e.g. notepad.exe).', true);
    return;
  }

  requestPassword(async (pwd) => {
    const res = await api.appControlAddRule({
      password: pwd,
      application: app,
      friendlyName: friendly,
      action: act
    });

    if (res && res.success) {
      toast(res.message || 'Rule saved successfully!');
      $('newAppExe').value = '';
      $('newAppLabel').value = '';
      refreshAppControl();
    } else {
      toast((res && res.error) || 'Failed to add rule', true);
    }
  });
};

async function toggleRule(id, enabled) {
  requestPassword(async (pwd) => {
    const res = await api.appControlToggleRule({ password: pwd, id, enabled });
    if (res && res.success) {
      toast('Rule updated.');
      refreshAppControl();
    } else {
      toast((res && res.error) || 'Toggle failed', true);
    }
  });
}

async function deleteRule(id) {
  requestPassword(async (pwd) => {
    const res = await api.appControlDeleteRule({ password: pwd, id });
    if (res && res.success) {
      toast(res.message || 'Rule deleted.');
      refreshAppControl();
    } else {
      toast((res && res.error) || 'Delete failed', true);
    }
  });
}

// ==========================================
// TEST PHASE LOGIC
// ==========================================

$('btnTestStart').onclick = () => {
  const target = $('testTargetApp').value.trim() || 'notepad.exe';
  requestPassword(async (pwd) => {
    $('testLog').textContent = `[${new Date().toLocaleTimeString()}] Sending block command for ${target}...`;
    const res = await api.appControlTestStart({ password: pwd, targetApp: target });
    if (res && res.success) {
      $('testStatusPill').textContent = 'TEST BLOCK ACTIVE';
      $('testStatusPill').className = 'test-status-pill active';
      $('testNotice').innerHTML = `⚠️ <strong>TEST BLOCK ACTIVE:</strong> <code>${target}</code> is now blocked! Try opening it via Start or Run to verify.`;
      if (res.log) $('testLog').textContent = res.log.join('\n');
      toast(res.message || `Test block active for ${target}`);
      refreshAppControl();
    } else {
      toast((res && res.error) || 'Test block failed', true);
    }
  });
};

$('btnTestLaunch').onclick = async () => {
  const target = $('testTargetApp').value.trim() || 'notepad.exe';
  $('testLog').textContent += `\n[${new Date().toLocaleTimeString()}] Executing verification launch test on '${target}'...`;
  try {
    const res = await api.appControlTestLaunch({ targetApp: target });
    if (res && res.success) {
      if (res.log) $('testLog').textContent = res.log.join('\n');
      if (res.blocked) {
        $('testStatusPill').textContent = 'VERIFIED BLOCKED';
        $('testStatusPill').className = 'test-status-pill verified';
        toast('SUCCESS: Application was blocked from opening!');
      } else {
        toast('NOTICE: App process started but was terminated.', true);
      }
    }
  } catch (e) {
    $('testLog').textContent += `\n[Error] Test launch check failed: ${e.message}`;
  }
};

$('btnTestEnd').onclick = () => {
  const target = $('testTargetApp').value.trim() || 'notepad.exe';
  requestPassword(async (pwd) => {
    $('testLog').textContent += `\n[${new Date().toLocaleTimeString()}] Ending test phase and restoring ${target}...`;
    const res = await api.appControlTestEnd({ password: pwd, targetApp: target });
    if (res && res.success) {
      $('testStatusPill').textContent = 'Test: Ready';
      $('testStatusPill').className = 'test-status-pill idle';
      $('testNotice').innerHTML = `✅ <strong>TEST RESTORED:</strong> <code>${target}</code> is unblocked. You can open it normally again.`;
      if (res.log) $('testLog').textContent = res.log.join('\n');
      toast(res.message || 'Test phase ended.');
      refreshAppControl();
    } else {
      toast((res && res.error) || 'Restore failed', true);
    }
  });
};

// Initial load
(async () => {
  try {
    const info = await api.pathInfo();
    $('pathLine').textContent = 'worker: ' + (info.dataDir || 'C:\\ProgramData\\OMAgent');
  } catch { }
  updateLockUI();
  refresh();
  refreshLog();
  refreshAppControl();
  setInterval(refresh, 3000);
})();
