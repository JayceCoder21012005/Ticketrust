import type { Metadata, Viewport } from "next";
import { Providers } from "@/components/providers";
import "./fonts.css";
import "./globals.css";

export const metadata: Metadata = {
  title: { default: "TicketRush", template: "%s · TicketRush" },
  description: "Săn vé nhanh, công bằng, không bao giờ bán trùng ghế.",
};

export const viewport: Viewport = {
  themeColor: [
    { media: "(prefers-color-scheme: light)", color: "#ffffff" },
    { media: "(prefers-color-scheme: dark)", color: "#09090b" },
  ],
};

export default function RootLayout({ children }: LayoutProps<"/">) {
  return (
    // next-themes sets the theme class before hydration, hence suppressHydrationWarning.
    <html lang="vi" className="h-full antialiased" suppressHydrationWarning>
      <body className="flex min-h-full flex-col">
        <Providers>{children}</Providers>
      </body>
    </html>
  );
}
