// The AI OS Care Archive page's script (plan §15s D6, D9, E10.S2a). Loaded from media/ by the static shell of
// src/panel/panelHtml.ts under a nonce-only CSP. It receives ONE kind of message — { type: 'view', view } built by the host's view
// model (src/archivePage/archiveViewModel.ts) — and builds the DOM from it with createElement and textContent ONLY: no innerHTML,
// no markup strings, nothing parsed, and NO ARITHMETIC — every figure arrives spelt; the page lays it out. The rendering of this
// page lives in this ONE file, so it can move into the shared webview kit (todo/PLAN_disk_health.md E18) as a whole.
// src/test/archivePage/archivePage.test.ts runs this file in a strict DOM that throws on every HTML sink.
//
// It sends the host only its closed set (src/archivePage/archiveMessages.ts): ready, rendered, refresh, showPaths / hidePaths,
// expandMonth / collapseMonth / selectMonth with a month's INDEX and toggleEntry with an entry's INDEX — each with the view's
// generation (E10.S2b) — and the bare clearSelection and restore. Never a month name, an agent, a session path or an entry id: the
// selection a restore takes is the HOST's.
(function () {
  'use strict';

  const vscode = acquireVsCodeApi();
  const root = document.getElementById('archive');

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

  /** A button that posts `message()` when pressed — and nothing while it is disabled, as a browser would not fire it. */
  function button(label, data, message) {
    const node = element('button', label, data);
    node.setAttribute('type', 'button');
    node.addEventListener('click', function () {
      if (!node.disabled) {
        vscode.postMessage(message());
      }
    });
    return node;
  }

  // The notice is the page's ONE live region and the SAME element for the page's whole life (as in logs.js): only its text
  // changes, so a screen reader announces the new sentence and nothing else.
  const notice = element('p', '', { notice: '', level: 'none' });
  notice.setAttribute('aria-live', 'polite');

  function updateNotice(view) {
    if (notice.textContent !== view.notice) {
      notice.textContent = view.notice;
    }
    notice.setAttribute('data-level', view.noticeLevel);
  }

  function pathsButton(view) {
    const node = button(view.pathsLabel, { action: 'paths' }, function () { return { type: view.pathsShown ? 'hidePaths' : 'showPaths' }; });
    node.setAttribute('aria-pressed', view.pathsShown ? 'true' : 'false');
    return node;
  }

  function header(view) {
    const top = element('header', undefined, { part: 'header' });
    top.appendChild(element('h1', view.heading, { heading: '' }));
    updateNotice(view);
    top.appendChild(notice);
    top.appendChild(element('p', view.scope, { scope: '' }));
    const tools = element('div', undefined, { part: 'tools' });
    const refresh = button('Refresh', { action: 'refresh' }, function () { return { type: 'refresh' }; });
    refresh.disabled = !view.refreshEnabled;
    tools.appendChild(refresh);
    tools.appendChild(pathsButton(view));
    top.appendChild(tools);
    return top;
  }

  function linePart(model) {
    const line = element('div', undefined, { line: model.id });
    line.appendChild(element('span', model.label, { label: '' }));
    line.appendChild(element('span', model.value, { value: '' }));
    return line;
  }

  function notesPart(notes) {
    const box = element('div', undefined, { notes: '' });
    notes.forEach(function (note) { box.appendChild(element('p', note, { note: '' })); });
    return box;
  }

  function summary(view) {
    const node = element('section', undefined, { block: 'summary', state: view.state });
    node.setAttribute('aria-label', 'The archive');
    view.lines.forEach(function (line) { node.appendChild(linePart(line)); });
    node.appendChild(notesPart(view.notes));
    return node;
  }

  function headerRow(headers) {
    const head = element('thead');
    const line = element('tr');
    headers.forEach(function (name) {
      const cell = element('th', name, { header: '' });
      cell.setAttribute('scope', 'col');
      line.appendChild(cell);
    });
    head.appendChild(line);
    return head;
  }

  /** A selectable entry's toggle (E10.S2b): pressed while it is selected; a row that cannot be restored has none. */
  function selectCell(row, generation) {
    const cell = element('td', undefined, { select: '' });
    if (row.selectable) {
      const toggle = button(row.selected ? 'Selected' : 'Select', { 'select-entry': String(row.index) }, function () {
        return { type: 'toggleEntry', index: row.index, generation: generation };
      });
      toggle.setAttribute('aria-pressed', row.selected ? 'true' : 'false');
      cell.appendChild(toggle);
    }
    return cell;
  }

  function entryRow(row, generation) {
    const line = element('tr', undefined, { entry: '' });
    row.cells.forEach(function (value) { line.appendChild(element('td', value, { cell: '' })); });
    line.appendChild(selectCell(row, generation));
    return line;
  }

  function entriesTable(month, headers, generation) {
    const table = element('table', undefined, { entries: String(month.index) });
    table.appendChild(headerRow(headers));
    const body = element('tbody');
    month.rows.forEach(function (row) { body.appendChild(entryRow(row, generation)); });
    table.appendChild(body);
    return table;
  }

  function monthButton(month, generation) {
    const node = button(month.expanded ? 'Hide entries' : 'Show entries', { month: String(month.index) }, function () {
      return { type: month.expanded ? 'collapseMonth' : 'expandMonth', index: month.index, generation: generation };
    });
    node.setAttribute('aria-expanded', month.expanded ? 'true' : 'false');
    return node;
  }

  function selectMonthButton(month, generation) {
    const node = button('Select month', { 'select-month': String(month.index) }, function () {
      return { type: 'selectMonth', index: month.index, generation: generation };
    });
    node.disabled = !month.selectEnabled;
    return node;
  }

  function monthPart(month, headers, generation) {
    const box = element('div', undefined, { 'month-of': String(month.index) });
    box.appendChild(element('h3', month.month, { title: '' }));
    box.appendChild(element('p', month.summary, { summary: '' }));
    box.appendChild(monthButton(month, generation));
    box.appendChild(selectMonthButton(month, generation));
    if (month.expanded) {
      box.appendChild(entriesTable(month, headers, generation));
    }
    return box;
  }

  function agentSection(agent, headers, generation) {
    const node = element('section', undefined, { agent: agent.agent });
    node.setAttribute('aria-label', agent.agent);
    node.appendChild(element('h2', agent.agent, { title: '' }));
    node.appendChild(element('p', agent.summary, { summary: '' }));
    agent.months.forEach(function (month) { node.appendChild(monthPart(month, headers, generation)); });
    return node;
  }

  /** The selection bar (E10.S2b): what is selected, the ceiling, Restore, Clear selection, and the restore line — the host's words. */
  function selectionBar(selection) {
    const bar = element('section', undefined, { block: 'selection' });
    bar.setAttribute('aria-label', 'Restore');
    bar.appendChild(element('p', selection.summary, { 'selection-summary': '' }));
    const restore = button('Restore', { action: 'restore' }, function () { return { type: 'restore' }; });
    restore.disabled = !selection.restoreEnabled;
    const clear = button('Clear selection', { action: 'clearSelection' }, function () { return { type: 'clearSelection' }; });
    clear.disabled = !selection.clearEnabled;
    bar.appendChild(restore);
    bar.appendChild(clear);
    bar.appendChild(element('p', selection.line, { 'restore-line': '', level: selection.lineLevel }));
    return bar;
  }

  function isObject(value) {
    return value !== null && typeof value === 'object';
  }

  /** Only a view message is rendered; anything else is ignored. */
  function isView(data) {
    return isObject(data) && data.type === 'view' && isArchiveView(data.view);
  }

  function hasLists(view) {
    return Array.isArray(view.agents) && Array.isArray(view.lines) && Array.isArray(view.notes);
  }

  function isArchiveView(view) {
    return isObject(view) && hasLists(view) && isObject(view.selection);
  }

  function render(view) {
    const agents = view.agents.map(function (agent) { return agentSection(agent, view.headers, view.generation); });
    const bar = view.state === 'answered' ? [selectionBar(view.selection)] : [];
    root.replaceChildren(header(view), summary(view), ...bar, ...agents);
    vscode.postMessage({ type: 'rendered', blocks: agents.length });
  }

  window.addEventListener('message', function (event) {
    if (isView(event.data)) {
      render(event.data.view);
    }
  });

  vscode.postMessage({ type: 'ready' });
})();
