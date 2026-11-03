/** Mirrors the api's PasswordRules (spec 03): 10+ characters, a letter, a digit. */
export const PASSWORD_MIN_LENGTH = 10;

/** zod-free on purpose: the meter renders on first load, the zod schemas load on first validation. */
export function meetsPasswordRules(value: string): boolean {
  return value.length >= PASSWORD_MIN_LENGTH && /\p{L}/u.test(value) && /\d/.test(value);
}

const LABELS = ["", "Yếu", "Trung bình", "Mạnh", "Rất mạnh"] as const;

/**
 * Strength hint for the meter. Anything the api would reject is "weak" however long it is, so the meter
 * never encourages a password that fails on submit.
 */
export function passwordStrength(value: string): { score: 0 | 1 | 2 | 3 | 4; label: string } {
  if (!value) {
    return { score: 0, label: LABELS[0] };
  }

  if (!meetsPasswordRules(value)) {
    return { score: 1, label: LABELS[1] };
  }

  const mixedCase = /\p{Ll}/u.test(value) && /\p{Lu}/u.test(value);
  const longWithSymbols = value.length >= 12 && /[^\p{L}\d]/u.test(value);
  const score = Math.min(4, 2 + Number(mixedCase) + Number(longWithSymbols)) as 2 | 3 | 4;
  return { score, label: LABELS[score] };
}
