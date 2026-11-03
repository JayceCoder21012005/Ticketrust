import type { Metadata, Viewport } from "next";
import { Be_Vietnam_Pro } from "next/font/google";
import { Providers } from "@/components/providers";
import "./globals.css";

// Full Vietnamese diacritics in every weight we use.
const beVietnamPro = Be_Vietnam_Pro({
  subsets: ["latin", "vietnamese"],
  weight: ["400", "500", "600", "700"],
  display: "swap",
  variable: "--font-be-vietnam-pro",
});

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
    <html lang="vi" className={`${beVietnamPro.variable} h-full antialiased`} suppressHydrationWarning>
      <body className="flex min-h-full flex-col">
        <Providers>{children}</Providers>
      </body>
    </html>
  );
}
