"use client";

import { Moon, Sun } from "lucide-react";
import { useTheme } from "next-themes";
import { Button } from "@/components/ui/button";
import { t } from "@/lib/i18n";

export function ThemeToggle() {
  const { resolvedTheme, setTheme } = useTheme();

  return (
    <Button
      variant="ghost"
      size="icon"
      aria-label={t("theme.toggle")}
      onClick={() => setTheme(resolvedTheme === "dark" ? "light" : "dark")}
    >
      {/* Both icons render; CSS picks one, so there is no hydration mismatch before the theme is known. */}
      <Sun aria-hidden className="dark:hidden" />
      <Moon aria-hidden className="hidden dark:block" />
    </Button>
  );
}
