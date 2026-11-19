export function show(menu, x, y, anchor) {
    if (!menu?.isConnected) return;

    menu.showPopover();

    const gutter = 8;
    const { width, height } = menu.getBoundingClientRect();
    if (anchor) {
        const rect = anchor.getBoundingClientRect();
        x = rect.right - width;
        y = rect.bottom + 4;
    }
    const { clientWidth, clientHeight } = document.documentElement;
    menu.style.left = `${Math.max(gutter, Math.min(x, clientWidth - width - gutter))}px`;
    menu.style.top = `${Math.max(gutter, Math.min(y, clientHeight - height - gutter))}px`;
    menu.querySelector("button")?.focus({ preventScroll: true });
}
