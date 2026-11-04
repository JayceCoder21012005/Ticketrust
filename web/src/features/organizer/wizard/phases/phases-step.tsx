"use client";

import { Plus, Shuffle, Trash2 } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { Button } from "@/components/ui/button";
import { Field, FieldDescription, FieldError, FieldLabel } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { loadClockOffset } from "@/lib/catalog/browser";
import { formatOpensIn } from "@/lib/clock";
import { cn } from "@/lib/utils";
import { formatVietnam, fromIsoToVietnam, toVietnamIso } from "@/lib/vn-time";
import { putPhases } from "../../api";
import { NativeSelect } from "../../native-select";
import type { PhaseInput } from "../../types";
import { LockedBanner } from "../locked-banner";
import { useStepAutosave, type StepProps } from "../step-props";
import { generateAccessCode } from "./access-code";
import { layoutTimeline, type TimelineBlock } from "./timeline";

const CODE = /^[A-Za-z0-9-]{4,32}$/;

/** `keep`: the stored hash stays (or there is none); `set`: a new code; `remove`: drop the stored one. */
type CodeMode = "keep" | "set" | "remove";

type Row = {
  key: string;
  id: string | null;
  name: string;
  openDate: string;
  openTime: string;
  closeDate: string;
  closeTime: string;
  queueEnabled: boolean;
  maxTicketsPerUser: string;
  hasAccessCode: boolean;
  codeMode: CodeMode;
  code: string;
};

let nextKey = 0;
const newKey = () => `phase-${nextKey++}`;

const iso = (date: string, time: string) => {
  try {
    return toVietnamIso(date, time);
  } catch {
    return null;
  }
};

function toInput(row: Row): PhaseInput | null {
  const opensAt = iso(row.openDate, row.openTime);
  const closesAt = iso(row.closeDate, row.closeTime);
  if (!opensAt || !closesAt) return null;
  return {
    id: row.id,
    name: row.name.trim(),
    opensAt,
    closesAt,
    queueEnabled: row.queueEnabled,
    maxTicketsPerUser: Number(row.maxTicketsPerUser),
    accessCode: row.codeMode === "remove" ? "" : row.codeMode === "set" && row.code.trim() ? row.code.trim() : null,
  };
}

function rowErrors(row: Row, eventStart: string): Partial<Record<"name" | "open" | "close" | "max" | "code", string>> {
  const errors: Partial<Record<"name" | "open" | "close" | "max" | "code", string>> = {};
  const opens = iso(row.openDate, row.openTime);
  const closes = iso(row.closeDate, row.closeTime);
  if (!row.name.trim()) errors.name = "Nhập tên đợt";
  if (!opens) errors.open = "Chọn ngày và giờ mở";
  if (!closes) errors.close = "Chọn ngày và giờ đóng";
  else if (opens && Date.parse(closes) <= Date.parse(opens)) errors.close = "Giờ đóng phải sau giờ mở";
  else if (Date.parse(closes) > Date.parse(eventStart)) errors.close = "Phải đóng trước khi sự kiện bắt đầu";
  if (!/^[1-8]$/.test(row.maxTicketsPerUser)) errors.max = "Từ 1 đến 8 vé";
  if (row.codeMode === "set" && row.code.trim() && !CODE.test(row.code.trim())) errors.code = "4–32 chữ, số hoặc dấu gạch ngang";
  return errors;
}

/** Step 4 (design §3.3): a timeline of the phases against the event date, then one card per phase. */
export function PhasesStep(props: StepProps) {
  const { event, onSaved } = props;
  const locked = event.status !== "Draft";
  const [rows, setRows] = useState<Row[]>(() =>
    event.phases.map((p) => {
      const open = fromIsoToVietnam(p.opensAt);
      const close = fromIsoToVietnam(p.closesAt);
      return {
        key: newKey(),
        id: p.id,
        name: p.name,
        openDate: open.date,
        openTime: open.time,
        closeDate: close.date,
        closeTime: close.time,
        queueEnabled: p.queueEnabled,
        maxTicketsPerUser: String(p.maxTicketsPerUser),
        hasAccessCode: p.hasAccessCode,
        codeMode: "keep" as const,
        code: "",
      };
    }),
  );
  const now = useServerNow();

  const inputs = rows.map(toInput);
  const timeline = layoutTimeline(
    rows.map((row, i) => ({ id: row.key, opensAt: inputs[i]?.opensAt ?? "", closesAt: inputs[i]?.closesAt ?? "" })),
    now,
    event.startsAt,
  );
  const blockOf = (key: string) => timeline.blocks.find((b) => b.id === key);
  const errors = rows.map((row) => rowErrors(row, event.startsAt));
  const overlapping = timeline.blocks.some((b) => b.overlaps);
  const errorCount = errors.filter((e) => Object.keys(e).length > 0).length + (overlapping ? 1 : 0);

  // Ids the api gave new rows, by row key: a save queued behind the one that created them may still carry the rows
  // from before they were written back, and must keep those phases (and their codes) rather than create them again.
  const assigned = useRef(new Map<string, string>());

  useStepAutosave(props, {
    value: rows.map((row, i) => ({ key: row.key, input: inputs[i] })),
    valid: !locked && errorCount === 0 && inputs.every((i) => i !== null),
    errorCount,
    save: async (value, current) => {
      const sent = value.map(({ key, input }) => ({ ...(input as PhaseInput), id: input!.id ?? assigned.current.get(key) ?? null }));
      const updated = await putPhases(current.id, current.version, sent);
      onSaved(updated);
      // Give new rows the ids the api assigned (matched by opening time), so the next save keeps their codes.
      sent.forEach((phase, i) => {
        const match = phase.id === null && updated.phases.find((p) => Date.parse(p.opensAt) === Date.parse(phase.opensAt) && !sent.some((s) => s.id === p.id));
        if (match) assigned.current.set(value[i].key, match.id);
      });
      setRows((all) => all.map((row) => (row.id === null && assigned.current.has(row.key) ? { ...row, id: assigned.current.get(row.key)! } : row)));
    },
  });

  const update = (key: string, patch: Partial<Row>) => setRows((all) => all.map((r) => (r.key === key ? { ...r, ...patch } : r)));
  const add = () =>
    setRows((all) => [
      ...all,
      {
        key: newKey(),
        id: null,
        name: all.length === 0 ? "Mở bán" : `Đợt ${all.length + 1}`,
        openDate: "",
        openTime: "",
        closeDate: "",
        closeTime: "",
        queueEnabled: true,
        maxTicketsPerUser: "4",
        hasAccessCode: false,
        codeMode: "keep",
        code: "",
      },
    ]);

  return (
    <div className="flex flex-col gap-6">
      {locked ? <LockedBanner /> : null}
      <Timeline blocks={timeline.blocks} rows={rows} eventMarkPct={timeline.eventMarkPct} eventStart={event.startsAt} />
      <fieldset disabled={locked} className="flex flex-col gap-4">
        {rows.map((row, i) => (
          <PhaseCard
            key={row.key}
            index={i}
            row={row}
            errors={errors[i]}
            block={blockOf(row.key)}
            now={now}
            locked={locked}
            onChange={(patch) => update(row.key, patch)}
            onRemove={() => setRows((all) => all.filter((r) => r.key !== row.key))}
          />
        ))}
      </fieldset>
      {rows.length === 0 ? <p className="text-sm text-muted-foreground">Chưa có đợt mở bán. Cần ít nhất một đợt để xuất bản.</p> : null}
      {locked ? null : (
        <Button type="button" variant="outline" className="self-start" onClick={add}>
          <Plus aria-hidden />
          Thêm đợt mở bán
        </Button>
      )}
    </div>
  );
}

/** Server time (spec 04 clock offset), refreshed every 30 s: enough for "opens in 3 days 4 hours" and the 10-minute rule. */
function useServerNow() {
  const [now, setNow] = useState(() => Date.now());

  useEffect(() => {
    let offset = 0;
    let active = true;
    void loadClockOffset().then((value) => {
      if (!active) return;
      offset = value;
      setNow(Date.now() + offset);
    });
    const id = setInterval(() => setNow(Date.now() + offset), 30_000);
    return () => {
      active = false;
      clearInterval(id);
    };
  }, []);

  return now;
}

function Timeline({ blocks, rows, eventMarkPct, eventStart }: { blocks: TimelineBlock[]; rows: Row[]; eventMarkPct: number; eventStart: string }) {
  return (
    <figure className="flex flex-col gap-2">
      <figcaption className="flex justify-between text-xs text-muted-foreground">
        <span>Bây giờ</span>
        <span>Sự kiện bắt đầu: {formatVietnam(eventStart)}</span>
      </figcaption>
      <div className="relative h-10 rounded-lg bg-muted" aria-hidden>
        {blocks.map((block) => {
          const row = rows.find((r) => r.key === block.id);
          return (
            <div
              key={block.id}
              className={cn(
                "absolute top-1 bottom-1 flex min-w-1 items-center overflow-hidden rounded px-2 text-xs font-medium",
                // Text colour per background: --danger is light in dark mode, --warning is light in both.
                block.overlaps ? "bg-danger text-white dark:text-background" : block.tooSoon ? "bg-warning text-black" : "bg-primary text-primary-foreground",
              )}
              style={{ left: `${block.leftPct}%`, width: `${block.widthPct}%` }}
            >
              <span className="truncate">{row?.name}</span>
            </div>
          );
        })}
        <div className="absolute top-0 bottom-0 w-0.5 bg-foreground" style={{ left: `calc(${eventMarkPct}% - 1px)` }} />
      </div>
    </figure>
  );
}

type PhaseCardProps = {
  index: number;
  row: Row;
  errors: ReturnType<typeof rowErrors>;
  block: TimelineBlock | undefined;
  now: number;
  locked: boolean;
  onChange: (patch: Partial<Row>) => void;
  onRemove: () => void;
};

function PhaseCard({ index, row, errors, block, now, locked, onChange, onRemove }: PhaseCardProps) {
  const id = (field: string) => `${row.key}-${field}`;
  const err = (message: string | undefined) => (message ? [{ message }] : undefined);
  const opens = iso(row.openDate, row.openTime);

  return (
    <section
      role="group"
      aria-label={`Đợt ${index + 1}: ${row.name || "chưa đặt tên"}`}
      className={cn("grid gap-4 rounded-xl border p-4 sm:grid-cols-2", block?.overlaps && "border-danger")}
    >
      <Field className="sm:col-span-2" data-invalid={!!errors.name}>
        <FieldLabel htmlFor={id("name")}>Tên đợt</FieldLabel>
        <Input id={id("name")} value={row.name} onChange={(e) => onChange({ name: e.target.value })} />
        <FieldError errors={err(errors.name)} />
      </Field>

      <div className="grid grid-cols-2 gap-3">
        <Field data-invalid={!!errors.open}>
          <FieldLabel htmlFor={id("open-date")}>Ngày mở</FieldLabel>
          <Input id={id("open-date")} type="date" value={row.openDate} onChange={(e) => onChange({ openDate: e.target.value })} />
        </Field>
        <Field data-invalid={!!errors.open}>
          <FieldLabel htmlFor={id("open-time")}>Giờ mở</FieldLabel>
          <Input id={id("open-time")} type="time" value={row.openTime} onChange={(e) => onChange({ openTime: e.target.value })} />
        </Field>
        <FieldError className="col-span-2" errors={err(errors.open)} />
      </div>
      <div className="grid grid-cols-2 gap-3">
        <Field data-invalid={!!errors.close}>
          <FieldLabel htmlFor={id("close-date")}>Ngày đóng</FieldLabel>
          <Input id={id("close-date")} type="date" value={row.closeDate} onChange={(e) => onChange({ closeDate: e.target.value })} />
        </Field>
        <Field data-invalid={!!errors.close}>
          <FieldLabel htmlFor={id("close-time")}>Giờ đóng</FieldLabel>
          <Input id={id("close-time")} type="time" value={row.closeTime} onChange={(e) => onChange({ closeTime: e.target.value })} />
        </Field>
        <FieldError className="col-span-2" errors={err(errors.close)} />
      </div>

      <p className="text-sm text-muted-foreground sm:col-span-2">
        Giờ Việt Nam (GMT+7).
        {opens ? (Date.parse(opens) > now ? ` Mở bán sau ${formatOpensIn(Date.parse(opens) - now)}.` : " Đợt này đã mở.") : null}
      </p>
      {block?.overlaps ? (
        <p role="alert" className="text-sm font-medium text-danger sm:col-span-2">
          Trùng giờ với đợt khác
        </p>
      ) : block?.tooSoon ? (
        <p className="rounded-md bg-warning/15 px-3 py-2 text-sm font-medium sm:col-span-2">Mở trong vòng 10 phút tới: sẽ không xuất bản được.</p>
      ) : null}

      <Field data-invalid={!!errors.max}>
        <FieldLabel htmlFor={id("max")}>Số vé tối đa mỗi người</FieldLabel>
        <NativeSelect id={id("max")} value={row.maxTicketsPerUser} onChange={(e) => onChange({ maxTicketsPerUser: e.target.value })}>
          {[1, 2, 3, 4, 5, 6, 7, 8].map((n) => (
            <option key={n} value={String(n)}>
              {n}
            </option>
          ))}
        </NativeSelect>
      </Field>
      <Field orientation="horizontal" className="items-center self-end">
        <input id={id("queue")} type="checkbox" className="size-4" checked={row.queueEnabled} onChange={(e) => onChange({ queueEnabled: e.target.checked })} />
        <FieldLabel htmlFor={id("queue")}>Dùng phòng chờ</FieldLabel>
      </Field>

      <div className="sm:col-span-2">
        {row.hasAccessCode && row.codeMode === "keep" ? (
          <div className="flex flex-wrap items-center gap-2 text-sm">
            <span className="font-medium">Đã đặt mã</span>
            {locked ? null : (
              <>
                <Button type="button" size="sm" variant="outline" onClick={() => onChange({ codeMode: "set", code: "" })}>
                  Đổi mã
                </Button>
                <Button type="button" size="sm" variant="ghost" onClick={() => onChange({ codeMode: "remove" })}>
                  Bỏ mã
                </Button>
              </>
            )}
          </div>
        ) : row.codeMode === "remove" ? (
          <div className="flex items-center gap-2 text-sm">
            Mã sẽ bị bỏ.
            <Button type="button" size="sm" variant="ghost" onClick={() => onChange({ codeMode: "keep" })}>
              Giữ mã cũ
            </Button>
          </div>
        ) : (
          <Field data-invalid={!!errors.code}>
            <FieldLabel htmlFor={id("code")}>Mã presale (tuỳ chọn)</FieldLabel>
            <div className="flex gap-2">
              <Input id={id("code")} value={row.code} autoComplete="off" onChange={(e) => onChange({ code: e.target.value, codeMode: "set" })} />
              <Button type="button" variant="outline" onClick={() => onChange({ code: generateAccessCode(), codeMode: "set" })}>
                <Shuffle aria-hidden />
                Tạo mã ngẫu nhiên
              </Button>
            </div>
            <FieldDescription>Mã được mã hoá khi lưu, sau đó không xem lại được. Hãy sao chép trước khi lưu.</FieldDescription>
            <FieldError errors={err(errors.code)} />
          </Field>
        )}
      </div>

      {locked ? null : (
        <Button type="button" variant="ghost" size="sm" className="justify-self-start text-danger sm:col-span-2" onClick={onRemove}>
          <Trash2 aria-hidden />
          Xoá đợt
        </Button>
      )}
    </section>
  );
}
