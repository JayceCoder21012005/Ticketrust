import { Skeleton } from "@/components/ui/skeleton";

export function AuthHeading({ title, description }: { title: string; description?: string }) {
  return (
    <div className="mb-6 flex flex-col gap-1.5">
      <h1 className="text-2xl font-bold tracking-tight">{title}</h1>
      {description ? <p className="text-sm text-muted-foreground">{description}</p> : null}
    </div>
  );
}

/** Same footprint as a two-field form, so the card doesn't jump when the form streams in. */
export function AuthFormSkeleton() {
  return (
    <div aria-busy className="flex flex-col gap-6">
      <Skeleton className="h-16 w-full" />
      <Skeleton className="h-16 w-full" />
      <Skeleton className="h-10 w-full" />
    </div>
  );
}
