import { Suspense } from "react";
import { Skeleton } from "@/components/ui/skeleton";
import { UsersPage } from "@/features/admin/admin-pages";

// The filters live in the URL (useSearchParams), so the page renders inside Suspense.
export default function Page() {
  return (
    <Suspense fallback={<Skeleton className="m-6 h-64" />}>
      <UsersPage />
    </Suspense>
  );
}
