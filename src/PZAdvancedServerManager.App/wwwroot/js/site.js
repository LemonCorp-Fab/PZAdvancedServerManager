document.documentElement.dataset.pzasmUiReady = 'true';

document.querySelectorAll('[data-mod-filter]').forEach(filter => {
    const select = document.querySelector('[data-mod-select]');
    if (!select) return;
    const options = Array.from(select.options).slice(1).map(option => ({ option, text: option.text.toLocaleLowerCase() }));
    filter.addEventListener('input', () => {
        const query = filter.value.trim().toLocaleLowerCase();
        for (const entry of options) entry.option.hidden = query.length > 0 && !entry.text.includes(query);
        if (select.selectedOptions[0]?.hidden) select.value = '';
    });
});

document.querySelectorAll('[data-mod-import-preview]').forEach(preview => {
    const filter = preview.querySelector('[data-mod-import-filter]');
    const rows = Array.from(preview.querySelectorAll('[data-mod-import-row]'));
    const checkboxes = rows.flatMap(row => Array.from(row.querySelectorAll('input[type="checkbox"][name="selectedEntries"]')));
    const count = preview.querySelector('[data-mod-import-selected]');
    const submit = preview.querySelector('[data-mod-import-submit]');
    const empty = preview.querySelector('[data-mod-import-empty]');
    const normalize = value => value.toLocaleLowerCase().normalize('NFD').replace(/[\u0300-\u036f]/g, '');
    const update = () => {
        const selected = checkboxes.filter(checkbox => checkbox.checked).length;
        const visible = rows.filter(row => !row.hidden).length;
        if (count) count.textContent = String(selected);
        if (submit instanceof HTMLButtonElement) submit.disabled = selected === 0;
        if (empty) empty.hidden = visible > 0;
    };
    filter?.addEventListener('input', () => {
        const query = normalize(filter.value.trim());
        rows.forEach(row => { row.hidden = query.length > 0 && !normalize(row.dataset.modImportSearch || row.textContent).includes(query); });
        update();
    });
    preview.querySelectorAll('[data-mod-import-select]').forEach(button => {
        button.addEventListener('click', () => {
            const shouldSelect = button.dataset.modImportSelect === 'all';
            rows.forEach(row => {
                const checkbox = row.querySelector('input[type="checkbox"][name="selectedEntries"]');
                if (checkbox instanceof HTMLInputElement) checkbox.checked = shouldSelect;
            });
            update();
        });
    });
    checkboxes.forEach(checkbox => checkbox.addEventListener('change', update));
    update();
});

document.querySelectorAll('details.mod-card').forEach(card => {
    const label = card.querySelector('.mod-expand-hint > span');
    const update = () => { if (label) label.textContent = card.open ? 'Masquer les détails' : 'Afficher les détails'; };
    card.addEventListener('toggle', update);
    update();
});

(() => {
    const root = document.documentElement;
    const themeToggle = document.querySelector('[data-theme-toggle]');
    const themeIcon = document.querySelector('[data-theme-icon]');
    const themeLabel = document.querySelector('[data-theme-label]');
    const applyTheme = theme => {
        const selected = theme === 'dark' ? 'dark' : 'light';
        root.dataset.theme = selected;
        if (themeIcon) themeIcon.textContent = selected === 'dark' ? '☾' : '☀';
        if (themeLabel) themeLabel.textContent = selected === 'dark' ? 'Mode sombre' : 'Mode clair';
        themeToggle?.setAttribute('aria-pressed', selected === 'dark' ? 'true' : 'false');
        try { window.localStorage.setItem('pzasm-theme', selected); } catch { }
    };
    themeToggle?.addEventListener('click', () => applyTheme(root.dataset.theme === 'dark' ? 'light' : 'dark'));
    applyTheme(root.dataset.theme);
})();

(() => {
    const frame = document.querySelector('[data-app-frame]');
    const sidebar = document.querySelector('[data-app-sidebar]');
    const toggle = document.querySelector('[data-sidebar-toggle]');
    const scrim = document.querySelector('[data-sidebar-close]');
    if (!frame || !sidebar || !toggle) return;

    const setOpen = open => {
        frame.classList.toggle('is-sidebar-open', open);
        toggle.setAttribute('aria-expanded', open ? 'true' : 'false');
        if (scrim) scrim.hidden = !open;
        document.body.classList.toggle('has-open-sidebar', open);
    };

    toggle.addEventListener('click', () => setOpen(!frame.classList.contains('is-sidebar-open')));
    scrim?.addEventListener('click', () => setOpen(false));
    sidebar.querySelectorAll('a').forEach(link => link.addEventListener('click', () => setOpen(false)));
    document.addEventListener('keydown', event => {
        if (event.key === 'Escape' && frame.classList.contains('is-sidebar-open')) {
            setOpen(false);
            toggle.focus();
        }
    });
    window.matchMedia('(min-width: 921px)').addEventListener('change', event => {
        if (event.matches) setOpen(false);
    });
})();

(() => {
    const accountMenu = document.querySelector('details.account-menu');
    if (!accountMenu) return;
    document.addEventListener('click', event => {
        if (accountMenu.open && event.target instanceof Node && !accountMenu.contains(event.target)) accountMenu.open = false;
    });
    document.addEventListener('keydown', event => {
        if (event.key === 'Escape' && accountMenu.open) accountMenu.open = false;
    });
})();

(() => {
    const root = document.querySelector('[data-server-runtime]');
    if (!root) return;
    const endpoint = root.dataset.runtimeEndpoint;
    if (!endpoint) return;

    const status = root.querySelector('[data-runtime-status]');
    const detail = root.querySelector('[data-runtime-detail]');
    const pid = root.querySelector('[data-runtime-pid]');
    const source = root.querySelector('[data-runtime-source]');
    const started = root.querySelector('[data-runtime-started]');
    const lastOutput = root.querySelector('[data-runtime-last-output]');
    const output = root.querySelector('[data-runtime-output]');
    const warning = root.querySelector('[data-runtime-rcon-warning]');
    const instanceWarning = root.querySelector('[data-runtime-instance-warning]');
    const helperNote = root.querySelector('[data-runtime-helper-note]');
    const helperCount = root.querySelector('[data-runtime-helper-count]');
    const instances = root.querySelector('[data-runtime-instances]');
    const logSummary = root.querySelector('[data-runtime-log-summary]');
    const logSearch = root.querySelector('[data-runtime-log-search]');
    const logFilters = Array.from(root.querySelectorAll('[data-log-filter]'));
    const rconPort = document.querySelector('[data-runtime-rcon-port]');
    let knownRunning = root.dataset.runtimeRunning === 'true';
    let knownRcon = root.dataset.runtimeRcon === 'true';
    let outputSignature = '';
    let requestActive = false;
    let reloadScheduled = false;
    let activeLogFilter = 'all';
    let logQuery = '';

    const formatDate = (value, includeDate) => {
        if (!value) return '—';
        const date = new Date(value);
        if (Number.isNaN(date.getTime())) return '—';
        return new Intl.DateTimeFormat(document.documentElement.lang || 'fr', includeDate
            ? { dateStyle: 'short', timeStyle: 'medium' }
            : { timeStyle: 'medium' }).format(date);
    };

    const classifyLine = line => {
        if (line.level) return line.level.toLocaleLowerCase();
        const message = line.message || '';
        if (/ERROR:|Exception thrown|BindException/i.test(message) || /^(ERR|STDERR)$/i.test(line.stream || '')) return 'error';
        if (/WARN\s*:|WARNING/i.test(message)) return 'warning';
        if (/\*\*\* SERVER STARTED \*\*\*|SERVER STARTED/i.test(message)) return 'success';
        if (/^SYSTEM$/i.test(line.stream || '')) return 'system';
        if (/^\s|Stack trace:/i.test(message)) return 'stack';
        return 'info';
    };

    const levelLabel = level => ({ error: 'ERR', warning: 'WARN', success: 'OK', system: 'SYS', stack: 'TRACE', info: 'INFO' })[level] || 'INFO';

    const applyLogFilter = () => {
        if (!output) return;
        const rows = Array.from(output.querySelectorAll('.process-log-line'));
        const counts = { all: rows.length, important: 0, error: 0, warning: 0 };
        rows.forEach(row => {
            const level = row.dataset.logLevel || 'info';
            const important = level === 'error' || level === 'warning' || level === 'success';
            if (important) counts.important++;
            if (level === 'error') counts.error++;
            if (level === 'warning') counts.warning++;
            const levelMatches = activeLogFilter === 'all'
                || activeLogFilter === 'important' && important
                || level === activeLogFilter;
            const queryMatches = !logQuery || row.textContent.toLocaleLowerCase().includes(logQuery);
            row.hidden = !(levelMatches && queryMatches);
        });
        Object.entries(counts).forEach(([name, count]) => {
            const target = root.querySelector(`[data-log-count="${name}"]`);
            if (target) target.textContent = String(count);
        });
        if (logSummary) {
            const visible = rows.filter(row => !row.hidden).length;
            logSummary.textContent = visible === rows.length ? `${rows.length} lignes récentes` : `${visible} sur ${rows.length} lignes`;
        }
    };

    const renderInstances = values => {
        if (!instances || !Array.isArray(values)) return;
        instances.replaceChildren();
        values.forEach(instance => {
            const hosted = instance.origin === 'LocalHostedSession';
            const card = document.createElement('article');
            const icon = document.createElement('span');
            const body = document.createElement('div');
            const heading = document.createElement('strong');
            const detailLine = document.createElement('small');
            const executable = document.createElement('code');
            card.className = `runtime-instance ${hosted ? 'hosted' : 'dedicated'}`;
            icon.className = 'runtime-instance-icon';
            icon.textContent = hosted ? 'H' : 'D';
            heading.textContent = instance.label || (hosted ? 'Session hébergée par le jeu' : 'Serveur dédié local');
            detailLine.textContent = `PID ${instance.processId || '—'} · démarré ${formatDate(instance.startedAt, true)}`;
            executable.textContent = instance.executablePath || '';
            body.append(heading, detailLine, executable);
            card.append(icon, body);
            instances.append(card);
        });
        instances.hidden = values.length === 0;
    };

    const renderOutput = lines => {
        if (!output || !Array.isArray(lines)) return;
        const last = lines.at(-1);
        const signature = `${lines.length}:${last?.sequence || 0}:${last?.message || ''}`;
        if (signature === outputSignature) return;
        outputSignature = signature;
        const followsTail = output.scrollHeight - output.scrollTop - output.clientHeight < 45;
        output.replaceChildren();
        if (lines.length === 0) {
            const empty = document.createElement('div');
            const heading = document.createElement('strong');
            const copy = document.createElement('span');
            empty.className = 'process-terminal-empty';
            heading.textContent = 'Aucune sortie disponible';
            copy.textContent = 'Le journal apparaîtra ici dès que le processus écrira une ligne.';
            empty.append(heading, copy);
            output.append(empty);
            return;
        }
        const fragment = document.createDocumentFragment();
        lines.forEach(line => {
            const level = classifyLine(line);
            const row = document.createElement('div');
            const time = document.createElement('time');
            const stream = document.createElement('span');
            const message = document.createElement('code');
            row.className = `process-log-line level-${level}`;
            row.dataset.logLevel = level;
            time.textContent = line.timestamp ? formatDate(line.timestamp, false) : `#${line.sequence || '—'}`;
            stream.textContent = levelLabel(level);
            message.textContent = line.message || '';
            row.append(time, stream, message);
            fragment.append(row);
        });
        output.append(fragment);
        applyLogFilter();
        if (followsTail || output.scrollTop === 0) output.scrollTop = output.scrollHeight;
    };

    const render = data => {
        if (status) {
            status.textContent = data.status || status.textContent;
            status.classList.remove('online', 'degraded', 'starting', 'conflict', 'offline');
            status.classList.add(data.cssClass || 'offline');
        }
        if (detail) detail.textContent = data.detail || '';
        if (pid) pid.textContent = data.processId ? `PID ${data.processId}` : '—';
        if (source) source.textContent = data.source || '—';
        if (started) started.textContent = formatDate(data.startedAt, true);
        if (lastOutput) lastOutput.textContent = formatDate(data.lastOutputAt, false);
        if (warning) {
            warning.hidden = !data.rconBindFailed;
            warning.classList.toggle('is-hidden', !data.rconBindFailed);
        }
        if (instanceWarning) {
            const multiple = Array.isArray(data.instances) && data.instances.length > 1;
            instanceWarning.hidden = !multiple;
            instanceWarning.classList.toggle('is-hidden', !multiple);
        }
        if (helperNote) {
            const count = Number(data.inactiveHostedHelperCount || 0);
            helperNote.hidden = count === 0;
            helperNote.classList.toggle('is-hidden', count === 0);
            if (helperCount) helperCount.textContent = String(count);
        }
        renderInstances(data.instances);
        rconPort?.classList.toggle('port-conflict', Boolean(data.rconBindFailed));
        renderOutput(data.output);

        const runningChanged = Boolean(data.isRunning) !== knownRunning;
        const rconChanged = Boolean(data.isRconAuthenticated) !== knownRcon;
        knownRunning = Boolean(data.isRunning);
        knownRcon = Boolean(data.isRconAuthenticated);
        root.dataset.runtimeRunning = String(knownRunning);
        root.dataset.runtimeRcon = String(knownRcon);
        if ((runningChanged || rconChanged) && !reloadScheduled) {
            reloadScheduled = true;
            window.setTimeout(() => window.location.reload(), 700);
        }
    };

    logFilters.forEach(button => button.addEventListener('click', () => {
        activeLogFilter = button.dataset.logFilter || 'all';
        logFilters.forEach(candidate => candidate.classList.toggle('is-active', candidate === button));
        applyLogFilter();
    }));
    logSearch?.addEventListener('input', () => {
        logQuery = logSearch.value.trim().toLocaleLowerCase();
        applyLogFilter();
    });

    const poll = async () => {
        if (requestActive || document.hidden || reloadScheduled) return;
        requestActive = true;
        try {
            const response = await fetch(endpoint, { credentials: 'same-origin', cache: 'no-store' });
            if (!response.ok) return;
            render(await response.json());
        } catch { }
        finally { requestActive = false; }
    };

    void poll();
    const timer = window.setInterval(() => void poll(), 2500);
    document.addEventListener('visibilitychange', () => { if (!document.hidden) void poll(); });
    window.addEventListener('pagehide', () => window.clearInterval(timer), { once: true });
})();

(() => {
    const tabs = document.querySelector('[data-server-view-tabs]');
    if (!tabs) return;
    const container = tabs.closest('.server-content');
    if (!container) return;
    const buttons = Array.from(tabs.querySelectorAll('[data-server-view-target]'));
    const views = Array.from(container.querySelectorAll('[data-server-view]'));
    const profile = new URLSearchParams(window.location.search).get('name') || 'default';
    const storageKey = `pzasm-server-view:${profile}`;
    const activate = name => {
        if (!buttons.some(button => button.dataset.serverViewTarget === name)) name = 'supervision';
        buttons.forEach(button => {
            const active = button.dataset.serverViewTarget === name;
            button.classList.toggle('is-active', active);
            button.setAttribute('aria-selected', String(active));
        });
        views.forEach(view => {
            const active = view.dataset.serverView === name;
            view.hidden = !active;
            view.classList.toggle('is-active', active);
        });
        try { window.sessionStorage.setItem(storageKey, name); } catch { }
    };
    buttons.forEach(button => button.addEventListener('click', () => activate(button.dataset.serverViewTarget)));
    const parameters = new URLSearchParams(window.location.search);
    const requestedTab = parameters.get('tab');
    const configurationTabs = new Set(['identity', 'access', 'gameplay', 'content', 'all-settings', 'sandbox', 'lua-files', 'raw']);
    let initial = parameters.get('view');
    if (!initial && configurationTabs.has(requestedTab)) initial = 'configuration';
    if (!initial && window.location.hash === '#rcon-console') initial = 'network';
    if (!initial) {
        try { initial = window.sessionStorage.getItem(storageKey); } catch { }
    }
    activate(initial || 'supervision');
})();

document.querySelectorAll('[data-tabs]').forEach(tabSet => {
    const buttons = Array.from(tabSet.querySelectorAll(':scope > [role="tablist"] [data-tab-target]'));
    const panels = Array.from(tabSet.querySelectorAll(':scope > .workspace-form > [data-tab-panel], :scope > [data-tab-panel]'));
    if (buttons.length === 0 || panels.length === 0) return;
    const storageKey = `pzasm-tab:${window.location.pathname}:${tabSet.dataset.tabs}`;

    const activate = name => {
        if (!panels.some(panel => panel.dataset.tabPanel === name)) name = buttons[0].dataset.tabTarget;
        buttons.forEach((button, index) => {
            const active = button.dataset.tabTarget === name;
            button.classList.toggle('is-active', active);
            button.setAttribute('aria-selected', active ? 'true' : 'false');
            button.tabIndex = active ? 0 : -1;
            if (!button.id) button.id = `${tabSet.dataset.tabs}-tab-${index}`;
        });
        panels.forEach(panel => {
            const active = panel.dataset.tabPanel === name;
            panel.hidden = !active;
            const owner = buttons.find(button => button.dataset.tabTarget === panel.dataset.tabPanel);
            if (owner) panel.setAttribute('aria-labelledby', owner.id);
        });
        tabSet.querySelectorAll(':scope > .workspace-form').forEach(form => {
            form.classList.toggle('has-active-panel', Boolean(form.querySelector(`[data-tab-panel="${CSS.escape(name)}"]`)));
        });
        try { window.sessionStorage.setItem(storageKey, name); } catch { }
    };

    buttons.forEach(button => button.addEventListener('click', () => activate(button.dataset.tabTarget)));
    document.querySelectorAll('[data-open-project-tab]').forEach(link => {
        link.addEventListener('click', event => {
            const target = link.dataset.openProjectTab;
            if (!target || !panels.some(panel => panel.dataset.tabPanel === target)) return;
            event.preventDefault();
            activate(target);
            const url = new URL(window.location.href);
            url.searchParams.set('tab', target);
            window.history.replaceState(null, '', url);
            tabSet.scrollIntoView({ behavior: 'smooth', block: 'start' });
        });
    });
    let initial = buttons[0].dataset.tabTarget;
    const requestedTab = new URLSearchParams(window.location.search).get('tab');
    try { initial = requestedTab || window.sessionStorage.getItem(storageKey) || initial; } catch { initial = requestedTab || initial; }
    activate(initial);
});

document.querySelectorAll('[data-catalog-selection]').forEach(form => {
    const checkboxes = Array.from(form.querySelectorAll('input[type="checkbox"]:not(:disabled)'));
    const count = form.querySelector('[data-catalog-count]');
    const submit = form.querySelector('button[type="submit"]');
    const selectedList = form.querySelector('[data-catalog-selected-list]');
    const emptySelection = form.querySelector('[data-catalog-selection-empty]');
    const hiddenInputs = form.querySelector('[data-catalog-hidden-inputs]');
    const storageKey = form.dataset.catalogStorageKey;
    const inputName = form.dataset.catalogInputName;
    const selections = new Map();

    try {
        const stored = JSON.parse(window.sessionStorage.getItem(storageKey) || '[]');
        if (Array.isArray(stored)) {
            stored.filter(item => item && typeof item.value === 'string' && typeof item.title === 'string')
                .forEach(item => selections.set(item.value, item));
        }
    } catch { }

    checkboxes.forEach(checkbox => {
        checkbox.checked = selections.has(checkbox.value);
    });

    form.querySelectorAll('input[type="checkbox"]:disabled').forEach(checkbox => selections.delete(checkbox.value));

    const selectionFrom = checkbox => ({
        value: checkbox.value,
        title: checkbox.dataset.catalogTitle || checkbox.value,
        detail: checkbox.dataset.catalogDetail || ''
    });

    const renderSelectedList = () => {
        if (!selectedList) return;
        selectedList.replaceChildren();
        selections.forEach(item => {
            const row = document.createElement('div');
            row.className = 'catalog-selected-item';
            row.dataset.catalogValue = item.value;
            const copy = document.createElement('span');
            const title = document.createElement('strong');
            const detail = document.createElement('small');
            const remove = document.createElement('button');
            title.textContent = item.title;
            detail.textContent = item.detail;
            copy.append(title, detail);
            remove.type = 'button';
            remove.className = 'catalog-selected-remove';
            remove.dataset.catalogRemove = item.value;
            remove.setAttribute('aria-label', `Retirer ${item.title}`);
            remove.textContent = 'Retirer';
            row.append(copy, remove);
            selectedList.append(row);
        });
    };

    const renderHiddenInputs = () => {
        if (!hiddenInputs || !inputName) return;
        hiddenInputs.replaceChildren();
        const visibleValues = new Set(checkboxes.filter(checkbox => checkbox.checked).map(checkbox => checkbox.value));
        selections.forEach(item => {
            if (visibleValues.has(item.value)) return;
            const input = document.createElement('input');
            input.type = 'hidden';
            input.name = inputName;
            input.value = item.value;
            hiddenInputs.append(input);
        });
    };

    const update = () => {
        checkboxes.forEach(checkbox => {
            if (checkbox.checked) selections.set(checkbox.value, selectionFrom(checkbox));
            else selections.delete(checkbox.value);
        });
        if (count) count.textContent = `${selections.size}`;
        if (submit instanceof HTMLButtonElement) submit.disabled = selections.size === 0;
        if (emptySelection) emptySelection.hidden = selections.size > 0;
        checkboxes.forEach(checkbox => checkbox.closest('.workshop-catalog-card, .local-catalog-card')?.classList.toggle('is-selected', checkbox.checked));
        renderSelectedList();
        renderHiddenInputs();
        try { window.sessionStorage.setItem(storageKey, JSON.stringify(Array.from(selections.values()))); } catch { }
    };
    checkboxes.forEach(checkbox => checkbox.addEventListener('change', update));
    form.querySelector('[data-catalog-select-all]')?.addEventListener('click', () => {
        checkboxes.forEach(checkbox => { checkbox.checked = true; });
        update();
    });
    form.querySelector('[data-catalog-deselect-page]')?.addEventListener('click', () => {
        checkboxes.forEach(checkbox => { checkbox.checked = false; });
        update();
    });
    form.querySelector('[data-catalog-clear-all]')?.addEventListener('click', () => {
        selections.clear();
        checkboxes.forEach(checkbox => { checkbox.checked = false; });
        update();
    });
    selectedList?.addEventListener('click', event => {
        if (!(event.target instanceof Element)) return;
        const button = event.target.closest('[data-catalog-remove]');
        if (!button) return;
        selections.delete(button.dataset.catalogRemove);
        const visible = checkboxes.find(checkbox => checkbox.value === button.dataset.catalogRemove);
        if (visible) visible.checked = false;
        update();
    });
    form.addEventListener('submit', () => {
        renderHiddenInputs();
        if (!form.matches('[data-workshop-progress]')) {
            try { window.sessionStorage.removeItem(storageKey); } catch { }
        }
    });
    update();
});

document.querySelectorAll('[data-map-sorter]').forEach(sorter => {
    const rawInput = sorter.closest('[data-tab-panel]')?.querySelector('[data-map-raw]');
    const manualTemplate = document.querySelector('[data-map-manual-template]');
    if (!rawInput) return;
    const knownRows = new Map(Array.from(sorter.querySelectorAll('[data-map-item]')).map(row => [row.dataset.mapName.toLocaleLowerCase(), row]));
    let draggedRow = null;

    const rows = () => Array.from(sorter.querySelectorAll('[data-map-item]'));
    const update = () => {
        rows().forEach((row, index) => {
            const rank = row.querySelector('[data-map-rank]');
            if (rank) rank.textContent = `${index + 1}`;
        });
        rawInput.value = rows().map(row => row.dataset.mapName).join(';');
    };
    const createManualRow = name => {
        if (!(manualTemplate instanceof HTMLTemplateElement)) return null;
        const row = manualTemplate.content.firstElementChild.cloneNode(true);
        row.dataset.mapName = name;
        row.querySelector('[data-map-manual-name]').textContent = name;
        knownRows.set(name.toLocaleLowerCase(), row);
        bindDrag(row);
        return row;
    };
    const move = (row, direction) => {
        const sibling = direction < 0 ? row.previousElementSibling : row.nextElementSibling;
        if (!sibling) return;
        if (direction < 0) sorter.insertBefore(row, sibling);
        else sorter.insertBefore(sibling, row);
        update();
    };
    const bindDrag = row => {
        row.addEventListener('dragstart', event => {
            draggedRow = row;
            row.classList.add('is-dragging');
            event.dataTransfer.effectAllowed = 'move';
        });
        row.addEventListener('dragend', () => {
            row.classList.remove('is-dragging');
            draggedRow = null;
            update();
        });
        row.addEventListener('dragover', event => {
            if (!draggedRow || draggedRow === row) return;
            event.preventDefault();
            const bounds = row.getBoundingClientRect();
            sorter.insertBefore(draggedRow, event.clientY < bounds.top + bounds.height / 2 ? row : row.nextElementSibling);
        });
    };

    knownRows.forEach(bindDrag);
    sorter.addEventListener('click', event => {
        if (!(event.target instanceof Element)) return;
        const row = event.target.closest('[data-map-item]');
        if (!row) return;
        if (event.target.closest('[data-map-up]')) move(row, -1);
        if (event.target.closest('[data-map-down]')) move(row, 1);
    });
    sorter.closest('[data-tab-panel]')?.querySelector('[data-map-apply-recommended]')?.addEventListener('click', () => {
        Array.from(knownRows.values())
            .filter(row => row.dataset.recommendedRank !== undefined)
            .sort((left, right) => Number(left.dataset.recommendedRank) - Number(right.dataset.recommendedRank))
            .forEach(row => sorter.append(row));
        update();
    });
    sorter.closest('[data-tab-panel]')?.querySelector('[data-map-vanilla-last]')?.addEventListener('click', () => {
        const vanilla = Array.from(knownRows.values()).find(row => row.classList.contains('is-vanilla'));
        if (vanilla) sorter.append(vanilla);
        update();
    });
    rawInput.addEventListener('change', () => {
        const names = rawInput.value.split(';').map(value => value.trim()).filter(Boolean);
        names.forEach(name => {
            const key = name.toLocaleLowerCase();
            const row = knownRows.get(key) || createManualRow(name);
            if (row) sorter.append(row);
        });
        const selected = new Set(names.map(name => name.toLocaleLowerCase()));
        rows().filter(row => !selected.has(row.dataset.mapName.toLocaleLowerCase())).forEach(row => row.remove());
        update();
    });
    update();
});

document.querySelectorAll('[data-workshop-tag-presets]').forEach(container => {
    const inputName = container.dataset.workshopTagInput;
    const form = container.closest('form');
    const input = form?.querySelector(`[name="${inputName}"]`);
    if (!(input instanceof HTMLInputElement)) return;
    const readTags = () => input.value.split(/[;,\n\r]+/).map(value => value.trim()).filter(Boolean);
    const refresh = () => {
        const selected = new Set(readTags().map(value => value.toLocaleLowerCase()));
        container.querySelectorAll('[data-workshop-tag]').forEach(button => {
            button.classList.toggle('active', selected.has(button.dataset.workshopTag.toLocaleLowerCase()));
        });
    };
    container.addEventListener('click', event => {
        const button = event.target.closest('[data-workshop-tag]');
        if (!(button instanceof HTMLButtonElement)) return;
        const tags = readTags();
        const index = tags.findIndex(value => value.localeCompare(button.dataset.workshopTag, undefined, { sensitivity: 'accent' }) === 0);
        if (index >= 0) tags.splice(index, 1);
        else tags.push(button.dataset.workshopTag);
        input.value = tags.join('; ');
        input.dispatchEvent(new Event('input', { bubbles: true }));
        refresh();
    });
    input.addEventListener('input', refresh);
    refresh();
});

(() => {
    const overlay = document.querySelector('[data-confirm-dialog]');
    if (!overlay) return;
    const card = overlay.querySelector('.confirmation-card');
    const title = overlay.querySelector('[data-confirm-dialog-title]');
    const message = overlay.querySelector('[data-confirm-dialog-message]');
    const cancel = overlay.querySelector('[data-confirm-cancel]');
    const secondary = overlay.querySelector('[data-confirm-secondary]');
    const accept = overlay.querySelector('[data-confirm-accept]');
    let pending = null;
    let previousFocus = null;
    const translate = value => typeof window.pzasmTranslate === 'function' ? window.pzasmTranslate(value) : value;

    const close = () => {
        overlay.hidden = true;
        document.body.classList.remove('has-modal');
        card?.classList.remove('is-danger', 'is-publish');
        if (secondary instanceof HTMLButtonElement) secondary.hidden = true;
        if (accept instanceof HTMLButtonElement) accept.hidden = false;
        pending = null;
        if (previousFocus instanceof HTMLElement) previousFocus.focus();
        previousFocus = null;
    };

    const requestConfirmation = (form, submitter) => {
        previousFocus = document.activeElement;
        pending = { form, submitter };
        const toggle = form.dataset.confirmToggle ? form.elements.namedItem(form.dataset.confirmToggle) : null;
        const variant = toggle instanceof HTMLInputElement ? (toggle.checked ? 'Checked' : 'Unchecked') : '';
        const selectVariant = (name, fallback) => variant ? form.dataset[`${name}${variant}`] || fallback : fallback;
        title.textContent = form.dataset.confirmTitle;
        message.textContent = selectVariant('confirmMessage', form.dataset.confirmMessage) || 'Vérifiez attentivement les conséquences avant de continuer.';
        accept.textContent = selectVariant('confirmAction', form.dataset.confirmAction) || 'Confirmer';
        if (secondary instanceof HTMLButtonElement) {
            secondary.textContent = form.dataset.confirmSecondaryAction || '';
            secondary.hidden = !form.dataset.confirmSecondaryAction;
        }
        if (accept instanceof HTMLButtonElement) accept.hidden = false;
        if (variant) {
            form.dataset.loadingDetail = selectVariant('loadingDetail', form.dataset.loadingDetail);
            form.dataset.loadingSteps = selectVariant('loadingSteps', form.dataset.loadingSteps);
        }
        card?.classList.toggle('is-danger', form.dataset.confirmTone === 'danger');
        card?.classList.toggle('is-publish', form.dataset.confirmTone === 'publish');
        overlay.hidden = false;
        document.body.classList.add('has-modal');
        requestAnimationFrame(() => accept?.focus());
    };

    const setHiddenValue = (form, name, value) => {
        let input = form.querySelector(`input[type="hidden"][name="${name}"]`);
        if (!(input instanceof HTMLInputElement)) {
            input = document.createElement('input');
            input.type = 'hidden';
            input.name = name;
            form.append(input);
        }
        input.value = value;
    };

    const submitDependencyChoice = includeDependencies => {
        if (!pending) return;
        const { form, submitter } = pending;
        setHiddenValue(form, 'includeDependencies', includeDependencies ? 'true' : 'false');
        setHiddenValue(form, 'dependencyChoiceAcknowledged', 'true');
        overlay.hidden = true;
        document.body.classList.remove('has-modal');
        pending = null;
        form.dataset.confirmBypass = 'true';
        form.requestSubmit(submitter || undefined);
    };

    const dependencySignature = form => JSON.stringify([...new FormData(form).entries()]
        .filter(([name]) => !['__RequestVerificationToken', 'includeDependencies', 'dependencyChoiceAcknowledged'].includes(name))
        .map(([name, value]) => [name, String(value)]));

    const prepareDependencyConfirmation = async (form, submitter) => {
        const signature = dependencySignature(form);
        if (form.dataset.dependencyPreflightComplete === 'true'
            && form.dataset.dependencyPreflightSignature !== signature) {
            delete form.dataset.dependencyPreflightComplete;
            delete form.dataset.dependencyPreflightSignature;
        }
        if (!form.dataset.dependencyPreflightUrl || form.dataset.dependencyPreflightComplete === 'true') {
            requestConfirmation(form, submitter);
            return;
        }
        if (form.dataset.dependencyPreflightPending === 'true') return;
        form.dataset.dependencyPreflightPending = 'true';
        previousFocus = document.activeElement;
        title.textContent = translate('Analyse des dépendances…');
        message.textContent = translate('Lecture des dépendances locales et Workshop avant toute modification du pack.');
        if (secondary instanceof HTMLButtonElement) secondary.hidden = true;
        if (accept instanceof HTMLButtonElement) accept.hidden = true;
        overlay.hidden = false;
        document.body.classList.add('has-modal');
        try {
            const response = await fetch(form.dataset.dependencyPreflightUrl, {
                method: 'POST',
                body: new FormData(form),
                credentials: 'same-origin'
            });
            const plan = await response.json();
            if (!response.ok || plan.error) throw new Error(plan.error || `Le serveur a répondu ${response.status}.`);
            const dependencies = Array.isArray(plan.dependencies) ? plan.dependencies : [];
            const unresolved = Array.isArray(plan.unresolved) ? plan.unresolved : [];
            form.dataset.dependencyPreflightComplete = 'true';
            form.dataset.dependencyPreflightSignature = signature;
            if (dependencies.length === 0 && unresolved.length === 0) {
                overlay.hidden = true;
                document.body.classList.remove('has-modal');
                form.dataset.confirmBypass = 'true';
                form.requestSubmit(submitter || undefined);
                return;
            }
            const lines = dependencies.map(item => `• ${item.name || item.id} — ${item.source || item.id}`);
            if (unresolved.length > 0) lines.push(`• ${translate('Source non résolue automatiquement :')} ${unresolved.join(', ')}`);
            form.dataset.confirmTitle = dependencies.length > 0 ? translate('Ajouter les dépendances manquantes ?') : translate('Dépendance non résolue');
            form.dataset.confirmMessage = `${translate('Le mod déclare les dépendances suivantes, absentes du pack :')}\n${lines.join('\n')}\n\n${translate('Vous pouvez les ajouter maintenant ou continuer uniquement avec le mod sélectionné.')}`;
            form.dataset.confirmAction = dependencies.length > 0 ? `${translate('Ajouter avec')} ${dependencies.length} ${translate('dépendance(s)')}` : translate('Ajouter le mod malgré tout');
            form.dataset.confirmSecondaryAction = dependencies.length > 0 ? translate('Ajouter uniquement le mod') : '';
            requestConfirmation(form, submitter);
        } catch (error) {
            pending = { form, submitter };
            title.textContent = translate('Analyse des dépendances interrompue');
            message.textContent = error instanceof Error ? error.message : String(error);
            if (secondary instanceof HTMLButtonElement) {
                secondary.textContent = translate('Continuer sans les dépendances');
                secondary.hidden = false;
            }
            if (accept instanceof HTMLButtonElement) accept.hidden = true;
        } finally {
            delete form.dataset.dependencyPreflightPending;
        }
    };

    document.addEventListener('click', event => {
        if (!(event.target instanceof Element)) return;
        const submitter = event.target.closest('button[type="submit"], input[type="submit"]');
        const form = submitter?.form;
        if (!(form instanceof HTMLFormElement) || (!form.dataset.confirmTitle && !form.dataset.dependencyPreflightUrl) || form.dataset.confirmBypass === 'true') return;
        event.preventDefault();
        event.stopImmediatePropagation();
        void prepareDependencyConfirmation(form, submitter instanceof HTMLButtonElement ? submitter : null);
    }, true);

    document.addEventListener('submit', event => {
        const form = event.target;
        if (!(form instanceof HTMLFormElement) || (!form.dataset.confirmTitle && !form.dataset.dependencyPreflightUrl)) return;
        if (form.dataset.loadingCommitting === 'true') return;
        if (form.dataset.confirmBypass === 'true') {
            delete form.dataset.confirmBypass;
            form.dataset.loadingConfirmed = 'true';
            return;
        }
        if (event.defaultPrevented) return;
        event.preventDefault();
        void prepareDependencyConfirmation(form, event.submitter instanceof HTMLButtonElement ? event.submitter : null);
    });

    cancel?.addEventListener('click', close);
    accept?.addEventListener('click', () => {
        if (!pending) return;
        if (pending.form.dataset.dependencyPreflightComplete === 'true') {
            submitDependencyChoice(true);
            return;
        }
        const { form, submitter } = pending;
        let acknowledgement = form.querySelector('input[name="confirmationAcknowledged"]');
        if (!(acknowledgement instanceof HTMLInputElement)) {
            acknowledgement = document.createElement('input');
            acknowledgement.type = 'hidden';
            acknowledgement.name = 'confirmationAcknowledged';
            form.append(acknowledgement);
        }
        acknowledgement.value = 'true';
        overlay.hidden = true;
        document.body.classList.remove('has-modal');
        pending = null;
        form.dataset.confirmBypass = 'true';
        form.requestSubmit(submitter || undefined);
    });
    secondary?.addEventListener('click', () => submitDependencyChoice(false));
    overlay.addEventListener('click', event => { if (event.target === overlay) close(); });
    document.addEventListener('keydown', event => {
        if (event.key === 'Escape' && !overlay.hidden) {
            event.preventDefault();
            close();
        }
    });
})();

(() => {
    const overlay = document.querySelector('[data-loading-overlay]');
    if (!overlay) return;

    const overlayCard = overlay.querySelector('.loading-card');
    const overlayTitle = overlay.querySelector('[data-loading-title]');
    const overlayDetail = overlay.querySelector('[data-loading-detail]');
    const overlayStage = overlay.querySelector('[data-loading-stage]');
    const overlayCounter = overlay.querySelector('[data-loading-counter]');
    const overlayCurrent = overlay.querySelector('[data-loading-current]');
    const overlayTrack = overlay.querySelector('[data-loading-track]');
    const overlayProgressList = overlay.querySelector('[data-loading-progress-list]');
    const overlayClose = overlay.querySelector('[data-loading-close]');
    const overlayCancel = overlay.querySelector('[data-loading-cancel]');
    const overlayInteraction = overlay.querySelector('[data-loading-interaction]');
    const overlayInteractionEyebrow = overlay.querySelector('[data-loading-interaction-eyebrow]');
    const overlayInteractionTitle = overlay.querySelector('[data-loading-interaction-title]');
    const overlayInteractionMessage = overlay.querySelector('[data-loading-interaction-message]');
    const overlayInteractionMobile = overlay.querySelector('[data-loading-interaction-mobile]');
    const overlayInteractionCodeField = overlay.querySelector('[data-loading-interaction-code-field]');
    const overlayInteractionCode = overlay.querySelector('[data-loading-interaction-code]');
    const overlayInteractionError = overlay.querySelector('[data-loading-interaction-error]');
    const overlayInteractionCancel = overlay.querySelector('[data-loading-interaction-cancel]');
    const overlayInteractionSecondary = overlay.querySelector('[data-loading-interaction-secondary]');
    const overlayInteractionSubmit = overlay.querySelector('[data-loading-interaction-submit]');
    const buttonContent = new WeakMap();
    const navigationDelay = 160;
    let operationActive = false;
    let activeController = null;
    let activeStepTimer = null;
    let pendingInteraction = null;
    const suppressedAbortControllers = new WeakSet();
    const activeRows = new Map();

    const inferTitle = value => {
        const label = (value || '').trim().toLocaleLowerCase('fr');
        const operations = [
            [/publi/, 'Publication Steam Workshop…'],
            [/constru|build/, 'Construction du pack…'],
            [/actual|détect|relancer|refresh/, 'Actualisation des données…'],
            [/télécharg|import/, 'Téléchargement du contenu Workshop…'],
            [/cré|create/, 'Création en cours…'],
            [/dupli/, 'Duplication du pack…'],
            [/supprim/, 'Suppression en cours…'],
            [/démarr|start/, 'Démarrage du serveur…'],
            [/arrêt|arrêter|quit/, 'Arrêt propre du serveur…'],
            [/appli|install/, 'Application du pack au serveur…'],
            [/ajout/, 'Ajout du mod au pack…'],
            [/retir/, 'Retrait du mod…'],
            [/enregistr|sauvegard/, 'Enregistrement des modifications…'],
            [/mont|desc|ordre|déplac/, 'Mise à jour de l’ordre…']
        ];
        return operations.find(([pattern]) => pattern.test(label))?.[1] || 'Traitement en cours…';
    };

    const setButtonBusy = (button, label) => {
        if (!(button instanceof HTMLButtonElement) || button.classList.contains('is-loading')) return;
        buttonContent.set(button, Array.from(button.childNodes).map(node => node.cloneNode(true)));
        button.style.minWidth = `${Math.ceil(button.getBoundingClientRect().width)}px`;
        button.classList.add('is-loading');
        button.setAttribute('aria-disabled', 'true');

        const spinner = document.createElement('span');
        spinner.className = 'button-spinner';
        spinner.setAttribute('aria-hidden', 'true');
        const text = document.createElement('span');
        text.textContent = label;
        button.replaceChildren(spinner, text);
    };

    const showLoading = ({ title, detail, button, form, cancellable = false } = {}) => {
        if (operationActive) return;
        operationActive = true;
        overlayTitle.textContent = title || inferTitle(button?.textContent);
        overlayDetail.textContent = detail || 'Veuillez patienter pendant que PZASM termine cette opération.';
        if (form) {
            form.dataset.loadingBusy = 'true';
            form.setAttribute('aria-busy', 'true');
            form.querySelectorAll('button[type="submit"], input[type="submit"]').forEach(control => control.setAttribute('aria-disabled', 'true'));
        }
        setButtonBusy(button, button?.dataset.loadingButton || 'Patientez…');
        document.documentElement.setAttribute('aria-busy', 'true');
        document.body.classList.add('is-loading');
        overlay.hidden = false;
        if (overlayCancel) {
            overlayCancel.hidden = !cancellable;
            overlayCancel.disabled = false;
        }
        requestAnimationFrame(() => overlay.classList.add('is-visible'));
    };

    const inferredSteps = form => {
        if (form?.dataset.loadingSteps) return form.dataset.loadingSteps.split('|').map(value => value.trim()).filter(Boolean);
        const text = `${form?.dataset.loadingTitle || ''} ${form?.dataset.loadingDetail || ''}`.toLocaleLowerCase('fr');
        if (/publi/.test(text)) return ['Validation du projet', 'Construction atomique', 'Authentification SteamCMD', 'Envoi Workshop', 'Coordination RCON', 'Enregistrement du Workshop ID'];
        if (/mise à jour|actual|mod/.test(text)) return ['Préparation de la sélection', 'Téléchargement SteamCMD', 'Inspection des mod.info', 'Remplacement des snapshots', 'Enregistrement du projet'];
        if (/cré/.test(text)) return ['Validation des informations', 'Création de l’identifiant stable', 'Préparation des dossiers', 'Enregistrement'];
        if (/constru|build/.test(text)) return ['Validation', 'Vérification des snapshots', 'Copie des fichiers', 'Génération des manifestes'];
        if (/serveur|rcon|ssh/.test(text)) return ['Validation du profil', 'Connexion au serveur', 'Exécution de l’action', 'Vérification du résultat'];
        return ['Validation de la demande', 'Exécution de l’opération', 'Enregistrement des changements'];
    };

    const prepareDetailedProgress = steps => {
        overlay.classList.add('has-detailed-progress');
        if (overlayStage) overlayStage.hidden = false;
        if (overlayProgressList) overlayProgressList.hidden = false;
        if (overlayTrack) {
            overlayTrack.classList.add('is-determinate');
            overlayTrack.style.width = '3%';
        }
        activeRows.clear();
        overlayProgressList?.replaceChildren();
        steps.forEach((step, index) => {
            const row = document.createElement('div');
            row.className = 'loading-progress-item';
            const marker = document.createElement('span');
            marker.textContent = `${index + 1}`;
            const copy = document.createElement('span');
            const title = document.createElement('strong');
            const detail = document.createElement('small');
            title.textContent = step;
            detail.textContent = index === 0 ? 'En cours…' : 'En attente';
            copy.append(title, detail);
            row.append(marker, copy);
            if (index === 0) row.classList.add('is-current');
            overlayProgressList?.append(row);
            activeRows.set(step.toLocaleLowerCase('fr'), { row, title, detail });
        });
        if (overlayCounter) overlayCounter.textContent = `1 / ${Math.max(steps.length, 1)}`;
        if (overlayCurrent) overlayCurrent.textContent = steps[0] || 'Préparation';
    };

    const markStep = (index, message) => {
        const rows = Array.from(activeRows.values());
        if (rows.length === 0) return;
        const selected = Math.max(0, Math.min(index, rows.length - 1));
        rows.forEach((entry, rowIndex) => {
            entry.row.classList.toggle('is-current', rowIndex === selected);
            entry.row.classList.toggle('is-complete', rowIndex < selected);
            if (rowIndex < selected) entry.detail.textContent = 'Terminé';
            else if (rowIndex === selected) entry.detail.textContent = message || 'En cours…';
        });
        if (overlayCounter) overlayCounter.textContent = `${selected + 1} / ${rows.length}`;
        if (overlayCurrent) overlayCurrent.textContent = rows[selected].title.textContent;
        if (overlayDetail && message) overlayDetail.textContent = message;
        if (overlayTrack) overlayTrack.style.width = `${Math.max(3, ((selected + .35) / rows.length) * 100)}%`;
    };

    const beginEstimatedSteps = () => {
        let index = 0;
        activeStepTimer = window.setInterval(() => {
            const total = activeRows.size;
            if (total <= 1 || index >= total - 2) return;
            index += 1;
            markStep(index, 'Opération en cours…');
        }, 1400);
    };

    const startWorkshopProgress = async (form, button) => {
        showLoading({
            title: form.dataset.loadingTitle,
            detail: 'Préparation de la file de téléchargement et vérification de la destination.',
            button,
            form,
            cancellable: true
        });
        activeController = new AbortController();
        overlay.classList.add('has-detailed-progress');
        if (overlayStage) overlayStage.hidden = false;
        if (overlayProgressList) overlayProgressList.hidden = false;
        if (overlayTrack) {
            overlayTrack.classList.add('is-determinate');
            overlayTrack.style.width = '0%';
        }

        const selectedItems = Array.from(form.querySelectorAll('[data-catalog-selected-list] [data-catalog-value]')).map(row => ({
            value: row.dataset.catalogValue,
            title: row.querySelector('strong')?.textContent?.trim() || `Workshop ${row.dataset.catalogValue}`,
            detail: row.querySelector('small')?.textContent?.trim() || `Workshop ${row.dataset.catalogValue}`
        }));
        const rows = new Map();
        overlayProgressList?.replaceChildren();
        selectedItems.forEach((item, index) => {
            const row = document.createElement('div');
            row.className = 'loading-progress-item';
            row.dataset.workshopId = item.value;
            const marker = document.createElement('span');
            marker.textContent = `${index + 1}`;
            const copy = document.createElement('span');
            const title = document.createElement('strong');
            const detail = document.createElement('small');
            title.textContent = item.title;
            detail.textContent = 'En attente';
            copy.append(title, detail);
            row.append(marker, copy);
            overlayProgressList?.append(row);
            rows.set(item.value, { row, detail, title: item.title });
        });

        const updateProgress = record => {
            if (record.type === 'progress') {
                const item = rows.get(String(record.workshopId));
                rows.forEach(({ row }) => row.classList.remove('is-current'));
                if (item) {
                    item.row.classList.add('is-current');
                    item.row.classList.toggle('is-complete', record.phase === 'complete');
                    item.detail.textContent = record.message;
                    item.row.scrollIntoView({ block: 'nearest' });
                }
                const fraction = record.phase === 'complete' ? 1 : record.phase === 'inspect' ? .68 : .22;
                const percent = Math.max(2, Math.min(100, ((record.index + fraction) / record.total) * 100));
                if (overlayTrack) overlayTrack.style.width = `${percent}%`;
                if (overlayCounter) overlayCounter.textContent = `${record.index + 1} / ${record.total}`;
                if (overlayCurrent) overlayCurrent.textContent = item?.title || `Workshop ${record.workshopId}`;
                if (overlayDetail) overlayDetail.textContent = record.message;
            } else if (record.type === 'finalizing') {
                rows.forEach(({ row }) => row.classList.remove('is-current'));
                if (overlayCurrent) overlayCurrent.textContent = 'Finalisation';
                if (overlayDetail) overlayDetail.textContent = record.message;
                if (overlayTrack) overlayTrack.style.width = '96%';
            } else if (record.type === 'done') {
                try { window.sessionStorage.removeItem(form.dataset.catalogStorageKey); } catch { }
                rows.forEach(({ row }) => row.classList.add('is-complete'));
                if (overlayTitle) overlayTitle.textContent = 'Import terminé';
                if (overlayCurrent) overlayCurrent.textContent = record.message;
                if (overlayDetail) overlayDetail.textContent = 'Redirection vers votre configuration…';
                if (overlayTrack) overlayTrack.style.width = '100%';
                window.setTimeout(() => window.location.assign(record.redirectUrl), 650);
            } else if (record.type === 'error') {
                overlay.classList.add('has-error');
                if (overlayTitle) overlayTitle.textContent = 'Import interrompu';
                if (overlayDetail) overlayDetail.textContent = record.message;
                if (overlayCurrent) overlayCurrent.textContent = 'Une intervention est nécessaire';
                if (overlayClose) overlayClose.hidden = false;
            }
        };

        try {
            const endpoint = new URL(form.action, window.location.href);
            endpoint.searchParams.set('handler', 'ImportWorkshopStream');
            const response = await fetch(endpoint, { method: 'POST', body: new FormData(form), credentials: 'same-origin', signal: activeController.signal });
            if (!response.ok || !response.body) throw new Error(`Le serveur a répondu ${response.status}.`);
            const reader = response.body.getReader();
            const decoder = new TextDecoder();
            let buffer = '';
            while (true) {
                const { done, value } = await reader.read();
                buffer += decoder.decode(value || new Uint8Array(), { stream: !done });
                const lines = buffer.split('\n');
                buffer = lines.pop() || '';
                lines.filter(Boolean).forEach(line => updateProgress(JSON.parse(line)));
                if (done) break;
            }
            if (buffer.trim()) updateProgress(JSON.parse(buffer));
        } catch (error) {
            updateProgress({ type: 'error', message: error?.name === 'AbortError' ? 'Import annulé. Les éléments déjà terminés restent inchangés.' : error instanceof Error ? error.message : String(error) });
        }
    };

    const readProgressStream = async (response, update) => {
        if (!response.ok || !response.body) throw new Error(`Le serveur a répondu ${response.status}.`);
        const reader = response.body.getReader();
        const decoder = new TextDecoder();
        let buffer = '';
        while (true) {
            const { done, value } = await reader.read();
            buffer += decoder.decode(value || new Uint8Array(), { stream: !done });
            const lines = buffer.split('\n');
            buffer = lines.pop() || '';
            lines.filter(Boolean).forEach(line => update(JSON.parse(line)));
            if (done) break;
        }
        if (buffer.trim()) update(JSON.parse(buffer));
    };

    const resolveSubmissionEndpoint = (form, button) => {
        const buttonOverride = button instanceof HTMLButtonElement && button.hasAttribute('formaction')
            ? button.getAttribute('formaction')
            : null;
        return new URL(buttonOverride || form.getAttribute('action') || form.action, window.location.href);
    };

    const revealInteraction = () => window.requestAnimationFrame(() => overlayInteraction?.scrollIntoView({ block: 'nearest' }));

    const showMobileApproval = (form, button, message) => {
        pendingInteraction = { form, button, kind: 'steam_mobile_approval' };
        overlay.classList.add('requires-interaction', 'awaiting-mobile-approval');
        if (overlayTitle) overlayTitle.textContent = 'Approbation Steam Mobile';
        if (overlayCurrent) overlayCurrent.textContent = 'Confirmation sur votre téléphone';
        if (overlayDetail) overlayDetail.textContent = message;
        if (overlayInteraction) overlayInteraction.hidden = false;
        if (overlayInteractionEyebrow) overlayInteractionEyebrow.textContent = 'APPROBATION STEAM GUARD';
        if (overlayInteractionTitle) overlayInteractionTitle.textContent = 'Confirmez la demande dans Steam Mobile';
        if (overlayInteractionMessage) overlayInteractionMessage.textContent = message;
        if (overlayInteractionMobile) overlayInteractionMobile.hidden = false;
        if (overlayInteractionCodeField) overlayInteractionCodeField.hidden = true;
        if (overlayInteractionError) overlayInteractionError.hidden = true;
        if (overlayInteractionSecondary) overlayInteractionSecondary.hidden = true;
        if (overlayInteractionSubmit) overlayInteractionSubmit.textContent = 'Utiliser un code à la place';
        if (overlayCancel) overlayCancel.hidden = true;
        if (overlayClose) overlayClose.hidden = true;
        revealInteraction();
    };

    const showGuardCodeInteraction = (form, button, message, allowMobileRetry = false) => {
        pendingInteraction = { form, button, kind: 'steam_guard_code' };
        overlay.classList.add('requires-interaction');
        overlay.classList.remove('awaiting-mobile-approval');
        if (overlayTitle) overlayTitle.textContent = 'Code Steam Guard';
        if (overlayCurrent) overlayCurrent.textContent = 'Méthode de secours';
        if (overlayDetail) overlayDetail.textContent = message;
        if (overlayInteraction) overlayInteraction.hidden = false;
        if (overlayInteractionEyebrow) overlayInteractionEyebrow.textContent = 'CODE STEAM GUARD';
        if (overlayInteractionTitle) overlayInteractionTitle.textContent = 'Utiliser le code actuel';
        if (overlayInteractionMessage) overlayInteractionMessage.textContent = message;
        if (overlayInteractionMobile) overlayInteractionMobile.hidden = true;
        if (overlayInteractionCodeField) overlayInteractionCodeField.hidden = false;
        if (overlayInteractionError) overlayInteractionError.hidden = true;
        if (overlayInteractionSecondary) {
            overlayInteractionSecondary.hidden = !allowMobileRetry;
            overlayInteractionSecondary.textContent = 'Réessayer l’approbation mobile';
        }
        if (overlayInteractionSubmit) overlayInteractionSubmit.textContent = 'Valider et réessayer';
        if (overlayCancel) overlayCancel.hidden = true;
        if (overlayClose) overlayClose.hidden = true;
        if (overlayInteractionCode) {
            overlayInteractionCode.value = document.querySelector('[data-steam-guard-code]')?.value || '';
            window.setTimeout(() => overlayInteractionCode.focus(), 50);
        }
        revealInteraction();
    };

    const showSessionInteraction = (form, button, message) => {
        pendingInteraction = { form, button, kind: 'steam_session_required' };
        overlay.classList.add('requires-interaction');
        overlay.classList.remove('awaiting-mobile-approval');
        if (overlayTitle) overlayTitle.textContent = 'Session Steam requise';
        if (overlayCurrent) overlayCurrent.textContent = 'Reconnectez le compte éditeur';
        if (overlayDetail) overlayDetail.textContent = message;
        if (overlayInteraction) overlayInteraction.hidden = false;
        if (overlayInteractionEyebrow) overlayInteractionEyebrow.textContent = 'SESSION STEAMCMD';
        if (overlayInteractionTitle) overlayInteractionTitle.textContent = 'Session portable expirée ou absente';
        if (overlayInteractionMessage) overlayInteractionMessage.textContent = message;
        if (overlayInteractionMobile) overlayInteractionMobile.hidden = true;
        if (overlayInteractionCodeField) overlayInteractionCodeField.hidden = true;
        if (overlayInteractionSecondary) overlayInteractionSecondary.hidden = true;
        if (overlayInteractionSubmit) overlayInteractionSubmit.textContent = 'Fermer et reconnecter';
        if (overlayCancel) overlayCancel.hidden = true;
        if (overlayClose) overlayClose.hidden = true;
        revealInteraction();
    };

    const startOperationProgress = async (form, button) => {
        const source = button?.dataset.loadingTitle || button?.dataset.loadingSteps ? button : form;
        showLoading({ title: source.dataset.loadingTitle || form.dataset.loadingTitle, detail: source.dataset.loadingDetail || form.dataset.loadingDetail, button, form, cancellable: true });
        prepareDetailedProgress(inferredSteps(source));
        const controller = new AbortController();
        activeController = controller;
        const phaseIndexes = new Map();
        const configuredPhaseIndexes = new Map();
        const phaseMap = source.dataset.loadingPhaseMap || form.dataset.loadingPhaseMap || '';
        phaseMap.split(',').forEach(entry => {
            const separator = entry.lastIndexOf(':');
            if (separator <= 0) return;
            const phase = entry.slice(0, separator).trim();
            const index = Number.parseInt(entry.slice(separator + 1).trim(), 10);
            if (phase && Number.isInteger(index)) configuredPhaseIndexes.set(phase, index);
        });
        let nextPhaseIndex = 0;
        let latestPhaseIndex = 0;
        const update = record => {
            if (record.type === 'progress') {
                const maximumIndex = Math.max(activeRows.size - 1, 0);
                if (!phaseIndexes.has(record.phase)) {
                    const configuredIndex = configuredPhaseIndexes.get(record.phase);
                    const candidate = Number.isInteger(configuredIndex)
                        ? configuredIndex
                        : configuredPhaseIndexes.size > 0
                            ? latestPhaseIndex
                            : nextPhaseIndex++;
                    latestPhaseIndex = Math.max(latestPhaseIndex, Math.min(candidate, maximumIndex));
                    phaseIndexes.set(record.phase, latestPhaseIndex);
                }
                const targetIndex = Math.max(phaseIndexes.get(record.phase), latestPhaseIndex);
                phaseIndexes.set(record.phase, targetIndex);
                markStep(targetIndex, record.message);
                if (record.phase === 'mobileapproval') showMobileApproval(form, button, record.message);
            } else if (record.type === 'done') {
                overlay.classList.remove('requires-interaction', 'awaiting-mobile-approval');
                if (overlayInteraction) overlayInteraction.hidden = true;
                Array.from(activeRows.values()).forEach(entry => { entry.row.classList.remove('is-current'); entry.row.classList.add('is-complete'); entry.detail.textContent = 'Terminé'; });
                if (overlayTitle) overlayTitle.textContent = 'Opération terminée';
                if (overlayCurrent) overlayCurrent.textContent = record.message;
                if (overlayDetail) overlayDetail.textContent = 'Redirection vers la configuration mise à jour…';
                if (overlayTrack) overlayTrack.style.width = '100%';
                if (overlayCancel) overlayCancel.hidden = true;
                window.setTimeout(() => window.location.assign(record.redirectUrl), 650);
            } else if (record.type === 'interaction') {
                activeController = null;
                if (record.kind === 'steam_guard_code' || record.kind === 'steam_guard_mobile_expired')
                    showGuardCodeInteraction(form, button, record.message, record.kind === 'steam_guard_mobile_expired');
                else showSessionInteraction(form, button, record.message);
            } else if (record.type === 'error') {
                overlay.classList.remove('requires-interaction', 'awaiting-mobile-approval');
                if (overlayInteraction) overlayInteraction.hidden = true;
                overlay.classList.add('has-error');
                if (overlayTitle) overlayTitle.textContent = 'Opération interrompue';
                if (overlayDetail) overlayDetail.textContent = record.message;
                if (overlayCurrent) overlayCurrent.textContent = 'Une intervention est nécessaire';
                if (overlayCancel) overlayCancel.hidden = true;
                if (overlayClose) overlayClose.hidden = false;
            }
        };
        try {
            const endpoint = resolveSubmissionEndpoint(form, button);
            const handler = endpoint.searchParams.get('handler');
            if (!handler) throw new Error('Opération serveur non reconnue.');
            endpoint.searchParams.set('handler', `${handler}Stream`);
            const formData = typeof FormData === 'function' ? new FormData(form, button || undefined) : new FormData(form);
            const response = await fetch(endpoint, { method: 'POST', body: formData, credentials: 'same-origin', signal: controller.signal });
            await readProgressStream(response, update);
        } catch (error) {
            if (error?.name === 'AbortError' && suppressedAbortControllers.has(controller)) return;
            update({ type: 'error', message: error?.name === 'AbortError' ? 'Annulation demandée. Le processus externe a été arrêté; les étapes atomiques déjà terminées peuvent rester appliquées.' : error instanceof Error ? error.message : String(error) });
        }
    };

    const startFetchProgress = async (form, button) => {
        const source = button?.dataset.loadingTitle || button?.dataset.loadingSteps ? button : form;
        showLoading({ title: source.dataset.loadingTitle || form.dataset.loadingTitle, detail: source.dataset.loadingDetail || form.dataset.loadingDetail, button, form, cancellable: true });
        prepareDetailedProgress(inferredSteps(source));
        beginEstimatedSteps();
        activeController = new AbortController();
        try {
            const endpoint = resolveSubmissionEndpoint(form, button);
            const formData = new FormData(form, button || undefined);
            const response = await fetch(endpoint, { method: 'POST', body: formData, credentials: 'same-origin', redirect: 'follow', signal: activeController.signal });
            if (!response.ok) throw new Error((await response.text()).trim() || `Le serveur a répondu ${response.status}.`);
            markStep(Math.max(activeRows.size - 1, 0), 'Terminé. Redirection…');
            if (overlayTrack) overlayTrack.style.width = '100%';
            if (overlayCancel) overlayCancel.hidden = true;
            window.location.assign(response.url || window.location.href);
        } catch (error) {
            overlay.classList.add('has-error');
            if (overlayTitle) overlayTitle.textContent = error?.name === 'AbortError' ? 'Opération annulée' : 'Opération interrompue';
            if (overlayCurrent) overlayCurrent.textContent = error?.name === 'AbortError' ? 'Annulation confirmée' : 'Une intervention est nécessaire';
            if (overlayDetail) overlayDetail.textContent = error?.name === 'AbortError' ? 'La requête a été annulée. Une étape déjà validée par le serveur peut rester appliquée.' : error instanceof Error ? error.message : String(error);
            if (overlayCancel) overlayCancel.hidden = true;
            if (overlayClose) overlayClose.hidden = false;
        }
    };

    const resetLoading = () => {
        if (activeStepTimer) window.clearInterval(activeStepTimer);
        activeStepTimer = null;
        activeController = null;
        operationActive = false;
        pendingInteraction = null;
        overlay.classList.remove('is-visible');
        overlay.hidden = true;
        document.documentElement.removeAttribute('aria-busy');
        document.body.classList.remove('is-loading');
        document.querySelectorAll('form[data-loading-busy="true"]').forEach(form => {
            delete form.dataset.loadingBusy;
            form.removeAttribute('aria-busy');
            form.querySelectorAll('[aria-disabled="true"]').forEach(control => control.removeAttribute('aria-disabled'));
        });
        document.querySelectorAll('button.is-loading').forEach(button => {
            const original = buttonContent.get(button);
            if (original) button.replaceChildren(...original.map(node => node.cloneNode(true)));
            button.classList.remove('is-loading');
            button.style.minWidth = '';
            button.removeAttribute('aria-disabled');
        });
        overlay.classList.remove('has-detailed-progress', 'has-error', 'requires-interaction', 'awaiting-mobile-approval');
        if (overlayStage) overlayStage.hidden = true;
        if (overlayProgressList) { overlayProgressList.hidden = true; overlayProgressList.replaceChildren(); }
        if (overlayClose) overlayClose.hidden = true;
        if (overlayCancel) overlayCancel.hidden = true;
        if (overlayInteraction) overlayInteraction.hidden = true;
        if (overlayInteractionEyebrow) overlayInteractionEyebrow.textContent = 'VALIDATION STEAM GUARD';
        if (overlayInteractionMobile) overlayInteractionMobile.hidden = true;
        if (overlayInteractionCodeField) overlayInteractionCodeField.hidden = false;
        if (overlayInteractionCode) overlayInteractionCode.value = '';
        const transientGuardCode = document.querySelector('[data-steam-guard-code]');
        if (transientGuardCode instanceof HTMLInputElement) transientGuardCode.value = '';
        if (overlayInteractionError) overlayInteractionError.hidden = true;
        if (overlayInteractionSecondary) overlayInteractionSecondary.hidden = true;
        if (overlayInteractionSubmit) overlayInteractionSubmit.textContent = 'Valider et continuer';
        if (overlayTrack) { overlayTrack.classList.remove('is-determinate'); overlayTrack.style.width = ''; }
        if (overlayCard) overlayCard.scrollTop = 0;
    };

    overlayClose?.addEventListener('click', resetLoading);
    overlayInteractionCancel?.addEventListener('click', () => {
        if (activeController) {
            suppressedAbortControllers.add(activeController);
            activeController.abort();
        }
        resetLoading();
    });
    overlayInteractionSecondary?.addEventListener('click', () => {
        if (!pendingInteraction || pendingInteraction.kind !== 'steam_guard_code') return;
        const retry = { form: pendingInteraction.form, button: pendingInteraction.button };
        resetLoading();
        window.setTimeout(() => void startOperationProgress(retry.form, retry.button), 80);
    });
    overlayInteractionSubmit?.addEventListener('click', () => {
        if (!pendingInteraction) return;
        if (pendingInteraction.kind === 'steam_session_required') {
            resetLoading();
            const passwordField = document.querySelector('[data-steam-password]');
            passwordField?.scrollIntoView({ behavior: 'smooth', block: 'center' });
            window.setTimeout(() => passwordField?.focus(), 350);
            return;
        }

        if (pendingInteraction.kind === 'steam_mobile_approval') {
            const { form, button } = pendingInteraction;
            if (activeController) {
                suppressedAbortControllers.add(activeController);
                activeController.abort();
                activeController = null;
            }
            showGuardCodeInteraction(form, button, 'Saisissez le code actuel affiché dans l’application Steam ou reçu par e-mail. Une nouvelle tentative sécurisée sera lancée.', true);
            return;
        }

        const code = overlayInteractionCode?.value.trim() || '';
        if (!code) {
            if (overlayInteractionError) overlayInteractionError.hidden = false;
            overlayInteractionCode?.focus();
            return;
        }
        const retry = { form: pendingInteraction.form, button: pendingInteraction.button };
        resetLoading();
        const guardField = document.querySelector('[data-steam-guard-code]');
        if (guardField instanceof HTMLInputElement) guardField.value = code;
        window.setTimeout(() => void startOperationProgress(retry.form, retry.button), 80);
    });
    overlayInteractionCode?.addEventListener('input', () => {
        if (overlayInteractionError) overlayInteractionError.hidden = true;
    });
    overlayInteractionCode?.addEventListener('keydown', event => {
        if (event.key === 'Enter') {
            event.preventDefault();
            overlayInteractionSubmit?.click();
        }
    });
    overlayCancel?.addEventListener('click', () => {
        if (!activeController) return;
        overlayCancel.disabled = true;
        if (overlayCurrent) overlayCurrent.textContent = 'Annulation demandée…';
        if (overlayDetail) overlayDetail.textContent = 'Arrêt du processus externe et fermeture propre de la requête en cours.';
        activeController.abort();
    });

    document.addEventListener('submit', event => {
        const form = event.target;
        if (!(form instanceof HTMLFormElement) || form.dataset.loadingSkip === 'true') return;
        if (form.dataset.loadingCommitting === 'true') {
            delete form.dataset.loadingCommitting;
            return;
        }
        if (form.dataset.loadingBusy === 'true') {
            event.preventDefault();
            return;
        }
        if (event.defaultPrevented) return;
        if (form.dataset.confirmTitle && form.dataset.loadingConfirmed !== 'true') {
            event.preventDefault();
            return;
        }
        delete form.dataset.loadingConfirmed;
        const button = event.submitter instanceof HTMLButtonElement ? event.submitter : form.querySelector('button[type="submit"]');
        if (form.matches('[data-file-download]')) {
            const token = typeof crypto.randomUUID === 'function'
                ? crypto.randomUUID()
                : `${Date.now()}-${Math.random().toString(16).slice(2)}`;
            const tokenField = form.querySelector('[data-download-token]');
            if (tokenField instanceof HTMLInputElement) tokenField.value = token;
            form.target = 'pzasm-download-frame';
            showLoading({
                title: form.dataset.loadingTitle,
                detail: form.dataset.loadingDetail,
                button,
                form
            });
            prepareDetailedProgress(inferredSteps(form));
            beginEstimatedSteps();
            const startedAt = Date.now();
            const monitor = window.setInterval(() => {
                const cookie = document.cookie.split(';').map(value => value.trim()).find(value => value.startsWith('PZASM.Download='));
                if (!cookie && Date.now() - startedAt < 12 * 60 * 60 * 1000) return;
                if (!cookie) {
                    window.clearInterval(monitor);
                    if (overlayTitle) overlayTitle.textContent = 'Export interrompu';
                    if (overlayDetail) overlayDetail.textContent = 'Le manager n’a pas confirmé la création du fichier dans le délai maximal.';
                    overlay.classList.add('has-error');
                    if (overlayClose) overlayClose.hidden = false;
                    return;
                }
                const value = decodeURIComponent(cookie.slice(cookie.indexOf('=') + 1));
                if (!value.startsWith(`${token}:`)) return;
                window.clearInterval(monitor);
                document.cookie = 'PZASM.Download=; Max-Age=0; Path=/; SameSite=Strict';
                const failed = value.endsWith(':error');
                resetLoading();
                if (failed) window.location.reload();
            }, 500);
            return;
        }
        if (form.matches('[data-workshop-progress]')) {
            event.preventDefault();
            void startWorkshopProgress(form, button);
            return;
        }
        if (form.matches('[data-operation-progress]') || button?.matches('[data-operation-progress]')) {
            event.preventDefault();
            void startOperationProgress(form, button);
            return;
        }
        if ((form.method || 'get').toLocaleLowerCase() === 'get') {
            showLoading({
                title: form.dataset.loadingTitle || button?.dataset.loadingTitle || inferTitle(button?.textContent),
                detail: form.dataset.loadingDetail || button?.dataset.loadingDetail,
                button,
                form
            });
            return;
        }
        const source = button?.dataset.loadingTitle || button?.dataset.loadingSteps ? button : form;
        showLoading({
            title: source.dataset.loadingTitle || form.dataset.loadingTitle,
            detail: source.dataset.loadingDetail || form.dataset.loadingDetail,
            button,
            form
        });
        prepareDetailedProgress(inferredSteps(source));
        beginEstimatedSteps();
    });

    document.addEventListener('click', event => {
        if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
        if (!(event.target instanceof Element)) return;
        const link = event.target.closest('a[href]');
        if (!link || link.target === '_blank' || link.hasAttribute('download')) return;
        const destination = new URL(link.href, window.location.href);
        if (destination.origin !== window.location.origin || (destination.pathname === window.location.pathname && destination.search === window.location.search && destination.hash)) return;
        event.preventDefault();
        showLoading({
            title: link.dataset.loadingTitle || 'Chargement de la page…',
            detail: link.dataset.loadingDetail || 'Préparation de la prochaine vue et actualisation des informations.'
        });
        window.setTimeout(() => window.location.assign(destination.href), navigationDelay);
    });

    document.querySelectorAll('.toast-message, .error-message').forEach(message => {
        const isError = message.classList.contains('error-message');
        message.setAttribute('role', isError ? 'alert' : 'status');
        message.setAttribute('aria-live', isError ? 'assertive' : 'polite');
    });

    document.querySelectorAll('[data-rcon-transcript]').forEach(transcript => {
        transcript.scrollTop = transcript.scrollHeight;
    });

    document.querySelectorAll('[data-summary-action]').forEach(control => {
        control.addEventListener('click', event => event.stopPropagation());
    });

    document.querySelectorAll('[data-rcon-command]').forEach(input => {
        const commands = Array.from(document.querySelectorAll('.rcon-entry > code'))
            .map(node => node.textContent.replace(/^>\s*/, '').trim())
            .filter(command => command && !command.includes('<arguments redacted>'));
        let index = commands.length;
        input.addEventListener('keydown', event => {
            if (event.key !== 'ArrowUp' && event.key !== 'ArrowDown') return;
            event.preventDefault();
            index = event.key === 'ArrowUp' ? Math.max(0, index - 1) : Math.min(commands.length, index + 1);
            input.value = index < commands.length ? commands[index] : '';
        });
    });

    document.querySelectorAll('[data-settings-filter]').forEach(input => {
        const catalog = document.getElementById(input.dataset.settingsFilter || '');
        if (!catalog) return;
        input.addEventListener('input', () => {
            const query = input.value.trim().toLocaleLowerCase().normalize('NFD').replace(/[\u0300-\u036f]/g, '');
            catalog.querySelectorAll('[data-settings-group]').forEach(group => {
                let visible = 0;
                group.querySelectorAll('[data-setting-row]').forEach(row => {
                    const haystack = (row.dataset.settingSearch || '').normalize('NFD').replace(/[\u0300-\u036f]/g, '');
                    const matches = !query || haystack.includes(query);
                    row.hidden = !matches;
                    if (matches) visible++;
                });
                group.hidden = visible === 0;
                if (query && visible > 0) group.open = true;
            });
        });
    });

    document.querySelectorAll('[data-diff-workspace]').forEach(workspace => {
        const modeButtons = Array.from(workspace.querySelectorAll('[data-diff-mode]'));
        const panels = Array.from(workspace.querySelectorAll('[data-diff-panel]'));
        const onlyChanges = workspace.querySelector('[data-diff-only-changes]');
        const wrapLines = workspace.querySelector('[data-diff-wrap]');
        const search = workspace.querySelector('[data-diff-search]');
        const searchCount = workspace.querySelector('[data-diff-search-count]');
        const currentChangeLabel = workspace.querySelector('[data-diff-current-change]');
        const leftSelect = workspace.querySelector('[data-diff-left]');
        const rightSelect = workspace.querySelector('[data-diff-right]');
        const selectorForm = workspace.querySelector('[data-diff-selector-form]');
        let activeMode = localStorage.getItem('pzasm-diff-mode') === 'unified' ? 'unified' : 'side';
        let currentChange = 0;

        const activePanel = () => panels.find(panel => panel.dataset.diffPanel === activeMode);
        const rowsFor = panel => Array.from(panel?.querySelectorAll('[data-diff-row]') || []);
        const changeRows = () => {
            const seen = new Set();
            return rowsFor(activePanel()).filter(row => {
                const value = Number.parseInt(row.dataset.diffChange || '', 10);
                if (!value || seen.has(value)) return false;
                seen.add(value);
                return true;
            });
        };
        const applyContext = () => panels.forEach(panel => {
            const rows = rowsFor(panel);
            if (!onlyChanges?.checked) {
                rows.forEach(row => row.classList.remove('context-hidden'));
                return;
            }
            const changed = rows.map((row, index) => row.dataset.diffChange ? index : -1).filter(index => index >= 0);
            rows.forEach((row, index) => row.classList.toggle('context-hidden', !changed.some(change => Math.abs(change - index) <= 3)));
        });
        const applySearch = () => {
            const query = (search?.value || '').trim().toLocaleLowerCase();
            let count = 0;
            rowsFor(activePanel()).forEach(row => {
                const matches = Boolean(query) && row.textContent.toLocaleLowerCase().includes(query);
                row.classList.toggle('is-search-match', matches);
                if (matches) count++;
            });
            if (searchCount) searchCount.textContent = query ? `${count} résultat(s)` : '';
        };
        const activateMode = mode => {
            activeMode = mode === 'unified' ? 'unified' : 'side';
            panels.forEach(panel => panel.hidden = panel.dataset.diffPanel !== activeMode);
            modeButtons.forEach(button => button.classList.toggle('active', button.dataset.diffMode === activeMode));
            localStorage.setItem('pzasm-diff-mode', activeMode);
            currentChange = 0;
            if (currentChangeLabel) currentChangeLabel.textContent = '0';
            applySearch();
        };
        const navigateChange = direction => {
            const rows = changeRows();
            if (!rows.length) return;
            currentChange = currentChange <= 0
                ? direction > 0 ? 1 : rows.length
                : ((currentChange - 1 + direction + rows.length) % rows.length) + 1;
            panels.forEach(panel => rowsFor(panel).forEach(row => row.classList.remove('is-current-change')));
            const target = rows[currentChange - 1];
            const changeId = target.dataset.diffChange;
            rowsFor(activePanel()).filter(row => row.dataset.diffChange === changeId).forEach(row => row.classList.add('is-current-change'));
            target.scrollIntoView({ behavior: 'smooth', block: 'center', inline: 'nearest' });
            if (currentChangeLabel) currentChangeLabel.textContent = String(currentChange);
        };

        modeButtons.forEach(button => button.addEventListener('click', () => activateMode(button.dataset.diffMode)));
        onlyChanges?.addEventListener('change', applyContext);
        wrapLines?.addEventListener('change', () => workspace.classList.toggle('wrap-lines', wrapLines.checked));
        search?.addEventListener('input', applySearch);
        search?.addEventListener('keydown', event => {
            if (event.key !== 'Enter') return;
            event.preventDefault();
            const matches = rowsFor(activePanel()).filter(row => row.classList.contains('is-search-match'));
            if (!matches.length) return;
            const current = matches.findIndex(row => row.classList.contains('is-current-search'));
            matches.forEach(row => row.classList.remove('is-current-search'));
            const target = matches[(current + 1) % matches.length];
            target.classList.add('is-current-search');
            target.scrollIntoView({ behavior: 'smooth', block: 'center', inline: 'nearest' });
        });
        workspace.querySelector('[data-diff-previous]')?.addEventListener('click', () => navigateChange(-1));
        workspace.querySelector('[data-diff-next]')?.addEventListener('click', () => navigateChange(1));
        workspace.querySelector('[data-diff-swap]')?.addEventListener('click', () => {
            if (!leftSelect || !rightSelect || !selectorForm) return;
            [leftSelect.value, rightSelect.value] = [rightSelect.value, leftSelect.value];
            selectorForm.requestSubmit();
        });
        [leftSelect, rightSelect].forEach(select => select?.addEventListener('change', () => {
            if (!leftSelect || !rightSelect || leftSelect.value !== rightSelect.value) return;
            const alternative = Array.from(rightSelect.options).find(option => option.value !== leftSelect.value);
            if (alternative) rightSelect.value = alternative.value;
        }));
        document.addEventListener('keydown', event => {
            if (event.target instanceof HTMLInputElement || event.target instanceof HTMLSelectElement || event.target instanceof HTMLTextAreaElement) return;
            if (event.key === ']' || event.key === 'j') navigateChange(1);
            if (event.key === '[' || event.key === 'k') navigateChange(-1);
        });
        activateMode(activeMode);
        applyContext();
    });

    document.querySelectorAll('.remote-provider-form').forEach(form => {
        const select = form.querySelector('[data-remote-provider]');
        const groups = Array.from(form.querySelectorAll('[data-provider-fields]'));
        if (!(select instanceof HTMLSelectElement)) return;
        const update = () => {
            const pinePath = form.querySelector('#NewRemote_PineIniPath');
            const sshPath = form.querySelector('#NewRemote_SshIniPath');
            if (select.value === 'PineHosting' && pinePath instanceof HTMLInputElement && !pinePath.value)
                pinePath.value = '/.cache/Server/Zomboid.ini';
            if (select.value === 'RconSsh' && sshPath instanceof HTMLInputElement && sshPath.value === '/.cache/Server/Zomboid.ini')
                sshPath.value = '';
            groups.forEach(group => {
                const active = group.dataset.providerFields === select.value;
                group.hidden = !active;
                group.querySelectorAll('input, select, textarea, button').forEach(control => {
                    if (control instanceof HTMLInputElement || control instanceof HTMLSelectElement || control instanceof HTMLTextAreaElement || control instanceof HTMLButtonElement)
                        control.disabled = !active;
                });
            });
        };
        select.addEventListener('change', update);
        update();
    });

    window.addEventListener('pageshow', resetLoading);
})();
