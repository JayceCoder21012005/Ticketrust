import type { Metadata } from "next";
import { notFound } from "next/navigation";
import { Suspense } from "react";
import { MockGatewayView } from "@/features/mock-gateway/mock-gateway-view";
import { isMockGatewayEnabled } from "@/lib/feature-flags";
import { t } from "@/lib/i18n.server";

export const metadata: Metadata = { title: t("mockGateway.title"), robots: { index: false } };

/**
 * The mock payment gateway (spec 08). Development and demos only: a production build answers 404 unless
 * `ENABLE_MOCK_GATEWAY=1`, and nothing links here but the redirect the mock gateway itself hands out.
 */
export default function MockGatewayPage({ params }: PageProps<"/mock-gateway/[sessionId]">) {
  return (
    <Suspense fallback={null}>
      <MockGatewayContent params={params} />
    </Suspense>
  );
}

async function MockGatewayContent({ params }: { params: PageProps<"/mock-gateway/[sessionId]">["params"] }) {
  const { sessionId } = await params;
  if (!isMockGatewayEnabled()) {
    notFound();
  }

  return <MockGatewayView sessionId={sessionId} />;
}
