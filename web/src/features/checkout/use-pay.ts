"use client";

import { useEffect, useRef, useState } from "react";
import { createKeyKeeper } from "@/lib/checkout/idempotency";
import { navigateTo } from "@/lib/checkout/navigate";
import { payOrder } from "@/lib/checkout/orders-api";
import { isSafeRedirect } from "@/lib/checkout/redirect";
import { ApiError } from "@/lib/errors";

/**
 * "Thanh toán" (spec 08): `POST /orders/{id}/pay`, then off to the gateway. The Idempotency-Key is kept for as long
 * as the api has not answered for good (double click, network blip, 503), so those retries share one attempt, and
 * dropped after any stored answer: a retry after a refusal runs again instead of replaying a dead session URL (C2).
 * A new visit to the page (for example "Thử lại" after a failed payment) starts with a new key.
 *
 * `onFailed` sees each failed attempt once, when it happens (not on every render).
 */
export function usePay(orderId: string, onFailed?: (error: unknown) => void) {
  const [keys] = useState(createKeyKeeper);
  const running = useRef(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);

  useEffect(() => {
    // Back from the gateway can restore this page from the back/forward cache with the button still locked.
    const onShow = (event: PageTransitionEvent) => {
      if (event.persisted) {
        running.current = false;
        setBusy(false);
      }
    };
    window.addEventListener("pageshow", onShow);
    return () => window.removeEventListener("pageshow", onShow);
  }, []);

  const fail = (e: unknown) => {
    setError(e);
    setBusy(false);
    running.current = false;
    onFailed?.(e);
  };

  const pay = async () => {
    if (running.current) {
      return;
    }

    running.current = true;
    setBusy(true);
    setError(null);
    let redirectUrl: string;
    try {
      ({ redirectUrl } = await payOrder(orderId, keys.acquire()));
    } catch (e) {
      keys.settle(e);
      fail(e);
      return;
    }

    keys.settle(null);
    if (!isSafeRedirect(redirectUrl)) {
      fail(new ApiError(502, { code: "payment.invalid_redirect" }));
      return;
    }

    // Stay busy (and refuse a second click): the page is about to be replaced by the gateway.
    navigateTo(redirectUrl);
  };

  return { pay, busy, error };
}
