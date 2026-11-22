"use client";

import Link from "next/link";
import { useEffect, useState, type FormEvent, type ReactNode } from "react";
import { ErrorState } from "@/components/shared/error-state";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { Skeleton } from "@/components/ui/skeleton";
import * as admin from "@/lib/admin/api";
import { toFriendlyMessage } from "@/lib/errors";
import { formatVnd } from "@/lib/format";
import { AdminShell } from "./admin-shell";
import { AdminTable, useUrlFilters, type Column } from "./admin-table";
import { AuditDiff } from "./audit-diff";
import { ReasonDialog, type ReasonDialogProps } from "./reason-dialog";

const when = (iso: string | null | undefined) =>
  iso ? new Date(iso).toLocaleString("vi-VN", { dateStyle: "short", timeStyle: "short", timeZone: "Asia/Ho_Chi_Minh" }) : "—";

type Pending = Omit<ReasonDialogProps, "open" | "onOpenChange">;

/** One reason dialog per page; `ask` opens it for an action, `version` bumps after a change so the list reloads. */
function usePendingAction() {
  const [pending, setPending] = useState<Pending | null>(null);
  const [version, setVersion] = useState(0);
  const ask = (action: Pending) =>
    setPending({
      ...action,
      onConfirm: async (reason, confirm) => {
        await action.onConfirm(reason, confirm);
        setVersion((v) => v + 1);
      },
    });
  const dialog = pending ? <ReasonDialog {...pending} open onOpenChange={(open) => !open && setPending(null)} /> : null;
  return { ask, dialog, version };
}

function SearchBox({ value, onSearch, placeholder, label }: { value: string; onSearch: (q: string | null) => void; placeholder: string; label: string }) {
  const [text, setText] = useState(value);
  const submit = (event: FormEvent) => {
    event.preventDefault();
    onSearch(text.trim() || null);
  };
  return (
    <form role="search" onSubmit={submit} className="flex max-w-xl gap-2">
      <Input aria-label={label} placeholder={placeholder} value={text} onChange={(e) => setText(e.target.value)} />
      <Button type="submit" variant="secondary">
        Tìm
      </Button>
    </form>
  );
}

function Filter({ label, value, options, onChange }: { label: string; value: string; options: [string, string][]; onChange: (v: string | null) => void }) {
  return (
    <label className="flex items-center gap-2 text-sm">
      {label}
      <select value={value} onChange={(e) => onChange(e.target.value || null)} className="h-9 rounded-md border bg-background px-2 text-sm">
        {options.map(([v, text]) => (
          <option key={v} value={v}>
            {text}
          </option>
        ))}
      </select>
    </label>
  );
}

function Title({ children }: { children: ReactNode }) {
  return <h1 className="text-2xl font-bold tracking-tight">{children}</h1>;
}

// ── Users ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

const ROLES = ["Customer", "Organizer", "GateStaff", "Admin"];

function userActions(ask: (a: Pending) => void) {
  return [
    {
      label: "Khoá tài khoản",
      destructive: true,
      hidden: (u: admin.AdminUser) => u.locked,
      onSelect: (u: admin.AdminUser) =>
        ask({ title: "Khoá tài khoản", description: `${u.email} sẽ bị đăng xuất khỏi mọi thiết bị.`, confirmLabel: "Khoá", confirmText: u.email, destructive: true, onConfirm: async (r) => void (await admin.lockUser(u.id, r)) }),
    },
    {
      label: "Mở khoá",
      hidden: (u: admin.AdminUser) => !u.locked,
      onSelect: (u: admin.AdminUser) => ask({ title: "Mở khoá tài khoản", description: u.email, confirmLabel: "Mở khoá", onConfirm: async (r) => void (await admin.unlockUser(u.id, r)) }),
    },
    ...ROLES.map((role) => ({
      label: `Cấp quyền ${role}`,
      hidden: (u: admin.AdminUser) => u.roles.includes(role),
      onSelect: (u: admin.AdminUser) => ask({ title: `Cấp quyền ${role}`, description: u.email, confirmLabel: "Cấp quyền", onConfirm: async (r) => void (await admin.grantRole(u.id, role, r)) }),
    })),
    ...ROLES.map((role) => ({
      label: `Thu hồi ${role}`,
      destructive: true,
      hidden: (u: admin.AdminUser) => !u.roles.includes(role),
      onSelect: (u: admin.AdminUser) =>
        ask({ title: `Thu hồi quyền ${role}`, description: u.email, confirmLabel: "Thu hồi", confirmText: u.email, destructive: true, onConfirm: async (r) => void (await admin.revokeRole(u.id, role, r)) }),
    })),
  ];
}

const userColumns: Column<admin.AdminUser>[] = [
  {
    id: "email",
    label: "Email",
    hideable: false,
    cell: (u) => (
      <Link href={`/admin/users/${u.id}`} className="font-medium hover:underline">
        {u.email}
      </Link>
    ),
  },
  { id: "name", label: "Họ tên", cell: (u) => u.fullName },
  { id: "phone", label: "Điện thoại", cell: (u) => u.phoneNumber ?? "—" },
  { id: "roles", label: "Vai trò", cell: (u) => <span className="flex flex-wrap gap-1">{u.roles.map((r) => <Badge key={r} variant="secondary">{r}</Badge>)}</span> },
  { id: "state", label: "Trạng thái", cell: (u) => (u.locked ? <Badge variant="destructive">Đã khoá</Badge> : <Badge variant="outline">Hoạt động</Badge>) },
  { id: "created", label: "Tạo lúc", cell: (u) => when(u.createdAt) },
];

export function UsersPage() {
  const [filters, setFilters] = useUrlFilters();
  const { ask, dialog, version } = usePendingAction();
  return (
    <AdminShell breadcrumbs={[{ label: "Quản trị" }, { label: "Người dùng" }]}>
      <Title>Người dùng</Title>
      <SearchBox label="Tìm người dùng" placeholder="Email, họ tên, số điện thoại hoặc id" value={filters.q ?? ""} onSearch={(q) => setFilters({ q })} />
      <AdminTable
        caption="Người dùng"
        columns={userColumns}
        rowKey={(u) => u.id}
        load={(cursor) => admin.searchUsers(filters.q, cursor)}
        reloadKey={`${filters.q ?? ""}:${version}`}
        actions={userActions(ask)}
      />
      {dialog}
    </AdminShell>
  );
}

export function UserDetailPage({ userId }: { userId: string }) {
  const { ask, dialog, version } = usePendingAction();
  const [detail, setDetail] = useState<admin.AdminUserDetail | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    admin.getUser(userId).then(setDetail, (e: unknown) => setError(toFriendlyMessage(e)));
  }, [userId, version]);

  const crumbs = [{ label: "Quản trị" }, { label: "Người dùng", href: "/admin/users" }, { label: detail?.user.email ?? "…" }];
  if (error) return <AdminShell breadcrumbs={crumbs}><ErrorState title="Không tải được người dùng" description={error} /></AdminShell>;
  if (!detail) return <AdminShell breadcrumbs={crumbs}><Skeleton className="h-64 w-full" /></AdminShell>;

  const { user } = detail;
  return (
    <AdminShell breadcrumbs={crumbs}>
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <Title>{user.fullName || user.email}</Title>
          <p className="text-sm text-muted-foreground">{`${user.email} · ${user.phoneNumber ?? "không có số điện thoại"}`}</p>
        </div>
        <div className="flex flex-wrap gap-2">
          {userActions(ask)
            .filter((a) => !a.hidden(user))
            .slice(0, 3)
            .map((a) => (
              <Button key={a.label} type="button" size="sm" variant={a.destructive ? "destructive" : "outline"} onClick={() => a.onSelect(user)}>
                {a.label}
              </Button>
            ))}
        </div>
      </div>
      <div className="flex flex-wrap gap-2">
        {user.roles.map((r) => (
          <Badge key={r}>{r}</Badge>
        ))}
        {user.locked ? <Badge variant="destructive">Đã khoá</Badge> : null}
        {Number(detail.openFlags) > 0 ? (
          <Badge variant="outline" className="border-amber-500">
            {`${detail.openFlags} cờ rủi ro mở`}
          </Badge>
        ) : null}
      </div>
      <Card>
        <CardHeader>
          <CardTitle>Đơn hàng gần đây</CardTitle>
        </CardHeader>
        <CardContent>
          {detail.recentOrders.length === 0 ? (
            <p className="text-sm text-muted-foreground">Chưa có đơn hàng.</p>
          ) : (
            <ul className="divide-y text-sm">
              {detail.recentOrders.map((o) => (
                <li key={o.id} className="flex flex-wrap justify-between gap-2 py-2">
                  <Link href={`/admin/orders?q=${o.id}`} className="hover:underline">
                    {o.eventTitle}
                  </Link>
                  <span className="text-muted-foreground">{`${o.status} · ${formatVnd(Number(o.total))} · ${when(o.createdAt)}`}</span>
                </li>
              ))}
            </ul>
          )}
        </CardContent>
      </Card>
      {dialog}
    </AdminShell>
  );
}

// ── Organizer applications ─────────────────────────────────────────────────────────────────────────────────────────────

export function ApplicationsPage() {
  const [filters, setFilters] = useUrlFilters();
  const { ask, dialog, version } = usePendingAction();
  const status = filters.status ?? "Pending";
  return (
    <AdminShell breadcrumbs={[{ label: "Quản trị" }, { label: "Đơn đăng ký nhà tổ chức" }]}>
      <Title>Đơn đăng ký nhà tổ chức</Title>
      <Filter label="Trạng thái" value={status} options={[["Pending", "Chờ duyệt"], ["Approved", "Đã duyệt"], ["Rejected", "Từ chối"]]} onChange={(v) => setFilters({ status: v })} />
      <AdminTable
        caption="Đơn đăng ký nhà tổ chức"
        columns={[
          { id: "company", label: "Công ty", hideable: false, cell: (a: admin.OrganizerApplication) => <span className="font-medium">{a.companyName}</span> },
          { id: "phone", label: "Điện thoại", cell: (a) => a.phone },
          { id: "description", label: "Mô tả", cell: (a) => <span className="line-clamp-2 max-w-md">{a.description}</span> },
          { id: "user", label: "Người dùng", cell: (a) => <Link href={`/admin/users/${a.userId}`} className="hover:underline">Xem</Link> },
          { id: "status", label: "Trạng thái", cell: (a) => <Badge variant="secondary">{a.status}</Badge> },
          { id: "created", label: "Gửi lúc", cell: (a) => when(a.createdAt) },
        ]}
        rowKey={(a) => a.id}
        load={(cursor) => admin.listApplications(status, cursor)}
        reloadKey={`${status}:${version}`}
        actions={[
          {
            label: "Duyệt",
            hidden: (a) => a.status !== "Pending",
            onSelect: (a) => ask({ title: "Duyệt đơn", description: `${a.companyName} sẽ có quyền nhà tổ chức.`, confirmLabel: "Duyệt", onConfirm: async (r) => void (await admin.decideApplication(a.id, true, r)) }),
          },
          {
            label: "Từ chối",
            destructive: true,
            hidden: (a) => a.status !== "Pending",
            onSelect: (a) =>
              ask({ title: "Từ chối đơn", description: "Lý do sẽ được gửi cho người đăng ký.", confirmLabel: "Từ chối", destructive: true, onConfirm: async (r) => void (await admin.decideApplication(a.id, false, r)) }),
          },
        ]}
        empty="Không có đơn nào."
      />
      {dialog}
    </AdminShell>
  );
}

// ── Events ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

export function EventsPage() {
  const [filters, setFilters] = useUrlFilters();
  const { ask, dialog, version } = usePendingAction();
  return (
    <AdminShell breadcrumbs={[{ label: "Quản trị" }, { label: "Sự kiện" }]}>
      <Title>Sự kiện</Title>
      <div className="flex flex-wrap items-end gap-3">
        <SearchBox label="Tìm sự kiện" placeholder="Tên, slug hoặc id" value={filters.q ?? ""} onSearch={(q) => setFilters({ q })} />
        <Filter
          label="Trạng thái"
          value={filters.status ?? ""}
          options={[["", "Tất cả"], ["Draft", "Nháp"], ["Published", "Đang bán"], ["Suspended", "Tạm ẩn"], ["Ended", "Đã kết thúc"], ["Cancelled", "Đã huỷ"]]}
          onChange={(v) => setFilters({ status: v })}
        />
      </div>
      <AdminTable
        caption="Sự kiện"
        columns={[
          { id: "title", label: "Sự kiện", hideable: false, cell: (e: admin.AdminEventRow) => <Link href={`/events/${e.slug}`} className="font-medium hover:underline">{e.title}</Link> },
          { id: "city", label: "Thành phố", cell: (e) => e.city },
          { id: "starts", label: "Bắt đầu", cell: (e) => when(e.startsAt) },
          { id: "status", label: "Trạng thái", cell: (e) => <Badge variant={e.status === "Suspended" ? "destructive" : "secondary"}>{e.status}</Badge> },
        ]}
        rowKey={(e) => e.id}
        load={(cursor) => admin.listEvents(filters.q, filters.status, cursor)}
        reloadKey={`${filters.q ?? ""}:${filters.status ?? ""}:${version}`}
        actions={[
          {
            label: "Tạm ẩn (kiểm duyệt)",
            destructive: true,
            hidden: (e) => e.status !== "Published",
            onSelect: (e) =>
              ask({
                title: "Tạm ẩn sự kiện",
                description: "Sự kiện bị ẩn và ngừng bán; vé đã bán vẫn có hiệu lực. Nhà tổ chức sẽ nhận lý do.",
                confirmLabel: "Tạm ẩn",
                confirmText: e.title,
                destructive: true,
                onConfirm: async (r) => void (await admin.moderateEvent(e.id, true, r)),
              }),
          },
          {
            label: "Mở lại",
            hidden: (e) => e.status !== "Suspended",
            onSelect: (e) => ask({ title: "Mở lại sự kiện", description: e.title, confirmLabel: "Mở lại", onConfirm: async (r) => void (await admin.moderateEvent(e.id, false, r)) }),
          },
        ]}
      />
      {dialog}
    </AdminShell>
  );
}

// ── Orders ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

export function OrdersPage() {
  const [filters, setFilters] = useUrlFilters();
  const { ask, dialog, version } = usePendingAction();
  return (
    <AdminShell breadcrumbs={[{ label: "Quản trị" }, { label: "Đơn hàng" }]}>
      <Title>Đơn hàng</Title>
      <SearchBox label="Tìm đơn hàng" placeholder="Mã đơn, email hoặc số điện thoại người mua" value={filters.q ?? ""} onSearch={(q) => setFilters({ q })} />
      <AdminTable
        caption="Đơn hàng"
        columns={[
          { id: "id", label: "Mã đơn", hideable: false, cell: (o: admin.AdminOrderRow) => <span className="font-mono text-xs">{o.id}</span> },
          { id: "event", label: "Sự kiện", cell: (o) => o.eventTitle },
          { id: "status", label: "Trạng thái", cell: (o) => <Badge variant="secondary">{o.status}</Badge> },
          { id: "kind", label: "Loại", cell: (o) => (o.kind === "Resale" ? "Bán lại" : "Bán lần đầu") },
          { id: "tickets", label: "Số vé", cell: (o) => o.tickets },
          { id: "total", label: "Tổng", cell: (o) => formatVnd(Number(o.total)) },
          { id: "paid", label: "Thanh toán", cell: (o) => when(o.paidAt) },
        ]}
        rowKey={(o) => o.id}
        load={(cursor) => admin.searchOrders(filters.q, cursor)}
        reloadKey={`${filters.q ?? ""}:${version}`}
        actions={[
          {
            label: "Hoàn tiền thủ công",
            destructive: true,
            hidden: (o) => o.status !== "Paid" || o.kind !== "Primary",
            onSelect: (o) =>
              ask({
                title: "Hoàn tiền đơn hàng",
                description: "Đơn chuyển sang chờ hoàn tiền, vé bị huỷ và ghế được trả về danh sách chờ. Người mua sẽ được báo.",
                confirmLabel: "Hoàn tiền",
                confirmText: o.id,
                destructive: true,
                onConfirm: async (r, c) => void (await admin.refundOrder(o.id, r, c)),
              }),
          },
        ]}
        empty="Nhập mã đơn, email hoặc số điện thoại để tìm."
      />
      {dialog}
    </AdminShell>
  );
}

// ── Outbox ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

export function OutboxPage() {
  const { ask, dialog, version } = usePendingAction();
  return (
    <AdminShell breadcrumbs={[{ label: "Quản trị" }, { label: "Outbox lỗi" }]}>
      <Title>Tin nhắn outbox lỗi</Title>
      <AdminTable
        caption="Tin nhắn outbox lỗi"
        columns={[
          { id: "type", label: "Loại", hideable: false, cell: (m: admin.DeadLetter) => <span className="font-medium">{m.type}</span> },
          { id: "occurred", label: "Phát sinh", cell: (m) => when(m.occurredAt) },
          { id: "attempts", label: "Số lần thử", cell: (m) => m.attempts },
          { id: "error", label: "Lỗi", cell: (m) => <span className="line-clamp-2 max-w-lg font-mono text-xs">{m.error}</span> },
        ]}
        rowKey={(m) => m.id}
        load={(cursor) => admin.listDeadLetters(cursor)}
        reloadKey={String(version)}
        actions={[
          { label: "Thử lại", onSelect: (m) => ask({ title: "Thử lại tin nhắn", description: m.type, confirmLabel: "Thử lại", onConfirm: (r) => admin.resolveDeadLetter(m.id, true, r) }) },
          {
            label: "Bỏ qua",
            destructive: true,
            onSelect: (m) => ask({ title: "Bỏ qua tin nhắn", description: "Tin nhắn sẽ không được xử lý nữa.", confirmLabel: "Bỏ qua", destructive: true, confirmText: m.type, onConfirm: (r) => admin.resolveDeadLetter(m.id, false, r) }),
          },
        ]}
        empty="Không có tin nhắn lỗi."
      />
      {dialog}
    </AdminShell>
  );
}

// ── Risk flags ─────────────────────────────────────────────────────────────────────────────────────────────────────────

const FLAG_REASONS: Record<string, string> = { shared_phone: "Số điện thoại dùng cho nhiều tài khoản", subnet_join_burst: "Nhiều lượt vào hàng chờ từ cùng mạng" };

export function FlagsPage() {
  const [filters, setFilters] = useUrlFilters();
  const { ask, dialog, version } = usePendingAction();
  const open = filters.state !== "resolved";
  return (
    <AdminShell breadcrumbs={[{ label: "Quản trị" }, { label: "Cờ rủi ro" }]}>
      <Title>Cờ rủi ro</Title>
      <Filter label="Trạng thái" value={open ? "" : "resolved"} options={[["", "Chưa xử lý"], ["resolved", "Đã xử lý"]]} onChange={(v) => setFilters({ state: v })} />
      <AdminTable
        caption="Cờ rủi ro"
        columns={[
          { id: "reason", label: "Lý do", hideable: false, cell: (f: admin.AdminFlag) => FLAG_REASONS[f.reason] ?? f.reason },
          { id: "detail", label: "Chi tiết", cell: (f) => f.detail },
          { id: "user", label: "Tài khoản", cell: (f) => <Link href={`/admin/users/${f.userId}`} className="hover:underline">Xem</Link> },
          { id: "created", label: "Phát hiện", cell: (f) => when(f.createdAt) },
          { id: "resolution", label: "Kết quả", cell: (f) => f.resolution ?? "—" },
        ]}
        rowKey={(f) => f.id}
        load={(cursor) => admin.listFlags(open, cursor)}
        reloadKey={`${open}:${version}`}
        actions={[
          { label: "Bỏ qua cờ", hidden: (f) => !!f.resolvedAt, onSelect: (f) => ask({ title: "Bỏ qua cờ", description: FLAG_REASONS[f.reason] ?? f.reason, confirmLabel: "Bỏ qua", onConfirm: async (r) => void (await admin.resolveFlag(f.id, false, r)) }) },
          {
            label: "Khoá tài khoản",
            destructive: true,
            hidden: (f) => !!f.resolvedAt,
            onSelect: (f) => ask({ title: "Khoá tài khoản bị gắn cờ", description: "Tài khoản bị khoá và đăng xuất khỏi mọi thiết bị.", confirmLabel: "Khoá", destructive: true, onConfirm: async (r) => void (await admin.resolveFlag(f.id, true, r)) }),
          },
        ]}
        empty="Không có cờ nào."
      />
      {dialog}
    </AdminShell>
  );
}

// ── Audit ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

export function AuditPage() {
  const [filters, setFilters] = useUrlFilters();
  const [selected, setSelected] = useState<admin.AuditRow | null>(null);
  const audit: admin.AuditFilters = { actor: filters.actor, entityType: filters.entityType, entityId: filters.entityId, action: filters.action, from: filters.from, to: filters.to };
  const field = (key: keyof admin.AuditFilters, label: string, type = "text") => (
    <label className="flex flex-col gap-1 text-xs font-medium">
      {label}
      <Input type={type} defaultValue={filters[key] ?? ""} onBlur={(e) => setFilters({ [key]: e.target.value.trim() || null })} className="h-9 w-44" />
    </label>
  );
  return (
    <AdminShell breadcrumbs={[{ label: "Quản trị" }, { label: "Nhật ký" }]}>
      <Title>Nhật ký thay đổi</Title>
      <div className="flex flex-wrap items-end gap-3" role="group" aria-label="Bộ lọc nhật ký">
        {field("actor", "Người thực hiện (id)")}
        {field("entityType", "Loại đối tượng")}
        {field("entityId", "Id đối tượng")}
        {field("action", "Hành động")}
        {field("from", "Từ ngày", "date")}
        {field("to", "Đến ngày", "date")}
      </div>
      <AdminTable
        caption="Nhật ký thay đổi"
        columns={[
          { id: "at", label: "Lúc", hideable: false, cell: (a: admin.AuditRow) => when(a.at) },
          { id: "action", label: "Hành động", cell: (a) => <span className="font-mono text-xs">{a.action}</span> },
          { id: "entity", label: "Đối tượng", cell: (a) => <span className="font-mono text-xs">{`${a.entityType}${a.entityId ? ` · ${a.entityId}` : ""}`}</span> },
          { id: "actor", label: "Người thực hiện", cell: (a) => (a.actorUserId ? <Link href={`/admin/users/${a.actorUserId}`} className="hover:underline">Xem</Link> : "Hệ thống") },
          { id: "ip", label: "IP", cell: (a) => a.actorIp ?? "—" },
          { id: "detail", label: "Chi tiết", hideable: false, cell: (a) => <Button type="button" variant="link" size="sm" className="h-auto p-0" onClick={() => setSelected(a)}>Xem</Button> },
        ]}
        rowKey={(a) => a.id}
        load={(cursor) => admin.listAudit(audit, cursor)}
        reloadKey={JSON.stringify(audit)}
        empty="Không có mục nào khớp bộ lọc."
      />
      <Sheet open={selected !== null} onOpenChange={(open) => !open && setSelected(null)}>
        <SheetContent className="w-full overflow-y-auto sm:max-w-xl">
          <SheetHeader>
            <SheetTitle>{selected?.action}</SheetTitle>
            <SheetDescription>{selected ? `${when(selected.at)} · ${selected.entityType} ${selected.entityId ?? ""} · ${selected.correlationId ?? ""}` : ""}</SheetDescription>
          </SheetHeader>
          <div className="px-4 pb-4">
            <AuditDiff changes={selected?.changes} />
          </div>
        </SheetContent>
      </Sheet>
    </AdminShell>
  );
}
