"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { MapPin, Plus } from "lucide-react";
import { useState } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod";
import { EmptyState } from "@/components/shared/empty-state";
import { ErrorState } from "@/components/shared/error-state";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle, DialogTrigger } from "@/components/ui/dialog";
import { Field, FieldError, FieldGroup, FieldLabel } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { FormAlert } from "@/features/auth/form-alert";
import { externalHttpUrl } from "@/lib/catalog/safe-url";
import { toFriendlyMessage } from "@/lib/errors";
import { createVenue, listVenues, organizerKeys } from "../api";

export function VenuesTable() {
  const venues = useQuery({ queryKey: organizerKeys.venues, queryFn: listVenues });

  if (venues.isPending) {
    return <Skeleton className="h-64 w-full" />;
  }

  if (venues.isError) {
    return <ErrorState description={toFriendlyMessage(venues.error)} onRetry={() => void venues.refetch()} />;
  }

  if (venues.data.length === 0) {
    return <EmptyState icon={MapPin} title="Chưa có địa điểm" description="Thêm địa điểm để tạo sự kiện." />;
  }

  return (
    <div className="rounded-xl border">
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>Tên</TableHead>
            <TableHead className="hidden md:table-cell">Địa chỉ</TableHead>
            <TableHead>Thành phố</TableHead>
            <TableHead className="text-right">Sức chứa</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {venues.data.map((venue) => {
            const map = externalHttpUrl(venue.mapUrl);
            return (
              <TableRow key={venue.id}>
                <TableCell className="font-medium">
                  <span className="flex items-center gap-2">
                    {map ? (
                      <a href={map} target="_blank" rel="noopener noreferrer" className="hover:underline">
                        {venue.name}
                      </a>
                    ) : (
                      venue.name
                    )}
                    {venue.shared ? (
                      <Badge variant="outline" title="Địa điểm dùng chung: ai cũng dùng được, không sửa được">
                        Dùng chung
                      </Badge>
                    ) : null}
                  </span>
                </TableCell>
                <TableCell className="hidden text-muted-foreground md:table-cell">{venue.address}</TableCell>
                <TableCell>{venue.city}</TableCell>
                <TableCell className="text-right tabular-nums">{venue.capacity.toLocaleString("vi-VN")}</TableCell>
              </TableRow>
            );
          })}
        </TableBody>
      </Table>
    </div>
  );
}

const schema = z.object({
  name: z.string().trim().min(1, "Nhập tên").max(200),
  address: z.string().trim().min(1, "Nhập địa chỉ").max(300),
  city: z.string().trim().min(1, "Nhập thành phố").max(100),
  mapUrl: z
    .string()
    .trim()
    .max(500)
    .refine((v) => v === "" || externalHttpUrl(v) !== null, "Dùng liên kết bắt đầu bằng http(s)://"),
  capacity: z.coerce.number<string>().int("Nhập số nguyên").min(1, "Sức chứa phải lớn hơn 0").max(200_000),
});

type Values = z.input<typeof schema>;

export function CreateVenueDialog() {
  const queryClient = useQueryClient();
  const [open, setOpen] = useState(false);
  const form = useForm<Values, unknown, z.output<typeof schema>>({
    resolver: zodResolver(schema),
    defaultValues: { name: "", address: "", city: "", mapUrl: "", capacity: "" },
  });
  const create = useMutation({
    mutationFn: (v: z.output<typeof schema>) => createVenue({ ...v, mapUrl: v.mapUrl || null }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: organizerKeys.venues });
      form.reset();
      setOpen(false);
    },
  });
  const errors = form.formState.errors;
  const field = (name: keyof Values, label: string, props: React.ComponentProps<typeof Input> = {}) => (
    <Field data-invalid={!!errors[name]}>
      <FieldLabel htmlFor={`venue-${name}`}>{label}</FieldLabel>
      <Input id={`venue-${name}`} aria-invalid={!!errors[name]} {...props} {...form.register(name)} />
      <FieldError errors={[errors[name]]} />
    </Field>
  );

  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogTrigger asChild>
        <Button>
          <Plus aria-hidden />
          Thêm địa điểm
        </Button>
      </DialogTrigger>
      <DialogContent className="sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>Thêm địa điểm</DialogTitle>
          <DialogDescription>Địa điểm bạn thêm chỉ bạn dùng được.</DialogDescription>
        </DialogHeader>
        <form noValidate onSubmit={form.handleSubmit((v) => create.mutate(v))}>
          <FieldGroup>
            {create.isError ? <FormAlert message={toFriendlyMessage(create.error)} /> : null}
            {field("name", "Tên địa điểm")}
            {field("address", "Địa chỉ")}
            <div className="grid gap-4 sm:grid-cols-2">
              {field("city", "Thành phố")}
              {field("capacity", "Sức chứa", { inputMode: "numeric" })}
            </div>
            {field("mapUrl", "Link bản đồ (tuỳ chọn)", { type: "url", placeholder: "https://maps.google.com/…" })}
          </FieldGroup>
          <DialogFooter className="mt-6">
            <Button type="submit" disabled={create.isPending}>
              {create.isPending ? "Đang lưu…" : "Lưu địa điểm"}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
