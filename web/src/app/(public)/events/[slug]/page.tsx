import type { Metadata } from "next";
import { notFound } from "next/navigation";
import { Suspense } from "react";
import { Skeleton } from "@/components/ui/skeleton";
import { PageTransition } from "@/components/shared/event-card";
import { EventDetailView } from "@/features/catalog/detail/event-detail-view";
import { getEvent, getPrerenderSlugs, isSlug } from "@/lib/catalog/api";
import { formatDateTime } from "@/lib/format";

/** Placeholder when the api is unreachable at build: not a valid slug, so it renders the 404 without a request. */
const BUILD_PLACEHOLDER = "__no-api-at-build__";

/**
 * ISR (Cache Components): the soonest events are prerendered at build; any other slug gets the app shell on its
 * first visit and a complete static page from the ISR cache afterwards, so the title and poster are in the
 * initial HTML instead of a streamed Suspense boundary (React delays those reveals by up to 300 ms, hurting LCP).
 */
export async function generateStaticParams() {
  const slugs = await getPrerenderSlugs(20);
  return (slugs.length > 0 ? slugs : [BUILD_PLACEHOLDER]).map((slug) => ({ slug }));
}

async function load(slug: string) {
  return isSlug(slug) ? getEvent(slug) : null;
}

export async function generateMetadata({ params }: PageProps<"/events/[slug]">): Promise<Metadata> {
  const { slug } = await params;
  const event = await load(slug).catch(() => null);
  if (!event) {
    return {};
  }

  const description = `${formatDateTime(event.startsAt)} · ${event.venue.name}, ${event.venue.city}`;
  return {
    title: event.title,
    description,
    openGraph: { title: event.title, description, type: "website" },
  };
}

/** Event detail (spec 04). Draft, cancelled and unknown events are a 404, as in the api. */
export default function EventPage({ params }: PageProps<"/events/[slug]">) {
  return (
    <Suspense fallback={<DetailSkeleton />}>
      <EventContent params={params} />
    </Suspense>
  );
}

async function EventContent({ params }: { params: PageProps<"/events/[slug]">["params"] }) {
  const { slug } = await params;
  const event = await load(slug);
  if (!event) {
    notFound();
  }

  return (
    <PageTransition>
      <EventDetailView event={event} />
    </PageTransition>
  );
}

function DetailSkeleton() {
  return (
    <div className="mx-auto grid w-full max-w-content gap-8 px-4 pt-6 md:grid-cols-[minmax(0,5fr)_minmax(0,6fr)] md:pt-10 lg:gap-12">
      <Skeleton className="aspect-square rounded-2xl" />
      <div className="flex flex-col gap-4">
        <Skeleton className="h-4 w-24" />
        <Skeleton className="h-10 w-4/5" />
        <Skeleton className="h-12 w-3/5" />
        <Skeleton className="h-12 w-3/5" />
        <Skeleton className="h-40 w-full rounded-xl" />
      </div>
    </div>
  );
}
