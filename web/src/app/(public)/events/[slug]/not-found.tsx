import { CalendarX } from "lucide-react";
import Link from "next/link";
import { EmptyState } from "@/components/shared/empty-state";
import { Button } from "@/components/ui/button";
import { t } from "@/lib/i18n.server";

export default function EventNotFound() {
  return (
    <div className="mx-auto flex w-full max-w-content flex-1 flex-col justify-center px-4 py-16">
      <h1 className="sr-only">{t("catalog.detail.notFoundTitle")}</h1>
      <EmptyState
        icon={CalendarX}
        title={t("catalog.detail.notFoundTitle")}
        description={t("catalog.detail.notFoundDescription")}
        action={
          <Button asChild>
            <Link href="/events">{t("catalog.detail.backToEvents")}</Link>
          </Button>
        }
      />
    </div>
  );
}
