// Holds a task dialog open while it submits. Radzen closes a dialog on Escape through a document listener and on its
// title bar's close button through a Blazor click handler; both are stopped here, in the window's capture phase, so
// neither fires while a create or a save is in flight. The close button says it is unavailable meanwhile.

const closeButton = '.rz-dialog-titlebar-close';

function block(event) {
    const closing = event.type === 'keydown'
        ? event.key === 'Escape'
        : event.target instanceof Element && event.target.closest(closeButton) !== null;
    if (closing) {
        event.stopPropagation();
        event.preventDefault();
    }
}

function markClose(disabled) {
    for (const button of document.querySelectorAll(closeButton)) {
        if (disabled) {
            button.setAttribute('aria-disabled', 'true');
        } else {
            button.removeAttribute('aria-disabled');
        }
    }
}

let held = false;

/** Stops Escape and the close button from closing the dialog until release is called. */
export function hold() {
    if (held) {
        return;
    }

    held = true;
    window.addEventListener('keydown', block, true);
    window.addEventListener('click', block, true);
    markClose(true);
}

/** Lets Escape and the close button close the dialog again. Safe to call when not held. */
export function release() {
    if (!held) {
        return;
    }

    held = false;
    window.removeEventListener('keydown', block, true);
    window.removeEventListener('click', block, true);
    markClose(false);
}
