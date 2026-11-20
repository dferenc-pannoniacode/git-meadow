const openPointerMenu = '.context-menu[popover="manual"]:popover-open';
const previousFocus = new WeakMap();

// Firefox on macOS can open the menu on mouse-down. Only a new press should dismiss it.
document.addEventListener("pointerdown", event => {
    const menu = document.querySelector(openPointerMenu);
    if (menu && !menu.contains(event.target)) menu.hidePopover();
}, true);

document.addEventListener("keydown", event => {
    if (event.key !== "Escape" || event.defaultPrevented) return;
    const menu = document.querySelector(openPointerMenu);
    if (!menu) return;

    const restoreFocus = menu.contains(document.activeElement);
    menu.hidePopover();
    if (restoreFocus) previousFocus.get(menu)?.focus({ preventScroll: true });
    event.preventDefault();
});

export function show(menu, x, y, anchor) {
    if (!menu?.isConnected) return;

    if (menu.popover === "manual") previousFocus.set(menu, document.activeElement);
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
