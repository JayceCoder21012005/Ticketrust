import { passwordStrength } from "@/lib/auth/password-strength";
import { t } from "@/lib/i18n";
import { cn } from "@/lib/utils";

const BAR_COLORS = ["", "bg-danger", "bg-warning", "bg-success", "bg-success"] as const;

export function PasswordStrengthMeter({ password }: { password: string }) {
  const { score, label } = passwordStrength(password);

  return (
    <div className="flex flex-col gap-1.5">
      <div aria-hidden className="grid grid-cols-4 gap-1">
        {[1, 2, 3, 4].map((bar) => (
          <span key={bar} className={cn("h-1 rounded-full bg-muted", bar <= score && BAR_COLORS[score])} />
        ))}
      </div>
      <p aria-live="polite" className="text-xs text-muted-foreground">
        {score > 0 ? t("auth.strength", "vi", { label }) : t("auth.passwordHint")}
      </p>
    </div>
  );
}
