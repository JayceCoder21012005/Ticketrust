"use client";

import { useQuery } from "@tanstack/react-query";
import { X } from "lucide-react";
import { useState } from "react";
import { z } from "zod";
import { Button } from "@/components/ui/button";
import { Field, FieldDescription, FieldError, FieldGroup, FieldLabel } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Textarea } from "@/components/ui/textarea";
import { CATEGORIES } from "@/lib/catalog/categories";
import { toParagraphs } from "@/lib/catalog/paragraphs";
import { formatVietnam, fromIsoToVietnam, toVietnamIso } from "@/lib/vn-time";
import { listVenues, organizerKeys, putInfo, putSchedule } from "../../api";
import { NativeSelect } from "../../native-select";
import { LockedMark } from "../locked-banner";
import { useStepAutosave, type StepProps } from "../step-props";
import { CoverField } from "./cover-field";

const MAX_LINEUP = 20;

const schema = z
  .object({
    title: z.string().trim().min(3, "Tên cần ít nhất 3 ký tự").max(200, "Tên tối đa 200 ký tự"),
    description: z.string().max(5000, "Mô tả tối đa 5.000 ký tự"),
    lineup: z.array(z.string()).max(MAX_LINEUP),
    coverImageUrl: z.string().nullable(),
    category: z.string().min(1, "Chọn danh mục"),
    venueId: z.string().min(1, "Chọn địa điểm"),
    startDate: z.string().min(1, "Chọn ngày"),
    startTime: z.string().min(1, "Chọn giờ"),
    endDate: z.string().min(1, "Chọn ngày"),
    endTime: z.string().min(1, "Chọn giờ"),
  })
  .refine((v) => `${v.endDate}T${v.endTime}` > `${v.startDate}T${v.startTime}`, { path: ["endTime"], message: "Giờ kết thúc phải sau giờ bắt đầu" });

type Values = z.infer<typeof schema>;

function initialValues(event: StepProps["event"]): Values {
  const start = fromIsoToVietnam(event.startsAt);
  const end = fromIsoToVietnam(event.endsAt);
  return {
    title: event.title,
    description: event.description,
    lineup: event.lineup,
    coverImageUrl: event.coverImageUrl,
    category: event.category,
    venueId: event.venueId,
    startDate: start.date,
    startTime: start.time,
    endDate: end.date,
    endTime: end.time,
  };
}

/** Step 1 (design §3.3). Text and cover stay editable after publishing; category, venue and dates are frozen. */
export function InfoStep(props: StepProps) {
  const { event, onSaved } = props;
  const [values, setValues] = useState(() => initialValues(event));
  const [touched, setTouched] = useState<Partial<Record<keyof Values, boolean>>>({});
  const venues = useQuery({ queryKey: organizerKeys.venues, queryFn: listVenues });
  const locked = event.status !== "Draft";
  const parsed = schema.safeParse(values);
  const errors: Partial<Record<keyof Values, string>> = parsed.success ? {} : Object.fromEntries(parsed.error.issues.map((i) => [String(i.path[0]), i.message]));
  // The api refuses a start before a sale phase closes (phase.after_event_start): say so instead of failing the save.
  const lastClose = event.phases.reduce<string | null>((last, p) => (last === null || Date.parse(p.closesAt) > Date.parse(last) ? p.closesAt : last), null);
  if (parsed.success && !locked && lastClose && Date.parse(toVietnamIso(values.startDate, values.startTime)) < Date.parse(lastClose)) {
    errors.startDate = `Sự kiện phải bắt đầu sau khi đợt mở bán cuối kết thúc (${formatVietnam(lastClose)}).`;
  }

  useStepAutosave(props, {
    value: values,
    valid: Object.keys(errors).length === 0,
    errorCount: Object.keys(errors).length,
    save: async (v, current) => {
      const info = { title: v.title.trim(), description: v.description, lineup: v.lineup, coverImageUrl: v.coverImageUrl };
      const infoChanged =
        info.title !== current.title ||
        info.description !== current.description ||
        info.coverImageUrl !== current.coverImageUrl ||
        JSON.stringify(info.lineup) !== JSON.stringify(current.lineup);
      if (infoChanged) {
        current = await putInfo(current.id, { version: current.version, ...info });
        onSaved(current);
      }

      const schedule = {
        category: v.category,
        venueId: v.venueId,
        startsAt: toVietnamIso(v.startDate, v.startTime),
        endsAt: toVietnamIso(v.endDate, v.endTime),
      };
      const scheduleChanged =
        schedule.category !== current.category ||
        schedule.venueId !== current.venueId ||
        Date.parse(schedule.startsAt) !== Date.parse(current.startsAt) ||
        Date.parse(schedule.endsAt) !== Date.parse(current.endsAt);
      if (scheduleChanged && current.status === "Draft") {
        onSaved(await putSchedule(current.id, { version: current.version, ...schedule }));
      }
    },
  });

  const set = <K extends keyof Values>(key: K, value: Values[K]) => {
    setValues((v) => ({ ...v, [key]: value }));
    setTouched((t) => ({ ...t, [key]: true }));
  };
  const error = (key: keyof Values) => (touched[key] && errors[key] ? [{ message: errors[key] }] : undefined);
  const lockMark = locked ? <LockedMark /> : null;

  return (
    <FieldGroup>
      <Field data-invalid={!!error("title")}>
        <FieldLabel htmlFor="info-title">Tên sự kiện</FieldLabel>
        <Input id="info-title" value={values.title} aria-invalid={!!error("title")} onChange={(e) => set("title", e.target.value)} />
        <FieldError errors={error("title")} />
      </Field>

      <div className="grid gap-4 sm:grid-cols-2">
        <Field>
          <FieldLabel htmlFor="info-category">
            Danh mục {lockMark}
          </FieldLabel>
          <NativeSelect id="info-category" value={values.category} disabled={locked} onChange={(e) => set("category", e.target.value)}>
            {CATEGORIES.map((c) => (
              <option key={c.slug} value={c.slug}>
                {c.name}
              </option>
            ))}
          </NativeSelect>
        </Field>
        <Field>
          <FieldLabel htmlFor="info-venue">
            Địa điểm {lockMark}
          </FieldLabel>
          <NativeSelect id="info-venue" value={values.venueId} disabled={locked} onChange={(e) => set("venueId", e.target.value)}>
            {venues.data?.some((v) => v.id === values.venueId) ? null : <option value={values.venueId}>Địa điểm hiện tại</option>}
            {venues.data?.map((v) => (
              <option key={v.id} value={v.id}>
                {v.name} · {v.city}
              </option>
            ))}
          </NativeSelect>
        </Field>
      </div>

      <fieldset className="grid grid-cols-2 gap-4 sm:grid-cols-4">
        <legend className="mb-2 text-sm font-medium">Thời gian (giờ Việt Nam, GMT+7)</legend>
        {(
          [
            ["startDate", "Ngày bắt đầu", "date"],
            ["startTime", "Giờ bắt đầu", "time"],
            ["endDate", "Ngày kết thúc", "date"],
            ["endTime", "Giờ kết thúc", "time"],
          ] as const
        ).map(([key, label, type]) => (
          <Field key={key} data-invalid={!!error(key)}>
            <FieldLabel htmlFor={`info-${key}`}>
              {label} {lockMark}
            </FieldLabel>
            <Input id={`info-${key}`} type={type} value={values[key]} disabled={locked} aria-invalid={!!error(key)} onChange={(e) => set(key, e.target.value)} />
            <FieldError errors={error(key)} />
          </Field>
        ))}
      </fieldset>

      <LineupField value={values.lineup} onChange={(lineup) => set("lineup", lineup)} />

      <Field data-invalid={!!error("description")}>
        <FieldLabel htmlFor="info-description">Giới thiệu</FieldLabel>
        <Tabs defaultValue="write">
          <TabsList>
            <TabsTrigger value="write">Soạn</TabsTrigger>
            <TabsTrigger value="preview">Xem trước</TabsTrigger>
          </TabsList>
          <TabsContent value="write">
            <Textarea id="info-description" rows={8} value={values.description} onChange={(e) => set("description", e.target.value)} />
            <FieldDescription>Văn bản thuần. Để một dòng trống giữa các đoạn.</FieldDescription>
          </TabsContent>
          <TabsContent value="preview">
            <div data-testid="description-preview" className="flex min-h-32 flex-col gap-3 rounded-md border p-3 leading-relaxed">
              {toParagraphs(values.description).length > 0 ? (
                toParagraphs(values.description).map((paragraph, i) => (
                  <p key={i} className="whitespace-pre-line">
                    {paragraph}
                  </p>
                ))
              ) : (
                <span className="text-muted-foreground">Chưa có giới thiệu.</span>
              )}
            </div>
          </TabsContent>
        </Tabs>
        <FieldError errors={error("description")} />
      </Field>

      <CoverField eventId={event.id} value={values.coverImageUrl} onChange={(url) => set("coverImageUrl", url)} />
    </FieldGroup>
  );
}

function LineupField({ value, onChange }: { value: string[]; onChange: (lineup: string[]) => void }) {
  const [draft, setDraft] = useState("");
  const add = () => {
    const name = draft.trim();
    if (name && !value.includes(name) && value.length < MAX_LINEUP) {
      onChange([...value, name]);
    }

    setDraft("");
  };

  return (
    <Field>
      <FieldLabel htmlFor="info-lineup">Nghệ sĩ</FieldLabel>
      {value.length > 0 ? (
        <ul className="flex flex-wrap gap-2">
          {value.map((artist) => (
            <li key={artist} className="flex items-center gap-1 rounded-full border bg-muted px-3 py-1 text-sm">
              {artist}
              <Button type="button" size="icon" variant="ghost" className="size-5" aria-label={`Bỏ ${artist}`} onClick={() => onChange(value.filter((a) => a !== artist))}>
                <X aria-hidden />
              </Button>
            </li>
          ))}
        </ul>
      ) : null}
      <Input
        id="info-lineup"
        value={draft}
        placeholder="Gõ tên rồi nhấn Enter"
        onChange={(e) => setDraft(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === "Enter") {
            e.preventDefault();
            add();
          }
        }}
        onBlur={add}
      />
      <FieldDescription>Tối đa {MAX_LINEUP} nghệ sĩ.</FieldDescription>
    </Field>
  );
}
