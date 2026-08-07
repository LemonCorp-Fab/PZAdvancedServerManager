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
        window.setTimeout(() => HTMLFormElement.prototype.submit.call(form), navigationDelay);
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
