import { ApiStatus } from "@/components/api-status";

export default function Home() {
  return (
    <main className="mx-auto flex w-full max-w-3xl flex-1 flex-col justify-center gap-4 px-6 py-24">
      <h1 className="text-3xl font-semibold tracking-tight">TicketRush</h1>
      <p className="text-zinc-600">Nền tảng bán vé flash-sale. Giao diện đầy đủ đến ở spec 02.</p>
      <ApiStatus />
    </main>
  );
}
