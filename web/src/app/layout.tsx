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

// All four latin faces, as next/font preloaded them: 400 body text, 500 buttons (font-medium), 600 semibold headings and
// labels, 700 home headings (font-bold). The vietnamese subsets load on demand through their unicode-range.
// `new URL(..., import.meta.url)` resolves to the content-hashed asset the bundler emits for the same files fonts.css uses.
// A plain <link> in <head>: react-dom's preload() only reaches the RSC payload here, not the prerendered HTML.
// Literal paths on purpose: the bundler only resolves a statically analysable `new URL`.
const preloadedFonts = [
  new URL("./fonts/BeVietnamPro-400-latin.woff2", import.meta.url),
  new URL("./fonts/BeVietnamPro-500-latin.woff2", import.meta.url),
  new URL("./fonts/BeVietnamPro-600-latin.woff2", import.meta.url),
  new URL("./fonts/BeVietnamPro-700-latin.woff2", import.meta.url),
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
