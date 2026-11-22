import { Building2, CalendarDays, Flag, Inbox, ScrollText, ShoppingBag, Users } from "lucide-react";

/** The admin area's pages (spec 16), for the sidebar and the ⌘K palette. */
export const ADMIN_NAV = [
  { label: "Người dùng", href: "/admin/users", icon: Users },
  { label: "Đơn đăng ký BTC", href: "/admin/applications", icon: Building2 },
  { label: "Sự kiện", href: "/admin/events", icon: CalendarDays },
  { label: "Đơn hàng", href: "/admin/orders", icon: ShoppingBag },
  { label: "Outbox lỗi", href: "/admin/outbox", icon: Inbox },
  { label: "Cờ rủi ro", href: "/admin/flags", icon: Flag },
  { label: "Nhật ký", href: "/admin/audit", icon: ScrollText },
] as const;
