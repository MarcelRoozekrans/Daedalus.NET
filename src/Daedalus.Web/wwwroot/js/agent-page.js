// Agent page interop: a document-level Escape listener so "stop the turn" works wherever focus is
// (a wrapper-div keydown only fires while the wrapper subtree has focus).
export function registerEscape(dotnetRef) {
    const handler = e => {
        if (e.key === 'Escape') {
            dotnetRef.invokeMethodAsync('OnEscape');
        }
    };
    document.addEventListener('keydown', handler);
    return {
        dispose() {
            document.removeEventListener('keydown', handler);
        }
    };
}
