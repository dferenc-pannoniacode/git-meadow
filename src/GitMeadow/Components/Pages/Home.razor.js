function workspaceKey(preference, workspace) {
    return `git-meadow:${preference}:${new URL(document.baseURI).pathname}:${workspace}`;
}

function readRememberedRepositories(key) {
    const value = localStorage.getItem(key);
    try {
        const entries = JSON.parse(value);
        return Array.isArray(entries) ? entries.filter(entry => typeof entry === 'string') : [];
    } catch (error) {
        if (error instanceof SyntaxError)
            return [];
        throw error;
    }
}

export function readRepositories(workspace) {
    return {
        selected: localStorage.getItem(workspaceKey('selected-repository', workspace)),
        remembered: readRememberedRepositories(workspaceKey('remembered-repositories', workspace))
    };
}

export function rememberRepository(workspace, repository, select) {
    if (select)
        localStorage.setItem(workspaceKey('selected-repository', workspace), repository);
    const key = workspaceKey('remembered-repositories', workspace);
    const remembered = readRememberedRepositories(key);
    if (!remembered.includes(repository))
        localStorage.setItem(key, JSON.stringify([...remembered, repository]));
}

export function initializeSidebarResize(workbench, handle, workspace) {
    const key = workspaceKey('sidebar-width', workspace);
    const sidebar = handle.previousElementSibling;
    const style = getComputedStyle(workbench);
    const minimum = parseFloat(style.getPropertyValue('--sidebar-min-width'));
    const maximum = parseFloat(style.getPropertyValue('--sidebar-max-width'));
    const mainMinimum = parseFloat(style.getPropertyValue('--workbench-main-min-width'));
    let drag;

    try {
        const savedWidth = Number(localStorage.getItem(key));
        if (savedWidth >= minimum && savedWidth <= maximum)
            workbench.style.setProperty('--sidebar-width', `${savedWidth}px`);
    } catch {
        // The sidebar can still be resized when browser storage is unavailable.
    }

    function maxWidth() {
        return Math.max(minimum, Math.min(maximum, workbench.clientWidth - mainMinimum - handle.offsetWidth));
    }

    function updateSeparator() {
        if (handle.offsetWidth === 0)
            return;
        handle.setAttribute('aria-valuemin', minimum);
        handle.setAttribute('aria-valuemax', maxWidth());
        handle.setAttribute('aria-valuenow', sidebar.offsetWidth);
    }

    function resize(width) {
        const size = Math.max(minimum, Math.min(maxWidth(), Math.round(width)));
        workbench.style.setProperty('--sidebar-width', `${size}px`);
        updateSeparator();
        return size;
    }

    function saveWidth(width) {
        try {
            localStorage.setItem(key, String(width));
        } catch {
            // Keep the current width even if the preference could not be saved.
        }
    }

    handle.tabIndex = 0;
    updateSeparator();
    const observer = new ResizeObserver(() => {
        if (workbench.isConnected)
            updateSeparator();
        else
            observer.disconnect();
    });
    observer.observe(workbench);
    handle.addEventListener('keydown', event => {
        if (drag)
            return;
        let width = sidebar.offsetWidth;
        switch (event.key) {
            case 'ArrowLeft': width -= 16; break;
            case 'ArrowRight': width += 16; break;
            case 'Home': width = minimum; break;
            case 'End': width = maxWidth(); break;
            default: return;
        }
        event.preventDefault();
        saveWidth(resize(width));
    });
    handle.addEventListener('pointerdown', event => {
        if (event.button !== 0 || drag)
            return;
        event.preventDefault();
        handle.focus({ preventScroll: true });
        handle.setPointerCapture(event.pointerId);
        drag = { pointerId: event.pointerId, startX: event.clientX, startWidth: sidebar.offsetWidth };
        workbench.classList.add('resizing-sidebar');
    });
    handle.addEventListener('pointermove', event => {
        if (drag?.pointerId === event.pointerId)
            drag.width = resize(drag.startWidth + event.clientX - drag.startX);
    });
    handle.addEventListener('lostpointercapture', event => {
        if (drag?.pointerId !== event.pointerId)
            return;
        if (drag.width !== undefined)
            saveWidth(drag.width);
        drag = undefined;
        workbench.classList.remove('resizing-sidebar');
    });
}
