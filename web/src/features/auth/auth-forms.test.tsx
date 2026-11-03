import { act, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { StrictMode, type ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider } from "@/components/auth/auth-provider";
import { clearSession } from "@/lib/api/session";
import { ForgotPasswordForm } from "./forgot-password-form";
import { LoginForm } from "./login-form";
import { RegisterForm } from "./register-form";
import { ResetPasswordForm } from "./reset-password-form";
import { VerifyEmail } from "./verify-email";

const replace = vi.fn();
vi.mock("next/navigation", () => ({ useRouter: () => ({ replace, push: vi.fn(), refresh: vi.fn() }) }));

const fetchMock = vi.fn<typeof fetch>();
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });
const loginResponse = {
  accessToken: "jwt",
  expiresAt: new Date(Date.now() + 600_000).toISOString(),
  user: { id: "1", email: "an@example.com", fullName: "An", phoneNumber: null, avatarUrl: null, emailConfirmed: true, roles: ["Customer"] },
};

const withAuth = (ui: ReactNode) => render(<AuthProvider>{ui}</AuthProvider>);
const requestBody = (call = 0) => JSON.parse(String(fetchMock.mock.calls[call][1]?.body));

beforeEach(() => vi.stubGlobal("fetch", fetchMock));

afterEach(() => {
  act(() => clearSession());
  fetchMock.mockReset();
  replace.mockReset();
  vi.unstubAllGlobals();
});

describe("LoginForm", () => {
  it("validates inline before calling the api", async () => {
    withAuth(<LoginForm returnUrl={null} />);

    await userEvent.click(screen.getByRole("button", { name: "Đăng nhập" }));

    expect(await screen.findByText("Vui lòng nhập email.")).toBeInTheDocument();
    expect(screen.getByText("Vui lòng nhập mật khẩu.")).toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("signs in and returns to the same-origin returnUrl", async () => {
    fetchMock.mockResolvedValue(json(loginResponse));
    withAuth(<LoginForm returnUrl="/account?tab=1" />);

    await userEvent.type(screen.getByLabelText("Email"), "an@example.com");
    await userEvent.type(screen.getByLabelText("Mật khẩu"), "matkhau123{Enter}");

    await waitFor(() => expect(replace).toHaveBeenCalledWith("/account?tab=1"));
    expect(requestBody()).toEqual({ email: "an@example.com", password: "matkhau123" });
  });

  it("ignores an off-site returnUrl", async () => {
    fetchMock.mockResolvedValue(json(loginResponse));
    withAuth(<LoginForm returnUrl="https://evil.com" />);

    await userEvent.type(screen.getByLabelText("Email"), "an@example.com");
    await userEvent.type(screen.getByLabelText("Mật khẩu"), "matkhau123{Enter}");

    await waitFor(() => expect(replace).toHaveBeenCalledWith("/"));
  });

  it("shows the friendly message for wrong credentials and lets the user retry", async () => {
    fetchMock.mockResolvedValue(json({ code: "auth.invalid_credentials", status: 401 }, 401));
    withAuth(<LoginForm returnUrl={null} />);

    await userEvent.type(screen.getByLabelText("Email"), "an@example.com");
    await userEvent.type(screen.getByLabelText("Mật khẩu"), "wrongpass1{Enter}");

    expect(await screen.findByText("Email hoặc mật khẩu không đúng.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Đăng nhập" })).toBeEnabled();
    expect(replace).not.toHaveBeenCalled();
  });

  it("disables the submit button while the request is pending", async () => {
    fetchMock.mockReturnValue(new Promise(() => {}));
    withAuth(<LoginForm returnUrl={null} />);

    await userEvent.type(screen.getByLabelText("Email"), "an@example.com");
    await userEvent.type(screen.getByLabelText("Mật khẩu"), "matkhau123{Enter}");

    expect(await screen.findByRole("button", { name: "Đang đăng nhập…" })).toBeDisabled();
  });

  it("shows and hides the password", async () => {
    withAuth(<LoginForm returnUrl={null} />);
    const password = screen.getByLabelText("Mật khẩu");

    await userEvent.click(screen.getByRole("button", { name: "Hiện mật khẩu" }));
    expect(password).toHaveAttribute("type", "text");
    await userEvent.click(screen.getByRole("button", { name: "Ẩn mật khẩu" }));
    expect(password).toHaveAttribute("type", "password");
  });
});

describe("RegisterForm", () => {
  const fill = async (password: string) => {
    await userEvent.type(screen.getByLabelText("Họ và tên"), "Nguyễn An");
    await userEvent.type(screen.getByLabelText("Email"), "an@example.com");
    await userEvent.type(screen.getByLabelText("Mật khẩu"), password);
  };

  it("rates the password as the user types", async () => {
    render(<RegisterForm />);

    await userEvent.type(screen.getByLabelText("Mật khẩu"), "MatKhau123!@#xyz");

    expect(screen.getByText("Độ mạnh: Rất mạnh")).toBeInTheDocument();
  });

  it("rejects a weak password before calling the api", async () => {
    render(<RegisterForm />);

    await fill("onlyletters");
    await userEvent.click(screen.getByRole("button", { name: "Tạo tài khoản" }));

    expect(await screen.findByText("Mật khẩu cần có chữ số.")).toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("asks the user to check their inbox once the api accepts the registration", async () => {
    fetchMock.mockResolvedValue(new Response(null, { status: 202 }));
    render(<RegisterForm />);

    await fill("matkhau123");
    await userEvent.click(screen.getByRole("button", { name: "Tạo tài khoản" }));

    expect(await screen.findByRole("heading", { name: "Kiểm tra hộp thư" })).toBeInTheDocument();
    expect(screen.getByText(/an@example\.com/)).toBeInTheDocument();
    expect(String(fetchMock.mock.calls[0][0])).toBe("/api/v1/auth/register");
    expect(requestBody()).toEqual({ fullName: "Nguyễn An", email: "an@example.com", password: "matkhau123" });
  });

  it("shows server-side errors", async () => {
    fetchMock.mockResolvedValue(json({ code: "auth.weak_password", status: 400 }, 400));
    render(<RegisterForm />);

    await fill("matkhau123");
    await userEvent.click(screen.getByRole("button", { name: "Tạo tài khoản" }));

    expect(await screen.findByText("Mật khẩu cần ít nhất 10 ký tự, gồm cả chữ và số.")).toBeInTheDocument();
  });
});

describe("ForgotPasswordForm", () => {
  it("shows the same confirmation whether or not the email exists", async () => {
    fetchMock.mockResolvedValue(new Response(null, { status: 202 }));
    render(<ForgotPasswordForm />);

    await userEvent.type(screen.getByLabelText("Email"), "ghost@example.com{Enter}");

    expect(await screen.findByText(/Nếu ghost@example\.com có tài khoản/)).toBeInTheDocument();
    expect(String(fetchMock.mock.calls[0][0])).toBe("/api/v1/auth/forgot-password");
  });
});

describe("ResetPasswordForm", () => {
  const fill = async (password: string, confirm: string) => {
    await userEvent.type(screen.getByLabelText("Mật khẩu mới"), password);
    await userEvent.type(screen.getByLabelText("Nhập lại mật khẩu"), confirm);
    await userEvent.click(screen.getByRole("button", { name: "Đặt lại mật khẩu" }));
  };

  it("requires the confirmation to match", async () => {
    render(<ResetPasswordForm email="an@example.com" token="tok" />);

    await fill("matkhaumoi1", "matkhaukhac1");

    expect(await screen.findByText("Mật khẩu nhập lại không khớp.")).toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("resets with the emailed token and points to login", async () => {
    fetchMock.mockResolvedValue(new Response(null, { status: 204 }));
    render(<ResetPasswordForm email="an@example.com" token="tok+/=" />);

    await fill("matkhaumoi1", "matkhaumoi1");

    expect(await screen.findByRole("link", { name: "Đăng nhập" })).toHaveAttribute("href", "/login");
    expect(requestBody()).toEqual({ email: "an@example.com", token: "tok+/=", newPassword: "matkhaumoi1" });
  });

  it("explains an expired link", async () => {
    fetchMock.mockResolvedValue(json({ code: "auth.invalid_token", status: 400 }, 400));
    render(<ResetPasswordForm email="an@example.com" token="old" />);

    await fill("matkhaumoi1", "matkhaumoi1");

    expect(await screen.findByText(/Liên kết không hợp lệ hoặc đã hết hạn/)).toBeInTheDocument();
  });
});

describe("VerifyEmail", () => {
  it("confirms exactly once, even under StrictMode", async () => {
    fetchMock.mockResolvedValue(new Response(null, { status: 204 }));
    render(
      <StrictMode>
        <VerifyEmail userId="0193" token="tok" />
      </StrictMode>,
    );

    expect(await screen.findByRole("heading", { name: "Email đã được xác nhận" })).toBeInTheDocument();
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(requestBody()).toEqual({ userId: "0193", token: "tok" });
  });

  it("explains a broken or expired link", async () => {
    fetchMock.mockResolvedValue(json({ code: "auth.invalid_token", status: 400 }, 400));
    render(<VerifyEmail userId="0193" token="bad" />);

    expect(await screen.findByRole("heading", { name: "Không xác nhận được email" })).toBeInTheDocument();
  });

  it("does not call the api when the link is incomplete", async () => {
    render(<VerifyEmail userId={null} token={null} />);

    expect(await screen.findByRole("heading", { name: "Không xác nhận được email" })).toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalled();
  });
});
