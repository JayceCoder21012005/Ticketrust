"use client";

import { useState, type FormEvent } from "react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";

/** For a code that cannot be scanned (a cracked camera, a code sent as text): type or paste it and check it like a scan. */
export function ManualEntry({ onSubmit, disabled }: { onSubmit: (payload: string) => void; disabled: boolean }) {
  const [value, setValue] = useState("");

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const payload = value.trim();
    if (!payload) return;
    onSubmit(payload);
    setValue("");
  };

  return (
    <form onSubmit={submit} className="flex gap-2" aria-label="Nhập mã vé">
      <Input
        value={value}
        onChange={(e) => setValue(e.target.value)}
        aria-label="Mã vé"
        placeholder="TR1.…"
        autoCapitalize="off"
        autoCorrect="off"
        spellCheck={false}
        className="h-12 bg-white font-mono text-base text-black"
      />
      <Button type="submit" size="lg" disabled={disabled || value.trim() === ""} className="h-12">
        Kiểm tra
      </Button>
    </form>
  );
}
