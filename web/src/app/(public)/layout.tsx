import { CardSpotlight } from "@/components/shared/card-spotlight";
import { SiteFooter } from "@/components/shared/site-footer";
import { SiteHeader } from "@/components/shared/site-header";
import { currentYear } from "@/lib/current-year";

export default async function PublicLayout({ children }: LayoutProps<"/">) {
  return (
    <>
      <SiteHeader />
      <main id="main" className="flex flex-1 flex-col">
        {children}
      </main>
      <SiteFooter year={await currentYear()} />
      <CardSpotlight />
    </>
  );
}
