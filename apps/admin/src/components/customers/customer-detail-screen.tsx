"use client";

import { useMemo, useRef, useState } from "react";
import Link from "next/link";
import { useQuery } from "@tanstack/react-query";
import type { ColumnDef, PaginationState } from "@tanstack/react-table";
import { AlertTriangle, ArrowLeft, ShieldAlert, ShieldCheck, UserCheck } from "lucide-react";
import { isConcurrencyConflict } from "../../lib/api";
import {
  CustomersApi,
  customerDetailKey,
  customerOrdersKey,
  customerTagsKey,
  customersRootKey,
  type CustomerDetail,
  type CustomerOrderSummary
} from "../../lib/customers-api";
import {
  acquisitionSourceLabels,
  acquisitionSources,
  buildCorrectIdentityPayload,
  buildPatchCustomerPayload,
  consentChannelLabels,
  consentPurposeLabels,
  consentSourceLabels,
  consentStatusLabels,
  contactStatusLabels,
  customerGroupLabels,
  customerGroups,
  customerOrigins,
  formatInt,
  formatVnDate,
  formatVnDateTime,
  formatVnd,
  interestSourceLabels,
  interestTypeLabels,
  isImmutableCustomer,
  isUuid,
  orderPaymentStatusLabels,
  orderStatusLabels,
  originLabels,
  paymentMethodLabels,
  priceBandLabels,
  salesChannelLabels,
  toLocalPhone,
  writableContactStatuses,
  type CorrectIdentityDraft,
  type CorrectIdentityErrors,
  type CustomerAcquisitionSource,
  type CustomerFormErrors,
  type CustomerGroup,
  type CustomerOrigin,
  type CustomerPermissions,
  type PatchCustomerDraft,
  type WritableContactStatus
} from "../../lib/customers-forms";
import { DataTable, paginationParams } from "../data-table";
import { useProductNavigationGuard } from "../products/product-navigation";
import { useAdmin } from "../providers";
import { Feedback, FormError, Pending } from "../ui";
import { CustomerAddressesPanel } from "./customer-addresses-panel";
import {
  ContactStatusBadge,
  CustomerGroupBadge,
  DuplicatePhoneLink,
  QuickContactStrip,
  TagChip
} from "./customer-badges";
import { CustomerInteractionsPanel } from "./customer-interactions-panel";

type DetailTab = "overview" | "addresses" | "interactions" | "orders" | "preferences";

export function CustomerDetailScreen({ id, perms }: { id: string; perms: CustomerPermissions }) {
  const { api: admin, queries } = useAdmin();
  const api = useMemo(() => new CustomersApi(admin), [admin]);

  const [tab, setTab] = useState<DetailTab>("overview");
  const [conflictStale, setConflictStale] = useState(false);
  const [detailNotice, setDetailNotice] = useState("");

  const detailQuery = useQuery({
    queryKey: customerDetailKey(id),
    queryFn: ({ signal }) => api.getCustomer(id, signal)
  });

  async function refreshAll(message?: string) {
    setConflictStale(false);
    if (message !== undefined) setDetailNotice(message);
    await queries.invalidateQueries({ queryKey: customersRootKey });
    await detailQuery.refetch();
  }

  if (detailQuery.isPending) {
    return <Pending>Đang tải hồ sơ khách hàng 360°…</Pending>;
  }

  if (detailQuery.isError || !detailQuery.data) {
    return (
      <section className="inventory-panel">
        <Link className="customer-back-link" href="/customers">
          <ArrowLeft size={16} aria-hidden="true" />
          <span>Quay lại danh sách khách hàng</span>
        </Link>
        <FormError error={detailQuery.error} />
        <button type="button" className="button secondary" onClick={() => void detailQuery.refetch()}>
          Thử tải lại
        </button>
      </section>
    );
  }

  const detail = detailQuery.data;
  const immutable = isImmutableCustomer(detail);
  const canEdit = perms.canWrite && !immutable;
  const tabs: Array<{ key: DetailTab; label: string }> = [
    { key: "overview", label: "Tổng quan & Hồ sơ" },
    { key: "addresses", label: `Sổ địa chỉ (${detail.addresses.length})` },
    { key: "interactions", label: "Tương tác & Hẹn chăm sóc" },
    { key: "orders", label: `Đơn hàng (${formatInt(detail.stats.deliveredOrdersCount)} giao)` },
    { key: "preferences", label: "Sở thích & Consent" }
  ];

  return (
    <section className="inventory-stack" aria-labelledby="customer-360-heading">
      <div>
        <Link className="customer-back-link" href="/customers">
          <ArrowLeft size={16} aria-hidden="true" />
          <span>Quay lại danh sách khách hàng</span>
        </Link>
      </div>

      {conflictStale && (
        <div className="inventory-conflict-bar" role="alert">
          <span>Dữ liệu khách hàng vừa được người khác cập nhật. Tải bản mới nhất trước khi tiếp tục lưu.</span>
          <button type="button" className="button secondary" onClick={() => void refreshAll("")}>
            Tải bản mới nhất
          </button>
        </div>
      )}

      {detailNotice && <Feedback>{detailNotice}</Feedback>}

      {immutable && (
        <div className="alert" role="status">
          <ShieldAlert size={19} aria-hidden="true" />
          <div>
            {detail.contactStatus === "erased" ? (
              <p>
                <strong>Hồ sơ đã được xóa dữ liệu (ẩn danh hóa)</strong>
                {detail.erasedAt ? ` lúc ${formatVnDateTime(detail.erasedAt)}` : ""}. Mọi thao tác chỉnh sửa đã bị khóa.
              </p>
            ) : (
              <p>
                <strong>Hồ sơ đã được gộp vào hồ sơ khác.</strong> Mọi thao tác chỉnh sửa đã bị khóa.{" "}
                {detail.mergedIntoCustomerId && isUuid(detail.mergedIntoCustomerId) && (
                  <Link className="product-link-button" href={`/customers/${detail.mergedIntoCustomerId}`}>
                    Mở hồ sơ đích
                  </Link>
                )}
              </p>
            )}
          </div>
        </div>
      )}

      <div className="customer-hero-card">
        <div className="customer-hero-top">
          <div>
            <div className="customer-identity-head">
              <h2 id="customer-360-heading">{detail.fullName}</h2>
              <code className="customer-code">{detail.code}</code>
            </div>
            <div className="inventory-badges">
              <CustomerGroupBadge group={detail.customerGroup} isVip={detail.isVip} />
              <ContactStatusBadge status={detail.contactStatus} />
              {detail.accountSummary.hasAccount ? (
                <span className="inventory-badge is-ok">
                  <UserCheck size={14} aria-hidden="true" />
                  <span>Tài khoản web ({detail.accountSummary.status ?? "active"})</span>
                </span>
              ) : (
                <span className="inventory-badge is-muted">Chưa có tài khoản web</span>
              )}
              {detail.stats.hadLegacyPurchase && (
                <span className="inventory-badge is-muted">Khách đã mua trước hệ thống</span>
              )}
            </div>
          </div>
        </div>

        <QuickContactStrip phoneE164={detail.phoneE164} zaloName={detail.zaloName} email={detail.email} />

        {detail.tags.length > 0 && (
          <div className="customer-tag-list" role="group" aria-label="Nhãn hiện tại của khách hàng">
            {detail.tags.map(tag => (
              <TagChip key={tag.id} tag={tag} />
            ))}
          </div>
        )}

        <div className="inventory-summary-grid">
          <div className="inventory-stat-card">
            <span>Tổng chi tiêu (LTV)</span>
            <strong>{formatVnd(detail.stats.lifetimeValueVnd)}</strong>
          </div>
          <div className="inventory-stat-card">
            <span>Đơn giao thành công</span>
            <strong>{formatInt(detail.stats.deliveredOrdersCount)}</strong>
          </div>
          <div className="inventory-stat-card">
            <span>Lần mua gần nhất</span>
            <strong>{formatVnDate(detail.stats.lastDeliveredAt)}</strong>
            <small className="hint">Đầu tiên: {formatVnDate(detail.stats.firstDeliveredAt)}</small>
          </div>
          <div className="inventory-stat-card">
            <span>Liên hệ gần nhất</span>
            <strong>{formatVnDateTime(detail.stats.lastContactedAt)}</strong>
          </div>
        </div>
      </div>

      <div className="product-tabs" role="tablist" aria-label="Các mục hồ sơ 360 độ">
        {tabs.map(item => (
          <button
            key={item.key}
            id={`customer-tab-${item.key}`}
            type="button"
            role="tab"
            aria-selected={tab === item.key}
            aria-controls={`customer-panel-${item.key}`}
            onClick={() => setTab(item.key)}
          >
            {item.label}
          </button>
        ))}
      </div>

      <div
        id={`customer-panel-${tab}`}
        role="tabpanel"
        aria-labelledby={`customer-tab-${tab}`}
        className="product-tab-panel"
      >
        {tab === "overview" && (
          <div className="inventory-stack">
            <CustomerOverviewForm
              key={`overview-${detail.rowVersion}`}
              api={api}
              detail={detail}
              canEdit={canEdit}
              onSaved={msg => refreshAll(msg)}
              onConflict={() => setConflictStale(true)}
            />
            <CustomerTagsCard
              key={`tags-${detail.rowVersion}`}
              api={api}
              detail={detail}
              canEdit={canEdit && perms.canManageTags}
              onSaved={msg => refreshAll(msg)}
              onConflict={() => setConflictStale(true)}
            />
            {perms.canCorrectPhone && !immutable && (
              <CorrectIdentityCard
                key={`identity-${detail.rowVersion}`}
                api={api}
                detail={detail}
                canCorrectOwnerIdentity={perms.canCorrectOwnerIdentity}
                onSaved={msg => refreshAll(msg)}
                onConflict={() => setConflictStale(true)}
              />
            )}
          </div>
        )}

        {tab === "addresses" && (
          <CustomerAddressesPanel
            customerId={detail.id}
            customerFullName={detail.fullName}
            customerPhoneE164={detail.phoneE164}
            rowVersion={detail.rowVersion}
            addresses={detail.addresses}
            canEdit={canEdit && perms.canManageAddresses}
            onMutated={() => refreshAll("")}
            onConflict={() => setConflictStale(true)}
          />
        )}

        {tab === "interactions" && (
          <CustomerInteractionsPanel
            customerId={detail.id}
            canEdit={canEdit}
            onMutated={() => refreshAll("")}
          />
        )}

        {tab === "orders" && <CustomerOrdersTab api={api} customerId={detail.id} />}

        {tab === "preferences" && <CustomerPreferencesTab detail={detail} />}
      </div>
    </section>
  );
}

function CustomerOverviewForm({
  api,
  detail,
  canEdit,
  onSaved,
  onConflict
}: {
  api: CustomersApi;
  detail: CustomerDetail;
  canEdit: boolean;
  onSaved: (message: string) => Promise<void>;
  onConflict: () => void;
}) {
  const initial: PatchCustomerDraft = {
    fullName: detail.fullName,
    zaloName: detail.zaloName ?? "",
    email: detail.email ?? "",
    customerGroup: detail.customerGroup,
    isVip: detail.isVip,
    contactStatus: detail.contactStatus === "do_not_contact" ? "do_not_contact" : "active",
    noteSummary: detail.noteSummary ?? ""
  };

  const [draft, setDraft] = useState<PatchCustomerDraft>(initial);
  const [fieldErrors, setFieldErrors] = useState<CustomerFormErrors>({});
  const [serverError, setServerError] = useState<unknown>();
  const [notice, setNotice] = useState("");
  const [pending, setPending] = useState(false);
  const busy = useRef(false);

  const dirty = canEdit && JSON.stringify(draft) !== JSON.stringify(initial);
  useProductNavigationGuard(dirty, pending);

  async function handleSubmit(event: React.FormEvent) {
    event.preventDefault();
    if (!canEdit || busy.current) return;
    setServerError(undefined);
    setNotice("");
    const { payload, errors } = buildPatchCustomerPayload(detail, draft);
    if (errors || !payload) {
      setFieldErrors(errors ?? {});
      return;
    }
    const changed = Object.keys(payload).some(k => k !== "rowVersion");
    if (!changed) {
      setNotice("Chưa có thay đổi nào để lưu.");
      return;
    }
    setFieldErrors({});
    busy.current = true;
    setPending(true);
    try {
      await api.patchCustomer(detail.id, payload);
      await onSaved("Đã lưu thay đổi hồ sơ khách hàng.");
    } catch (error) {
      if (isConcurrencyConflict(error)) onConflict();
      setServerError(error);
    } finally {
      busy.current = false;
      setPending(false);
    }
  }

  return (
    <div className="catalog-form inventory-form-card">
      <h3>Thông tin hồ sơ &amp; nguồn khách</h3>
      <dl className="customer-meta-grid">
        <div>
          <dt>Nguồn tạo (origin)</dt>
          <dd>{originLabels[detail.origin] ?? detail.origin}</dd>
        </div>
        <div>
          <dt>Nguồn tiếp cận</dt>
          <dd>{acquisitionSourceLabels[detail.acquisitionSource] ?? detail.acquisitionSource}</dd>
        </div>
        <div>
          <dt>Ngày tạo hồ sơ</dt>
          <dd>{formatVnDateTime(detail.createdAt)}</dd>
        </div>
        <div>
          <dt>Cập nhật gần nhất</dt>
          <dd>{formatVnDateTime(detail.updatedAt)}</dd>
        </div>
        {detail.referredByCustomerId && isUuid(detail.referredByCustomerId) && (
          <div>
            <dt>Người giới thiệu</dt>
            <dd>
              <Link className="product-link-button" href={`/customers/${detail.referredByCustomerId}`}>
                Xem hồ sơ người giới thiệu
              </Link>
            </dd>
          </div>
        )}
        {detail.accountSummary.hasAccount && (
          <div>
            <dt>Tài khoản web</dt>
            <dd>
              Trạng thái: {detail.accountSummary.status ?? "—"}
              {detail.accountSummary.lastLoginAt ? ` · Đăng nhập gần nhất: ${formatVnDateTime(detail.accountSummary.lastLoginAt)}` : ""}
            </dd>
          </div>
        )}
      </dl>

      {notice && <Feedback>{notice}</Feedback>}
      <FormError error={serverError} />

      <form
        className={`inventory-stack ${!canEdit ? "product-readonly" : ""}`}
        onSubmit={event => void handleSubmit(event)}
        noValidate
      >
        <div className="catalog-fields">
          <div className="product-field">
            <label htmlFor="edit-full-name">Họ tên khách hàng *</label>
            <input
              id="edit-full-name"
              type="text"
              maxLength={150}
              disabled={!canEdit || pending}
              value={draft.fullName}
              aria-invalid={Boolean(fieldErrors.fullName)}
              onChange={e => setDraft(prev => ({ ...prev, fullName: e.target.value }))}
            />
            {fieldErrors.fullName && <p className="field-error">{fieldErrors.fullName}</p>}
          </div>

          <div className="product-field">
            <label htmlFor="edit-zalo-name">Tên Zalo</label>
            <input
              id="edit-zalo-name"
              type="text"
              maxLength={150}
              disabled={!canEdit || pending}
              value={draft.zaloName}
              aria-invalid={Boolean(fieldErrors.zaloName)}
              onChange={e => setDraft(prev => ({ ...prev, zaloName: e.target.value }))}
            />
            {fieldErrors.zaloName && <p className="field-error">{fieldErrors.zaloName}</p>}
          </div>

          <div className="product-field">
            <label htmlFor="edit-email">Email</label>
            <input
              id="edit-email"
              type="email"
              maxLength={254}
              disabled={!canEdit || pending}
              value={draft.email}
              aria-invalid={Boolean(fieldErrors.email)}
              onChange={e => setDraft(prev => ({ ...prev, email: e.target.value }))}
            />
            {fieldErrors.email && <p className="field-error">{fieldErrors.email}</p>}
          </div>

          <div className="product-field">
            <label htmlFor="edit-group">Nhóm khách hàng</label>
            <select
              id="edit-group"
              disabled={!canEdit || pending}
              value={draft.customerGroup}
              onChange={e => setDraft(prev => ({ ...prev, customerGroup: e.target.value as CustomerGroup }))}
            >
              {customerGroups.map(g => (
                <option key={g} value={g}>{customerGroupLabels[g]}</option>
              ))}
            </select>
          </div>

          <div className="product-field">
            <label htmlFor="edit-contact-status">Trạng thái liên hệ</label>
            {detail.contactStatus === "erased" ? (
              <input id="edit-contact-status" type="text" disabled value={contactStatusLabels.erased} />
            ) : (
              <select
                id="edit-contact-status"
                disabled={!canEdit || pending}
                value={draft.contactStatus}
                onChange={e => setDraft(prev => ({ ...prev, contactStatus: e.target.value as WritableContactStatus }))}
              >
                {writableContactStatuses.map(s => (
                  <option key={s} value={s}>{contactStatusLabels[s]}</option>
                ))}
              </select>
            )}
          </div>

          <div className="product-field customer-checkbox-group">
            <span className="inventory-field-label">Đánh dấu ưu tiên</span>
            <label className="product-check">
              <input
                type="checkbox"
                disabled={!canEdit || pending}
                checked={draft.isVip}
                onChange={e => setDraft(prev => ({ ...prev, isVip: e.target.checked }))}
              />
              <span>Khách hàng VIP</span>
            </label>
          </div>
        </div>

        <div className="product-field">
          <label htmlFor="edit-note-summary">Ghi chú tóm tắt</label>
          <textarea
            id="edit-note-summary"
            rows={3}
            maxLength={2000}
            disabled={!canEdit || pending}
            value={draft.noteSummary}
            aria-invalid={Boolean(fieldErrors.noteSummary)}
            onChange={e => setDraft(prev => ({ ...prev, noteSummary: e.target.value }))}
          />
          {fieldErrors.noteSummary && <p className="field-error">{fieldErrors.noteSummary}</p>}
        </div>

        {canEdit && (
          <div className="catalog-actions inventory-form-actions">
            <button type="submit" className="button" disabled={pending}>
              {pending ? "Đang lưu…" : "Lưu thay đổi hồ sơ"}
            </button>
          </div>
        )}
      </form>
    </div>
  );
}

function CustomerTagsCard({
  api,
  detail,
  canEdit,
  onSaved,
  onConflict
}: {
  api: CustomersApi;
  detail: CustomerDetail;
  canEdit: boolean;
  onSaved: (message: string) => Promise<void>;
  onConflict: () => void;
}) {
  const [selectedIds, setSelectedIds] = useState<string[]>(() => detail.tags.map(t => t.id));
  const [serverError, setServerError] = useState<unknown>();
  const [pending, setPending] = useState(false);
  const busy = useRef(false);

  // GAP-P202-02 = B (`IMPLEMENTED_BUT_NOT_CONFIRMED` per ADR-P2-01):
  // Only query `GET /api/v1/admin/tags` when `canEdit` (`customers.write` + mutable customer).
  // `customers.read`-only users view `detail.tags` cleanly with 0 calls to `GET admin/tags`.
  const allTagsQuery = useQuery({
    queryKey: customerTagsKey,
    queryFn: ({ signal }) => api.listTags(signal),
    enabled: canEdit,
    refetchOnWindowFocus: false
  });

  function toggleTag(tagId: string) {
    setSelectedIds(prev => (prev.includes(tagId) ? prev.filter(id => id !== tagId) : [...prev, tagId]));
  }

  async function handleSaveTags() {
    if (!canEdit || busy.current) return;
    setServerError(undefined);
    busy.current = true;
    setPending(true);
    try {
      await api.putCustomerTags(detail.id, { tagIds: selectedIds, rowVersion: detail.rowVersion });
      await onSaved("Đã cập nhật nhãn khách hàng.");
    } catch (error) {
      if (isConcurrencyConflict(error)) onConflict();
      setServerError(error);
    } finally {
      busy.current = false;
      setPending(false);
    }
  }

  return (
    <div className="catalog-form inventory-form-card">
      <div className="inventory-section-header">
        <div>
          <h3>Nhãn khách hàng</h3>
          <p className="hint">
            {canEdit
              ? "Chọn hoặc bỏ chọn nhãn cho khách hàng này rồi bấm Lưu nhãn."
              : "Hiển thị nhãn đã gắn theo quyền xem hồ sơ."}
          </p>
        </div>
        {canEdit && (
          <Link className="button secondary" href="/customers/tags">
            Quản lý danh mục nhãn
          </Link>
        )}
      </div>

      <FormError error={serverError} />

      {!canEdit ? (
        detail.tags.length === 0 ? (
          <p className="hint">Khách hàng chưa được gắn nhãn nào.</p>
        ) : (
          <div className="customer-tag-list">
            {detail.tags.map(tag => (
              <TagChip key={tag.id} tag={tag} />
            ))}
          </div>
        )
      ) : allTagsQuery.isPending ? (
        <Pending>Đang tải danh mục nhãn…</Pending>
      ) : allTagsQuery.isError ? (
        <FormError error={allTagsQuery.error} />
      ) : (allTagsQuery.data?.length ?? 0) === 0 ? (
        <p className="hint">
          Chưa có nhãn nào trong danh mục.{" "}
          <Link className="product-link-button" href="/customers/tags">Tạo nhãn mới</Link>
        </p>
      ) : (
        <>
          <div className="customer-tag-picker" role="group" aria-label="Chọn nhãn gắn cho khách hàng">
            {(allTagsQuery.data ?? []).map(tag => (
              <label key={tag.id} className="customer-tag-option">
                <input
                  type="checkbox"
                  disabled={pending}
                  checked={selectedIds.includes(tag.id)}
                  onChange={() => toggleTag(tag.id)}
                />
                <TagChip tag={tag} />
              </label>
            ))}
          </div>
          <div className="catalog-actions">
            <button
              type="button"
              className="button"
              disabled={pending}
              onClick={() => void handleSaveTags()}
            >
              {pending ? "Đang lưu nhãn…" : "Lưu nhãn khách hàng"}
            </button>
          </div>
        </>
      )}
    </div>
  );
}

function CorrectIdentityCard({
  api,
  detail,
  canCorrectOwnerIdentity,
  onSaved,
  onConflict
}: {
  api: CustomersApi;
  detail: CustomerDetail;
  canCorrectOwnerIdentity: boolean;
  onSaved: (message: string) => Promise<void>;
  onConflict: () => void;
}) {
  const [draft, setDraft] = useState<CorrectIdentityDraft>({
    phone: toLocalPhone(detail.phoneE164) ?? "",
    origin: detail.origin,
    acquisitionSource: detail.acquisitionSource,
    hadLegacyPurchase: detail.stats.hadLegacyPurchase,
    reason: ""
  });
  const [fieldErrors, setFieldErrors] = useState<CorrectIdentityErrors>({});
  const [serverError, setServerError] = useState<unknown>();
  const [pending, setPending] = useState(false);
  const busy = useRef(false);

  async function handleSubmit(event: React.FormEvent) {
    event.preventDefault();
    if (busy.current) return;
    setServerError(undefined);

    const { payload, errors } = buildCorrectIdentityPayload(detail, draft, canCorrectOwnerIdentity);
    if (errors || !payload) {
      setFieldErrors(errors ?? {});
      return;
    }
    setFieldErrors({});
    busy.current = true;
    setPending(true);
    try {
      await api.correctIdentity(detail.id, payload);
      await onSaved("Đã đính chính danh tính khách hàng và ghi nhật ký kiểm tra.");
    } catch (error) {
      if (isConcurrencyConflict(error)) onConflict();
      setServerError(error);
    } finally {
      busy.current = false;
      setPending(false);
    }
  }

  return (
    <form className="catalog-form inventory-form-card" onSubmit={event => void handleSubmit(event)} noValidate>
      <div>
        <h3>Đính chính danh tính (có lý do &amp; audit)</h3>
        <p className="hint">
          Theo BR-CRM-04: sửa/xóa SĐT yêu cầu quyền <code>customers.write</code>; sửa nguồn tạo (origin), nguồn tiếp cận và lịch sử mua cũ chỉ dành cho chủ cửa hàng (Owner).
        </p>
      </div>

      {detail.accountSummary.hasAccount && (
        <div className="catalog-warning customer-warning-banner" role="note">
          <AlertTriangle size={18} aria-hidden="true" />
          <div>
            Khách hàng đã có tài khoản web — thao tác đổi hoặc xóa SĐT sẽ bị hệ thống từ chối (<code>account_exists</code>; quy trình đồng bộ tài khoản thuộc P3-05).
          </div>
        </div>
      )}

      <FormError error={serverError} />
      <DuplicatePhoneLink error={serverError} />
      {fieldErrors.target && (
        <div className="alert" role="alert">
          <div><p>{fieldErrors.target}</p></div>
        </div>
      )}

      <div className="catalog-fields">
        <div className="product-field">
          <label htmlFor="identity-phone">Số điện thoại di động VN</label>
          <input
            id="identity-phone"
            type="tel"
            maxLength={30}
            disabled={pending}
            value={draft.phone}
            aria-invalid={Boolean(fieldErrors.phone)}
            onChange={e => setDraft(prev => ({ ...prev, phone: e.target.value }))}
            placeholder="Nhập SĐT mới hoặc xóa trống (nếu có tên Zalo)"
          />
          {fieldErrors.phone && <p className="field-error">{fieldErrors.phone}</p>}
        </div>

        {canCorrectOwnerIdentity && (
          <>
            <div className="product-field">
              <label htmlFor="identity-origin">Nguồn tạo hồ sơ (Owner)</label>
              <select
                id="identity-origin"
                disabled={pending}
                value={draft.origin}
                onChange={e => setDraft(prev => ({ ...prev, origin: e.target.value as CustomerOrigin }))}
              >
                {customerOrigins.map(o => (
                  <option key={o} value={o}>{originLabels[o]}</option>
                ))}
              </select>
            </div>

            <div className="product-field">
              <label htmlFor="identity-source">Nguồn tiếp cận (Owner)</label>
              <select
                id="identity-source"
                disabled={pending}
                value={draft.acquisitionSource}
                onChange={e => setDraft(prev => ({ ...prev, acquisitionSource: e.target.value as CustomerAcquisitionSource }))}
              >
                {acquisitionSources.map(s => (
                  <option key={s} value={s}>{acquisitionSourceLabels[s]}</option>
                ))}
              </select>
            </div>

            <div className="product-field customer-checkbox-group">
              <span className="inventory-field-label">Lịch sử mua cũ (Owner)</span>
              <label className="product-check">
                <input
                  type="checkbox"
                  disabled={pending}
                  checked={draft.hadLegacyPurchase}
                  onChange={e => setDraft(prev => ({ ...prev, hadLegacyPurchase: e.target.checked }))}
                />
                <span>Đã từng mua hàng trước hệ thống (hadLegacyPurchase)</span>
              </label>
            </div>
          </>
        )}
      </div>

      <div className="product-field">
        <label htmlFor="identity-reason">Lý do đính chính *</label>
        <textarea
          id="identity-reason"
          rows={2}
          maxLength={500}
          required
          disabled={pending}
          value={draft.reason}
          aria-invalid={Boolean(fieldErrors.reason)}
          onChange={e => setDraft(prev => ({ ...prev, reason: e.target.value }))}
          placeholder="Ghi rõ lý do đổi SĐT hoặc nguồn khách (tối thiểu 3 ký tự)…"
        />
        {fieldErrors.reason && <p className="field-error">{fieldErrors.reason}</p>}
      </div>

      <div className="catalog-actions inventory-form-actions">
        <button type="submit" className="button" disabled={pending}>
          <ShieldCheck size={17} aria-hidden="true" />
          <span>{pending ? "Đang đính chính…" : "Lưu đính chính danh tính"}</span>
        </button>
      </div>
    </form>
  );
}

function CustomerOrdersTab({ api, customerId }: { api: CustomersApi; customerId: string }) {
  const [pagination, setPagination] = useState<PaginationState>({ pageIndex: 0, pageSize: 20 });
  const params = paginationParams(pagination);

  const ordersQuery = useQuery({
    queryKey: [...customerOrdersKey(customerId), params],
    queryFn: ({ signal }) => api.listOrders(customerId, params, signal)
  });

  const columns = useMemo<ColumnDef<CustomerOrderSummary>[]>(() => [
    {
      id: "code",
      header: "Mã đơn & Kênh",
      cell: ({ row: { original: ord } }) => (
        <div className="customer-identity-cell">
          <code className="customer-code">{ord.code}</code>
          <span className="hint">{salesChannelLabels[ord.salesChannel] ?? ord.salesChannel}</span>
        </div>
      )
    },
    {
      id: "placedAt",
      header: "Thời gian",
      cell: ({ row: { original: ord } }) => (
        <div className="customer-activity-cell">
          <span>Đặt: {formatVnDateTime(ord.placedAt)}</span>
          {ord.deliveredAt && <span>Giao: {formatVnDateTime(ord.deliveredAt)}</span>}
          {ord.cancelledAt && <span>Hủy: {formatVnDateTime(ord.cancelledAt)}</span>}
        </div>
      )
    },
    {
      id: "status",
      header: "Trạng thái đơn",
      cell: ({ row: { original: ord } }) => {
        const tone =
          ord.status === "delivered"
            ? "is-ok"
            : ord.status === "cancelled" || ord.status === "delivery_failed"
              ? "is-danger"
              : "is-warning";
        return (
          <span className={`inventory-badge ${tone}`}>
            {orderStatusLabels[ord.status] ?? ord.status}
          </span>
        );
      }
    },
    {
      id: "payment",
      header: "Thanh toán",
      cell: ({ row: { original: ord } }) => (
        <div className="customer-activity-cell">
          <strong>{paymentMethodLabels[ord.paymentMethod] ?? ord.paymentMethod}</strong>
          <span>{orderPaymentStatusLabels[ord.paymentStatus] ?? ord.paymentStatus}</span>
        </div>
      )
    },
    {
      id: "total",
      header: "Tổng tiền",
      cell: ({ row: { original: ord } }) => (
        <div className="customer-metrics-cell">
          <strong>{formatVnd(ord.totalVnd)}</strong>
          <span className="hint">
            Hàng: {formatVnd(ord.subtotalVnd)}
            {Number(ord.discountVnd) > 0 ? ` · Giảm: -${formatVnd(ord.discountVnd)}` : ""}
            {Number(ord.shippingFeeVnd) > 0 ? ` · Ship: ${formatVnd(ord.shippingFeeVnd)}` : ""}
          </span>
        </div>
      )
    }
  ], []);

  return (
    <div className="inventory-stack">
      <div>
        <h3>Lịch sử đơn hàng của khách</h3>
        <p className="hint">
          Hiển thị các đơn hàng đã tạo của khách hàng này (chỉ hiển thị thông tin bán hàng, không hiển thị giá vốn).
        </p>
      </div>

      <DataTable<CustomerOrderSummary>
        caption="Danh sách đơn hàng của khách hàng"
        data={ordersQuery.data?.items ?? []}
        columns={columns}
        totalItems={Number(ordersQuery.data?.totalItems ?? 0)}
        pagination={pagination}
        onPaginationChange={setPagination}
        loading={ordersQuery.isPending}
        error={ordersQuery.error}
        onRetry={() => void ordersQuery.refetch()}
        getRowId={row => row.id}
      />
    </div>
  );
}

function CustomerPreferencesTab({ detail }: { detail: CustomerDetail }) {
  return (
    <div className="inventory-stack">
      <div className="catalog-form inventory-form-card">
        <h3>Sở thích khách hàng (chỉ đọc)</h3>
        <p className="hint">
          Dữ liệu sở thích tự động tổng hợp từ đơn hàng giao thành công sẽ kích hoạt ở giai đoạn <strong>P2-11</strong>.
          Màn hình này chỉ hiển thị các bản ghi hiện có từ hồ sơ 360°.
        </p>

        {detail.interests.length === 0 ? (
          <p className="inventory-muted-note">Chưa có dữ liệu sở thích nào được ghi nhận cho khách hàng này.</p>
        ) : (
          <ul className="customer-card-list" aria-label="Danh sách sở thích khách hàng">
            {detail.interests.map(item => (
              <li key={item.id} className="customer-card">
                <div className="customer-card-row">
                  <div>
                    <div className="inventory-badges">
                      <span className="inventory-badge is-ok">
                        {interestTypeLabels[item.interestType] ?? item.interestType}
                      </span>
                      <span className="inventory-badge is-muted">
                        {interestSourceLabels[item.source] ?? item.source}
                      </span>
                    </div>
                    <p>
                      {item.priceBand
                        ? priceBandLabels[item.priceBand] ?? item.priceBand
                        : item.refId
                          ? `Mã tham chiếu: ${item.refId}`
                          : "—"}
                    </p>
                  </div>
                  <div className="customer-metrics-cell">
                    <strong>Điểm: {formatInt(item.score)}</strong>
                    <span className="hint">{formatVnDateTime(item.updatedAt)}</span>
                  </div>
                </div>
              </li>
            ))}
          </ul>
        )}
      </div>

      <div className="catalog-form inventory-form-card">
        <h3>Đồng ý liên hệ / Consent (chỉ đọc)</h3>
        <p className="hint">
          Các thao tác cập nhật consent, gộp hồ sơ trùng, xóa dữ liệu (ẩn danh hóa) và xuất dữ liệu cá nhân thuộc giai đoạn{" "}
          <strong>P2-12</strong>; lịch sử chiến dịch gửi tin thuộc <strong>P5-02</strong>. Màn hình này chỉ hiển thị trạng thái hiện có.
        </p>

        {detail.consents.length === 0 ? (
          <p className="inventory-muted-note">Chưa có bản ghi consent nào cho khách hàng này.</p>
        ) : (
          <ul className="customer-card-list" aria-label="Danh sách consent khách hàng">
            {detail.consents.map(item => (
              <li key={item.id} className="customer-card">
                <div className="customer-card-row">
                  <div>
                    <div className="inventory-badges">
                      <span className={`inventory-badge ${item.status === "granted" ? "is-ok" : "is-danger"}`}>
                        {consentStatusLabels[item.status] ?? item.status}
                      </span>
                      <span className="inventory-badge is-muted">
                        {consentChannelLabels[item.channel] ?? item.channel}
                      </span>
                      <span className="inventory-badge is-muted">
                        {consentSourceLabels[item.source] ?? item.source}
                      </span>
                    </div>
                    <p>{consentPurposeLabels[item.purpose] ?? item.purpose}</p>
                  </div>
                  <span className="hint">Ghi nhận: {formatVnDateTime(item.capturedAt)}</span>
                </div>
              </li>
            ))}
          </ul>
        )}
      </div>
    </div>
  );
}
