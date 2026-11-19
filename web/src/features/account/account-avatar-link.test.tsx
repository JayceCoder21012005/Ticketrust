import { act, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { clearSession, setSession } from "@/lib/api/session";
import { AccountAvatarLink } from "./account-avatar-link";

const session = (avatarUrl: string | null) => ({
  accessToken: "jwt",
  expiresAt: new Date(Date.now() + 600_000).toISOString(),
  user: { id: "1", email: "an@example.com", fullName: "Nguyễn An", phoneNumber: null, avatarUrl, emailConfirmed: true, roles: ["Customer"] },
});

afterEach(() => act(() => clearSession()));

describe("AccountAvatarLink", () => {
  it("shows the signed-in buyer's photo and links to the account", () => {
    act(() => setSession(session("http://localhost:3000/media/avatars/u/a.webp")));
    const { container } = render(<AccountAvatarLink />);

    expect(screen.getByRole("link", { name: "Tài khoản" })).toHaveAttribute("href", "/account");
    expect(container.querySelector("[data-slot='avatar']")).not.toBeNull();
  });

  it("shows the initial when there is no photo", () => {
    act(() => setSession(session(null)));
    render(<AccountAvatarLink />);

    expect(screen.getByText("N")).toBeInTheDocument();
  });

  it("keeps the plain account link until the session is restored", () => {
    render(<AccountAvatarLink />);

    expect(screen.getByRole("link", { name: "Tài khoản" })).toHaveAttribute("href", "/account");
    expect(screen.queryByText("N")).not.toBeInTheDocument();
  });
});
