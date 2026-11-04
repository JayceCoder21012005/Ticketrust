import { act, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { clearSession } from "@/lib/api/session";
import { OrganizerGate } from "./organizer-gate";
import { organizerUser, renderWithProviders, resetApi, signIn } from "./test-utils";

vi.mock("next/navigation", () => ({ useRouter: () => ({ replace: vi.fn(), push: vi.fn() }) }));

beforeEach(resetApi);
afterEach(() => act(() => clearSession()));

describe("OrganizerGate", () => {
  it("tells a customer they need an organizer account", () => {
    signIn(organizerUser(["Customer"]));

    renderWithProviders(<OrganizerGate>bí mật</OrganizerGate>);

    expect(screen.getByRole("heading", { name: "Bạn cần tài khoản nhà tổ chức" })).toBeInTheDocument();
    expect(screen.queryByText("bí mật")).not.toBeInTheDocument();
  });

  it("shows the organizer area to an organizer", () => {
    signIn();

    renderWithProviders(<OrganizerGate>khu nhà tổ chức</OrganizerGate>);

    expect(screen.getByText("khu nhà tổ chức")).toBeInTheDocument();
  });
});
