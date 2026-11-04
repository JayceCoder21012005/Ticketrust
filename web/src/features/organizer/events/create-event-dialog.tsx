"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQuery } from "@tanstack/react-query";
import { Plus } from "lucide-react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle, DialogTrigger } from "@/components/ui/dialog";
import { Field, FieldError, FieldGroup, FieldLabel } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { FormAlert } from "@/features/auth/form-alert";
import { CATEGORIES } from "@/lib/catalog/categories";
import { toFriendlyMessage } from "@/lib/errors";
import { toVietnamIso } from "@/lib/vn-time";
import { createEvent, listVenues, organizerKeys } from "../api";
import { NativeSelect } from "../native-select";

const schema = z
  .object({
    title: z.string().trim().min(3, "Tên cần ít nhất 3 ký tự").max(200, "Tên tối đa 200 ký tự"),
    category: z.string().min(1, "Chọn danh mục"),
    venueId: z.string().min(1, "Chọn địa điểm"),
    startDate: z.string().min(1, "Chọn ngày"),
    startTime: z.string().min(1, "Chọn giờ"),
    endDate: z.string().min(1, "Chọn ngày"),
    endTime: z.string().min(1, "Chọn giờ"),
  })
  .refine((v) => `${v.endDate}T${v.endTime}` > `${v.startDate}T${v.startTime}`, {
    path: ["endTime"],
    message: "Giờ kết thúc phải sau giờ bắt đầu",
  });

type Values = z.infer<typeof schema>;

/** Luma-style quick create (design §3.2): five fields make a draft, the wizard does the rest. */
export function CreateEventDialog() {
  const router = useRouter();
  const [open, setOpen] = useState(false);
  const venues = useQuery({ queryKey: organizerKeys.venues, queryFn: listVenues, enabled: open });
  const form = useForm<Values>({
    resolver: zodResolver(schema),
    defaultValues: { title: "", category: "", venueId: "", startDate: "", startTime: "", endDate: "", endTime: "" },
  });
  const create = useMutation({
    mutationFn: (v: Values) =>
      createEvent({
        title: v.title.trim(),
        category: v.category,
        venueId: v.venueId,
        startsAt: toVietnamIso(v.startDate, v.startTime),
        endsAt: toVietnamIso(v.endDate, v.endTime),
      }),
    onSuccess: (event) => router.push(`/organizer/events/${event.id}/info`),
  });
  const errors = form.formState.errors;

  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogTrigger asChild>
        <Button>
          <Plus aria-hidden />
          Tạo sự kiện
        </Button>
      </DialogTrigger>
      <DialogContent className="sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>Tạo sự kiện</DialogTitle>
          <DialogDescription>Chỉ cần vài thông tin để có bản nháp. Mọi giờ là giờ Việt Nam (GMT+7).</DialogDescription>
        </DialogHeader>
        <form noValidate onSubmit={form.handleSubmit((v) => create.mutate(v))}>
          <FieldGroup>
            {create.isError ? <FormAlert message={toFriendlyMessage(create.error)} /> : null}
            <Field data-invalid={!!errors.title}>
              <FieldLabel htmlFor="new-title">Tên sự kiện</FieldLabel>
              <Input id="new-title" aria-invalid={!!errors.title} {...form.register("title")} />
              <FieldError errors={[errors.title]} />
            </Field>
            <div className="grid gap-4 sm:grid-cols-2">
              <Field data-invalid={!!errors.category}>
                <FieldLabel htmlFor="new-category">Danh mục</FieldLabel>
                <NativeSelect id="new-category" aria-invalid={!!errors.category} {...form.register("category")}>
                  <option value="">Chọn…</option>
                  {CATEGORIES.map((c) => (
                    <option key={c.slug} value={c.slug}>
                      {c.name}
                    </option>
                  ))}
                </NativeSelect>
                <FieldError errors={[errors.category]} />
              </Field>
              <Field data-invalid={!!errors.venueId}>
                <FieldLabel htmlFor="new-venue">Địa điểm</FieldLabel>
                <NativeSelect id="new-venue" aria-invalid={!!errors.venueId} {...form.register("venueId")}>
                  <option value="">{venues.isPending ? "Đang tải…" : "Chọn…"}</option>
                  {venues.data?.map((v) => (
                    <option key={v.id} value={v.id}>
                      {v.name} · {v.city}
                    </option>
                  ))}
                </NativeSelect>
                <FieldError errors={[errors.venueId]} />
                <Link href="/organizer/venues" prefetch={false} className="text-sm text-primary hover:underline">
                  Thêm địa điểm
                </Link>
              </Field>
            </div>
            <div className="grid grid-cols-2 gap-4">
              <Field data-invalid={!!errors.startDate}>
                <FieldLabel htmlFor="new-start-date">Ngày bắt đầu</FieldLabel>
                <Input id="new-start-date" type="date" {...form.register("startDate")} />
                <FieldError errors={[errors.startDate]} />
              </Field>
              <Field data-invalid={!!errors.startTime}>
                <FieldLabel htmlFor="new-start-time">Giờ bắt đầu</FieldLabel>
                <Input id="new-start-time" type="time" {...form.register("startTime")} />
                <FieldError errors={[errors.startTime]} />
              </Field>
              <Field data-invalid={!!errors.endDate}>
                <FieldLabel htmlFor="new-end-date">Ngày kết thúc</FieldLabel>
                <Input id="new-end-date" type="date" {...form.register("endDate")} />
                <FieldError errors={[errors.endDate]} />
              </Field>
              <Field data-invalid={!!errors.endTime}>
                <FieldLabel htmlFor="new-end-time">Giờ kết thúc</FieldLabel>
                <Input id="new-end-time" type="time" aria-invalid={!!errors.endTime} {...form.register("endTime")} />
                <FieldError errors={[errors.endTime]} />
              </Field>
            </div>
          </FieldGroup>
          <DialogFooter className="mt-6">
            <Button type="submit" disabled={create.isPending}>
              {create.isPending ? "Đang tạo…" : "Tạo bản nháp"}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
