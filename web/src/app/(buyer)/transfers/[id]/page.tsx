import type { Metadata } from "next";
import { Suspense } from "react";
import { Skeleton } from "@/components/ui/skeleton";
import { TransferAcceptView } from "@/features/resale/transfer-accept";
import { t } from "@/lib/i18n.buyer";

export const metadata: Metadata = { title: t("resale.accept.title"), robots: { index: false } };

/**
 * A ticket sent by a friend (spec 14). proxy.ts sends a visitor to login (or register) first and back here; only the account with
 * the email it was sent to sees it.
 */
export default function TransferRoute({ params }: PageProps<"/transfers/[id]">) {
  return (
    <div className="mx-auto w-full max-w-xl px-4 py-8">
      <Suspense fallback={<Skeleton className="h-80 w-full rounded-2xl" />}>
        <TransferContent params={params} />
      </Suspense>
    </div>
  );
}

async function TransferContent({ params }: { params: PageProps<"/transfers/[id]">["params"] }) {
  const { id } = await params;
  return <TransferAcceptView transferId={id} />;
}
