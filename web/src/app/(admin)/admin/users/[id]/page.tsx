import { Suspense } from "react";
import { Skeleton } from "@/components/ui/skeleton";
import { UserDetailPage } from "@/features/admin/admin-pages";

type Params = { params: Promise<{ id: string }> };

export default function Page({ params }: Params) {
  return (
    <Suspense fallback={<Skeleton className="m-6 h-64" />}>
      <UserContent params={params} />
    </Suspense>
  );
}

async function UserContent({ params }: Params) {
  const { id } = await params;
  return <UserDetailPage userId={id} />;
}
