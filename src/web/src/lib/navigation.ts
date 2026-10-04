// Full-page navigation to another site (Stripe). A module of its own so tests can replace it.
export function leaveTo(url: string) {
  window.location.assign(url)
}
