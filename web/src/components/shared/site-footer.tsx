import { t } from "@/lib/i18n";

type SiteFooterProps = {
  /** Passed from a cached server function in the layout, so pages stay prerenderable. */
  year?: number;
};

export function SiteFooter({ year = new Date().getFullYear() }: SiteFooterProps) {
  return (
    <footer className="border-t">
      <div className="mx-auto flex max-w-content flex-col gap-1 px-4 py-8 text-sm text-muted-foreground sm:flex-row sm:justify-between">
        <p>{t("footer.tagline")}</p>
        <p className="flex flex-col gap-1 sm:items-end">
          <span>{t("footer.rights", "vi", { year })}</span>
          <span className="text-xs">{t("footer.photos")}</span>
        </p>
      </div>
    </footer>
  );
}
