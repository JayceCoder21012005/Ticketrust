// zod/mini: same validation core as `zod`, but tree-shakeable. Forms load this module lazily on their first
// validation (see lazy-resolver.ts) so it stays out of first-load JS (170 KB budget, spec 02).
import * as z from "zod/mini";
import { PASSWORD_MIN_LENGTH } from "./password-strength";

/** Mirrors the api's PasswordRules (spec 03): 10+ characters, a letter, a digit, not the email. */
const password = z.string().check(
  z.minLength(PASSWORD_MIN_LENGTH, `Mật khẩu cần ít nhất ${PASSWORD_MIN_LENGTH} ký tự.`),
  z.regex(/\p{L}/u, "Mật khẩu cần có chữ cái."),
  z.regex(/\d/, "Mật khẩu cần có chữ số."),
);

const email = z.email("Email không hợp lệ.").check(z.maxLength(256));
const fullName = z
  .string()
  .check(z.trim(), z.minLength(1, "Vui lòng nhập họ tên."), z.maxLength(100, "Họ tên tối đa 100 ký tự."));

const notTheEmail = <T extends { email: string; password: string }>() =>
  z.refine<T>((v) => v.password.toLowerCase() !== v.email.toLowerCase(), {
    error: "Mật khẩu không được trùng với email.",
    path: ["password"],
  });

export const loginSchema = z.object({
  email: z.string().check(z.trim(), z.minLength(1, "Vui lòng nhập email.")),
  password: z.string().check(z.minLength(1, "Vui lòng nhập mật khẩu.")),
});

export const registerSchema = z.object({ fullName, email, password }).check(notTheEmail());

export const forgotPasswordSchema = z.object({ email });

export const resetPasswordSchema = z
  .object({ email, token: z.string().check(z.minLength(1)), password, confirmPassword: z.string() })
  .check(
    notTheEmail(),
    z.refine((v) => v.password === v.confirmPassword, { error: "Mật khẩu nhập lại không khớp.", path: ["confirmPassword"] }),
  );

export const profileSchema = z.object({
  fullName,
  phoneNumber: z.string().check(z.regex(/^((0|\+84)\d{9})?$/, "Số điện thoại không hợp lệ.")),
});

/** Who an order is for (spec 08): the same rules as the profile, plus a required email. */
export const buyerSchema = z.object({ fullName, email, phoneNumber: z.string().check(z.regex(/^((0|\+84)\d{9})?$/, "Số điện thoại không hợp lệ.")) });

export type LoginInput = z.infer<typeof loginSchema>;
export type RegisterInput = z.infer<typeof registerSchema>;
export type ForgotPasswordInput = z.infer<typeof forgotPasswordSchema>;
export type ResetPasswordInput = z.infer<typeof resetPasswordSchema>;
export type ProfileInput = z.infer<typeof profileSchema>;
export type BuyerInput = z.infer<typeof buyerSchema>;

export { passwordStrength } from "./password-strength";
