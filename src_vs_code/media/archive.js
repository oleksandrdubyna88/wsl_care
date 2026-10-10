// The AI OS Care Archive page's script (plan §15s D6, D9, E10.S2a). Loaded from media/ by the static shell of
// src/panel/panelHtml.ts under a nonce-only CSP. It receives ONE kind of message — { type: 'view', view } built by the host's view
// model (src/archivePage/archiveViewModel.ts) — and builds the DOM from it with createElement and textContent ONLY: no innerHTML,
// no markup strings, nothing parsed, and NO ARITHMETIC — every figure arrives spelt; the page lays it out. The rendering of this
// page lives in this ONE file, so it can move into the shared webview kit (todo/PLAN_disk_health.md E18) as a whole.
// src/test/archivePage/archivePage.test.ts runs this file in a strict DOM that throws on every HTML sink.
//
// It sends the host only its closed set (src/archivePage/archiveMessages.ts): ready, rendered, refresh, showPaths / hidePaths, and
// expandMonth / collapseMonth with a month's INDEX — never a month name, an agent, a session path or an entry id.
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
    tools.appendChild(button('Refresh', { action: 'refresh' }, function () { return { type: 'refresh' }; }));
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

  function entryRow(row) {
    const line = element('tr', undefined, { entry: '' });
    row.cells.forEach(function (value) { line.appendChild(element('td', value, { cell: '' })); });
    return line;
  }

  function entriesTable(month, headers) {
    const table = element('table', undefined, { entries: String(month.index) });
    table.appendChild(headerRow(headers));
    const body = element('tbody');
    month.rows.forEach(function (row) { body.appendChild(entryRow(row)); });
    table.appendChild(body);
    return table;
  }

  function monthButton(month) {
    const node = button(month.expanded ? 'Hide entries' : 'Show entries', { month: String(month.index) }, function () {
      return { type: month.expanded ? 'collapseMonth' : 'expandMonth', index: month.index };
    });
    node.setAttribute('aria-expanded', month.expanded ? 'true' : 'false');
    return node;
  }

  function monthPart(month, headers) {
    const box = element('div', undefined, { 'month-of': String(month.index) });
    box.appendChild(element('h3', month.month, { title: '' }));
    box.appendChild(element('p', month.summary, { summary: '' }));
    box.appendChild(monthButton(month));
    if (month.expanded) {
      box.appendChild(entriesTable(month, headers));
    }
    return box;
  }

  function agentSection(agent, headers) {
    const node = element('section', undefined, { agent: agent.agent });
    node.setAttribute('aria-label', agent.agent);
    node.appendChild(element('h2', agent.agent, { title: '' }));
    node.appendChild(element('p', agent.summary, { summary: '' }));
    agent.months.forEach(function (month) { node.appendChild(monthPart(month, headers)); });
    return node;
  }

  function isObject(value) {
    return value !== null && typeof value === 'object';
  }

  /** Only a view message is rendered; anything else is ignored. */
  function isView(data) {
    return isObject(data) && data.type === 'view' && isArchiveView(data.view);
  }

  function isArchiveView(view) {
    return isObject(view) && Array.isArray(view.agents) && Array.isArray(view.lines) && Array.isArray(view.notes);
  }

  function render(view) {
    const agents = view.agents.map(function (agent) { return agentSection(agent, view.headers); });
    root.replaceChildren(header(view), summary(view), ...agents);
    vscode.postMessage({ type: 'rendered', blocks: agents.length });
  }

  window.addEventListener('message', function (event) {
    if (isView(event.data)) {
      render(event.data.view);
    }
  });

  vscode.postMessage({ type: 'ready' });
})();
