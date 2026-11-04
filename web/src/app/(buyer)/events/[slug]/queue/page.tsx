import type { Metadata } from "next";
import { notFound } from "next/navigation";
import { Suspense } from "react";
import { Skeleton } from "@/components/ui/skeleton";
import { QueueView } from "@/features/queue/queue-view";
import { getEvent, isSlug } from "@/lib/catalog/api";
import { t } from "@/lib/i18n.server";

export const metadata: Metadata = { title: t("queue.title"), robots: { index: false } };

/** The waiting room of one event (spec 06). Sign-in is enforced by proxy.ts; the api is the real guard. */
export default function QueuePage({ params }: PageProps<"/events/[slug]/queue">) {
  return (
    <Suspense fallback={<QueueSkeleton />}>
      <QueueContent params={params} />
    </Suspense>
  );
}

async function QueueContent({ params }: { params: PageProps<"/events/[slug]/queue">["params"] }) {
  const { slug } = await params;
  const event = isSlug(slug) ? await getEvent(slug) : null;
  if (!event) {
    notFound();
  }

  return <QueueView event={{ id: event.id, slug: event.slug, title: event.title, category: event.category, coverImageUrl: event.coverImageUrl }} />;
}

function QueueSkeleton() {
  return (
    <div className="flex min-h-[calc(100svh-8rem)] flex-1 items-center justify-center px-4 py-10">
      <Skeleton className="h-80 w-full max-w-md rounded-2xl" />
    </div>
  );
}
