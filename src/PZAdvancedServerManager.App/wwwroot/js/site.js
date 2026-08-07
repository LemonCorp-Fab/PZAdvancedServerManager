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
    let initial = buttons[0].dataset.tabTarget;
    try { initial = window.sessionStorage.getItem(storageKey) || initial; } catch { }
    activate(initial);
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

(() => {
    const overlay = document.querySelector('[data-loading-overlay]');
    if (!overlay) return;

    const overlayTitle = overlay.querySelector('[data-loading-title]');
    const overlayDetail = overlay.querySelector('[data-loading-detail]');
    const buttonContent = new WeakMap();
    const navigationDelay = 160;
    let operationActive = false;

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

    const showLoading = ({ title, detail, button, form } = {}) => {
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
        requestAnimationFrame(() => overlay.classList.add('is-visible'));
    };

    const resetLoading = () => {
        operationActive = false;
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
    };

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
        event.preventDefault();
        const button = event.submitter instanceof HTMLButtonElement ? event.submitter : form.querySelector('button[type="submit"]');
        showLoading({
            title: form.dataset.loadingTitle || button?.dataset.loadingTitle || inferTitle(button?.textContent),
            detail: form.dataset.loadingDetail || button?.dataset.loadingDetail,
            button,
            form
        });
        window.setTimeout(() => {
            form.dataset.loadingCommitting = 'true';
            form.requestSubmit(button || undefined);
        }, navigationDelay);
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

    window.addEventListener('pageshow', resetLoading);
})();
