"use client";

import { ShieldAlert } from "lucide-react";
import { useRouter } from "next/navigation";
import { useEffect, type ReactNode } from "react";
import { useAuth } from "@/components/auth/auth-provider";
import { EmptyState } from "@/components/shared/empty-state";
import { Skeleton } from "@/components/ui/skeleton";
import { loginRedirectUrl } from "@/lib/api/session";

/**
 * UX only: the api's `RequireGateStaff` policy and the event assignment are the real guards. A signed-in customer gets an
 * explanation instead of a page of 403 errors; a session that expired goes back to login.
 */
export function GateGuard({ children }: { children: ReactNode }) {
  const router = useRouter();
  const { status, user } = useAuth();

  useEffect(() => {
    if (status === "anonymous") router.replace(loginRedirectUrl(window.location.pathname));
  }, [status, router]);

  if (status !== "authenticated" || !user) {
    return (
      <div className="flex flex-col gap-4 p-6" aria-busy="true">
        <Skeleton className="h-8 w-64" />
        <Skeleton className="h-40 w-full" />
      </div>
    );
  }

  if (!user.roles.includes("GateStaff")) {
    return (
      <div className="mx-auto w-full max-w-xl p-6">
        <EmptyState
          icon={ShieldAlert}
          title="Bạn cần tài khoản nhân viên soát vé"
          description="Khu vực này dành cho nhân viên cổng. Nhờ nhà tổ chức sự kiện cấp quyền cho bạn."
        />
      </div>
    );
  }

  return <>{children}</>;
}
