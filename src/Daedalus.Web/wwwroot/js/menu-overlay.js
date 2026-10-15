// The phone menu, open over the page: the focus moves into it and stays there, Escape asks the layout to close it, and
// on release the focus goes back to the menu toggle.
const focusable = 'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), ' +
    'textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

export function trap(dotnetRef) {
    const menu = document.getElementById('main-menu');
    const items = () => menu === null
        ? []
        : Array.from(menu.querySelectorAll(focusable)).filter(e => e.getClientRects().length > 0);

    const onKeyDown = e => {
        if (e.key === 'Escape') {
            e.preventDefault();
            // The .NET side may already be disposed; swallow the rejected promise.
            dotnetRef.invokeMethodAsync('CloseMenu').catch(() => {});
            return;
        }

        if (e.key !== 'Tab') {
            return;
        }

        const list = items();
        if (list.length === 0) {
            return;
        }

        // The menu moves the focus itself, so an element Radzen made focusable but this list does not name never
        // lets it out: the next stop is the one after the focused element's position, wrapping at either end.
        e.preventDefault();
        let current = list.indexOf(document.activeElement);
        if (current === -1) {
            current = list.findIndex(item => item.contains(document.activeElement));
        }
        const step = e.shiftKey ? -1 : 1;
        const next = current === -1
            ? (e.shiftKey ? list.length - 1 : 0)
            : (current + step + list.length) % list.length;
        list[next].focus();
    };

    document.addEventListener('keydown', onKeyDown, true);
    const first = items()[0];
    if (first) {
        first.focus();
    }

    return {
        release(restoreFocus) {
            document.removeEventListener('keydown', onKeyDown, true);
            if (restoreFocus) {
                const toggle = document.querySelector('.rz-sidebar-toggle');
                if (toggle) {
                    toggle.focus();
                }
            }
        }
    };
}
