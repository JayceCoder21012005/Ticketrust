import type { Metadata } from "next";
import results from "@/features/lab/results.json";
import { LabView } from "@/features/lab/lab-view";
import type { LabResults } from "@/features/lab/types";
import { t } from "@/lib/i18n.server";

export const metadata: Metadata = {
  title: t("lab.title"),
  description: t("lab.description"),
};

/** `/lab` (spec 17): the latest load-test results, built into the page at build time from results.json. */
export default function LabPage() {
  return <LabView results={results as LabResults} />;
}
