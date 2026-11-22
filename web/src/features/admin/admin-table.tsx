"use client";

import { Columns3, MoreHorizontal } from "lucide-react";
import { usePathname, useRouter, useSearchParams } from "next/navigation";
import { useCallback, useEffect, useState, type ReactNode } from "react";
import { ErrorState } from "@/components/shared/error-state";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu,
  DropdownMenuCheckboxItem,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Skeleton } from "@/components/ui/skeleton";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { toFriendlyMessage } from "@/lib/errors";

export type Page<T> = { items: T[]; nextCursor: string | null };

export type Column<T> = { id: string; label: string; cell: (row: T) => ReactNode; hideable?: boolean; className?: string };

export type RowAction<T> = { label: string; onSelect: (row: T) => void; destructive?: boolean; hidden?: (row: T) => boolean };

/** The URL's query (the filters) as a plain object; the list reloads when it changes, and it survives a reload or a shared link. */
export function useUrlFilters(): [Record<string, string>, (patch: Record<string, string | null>) => void] {
  const params = useSearchParams();
  const router = useRouter();
  const pathname = usePathname();
  const filters = Object.fromEntries(params.entries());
  const set = useCallback(
    (patch: Record<string, string | null>) => {
      const next = new URLSearchParams(params.toString());
      for (const [key, value] of Object.entries(patch)) {
        if (value) next.set(key, value);
        else next.delete(key);
      }
      router.replace(next.size > 0 ? `${pathname}?${next}` : pathname, { scroll: false });
    },
    [params, router, pathname],
  );
  return [filters, set];
}

type AdminTableProps<T> = {
  caption: string;
  columns: Column<T>[];
  rowKey: (row: T) => string;
  /** Loads one page of the server-side filtered list (keyset cursor). */
  load: (cursor: string | null) => Promise<Page<T>>;
  /** Changes whenever the filters do: the list starts over. */
  reloadKey: string;
  actions?: RowAction<T>[];
  empty?: string;
};

/**
 * The admin lists (spec 16): filtering happens on the server (newest first, keyset pages with "Tải thêm"), columns can be hidden,
 * and each row has an actions menu. `reloadKey` restarts the list after a filter change or an action.
 */
export function AdminTable<T>({ caption, columns, rowKey, load, reloadKey, actions = [], empty = "Không có dữ liệu." }: AdminTableProps<T>) {
  const [rows, setRows] = useState<T[] | null>(null);
  const [next, setNext] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loadingMore, setLoadingMore] = useState(false);
  const [hidden, setHidden] = useState<Set<string>>(new Set());

  useEffect(() => {
    let live = true;
    load(null).then(
      (page) => {
        if (!live) return;
        setRows(page.items);
        setNext(page.nextCursor);
        setError(null);
      },
      (failure: unknown) => live && setError(toFriendlyMessage(failure)),
    );
    return () => {
      live = false;
    };
    // `load` is rebuilt every render; `reloadKey` says when its inputs changed.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [reloadKey]);

  const more = async () => {
    if (!next) return;
    setLoadingMore(true);
    try {
      const page = await load(next);
      setRows((current) => [...(current ?? []), ...page.items]);
      setNext(page.nextCursor);
    } catch (failure) {
      setError(toFriendlyMessage(failure));
    } finally {
      setLoadingMore(false);
    }
  };

  if (error && !rows) return <ErrorState title="Không tải được danh sách" description={error} onRetry={() => window.location.reload()} />;
  if (!rows) return <Skeleton className="h-64 w-full" />;

  const visible = columns.filter((c) => !hidden.has(c.id));
  return (
    <div className="flex flex-col gap-3">
      <div className="flex justify-end">
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button type="button" variant="outline" size="sm">
              <Columns3 aria-hidden />
              Cột
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end">
            <DropdownMenuLabel>Hiện cột</DropdownMenuLabel>
            {columns
              .filter((c) => c.hideable !== false)
              .map((column) => (
                <DropdownMenuCheckboxItem
                  key={column.id}
                  checked={!hidden.has(column.id)}
                  onCheckedChange={(on) =>
                    setHidden((current) => {
                      const copy = new Set(current);
                      if (on) copy.delete(column.id);
                      else copy.add(column.id);
                      return copy;
                    })
                  }
                >
                  {column.label}
                </DropdownMenuCheckboxItem>
              ))}
          </DropdownMenuContent>
        </DropdownMenu>
      </div>
      <div className="overflow-x-auto rounded-xl border">
        <Table>
          <caption className="sr-only">{caption}</caption>
          <TableHeader>
            <TableRow>
              {visible.map((column) => (
                <TableHead key={column.id} className={column.className}>
                  {column.label}
                </TableHead>
              ))}
              {actions.length > 0 ? <TableHead className="w-12"><span className="sr-only">Thao tác</span></TableHead> : null}
            </TableRow>
          </TableHeader>
          <TableBody>
            {rows.length === 0 ? (
              <TableRow>
                <TableCell colSpan={visible.length + 1} className="py-8 text-center text-muted-foreground">
                  {empty}
                </TableCell>
              </TableRow>
            ) : (
              rows.map((row) => (
                <TableRow key={rowKey(row)}>
                  {visible.map((column) => (
                    <TableCell key={column.id} className={column.className}>
                      {column.cell(row)}
                    </TableCell>
                  ))}
                  {actions.length > 0 ? (
                    <TableCell>
                      <DropdownMenu>
                        <DropdownMenuTrigger asChild>
                          <Button type="button" variant="ghost" size="icon" aria-label="Thao tác">
                            <MoreHorizontal aria-hidden />
                          </Button>
                        </DropdownMenuTrigger>
                        <DropdownMenuContent align="end">
                          {actions
                            .filter((a) => !a.hidden?.(row))
                            .map((action) => (
                              <DropdownMenuItem key={action.label} variant={action.destructive ? "destructive" : "default"} onSelect={() => action.onSelect(row)}>
                                {action.label}
                              </DropdownMenuItem>
                            ))}
                        </DropdownMenuContent>
                      </DropdownMenu>
                    </TableCell>
                  ) : null}
                </TableRow>
              ))
            )}
          </TableBody>
        </Table>
      </div>
      {error ? (
        <p role="alert" className="text-sm text-danger">
          {error}
        </p>
      ) : null}
      {next ? (
        <Button type="button" variant="outline" onClick={more} disabled={loadingMore} className="self-center">
          Tải thêm
        </Button>
      ) : null}
    </div>
  );
}
