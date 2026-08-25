(() => {
  'use strict';

  const COMMAND_TYPES = [
    'GET_SYSTEM_INFO', 'GET_INSTALLED_APPS', 'INSTALL_APP', 'UNINSTALL_APP', 'UPDATE_APP', 'CHECK_APP',
    'APPLY_DNS', 'CHECK_DNS', 'APPLY_BROWSER_POLICY', 'CHECK_BROWSER_POLICY', 'APPLY_APP_POLICY',
    'CHECK_APP_POLICY', 'SYNC_POLICY', 'RESTART_AGENT', 'RESTART_PC', 'SHUTDOWN_PC', 'LOCK_PC',
    'LOGOFF_USER', 'RUN_ADMIN_COMMAND'
  ];
  const state = { csrf: '', me: null, dashboard: null, groups: [], apps: [], operations: [], eventSource: null, refreshTimer: null };
  const $ = (selector, parent = document) => parent.querySelector(selector);
  const $$ = (selector, parent = document) => [...parent.querySelectorAll(selector)];
  const encoder = new TextEncoder();

  function escape(value) {
    return String(value ?? '').replace(/[&<>'"]/g, character => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' }[character]));
  }
  function fmtTime(value) {
    if (!value) return '—';
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? '—' : date.toLocaleString([], { month: 'short', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit' });
  }
  function relative(value) {
    if (!value) return 'never';
    const seconds = Math.max(0, Math.floor((Date.now() - new Date(value).getTime()) / 1000));
    if (seconds < 60) return `${seconds}s ago`;
    if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`;
    return `${Math.floor(seconds / 3600)}h ago`;
  }
  function cssStatus(status) { return String(status || 'UNKNOWN').toLowerCase().replace(/[^a-z]/g, ''); }
  function statusBadge(status) { return `<span class="status ${cssStatus(status)}">${escape(status || 'UNKNOWN')}</span>`; }
  function json(value) { try { return JSON.stringify(value); } catch { return '{}'; } }
  function bytes(value) { return encoder.encode(value || '').length; }

  async function getCsrf() {
    const response = await fetch('/api/auth/csrf', { credentials: 'same-origin' });
    if (!response.ok) throw new Error('Could not establish a secure browser session.');
    const body = await response.json();
    state.csrf = body.token;
  }
  async function api(path, options = {}) {
    const settings = { credentials: 'same-origin', ...options, headers: { ...(options.headers || {}) } };
    if (settings.body && !(settings.body instanceof FormData) && !settings.headers['Content-Type']) settings.headers['Content-Type'] = 'application/json';
    if (!['GET', 'HEAD', 'OPTIONS'].includes((settings.method || 'GET').toUpperCase())) settings.headers['X-TEMPY-CSRF'] = state.csrf;
    const response = await fetch(path, settings);
    if (response.status === 401) {
      showLogin();
      throw new Error('Your administrator session has expired.');
    }
    if (response.status === 204) return null;
    const raw = await response.text();
    let data = null;
    try { data = raw ? JSON.parse(raw) : null; } catch { data = raw; }
    if (!response.ok) throw new Error(data?.error || data?.title || `Request failed (${response.status}).`);
    return data;
  }
  function toast(message, type = '') {
    const toastNode = document.createElement('div');
    toastNode.className = `toast ${type}`;
    toastNode.textContent = message;
    $('#toast-region').append(toastNode);
    setTimeout(() => toastNode.remove(), 5200);
  }
  function setError(id, error = '') { $(id).textContent = error; }

  async function initialize() {
    try {
      await getCsrf();
      const me = await api('/api/auth/me');
      state.me = me;
      await showApp();
    } catch {
      showLogin();
    }
  }
  function showLogin() {
    state.eventSource?.close(); state.eventSource = null;
    $('#app-view').classList.add('hidden');
    $('#login-view').classList.remove('hidden');
  }
  async function showApp() {
    $('#login-view').classList.add('hidden');
    $('#app-view').classList.remove('hidden');
    $('#current-user').textContent = state.me.username;
    $('#current-role').textContent = state.me.role;
    $('#user-initial').textContent = (state.me.username || 'A')[0].toUpperCase();
    $$('.admin-only').forEach(node => node.classList.toggle('hidden', state.me.role !== 'Administrator'));
    populateCommandTypes();
    await Promise.all([refreshDashboard(), refreshGroups(), refreshApplications(), refreshPolicy(), refreshOperations()]);
    connectEvents();
  }
  async function refreshDashboard() {
    const dashboard = await api('/api/dashboard');
    state.dashboard = dashboard;
    renderDashboard(dashboard);
    populateDevicePickers();
  }
  async function refreshGroups() {
    state.groups = await api('/api/groups');
    populateGroupPickers();
    renderGroups();
  }
  async function refreshApplications() {
    state.apps = await api('/api/applications');
    renderApplications();
    populateApplicationPickers();
  }
  async function refreshPolicy() {
    const policy = await api('/api/policy');
    renderPolicy(policy);
  }
  async function refreshOperations() {
    state.operations = await api('/api/commands/operations?take=30');
    renderOperations();
  }
  async function refreshAll() {
    try {
      await Promise.all([refreshDashboard(), refreshGroups(), refreshApplications(), refreshPolicy(), refreshOperations()]);
      toast('Dashboard refreshed.');
    } catch (error) { toast(error.message, 'error'); }
  }
  function scheduleRefresh() {
    clearTimeout(state.refreshTimer);
    state.refreshTimer = setTimeout(() => {
      Promise.all([refreshDashboard(), refreshOperations(), refreshPolicy()]).catch(error => console.warn(error));
    }, 220);
  }
  function connectEvents() {
    state.eventSource?.close();
    const events = new EventSource('/api/events');
    state.eventSource = events;
    events.addEventListener('dashboard', scheduleRefresh);
    events.onopen = () => {
      $('#event-status').classList.remove('offline');
      $('#event-status').innerHTML = '<i></i> Live updates connected';
    };
    events.onerror = () => {
      $('#event-status').classList.add('offline');
      $('#event-status').innerHTML = '<i></i> Live updates reconnecting';
    };
  }
  function renderDashboard(data) {
    $('#total-count').textContent = data.total;
    $('#online-count').textContent = data.online;
    $('#offline-count').textContent = data.offline;
    $('#syncing-count').textContent = data.syncing;
    $('#failed-count').textContent = data.failed;
    $('#desired-policy-version').textContent = data.desiredPolicyVersion;
    filterDevices();
    $('#sync-overview').innerHTML = [
      ['SYNCED', data.devices.filter(d => d.syncState === 'SYNCED').length],
      ['SYNCING', data.devices.filter(d => d.syncState === 'SYNCING').length],
      ['OUT OF SYNC', data.devices.filter(d => d.syncState === 'OUT_OF_SYNC').length],
      ['OFFLINE', data.devices.filter(d => !d.isOnline).length]
    ].map(([label, count]) => `<div><span>${label}</span><strong>${count}</strong></div>`).join('');
  }
  function filterDevices() {
    const term = ($('#device-search').value || '').trim().toLowerCase();
    const rows = (state.dashboard?.devices || []).filter(device => !term || [device.deviceId, device.hostname, device.localIp, device.agentVersion].some(v => String(v || '').toLowerCase().includes(term)));
    $('#device-filter-count').textContent = `${rows.length} shown`;
    $('#device-table').innerHTML = rows.length ? rows.map(device => `<tr class="clickable" data-device-id="${escape(device.deviceId)}">
      <td><div class="device-main"><strong>${escape(device.hostname || device.deviceId)}</strong><small>${escape(device.deviceId)}</small></div></td>
      <td>${statusBadge(device.connectionState)}</td><td>${escape(device.localIp || '—')}</td>
      <td title="${escape(fmtTime(device.lastHeartbeatAt))}">${escape(relative(device.lastHeartbeatAt))}</td>
      <td>${escape(device.agentVersion || '—')}</td><td>${device.appliedPolicyVersion} / ${device.desiredPolicyVersion}</td>
      <td>${statusBadge(device.syncState)}</td><td>${device.pendingCommandCount}</td><td>${statusBadge(device.dnsStatus)}</td>
    </tr>`).join('') : `<tr><td colspan="9" class="muted">No managed devices match this filter.</td></tr>`;
    $$('#device-table tr[data-device-id]').forEach(row => row.addEventListener('click', () => openDevice(row.dataset.deviceId)));
  }
  function populateCommandTypes() {
    $('#command-type').innerHTML = COMMAND_TYPES.map(type => `<option value="${type}">${type}</option>`).join('');
    updateCommandType();
  }
  function updateCommandType() {
    const isTerminal = $('#command-type').value === 'RUN_ADMIN_COMMAND';
    $('#terminal-command-wrap').classList.toggle('hidden', !isTerminal);
  }
  function populateDevicePickers() {
    const devices = state.dashboard?.devices || [];
    const selectedIds = new Set($$('#device-checklist input:checked').map(input => input.value));
    const groupSelectedIds = new Set($$('#group-device-checklist input:checked').map(input => input.value));
    const oneBefore = $('#one-device').value;
    const deployBefore = $('#deploy-device').value;
    const options = devices.map(device => `<option value="${escape(device.deviceId)}">${escape(device.hostname || device.deviceId)} — ${escape(device.deviceId)} (${device.connectionState})</option>`).join('');
    $('#one-device').innerHTML = options || '<option value="">No devices</option>';
    $('#deploy-device').innerHTML = options || '<option value="">No devices</option>';
    if (oneBefore) $('#one-device').value = oneBefore;
    if (deployBefore) $('#deploy-device').value = deployBefore;
    const checklist = devices.map(device => `<label class="check-device"><input type="checkbox" value="${escape(device.deviceId)}"${selectedIds.has(device.deviceId) ? ' checked' : ''}><span>${escape(device.hostname || device.deviceId)} <small>${escape(device.deviceId)}</small></span></label>`).join('') || '<span class="muted">No managed devices yet.</span>';
    const groupChecklist = devices.map(device => `<label class="check-device"><input type="checkbox" value="${escape(device.deviceId)}"${groupSelectedIds.has(device.deviceId) ? ' checked' : ''}><span>${escape(device.hostname || device.deviceId)} <small>${escape(device.deviceId)}</small></span></label>`).join('') || '<span class="muted">No managed devices yet.</span>';
    $('#device-checklist').innerHTML = checklist;
    $('#group-device-checklist').innerHTML = groupChecklist;
    $$('#device-checklist input').forEach(input => input.addEventListener('change', updateTargetPreview));
    updateTargetPreview();
  }
  function populateGroupPickers() {
    const options = state.groups.map(group => `<option value="${group.group_id || group.groupId}">${escape(group.name)} (${group.member_count ?? group.memberCount ?? 0})</option>`).join('');
    $('#target-group').innerHTML = options || '<option value="">No groups</option>';
    $('#deploy-group').innerHTML = options || '<option value="">No groups</option>';
    updateTargetPreview();
  }
  function populateApplicationPickers() {
    const enabled = state.apps.filter(app => app.isEnabled);
    $('#deploy-app').innerHTML = enabled.map(app => `<option value="${app.applicationId}">${escape(app.displayName)} — ${escape(app.packageId)}</option>`).join('') || '<option value="">No enabled applications</option>';
  }
  function renderGroups() {
    $('#group-list').innerHTML = state.groups.length ? state.groups.map(group => `<div class="app-row"><div><strong>${escape(group.name)}</strong><small>${escape(group.description || 'No description')}</small></div><span class="count-pill">${group.member_count ?? group.memberCount ?? 0} MEMBERS</span></div>`).join('') : '<p class="muted">No groups yet. Create classroom or lab groups for reliable bulk targeting.</p>';
  }
  function updateTargetControls() {
    const kind = $('#target-kind').value;
    $('#one-device-picker').classList.toggle('hidden', kind !== 'DEVICE');
    $('#multiple-device-picker').classList.toggle('hidden', kind !== 'DEVICES');
    $('#group-picker').classList.toggle('hidden', kind !== 'GROUP');
    updateTargetPreview();
  }
  function updateTargetPreview() {
    const kind = $('#target-kind').value;
    let text = 'All managed devices';
    if (kind === 'DEVICE') text = $('#one-device').selectedOptions[0]?.textContent || 'Choose one PC';
    if (kind === 'DEVICES') { const count = $$('#device-checklist input:checked').length; text = count ? `${count} selected device${count === 1 ? '' : 's'}` : 'Choose selected PCs'; }
    if (kind === 'GROUP') text = $('#target-group').selectedOptions[0]?.textContent || 'Choose a device group';
    $('#target-preview').textContent = text;
  }
  function renderPolicy(policy) {
    const desired = policy.desired_policy || policy.desiredPolicy;
    if (!desired) return;
    $('#policy-version-badge').textContent = `v${policy.policy_version ?? policy.policyVersion}`;
    $('#dns-enabled').checked = Boolean(desired.dns?.enabled);
    $('#dns-server').value = desired.dns?.server || '192.168.1.100';
    $('#chrome-incognito').checked = Boolean(desired.chrome?.incognito);
    $('#edge-inprivate').checked = Boolean(desired.edge?.inprivate);
    updatePolicyStateLabels();
  }
  function updatePolicyStateLabels() {
    $('#chrome-policy-state').textContent = $('#chrome-incognito').checked ? 'Allowed by desired policy' : 'Disabled by desired policy';
    $('#edge-policy-state').textContent = $('#edge-inprivate').checked ? 'Allowed by desired policy' : 'Disabled by desired policy';
  }
  function renderApplications() {
    $('#application-list').innerHTML = state.apps.length ? state.apps.map(app => `<div class="app-row"><div><strong>${escape(app.displayName)}</strong><small>${escape(app.packageId)} · ${escape(app.source)}${app.desiredVersion ? ` · ${escape(app.desiredVersion)}` : ''}</small></div>${app.isEnabled ? '<span class="status success">ENABLED</span>' : '<span class="status offline">DISABLED</span>'}</div>`).join('') : '<p class="muted">The catalog is empty. Add an approved WinGet package to begin.</p>';
  }
  function renderOperations() {
    const list = $('#operations-list');
    if (!state.operations.length) { list.innerHTML = '<section class="card operation-empty">No command operations have been created yet.</section>'; return; }
    list.innerHTML = state.operations.map(operation => {
      const summary = operation.summary || {};
      const pills = ['SUCCESS', 'RUNNING', 'RECEIVED', 'SENT', 'PENDING', 'FAILED', 'TIMEOUT', 'CANCELLED'].filter(key => summary[key]).map(key => `<span class="count-pill ${cssStatus(key)}">${key}: ${summary[key]}</span>`).join('') || '<span class="count-pill">No jobs</span>';
      const operationId = operation.operationId;
      const jobs = operation.jobs || [];
      return `<article class="card operation-card"><div class="operation-summary"><div><p class="eyebrow">${escape(operation.targetDescription)}</p><h4>${escape(operation.commandType)} <span class="muted">· ${escape(operation.createdBy)}</span></h4><small class="muted">${escape(fmtTime(operation.createdAt))} · ${jobs.length} device job${jobs.length === 1 ? '' : 's'}</small></div><div class="operation-counts">${pills}</div><button class="secondary open-operation" data-operation-id="${operationId}">View details</button></div><div class="table-wrap"><table><thead><tr><th>Device</th><th>Status</th><th>Attempts</th><th>Completed</th><th>Exit code</th><th>Result</th></tr></thead><tbody>${jobs.slice(0, 8).map(job => `<tr><td>${escape(job.hostname || job.deviceId)}<small class="muted"> ${escape(job.deviceId)}</small></td><td>${statusBadge(job.status)}</td><td>${job.attemptCount}</td><td>${escape(fmtTime(job.completedAt))}</td><td>${job.exitCode ?? '—'}</td><td>${escape(job.error || job.output || '—').slice(0, 110)}</td></tr>`).join('')}${jobs.length > 8 ? `<tr><td colspan="6" class="muted">+ ${jobs.length - 8} additional device jobs. Open details for full progress.</td></tr>` : ''}</tbody></table></div></article>`;
    }).join('');
    $$('.open-operation').forEach(button => button.addEventListener('click', () => openOperation(button.dataset.operationId)));
  }
  async function openDevice(deviceId) {
    try {
      const device = await api(`/api/devices/${encodeURIComponent(deviceId)}`);
      const dialog = $('#device-dialog');
      const applications = device.applications?.length ? device.applications.map(app => `<li>${escape(app.displayName)} <span class="muted">${escape(app.installedVersion || '')}</span></li>`).join('') : '<li class="muted">No installed application inventory reported.</li>';
      const commands = device.recentCommands?.length ? device.recentCommands.slice(0, 10).map(command => `<tr><td>${escape(command.commandType || '')}</td><td>${statusBadge(command.status)}</td><td>${escape(fmtTime(command.completedAt))}</td><td>${escape(command.error || command.output || '—').slice(0, 90)}</td></tr>`).join('') : '<tr><td colspan="4" class="muted">No command history.</td></tr>';
      const logs = device.logs?.length ? device.logs.slice(0, 12).map(log => `<li><strong>${escape(log.action)}</strong> <span class="muted">${escape(fmtTime(log.occurredAt))} · ${escape(log.actor)} · ${log.succeeded ? 'SUCCESS' : 'FAILED'}</span></li>`).join('') : '<li class="muted">No related administrator audit events.</li>';
      $('#device-detail-content').innerHTML = `<div class="detail-header"><span class="status ${device.connectionState === 'ONLINE' ? 'online' : 'offline'}">${escape(device.connectionState)}</span><div><p class="eyebrow">${escape(device.deviceId)}</p><h3>${escape(device.hostname)}</h3></div></div><div class="detail-grid"><div class="detail-metric"><span>IP address</span><strong>${escape(device.localIp || '—')}</strong></div><div class="detail-metric"><span>Last heartbeat</span><strong>${escape(fmtTime(device.lastHeartbeatAt))}</strong></div><div class="detail-metric"><span>Agent / OS</span><strong>${escape(device.agentVersion || '—')}</strong></div><div class="detail-metric"><span>Policy</span><strong>${device.appliedPolicyVersion} / ${device.desiredPolicyVersion}</strong></div><div class="detail-metric"><span>Sync status</span><strong>${escape(device.syncState)}</strong></div><div class="detail-metric"><span>Pending commands</span><strong>${device.pendingCommandCount}</strong></div></div><div class="detail-actions"><button class="secondary device-action" data-command="SYNC_POLICY">SYNC NOW</button><button class="secondary device-action" data-command="RESTART_PC">RESTART</button><button class="secondary device-action" data-command="SHUTDOWN_PC">SHUTDOWN</button><button class="secondary device-action" data-command="LOCK_PC">LOCK</button><button class="secondary device-terminal">TERMINAL</button></div><section class="detail-section"><h4>Installed applications</h4><ul>${applications}</ul></section><section class="detail-section"><h4>Recent commands</h4><div class="table-wrap"><table><thead><tr><th>Command</th><th>Status</th><th>Completed</th><th>Output / error</th></tr></thead><tbody>${commands}</tbody></table></div></section><section class="detail-section"><h4>Related audit logs</h4><ul>${logs}</ul></section>`;
      $$('.device-action', dialog).forEach(button => button.addEventListener('click', () => quickDeviceCommand(device.deviceId, button.dataset.command)));
      $('.device-terminal', dialog).addEventListener('click', () => { dialog.close(); $('#target-kind').value = 'DEVICE'; updateTargetControls(); $('#one-device').value = device.deviceId; updateTargetPreview(); $('#command-type').value = 'RUN_ADMIN_COMMAND'; updateCommandType(); $('#terminal-command').focus(); switchPanel('dashboard-panel'); toast(`Terminal target prepared for ${device.deviceId}. Enter an approved administrator command.`); });
      dialog.showModal();
    } catch (error) { toast(error.message, 'error'); }
  }
  async function quickDeviceCommand(deviceId, commandType) {
    if (!confirm(`${commandType === 'SYNC_POLICY' ? 'Synchronize desired policy on' : `Create a durable ${commandType} command for`} ${deviceId}?`)) return;
    try {
      if (commandType === 'SYNC_POLICY') {
        await api(`/api/policy/devices/${encodeURIComponent(deviceId)}/sync`, { method: 'POST', body: '{}' });
        toast(`Policy synchronization queued for ${deviceId}.`);
      } else {
        const response = await api('/api/commands', { method: 'POST', body: json({ command_type: commandType, payload: {}, target: { kind: 'DEVICE', device_ids: [deviceId] }, display_name: `${commandType} for ${deviceId}` }) });
        toast(`Created operation ${response.operationId}.`);
      }
      $('#device-dialog').close(); await Promise.all([refreshOperations(), refreshDashboard()]);
    } catch (error) { toast(error.message, 'error'); }
  }
  async function openOperation(operationId) {
    try {
      const operation = await api(`/api/commands/operations/${operationId}`);
      const dialog = $('#operation-dialog');
      $('#operation-detail-content').innerHTML = `<div class="detail-header"><div><p class="eyebrow">${escape(operation.targetDescription)}</p><h3>${escape(operation.commandType)} progress</h3><p class="subtle">Created by ${escape(operation.createdBy)} at ${escape(fmtTime(operation.createdAt))}. Each row is an independently durable device job.</p></div></div><div class="operation-counts" style="margin:19px 0">${Object.entries(operation.summary || {}).map(([key,value]) => `<span class="count-pill ${cssStatus(key)}">${escape(key)}: ${value}</span>`).join('')}</div><div class="table-wrap"><table><thead><tr><th>Device</th><th>Status</th><th>Attempts</th><th>Created</th><th>Received</th><th>Completed</th><th>Exit</th><th>Output / error</th></tr></thead><tbody>${(operation.jobs || []).map(job => `<tr><td><strong>${escape(job.hostname || job.deviceId)}</strong><small class="muted"> ${escape(job.deviceId)}</small></td><td>${statusBadge(job.status)}</td><td>${job.attemptCount}</td><td>${escape(fmtTime(job.createdAt))}</td><td>${escape(fmtTime(job.receivedAt))}</td><td>${escape(fmtTime(job.completedAt))}</td><td>${job.exitCode ?? '—'}</td><td>${escape(job.error || job.output || '—').slice(0,160)}</td></tr>`).join('')}</tbody></table></div>`;
      dialog.showModal();
    } catch (error) { toast(error.message, 'error'); }
  }
  async function loadAudit() {
    try {
      const records = await api('/api/audit?take=250');
      $('#audit-table').innerHTML = records.map(record => `<tr><td>${escape(fmtTime(record.occurred_at || record.occurredAt))}</td><td>${escape(record.actor)}</td><td>${escape(record.action)}</td><td>${escape(record.entity_type || record.entityType)} <small class="muted">${escape(record.entity_id || record.entityId || '')}</small></td><td>${record.succeeded ? statusBadge('SUCCESS') : statusBadge('FAILED')}</td><td>${escape(record.remote_ip || record.remoteIp || '—')}</td></tr>`).join('') || '<tr><td colspan="6" class="muted">No audit entries.</td></tr>';
    } catch (error) { $('#audit-table').innerHTML = `<tr><td colspan="6" class="muted">${escape(error.message)}</td></tr>`; }
  }
  function switchPanel(panelId) {
    $$('.panel').forEach(panel => panel.classList.toggle('active', panel.id === panelId));
    $$('.nav-link').forEach(link => link.classList.toggle('active', link.dataset.panel === panelId));
    const labels = { 'dashboard-panel': ['FLEET OVERVIEW', 'Live device dashboard'], 'groups-panel': ['TARGET ORGANIZATION', 'Device groups'], 'operations-panel': ['PER-DEVICE OUTCOMES', 'Command operations'], 'policy-panel': ['DESIRED STATE', 'Policy management'], 'apps-panel': ['APP CATALOG', 'Application management'], 'audit-panel': ['IMMUTABLE HISTORY', 'Administrator audit trail'] };
    $('#panel-kicker').textContent = labels[panelId]?.[0] || '';
    $('#panel-title').textContent = labels[panelId]?.[1] || '';
    if (panelId === 'audit-panel') loadAudit();
  }

  $('#login-form').addEventListener('submit', async event => {
    event.preventDefault(); setError('#login-error');
    try {
      const response = await api('/api/auth/login', { method: 'POST', body: json({ username: $('#login-username').value, password: $('#login-password').value }) });
      state.me = response; $('#login-password').value = ''; await showApp();
    } catch (error) { setError('#login-error', error.message); }
  });
  $('#logout-button').addEventListener('click', async () => { try { await api('/api/auth/logout', { method: 'POST' }); } catch {} state.me = null; showLogin(); });
  $('#refresh-button').addEventListener('click', refreshAll);
  $('#operations-refresh').addEventListener('click', () => refreshOperations().catch(error => toast(error.message, 'error')));
  $('#audit-refresh').addEventListener('click', loadAudit);
  $('#device-search').addEventListener('input', filterDevices);
  $('#target-kind').addEventListener('change', updateTargetControls);
  $('#command-type').addEventListener('change', updateCommandType);
  $('#one-device').addEventListener('change', updateTargetPreview);
  $('#target-group').addEventListener('change', updateTargetPreview);
  $$('.nav-link').forEach(link => link.addEventListener('click', () => switchPanel(link.dataset.panel)));
  $('#chrome-incognito').addEventListener('change', updatePolicyStateLabels);
  $('#edge-inprivate').addEventListener('change', updatePolicyStateLabels);
  $('#close-device-dialog').addEventListener('click', () => $('#device-dialog').close());
  $('#close-operation-dialog').addEventListener('click', () => $('#operation-dialog').close());

  $('#command-form').addEventListener('submit', async event => {
    event.preventDefault(); setError('#command-error');
    let payload;
    try { payload = JSON.parse($('#command-payload').value || '{}'); if (!payload || Array.isArray(payload) || typeof payload !== 'object') throw new Error(); }
    catch { setError('#command-error', 'Payload must be a valid JSON object.'); return; }
    if ($('#command-type').value === 'RUN_ADMIN_COMMAND') {
      const terminalCommand = $('#terminal-command').value.trim();
      if (terminalCommand) payload.command = terminalCommand;
      if (!payload.command || typeof payload.command !== 'string') { setError('#command-error', 'Enter an administrator terminal command.'); return; }
    }
    const kind = $('#target-kind').value;
    const target = { kind, device_ids: null, group_id: null };
    if (kind === 'DEVICE') target.device_ids = [$('#one-device').value];
    if (kind === 'DEVICES') target.device_ids = $$('#device-checklist input:checked').map(input => input.value);
    if (kind === 'GROUP') target.group_id = $('#target-group').value || null;
    if ((kind === 'DEVICE' || kind === 'DEVICES') && !target.device_ids.filter(Boolean).length) { setError('#command-error', 'Select at least one target device.'); return; }
    if (kind === 'GROUP' && !target.group_id) { setError('#command-error', 'Select a target group.'); return; }
    try {
      const result = await api('/api/commands', { method: 'POST', body: json({ command_type: $('#command-type').value, payload, target, display_name: $('#command-label').value || null }) });
      toast(`Created operation ${result.operationId} with ${result.targetCount} durable job${result.targetCount === 1 ? '' : 's'}.`);
      $('#command-label').value = ''; await Promise.all([refreshOperations(), refreshDashboard()]); switchPanel('operations-panel');
    } catch (error) { setError('#command-error', error.message); }
  });
  $('#policy-form').addEventListener('submit', async event => {
    event.preventDefault(); setError('#policy-error');
    try {
      const result = await api('/api/policy', { method: 'PUT', body: json({ dns_enabled: $('#dns-enabled').checked, dns_server: $('#dns-server').value, chrome_incognito: $('#chrome-incognito').checked, edge_inprivate: $('#edge-inprivate').checked, reason: $('#policy-reason').value || null }) });
      toast(`Desired policy saved as version ${result.policy_version}.`); $('#policy-reason').value = ''; await Promise.all([refreshPolicy(), refreshDashboard()]);
    } catch (error) { setError('#policy-error', error.message); }
  });
  $('#sync-all-button').addEventListener('click', async () => {
    if (!confirm('Create a new durable policy revision and synchronize every managed device? Offline devices will remain pending until reconnect.')) return;
    setError('#policy-error');
    try { const result = await api('/api/policy/sync-all', { method: 'POST', body: '{}' }); toast(`SYNC ALL created policy version ${result.policy_version}.`); await Promise.all([refreshPolicy(), refreshDashboard()]); }
    catch (error) { setError('#policy-error', error.message); }
  });
  $('#group-form').addEventListener('submit', async event => {
    event.preventDefault(); setError('#group-error');
    const deviceIds = $$('#group-device-checklist input:checked').map(input => input.value);
    try {
      await api('/api/groups', { method: 'POST', body: json({ name: $('#group-name').value, description: $('#group-description').value || null, device_ids: deviceIds }) });
      event.target.reset(); toast('Device group created.'); await refreshGroups();
    } catch (error) { setError('#group-error', error.message); }
  });
  $('#application-form').addEventListener('submit', async event => {
    event.preventDefault(); setError('#app-error');
    try {
      await api('/api/applications', { method: 'POST', body: json({ package_id: $('#app-package').value, display_name: $('#app-name').value, source: $('#app-source').value, desired_version: $('#app-version').value || null, install_arguments: null, is_enabled: true }) });
      event.target.reset(); $('#app-source').value = 'winget'; toast('Application added to approved catalog.'); await refreshApplications();
    } catch (error) { setError('#app-error', error.message); }
  });
  $('#deploy-target-kind').addEventListener('change', () => { const kind = $('#deploy-target-kind').value; $('#deploy-device-wrap').classList.toggle('hidden', kind !== 'DEVICE'); $('#deploy-group-wrap').classList.toggle('hidden', kind !== 'GROUP'); });
  $('#deploy-form').addEventListener('submit', async event => {
    event.preventDefault(); setError('#deploy-error');
    const kind = $('#deploy-target-kind').value;
    const appId = $('#deploy-app').value;
    if (!appId) { setError('#deploy-error', 'Choose an enabled catalog application.'); return; }
    const target = { kind, device_ids: kind === 'DEVICE' ? [$('#deploy-device').value] : null, group_id: kind === 'GROUP' ? $('#deploy-group').value || null : null };
    if ((kind === 'DEVICE' && !target.device_ids[0]) || (kind === 'GROUP' && !target.group_id)) { setError('#deploy-error', 'Choose a valid deployment target.'); return; }
    try { const result = await api(`/api/applications/${encodeURIComponent(appId)}/deploy/${encodeURIComponent($('#deploy-action').value)}`, { method: 'POST', body: json(target) }); toast(`Deployment operation ${result.operationId} has ${result.targetCount} durable jobs.`); await refreshOperations(); switchPanel('operations-panel'); }
    catch (error) { setError('#deploy-error', error.message); }
  });

  initialize();
})();
