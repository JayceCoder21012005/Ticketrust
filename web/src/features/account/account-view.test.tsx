import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider } from "@/components/auth/auth-provider";
import { clearSession, getSession, setSession } from "@/lib/api/session";
import { AccountView } from "./account-view";

const replace = vi.fn();
vi.mock("next/navigation", () => ({ useRouter: () => ({ replace, push: vi.fn(), refresh: vi.fn() }) }));

const me = {
  id: "0193",
  email: "an@example.com",
  fullName: "Nguyễn An",
  phoneNumber: null,
  avatarUrl: null,
  emailConfirmed: false,
  roles: ["Customer"],
};

type Route = (request: Request) => Response | Promise<Response>;
const routes = new Map<string, Route>();
const calls: Request[] = [];
const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });

const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
  const request = input instanceof Request ? input : new Request(new URL(String(input), "http://localhost:3000"), init);
  calls.push(request.clone());
  const route = routes.get(`${request.method} ${new URL(request.url).pathname}`);
  return route ? route(request) : json({ status: 404 }, 404);
});

const callsTo = (method: string, path: string) => calls.filter((r) => r.method === method && new URL(r.url).pathname === path);

function renderAccount() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <AuthProvider>
        <AccountView />
      </AuthProvider>
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.stubGlobal("fetch", fetchMock);
  act(() => setSession({ accessToken: "jwt", expiresAt: new Date(Date.now() + 600_000).toISOString(), user: me }));
  routes.set("GET /api/v1/me", () => json(me));
});

afterEach(() => {
  act(() => clearSession());
  routes.clear();
  calls.length = 0;
  fetchMock.mockClear();
  replace.mockReset();
  vi.unstubAllGlobals();
});

describe("AccountView", () => {
  it("loads the profile with the bearer token and reminds an unconfirmed user", async () => {
    renderAccount();

    expect(await screen.findByDisplayValue("Nguyễn An")).toBeInTheDocument();
    expect(screen.getByText("an@example.com")).toBeInTheDocument();
    expect(screen.getByText(/Email chưa được xác nhận/)).toBeInTheDocument();
    expect(callsTo("GET", "/api/v1/me")[0].headers.get("Authorization")).toBe("Bearer jwt");
  });

  it("saves the profile and updates the signed-in user", async () => {
    routes.set("PUT /api/v1/me", async (request) => json({ ...me, ...(await request.json()) }));
    renderAccount();

    const name = await screen.findByLabelText("Họ và tên");
    await userEvent.clear(name);
    await userEvent.type(name, "Nguyễn Bình");
    await userEvent.type(screen.getByLabelText("Số điện thoại"), "0901234567");
    await userEvent.click(screen.getByRole("button", { name: "Lưu thay đổi" }));

    expect(await screen.findByText("Đã lưu thông tin.")).toBeInTheDocument();
    expect(await callsTo("PUT", "/api/v1/me")[0].json()).toEqual({ fullName: "Nguyễn Bình", phoneNumber: "0901234567", avatarUrl: null });
    expect(getSession()?.user.fullName).toBe("Nguyễn Bình");
  });

  it("validates the phone number before saving", async () => {
    renderAccount();

    await userEvent.type(await screen.findByLabelText("Số điện thoại"), "12345");
    await userEvent.click(screen.getByRole("button", { name: "Lưu thay đổi" }));

    expect(await screen.findByText("Số điện thoại không hợp lệ.")).toBeInTheDocument();
    expect(callsTo("PUT", "/api/v1/me")).toHaveLength(0);
  });

  it("uploads an avatar through the presigned url, then saves its public url", async () => {
    routes.set("POST /api/v1/me/avatar-upload-url", () =>
      json({ uploadUrl: "http://localhost:3000/api/v1/storage/uploads?token=abc", publicUrl: "http://localhost:3000/media/avatars/0193/a.png", expiresAt: "2026-11-03T10:10:00Z" }),
    );
    routes.set("PUT /api/v1/storage/uploads", () => new Response(null, { status: 204 }));
    routes.set("PUT /api/v1/me", async (request) => json({ ...me, ...(await request.json()) }));
    renderAccount();

    const file = new File([new Uint8Array(1024)], "me.png", { type: "image/png" });
    await userEvent.upload(await screen.findByLabelText("Đổi ảnh"), file);

    await waitFor(() => expect(callsTo("PUT", "/api/v1/me")).toHaveLength(1));
    expect(await callsTo("POST", "/api/v1/me/avatar-upload-url")[0].json()).toEqual({ contentType: "image/png", size: 1024 });
    const upload = callsTo("PUT", "/api/v1/storage/uploads")[0];
    expect(new URL(upload.url).search).toBe("?token=abc");
    expect(upload.headers.get("Content-Type")).toBe("image/png");
    expect((await callsTo("PUT", "/api/v1/me")[0].json()).avatarUrl).toBe("http://localhost:3000/media/avatars/0193/a.png");
  });

  it("refuses images over 2 MB without calling the api", async () => {
    renderAccount();

    const big = new File([new Uint8Array(2 * 1024 * 1024 + 1)], "big.png", { type: "image/png" });
    await userEvent.upload(await screen.findByLabelText("Đổi ảnh"), big);

    expect(await screen.findByText("Ảnh tối đa 2 MB.")).toBeInTheDocument();
    expect(callsTo("POST", "/api/v1/me/avatar-upload-url")).toHaveLength(0);
  });

  it("sends signed-out visitors to login with a returnUrl", async () => {
    act(() => clearSession());
    renderAccount();

    await waitFor(() => expect(replace).toHaveBeenCalledWith("/login?returnUrl=%2Faccount"));
  });

  it("signs out and goes home", async () => {
    routes.set("POST /api/v1/auth/logout", () => new Response(null, { status: 204 }));
    renderAccount();

    await userEvent.click(await screen.findByRole("button", { name: "Đăng xuất" }));

    await waitFor(() => expect(replace).toHaveBeenCalledWith("/"));
    expect(callsTo("POST", "/api/v1/auth/logout")).toHaveLength(1);
    expect(getSession()).toBeNull();
  });
});
