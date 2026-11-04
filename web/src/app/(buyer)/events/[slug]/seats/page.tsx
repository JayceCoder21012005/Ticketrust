import type { Metadata } from "next";
import { notFound } from "next/navigation";
import { Suspense } from "react";
import { Skeleton } from "@/components/ui/skeleton";
import { SeatsView } from "@/features/seats/seats-view";
import { getEvent, isSlug } from "@/lib/catalog/api";
import { t } from "@/lib/i18n.server";

export const metadata: Metadata = { title: t("seats.title"), robots: { index: false } };

/** Seat selection (spec 07). Sign-in is enforced by proxy.ts; the admission token and the api are the real guards. */
export default function SeatsPage({ params }: PageProps<"/events/[slug]/seats">) {
  return (
    <Suspense fallback={<SeatsSkeleton />}>
      <SeatsContent params={params} />
    </Suspense>
  );
}

async function SeatsContent({ params }: { params: PageProps<"/events/[slug]/seats">["params"] }) {
  const { slug } = await params;
  const event = isSlug(slug) ? await getEvent(slug) : null;
  if (!event) {
    notFound();
  }

  return <SeatsView event={{ id: event.id, slug: event.slug, title: event.title, tiers: event.tiers, phases: event.phases }} />;
}

function SeatsSkeleton() {
  return (
    <div className="mx-auto grid w-full max-w-6xl gap-6 px-4 py-6 md:grid-cols-[1fr_22rem]">
      <Skeleton className="aspect-[10/7] w-full rounded-xl" />
      <Skeleton className="h-72 w-full rounded-xl" />
    </div>
  );
}
