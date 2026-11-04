/** Organizer descriptions are plain text: blank lines separate paragraphs, nothing is ever rendered as HTML. */
export function toParagraphs(text: string): string[] {
  return text
    .split(/\n{2,}/)
    .map((paragraph) => paragraph.trim())
    .filter((paragraph) => paragraph.length > 0);
}
