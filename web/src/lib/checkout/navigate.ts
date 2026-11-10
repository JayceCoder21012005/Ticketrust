/** A full-page navigation (the payment gateway is another site). A seam so tests can observe it. */
export function navigateTo(url: string): void {
  window.location.assign(url);
}
