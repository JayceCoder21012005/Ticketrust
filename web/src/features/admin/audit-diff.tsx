import { cn } from "@/lib/utils";

type Change = { before: unknown; after: unknown };

const show = (value: unknown) => (value === null || value === undefined ? "—" : typeof value === "string" ? value : JSON.stringify(value));

/** Field-level entries (`{ field: { before, after } }`); anything else is an explicit action's own details. */
export function parseChanges(json: string | null | undefined): { fields: [string, Change][] } | { details: Record<string, unknown> } | null {
  if (!json) return null;
  try {
    const value = JSON.parse(json) as Record<string, unknown>;
    const entries = Object.entries(value);
    const fieldLevel = entries.length > 0 && entries.every(([, v]) => v !== null && typeof v === "object" && "before" in v && "after" in v);
    return fieldLevel ? { fields: entries as [string, Change][] } : { details: value };
  } catch {
    return { details: { raw: json } };
  }
}

/** Spec 16's diff viewer: before and after side by side, changed fields highlighted. */
export function AuditDiff({ changes }: { changes: string | null | undefined }) {
  const parsed = parseChanges(changes);
  if (!parsed) return <p className="text-sm text-muted-foreground">Không có chi tiết.</p>;

  if ("details" in parsed) {
    return (
      <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-sm">
        {Object.entries(parsed.details).map(([key, value]) => (
          <div key={key} className="contents">
            <dt className="font-medium text-muted-foreground">{key}</dt>
            <dd className="break-all">{show(value)}</dd>
          </div>
        ))}
      </dl>
    );
  }

  return (
    <table className="w-full table-fixed text-sm">
      <caption className="sr-only">Giá trị trước và sau thay đổi</caption>
      <thead>
        <tr className="text-left text-xs text-muted-foreground">
          <th scope="col" className="w-1/4 pb-1 font-medium">
            Trường
          </th>
          <th scope="col" className="pb-1 font-medium">
            Trước
          </th>
          <th scope="col" className="pb-1 font-medium">
            Sau
          </th>
        </tr>
      </thead>
      <tbody>
        {parsed.fields.map(([field, change]) => {
          const changed = show(change.before) !== show(change.after);
          return (
            <tr key={field} data-changed={changed} className={cn("border-t align-top", changed && "bg-amber-50 dark:bg-amber-950/40")}>
              <th scope="row" className="py-1 pr-2 text-left font-mono text-xs font-normal">
                {field}
              </th>
              <td className={cn("break-all py-1 pr-2", changed && "text-danger line-through decoration-danger/40")}>{show(change.before)}</td>
              <td className="break-all py-1 font-medium">{show(change.after)}</td>
            </tr>
          );
        })}
      </tbody>
    </table>
  );
}
