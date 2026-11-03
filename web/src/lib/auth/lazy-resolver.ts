import type { FieldValues, Resolver } from "react-hook-form";
import type * as Schemas from "./schemas";

type SchemaModule = typeof Schemas;

/**
 * react-hook-form resolver that loads zod and the schemas on the first validation instead of on page
 * load. Keeps the auth pages inside the 170 KB first-load JS budget (spec 02); the cost is one small
 * request when the user first submits, long after the page became interactive.
 */
export function lazyResolver<T extends FieldValues>(pick: (schemas: SchemaModule) => Parameters<typeof import("@hookform/resolvers/zod").zodResolver>[0]): Resolver<T> {
  let resolver: Resolver<T> | undefined;

  return async (values, context, options) => {
    if (!resolver) {
      const [{ zodResolver }, schemas] = await Promise.all([import("@hookform/resolvers/zod"), import("./schemas")]);
      resolver = zodResolver(pick(schemas)) as unknown as Resolver<T>;
    }

    return resolver(values, context, options);
  };
}
