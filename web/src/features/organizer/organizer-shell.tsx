"use client";

import { CalendarDays, LayoutDashboard, MapPin, Settings, Ticket } from "lucide-react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { Fragment, type ReactNode } from "react";
import { Badge } from "@/components/ui/badge";
import { Separator } from "@/components/ui/separator";
import {
  Sidebar,
  SidebarContent,
  SidebarGroup,
  SidebarGroupContent,
  SidebarHeader,
  SidebarInset,
  SidebarMenu,
  SidebarMenuButton,
  SidebarMenuItem,
  SidebarProvider,
  SidebarTrigger,
} from "@/components/ui/sidebar";

export type Crumb = { label: string; href?: string };

const NAV = [
  { label: "Sự kiện", href: "/organizer/events", icon: CalendarDays },
  { label: "Địa điểm", href: "/organizer/venues", icon: MapPin },
  // Spec 15 (dashboard) and later settings: shown so the area's shape is clear, not clickable yet.
  { label: "Dashboard", icon: LayoutDashboard },
  { label: "Cài đặt", icon: Settings },
] as const;

/** Spec 05 organizer chrome: shadcn Sidebar + breadcrumb header. */
export function OrganizerShell({ breadcrumbs, actions, children }: { breadcrumbs: Crumb[]; actions?: ReactNode; children: ReactNode }) {
  const pathname = usePathname();

  return (
    <SidebarProvider>
      <Sidebar collapsible="icon">
        <SidebarHeader>
          <Link href="/organizer/events" prefetch={false} className="flex items-center gap-2 px-2 py-1.5 font-semibold">
            <Ticket aria-hidden className="size-5 text-primary" />
            <span className="group-data-[collapsible=icon]:hidden">TicketRush · Nhà tổ chức</span>
          </Link>
        </SidebarHeader>
        <SidebarContent>
          <SidebarGroup>
            <SidebarGroupContent>
              <SidebarMenu>
                {NAV.map((item) => (
                  <SidebarMenuItem key={item.label}>
                    {"href" in item ? (
                      <SidebarMenuButton asChild isActive={pathname.startsWith(item.href)} tooltip={item.label}>
                        <Link href={item.href} prefetch={false}>
                          <item.icon aria-hidden />
                          <span>{item.label}</span>
                        </Link>
                      </SidebarMenuButton>
                    ) : (
                      <SidebarMenuButton disabled aria-disabled tooltip={`${item.label} (sắp có)`}>
                        <item.icon aria-hidden />
                        <span>{item.label}</span>
                        <Badge variant="outline" className="ml-auto">
                          Sắp có
                        </Badge>
                      </SidebarMenuButton>
                    )}
                  </SidebarMenuItem>
                ))}
              </SidebarMenu>
            </SidebarGroupContent>
          </SidebarGroup>
        </SidebarContent>
      </Sidebar>
      <SidebarInset id="main">
        <header className="flex h-14 shrink-0 items-center gap-2 border-b px-4">
          <SidebarTrigger aria-label="Mở hoặc thu gọn menu" />
          <Separator orientation="vertical" className="mr-1 h-4" />
          <nav aria-label="Đường dẫn" className="min-w-0 flex-1">
            <ol className="flex min-w-0 items-center gap-1.5 text-sm text-muted-foreground">
              {breadcrumbs.map((crumb, i) => (
                <Fragment key={`${crumb.label}-${i}`}>
                  {i > 0 ? <li aria-hidden>›</li> : null}
                  <li className="truncate">
                    {crumb.href ? (
                      <Link href={crumb.href} prefetch={false} className="hover:text-foreground">
                        {crumb.label}
                      </Link>
                    ) : (
                      <span aria-current="page" className="font-medium text-foreground">
                        {crumb.label}
                      </span>
                    )}
                  </li>
                </Fragment>
              ))}
            </ol>
          </nav>
          {actions}
        </header>
        <div className="flex flex-1 flex-col">{children}</div>
      </SidebarInset>
    </SidebarProvider>
  );
}
