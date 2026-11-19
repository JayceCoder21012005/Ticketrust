import { AlertTriangle } from "lucide-react";
import { EmptyState } from "@/components/shared/empty-state";
import { getHomeSections } from "@/lib/catalog/api";
import { currentHour } from "@/lib/current-year";
import { t } from "@/lib/i18n.server";
import { HomeContent, HomeHero } from "./home-content";

/**
 * Loads the home's sections (one cached api call, `GET /catalog/home`) and renders them. No request-time APIs: the home page is
 * an ISR page (static HTML, refreshed every 30 s).
 */
export async function HomeSectionsLoader() {
  const home = await getHomeSections();
  if (home === null) {
    return <EmptyState icon={AlertTriangle} title={t("states.errorTitle")} description={t("states.errorDescription")} />;
  }

  return <HomeContent home={home} />;
}

/** The hero, in the page's static shell (no data but the cached hour that picks the day's photo). */
export async function HomeHeroLoader() {
  return <HomeHero now={new Date(await currentHour())} />;
}
