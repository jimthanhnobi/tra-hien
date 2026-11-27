"use client";

import { useMemo, useRef, useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useQuery } from "@tanstack/react-query";
import { ArrowLeft } from "lucide-react";
import { CustomersApi, customerTagsKey, customersRootKey } from "../../lib/customers-api";
import {
  acquisitionSourceLabels,
  acquisitionSources,
  buildCreateCustomerPayload,
  contactStatusLabels,
  customerGroupLabels,
  customerGroups,
  writableContactStatuses,
  type CreateCustomerDraft,
  type CustomerAcquisitionSource,
  type CustomerFormErrors,
  type CustomerGroup,
  type WritableContactStatus
} from "../../lib/customers-forms";
import { useProductNavigationGuard } from "../products/product-navigation";
import { useAdmin } from "../providers";
import { FormError } from "../ui";
import { DuplicatePhoneLink, TagChip } from "./customer-badges";

const initialDraft: CreateCustomerDraft = {
  fullName: "",
  phone: "",
  zaloName: "",
  email: "",
  customerGroup: "retail",
  isVip: false,
  acquisitionSource: "zalo_personal",
  contactStatus: "active",
  hadLegacyPurchase: false,
  noteSummary: "",
  tagIds: []
};

export function CustomerCreatePanel() {
  const { api: admin, queries } = useAdmin();
  const api = useMemo(() => new CustomersApi(admin), [admin]);
  const router = useRouter();

  const [draft, setDraft] = useState<CreateCustomerDraft>(initialDraft);
  const [fieldErrors, setFieldErrors] = useState<CustomerFormErrors>({});
  const [serverError, setServerError] = useState<unknown>();
  const [pending, setPending] = useState(false);
  const busy = useRef(false);

  const dirty = useMemo(() => JSON.stringify(draft) !== JSON.stringify(initialDraft), [draft]);
  useProductNavigationGuard(dirty, pending);

  // Page is strictly gated by `customers.write` in CustomersScreen.
  const tagsQuery = useQuery({
    queryKey: customerTagsKey,
    queryFn: ({ signal }) => api.listTags(signal),
    refetchOnWindowFocus: false
  });

  function toggleTag(id: string) {
    setDraft(prev => ({
      ...prev,
      tagIds: prev.tagIds.includes(id) ? prev.tagIds.filter(x => x !== id) : [...prev.tagIds, id]
    }));
  }

  async function handleSubmit(event: React.FormEvent) {
    event.preventDefault();
    if (busy.current) return;
    setServerError(undefined);
    const { payload, errors } = buildCreateCustomerPayload(draft);
    if (errors || !payload) {
      setFieldErrors(errors ?? {});
      return;
    }
    setFieldErrors({});
    busy.current = true;
    setPending(true);
    try {
      const created = await api.createCustomer(payload);
      await queries.invalidateQueries({ queryKey: customersRootKey });
      router.push(`/customers/${created.id}`);
    } catch (error) {
      setServerError(error);
    } finally {
      busy.current = false;
      setPending(false);
    }
  }

  return (
    <section className="inventory-panel" aria-labelledby="customer-create-heading">
      <div className="inventory-section-header">
        <div>
          <Link className="customer-back-link" href="/customers">
            <ArrowLeft size={16} aria-hidden="true" />
            <span>Quay lại danh sách khách hàng</span>
          </Link>
          <h2 id="customer-create-heading">Tạo hồ sơ khách hàng mới</h2>
          <p className="hint">
            Bắt buộc có họ tên và ít nhất một thông tin liên hệ (số điện thoại di động VN hoặc tên Zalo).
          </p>
        </div>
      </div>

      <form className="catalog-form inventory-form-card" onSubmit={event => void handleSubmit(event)} noValidate>
        <FormError error={serverError} />
        <DuplicatePhoneLink error={serverError} />

        {fieldErrors.contact && (
          <div className="alert" role="alert">
            <div><p>{fieldErrors.contact}</p></div>
          </div>
        )}

        <div className="catalog-fields">
          <div className="product-field">
            <label htmlFor="create-full-name">Họ tên khách hàng *</label>
            <input
              id="create-full-name"
              type="text"
              maxLength={150}
              required
              disabled={pending}
              value={draft.fullName}
              aria-invalid={Boolean(fieldErrors.fullName)}
              onChange={e => setDraft(prev => ({ ...prev, fullName: e.target.value }))}
              placeholder="Ví dụ: Nguyễn Minh Quân"
            />
            {fieldErrors.fullName && <p className="field-error">{fieldErrors.fullName}</p>}
          </div>

          <div className="product-field">
            <label htmlFor="create-phone">Số điện thoại di động VN</label>
            <input
              id="create-phone"
              type="tel"
              maxLength={30}
              disabled={pending}
              value={draft.phone}
              aria-invalid={Boolean(fieldErrors.phone)}
              onChange={e => setDraft(prev => ({ ...prev, phone: e.target.value }))}
              placeholder="0912 345 678 hoặc +84912345678"
            />
            <p className="hint">Tự chuẩn hóa về +84xxxxxxxxx; để trống nếu khách chỉ liên hệ qua Zalo.</p>
            {fieldErrors.phone && <p className="field-error">{fieldErrors.phone}</p>}
          </div>

          <div className="product-field">
            <label htmlFor="create-zalo-name">Tên Zalo</label>
            <input
              id="create-zalo-name"
              type="text"
              maxLength={150}
              disabled={pending}
              value={draft.zaloName}
              aria-invalid={Boolean(fieldErrors.zaloName)}
              onChange={e => setDraft(prev => ({ ...prev, zaloName: e.target.value }))}
              placeholder="Tên hiển thị trên Zalo"
            />
            {fieldErrors.zaloName && <p className="field-error">{fieldErrors.zaloName}</p>}
          </div>

          <div className="product-field">
            <label htmlFor="create-email">Email</label>
            <input
              id="create-email"
              type="email"
              maxLength={254}
              disabled={pending}
              value={draft.email}
              aria-invalid={Boolean(fieldErrors.email)}
              onChange={e => setDraft(prev => ({ ...prev, email: e.target.value }))}
              placeholder="khachhang@example.com"
            />
            {fieldErrors.email && <p className="field-error">{fieldErrors.email}</p>}
          </div>

          <div className="product-field">
            <label htmlFor="create-group">Nhóm khách hàng</label>
            <select
              id="create-group"
              disabled={pending}
              value={draft.customerGroup}
              onChange={e => setDraft(prev => ({ ...prev, customerGroup: e.target.value as CustomerGroup }))}
            >
              {customerGroups.map(g => (
                <option key={g} value={g}>{customerGroupLabels[g]}</option>
              ))}
            </select>
          </div>

          <div className="product-field">
            <label htmlFor="create-source">Nguồn tiếp cận</label>
            <select
              id="create-source"
              disabled={pending}
              value={draft.acquisitionSource}
              onChange={e => setDraft(prev => ({ ...prev, acquisitionSource: e.target.value as CustomerAcquisitionSource }))}
            >
              {acquisitionSources.map(s => (
                <option key={s} value={s}>{acquisitionSourceLabels[s]}</option>
              ))}
            </select>
          </div>

          <div className="product-field">
            <label htmlFor="create-contact-status">Trạng thái liên hệ</label>
            <select
              id="create-contact-status"
              disabled={pending}
              value={draft.contactStatus}
              onChange={e => setDraft(prev => ({ ...prev, contactStatus: e.target.value as WritableContactStatus }))}
            >
              {writableContactStatuses.map(s => (
                <option key={s} value={s}>{contactStatusLabels[s]}</option>
              ))}
            </select>
          </div>

          <div className="product-field customer-checkbox-group">
            <span className="inventory-field-label">Phân loại bổ sung</span>
            <label className="product-check">
              <input
                type="checkbox"
                disabled={pending}
                checked={draft.isVip}
                onChange={e => setDraft(prev => ({ ...prev, isVip: e.target.checked }))}
              />
              <span>Khách hàng VIP</span>
            </label>
            <label className="product-check">
              <input
                type="checkbox"
                disabled={pending}
                checked={draft.hadLegacyPurchase}
                onChange={e => setDraft(prev => ({ ...prev, hadLegacyPurchase: e.target.checked }))}
              />
              <span>Đã từng mua hàng trước khi dùng hệ thống</span>
            </label>
          </div>
        </div>

        <div className="product-field">
          <label htmlFor="create-note-summary">Ghi chú tóm tắt</label>
          <textarea
            id="create-note-summary"
            rows={3}
            maxLength={2000}
            disabled={pending}
            value={draft.noteSummary}
            aria-invalid={Boolean(fieldErrors.noteSummary)}
            onChange={e => setDraft(prev => ({ ...prev, noteSummary: e.target.value }))}
            placeholder="Gu trà, dòng ấm yêu thích, lưu ý giao hàng…"
          />
          {fieldErrors.noteSummary && <p className="field-error">{fieldErrors.noteSummary}</p>}
        </div>

        {(tagsQuery.data?.length ?? 0) > 0 && (
          <fieldset className="product-checks">
            <legend className="inventory-field-label">Gắn nhãn ban đầu</legend>
            <div className="customer-tag-picker">
              {(tagsQuery.data ?? []).map(tag => (
                <label key={tag.id} className="customer-tag-option">
                  <input
                    type="checkbox"
                    disabled={pending}
                    checked={draft.tagIds.includes(tag.id)}
                    onChange={() => toggleTag(tag.id)}
                  />
                  <TagChip tag={tag} />
                </label>
              ))}
            </div>
          </fieldset>
        )}

        <div className="catalog-actions inventory-form-actions">
          <button type="submit" className="button" disabled={pending}>
            {pending ? "Đang tạo hồ sơ…" : "Tạo khách hàng"}
          </button>
          <Link className="button secondary" href="/customers">
            Hủy
          </Link>
        </div>
      </form>
    </section>
  );
}
