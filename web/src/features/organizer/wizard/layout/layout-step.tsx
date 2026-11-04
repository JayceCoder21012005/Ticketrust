"use client";

import { Plus, Trash2 } from "lucide-react";
import Link from "next/link";
import { useMemo, useState } from "react";
import { Button } from "@/components/ui/button";
import { Field, FieldError, FieldLabel } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { generateSeats, validateLayout, type LayoutError } from "@/lib/organizer/seat-layout";
import { cn } from "@/lib/utils";
import { putLayout } from "../../api";
import { NativeSelect } from "../../native-select";
import type { SectionDefinition, SectionKind, TicketKind } from "../../types";
import { LockedBanner } from "../locked-banner";
import { useStepAutosave, type StepProps } from "../step-props";
import { LayoutPreview } from "./layout-preview";
import { PRESETS } from "./presets";

const cardId = (code: string) => `section-card-${code}`;

/** Step 3 (design §3.3): sections as forms on the left, the live SVG preview (computed in the browser) on the right. */
export function LayoutStep(props: StepProps) {
  const { event, onSaved } = props;
  const locked = event.status !== "Draft";
  const [sections, setSections] = useState<SectionDefinition[]>(() => event.layout?.sections ?? []);
  const [active, setActive] = useState<string | null>(null);
  const [preset, setPreset] = useState("");
  const kinds = useMemo(() => Object.fromEntries(event.tiers.map((t) => [t.id, t.kind])) as Record<string, TicketKind>, [event.tiers]);
  const errors = useMemo(() => validateLayout({ sections }, kinds), [sections, kinds]);
  const seats = useMemo(() => {
    // Preview whatever is valid; a layout over the seat limit isn't drawn at all.
    if (errors.some((e) => e.section === "")) return [];
    const broken = new Set(errors.map((e) => e.section));
    return generateSeats({ sections: sections.filter((s) => !broken.has(s.code)) });
  }, [sections, errors]);

  useStepAutosave(props, {
    value: sections,
    valid: !locked && errors.length === 0,
    errorCount: errors.length,
    save: async (value, current) => onSaved(await putLayout(current.id, current.version, value)),
  });

  if (event.tiers.length === 0) {
    return (
      <p className="text-muted-foreground">
        Sơ đồ cần hạng vé để tô màu các khu.{" "}
        <Link href={`/organizer/events/${event.id}/tiers`} prefetch={false} className="font-medium text-primary hover:underline">
          Tạo hạng vé
        </Link>{" "}
        trước nhé.
      </p>
    );
  }

  const update = (index: number, patch: Partial<SectionDefinition>) =>
    setSections((all) => all.map((s, i) => (i === index ? { ...s, ...patch } : s)));
  const focus = (code: string) => {
    setActive(code);
    document.getElementById(cardId(code))?.scrollIntoView({ behavior: "smooth", block: "nearest" });
  };
  const addSection = () => {
    const seated = event.tiers.find((t) => t.kind === "Seated") ?? event.tiers[0];
    const code = nextCode(sections);
    setSections((all) => [
      ...all,
      seated.kind === "Seated"
        ? { code, tierId: seated.id, kind: "seated", rows: 5, seatsPerRow: 10, origin: { x: 100, y: 120 }, curve: 0 }
        : { code, tierId: seated.id, kind: "standing", origin: { x: 380, y: 400 }, curve: 0 },
    ]);
  };

  return (
    <div className="flex flex-col gap-4">
      {locked ? <LockedBanner /> : null}
      <div className="grid gap-6 xl:grid-cols-[22rem_1fr]">
        <div className="flex flex-col gap-4">
          {locked ? null : (
            <div className="flex items-end gap-2">
              <Field className="flex-1">
                <FieldLabel htmlFor="layout-preset">Bắt đầu từ mẫu</FieldLabel>
                <NativeSelect id="layout-preset" value={preset} onChange={(e) => setPreset(e.target.value)}>
                  <option value="">Chọn mẫu…</option>
                  {PRESETS.map((p) => (
                    <option key={p.id} value={p.id}>
                      {p.name}
                    </option>
                  ))}
                </NativeSelect>
              </Field>
              <Button
                type="button"
                variant="outline"
                disabled={!preset}
                onClick={() => setSections(PRESETS.find((p) => p.id === preset)!.sections(event.tiers))}
              >
                Dùng mẫu
              </Button>
            </div>
          )}
          {sections.map((section, i) => (
            <SectionCard
              key={i}
              section={section}
              tiers={event.tiers}
              errors={errors.filter((e) => e.section === section.code)}
              active={section.code === active}
              locked={locked}
              onChange={(patch) => update(i, patch)}
              onRemove={() => setSections((all) => all.filter((_, j) => j !== i))}
              onFocus={() => setActive(section.code)}
            />
          ))}
          {errors
            .filter((e) => e.section === "")
            .map((e) => (
              <p key={e.field} role="alert" className="text-sm text-danger">
                {e.message}
              </p>
            ))}
          {locked ? null : (
            <Button type="button" variant="outline" className="self-start" onClick={addSection}>
              <Plus aria-hidden />
              Thêm khu
            </Button>
          )}
        </div>
        <div className="flex flex-col gap-3 xl:sticky xl:top-4 xl:self-start">
          <LayoutPreview sections={sections} tiers={event.tiers} seats={seats} activeSection={active} onSectionClick={focus} />
          <ul aria-label="Số chỗ theo hạng vé" className="flex flex-wrap gap-x-6 gap-y-2 text-sm">
            {event.tiers.map((tier) => (
              <li key={tier.id} className="flex items-center gap-2">
                <span aria-hidden className="size-3 rounded-full" style={{ backgroundColor: tier.color }} />
                {tier.name}:{" "}
                {tier.kind === "Seated"
                  ? `${seats.filter((s) => s.tierId === tier.id).length.toLocaleString("vi-VN")} ghế`
                  : `${(tier.capacity ?? 0).toLocaleString("vi-VN")} chỗ đứng`}
              </li>
            ))}
          </ul>
        </div>
      </div>
    </div>
  );
}

function nextCode(sections: SectionDefinition[]): string {
  const used = new Set(sections.map((s) => s.code.toUpperCase()));
  for (let i = 0; ; i++) {
    const code = i < 26 ? String.fromCharCode(65 + i) : `K${i - 25}`;
    if (!used.has(code)) return code;
  }
}

type SectionCardProps = {
  section: SectionDefinition;
  tiers: StepProps["event"]["tiers"];
  errors: LayoutError[];
  active: boolean;
  locked: boolean;
  onChange: (patch: Partial<SectionDefinition>) => void;
  onRemove: () => void;
  onFocus: () => void;
};

function SectionCard({ section, tiers, errors, active, locked, onChange, onRemove, onFocus }: SectionCardProps) {
  const kind = section.kind ?? "seated";
  const error = (field: string) => {
    const found = errors.filter((e) => e.field === field);
    return found.length > 0 ? found.map((e) => ({ message: e.message })) : undefined;
  };
  const number = (value: string) => (value === "" ? null : Number(value));
  const id = (field: string) => `section-${section.code}-${field}`;
  const matching = tiers.filter((t) => t.kind === (kind === "standing" ? "GeneralAdmission" : "Seated"));

  return (
    <fieldset
      id={cardId(section.code)}
      role="group"
      aria-label={`Khu ${section.code}`}
      data-active={active}
      disabled={locked}
      onFocus={onFocus}
      className={cn("grid scroll-mt-4 grid-cols-2 gap-3 rounded-xl border p-4", active && "border-primary ring-2 ring-primary/30")}
    >
      <Field data-invalid={!!error("code")}>
        <FieldLabel htmlFor={id("code")}>Mã khu</FieldLabel>
        <Input id={id("code")} value={section.code} maxLength={8} onChange={(e) => onChange({ code: e.target.value.toUpperCase() })} />
        <FieldError errors={error("code")} />
      </Field>
      <Field>
        <FieldLabel htmlFor={id("kind")}>Kiểu</FieldLabel>
        <NativeSelect
          id={id("kind")}
          value={kind}
          onChange={(e) => {
            const next = e.target.value as SectionKind;
            const tier = tiers.find((t) => t.kind === (next === "standing" ? "GeneralAdmission" : "Seated"));
            onChange({ kind: next, tierId: tier?.id ?? "" });
          }}
        >
          <option value="seated">Ngồi</option>
          <option value="standing">Đứng</option>
        </NativeSelect>
      </Field>
      <Field className="col-span-2" data-invalid={!!error("tierId")}>
        <FieldLabel htmlFor={id("tier")}>Hạng vé</FieldLabel>
        <NativeSelect id={id("tier")} value={section.tierId} onChange={(e) => onChange({ tierId: e.target.value })}>
          <option value="">Chọn hạng…</option>
          {matching.map((t) => (
            <option key={t.id} value={t.id}>
              {t.name}
            </option>
          ))}
        </NativeSelect>
        <FieldError errors={error("tierId")} />
      </Field>
      {kind === "seated" ? (
        <>
          <Field data-invalid={!!error("rows")}>
            <FieldLabel htmlFor={id("rows")}>Số hàng</FieldLabel>
            <Input id={id("rows")} inputMode="numeric" value={section.rows ?? ""} onChange={(e) => onChange({ rows: number(e.target.value) })} />
            <FieldError errors={error("rows")} />
          </Field>
          <Field data-invalid={!!error("seatsPerRow")}>
            <FieldLabel htmlFor={id("perRow")}>Ghế mỗi hàng</FieldLabel>
            <Input id={id("perRow")} inputMode="numeric" value={section.seatsPerRow ?? ""} onChange={(e) => onChange({ seatsPerRow: number(e.target.value) })} />
            <FieldError errors={error("seatsPerRow")} />
          </Field>
          <Field className="col-span-2" data-invalid={!!error("curve")}>
            <FieldLabel htmlFor={id("curve")}>Độ cong ({section.curve.toFixed(2)})</FieldLabel>
            <input id={id("curve")} type="range" min={0} max={0.5} step={0.01} value={section.curve} onChange={(e) => onChange({ curve: Number(e.target.value) })} />
            <FieldError errors={error("curve")} />
          </Field>
        </>
      ) : null}
      <Field data-invalid={!!error("origin")}>
        <FieldLabel htmlFor={id("x")}>Vị trí X</FieldLabel>
        <Input
          id={id("x")}
          inputMode="numeric"
          value={section.origin?.x ?? ""}
          onChange={(e) => onChange({ origin: { x: number(e.target.value) ?? NaN, y: section.origin?.y ?? 0 } })}
        />
      </Field>
      <Field data-invalid={!!error("origin")}>
        <FieldLabel htmlFor={id("y")}>Vị trí Y</FieldLabel>
        <Input
          id={id("y")}
          inputMode="numeric"
          value={section.origin?.y ?? ""}
          onChange={(e) => onChange({ origin: { x: section.origin?.x ?? 0, y: number(e.target.value) ?? NaN } })}
        />
      </Field>
      <FieldError className="col-span-2" errors={error("origin")} />
      {locked ? null : (
        <Button type="button" variant="ghost" size="sm" className="col-span-2 justify-self-end text-danger" onClick={onRemove}>
          <Trash2 aria-hidden />
          Xoá khu {section.code}
        </Button>
      )}
    </fieldset>
  );
}
