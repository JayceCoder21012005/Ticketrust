import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, fireEvent, render, screen } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { TooltipProvider } from "@/components/ui/tooltip";
import { clearSession } from "@/lib/api/session";
import { callsTo, json, resetApi, routes, signIn } from "../test-utils";
import type { OrganizerEvent, TierInput } from "../types";
import { eventFixture } from "./steps.fixtures";
import { WizardView } from "./wizard-view";

vi.mock("next/navigation", () => ({ usePathname: () => "/organizer/events/e1/tiers", useRouter: () => ({ push: vi.fn(), replace: vi.fn() }) }));

const standing = { id: "t1", name: "Khu đứng", price: 500_000, kind: "GeneralAdmission" as const, capacity: 300, color: "#0f766e" };

/** The api as this tab sees it: every save bumps the version, a stale one is refused. Saves can be held in flight. */
let server: OrganizerEvent;
let held: (() => void)[] = [];
let holdNext = 0;
let failNext: Response | null = null;

async function save(request: Request, apply: (body: Record<string, unknown>) => Partial<OrganizerEvent>) {
  const body = (await request.clone().json()) as Record<string, unknown>;
  if (holdNext > 0) {
    holdNext--;
    await new Promise<void>((resolve) => held.push(resolve));
  }

  if (failNext) {
    const response = failNext;
    failNext = null;
    return response;
  }

  if (body.version !== server.version) {
    return json({ status: 409, code: "event.concurrent_edit" }, 409);
  }

  server = { ...server, ...apply(body), version: String(Number(server.version) + 1) };
  return json(server);
}

const versionsSentTo = async (path: string) =>
  Promise.all(callsTo("PUT", `/api/v1/organizer/events/e1/${path}`).map(async (r) => ((await r.clone().json()) as { version: string }).version));

const settle = () => act(() => vi.advanceTimersByTimeAsync(50));
const debounce = () => act(() => vi.advanceTimersByTimeAsync(800));
const release = async () => {
  await act(async () => held.shift()?.());
  await settle();
  await settle();
};

function setup(step: "tiers" | "info") {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={queryClient}>
      <TooltipProvider>{children}</TooltipProvider>
    </QueryClientProvider>
  );
  // The app router remounts the page (and the frame) on every step: the key stands in for that.
  const view = render(<WizardView key={step} eventId="e1" step={step} />, { wrapper });
  return { goTo: (next: "tiers" | "info") => view.rerender(<WizardView key={next} eventId="e1" step={next} />) };
}

const price = () => screen.getByLabelText("Giá hạng Khu đứng");
const typePrice = (value: string) => fireEvent.change(price(), { target: { value } });

beforeEach(() => {
  vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "setInterval", "clearInterval", "Date"] });
  resetApi();
  signIn();
  window.matchMedia = vi.fn().mockImplementation(() => ({ matches: false, addEventListener: vi.fn(), removeEventListener: vi.fn() }));
  server = eventFixture({ tiers: [standing] });
  held = [];
  holdNext = 0;
  failNext = null;
  routes.set("GET /api/v1/organizer/events/e1", () => json(server));
  routes.set("GET /api/v1/organizer/events/e1/checklist", () => json({ ready: false, failures: [] }));
  routes.set("GET /api/v1/organizer/venues", () => json([]));
  routes.set("PUT /api/v1/organizer/events/e1/tiers", (r) =>
    save(r, (body) => ({ tiers: (body.tiers as TierInput[]).map((t, i) => ({ ...t, id: t.id ?? `new-${i}` })) as OrganizerEvent["tiers"] })),
  );
  routes.set("PUT /api/v1/organizer/events/e1/info", (r) => save(r, (body) => ({ title: body.title as string })));
});
afterEach(() => {
  act(() => clearSession());
  vi.useRealTimers();
});

describe("WizardView saves", () => {
  it("sends the version the previous save returned when a change is queued behind it", async () => {
    setup("tiers");
    await settle();
    holdNext = 1;

    typePrice("600000");
    await debounce();
    typePrice("700000");
    await debounce();
    await release();

    expect(await versionsSentTo("tiers")).toEqual(["1", "2"]);
    expect(server.tiers[0].price).toBe(700_000);
  });

  it("updates a new tier, not creates it again, when its next save was queued before the ids came back", async () => {
    setup("tiers");
    await settle();
    holdNext = 1;

    fireEvent.click(screen.getByRole("button", { name: "Thêm hạng vé" }));
    fireEvent.change(screen.getByLabelText("Tên hạng mới 2"), { target: { value: "VIP" } });
    fireEvent.change(screen.getByLabelText("Loại hạng VIP"), { target: { value: "GeneralAdmission" } });
    fireEvent.change(screen.getByLabelText("Sức chứa hạng VIP"), { target: { value: "100" } });
    fireEvent.change(screen.getByLabelText("Giá hạng VIP"), { target: { value: "800000" } });
    await debounce();
    fireEvent.change(screen.getByLabelText("Giá hạng VIP"), { target: { value: "900000" } });
    await debounce();
    await release();

    const [, second] = await Promise.all(callsTo("PUT", "/api/v1/organizer/events/e1/tiers").map((r) => r.clone().json() as Promise<{ tiers: TierInput[] }>));
    expect(second.tiers.map((t) => t.id)).toEqual(["t1", "new-1"]);
  });

  it("saves a change made just before leaving the step with the newest version", async () => {
    const { goTo } = setup("tiers");
    await settle();
    holdNext = 1;

    typePrice("600000");
    await debounce();
    typePrice("700000");
    goTo("info");
    await settle();
    await release();

    expect(await versionsSentTo("tiers")).toEqual(["1", "2"]);
    expect(server.tiers[0].price).toBe(700_000);
  });

  it("runs the next step's save after the one still in flight from the step before", async () => {
    const { goTo } = setup("tiers");
    await settle();
    holdNext = 1;

    typePrice("600000");
    goTo("info");
    await settle();
    fireEvent.change(screen.getByLabelText("Tên sự kiện"), { target: { value: "Đêm nhạc mới" } });
    await debounce();
    expect(callsTo("PUT", "/api/v1/organizer/events/e1/info")).toHaveLength(0);

    await release();

    expect(await versionsSentTo("info")).toEqual(["2"]);
    expect(server).toMatchObject({ title: "Đêm nhạc mới", version: "3" });
  });

  it("saves with the reloaded version after another tab saved, once the step is opened again", async () => {
    const { goTo } = setup("tiers");
    await settle();
    typePrice("600000");
    await debounce();
    await settle();
    server = { ...server, tiers: [{ ...standing, price: 900_000 }], version: "7" }; // another tab saved

    goTo("info");
    await settle();
    goTo("tiers");
    await settle();
    await settle();
    expect(price()).toHaveValue("900000");
    typePrice("950000");
    await debounce();
    await settle();

    expect(await versionsSentTo("tiers")).toEqual(["1", "7"]);
    expect(server.tiers[0].price).toBe(950_000);
    expect(screen.getByRole("status")).not.toHaveTextContent("Sự kiện vừa được sửa ở nơi khác");
  });

  it("shows a failed save of a step already left in the header", async () => {
    const { goTo } = setup("tiers");
    await settle();
    holdNext = 1;
    failNext = json({ status: 500, code: "server.error" }, 500);

    typePrice("600000");
    goTo("info");
    await settle();
    await release();

    expect(screen.getByRole("status")).toHaveTextContent("Lưu thất bại");
  });

  it("drops the unsaved change when the organizer reloads after a conflict", async () => {
    setup("tiers");
    await settle();
    server = { ...server, tiers: [{ ...standing, price: 900_000 }], version: "7" }; // another tab saved

    typePrice("600000");
    await debounce();
    await settle();
    expect(screen.getByRole("status")).toHaveTextContent("Sự kiện vừa được sửa ở nơi khác");

    fireEvent.click(screen.getByRole("button", { name: "Tải lại" }));
    await settle();
    await settle();
    await debounce();

    expect(callsTo("PUT", "/api/v1/organizer/events/e1/tiers")).toHaveLength(1);
    expect(server.tiers[0].price).toBe(900_000);
    expect(price()).toHaveValue("900000");
  });
});
