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

// The latin faces used above the fold (body text 400, headings and buttons 600), preloaded as next/font used to do.
// `new URL(..., import.meta.url)` resolves to the content-hashed asset the bundler emits for the same files fonts.css uses.
// A plain <link> in <head>: react-dom's preload() only reaches the RSC payload here, not the prerendered HTML.
const preloadedFonts = [
  new URL("./fonts/BeVietnamPro-400-latin.woff2", import.meta.url),
  new URL("./fonts/BeVietnamPro-600-latin.woff2", import.meta.url),
];

export default function RootLayout({ children }: LayoutProps<"/">) {
  return (
    // next-themes sets the theme class before hydration, hence suppressHydrationWarning.
    <html lang="vi" className="h-full antialiased" suppressHydrationWarning>
      <head>
        {preloadedFonts.map((href) => (
          <link key={href.pathname} rel="preload" href={href.pathname} as="font" type="font/woff2" crossOrigin="anonymous" />
        ))}
      </head>
      <body className="flex min-h-full flex-col">
        <Providers>{children}</Providers>
      </body>
    </html>
  );
}
