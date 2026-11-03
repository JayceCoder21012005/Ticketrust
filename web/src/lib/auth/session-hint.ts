/** Set by the api at login next to the HttpOnly refresh cookie; non-secret, it only says "probably signed in". */
export const SESSION_HINT_COOKIE = "tr_session";

export function hasSessionHint(cookieString: string): boolean {
  return cookieString.split(";").some((cookie) => cookie.trim() === `${SESSION_HINT_COOKIE}=1`);
}
