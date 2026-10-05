// The WSL Care Logs page's script (plan §7.4, §15j M7 / M8). Loaded from media/ by the static shell of
// src/panel/panelHtml.ts under a nonce-only CSP. It receives ONE kind of message — { type: 'view', view } built by the
// host's view model (src/logsPage/logsViewModel.ts) — and builds the DOM from it with createElement and textContent ONLY:
// no innerHTML, no markup strings, nothing parsed, and NO ARITHMETIC — every figure arrives spelt; the page lays it out.
// src/test/logsPage/logsPage.test.ts runs this file in a strict DOM that throws on every HTML sink.
//
// It sends the host only its closed set (src/logsPage/logsMessages.ts): ready, rendered, refresh, a period's NAME
// (today, yesterday, thisRun), the date picker's day or two days as typed, and expand / collapse with a run's INDEX —
// never a run id, never an instant, never a flag.
(function () {
  'use strict';

  const vscode = acquireVsCodeApi();
  const root = document.getElementById('logs');

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
  function button(label, data, disabled, message) {
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

  // The notice is the page's ONE live region and the SAME element for the page's whole life (as in panel.js): only its
  // text changes, so a screen reader announces the new sentence and nothing else.
  const notice = element('p', '', { notice: '', level: 'none' });
  notice.setAttribute('aria-live', 'polite');

  function updateNotice(view) {
    if (notice.textContent !== view.notice) {
      notice.textContent = view.notice;
    }
    notice.setAttribute('data-level', view.noticeLevel);
  }

  function periodButton(model) {
    const node = button(model.label, { period: model.id }, !model.enabled, function () { return { type: model.id }; });
    node.setAttribute('aria-pressed', model.pressed ? 'true' : 'false');
    if (model.reason !== '') {
      node.setAttribute('title', model.reason);
    }
    return node;
  }

  /** A date input bounded by the retention (min / max): the browser's picker offers no day the daemon no longer keeps. */
  function dateInput(name, label, value, picker) {
    const node = element('input', undefined, { picker: name });
    node.setAttribute('type', 'date');
    node.setAttribute('min', picker.min);
    node.setAttribute('max', picker.max);
    node.setAttribute('aria-label', label);
    node.value = value;
    return node;
  }

  function pickerPart(picker) {
    const box = element('div', undefined, { part: 'picker' });
    const day = dateInput('day', 'Day', picker.day, picker);
    const from = dateInput('from', 'From', picker.from, picker);
    const to = dateInput('to', 'To', picker.to, picker);
    const showDay = button('Show day', { show: 'day' }, false, function () { return { type: 'day', day: day.value }; });
    showDay.setAttribute('aria-pressed', picker.dayPressed ? 'true' : 'false');
    const showRange = button('Show range', { show: 'range' }, false, function () { return { type: 'range', from: from.value, to: to.value }; });
    showRange.setAttribute('aria-pressed', picker.rangePressed ? 'true' : 'false');
    [day, showDay, from, to, showRange].forEach(function (node) { box.appendChild(node); });
    return box;
  }

  function header(view) {
    const top = element('header', undefined, { part: 'header' });
    top.appendChild(element('h1', view.heading, { heading: '' }));
    updateNotice(view);
    top.appendChild(notice);
    const bar = element('div', undefined, { part: 'periods' });
    view.periods.forEach(function (model) { bar.appendChild(periodButton(model)); });
    bar.appendChild(button('Refresh', { action: 'refresh' }, false, function () { return { type: 'refresh' }; }));
    top.appendChild(bar);
    top.appendChild(pickerPart(view.picker));
    top.appendChild(element('p', view.periodLabel, { 'period-label': '' }));
    return top;
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

  function cellsRow(cells, data) {
    const line = element('tr', undefined, data);
    cells.forEach(function (value) { line.appendChild(element('td', value, { cell: '' })); });
    return line;
  }

  function tablePart(model) {
    const box = element('div', undefined, { 'table-of': model.id });
    box.appendChild(element('h3', model.title, { title: '' }));
    const table = element('table', undefined, { table: model.id });
    table.appendChild(headerRow(model.headers));
    const body = element('tbody');
    model.rows.forEach(function (cells) { body.appendChild(cellsRow(cells, { row: '' })); });
    table.appendChild(body);
    box.appendChild(table);
    return box;
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

  function blockSection(model) {
    const node = element('section', undefined, { block: model.id, state: model.state });
    node.setAttribute('aria-label', model.title);
    node.appendChild(element('h2', model.title, { title: '' }));
    model.lines.forEach(function (line) { node.appendChild(linePart(line)); });
    model.tables.forEach(function (table) { node.appendChild(tablePart(table)); });
    node.appendChild(notesPart(model.notes));
    return node;
  }

  function expandButton(row) {
    const label = row.expanded ? 'Hide objects' : 'Show objects';
    const node = button(label, { expand: String(row.index) }, false, function () {
      return { type: row.expanded ? 'collapse' : 'expand', index: row.index };
    });
    node.setAttribute('aria-expanded', row.expanded ? 'true' : 'false');
    return node;
  }

  /** A run's line, and — once expanded — a second line holding what runs show answered. */
  function runLines(row, width) {
    const line = cellsRow(row.cells, { run: String(row.index) });
    const action = element('td');
    action.appendChild(expandButton(row));
    line.appendChild(action);
    if (!row.expanded) {
      return [line];
    }
    const holder = element('tr', undefined, { 'detail-for': String(row.index) });
    const cell = element('td');
    cell.setAttribute('colspan', String(width));
    row.detail.forEach(function (model) { cell.appendChild(blockSection(model)); });
    holder.appendChild(cell);
    return [line, holder];
  }

  function runListSection(list) {
    const node = element('section', undefined, { block: 'runList', state: list.state });
    node.setAttribute('aria-label', 'Run list');
    node.appendChild(element('h2', 'Run list', { title: '' }));
    const table = element('table', undefined, { table: 'runList' });
    table.appendChild(headerRow(list.headers.concat(['Objects'])));
    const body = element('tbody');
    list.rows.forEach(function (row) {
      runLines(row, list.headers.length + 1).forEach(function (line) { body.appendChild(line); });
    });
    table.appendChild(body);
    node.appendChild(table);
    node.appendChild(notesPart(list.notes));
    return node;
  }

  function isObject(value) {
    return value !== null && typeof value === 'object';
  }

  /** Only a view message is rendered; anything else is ignored. */
  function isView(data) {
    return isObject(data) && data.type === 'view' && isLogsView(data.view);
  }

  function isLogsView(view) {
    return isObject(view) && Array.isArray(view.blocks) && isObject(view.runList);
  }

  function render(view) {
    const sections = view.blocks.map(blockSection);
    if (view.runList.shown) {
      sections.push(runListSection(view.runList));
    }
    root.replaceChildren(header(view), ...sections);
    vscode.postMessage({ type: 'rendered', blocks: sections.length });
  }

  window.addEventListener('message', function (event) {
    if (isView(event.data)) {
      render(event.data.view);
    }
  });

  vscode.postMessage({ type: 'ready' });
})();
