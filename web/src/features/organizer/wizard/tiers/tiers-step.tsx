"use client";

import { Plus, Trash2 } from "lucide-react";
import { useRef, useState } from "react";
import { z } from "zod";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { putTiers } from "../../api";
import { NativeSelect } from "../../native-select";
import { TIER_COLORS } from "../../tier-colors";
import type { TicketKind } from "../../types";
import { LockedBanner } from "../locked-banner";
import { useStepAutosave, type StepProps } from "../step-props";

type Row = { key: string; id: string | null; name: string; price: string; kind: TicketKind; capacity: string; color: string };

const integer = (max: number) => z.string().regex(/^\d+$/, "Nhập số").refine((v) => Number(v) >= 1 && Number(v) <= max, `Từ 1 đến ${max.toLocaleString("vi-VN")}`);

const rowSchema = z
  .object({
    name: z.string().trim().min(1, "Nhập tên").max(100),
    price: integer(100_000_000),
    kind: z.enum(["Seated", "GeneralAdmission"]),
    capacity: z.string(),
    color: z.string(),
  })
  .refine((r) => r.kind === "Seated" || integer(200_000).safeParse(r.capacity).success, { path: ["capacity"], message: "Nhập sức chứa" });

let nextKey = 0;
const newKey = () => `row-${nextKey++}`;

/** Step 2: an editable table of tiers. Seated capacity comes from the layout; GA tiers need one. */
export function TiersStep(props: StepProps) {
  const { event, onSaved } = props;
  const locked = event.status !== "Draft";
  const [rows, setRows] = useState<Row[]>(() =>
    event.tiers.map((t) => ({
      key: newKey(),
      id: t.id,
      name: t.name,
      price: String(t.price),
      kind: t.kind,
      capacity: t.capacity === null ? "" : String(t.capacity),
      color: t.color,
    })),
  );
  const [blocked, setBlocked] = useState<string | null>(null);
  const results = rows.map((row) => rowSchema.safeParse(row));
  const errorCount = results.filter((r) => !r.success).length;
  const value = rows.map((row) => ({ key: row.key, id: row.id, name: row.name, price: row.price, kind: row.kind, capacity: row.capacity, color: row.color }));
  // Ids the api gave new rows, by row key. A save queued behind the one that created them may still carry the rows
  // from before they were written back, and must update those tiers rather than create them again.
  const assigned = useRef(new Map<string, string>());

  useStepAutosave(props, {
    value,
    valid: !locked && errorCount === 0,
    errorCount,
    save: async (tiers, current) => {
      const sent = tiers.map((t) => ({ ...t, id: t.id ?? assigned.current.get(t.key) ?? null }));
      const updated = await putTiers(
        current.id,
        current.version,
        sent.map((t) => ({
          id: t.id,
          name: t.name.trim(),
          price: Number(t.price),
          kind: t.kind,
          capacity: t.kind === "GeneralAdmission" ? Number(t.capacity) : null,
          color: t.color,
        })),
      );
      onSaved(updated);
      // New rows now have ids: keep them so the next save updates instead of re-creating. The api appends new tiers
      // in request order, so the ids the request didn't carry belong to its id-less rows, in order.
      const known = new Set(sent.map((t) => t.id));
      const fresh = updated.tiers.filter((t) => !known.has(t.id)).map((t) => t.id);
      for (const t of sent) {
        if (t.id === null && fresh.length > 0) assigned.current.set(t.key, fresh.shift()!);
      }
      setRows((all) => all.map((row) => (row.id === null && assigned.current.has(row.key) ? { ...row, id: assigned.current.get(row.key)! } : row)));
    },
  });

  const update = (key: string, patch: Partial<Row>) => setRows((all) => all.map((row) => (row.key === key ? { ...row, ...patch } : row)));
  // A section's seats follow its tier, so such a tier can neither go nor change kind (the api says tier.in_use).
  const sectionsUsing = (row: Row) => (row.id ? (event.layout?.sections.filter((s) => s.tierId === row.id).map((s) => s.code) ?? []) : []);
  const remove = (row: Row) => {
    const sections = sectionsUsing(row);
    if (sections.length > 0) {
      setBlocked(`Hạng “${row.name}” đang được dùng ở khu ${sections.join(", ")}. Đổi hạng của các khu đó ở bước Sơ đồ trước.`);
      return;
    }

    setBlocked(null);
    setRows((all) => all.filter((r) => r.key !== row.key));
  };
  const add = () =>
    setRows((all) => [
      ...all,
      { key: newKey(), id: null, name: "", price: "", kind: "Seated", capacity: "", color: TIER_COLORS[all.length % TIER_COLORS.length].value },
    ]);

  return (
    <div className="flex flex-col gap-4">
      {locked ? <LockedBanner /> : null}
      {blocked ? (
        <Alert variant="destructive" role="alert">
          <AlertDescription>{blocked}</AlertDescription>
        </Alert>
      ) : null}
      <div className="overflow-x-auto rounded-xl border">
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>Tên hạng</TableHead>
              <TableHead>Giá (₫)</TableHead>
              <TableHead>Loại</TableHead>
              <TableHead>Sức chứa</TableHead>
              <TableHead>Màu</TableHead>
              <TableHead>
                <span className="sr-only">Thao tác</span>
              </TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {rows.map((row, i) => {
              const issues = results[i].success ? {} : Object.fromEntries(results[i].error!.issues.map((x) => [String(x.path[0]), x.message]));
              const label = row.name.trim() || `mới ${i + 1}`;
              const usedBy = locked ? [] : sectionsUsing(row);
              return (
                <TableRow key={row.key}>
                  <TableCell className="min-w-44">
                    <Input aria-label={`Tên hạng ${label}`} value={row.name} disabled={locked} aria-invalid={!!issues.name && row.name !== ""} onChange={(e) => update(row.key, { name: e.target.value })} />
                  </TableCell>
                  <TableCell className="min-w-36">
                    <Input
                      aria-label={`Giá hạng ${label}`}
                      inputMode="numeric"
                      value={row.price}
                      disabled={locked}
                      aria-invalid={!!issues.price && row.price !== ""}
                      onChange={(e) => update(row.key, { price: e.target.value.replace(/\D/g, "") })}
                    />
                    {row.price && !issues.price ? <span className="text-xs text-muted-foreground">{Number(row.price).toLocaleString("vi-VN")} ₫</span> : null}
                  </TableCell>
                  <TableCell className="min-w-32">
                    <NativeSelect
                      aria-label={`Loại hạng ${label}`}
                      aria-describedby={usedBy.length > 0 ? `${row.key}-kind-locked` : undefined}
                      value={row.kind}
                      disabled={locked || usedBy.length > 0}
                      onChange={(e) => update(row.key, { kind: e.target.value as TicketKind })}
                    >
                      <option value="Seated">Ngồi</option>
                      <option value="GeneralAdmission">Đứng</option>
                    </NativeSelect>
                    {usedBy.length > 0 ? (
                      <span id={`${row.key}-kind-locked`} className="text-xs text-muted-foreground">
                        Đang dùng ở khu {usedBy.join(", ")}: đổi hạng của khu ở bước Sơ đồ trước.
                      </span>
                    ) : null}
                  </TableCell>
                  <TableCell className="min-w-36">
                    {row.kind === "Seated" ? (
                      <span className="text-sm text-muted-foreground">Số ghế: {row.id ? (event.seatsPerTier[row.id] ?? 0) : 0} (từ sơ đồ)</span>
                    ) : (
                      <Input
                        aria-label={`Sức chứa hạng ${label}`}
                        inputMode="numeric"
                        value={row.capacity}
                        disabled={locked}
                        aria-invalid={!!issues.capacity}
                        onChange={(e) => update(row.key, { capacity: e.target.value.replace(/\D/g, "") })}
                      />
                    )}
                  </TableCell>
                  <TableCell className="min-w-36">
                    <div className="flex items-center gap-2">
                      <span aria-hidden className="size-4 shrink-0 rounded-full" style={{ backgroundColor: row.color }} />
                      <NativeSelect aria-label={`Màu hạng ${label}`} value={row.color} disabled={locked} onChange={(e) => update(row.key, { color: e.target.value })}>
                        {TIER_COLORS.map((c) => (
                          <option key={c.value} value={c.value}>
                            {c.name}
                          </option>
                        ))}
                      </NativeSelect>
                    </div>
                  </TableCell>
                  <TableCell>
                    {locked ? null : (
                      <Button type="button" size="icon" variant="ghost" aria-label={`Xoá hạng ${label}`} onClick={() => remove(row)}>
                        <Trash2 aria-hidden />
                      </Button>
                    )}
                  </TableCell>
                </TableRow>
              );
            })}
          </TableBody>
        </Table>
      </div>
      {rows.length === 0 ? <p className="text-sm text-muted-foreground">Chưa có hạng vé. Thêm ít nhất một hạng để xuất bản.</p> : null}
      {locked ? null : (
        <Button type="button" variant="outline" className="self-start" onClick={add}>
          <Plus aria-hidden />
          Thêm hạng vé
        </Button>
      )}
    </div>
  );
}
