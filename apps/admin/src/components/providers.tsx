"use client";

import { createContext, useContext, useState, type ReactNode } from "react";
import { QueryClient, QueryClientProvider, useQuery } from "@tanstack/react-query";
import { AdminApi, type Auth } from "../lib/api";

export const authKey = ["auth", "me"] as const;

export function createAdminRuntime(api = new AdminApi()) {
  const queries = new QueryClient({ defaultOptions: {
    queries: { retry: false, staleTime: 0, gcTime: 0, refetchOnWindowFocus: "always" },
    mutations: { retry: false, gcTime: 0 }
  } });
  function clearOtherQueries() {
    void queries.cancelQueries({ predicate: q => q.queryKey[0] !== "auth" });
    queries.removeQueries({ predicate: q => q.queryKey[0] !== "auth" });
    queries.getMutationCache().clear();
  }
  async function clearSession() {
    api.reset();
    await queries.cancelQueries();
    // Keep the observed auth query object: removing it detaches mounted observers.
    clearOtherQueries();
    queries.setQueryData(authKey, null);
  }
  api.onUnauthorized = () => { void clearSession(); };
  return {
    api, queries, clearSession,
    async readSession(signal?: AbortSignal): Promise<Auth | null> {
      const auth = await api.me(signal);
      // Recheck identity/permissions every time, discard all module data on change.
      const old = queries.getQueryData<Auth | null>(authKey);
      if (!auth || JSON.stringify(old) !== JSON.stringify(auth)) { api.reset(); clearOtherQueries(); }
      return auth;
    },
    async authenticated() {
      await clearSession();
      await api.refreshCsrf();
      await queries.fetchQuery({ queryKey: authKey, queryFn: ({ signal }) => api.me(signal) });
    },
    async signedOut() {
      await clearSession();
      // If refreshing fails, the next unsafe operation will fetch a new token.
      await api.refreshCsrf().catch(() => {});
    }
  };
}
type Runtime = ReturnType<typeof createAdminRuntime> & { notice: string; setNotice: (value: string) => void };
const RuntimeContext = createContext<Runtime | null>(null);

export function AdminProvider({ children }: { children: ReactNode }) {
  const [runtime] = useState(createAdminRuntime);
  const [notice, setNotice] = useState("");
  return <QueryClientProvider client={runtime.queries}><RuntimeContext.Provider value={{ ...runtime, notice, setNotice }}>
    {children}
  </RuntimeContext.Provider></QueryClientProvider>;
}

export function useAdmin() {
  const runtime = useContext(RuntimeContext);
  if (!runtime) throw new Error("AdminProvider required");
  return runtime;
}

export function useSession() {
  const runtime = useAdmin();
  return useQuery({ queryKey: authKey, queryFn: ({ signal }) => runtime.readSession(signal), refetchInterval: 60_000 });
}
