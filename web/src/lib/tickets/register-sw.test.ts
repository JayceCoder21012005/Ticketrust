import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const cache = vi.hoisted(() => ({ clearAll: vi.fn(async () => undefined) }));
vi.mock("./key-cache", () => ({ keyCache: cache }));

const { forgetOfflineTickets, registerTicketWorker } = await import("./register-sw");

const worker = (scriptURL: string) => ({ scriptURL, postMessage: vi.fn() });

function stubServiceWorker() {
  const active = worker("http://localhost:3000/sw.js");
  const registration = { active, waiting: null, installing: null, unregister: vi.fn(async () => true) };
  const other = { active: worker("http://localhost:3000/other-sw.js"), waiting: null, installing: null, unregister: vi.fn(async () => true) };
  const serviceWorker = {
    register: vi.fn(async () => registration),
    ready: Promise.resolve(registration),
    getRegistrations: vi.fn(async () => [registration, other]),
  };
  Object.defineProperty(navigator, "serviceWorker", { value: serviceWorker, configurable: true });
  return { serviceWorker, registration, other, active };
}

const setHint = (on: boolean) => {
  document.cookie = on ? "tr_session=1; path=/" : "tr_session=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/";
};

beforeEach(() => {
  cache.clearAll.mockClear();
  vi.stubGlobal("isSecureContext", true);
});

afterEach(() => {
  setHint(false);
  Reflect.deleteProperty(navigator, "serviceWorker");
  document.head.innerHTML = "";
  vi.unstubAllGlobals();
});

const flush = async () => {
  for (let i = 0; i < 5; i++) await Promise.resolve();
};

describe("registerTicketWorker", () => {
  it("registers /sw.js for the whole origin when the visitor has a session hint", async () => {
    const { serviceWorker, active } = stubServiceWorker();
    setHint(true);
    const script = document.createElement("script");
    script.src = "/_next/static/chunks/app-123.js";
    document.head.append(script, Object.assign(document.createElement("link"), { rel: "icon", href: "https://cdn.example.com/x.png" }));

    registerTicketWorker();
    await flush();

    expect(serviceWorker.register).toHaveBeenCalledWith("/sw.js", { scope: "/", updateViaCache: "none" });
    // The worker is told what this page already loaded: the page itself and same-origin static assets only.
    expect(active.postMessage).toHaveBeenCalledWith({ type: "warm", urls: [location.pathname, "/_next/static/chunks/app-123.js"] });
  });

  it("does nothing without a session hint, without service workers, or on an insecure origin", async () => {
    const { serviceWorker } = stubServiceWorker();
    registerTicketWorker();
    setHint(true);
    vi.stubGlobal("isSecureContext", false);
    registerTicketWorker();
    await flush();
    expect(serviceWorker.register).not.toHaveBeenCalled();

    Reflect.deleteProperty(navigator, "serviceWorker");
    vi.stubGlobal("isSecureContext", true);
    expect(() => registerTicketWorker()).not.toThrow();
  });

  it("swallows a failed registration", async () => {
    const { serviceWorker } = stubServiceWorker();
    serviceWorker.register.mockRejectedValue(new DOMException("blocked", "SecurityError"));
    setHint(true);

    registerTicketWorker();
    await flush();
  });
});

describe("forgetOfflineTickets", () => {
  it("clears the key and ticket caches, unregisters the wallet worker and deletes its caches only", async () => {
    const { registration, other } = stubServiceWorker();
    const names = ["ticketrush-wallet-shell-v1", "ticketrush-wallet-static-v1", "someone-else"];
    const deleted: string[] = [];
    vi.stubGlobal("caches", { keys: async () => names, delete: async (name: string) => deleted.push(name) > 0 });

    await forgetOfflineTickets();

    expect(cache.clearAll).toHaveBeenCalledTimes(1);
    expect(registration.unregister).toHaveBeenCalledTimes(1);
    expect(registration.active.postMessage).toHaveBeenCalledWith({ type: "retire" });
    expect(other.unregister).not.toHaveBeenCalled();
    expect(deleted).toEqual(["ticketrush-wallet-shell-v1", "ticketrush-wallet-static-v1"]);
  });

  it("still clears the keys when the worker api or the caches fail", async () => {
    const { serviceWorker } = stubServiceWorker();
    serviceWorker.getRegistrations.mockRejectedValue(new Error("nope"));
    vi.stubGlobal("caches", { keys: async () => Promise.reject(new Error("nope")), delete: vi.fn() });

    await expect(forgetOfflineTickets()).resolves.toBeUndefined();
    expect(cache.clearAll).toHaveBeenCalledTimes(1);
  });
});
