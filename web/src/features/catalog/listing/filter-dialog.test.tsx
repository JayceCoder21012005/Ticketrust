import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { FilterDialog } from "./filter-dialog";

describe("FilterDialog", () => {
  const showModal = vi.fn(function (this: HTMLDialogElement) {
    this.setAttribute("open", "");
  });

  beforeEach(() => {
    // jsdom has no modal dialogs; emulate the open attribute.
    showModal.mockClear();
    HTMLDialogElement.prototype.showModal = showModal;
  });

  it("renders the panel only once opened, and shows how many filters are active", async () => {
    render(
      <FilterDialog activeCount={2}>
        <p>panel</p>
      </FilterDialog>,
    );
    expect(screen.queryByText("panel")).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Bộ lọc (2)" }));

    expect(screen.getByText("panel")).toBeInTheDocument();
    expect(showModal).toHaveBeenCalledOnce();
  });
});
