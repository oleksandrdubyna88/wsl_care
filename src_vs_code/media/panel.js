// The AI OS Care panel's page script (plan §7.2, §15g M7). Loaded from media/ by the static shell of
// src/panel/panelHtml.ts under a nonce-only CSP. It receives ONE kind of message — { type: 'view', view } built by the
// host's view model (src/panel/viewModel.ts) — and builds the DOM from it with createElement and textContent ONLY:
// no innerHTML, no markup strings, nothing parsed. Every value is text; a process name of '</script><img …>' is shown
// as those characters (src/test/panelPage.test.ts runs this file in a strict DOM that throws on every HTML sink).
//
// It sends the host only its closed set (src/panel/messages.ts): ready, rendered, the id of a pressed button — and, since
// E6.S3, the cleanup messages: clean / cleanSelected with row ids, runFullCheck, stop with the index the host gave it — and the
// Windows Time guard's bare installWindowsTimeGuard / removeWindowsTimeGuard, (E10.S1) the archive's bare
// chooseArchiveFolder / stopArchiving — and *Enable <GPU>*'s enableGpu with the index the host gave it (PLAN_gpu_health.md).
(function () {
  'use strict';

  const vscode = acquireVsCodeApi();
  const root = document.getElementById('panel');

  /** An element with optional text and data attributes. */
  function element(tag, textValue, data) {
    const node = document.createElement(tag);
    if (typeof textValue === 'string') {
      node.textContent = textValue;
    }
    for (const [key, value] of Object.entries(data || {})) {
      node.setAttribute('data-' + key, value);
    }
    return node;
  }

  function button(action) {
    const node = element('button', action.label, { action: action.id });
    node.setAttribute('type', 'button');
    node.addEventListener('click', function () {
      vscode.postMessage({ type: action.id });
    });
    return node;
  }

  // The notice is the page's ONE live region, and it is the SAME element for the page's whole life: every render rebuilds
  // the rest of the tree, and a live region that is replaced each time is either announced whole or not at all. Only its
  // text changes — and only when it differs — so a screen reader announces the new sentence and nothing else.
  const notice = element('p', '', { notice: '', level: 'none' });
  notice.setAttribute('aria-live', 'polite');

  function updateNotice(view) {
    if (notice.textContent !== view.notice) {
      notice.textContent = view.notice;
    }
    notice.setAttribute('data-level', view.noticeLevel);
  }

  function header(view) {
    const top = element('header', undefined, { part: 'header' });
    top.appendChild(element('h1', view.heading, { heading: '' }));
    updateNotice(view);
    top.appendChild(notice);
    const bar = element('div', undefined, { part: 'actions' });
    view.actions.forEach(function (action) {
      bar.appendChild(button(action));
    });
    top.appendChild(bar);
    return top;
  }

  /** A row's list as a sub-table: its headers, then one line per item. */
  function itemsTable(row) {
    const table = element('table', undefined, { items: '' });
    const head = element('thead');
    const headLine = element('tr');
    row.headers.forEach(function (name) {
      const cell = element('th', name, { header: '' });
      cell.setAttribute('scope', 'col');
      headLine.appendChild(cell);
    });
    head.appendChild(headLine);
    const body = element('tbody');
    row.items.forEach(function (cells) {
      const line = element('tr', undefined, { item: '' });
      cells.forEach(function (value) {
        line.appendChild(element('td', value, { cell: '' }));
      });
      body.appendChild(line);
    });
    table.appendChild(head);
    table.appendChild(body);
    return table;
  }

  /** The row itself, and — when it has a list — a second line holding it. */
  function rowLines(row) {
    const line = element('tr', undefined, { row: row.id, state: row.state, level: row.level });
    const label = element('th', row.label, { label: '' });
    label.setAttribute('scope', 'row');
    line.appendChild(label);
    line.appendChild(element('td', row.value, { value: '' }));
    if (row.items.length === 0) {
      return [line];
    }
    const holder = element('tr', undefined, { 'items-for': row.id });
    const cell = element('td');
    cell.setAttribute('colspan', '2');
    cell.appendChild(itemsTable(row));
    holder.appendChild(cell);
    return [line, holder];
  }

  function section(model) {
    const node = element('section', undefined, { section: model.id });
    node.setAttribute('aria-label', model.title);
    node.appendChild(element('h2', model.title, { title: '' }));
    const table = element('table');
    const body = element('tbody');
    model.rows.forEach(function (row) {
      rowLines(row).forEach(function (line) {
        body.appendChild(line);
      });
    });
    table.appendChild(body);
    node.appendChild(table);
    return node;
  }

  // ---- E6.S3: the cleanup controls (src/panel/view.ts CleanupControls) — every state the host derived; the page holds
  // only which rows are ticked for "Clean selected", a selection, never a status. Each button posts one message of the
  // closed set (src/panel/messages.ts): row ids of the compiled enum, an index the host gave it, or nothing at all. ----

  /** The rows ticked for "Clean selected" — kept across renders, dropped once a row is no longer enabled. */
  const selected = new Set();

  /** A button that posts `message` when pressed — and nothing while it is disabled, as a browser would not fire it. */
  function actionButton(label, data, disabled, message) {
    const node = element('button', label, data);
    node.setAttribute('type', 'button');
    node.disabled = disabled;
    node.addEventListener('click', function () {
      if (!node.disabled) {
        vscode.postMessage(message());
      }
    });
    return node;
  }

  function selectedIds(rows) {
    return rows.filter(function (row) { return selected.has(row.rowId); }).map(function (row) { return row.rowId; });
  }

  function selectedLabel(rows) {
    return 'Clean selected (' + selectedIds(rows).length + ')';
  }

  function cleanSelected(controls) {
    const node = actionButton(selectedLabel(controls.rows), { 'clean-selected': '' }, true, function () {
      return { type: 'cleanSelected', rowIds: selectedIds(controls.rows) };
    });
    node.disabled = !controls.enabled || selectedIds(controls.rows).length === 0;
    return node;
  }

  /** Ticking a row: its pressed state, and the "Clean selected" button's count and state, changed in place. */
  function toggle(row, toggleButton, controls, selectedButton) {
    if (selected.has(row.rowId)) {
      selected.delete(row.rowId);
    } else {
      selected.add(row.rowId);
    }
    toggleButton.setAttribute('aria-pressed', selected.has(row.rowId) ? 'true' : 'false');
    selectedButton.textContent = selectedLabel(controls.rows);
    selectedButton.disabled = !controls.enabled || selectedIds(controls.rows).length === 0;
  }

  function cleanRow(row, controls, selectedButton) {
    const line = element('div', undefined, { 'clean-row': row.rowId });
    // A tick is a page-local selection: it toggles and posts NOTHING (review C15).
    const tick = element('button', 'Select ' + row.rowId, { select: row.rowId });
    tick.setAttribute('type', 'button');
    tick.disabled = !row.enabled;
    tick.setAttribute('aria-pressed', selected.has(row.rowId) ? 'true' : 'false');
    tick.addEventListener('click', function () {
      if (!tick.disabled) {
        toggle(row, tick, controls, selectedButton);
      }
    });
    line.appendChild(tick);
    line.appendChild(actionButton(row.label, { clean: row.rowId }, !row.enabled, function () { return { type: 'clean', rowIds: [row.rowId] }; }));
    line.appendChild(element('span', row.note, { note: '' }));
    return line;
  }

  function pruneSelection(rows) {
    Array.from(selected).forEach(function (id) {
      if (!rows.some(function (row) { return row.rowId === id && row.enabled; })) {
        selected.delete(id);
      }
    });
  }

  function stopPart(controls) {
    if (controls.stop !== undefined && controls.stop !== null) {
      const index = controls.stop.index;
      return actionButton(controls.stop.label, { stop: '' }, false, function () { return { type: 'stop', index: index }; });
    }
    return element('p', controls.stopText, { 'stop-text': '' });
  }

  function cleanupControls(controls) {
    pruneSelection(controls.rows);
    const box = element('div', undefined, { cleanup: '' });
    box.appendChild(element('p', controls.state, { 'cleanup-state': '', level: controls.stateLevel }));
    box.appendChild(element('p', controls.reason, { 'cleanup-reason': '' }));
    const selectedButton = cleanSelected(controls);
    controls.rows.forEach(function (row) {
      box.appendChild(cleanRow(row, controls, selectedButton));
    });
    box.appendChild(selectedButton);
    box.appendChild(actionButton('Run full check now', { 'full-check': '' }, !controls.fullCheck, function () { return { type: 'runFullCheck' }; }));
    box.appendChild(stopPart(controls));
    return box;
  }

  function lastCleanupParts(controls) {
    const box = element('div', undefined, { 'last-cleanup': '' });
    const list = element('ul', undefined, { results: '' });
    controls.results.forEach(function (result) {
      list.appendChild(element('li', result.sentence, { result: '', level: result.level }));
    });
    box.appendChild(list);
    box.appendChild(element('p', controls.dockerAfter, { 'docker-after': '' }));
    // E6.S4 (§7.4): the Logs page on the run status.lastCleanup names — bare, the run id is the host's.
    box.appendChild(actionButton('Logs', { 'run-logs': '' }, false, function () { return { type: 'openRunLogs' }; }));
    return box;
  }

  // ---- The Windows Time guard (PLAN_windows_time_task.md D5, D8 o5): ONE line the host derived from Task Scheduler's
  // answer and the pending elevated run it persisted, and the buttons it allows — each posts its bare id, nothing else.
  // Since PLAN_pwsh_engine_windows_time.md D6 also which PowerShell its elevated runs use, and that its task runs on 5.1. ----

  /** A paragraph for a line the host may leave out — none for an absent or empty one. */
  function optionalLine(text, data) {
    return typeof text === 'string' && text !== '' ? [element('p', text, data)] : [];
  }

  function guardPart(guard) {
    const box = element('div', undefined, { guard: '' });
    box.appendChild(element('p', guard.line, { 'guard-line': '', level: guard.level }));
    optionalLine(guard.engine, { 'guard-engine': '' }).forEach(function (p) { box.appendChild(p); });
    guard.buttons.forEach(function (b) {
      const id = b.id;
      box.appendChild(actionButton(b.label, { 'guard-action': id }, !b.enabled, function () { return { type: id }; }));
    });
    return box;
  }

  // ---- E10.S1: the AI-session archive (src/archive/archiveView.ts ArchiveControls) — the base folder the daemon reads, per
  // agent what is due and its own retention (with the badge when that retention deletes sooner than the archive takes), the
  // run lock and the last run; and the archive's buttons, each posting its bare id — no folder ever comes from the page. ----

  // One line a reader reads as a sentence: "Claude Code — 3 sessions · … older than 14 d — its own cleanup deletes after 90 d" —
  // each part keeps its own span (the badge its warn level as data), a part with no text is left out (the badge when there is
  // none, a name the daemon did not give), and a " — " span stands between each two parts shown.
  function archiveAgent(agent) {
    const line = element('li', undefined, { 'archive-agent': '' });
    const parts = [
      [agent.name, { 'agent-name': '' }],
      [agent.due, { 'agent-due': '' }],
      [agent.retention, { 'agent-retention': '' }],
      [agent.badge, { 'retention-badge': '', level: 'warn' }],
    ].filter(function (part) { return part[0] !== ''; }).map(function (part) { return element('span', part[0], part[1]); });
    parts.forEach(function (part, index) {
      if (index > 0) {
        line.appendChild(element('span', ' — ', { 'agent-separator': '' }));
      }
      line.appendChild(part);
    });
    return line;
  }

  function archivePart(archive) {
    const box = element('div', undefined, { archive: '' });
    box.appendChild(element('p', archive.line, { 'archive-line': '', level: archive.level }));
    // The host's own words while the daemon is asked again, and why the per-agent lines are missing — never "nothing due".
    box.appendChild(element('p', archive.reading, { 'archive-reading': '' }));
    box.appendChild(element('p', archive.previewNote, { 'archive-preview-note': '' }));
    const list = element('ul', undefined, { 'archive-agents': '' });
    archive.agents.forEach(function (agent) { list.appendChild(archiveAgent(agent)); });
    box.appendChild(list);
    box.appendChild(element('p', archive.lock, { 'archive-lock': '' }));
    box.appendChild(element('p', archive.lastRun, { 'archive-last-run': '' }));
    archive.buttons.forEach(function (b) {
      const id = b.id;
      box.appendChild(actionButton(b.label, { 'archive-action': id }, !b.enabled, function () { return { type: id }; }));
    });
    return box;
  }

  function isArchive(value) {
    return isObject(value) && Array.isArray(value.buttons) && Array.isArray(value.agents);
  }

  // ---- The GPU alerts (PLAN_gpu_health.md D6): the FIRST element of the panel, above the heading — a GPU that dropped out
  // of Windows must be impossible to miss. ONE persistent container with role="alert", like the notice: its children are
  // rebuilt only when the alerts' words change, so a screen reader announces a new alert once, not on every render. The
  // level is carried by words and a sign, never by colour alone. Its button posts { type: 'enableGpu', index } — the index
  // of the adapter in the host's own reading; the host holds the device id. ----

  const gpuBox = element('div', undefined, { 'gpu-alerts': '' });
  gpuBox.setAttribute('role', 'alert');
  let gpuWords = '';
  // The NEWEST read's stamp per adapter index: a click names the read the host holds now, while the alert's WORDS — not the
  // stamp, which changes with every read — decide whether the live region is rebuilt and re-announced (own review 2, F4).
  let gpuReadAt = {};

  /** The alert's words: everything a person reads or presses, without the read's stamp. */
  function gpuWordsOf(alert) {
    const button = isObject(alert.button) ? { index: alert.button.index, label: alert.button.label, enabled: alert.button.enabled } : undefined;
    return { level: alert.level, line: alert.line, fix: alert.fix, consequence: alert.consequence, button: button };
  }

  const SIGN = { critical: '⚠ ', unknown: '? ' };

  function gpuAlert(alert) {
    const box = element('div', undefined, { 'gpu-alert': '', level: alert.level });
    box.appendChild(element('p', (SIGN[alert.level] || '') + alert.line, { 'gpu-line': '' }));
    box.appendChild(element('p', alert.fix, { 'gpu-fix': '' }));
    // D6 of PLAN_pwsh_engine_windows_time.md: the engine Enable elevates, beside its button.
    optionalLine(alert.consequence, { 'gpu-consequence': '' }).concat(optionalLine(alert.engine, { 'gpu-engine': '' })).forEach(function (p) { box.appendChild(p); });
    if (isObject(alert.button)) {
      const index = alert.button.index;
      box.appendChild(actionButton(alert.button.label, { 'gpu-action': 'enableGpu' }, !alert.button.enabled, function () { return { type: 'enableGpu', index: index, readAt: gpuReadAt[index] }; }));
    }
    return box;
  }

  function updateGpu(view) {
    const alerts = isObject(view.gpu) && Array.isArray(view.gpu.alerts) ? view.gpu.alerts : [];
    // A new map each render (never the old one changed in place — code round 5).
    gpuReadAt = alerts.reduce(function (stamps, alert) {
      return isObject(alert.button) ? Object.assign({}, stamps, { [alert.button.index]: alert.button.readAt }) : stamps;
    }, {});
    const words = JSON.stringify(alerts.map(gpuWordsOf));
    if (words !== gpuWords) {
      gpuWords = words;
      gpuBox.replaceChildren(...alerts.map(gpuAlert));
    }
    return gpuBox;
  }

  /** The GPU host's line when every adapter works ('' otherwise — the alerts above carry the rest). */
  function gpuLine(view) {
    const line = isObject(view.gpu) && typeof view.gpu.line === 'string' ? view.gpu.line : '';
    return line === '' ? [] : [element('p', line, { 'gpu-ok': '' })];
  }

  function guardParts(view) {
    return isObject(view.windowsTimeGuard) && Array.isArray(view.windowsTimeGuard.buttons) ? [guardPart(view.windowsTimeGuard)] : [];
  }

  /** E7.S5a: the bundled Windows daemon's version line (src/bundled/bundledView.ts) — nothing when the host says nothing. */
  function windowsDaemonLine(view) {
    const row = isObject(view.windowsDaemon) ? view.windowsDaemon : {};
    return optionalLine(row.line, { 'windows-daemon': '', level: typeof row.level === 'string' ? row.level : 'none' });
  }

  // ---- WR-E2 (PLAN_windows_runner.md §3.8): the Windows tasks' lines — the staged copy's, one per task and the dry state's — as
  // TEXT with the level the host derived; nothing at all when the host says nothing (off Windows, or before a caller derived it). ----

  /** The level the host derived for a line, or none. */
  function levelOf(row) {
    return typeof row.level === 'string' ? row.level : 'none';
  }

  function hasText(value) {
    return typeof value === 'string' && value !== '';
  }

  /** The host's Windows tasks part when it has a copy line to show; none otherwise. */
  function windowsTasksData(view) {
    const part = isObject(view.windowsTasks) ? view.windowsTasks : {};
    return isObject(part.copy) && hasText(part.copy.line) ? part : undefined;
  }

  function windowsTaskLine(task) {
    return element('p', String(task.line), { 'windows-task': String(task.id), level: levelOf(task) });
  }

  function windowsTasksParts(view) {
    const part = windowsTasksData(view);
    if (part === undefined) {
      return [];
    }
    const box = element('div', undefined, { 'windows-tasks': '' });
    const copy = element('p', part.copy.line, { 'windows-tasks-copy': '', level: levelOf(part.copy) });
    [copy].concat((Array.isArray(part.tasks) ? part.tasks : []).map(windowsTaskLine), optionalLine(part.dry, { 'windows-tasks-dry': '' })).forEach(function (line) { box.appendChild(line); });
    return [box];
  }

  function healthExtras(view) {
    const box = element('div', undefined, { 'health-extras': '' });
    gpuLine(view).concat(windowsDaemonLine(view), windowsTasksParts(view), guardParts(view)).forEach(function (part) { box.appendChild(part); });
    return box;
  }

  // ---- S7b.2b: the client-gone Windows MCP relays (src/relays/relaysView.ts RelaysControls) — the daemon's interopRelays line,
  // why the button is greyed, and *Stop client-gone relays…*, posting its bare id: the processes come from the host's held preview. ----

  function relaysPart(relays) {
    const box = element('div', undefined, { relays: '' });
    box.appendChild(element('p', relays.line, { 'relays-line': '', level: relays.level }));
    box.appendChild(element('p', relays.why, { 'relays-why': '' }));
    const id = relays.button.id;
    box.appendChild(actionButton(relays.button.label, { 'relays-action': id }, !relays.button.enabled, function () { return { type: id }; }));
    return box;
  }

  function isRelays(value) {
    return isObject(value) && typeof value.line === 'string' && value.line !== '' && isObject(value.button);
  }

  /** The AI agents section's extras: the archive's part, then the relays' line and button — each only when the host derived it. */
  function aiAgentsParts(view) {
    const parts = [isArchive(view.archive) ? archivePart(view.archive) : undefined, isRelays(view.relays) ? relaysPart(view.relays) : undefined].filter(function (part) { return part !== undefined; });
    if (parts.length === 0) {
      return undefined;
    }
    const box = element('div', undefined, { 'ai-agents-extras': '' });
    parts.forEach(function (part) { box.appendChild(part); });
    return box;
  }

  /** The sections the host's extra parts belong to — by the ids of src/panel/fieldMap.ts's SECTIONS. */
  const EXTRAS = {
    cleanup: function (view) { return isObject(view.cleanup) ? cleanupControls(view.cleanup) : undefined; },
    lastCleanup: function (view) { return isObject(view.cleanup) ? lastCleanupParts(view.cleanup) : undefined; },
    health: healthExtras,
    aiAgents: function (view) { return aiAgentsParts(view); },
  };

  function sectionWithExtras(model, view) {
    const node = section(model);
    const extra = Object.prototype.hasOwnProperty.call(EXTRAS, model.id) ? EXTRAS[model.id](view) : undefined;
    if (extra !== undefined) {
      node.appendChild(extra);
    }
    return node;
  }

  function isObject(value) {
    return value !== null && typeof value === 'object';
  }

  /** Only a view message is rendered; anything else is ignored. */
  function isView(data) {
    return isObject(data) && data.type === 'view' && isObject(data.view) && Array.isArray(data.view.sections);
  }

  // The rest of the panel lives in ONE container beside the live region: a render replaces that container's children and
  // never touches the root's, so the role="alert" box is mounted once and never detached (code round 4).
  const rest = element('div', undefined, { part: 'rest' });
  let mounted = false;

  function render(view) {
    updateGpu(view);
    if (!mounted) {
      root.replaceChildren(gpuBox, rest);
      mounted = true;
    }
    rest.replaceChildren(header(view), ...view.sections.map(function (model) { return sectionWithExtras(model, view); }));
    const rows = view.sections.reduce(function (sum, model) {
      return sum + model.rows.length;
    }, 0);
    vscode.postMessage({ type: 'rendered', rows: rows });
  }

  window.addEventListener('message', function (event) {
    if (isView(event.data)) {
      render(event.data.view);
    }
  });

  vscode.postMessage({ type: 'ready' });
})();
