// Removes the OIDC user the authentication library stores in sessionStorage, under 'oidc.user:<authority>:<client id>'.
// A token refresh that failed leaves it there, and the authentication state goes on reading it as a signed-in user.
export function clearStoredUser() {
    const keys = [];
    for (let i = 0; i < sessionStorage.length; i++) {
        const key = sessionStorage.key(i);
        if (key && key.startsWith('oidc.user:')) {
            keys.push(key);
        }
    }
    keys.forEach(key => sessionStorage.removeItem(key));
}
