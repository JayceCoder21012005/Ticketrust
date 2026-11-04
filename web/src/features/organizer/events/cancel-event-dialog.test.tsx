import { act, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { clearSession } from "@/lib/api/session";
import { callsTo, json, renderWithProviders, resetApi, routes, signIn } from "../test-utils";
import { eventFixture } from "../wizard/steps.fixtures";
import { CancelEventDialog } from "./cancel-event-dialog";

beforeEach(() => {
  resetApi();
  signIn();
  routes.set("POST /api/v1/organizer/events/e1/cancel", () => json(eventFixture({ status: "Cancelled" })));
});
afterEach(() => act(() => clearSession()));

describe("CancelEventDialog", () => {
  it("only cancels once the exact title is typed", async () => {
    const user = userEvent.setup();
    const onCancelled = vi.fn();
    renderWithProviders(<CancelEventDialog event={eventFixture({ title: "Đêm nhạc", status: "Published" })} onCancelled={onCancelled} />);

    await user.click(screen.getByRole("button", { name: "Huỷ sự kiện" }));
    expect(screen.getByText(/Người mua sẽ được hoàn tiền/)).toBeInTheDocument();
    const confirm = screen.getByRole("button", { name: "Xác nhận huỷ" });
    await user.type(screen.getByLabelText(/Gõ tên sự kiện/), "Đêm nhạ");
    expect(confirm).toBeDisabled();

    await user.type(screen.getByLabelText(/Gõ tên sự kiện/), "c");
    await user.click(confirm);

    await waitFor(() => expect(onCancelled).toHaveBeenCalled());
    expect(callsTo("POST", "/api/v1/organizer/events/e1/cancel")).toHaveLength(1);
  });

  it("does not mention refunds for a draft", async () => {
    const user = userEvent.setup();
    renderWithProviders(<CancelEventDialog event={eventFixture()} onCancelled={vi.fn()} />);

    await user.click(screen.getByRole("button", { name: "Huỷ sự kiện" }));

    expect(screen.queryByText(/hoàn tiền/)).not.toBeInTheDocument();
  });
});
