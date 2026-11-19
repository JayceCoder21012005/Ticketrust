import { NextResponse, type NextRequest } from "next/server";
import { SESSION_HINT_COOKIE } from "@/lib/auth/session-hint";
import { isMockGatewayEnabled } from "@/lib/feature-flags";

const GUARDED_PREFIXES = ["/account", "/my-tickets", "/orders", "/checkout", "/queue", "/organizer", "/admin", "/gate", "/tickets", "/waitlist"];

/** The waiting room, the seat page and the buyer step of one event (specs 06, 07, 08): `/events/<slug>/queue|seats|checkout`. */
const EVENT_BUY_PATH = /^\/events\/[^/]+\/(queue|seats|checkout)\/?$/;

export function requiresLogin(pathname: string): boolean {
  return GUARDED_PREFIXES.some((prefix) => pathname === prefix || pathname.startsWith(`${prefix}/`)) || EVENT_BUY_PATH.test(pathname);
}

/**
 * UX only (spec 03): sends signed-out visitors of app areas to login instead of a flash of an empty page.
 * The refresh cookie is scoped to /api/v1/auth, so this reads the non-secret `tr_session` hint.
 * The api's authorization stays the real guard.
 */
export function proxy(request: NextRequest) {
  const { pathname, search } = request.nextUrl;
  // The mock payment gateway is dev/demo only: answer a real 404 (the page itself cannot, it streams) unless enabled.
  if (pathname.startsWith("/mock-gateway") && !isMockGatewayEnabled()) {
    return new NextResponse(null, { status: 404 });
  }

  if (!requiresLogin(pathname) || request.cookies.get(SESSION_HINT_COOKIE)?.value === "1") {
    return NextResponse.next();
  }

  const login = new URL("/login", request.url);
  login.searchParams.set("returnUrl", pathname + search);
  return NextResponse.redirect(login);
}

export const config = {
  matcher: [
    "/account/:path*",
    "/my-tickets/:path*",
    "/orders/:path*",
    "/checkout/:path*",
    "/queue/:path*",
    "/events/:slug/queue",
    "/events/:slug/seats",
    "/events/:slug/checkout",
    "/mock-gateway/:path*",
    "/organizer/:path*",
    "/admin/:path*",
    "/gate/:path*",
    "/tickets/:path*",
    "/waitlist/:path*",
  ],
};
