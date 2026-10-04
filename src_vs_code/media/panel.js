// The WSL Care panel's page script (plan §7.2, §15g M7). Loaded from media/ by the static shell of
// src/panel/panelHtml.ts under a nonce-only CSP. It receives ONE kind of message — { type: 'view', view } built by the
// host's view model (src/panel/viewModel.ts) — and builds the DOM from it with createElement and textContent ONLY:
// no innerHTML, no markup strings, nothing parsed. Every value is text; a process name of '</script><img …>' is shown
// as those characters (src/test/panelPage.test.ts runs this file in a strict DOM that throws on every HTML sink).
//
// It sends the host only its closed set (src/panel/messages.ts): ready, rendered, and the id of a pressed button.
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

  function header(view) {
    const top = element('header', undefined, { part: 'header' });
    top.appendChild(element('h1', view.heading, { heading: '' }));
    if (view.notice !== '') {
      top.appendChild(element('p', view.notice, { notice: '', level: view.noticeLevel }));
    }
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

  function isObject(value) {
    return value !== null && typeof value === 'object';
  }

  /** Only a view message is rendered; anything else is ignored. */
  function isView(data) {
    return isObject(data) && data.type === 'view' && isObject(data.view) && Array.isArray(data.view.sections);
  }

  function render(view) {
    root.replaceChildren(header(view), ...view.sections.map(section));
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
