import { ApiError, type ProblemDetails } from "@/lib/errors";

/** Anonymous auth endpoints (no bearer token, no refresh): a plain same-origin POST is all they need. */
async function post(path: string, body: unknown): Promise<void> {
  const response = await fetch(path, {
    method: "POST",
    credentials: "same-origin",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });

  if (!response.ok) {
    const problem = (await response.json().catch(() => ({ status: response.status }))) as ProblemDetails;
    throw new ApiError(response.status, problem);
  }
}

/** `captchaToken` is Turnstile's (spec 12); `website` is the honeypot, empty for people. */
export const register = (body: { fullName: string; email: string; password: string; captchaToken: string | null; website: string }) =>
  post("/api/v1/auth/register", body);

export const confirmEmail = (body: { userId: string; token: string }) => post("/api/v1/auth/confirm-email", body);

export const forgotPassword = (body: { email: string }) => post("/api/v1/auth/forgot-password", body);

export const resetPassword = (body: { email: string; token: string; newPassword: string }) =>
  post("/api/v1/auth/reset-password", body);
