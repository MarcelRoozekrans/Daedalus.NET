// Test mode only: imported by AlwaysAuthenticatedStateProvider, never by the OIDC build. A browser test changes the
// user's roles without a reload by writing the roles key from another page of the same origin, which raises the
// 'storage' event here; the provider then reads the roles again and announces the new authentication state.
export function watchRoles(dotnetRef, key) {
    const handler = e => {
        if (e.key === key) {
            // The .NET side may already be disposed; swallow the rejected promise.
            dotnetRef.invokeMethodAsync('OnRolesChanged').catch(() => {});
        }
    };
    window.addEventListener('storage', handler);
    return {
        dispose() {
            window.removeEventListener('storage', handler);
        }
    };
}
