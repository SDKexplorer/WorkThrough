'use strict';
const base = document.body.dataset.base;
const byId = id => document.getElementById(id);
let offset = 0, busy = false, selected = null;
const element = (tag, text, className) => { const node = document.createElement(tag); if (text !== undefined) node.textContent = text; if (className) node.className = className; return node; };
async function request(path, options) {
  const response = await fetch(base + path, { cache: 'no-store', ...options });
  if (!response.ok) { const message = await response.json().catch(() => ({})); throw new Error(message.error || `Request failed (${response.status}).`); }
  return response.status === 204 || response.headers.get('content-length') === '0' ? null : response.json().catch(() => null);
}
async function refresh() {
  if (busy) return;
  busy = true; byId('refresh').disabled = true;
  try {
    const parameters = new URLSearchParams({ offset });
    if (byId('correlation').value) parameters.set('correlationId', byId('correlation').value);
    if (byId('status').value) parameters.set('status', byId('status').value);
    const [jobs, stats] = await Promise.all([request('/api/jobs?' + parameters), request('/api/stats')]);
    byId('notice').textContent = '';
    for (const state of ['Queued', 'Running', 'Succeeded', 'Failed']) byId(state.toLowerCase()).textContent = (stats.counts[state] || 0).toLocaleString();
    byId('rows').replaceChildren();
    for (const job of jobs) {
      const row = element('tr'); const title = element('td'); title.append(element('strong', job.contract.key), element('small', `${job.stepId || job.queue} · v${job.contract.version}`));
      const status = element('td'); status.append(element('span', job.status, 'badge ' + job.status));
      const action = element('td'); const button = element('button', 'Inspect'); button.type = 'button'; button.addEventListener('click', () => inspect(job.id)); action.append(button);
      row.append(title, status, element('td', job.correlationId || '—'), element('td', `${job.attempt} / ${job.maxAttempts}`), element('td', new Date(job.createdAt).toLocaleString()), action); byId('rows').append(row);
    }
    if (!jobs.length) { const row = element('tr'); const cell = element('td', 'No executions match these filters.'); cell.colSpan = 6; row.append(cell); byId('rows').append(row); }
    byId('limits').replaceChildren(...stats.limits.map(limit => element('span', `${limit.jobKey || 'All jobs'}: ${limit.maximum}`, 'limit')));
    if (!stats.limits.length) byId('limits').textContent = 'No shared limits configured. Worker-local limits still apply.';
    byId('previous').disabled = offset === 0; byId('next').disabled = jobs.length < 100; byId('page').textContent = 'Page ' + (offset / 100 + 1);
    byId('updated').textContent = 'Updated ' + new Date().toLocaleTimeString();
  } catch (error) { byId('notice').textContent = error.message; byId('updated').textContent = 'Update failed · displayed data may be stale'; }
  finally { busy = false; byId('refresh').disabled = false; }
}
async function inspect(id) {
  try {
    const data = await request('/api/jobs/' + id); selected = data.job;
    const content = byId('detail-content'); content.replaceChildren();
    const metadata = element('dl');
    for (const [label, value] of [['Job', selected.contract.key], ['Execution ID', selected.id], ['Status', selected.status], ['Correlation', selected.correlationId || '—'], ['Workflow run', selected.workflowRunId || 'Standalone']]) metadata.append(element('dt', label), element('dd', value));
    content.append(metadata);
    if (selected.workflowRunId) { const flow = await request('/api/flows/' + selected.workflowRunId); content.append(element('p', `Flow: ${flow.flowKey} / v${flow.version} · ${flow.status}`)); }
    for (const [title, value] of [['Input', selected.payload], ['Output', selected.output], ['Attempts', data.attempts]]) content.append(element('h3', title), element('pre', JSON.stringify(value, null, 2)));
    if (selected.error) content.append(element('p', selected.error));
    byId('retry').disabled = !!selected.workflowRunId || !['Failed', 'Cancelled'].includes(selected.status);
    byId('cancel').disabled = ['Succeeded', 'Failed', 'Skipped', 'Cancelled'].includes(selected.status);
    if (!byId('details').open) byId('details').showModal();
  } catch (error) { byId('notice').textContent = error.message; }
}
async function act(action) {
  if (!selected) return;
  byId(action).disabled = true;
  try {
    const csrf = await request('/api/csrf');
    await request(`/api/jobs/${selected.id}/${action}`, { method: 'POST', headers: { 'X-CSRF-TOKEN': csrf.token } });
    await inspect(selected.id); await refresh();
  } catch (error) { byId('notice').textContent = error.message; byId('details').close(); }
}
byId('refresh').addEventListener('click', refresh);
byId('filters').addEventListener('submit', event => { event.preventDefault(); offset = 0; refresh(); });
byId('previous').addEventListener('click', () => { offset = Math.max(0, offset - 100); refresh(); });
byId('next').addEventListener('click', () => { offset += 100; refresh(); });
byId('close').addEventListener('click', () => byId('details').close());
byId('retry').addEventListener('click', () => act('retry'));
byId('cancel').addEventListener('click', () => act('cancel'));
setInterval(() => { if (byId('automatic').checked && !document.hidden && !byId('details').open) refresh(); }, 5000);
refresh();
