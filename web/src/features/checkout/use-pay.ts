"use client";

import { useRef, useState } from "react";
import { createKeyKeeper } from "@/lib/checkout/idempotency";
import { navigateTo } from "@/lib/checkout/navigate";
import { payOrder } from "@/lib/checkout/orders-api";

/**
 * "Thanh toán" (spec 08): `POST /orders/{id}/pay`, then off to the gateway. The Idempotency-Key is kept for as long
 * as the api has not answered for good (double click, network blip, 503), so those retries share one attempt, and
 * dropped after any stored answer: a retry after a refusal runs again instead of replaying a dead session URL (C2).
 * A new visit to the page (for example "Thử lại" after a failed payment) starts with a new key.
 */
export function usePay(orderId: string) {
  const keys = useRef(createKeyKeeper());
  const running = useRef(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);

  const pay = async () => {
    if (running.current) {
      return;
    }

    running.current = true;
    setBusy(true);
    setError(null);
    try {
      const { redirectUrl } = await payOrder(orderId, keys.current.acquire());
      keys.current.settle(null);
      // Stay busy (and refuse a second click): the page is about to be replaced by the gateway.
      navigateTo(redirectUrl);
    } catch (e) {
      keys.current.settle(e);
      setError(e);
      setBusy(false);
      running.current = false;
    }
  };

  return { pay, busy, error };
}
