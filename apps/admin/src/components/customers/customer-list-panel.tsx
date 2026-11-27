"use client";

import { useEffect, useMemo, useState } from "react";
import Link from "next/link";
import { useQuery } from "@tanstack/react-query";
import type { ColumnDef, PaginationState } from "@tanstack/react-table";
import { Plus, RotateCcw } from "lucide-react";
import {
  CustomersApi,
  customerTagsKey,
  customersListKey,
  type CustomerSummary
} from "../../lib/customers-api";
import {
  contactStatusLabels,
  customerGroupLabels,
  customerGroups,
  customerSortLabels,
  customerSortOptions,
  formatInt,
  formatPhoneDisplay,
  formatVnDate,
  formatVnd,
  nextVnDate,
  parseMinLtvVnd,
  validateDeliveredDateRange,
  type CustomerContactStatus,
  type CustomerGroup,
  type CustomerPermissions,
  type CustomerSort
} from "../../lib/customers-forms";
import { DataTable, paginationParams } from "../data-table";
import { useAdmin } from "../providers";
import { FormError } from "../ui";
import { ContactStatusBadge, CustomerGroupBadge, TagChip } from "./customer-badges";

export function CustomerListPanel({ perms }: { perms: CustomerPermissions }) {
  const { api: admin } = useAdmin();
  const api = useMemo(() => new CustomersApi(admin), [admin]);

  const [search, setSearch] = useState("");
  const [q, setQ] = useState("");
  const [group, setGroup] = useState<CustomerGroup | "">("");
  const [contactStatus, setContactStatus] = useState<CustomerContactStatus | "">("");
  const [tagId, setTagId] = useState("");
  const [minLtvInput, setMinLtvInput] = useState("");
  const [lastDeliveredAfter, setLastDeliveredAfter] = useState("");
  const [lastDeliveredBefore, setLastDeliveredBefore] = useState("");
  const [sort, setSort] = useState<CustomerSort>("createdAt:desc");
  const [pagination, setPagination] = useState<PaginationState>({ pageIndex: 0, pageSize: 20 });

  const searchPending = search.trim() !== q;
  useEffect(() => {
    if (!searchPending) return;
    const timer = window.setTimeout(() => {
      setQ(search.trim());
      setPagination(prev => ({ ...prev, pageIndex: 0 }));
    }, 250);
    return () => window.clearTimeout(timer);
  }, [search, searchPending]);

  // GAP-P202-02 = B (`IMPLEMENTED_BUT_NOT_CONFIRMED` per ADR-P2-01):
  // `GET /api/v1/admin/tags` requires `customers.write`. Never fire it for `customers.read`-only users.
  const tagsQuery = useQuery({
    queryKey: customerTagsKey,
    queryFn: ({ signal }) => api.listTags(signal),
    enabled: perms.canManageTags,
    refetchOnWindowFocus: false
  });

  const minLtvCheck = useMemo(() => {
    try {
      return { value: parseMinLtvVnd(minLtvInput), error: null as string | null };
    } catch (error) {
      return { value: undefined, error: error instanceof Error ? error.message : "Chi tiêu tối thiểu không hợp lệ." };
    }
  }, [minLtvInput]);

  const dateRangeError = useMemo(
    () => validateDeliveredDateRange(lastDeliveredAfter, lastDeliveredBefore),
    [lastDeliveredAfter, lastDeliveredBefore]
  );

  const hasFilterError = Boolean(minLtvCheck.error || dateRangeError);
  const effectiveTag = perms.canManageTags && tagId ? tagId : undefined;
  // Backend filters half-open `[lastDeliveredAfter, lastDeliveredBefore)` at VN 00:00:00+07:00
  // (`CustomerContracts.cs:389-393`, `EfCustomerQueries.cs:60-68`); convert inclusive UI end date `D` -> `D+1`.
  const effectiveLastDeliveredBefore =
    !dateRangeError && lastDeliveredBefore.trim()
      ? nextVnDate(lastDeliveredBefore)
      : undefined;

  const queryParams = {
    ...paginationParams(pagination),
    q: q || undefined,
    group: group || undefined,
    contactStatus: contactStatus || undefined,
    tag: effectiveTag,
    minLtv: minLtvCheck.value,
    lastDeliveredAfter: lastDeliveredAfter.trim() || undefined,
    lastDeliveredBefore: effectiveLastDeliveredBefore,
    sort
  };

  const customersQuery = useQuery({
    queryKey: [...customersListKey, queryParams],
    queryFn: ({ signal }) => api.listCustomers(queryParams, signal),
    enabled: !hasFilterError
  });

  const hasActiveFilters = Boolean(
    search.trim() ||
      group ||
      contactStatus ||
      tagId ||
      minLtvInput.trim() ||
      lastDeliveredAfter ||
      lastDeliveredBefore ||
      sort !== "createdAt:desc"
  );

  function resetFilters() {
    setSearch("");
    setQ("");
    setGroup("");
    setContactStatus("");
    setTagId("");
    setMinLtvInput("");
    setLastDeliveredAfter("");
    setLastDeliveredBefore("");
    setSort("createdAt:desc");
    setPagination(prev => ({ ...prev, pageIndex: 0 }));
  }

  const columns = useMemo<ColumnDef<CustomerSummary>[]>(() => [
    {
      id: "customer",
      header: "Khách hàng",
      cell: ({ row: { original: item } }) => (
        <div className="customer-identity-cell">
          <div className="customer-identity-head">
            <Link className="customer-name-link" href={`/customers/${item.id}`}>
              {item.fullName}
            </Link>
            <code className="customer-code">{item.code}</code>
          </div>
          <div className="inventory-badges">
            <CustomerGroupBadge group={item.customerGroup} isVip={item.isVip} />
            <ContactStatusBadge status={item.contactStatus} />
          </div>
          {item.tags.length > 0 && (
            <div className="customer-tag-list">
              {item.tags.map(tag => (
                <TagChip key={tag.id} tag={tag} />
              ))}
            </div>
          )}
        </div>
      )
    },
    {
      id: "contact",
      header: "Liên hệ",
      cell: ({ row: { original: item } }) => (
        <div className="customer-contact-cell">
          <strong>{formatPhoneDisplay(item.phoneE164)}</strong>
          {item.zaloName && <span>Zalo: {item.zaloName}</span>}
          {item.email && <span className="customer-muted-small">{item.email}</span>}
        </div>
      )
    },
    {
      id: "metrics",
      header: "Chi tiêu & Đơn",
      cell: ({ row: { original: item } }) => (
        <div className="customer-metrics-cell">
          <strong>{formatVnd(item.lifetimeValueVnd)}</strong>
          <span>{formatInt(item.deliveredOrdersCount)} đơn đã giao</span>
        </div>
      )
    },
    {
      id: "activity",
      header: "Hoạt động gần nhất",
      cell: ({ row: { original: item } }) => (
        <div className="customer-activity-cell">
          <span>Mua gần nhất: {formatVnDate(item.lastDeliveredAt)}</span>
          <span>Liên hệ: {formatVnDate(item.lastContactedAt)}</span>
        </div>
      )
    },
    {
      id: "actions",
      header: "Thao tác",
      cell: ({ row: { original: item } }) => (
        <Link
          className="button secondary"
          href={`/customers/${item.id}`}
          aria-label={`Hồ sơ 360° — ${item.fullName}`}
        >
          Hồ sơ 360°
        </Link>
      )
    }
  ], []);

  return (
    <section className="inventory-panel" aria-labelledby="customers-list-heading">
      <div className="inventory-section-header">
        <div>
          <h2 id="customers-list-heading">Danh sách khách hàng</h2>
          <p className="hint">
            Tìm nhanh theo tên không dấu, tên Zalo, mã KH hoặc 3–10 số cuối điện thoại.
          </p>
        </div>
        {perms.canCreate && (
          <Link className="button" href="/customers/new">
            <Plus size={18} aria-hidden="true" />
            <span>Tạo khách mới</span>
          </Link>
        )}
      </div>

      <div className="customer-filters-card">
        <div className="inventory-filters">
          <label className="product-field customer-filter-wide">
            Tìm kiếm khách hàng
            <input
              type="search"
              maxLength={200}
              value={search}
              onChange={event => setSearch(event.target.value)}
              placeholder="Tên không dấu, Zalo, mã KH hoặc đuôi SĐT…"
            />
          </label>

          <label className="product-field">
            Nhóm khách
            <select
              aria-label="Nhóm khách"
              value={group}
              onChange={event => {
                setGroup(event.target.value as CustomerGroup | "");
                setPagination(prev => ({ ...prev, pageIndex: 0 }));
              }}
            >
              <option value="">Tất cả nhóm</option>
              {customerGroups.map(g => (
                <option key={g} value={g}>{customerGroupLabels[g]}</option>
              ))}
            </select>
          </label>

          <label className="product-field">
            Trạng thái liên hệ
            <select
              aria-label="Trạng thái liên hệ"
              value={contactStatus}
              onChange={event => {
                setContactStatus(event.target.value as CustomerContactStatus | "");
                setPagination(prev => ({ ...prev, pageIndex: 0 }));
              }}
            >
              <option value="">Tất cả trạng thái</option>
              <option value="active">{contactStatusLabels.active}</option>
              <option value="do_not_contact">{contactStatusLabels.do_not_contact}</option>
              <option value="erased">{contactStatusLabels.erased}</option>
            </select>
          </label>

          {perms.canManageTags && (
            <label className="product-field">
              Nhãn khách hàng
              <select
                aria-label="Nhãn khách hàng"
                value={tagId}
                onChange={event => {
                  setTagId(event.target.value);
                  setPagination(prev => ({ ...prev, pageIndex: 0 }));
                }}
              >
                <option value="">Tất cả nhãn</option>
                {(tagsQuery.data ?? []).map(tag => (
                  <option key={tag.id} value={tag.id}>{tag.name}</option>
                ))}
              </select>
            </label>
          )}

          <label className="product-field">
            Chi tiêu tối thiểu (VND)
            <input
              type="text"
              inputMode="numeric"
              value={minLtvInput}
              aria-invalid={Boolean(minLtvCheck.error)}
              onChange={event => {
                setMinLtvInput(event.target.value);
                setPagination(prev => ({ ...prev, pageIndex: 0 }));
              }}
              placeholder="VD: 1000000"
            />
          </label>

          <label className="product-field">
            Mua giao thành công từ ngày
            <input
              type="date"
              value={lastDeliveredAfter}
              aria-invalid={Boolean(dateRangeError)}
              onChange={event => {
                setLastDeliveredAfter(event.target.value);
                setPagination(prev => ({ ...prev, pageIndex: 0 }));
              }}
            />
          </label>

          <label className="product-field">
            Mua giao thành công đến ngày
            <input
              type="date"
              value={lastDeliveredBefore}
              aria-invalid={Boolean(dateRangeError)}
              onChange={event => {
                setLastDeliveredBefore(event.target.value);
                setPagination(prev => ({ ...prev, pageIndex: 0 }));
              }}
            />
          </label>

          <label className="product-field">
            Sắp xếp
            <select
              aria-label="Sắp xếp"
              value={sort}
              onChange={event => {
                setSort(event.target.value as CustomerSort);
                setPagination(prev => ({ ...prev, pageIndex: 0 }));
              }}
            >
              {customerSortOptions.map(opt => (
                <option key={opt} value={opt}>{customerSortLabels[opt]}</option>
              ))}
            </select>
          </label>
        </div>

        {(minLtvCheck.error || dateRangeError) && (
          <div className="alert" role="alert">
            <div>
              {minLtvCheck.error && <p>{minLtvCheck.error}</p>}
              {dateRangeError && <p>{dateRangeError}</p>}
            </div>
          </div>
        )}

        {perms.canManageTags && tagsQuery.isError && (
          <div>
            <FormError error={tagsQuery.error} />
            <button type="button" className="button secondary" onClick={() => void tagsQuery.refetch()}>
              Tải lại danh sách nhãn
            </button>
          </div>
        )}

        {hasActiveFilters && (
          <div className="catalog-actions">
            <button type="button" className="button secondary" onClick={resetFilters}>
              <RotateCcw size={16} aria-hidden="true" />
              <span>Xóa bộ lọc</span>
            </button>
          </div>
        )}
      </div>

      <DataTable<CustomerSummary>
        caption="Danh sách khách hàng Trà Hiên"
        data={customersQuery.data?.items ?? []}
        columns={columns}
        totalItems={Number(customersQuery.data?.totalItems ?? 0)}
        pagination={pagination}
        onPaginationChange={setPagination}
        loading={!hasFilterError && (searchPending || customersQuery.isPending)}
        error={customersQuery.error}
        onRetry={() => void customersQuery.refetch()}
        getRowId={row => row.id}
      />
    </section>
  );
}
