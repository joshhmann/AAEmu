// Operational work records only: canonical scope and evidence remain in project docs.
(() => {
    'use strict';
    if (document.getElementById('tabBtn-work')) return;

    let board = null;
    let busy = false;
    let loading = null;
    let selectedFeature = '';
    let previewRequest = 0;
    let briefRequest = 0;
    const editors = new Map();
    const byId = id => document.getElementById(id);
    const copy = value => JSON.parse(JSON.stringify(value));
    const lines = value => (value || '').split('\n').map(s => s.trim()).filter(Boolean);
    const array = value => Array.isArray(value) ? value : [];
    const text = value => typeof value === 'string' ? value : JSON.stringify(value ?? 'UNKNOWN', null, 2);

    function el(tag, value, className, id) {
        const node = document.createElement(tag);
        if (value !== undefined) node.textContent = text(value);
        if (className) node.className = className;
        if (id) node.id = id;
        return node;
    }
    function button(label, handler, id, primary = false) {
        const node = el('button', label, primary ? 'btn btn-primary' : 'btn', id);
        node.type = 'button';
        node.addEventListener('click', handler);
        return node;
    }
    function actions(...nodes) {
        const row = el('div');
        row.style.cssText = 'display:flex;gap:8px;flex-wrap:wrap;margin:12px 0';
        row.append(...nodes);
        return row;
    }
    function paragraph(parent, value, className = 'task-desc') {
        parent.append(el('p', value, className));
    }
    function heading(parent, value) { parent.append(el('h3', value, 'section-header')); }
    function bullets(parent, values, empty = 'None recorded.') {
        if (!array(values).length) { paragraph(parent, empty); return; }
        const list = el('ul');
        list.style.cssText = 'padding-left:22px;white-space:pre-wrap;overflow-wrap:anywhere';
        for (const value of values) list.append(el('li', value));
        parent.append(list);
    }
    function details(parent, title, value) {
        const node = el('details');
        node.append(el('summary', title));
        const pre = el('pre', value);
        pre.style.cssText = 'white-space:pre-wrap;overflow-wrap:anywhere;font-size:12px;margin:12px 0';
        node.append(pre);
        parent.append(node);
        return node;
    }
    function panel(title) {
        const node = el('section', undefined, 'info-card');
        node.style.cssText = 'margin-bottom:16px;overflow-wrap:anywhere';
        node.append(el('h3', title, 'info-card-title'));
        return node;
    }
    function setError(message) {
        const node = byId('workError');
        node.textContent = message || '';
        node.hidden = !message;
    }
    function notice(message) { byId('workNotice').textContent = message; }

    const nav = document.querySelector('.top-nav-inner');
    const legacyPane = byId('tabPane-tasks');
    if (!nav || !legacyPane?.parentNode) return;
    const tab = button('Delivery & Sessions', () => window.switchMainTab('work'), 'tabBtn-work');
    tab.className = 'nav-tab-btn';
    nav.append(tab);
    const pane = el('div', undefined, 'tab-pane', 'tabPane-work');
    legacyPane.parentNode.append(pane);
    pane.append(el('style', '#tabPane-work [hidden] { display: none !important; }'));
    const intro = panel('Delivery & Sessions');
    paragraph(intro, 'Bounded delivery cards and saved work records. Starting a record does not start OMP, launch an agent, run a command, or promote evidence. Vibe OFF; no Muse.');
    paragraph(intro, 'PROJECT-CONTROL / ROADMAP own scope. CURRENT / DECISIONS / EVIDENCE own the current objective, decisions and proof. Historical references retain their original revision, scope and limitations.');
    const filter = el('select', undefined, 'form-control', 'workFeatureFilter');
    filter.setAttribute('aria-label', 'Filter work by feature');
    filter.style.maxWidth = '430px';
    filter.append(new Option('All features', ''));
    filter.addEventListener('change', () => { selectedFeature = filter.value; renderLists(); });
    intro.append(actions(
        button('Refresh board', () => refresh(), 'workRefresh'),
        filter,
        button('New card', () => requireBoard(() => openCard()), 'workNewCard'),
        button('New feature', () => requireBoard(() => openFeature()), 'workNewFeature')
    ));
    const errorBox = el('div', undefined, 'eval-verdict-fail', 'workError');
    errorBox.setAttribute('role', 'alert');
    errorBox.style.cssText = 'white-space:pre-wrap;margin:12px 0';
    errorBox.hidden = true;
    intro.append(errorBox, el('p', 'Server-backed board not yet loaded.', 'task-desc', 'workNotice'));
    intro.append(el('div', undefined, undefined, 'workContext'));
    pane.append(intro);
    for (const id of ['workFeatureEditor', 'workCardEditor', 'workSessionEditor']) {
        const host = el('section', undefined, 'info-card', id);
        host.style.marginBottom = '16px';
        host.hidden = true;
        pane.append(host);
    }
    const lists = el('div');
    lists.style.cssText = 'display:grid;grid-template-columns:repeat(auto-fit,minmax(min(100%,340px),1fr));gap:16px';
    for (const [id, title] of [['workFeatures', 'Feature bibliography'], ['workCards', 'Delivery cards'], ['workSessions', 'Work records']]) {
        const host = el('section', undefined, undefined, id);
        host.setAttribute('aria-label', title);
        lists.append(host);
    }
    pane.append(lists);
    const preview = panel('Registered source preview');
    preview.id = 'workReferenceShelf';
    preview.append(el('p', 'Select a registered reference to see a bounded source preview. External HTTPS references are links only; the server does not fetch them.', 'task-desc', 'workReferenceMeta'));
    const previewText = el('textarea', undefined, 'form-control', 'workReferencePreview');
    previewText.readOnly = true;
    previewText.rows = 14;
    previewText.setAttribute('aria-label', 'Bounded registered source content');
    preview.append(previewText);
    pane.append(preview);
    const briefPanel = panel('Scope-locked session brief');
    briefPanel.append(el('p', 'Open a work record brief to copy or export its saved contract, checkpoint, references and canonical source pointers. This is pasteable context, not an executable command.', 'task-desc', 'workBriefMeta'));
    const briefText = el('textarea', undefined, 'form-control', 'workBrief');
    briefText.readOnly = true;
    briefText.rows = 18;
    briefText.setAttribute('aria-label', 'Scope-locked pasteable work brief');
    briefPanel.append(briefText, actions(
        button('Copy brief', copyBrief, 'workCopyBrief'),
        button('Export brief', exportBrief, 'workExportBrief')
    ));
    pane.append(briefPanel);

    // Register before legacy init restores a saved tab; keep every existing pane hook.
    const previousSwitch = window.switchMainTab;
    window.switchMainTab = function (name) {
        try {
            if (typeof previousSwitch === 'function') previousSwitch.apply(this, arguments);
        } finally {
            if (name === 'work' && !board) refresh();
        }
    };
    // An event listener preserves the legacy window.onload = init assignment.
    const onLoad = () => {
        if (pane.classList.contains('active')) refresh();
    };
    if (document.readyState === 'complete') onLoad();
    else window.addEventListener('load', onLoad, {once: true});

    function requireBoard(callback) {
        if (board) callback();
        else setError('Load the server board with Refresh before editing. There is no localStorage fallback.');
    }
    async function request(path, options) {
        const response = await fetch(path, {credentials: 'same-origin', cache: 'no-store', ...options});
        let payload;
        try { payload = await response.json(); }
        catch (_) { throw new Error('Work endpoint returned non-JSON data (HTTP ' + response.status + '). Run the dashboard server with work routes enabled.'); }
        if (!response.ok) {
            const error = new Error('HTTP ' + response.status + ': ' + text(payload.message || payload.error || payload));
            error.status = response.status;
            throw error;
        }
        return payload;
    }
    function applySnapshot(payload) {
        if (!Number.isInteger(payload.revision) || !Array.isArray(payload.features) || !Array.isArray(payload.cards) || !Array.isArray(payload.sessions)) {
            throw new Error('Invalid server board snapshot; no local data substituted.');
        }
        // POST responses need not repeat the server-lifetime write token.
        board = {...payload, write_token: payload.write_token || board?.write_token};
        renderBoard();
    }
    async function refresh(keepError = false) {
        if (loading) return loading;
        loading = (async () => {
            if (!keepError) setError('');
            notice('Loading server board…');
            try {
                applySnapshot(await request('/api/work'));
                notice('Loaded server revision ' + board.revision + '. Draft editors are preserved; review before rebasing stale drafts.');
                return true;
            } catch (error) {
                setError(error.message);
                notice('Board unavailable. No work data has been saved locally.');
                return false;
            } finally { loading = null; }
        })();
        return loading;
    }
    async function mutate(fields, draft, after) {
        if (busy || loading) { setError('A board request is in progress. Wait before saving.'); return; }
        if (!board?.write_token) { setError('No server write token. Refresh the board before saving.'); return; }
        busy = true;
        setError('');
        notice('Saving to server…');
        try {
            const payload = await request('/api/work', {
                method: 'POST',
                headers: {'Content-Type': 'application/json', 'X-Work-Token': board.write_token},
                body: JSON.stringify({revision: draft.revision, ...fields})
            });
            applySnapshot(payload);
            notice(board.storage_warning ? 'Committed server revision ' + board.revision + ' with storage uncertainty; inspect the warning and saved state before continuing.' : 'Saved server revision ' + board.revision + '. Operational metadata only; canonical evidence unchanged.');
            if (after) after();
        } catch (error) {
            const message = error.message + '\nNothing was reported as saved. Your draft remains available.';
            setError(message);
            notice('Save rejected. Correct the draft or review the refreshed board before retrying.');
            if (error.status === 409 || error.status === 403) {
                await refresh(true);
                setError(message + '\nLatest board loaded if available. Review differences and explicitly rebase the draft; stale context is never acknowledged automatically.');
            }
        } finally { busy = false; }
    }
    function renderBoard() {
        const oldSelection = selectedFeature;
        filter.replaceChildren(new Option('All features', ''));
        for (const feature of board.features) filter.append(new Option(feature.title + ' [' + feature.id + ']', feature.id));
        selectedFeature = board.features.some(f => f.id === oldSelection) ? oldSelection : '';
        filter.value = selectedFeature;
        const context = byId('workContext');
        context.replaceChildren();
        paragraph(context, 'Revision ' + board.revision + ' | branch ' + (board.context?.branch || 'UNKNOWN') + ' | source ' + (board.context?.source_sha || 'UNKNOWN') + ' | WIP limits ' + text(board.limits));
        if (board.storage_warning) paragraph(context, board.storage_warning, 'eval-verdict-caveat');
        paragraph(context, board.authority || 'UI assertions do not verify outcomes.');
        details(context, 'Current canonical context and dirty-state baseline', board.context);
        renderLists();
        for (const draft of editors.values()) updateDraftRevision(draft);
    }
    function renderLists() {
        if (!board) return;
        const features = board.features.filter(f => !selectedFeature || f.id === selectedFeature);
        const cards = board.cards.filter(c => !selectedFeature || c.feature_id === selectedFeature);
        const cardIds = new Set(cards.map(c => c.id));
        const sessions = board.sessions.filter(s => !selectedFeature || cardIds.has(s.card_id));
        const featureHost = byId('workFeatures');
        const cardHost = byId('workCards');
        const sessionHost = byId('workSessions');
        featureHost.replaceChildren(el('h2', 'Features (' + features.length + ' / ' + board.features.length + ')', 'section-header'));
        cardHost.replaceChildren(el('h2', 'Cards (' + cards.length + ')', 'section-header'));
        sessionHost.replaceChildren(el('h2', 'Work records (' + sessions.length + ')', 'section-header'));
        if (!features.length) paragraph(featureHost, 'No features match this filter.');
        if (!cards.length) paragraph(cardHost, 'No cards match this filter.');
        if (!sessions.length) paragraph(sessionHost, 'No work records. UI records are separate from actual OMP session identifiers.');
        for (const feature of features) {
            const node = panel(feature.title);
            paragraph(node, feature.track + ' | ' + feature.id);
            paragraph(node, feature.summary);
            heading(node, 'Unresolved questions');
            bullets(node, feature.gaps, 'No unresolved questions recorded; this is not an acceptance claim.');
            node.append(actions(button('Edit feature and references', () => openFeature(feature))));
            heading(node, 'Registered references (' + array(feature.references).length + ')');
            for (const ref of array(feature.references)) {
                const item = el('details');
                item.append(el('summary', ref.label + ' [' + ref.kind + ' / ' + ref.source + ']'));
                paragraph(item, 'ID: ' + ref.id + ' | revision: ' + (ref.revision || 'UNKNOWN') + ' | layer: ' + (ref.layer || 'UNKNOWN') + ' | confidence: ' + (ref.confidence || 'UNKNOWN'));
                paragraph(item, 'Source: ' + (ref.path || ref.url || 'UNKNOWN'));
                paragraph(item, 'Limits: ' + text(ref.limits || 'UNKNOWN'));
                if (ref.anchor || ref.line_start || ref.line_end) paragraph(item, 'Registered historical selection hint: ' + text({anchor: ref.anchor, line_start: ref.line_start, line_end: ref.line_end}) + '. Actual preview bounds are returned by the server.');
                item.append(actions(button('Preview registered source', () => showReference(feature.id, ref.id))));
                const href = httpsLink(ref.url);
                if (href) {
                    const link = el('a', 'Open external HTTPS reference');
                    link.href = href;
                    link.target = '_blank';
                    link.rel = 'noopener noreferrer';
                    item.append(link);
                }
                node.append(item);
            }
            featureHost.append(node);
        }
        for (const card of cards) {
            const node = panel(card.title);
            paragraph(node, card.id + ' | ' + card.mode + ' | ' + card.slot + ' | ' + card.status + ' | ' + card.priority);
            paragraph(node, 'Parent: ' + card.parent + '\nOutcome: ' + card.outcome);
            paragraph(node, 'Owner: ' + (card.owner || 'UNASSIGNED') + ' | verifier: ' + (card.verifier || 'UNASSIGNED') + ' | evidence layer: ' + card.evidence_layer);
            if (card.readiness) {
                paragraph(node, 'Readiness: ' + (card.readiness.ready ? 'ready for work' : 'not ready'));
                bullets(node, card.readiness.reasons, 'No readiness blockers reported by backend.');
            } else paragraph(node, 'Readiness is checked by the server on save/start; status alone is not proof.');
            if (card.blocker) paragraph(node, 'Blocker: ' + card.blocker, 'eval-verdict-caveat');
            heading(node, 'Acceptance criteria');
            bullets(node, card.acceptance, 'No acceptance criteria recorded.');
            heading(node, 'Dependencies');
            bullets(node, array(card.dependencies).map(id => {
                const dependency = board.cards.find(c => c.id === id);
                return id + ' — ' + (dependency ? dependency.title + ' [' + dependency.status + ']' : 'MISSING');
            }));
            paragraph(node, 'Next action: ' + (card.next_action || 'UNKNOWN'));
            details(node, 'Scope, non-goals, resources, baseline and evidence', card);
            node.append(actions(
                button('Edit card', () => openCard(card)),
                button('Start / continue / fresh record', () => openStart(card))
            ));
            cardHost.append(node);
        }
        for (const session of sessions) {
            const node = panel(session.name || session.id);
            paragraph(node, 'Work record ' + session.id + ' | card ' + session.card_id + ' | ' + session.status + ' | ' + session.strategy);
            paragraph(node, 'Not an automatically launched OMP session. Any real handle in the name is operator supplied.');
            if (session.reason) paragraph(node, 'Fresh reason: ' + session.reason);
            renderDrift(node, session.drift);
            paragraph(node, 'Next action: ' + (session.checkpoint?.next_action || 'UNKNOWN'));
            if (session.checkpoint?.blocker) paragraph(node, 'Blocker: ' + session.checkpoint.blocker, 'eval-verdict-caveat');
            details(node, 'Saved checkpoint', session.checkpoint || {});
            details(node, 'Saved contract and context', {contract: session.contract, context: session.context});
            renderHistory(node, session.history);
            const controls = [button(session.status === 'closed' ? 'View record' : 'Checkpoint / pause / close / resume', () => openSession(session)), button('View pasteable brief', () => showBrief(session.id))];
            const card = board.cards.find(c => c.id === session.card_id);
            if (card && session.status !== 'active') controls.push(button('Fresh record for this card', () => openStart(card, 'fresh')));
            node.append(actions(...controls));
            sessionHost.append(node);
        }
    }
    function renderDrift(parent, reasons) {
        if (array(reasons).length) {
            paragraph(parent, 'DRIFT: review source/canonical changes. Contract mismatch requires a fresh record, not silent reuse.', 'eval-verdict-caveat');
            bullets(parent, reasons);
        } else paragraph(parent, 'No drift reported in this server snapshot. This is not fresh verification.');
    }
    function renderHistory(parent, history) {
        const node = el('details');
        node.append(el('summary', 'Saved history (' + array(history).length + ' events)'));
        for (const [index, event] of array(history).entries()) details(node, 'Event ' + (index + 1) + (event.at || event.timestamp ? ' — ' + (event.at || event.timestamp) : ''), event);
        if (!array(history).length) paragraph(node, 'No checkpoint events recorded.');
        parent.append(node);
    }
    function httpsLink(value) {
        if (!value) return null;
        try { const url = new URL(value); return url.protocol === 'https:' && !url.username && !url.password ? url.href : null; }
        catch (_) { return null; }
    }

    function startEditor(id, title) {
        const host = byId(id);
        host.replaceChildren(el('h2', title, 'info-card-title'));
        host.hidden = false;
        const form = el('form');
        const draft = {id, host, form, revision: board.revision, context: copy(board.context), reviewed: null};
        draft.revisionLabel = el('p', undefined, 'task-desc');
        draft.rebase = button('Review current board and rebase draft revision', () => {
            draft.revision = board.revision;
            draft.context = copy(board.context);
            if (draft.reviewed) draft.reviewed.checked = false;
            draft.contextText.textContent = text(draft.context);
            draft.contextView.open = true;
            updateDraftRevision(draft);
            notice('Draft rebased only by your explicit action. Review its values and current context before saving.');
        });
        host.append(draft.revisionLabel, draft.rebase);
        draft.contextView = details(host, 'Frozen editor source / canonical context (review before acknowledging)', draft.context);
        draft.contextText = draft.contextView.querySelector('pre');
        host.append(form);
        editors.set(id, draft);
        updateDraftRevision(draft);
        host.scrollIntoView({block: 'start', behavior: 'smooth'});
        return draft;
    }
    function updateDraftRevision(draft) {
        const stale = draft.revision !== board.revision;
        const contextChanged = JSON.stringify(draft.context) !== JSON.stringify(board.context);
        draft.revisionLabel.textContent = 'Draft revision ' + draft.revision + ' | current server revision ' + board.revision + (stale ? '. Stale: saving will be rejected. Compare the current board and your draft before rebasing.' : '.') + (contextChanged ? ' Source / canonical context changed. Review it and explicitly rebase before acknowledging.' : '');
        draft.rebase.hidden = !stale && !contextChanged;
    }
    function closeEditor(draft) {
        if (editors.get(draft.id) !== draft) return;
        draft.host.hidden = true;
        draft.host.replaceChildren();
        editors.delete(draft.id);
    }
    function field(parent, label, id, value = '', options = {}) {
        const group = el('div', undefined, 'form-group');
        const caption = el('label', label);
        caption.htmlFor = id;
        let input;
        if (options.choices) {
            input = el('select', undefined, 'form-control', id);
            for (const choice of options.choices) {
                const [name, val] = Array.isArray(choice) ? choice : [choice, choice];
                input.append(new Option(name, val));
            }
        } else {
            input = el(options.multiline ? 'textarea' : 'input', undefined, 'form-control', id);
            if (options.multiline) input.rows = options.rows || 3;
            else input.type = options.type || 'text';
            input.maxLength = options.maxLength || (options.multiline ? 16000 : 2000);
        }
        input.value = Array.isArray(value) ? value.join('\n') : value ?? '';
        input.required = !!options.required;
        input.readOnly = !!options.readOnly;
        if (options.pattern) input.pattern = options.pattern;
        if (options.placeholder) input.placeholder = options.placeholder;
        group.append(caption, input);
        if (options.hint) paragraph(group, options.hint);
        parent.append(group);
        return input;
    }
    function listField(parent, label, id, values) {
        return field(parent, label + ' (one per line)', id, array(values), {multiline: true});
    }
    function selection(parent, title, id, choices, selected) {
        const group = el('fieldset', undefined, 'form-group', id);
        group.style.cssText = 'border:1px solid var(--border);padding:12px;border-radius:6px;max-height:260px;overflow:auto';
        group.append(el('legend', title));
        const chosen = new Set(array(selected));
        const inputs = [];
        for (const choice of choices) {
            const label = el('label', undefined, 'checklist-item');
            const input = el('input');
            input.type = 'checkbox';
            input.value = choice.id;
            input.checked = chosen.has(choice.id);
            label.append(input, el('span', choice.label));
            group.append(label);
            inputs.push(input);
        }
        if (!choices.length) paragraph(group, 'None available. Register feature references or prerequisite cards first.');
        const missing = [...chosen].filter(value => !choices.some(choice => choice.id === value));
        if (missing.length) paragraph(group, 'Unavailable prior selections: ' + missing.join(', ') + '. Select valid replacements before saving.', 'eval-verdict-caveat');
        parent.append(group);
        return () => {
            const checked = new Set(inputs.filter(input => input.checked).map(input => input.value));
            // Preserve the saved contract order when the selection did not change.
            return [...chosen].filter(id => checked.has(id))
                .concat([...checked].filter(id => !chosen.has(id)));
        };
    }
    function submitControls(draft, label, submit) {
        const save = el('button', label, 'btn btn-primary');
        save.type = 'submit';
        draft.form.append(actions(save, button('Cancel editor', () => closeEditor(draft))));
        draft.form.addEventListener('submit', event => { event.preventDefault(); submit(); });
    }

    function openCard(existing) {
        requireBoard(() => {
            const card = existing ? copy(existing) : {
                id: 'card-' + Date.now().toString(36), feature_id: selectedFeature || board.features[0]?.id || '',
                mode: 'Implement', slot: 'primary', status: 'Backlog', priority: 'Normal',
                evidence_layer: 'control', baseline_sha: board.context?.source_sha || ''
            };
            const draft = startEditor('workCardEditor', existing ? 'Edit bounded card: ' + card.id : 'Create bounded delivery card');
            paragraph(draft.form, 'Ready and Accepted gates are enforced by the server. Active-session contracts cannot be changed. Acceptance notes/evidence are operator reports, not canonical promotion.');
            const fields = {};
            fields.id = field(draft.form, 'Stable card ID', 'workCardId', card.id, {required: true, readOnly: !!existing, pattern: '[a-z0-9]+(?:-[a-z0-9]+)*'});
            fields.feature_id = field(draft.form, 'Feature', 'workCardFeature', card.feature_id, {required: true, choices: board.features.map(f => [f.title + ' [' + f.id + ']', f.id])});
            const textFields = [['title', 'Title'], ['parent', 'Parent capability / milestone'], ['outcome', 'Observable outcome'], ['owner', 'Named implementer'], ['verifier', 'Named verifier'], ['baseline_sha', 'Exact baseline source SHA'], ['next_action', 'Concrete next action'], ['blocker', 'Blocker (required for Blocked)'], ['acceptance_note', 'Actual acceptance scenario and limitations (required for Accepted)']];
            for (const [key, label] of textFields) fields[key] = field(draft.form, label, 'workCard' + camel(key), card[key], {multiline: ['outcome', 'next_action', 'blocker', 'acceptance_note'].includes(key), required: key === 'title'});
            for (const [key, label, choices] of [
                ['mode', 'Mode', ['Research', 'Implement', 'Validate', 'Review']],
                ['slot', 'WIP slot', ['primary', 'secondary']],
                ['status', 'Status', ['Backlog', 'Needs evidence', 'Ready', 'Implementing', 'Verifying', 'Accepted', 'Blocked']],
                ['priority', 'Priority', ['Critical', 'High', 'Normal', 'Low']],
                ['evidence_layer', 'Evidence layer', ['control', 'unit', 'rig', 'live', 'human']]
            ]) fields[key] = field(draft.form, label, 'workCard' + camel(key), card[key], {choices});
            for (const [key, label] of [['scope', 'Owned repo-relative files / directories'], ['resources', 'Exclusive resources'], ['non_goals', 'Non-goals'], ['acceptance', 'Observable acceptance criteria'], ['unknowns', 'Unknowns'], ['evidence_refs', 'Reported ledger / artifact evidence citations']]) fields[key] = listField(draft.form, label, 'workCard' + camel(key), card[key]);
            const referenceHost = el('div');
            draft.form.append(referenceHost);
            let getReferences;
            const populateReferences = (selected) => {
                referenceHost.replaceChildren();
                const feature = board.features.find(f => f.id === fields.feature_id.value);
                getReferences = selection(referenceHost, 'Relevant registered feature references', 'workCardReferences', array(feature?.references).map(r => ({id: r.id, label: r.label + ' [' + r.kind + ' / ' + r.revision + ']'})), selected);
            };
            populateReferences(card.reference_ids);
            fields.feature_id.addEventListener('change', () => populateReferences([]));
            const getDependencies = selection(draft.form, 'Dependencies (must be Accepted for Ready / start / acceptance)', 'workCardDependencies', board.cards.filter(c => c.id !== card.id).map(c => ({id: c.id, label: c.title + ' [' + c.id + ' / ' + c.status + ']'})), card.dependencies);
            submitControls(draft, 'Save card', () => {
                const saved = {...card};
                delete saved.readiness;
                for (const [key, input] of Object.entries(fields)) saved[key] = ['scope', 'resources', 'non_goals', 'acceptance', 'unknowns', 'evidence_refs'].includes(key) ? lines(input.value) : input.value.trim();
                saved.reference_ids = getReferences();
                saved.dependencies = getDependencies();
                mutate({action: 'card_save', card: saved}, draft, () => closeEditor(draft));
            });
        });
    }
    function camel(value) { return value.split('_').map(part => part[0].toUpperCase() + part.slice(1)).join(''); }

    function openFeature(existing) {
        requireBoard(() => {
            const feature = existing ? copy(existing) : {id: 'feature-' + Date.now().toString(36), references: []};
            const draft = startEditor('workFeatureEditor', existing ? 'Edit feature and bibliography: ' + feature.id : 'Create feature and bibliography');
            paragraph(draft.form, 'Register only relevant sources. References retain historical scope/revision and limits. Source previews are server-allowlisted and bounded; registering a path does not grant arbitrary file access.');
            const fields = {};
            for (const [key, label] of [['id', 'Stable feature ID'], ['title', 'Feature title'], ['track', 'Portfolio track'], ['summary', 'Existing implementation summary']]) fields[key] = field(draft.form, label, 'workFeature' + camel(key), feature[key], {required: ['id', 'title', 'track'].includes(key), readOnly: key === 'id' && !!existing, multiline: key === 'summary', ...(key === 'id' ? {pattern: '[a-z0-9]+(?:-[a-z0-9]+)*'} : {})});
            fields.gaps = listField(draft.form, 'Bounded unresolved questions', 'workFeatureGaps', feature.gaps);
            heading(draft.form, 'References');
            const refsHost = el('div', undefined, undefined, 'workFeatureReferences');
            draft.form.append(refsHost);
            const references = [];
            let refSequence = 0;
            const addReference = (ref = {}) => {
                const record = {original: copy(ref), fields: {}, node: el('fieldset', undefined, 'form-group')};
                const prefix = 'workRef' + (++refSequence);
                record.node.style.cssText = 'border:1px solid var(--border);padding:12px;border-radius:6px';
                record.node.append(el('legend', 'Registered reference ' + refSequence));
                for (const [key, label] of [['id', 'Stable reference ID'], ['label', 'Reference label'], ['path', 'Repo / research relative path'], ['url', 'External HTTPS URL'], ['revision', 'Original source revision (UNKNOWN permitted)'], ['layer', 'Evidence / research layer'], ['confidence', 'Confidence'], ['limits', 'Historical scope, limits and provenance'], ['anchor', 'Optional Markdown section anchor (historical selection hint)'], ['line_start', 'Optional first line (historical selection hint)'], ['line_end', 'Optional last line (historical selection hint)']]) {
                    record.fields[key] = field(record.node, label, prefix + camel(key), ref[key] ?? (key === 'id' ? 'ref-' + Date.now().toString(36) + '-' + refSequence : ['revision', 'layer', 'confidence', 'limits'].includes(key) ? 'UNKNOWN' : ''), {required: ['id', 'label', 'revision', 'layer', 'confidence', 'limits'].includes(key), readOnly: key === 'id' && !!ref.id, multiline: key === 'limits', type: key.startsWith('line_') ? 'number' : 'text', ...(key === 'id' ? {pattern: '[a-z0-9]+(?:-[a-z0-9]+)*'} : {})});
                    if (key.startsWith('line_')) { record.fields[key].min = '1'; record.fields[key].step = '1'; }
                }
                record.fields.kind = field(record.node, 'Kind', prefix + 'Kind', ref.kind || 'research', {choices: ['implementation', 'evidence', 'research', 'corpus', 'policy']});
                record.fields.source = field(record.node, 'Source root', prefix + 'Source', ref.source || 'repo', {choices: ['repo', 'research', 'external']});
                record.node.append(actions(button('Remove reference from this draft', () => {
                    record.node.remove();
                    references.splice(references.indexOf(record), 1);
                })));
                refsHost.append(record.node);
                references.push(record);
            };
            for (const ref of array(feature.references)) addReference(ref);
            draft.form.append(actions(button('Add reference', () => addReference(), 'workAddReference')));
            submitControls(draft, 'Save feature and references', () => {
                const saved = {...feature};
                for (const [key, input] of Object.entries(fields)) saved[key] = key === 'gaps' ? lines(input.value) : input.value.trim();
                saved.references = references.map(record => {
                    const ref = {...record.original};
                    for (const [key, input] of Object.entries(record.fields)) {
                        const value = input.value.trim();
                        if (key === 'anchor' || key.startsWith('line_')) {
                            if (!value) delete ref[key];
                            else ref[key] = key.startsWith('line_') ? Number(value) : value;
                        } else ref[key] = value;
                    }
                    return ref;
                });
                mutate({action: 'feature_save', feature: saved}, draft, () => closeEditor(draft));
            });
        });
    }

    function openStart(card, strategy = 'continue') {
        const draft = startEditor('workSessionEditor', 'Start a named work record for ' + card.title);
        paragraph(draft.form, 'Continue preserves compatible prior work. Fresh requires a reason and a paused/closed predecessor. Pause or close an active predecessor first. The server checks baseline, dependencies, owner, WIP, scope and exclusive-resource conflicts. No agent/process is launched.');
        const name = field(draft.form, 'Work record name / optional operator-supplied actual OMP handle', 'workSessionName', '', {required: true});
        const strategyInput = field(draft.form, 'Continuation strategy', 'workSessionStrategy', strategy, {choices: [['Continue compatible work', 'continue'], ['Fresh record with explicit restart reason', 'fresh']]});
        const reason = field(draft.form, 'Fresh restart reason', 'workSessionReason', '', {multiline: true});
        const updateReason = () => { reason.required = strategyInput.value === 'fresh'; };
        strategyInput.addEventListener('change', updateReason);
        updateReason();
        details(draft.form, 'Current bounded card contract', card);
        const prior = board.sessions.filter(s => s.card_id === card.id);
        if (prior.length) {
            heading(draft.form, 'Existing records');
            for (const session of prior) {
                paragraph(draft.form, session.id + ' | ' + session.name + ' | ' + session.status);
                draft.form.append(actions(button('Open existing record', () => openSession(session))));
            }
        }
        submitControls(draft, 'Start work record (no launch)', () => mutate({action: 'session_start', card_id: card.id, name: name.value.trim(), strategy: strategyInput.value, reason: reason.value.trim()}, draft, () => closeEditor(draft)));
    }
    function openSession(session) {
        const draft = startEditor('workSessionEditor', 'Work record: ' + (session.name || session.id));
        paragraph(draft.form, 'Record ' + session.id + ' | card ' + session.card_id + ' | ' + session.status + '. Saved scope/outcome/criteria do not change through checkpoints. Pause/close releases ownership; resuming reacquires it through server gates.');
        renderDrift(draft.form, session.drift);
        details(draft.form, 'Saved scope-locked contract', session.contract);
        details(draft.form, 'Saved source/canonical context', session.context);
        renderHistory(draft.form, session.history);
        draft.form.append(actions(button('View / copy / export saved brief', () => showBrief(session.id))));
        if (session.status === 'closed') {
            paragraph(draft.form, 'Closed records are read-only. Create a fresh record from the card with an explicit reason.');
            const card = board.cards.find(c => c.id === session.card_id);
            if (card) draft.form.append(actions(button('Fresh record', () => openStart(card, 'fresh'))));
            draft.form.append(actions(button('Close editor', () => closeEditor(draft))));
            return;
        }
        const fields = {};
        for (const [key, label] of [['confirmed', 'Confirmed checkpoint facts'], ['unknowns', 'Remaining unknowns'], ['evidence_refs', 'Reported checkpoint evidence citations']]) fields[key] = listField(draft.form, label, 'workCheckpoint' + camel(key), session.checkpoint?.[key]);
        fields.next_action = field(draft.form, 'Concrete next action (required while active / paused)', 'workCheckpointNextAction', session.checkpoint?.next_action, {multiline: true});
        fields.blocker = field(draft.form, 'Named blocker', 'workCheckpointBlocker', session.checkpoint?.blocker, {multiline: true});
        const reviewedLabel = el('label', undefined, 'checklist-item');
        draft.reviewed = el('input', undefined, undefined, 'workReviewedContext');
        draft.reviewed.type = 'checkbox';
        reviewedLabel.append(draft.reviewed, el('span', 'I reviewed the frozen editor source / canonical context above and acknowledge its changes. This cannot override an incompatible card/reference contract.'));
        draft.form.append(reviewedLabel);
        draft.contextView.open = true;
        const contextFields = () => draft.reviewed.checked ? {reviewed_context: copy(draft.context)} : {};
        const checkpoint = () => {
            const value = {};
            for (const [key, input] of Object.entries(fields)) value[key] = ['confirmed', 'unknowns', 'evidence_refs'].includes(key) ? lines(input.value) : input.value.trim();
            return value;
        };
        const resume = () => {
            if (JSON.stringify(checkpoint()) !== JSON.stringify({
                confirmed: array(session.checkpoint?.confirmed),
                unknowns: array(session.checkpoint?.unknowns),
                evidence_refs: array(session.checkpoint?.evidence_refs),
                next_action: session.checkpoint?.next_action || '',
                blocker: session.checkpoint?.blocker || ''
            })) {
                setError('Checkpoint edits are not saved. Save the paused checkpoint, then reopen the record to resume without losing those edits.');
                return;
            }
            mutate({action: 'session_resume', session_id: session.id, ...contextFields()}, draft, () => closeEditor(draft));
        };
        const saveCheckpoint = status => {
            if (status !== 'closed' && !fields.next_action.value.trim()) { setError('Active and paused checkpoints require a concrete next action.'); fields.next_action.focus(); return; }
            mutate({action: 'session_checkpoint', session_id: session.id, checkpoint: checkpoint(), status, ...contextFields()}, draft, () => closeEditor(draft));
        };
        draft.form.append(actions(
            button('Save checkpoint', () => saveCheckpoint(session.status), 'workSaveCheckpoint', true),
            ...(session.status === 'active' ? [button('Checkpoint and pause', () => saveCheckpoint('paused'), 'workPauseSession')] : [button('Resume saved record', resume, 'workResumeSession')]),
            button('Checkpoint and close', () => saveCheckpoint('closed'), 'workCloseSession'),
            button('Cancel editor', () => closeEditor(draft))
        ));
        if (session.status === 'paused') paragraph(draft.form, 'Resume uses the saved checkpoint. Save any edited checkpoint before resuming; then reopen this record.');
        draft.form.addEventListener('submit', event => { event.preventDefault(); saveCheckpoint(session.status); });
    }

    async function showReference(feature, ref) {
        const sequence = ++previewRequest;
        setError('');
        byId('workReferenceMeta').textContent = 'Loading registered reference ' + feature + ' / ' + ref + '…';
        previewText.value = '';
        try {
            const payload = await request('/api/work/reference?feature=' + encodeURIComponent(feature) + '&ref=' + encodeURIComponent(ref));
            if (sequence !== previewRequest) return;
            const metadata = {...payload};
            delete metadata.text;
            byId('workReferenceMeta').textContent = 'Registered source provenance and actual returned bounds:\n' + text(metadata);
            byId('workReferenceMeta').style.whiteSpace = 'pre-wrap';
            previewText.value = payload.text || (payload.unavailable ? 'Source unavailable. See provenance / limits above.' : payload.source === 'external' ? 'External HTTPS link only; not fetched by the server.' : 'No preview text returned.');
            preview.scrollIntoView({block: 'start', behavior: 'smooth'});
        } catch (error) {
            if (sequence !== previewRequest) return;
            setError(error.message);
            byId('workReferenceMeta').textContent = 'Preview failed; no unregistered source fallback.';
        }
    }
    async function showBrief(sessionId) {
        const sequence = ++briefRequest;
        setError('');
        byId('workBriefMeta').textContent = 'Loading saved work-record brief ' + sessionId + '…';
        briefText.value = '';
        delete briefText.dataset.session;
        try {
            const payload = await request('/api/work/brief?session=' + encodeURIComponent(sessionId));
            if (sequence !== briefRequest) return;
            briefText.value = payload.text;
            briefText.dataset.session = payload.session_id || sessionId;
            byId('workBriefMeta').textContent = 'Work record ' + sessionId + '. Scope locked to the saved contract; no OMP launch.\n' + (array(payload.drift).length ? 'DRIFT:\n' + payload.drift.join('\n') : 'No drift reported by server.') + '\nCanonical context:\n' + text(payload.context);
            byId('workBriefMeta').style.whiteSpace = 'pre-wrap';
            briefPanel.scrollIntoView({block: 'start', behavior: 'smooth'});
        } catch (error) {
            if (sequence !== briefRequest) return;
            setError(error.message);
            byId('workBriefMeta').textContent = 'Brief unavailable; no fabricated context substituted.';
        }
    }
    async function copyBrief() {
        if (!briefText.value) { setError('Open a saved work record brief first.'); return; }
        briefText.focus();
        briefText.select();
        try {
            if (!navigator.clipboard?.writeText) throw new Error('Clipboard API unavailable');
            await navigator.clipboard.writeText(briefText.value);
            notice('Copied saved brief. Paste it into a session manually; no process was launched.');
        } catch (_) {
            notice('Clipboard unavailable. The full brief is visible and selected; use your browser copy shortcut, then paste it manually.');
        }
    }
    function exportBrief() {
        if (!briefText.value) { setError('Open a saved work record brief first.'); return; }
        const url = URL.createObjectURL(new Blob([briefText.value], {type: 'text/plain;charset=utf-8'}));
        const link = el('a');
        link.href = url;
        link.download = 'work-brief-' + (briefText.dataset.session || 'record').replace(/[^a-zA-Z0-9_-]/g, '-') + '.txt';
        document.body.append(link);
        link.click();
        link.remove();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
        notice('Exported saved brief as plain text. No command or process was executed.');
    }
})();
