import { describe, expect, it } from "vitest";
import { passwordStrength, profileSchema, registerSchema, resetPasswordSchema } from "./schemas";

const valid = { fullName: "Nguyễn An", email: "an@example.com", password: "matkhau123" };

describe("registerSchema (mirrors the api's RegisterValidator)", () => {
  it("accepts 10+ characters with letters and digits", () => {
    expect(registerSchema.safeParse(valid).success).toBe(true);
  });

  it.each([
    ["short1abc", "9 characters"],
    ["onlyletterspassword", "no digit"],
    ["1234567890123", "no letter"],
  ])("rejects %s (%s)", (password) => {
    const result = registerSchema.safeParse({ ...valid, password });
    expect(result.success).toBe(false);
    expect(result.error?.issues[0].path).toEqual(["password"]);
  });

  it("rejects a password equal to the email, ignoring case", () => {
    const email = "an12345678@example.com";
    expect(registerSchema.safeParse({ ...valid, email, password: email.toUpperCase() }).success).toBe(false);
  });

  it("requires a real email and a non-blank name", () => {
    expect(registerSchema.safeParse({ ...valid, email: "nope" }).success).toBe(false);
    expect(registerSchema.safeParse({ ...valid, fullName: "   " }).success).toBe(false);
  });
});

describe("resetPasswordSchema", () => {
  it("applies the same rules and requires the confirmation to match", () => {
    const base = { email: "an@example.com", token: "t", password: "matkhaumoi1", confirmPassword: "matkhaumoi1" };
    expect(resetPasswordSchema.safeParse(base).success).toBe(true);
    expect(resetPasswordSchema.safeParse({ ...base, password: "weak", confirmPassword: "weak" }).success).toBe(false);
    const mismatch = resetPasswordSchema.safeParse({ ...base, confirmPassword: "khac123456" });
    expect(mismatch.error?.issues[0].path).toEqual(["confirmPassword"]);
  });
});

describe("profileSchema", () => {
  it.each([
    ["0901234567", true],
    ["+84901234567", true],
    ["", true],
    ["12345", false],
  ])("phone %s valid=%s", (phoneNumber, ok) => {
    expect(profileSchema.safeParse({ fullName: "An", phoneNumber }).success).toBe(ok);
  });
});

describe("passwordStrength", () => {
  it("scores from 0 (empty) to 4 (long, mixed case, digits and symbols)", () => {
    expect(passwordStrength("").score).toBe(0);
    expect(passwordStrength("abc").score).toBe(1);
    expect(passwordStrength("matkhau123").score).toBe(2);
    expect(passwordStrength("MatKhau123").score).toBe(3);
    expect(passwordStrength("MatKhau123!@#xyz").score).toBe(4);
  });

  it("never rates a password the api would reject above weak", () => {
    expect(passwordStrength("abcdefghijklmnopQRST!!").score).toBeLessThanOrEqual(1);
  });

  it("labels each score in Vietnamese", () => {
    expect(passwordStrength("MatKhau123!@#xyz").label).toBe("Rất mạnh");
  });
});
