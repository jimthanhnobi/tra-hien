"use client";

import { useEffect } from "react";

// Links and document navigation keep the current form available until the user decides.
export function useProductNavigationGuard(dirty: boolean, pending: boolean) {
  useEffect(() => {
    if (!dirty && !pending) return;
    const confirmLeave = () => !pending && window.confirm("Rời trang và bỏ thay đổi chưa lưu?");
    const leave = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = ""; };
    const click = (event: MouseEvent) => {
      const link = event.target instanceof Element ? event.target.closest<HTMLAnchorElement>("a[href]") : null;
      if (!link || event.button !== 0 || event.metaKey || event.ctrlKey || event.altKey || event.shiftKey || link.target === "_blank") return;
      const url = new URL(link.href);
      if (url.origin === location.origin && url.pathname === location.pathname && url.search === location.search) return;
      if (!confirmLeave()) { event.preventDefault(); event.stopPropagation(); }
    };
    const traverse = (event: Event) => {
      const navigationEvent = event as Event & { navigationType?: string; destination?: { url?: string } };
      if (event.cancelable && navigationEvent.navigationType === "traverse" && navigationEvent.destination?.url !== location.href && !confirmLeave()) event.preventDefault();
    };
    const navigation = (window as Window & { navigation?: EventTarget }).navigation;
    window.addEventListener("beforeunload", leave);
    document.addEventListener("click", click, true);
    navigation?.addEventListener("navigate", traverse);
    return () => {
      window.removeEventListener("beforeunload", leave);
      document.removeEventListener("click", click, true);
      navigation?.removeEventListener("navigate", traverse);
    };
  }, [dirty, pending]);
}
