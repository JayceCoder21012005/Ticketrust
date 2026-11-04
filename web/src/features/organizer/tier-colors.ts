/** Same list and order as the api's `TierColors.All` (a test compares them). White text on each is ≥ 4.5:1. */
export const TIER_COLORS = [
  { value: "#7c3aed", name: "Tím" },
  { value: "#be123c", name: "Đỏ hồng" },
  { value: "#0f766e", name: "Xanh ngọc" },
  { value: "#1d4ed8", name: "Xanh dương" },
  { value: "#b45309", name: "Hổ phách" },
  { value: "#15803d", name: "Xanh lá" },
  { value: "#a21caf", name: "Tím hồng" },
  { value: "#334155", name: "Xám đá" },
] as const;

export function tierColorName(value: string): string {
  return TIER_COLORS.find((c) => c.value === value.toLowerCase())?.name ?? value;
}
