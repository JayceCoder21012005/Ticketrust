import { contrastRatio } from "@/lib/contrast";
import type { TokenSet } from "@/lib/design-tokens";
import { cn } from "@/lib/utils";

type TokenSwatchesProps = {
  names: readonly string[];
  /** Declared values of the theme the swatches are shown in (light or dark). */
  values: TokenSet;
  /** Minimum contrast against `--background`: 3 for UI graphics (seats); omit to skip the check. */
  minContrast?: number;
};

/** Server-rendered: ratios come from the authored oklch values, the same ones the token test checks. */
export function TokenSwatches({ names, values, minContrast }: TokenSwatchesProps) {
  return (
    <ul className="grid grid-cols-2 gap-3 sm:grid-cols-3">
      {names.map((name) => {
        const ratio = minContrast === undefined ? undefined : contrastRatio(values[name], values.background);
        const passes = ratio === undefined || ratio >= (minContrast ?? 0);
        return (
          <li key={name} className="flex items-center gap-3">
            <span aria-hidden className="size-10 shrink-0 rounded-md border" style={{ background: `var(--${name})` }} />
            <span className="flex flex-col text-xs">
              <code className="font-mono">--{name}</code>
              {ratio !== undefined ? (
                <span className={cn(passes ? "text-muted-foreground" : "font-semibold text-danger")}>
                  {ratio.toFixed(2)}:1 {passes ? "✓" : "✗"}
                </span>
              ) : null}
            </span>
          </li>
        );
      })}
    </ul>
  );
}
