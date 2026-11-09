type Env = { NODE_ENV?: string; ENABLE_DESIGN_PAGE?: string; ENABLE_MOCK_GATEWAY?: string };

/** `/design` is a dev tool; CI forces it on in a production build to run Lighthouse and axe against it. */
export function isDesignPageEnabled(env: Env = process.env): boolean {
  return env.NODE_ENV !== "production" || env.ENABLE_DESIGN_PAGE === "1";
}

/**
 * `/mock-gateway/[sessionId]` stands in for a payment provider (spec 08): development and demos only. A production
 * build shows it only with `ENABLE_MOCK_GATEWAY=1` (read per request, so a demo deployment can switch it on), and the
 * api answers 404 to its calls anyway unless `Payments:Mock:Enabled` is on there too.
 */
export function isMockGatewayEnabled(env: Env = process.env): boolean {
  return env.NODE_ENV !== "production" || env.ENABLE_MOCK_GATEWAY === "1";
}
