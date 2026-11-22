"use client";

import { CalendarDays, Search, ShoppingBag, User } from "lucide-react";
import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { Button } from "@/components/ui/button";
import {
  Command,
  CommandDialog,
  CommandEmpty,
  CommandGroup,
  CommandInput,
  CommandItem,
  CommandList,
} from "@/components/ui/command";
import { ADMIN_NAV } from "./nav";

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** Where a typed id or email can lead. An id could be a user, an order or an event, so each is offered. */
export function jumpTargets(
  input: string,
): { label: string; href: string; kind: "user" | "order" | "event" }[] {
  const value = input.trim();
  if (GUID.test(value)) {
    return [
      {
        label: `Người dùng ${value}`,
        href: `/admin/users/${value}`,
        kind: "user",
      },
      {
        label: `Đơn hàng ${value}`,
        href: `/admin/orders?q=${value}`,
        kind: "order",
      },
      {
        label: `Sự kiện ${value}`,
        href: `/admin/events?q=${value}`,
        kind: "event",
      },
    ];
  }

  if (value.includes("@")) {
    const q = encodeURIComponent(value);
    return [
      {
        label: `Người dùng có email ${value}`,
        href: `/admin/users?q=${q}`,
        kind: "user",
      },
      {
        label: `Đơn hàng của ${value}`,
        href: `/admin/orders?q=${q}`,
        kind: "order",
      },
    ];
  }

  return value.length >= 2
    ? [
        {
          label: `Tìm sự kiện "${value}"`,
          href: `/admin/events?q=${encodeURIComponent(value)}`,
          kind: "event",
        },
      ]
    : [];
}

const icons = { user: User, order: ShoppingBag, event: CalendarDays };

/** ⌘K / Ctrl+K: jump to a user, order or event by id or email, or to any admin page. */
export function CommandPalette() {
  const router = useRouter();
  const [open, setOpen] = useState(false);
  const [value, setValue] = useState("");

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.key.toLowerCase() === "k" && (event.metaKey || event.ctrlKey)) {
        event.preventDefault();
        setOpen((o) => !o);
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);

  const go = (href: string) => {
    setOpen(false);
    setValue("");
    router.push(href);
  };

  const targets = jumpTargets(value);
  return (
    <>
      <Button
        type="button"
        variant="outline"
        size="sm"
        onClick={() => setOpen(true)}
        className="hidden gap-2 text-muted-foreground sm:inline-flex"
      >
        <Search aria-hidden className="size-4" />
        Tìm nhanh
        <kbd className="rounded border px-1 text-[10px]">⌘K</kbd>
      </Button>
      <CommandDialog
        open={open}
        onOpenChange={setOpen}
        title="Tìm nhanh"
        description="Nhập id hoặc email của người dùng, đơn hàng, sự kiện"
      >
        {/* This CommandDialog does not wrap its children in a Command: the palette brings its own. */}
        <Command>
          <CommandInput
            placeholder="Id hoặc email…"
            value={value}
            onValueChange={setValue}
          />
          <CommandList>
            <CommandEmpty>Không có gợi ý.</CommandEmpty>
            {targets.length > 0 ? (
              <CommandGroup heading="Đến">
                {targets.map((target) => {
                  const Icon = icons[target.kind];
                  return (
                    <CommandItem
                      key={target.href}
                      value={target.label}
                      onSelect={() => go(target.href)}
                    >
                      <Icon aria-hidden />
                      {target.label}
                    </CommandItem>
                  );
                })}
              </CommandGroup>
            ) : null}
            <CommandGroup heading="Trang">
              {ADMIN_NAV.map((item) => (
                <CommandItem
                  key={item.href}
                  value={item.label}
                  onSelect={() => go(item.href)}
                >
                  <item.icon aria-hidden />
                  {item.label}
                </CommandItem>
              ))}
            </CommandGroup>
          </CommandList>
        </Command>
      </CommandDialog>
    </>
  );
}
