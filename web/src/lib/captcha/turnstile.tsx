"use client";

import { useEffect, useRef } from "react";

/**
 * Cloudflare Turnstile (spec 12). With `NEXT_PUBLIC_TURNSTILE_SITE_KEY` set the widget is loaded on first use (lazily, never on
 * pages that do not need it) and solves itself in managed/invisible mode when it can. Without a site key (dev, unit and e2e
 * builds) nothing is loaded and the token is Cloudflare's test token, which only the api's fake verifier accepts: a production
 * api (Turnstile provider) refuses it, so a build that forgot the key fails loudly with `captcha.failed`.
 */
export const TEST_TOKEN = "XXXX.DUMMY.TOKEN.XXXX";

const SITE_KEY = process.env.NEXT_PUBLIC_TURNSTILE_SITE_KEY ?? "";
const SCRIPT_URL = "https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit";

type TurnstileApi = {
  render: (
    container: HTMLElement,
    options: {
      sitekey: string;
      appearance?: "always" | "execute" | "interaction-only";
      callback: (token: string) => void;
      "expired-callback"?: () => void;
      "error-callback"?: () => void;
    },
  ) => string;
  reset: (id: string) => void;
  remove: (id: string) => void;
};

declare global {
  interface Window {
    turnstile?: TurnstileApi;
  }
}

let loading: Promise<TurnstileApi> | null = null;

function loadTurnstile(): Promise<TurnstileApi> {
  if (window.turnstile) return Promise.resolve(window.turnstile);
  loading ??= new Promise<TurnstileApi>((resolve, reject) => {
    const script = document.createElement("script");
    script.src = SCRIPT_URL;
    script.async = true;
    script.onload = () => (window.turnstile ? resolve(window.turnstile) : reject(new Error("Turnstile did not load")));
    script.onerror = () => {
      loading = null;
      reject(new Error("Turnstile did not load"));
    };
    document.head.appendChild(script);
  });
  return loading;
}

/**
 * Renders the widget and reports each fresh token (`null` when it expires or errors: a token is single-use and short-lived).
 * Change `resetKey` after a submit to get a new token for the next one.
 */
export function Turnstile({ onToken, resetKey = 0 }: { onToken: (token: string | null) => void; resetKey?: number }) {
  const container = useRef<HTMLDivElement>(null);
  const callback = useRef(onToken);

  useEffect(() => {
    callback.current = onToken;
  });

  useEffect(() => {
    if (!SITE_KEY) {
      callback.current(TEST_TOKEN);
      return;
    }

    let id: string | null = null;
    let cancelled = false;
    callback.current(null);
    loadTurnstile()
      .then((api) => {
        if (cancelled || !container.current) return;
        id = api.render(container.current, {
          sitekey: SITE_KEY,
          appearance: "interaction-only",
          callback: (token) => callback.current(token),
          "expired-callback": () => callback.current(null),
          "error-callback": () => callback.current(null),
        });
      })
      .catch(() => callback.current(null));
    return () => {
      cancelled = true;
      if (id) window.turnstile?.remove(id);
    };
  }, [resetKey]);

  return SITE_KEY ? <div ref={container} className="flex min-h-0 justify-center" /> : null;
}
