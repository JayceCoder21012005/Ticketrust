"use client";

import { ShieldCheck } from "lucide-react";
import Link from "next/link";
import { notFound, usePathname, useRouter } from "next/navigation";
import { Fragment, useEffect, type ReactNode } from "react";
import { useAuth } from "@/components/auth/auth-provider";
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
import { Skeleton } from "@/components/ui/skeleton";
import { loginRedirectUrl } from "@/lib/api/session";
import { CommandPalette } from "./command-palette";
import { ADMIN_NAV } from "./nav";

export type Crumb = { label: string; href?: string };

/**
 * UX only: the api answers 404 to anyone but an admin. A signed-in non-admin sees the not-found page, as for any unknown URL
 * (spec 16: the area is not discoverable); a visitor goes to login first.
 */
export function AdminGate({ children }: { children: ReactNode }) {
  const router = useRouter();
  const { status, user } = useAuth();

  useEffect(() => {
    if (status === "anonymous") router.replace(loginRedirectUrl(window.location.pathname));
  }, [status, router]);

  if (status !== "authenticated" || !user) {
    return (
      <div className="flex flex-col gap-4 p-6" aria-busy="true">
        <Skeleton className="h-8 w-64" />
        <Skeleton className="h-64 w-full" />
      </div>
    );
  }

  if (!user.roles.includes("Admin")) notFound();
  return children;
}

/**
 * Spec 16 admin chrome: the organizer area's sidebar and breadcrumb, with an amber accent (stripe, badge, active item) so an
 * admin always knows where they are, and the ⌘K palette.
 */
export function AdminShell({ breadcrumbs, actions, children }: { breadcrumbs: Crumb[]; actions?: ReactNode; children: ReactNode }) {
  const pathname = usePathname();

  return (
    <SidebarProvider>
      <Sidebar collapsible="icon" className="border-t-4 border-t-amber-500">
        <SidebarHeader>
          <Link href="/admin/users" prefetch={false} className="flex items-center gap-2 px-2 py-1.5 font-semibold">
            <ShieldCheck aria-hidden className="size-5 text-amber-600 dark:text-amber-400" />
            <span className="group-data-[collapsible=icon]:hidden">TicketRush · Quản trị</span>
          </Link>
        </SidebarHeader>
        <SidebarContent>
          <SidebarGroup>
            <SidebarGroupContent>
              <SidebarMenu>
                {ADMIN_NAV.map((item) => (
                  <SidebarMenuItem key={item.href}>
                    <SidebarMenuButton
                      asChild
                      isActive={pathname.startsWith(item.href)}
                      tooltip={item.label}
                      className="data-[active=true]:bg-amber-100 data-[active=true]:text-amber-950 dark:data-[active=true]:bg-amber-950 dark:data-[active=true]:text-amber-50"
                    >
                      <Link href={item.href} prefetch={false}>
                        <item.icon aria-hidden />
                        <span>{item.label}</span>
                      </Link>
                    </SidebarMenuButton>
                  </SidebarMenuItem>
                ))}
              </SidebarMenu>
            </SidebarGroupContent>
          </SidebarGroup>
        </SidebarContent>
      </Sidebar>
      <SidebarInset id="main" className="border-t-4 border-t-amber-500">
        <header className="flex h-14 shrink-0 items-center gap-2 border-b px-4">
          <SidebarTrigger aria-label="Mở hoặc thu gọn menu" />
          <Separator orientation="vertical" className="mr-1 h-4" />
          <Badge className="bg-amber-500 text-amber-950 hover:bg-amber-500">Quản trị</Badge>
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
          <CommandPalette />
          {actions}
        </header>
        <div className="flex flex-1 flex-col gap-4 p-4 md:p-6">{children}</div>
      </SidebarInset>
    </SidebarProvider>
  );
}
