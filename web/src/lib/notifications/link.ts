/**
 * A notification's link as something safe to navigate to, or null. Only app-relative paths pass: one leading `/`,
 * no `//` (protocol-relative), no backslash (browsers read `/\host` as `//host`), no whitespace or control characters
 * (browsers drop tabs and newlines inside a URL, so `/\t/host` is `//host` too). The api only sends such paths; this is
 * defence in depth against a bad row or a tampered push.
 */
export function safeLink(link: string | null): string | null {
  if (link === null || !link.startsWith("/") || link.startsWith("//")) {
    return null;
  }

  return /[\\\s\u0000-\u001f\u007f]/.test(link) ? null : link;
}
