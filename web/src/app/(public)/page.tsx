import { ApiStatus } from "@/components/api-status";

/** Placeholder home; the real catalog home arrives with spec 04. */
export default function Home() {
  return (
    <div className="mx-auto flex w-full max-w-content flex-1 flex-col justify-center gap-4 px-4 py-24">
      <h1 className="text-3xl font-bold tracking-tight sm:text-4xl">TicketRush</h1>
      <p className="max-w-prose text-muted-foreground">Săn vé nhanh, công bằng, không bao giờ bán trùng ghế.</p>
      <ApiStatus />
    </div>
  );
}
