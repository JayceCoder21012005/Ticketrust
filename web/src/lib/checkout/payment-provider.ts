/** The payment provider the web was built for (spec 18), from the `NEXT_PUBLIC_PAYMENT_PROVIDER` build argument. Anything else is the mock. */
export type PaymentProvider = "mock" | "momo";

export function paymentProvider(): PaymentProvider {
  return process.env.NEXT_PUBLIC_PAYMENT_PROVIDER === "momo" ? "momo" : "mock";
}

/** MoMo's own page of test wallets and cards for its sandbox (MoMo owns the numbers, so they are not copied here). */
export const MOMO_TEST_GUIDE_URL = "https://developers.momo.vn/v3/docs/payment/onboarding/test-instructions/";
