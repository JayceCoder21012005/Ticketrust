import type { Metadata } from "next";
import { Suspense } from "react";
import { Skeleton } from "@/components/ui/skeleton";
import { EventListing } from "@/features/catalog/listing/event-listing";
import { t } from "@/lib/i18n.server";

export const metadata: Metadata = {
  title: t("catalog.listing.title"),
  description: t("catalog.listing.description"),
};

/** Listing (spec 04): filters in the URL, sidebar on desktop, dialog on phones, "Xem thêm" pagination. */
export default function EventsPage({ searchParams }: PageProps<"/events">) {
  return (
    <div className="mx-auto flex w-full max-w-content flex-col gap-6 px-4 py-6 sm:py-10">
      <header className="flex flex-col gap-1">
        <h1 className="text-2xl font-bold tracking-tight sm:text-3xl">{t("catalog.listing.title")}</h1>
        <p className="text-muted-foreground">{t("catalog.listing.description")}</p>
      </header>
      <Suspense fallback={<ListingSkeleton />}>
        <EventListing searchParams={searchParams} />
      </Suspense>
    </div>
  );
}

function ListingSkeleton() {
  return (
    <div className="flex flex-col gap-6">
      <Skeleton className="h-12 w-full rounded-xl" />
      <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-3 lg:pl-[18rem]">
        {Array.from({ length: 6 }, (_, i) => (
          <Skeleton key={i} className="aspect-[4/3] rounded-xl" />
        ))}
      </div>
    </div>
  );
}
