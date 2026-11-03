import { Ticket } from "lucide-react";
import { AuthProvider } from "@/components/auth/auth-provider";
import { t } from "@/lib/i18n";

/**
 * Centred card on mobile, split with brand artwork on desktop (spec 03). The artwork is CSS only:
 * no image to download, so it can never become the LCP element or shift the layout.
 */
export default function AuthLayout({ children }: LayoutProps<"/">) {
  return (
    <AuthProvider>
      <div className="mx-auto grid w-full max-w-content flex-1 items-center gap-10 px-4 py-10 lg:grid-cols-2">
        <aside className="relative hidden h-full min-h-[32rem] overflow-hidden rounded-3xl bg-primary p-10 text-primary-foreground lg:flex lg:flex-col lg:justify-end">
          <div
            aria-hidden
            className="absolute inset-0 bg-[radial-gradient(circle_at_20%_20%,oklch(1_0_0/0.25),transparent_45%),radial-gradient(circle_at_80%_70%,oklch(0.7_0.2_330/0.6),transparent_50%)]"
          />
          <Ticket aria-hidden className="absolute right-10 top-10 size-24 -rotate-12 opacity-30" />
          <div className="relative flex flex-col gap-3">
            <p className="text-3xl font-bold leading-tight">{t("auth.artworkTitle")}</p>
            <p className="max-w-sm text-primary-foreground/85">{t("auth.artworkDescription")}</p>
          </div>
        </aside>
        <div className="mx-auto w-full max-w-md rounded-2xl border bg-card p-6 text-card-foreground shadow-sm sm:p-8">
          {children}
        </div>
      </div>
    </AuthProvider>
  );
}
