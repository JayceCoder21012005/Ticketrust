type Env = { NODE_ENV?: string; ENABLE_DESIGN_PAGE?: string };

/** `/design` is a dev tool; CI forces it on in a production build to run Lighthouse and axe against it. */
export function isDesignPageEnabled(env: Env = process.env): boolean {
  return env.NODE_ENV !== "production" || env.ENABLE_DESIGN_PAGE === "1";
}
