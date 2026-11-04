import type { Metadata } from "next";
import { notFound } from "next/navigation";
import { isStep } from "@/features/organizer/wizard/steps";
import { WizardView } from "@/features/organizer/wizard/wizard-view";

export const metadata: Metadata = { title: "Sửa sự kiện" };

export default async function EventWizardPage({ params }: PageProps<"/organizer/events/[id]/[step]">) {
  const { id, step } = await params;
  if (!isStep(step)) {
    notFound();
  }

  return <WizardView eventId={id} step={step} />;
}
