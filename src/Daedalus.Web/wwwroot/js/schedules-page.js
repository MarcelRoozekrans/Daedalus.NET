// Focus-management helper for the schedules diagnostics page. Row expansion and collapse must move
// focus deliberately (into the expanded region, back to the row on collapse) rather than losing it to
// <body>. ElementReference covers the "into the expanded region" case, but returning focus to a
// specific row after Radzen removes/re-renders the detail template is simplest by element id.

export function focusElementById(id) {
    document.getElementById(id)?.focus();
}
