function selectionKey(workspace) {
    return `git-meadow:selected-repository:${new URL(document.baseURI).pathname}:${workspace}`;
}

export function readSelection(workspace) {
    return localStorage.getItem(selectionKey(workspace));
}

export function writeSelection(workspace, repository) {
    localStorage.setItem(selectionKey(workspace), repository);
}
