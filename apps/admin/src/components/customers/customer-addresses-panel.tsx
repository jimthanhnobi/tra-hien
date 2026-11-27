"use client";

import { useMemo, useRef, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { AlertTriangle, MapPin, Pencil, Plus, Star, Trash2 } from "lucide-react";
import { isConcurrencyConflict } from "../../lib/api";
import {
  AdministrativeGeoClient,
  PROVINCE_CODE_PATTERN,
  adminGeoProvincesKey,
  adminGeoWardsKey,
  isAdministrativeDataNotReady,
  selectWardsForProvince
} from "../../lib/administrative-geo";
import { CustomersApi, type CustomerAddress } from "../../lib/customers-api";
import {
  buildCreateAddressPayload,
  buildPatchAddressPayload,
  formatPhoneDisplay,
  toLocalPhone,
  type AddressFormErrors,
  type CustomerAddressDraft
} from "../../lib/customers-forms";
import { useAdmin } from "../providers";
import { Confirmation, Feedback, FormError, Pending } from "../ui";

function emptyDraft(defaultReceiverName: string, defaultReceiverPhone: string | null): CustomerAddressDraft {
  return {
    label: "",
    receiverName: defaultReceiverName,
    receiverPhone: toLocalPhone(defaultReceiverPhone) ?? "",
    provinceCode: "",
    wardCode: "",
    wardNameText: "",
    addressLine: "",
    isDefault: false
  };
}

function draftFromAddress(addr: CustomerAddress): CustomerAddressDraft {
  return {
    label: addr.label ?? "",
    receiverName: addr.receiverName,
    receiverPhone: toLocalPhone(addr.receiverPhone) ?? addr.receiverPhone,
    provinceCode: addr.provinceCode,
    wardCode: addr.wardCode ?? "",
    wardNameText: addr.wardNameText ?? "",
    addressLine: addr.addressLine,
    isDefault: addr.isDefault
  };
}

export function CustomerAddressesPanel({
  customerId,
  customerFullName,
  customerPhoneE164,
  rowVersion,
  addresses,
  canEdit,
  onMutated,
  onConflict
}: {
  customerId: string;
  customerFullName: string;
  customerPhoneE164: string | null;
  rowVersion: string;
  addresses: readonly CustomerAddress[];
  canEdit: boolean;
  onMutated: () => Promise<void>;
  onConflict: () => void;
}) {
  const { api: admin } = useAdmin();
  const api = useMemo(() => new CustomersApi(admin), [admin]);
  const geo = useMemo(() => new AdministrativeGeoClient(admin), [admin]);

  const [formMode, setFormMode] = useState<"closed" | "create" | "edit">("closed");
  const [editingAddress, setEditingAddress] = useState<CustomerAddress | null>(null);
  const [draft, setDraft] = useState<CustomerAddressDraft>(() => emptyDraft(customerFullName, customerPhoneE164));
  const [wardFilter, setWardFilter] = useState("");
  const [fieldErrors, setFieldErrors] = useState<AddressFormErrors>({});
  const [serverError, setServerError] = useState<unknown>();
  const [notice, setNotice] = useState("");
  const [pending, setPending] = useState(false);
  const busy = useRef(false);

  const formOpen = canEdit && formMode !== "closed";

  // Only load public province/ward catalogs when an authorized editor opens the address form.
  // Guarantees 0 geo requests for `customers.read`-only users and 0 unused calls on overview.
  const provincesQuery = useQuery({
    queryKey: adminGeoProvincesKey,
    queryFn: ({ signal }) => geo.listProvinces(signal),
    enabled: formOpen,
    refetchOnWindowFocus: false
  });

  const geoNotReady = isAdministrativeDataNotReady(provincesQuery.error);
  const validSelectedProvince = PROVINCE_CODE_PATTERN.test(draft.provinceCode);
  const provinces = useMemo(() => provincesQuery.data?.items ?? [], [provincesQuery.data]);
  const provincesEmpty = provincesQuery.isSuccess && provinces.length === 0;
  // Only fetch active wards when the selected province is present in the loaded active province catalog;
  // prevents spurious 404 requests when editing an inactive historical province (`isLocationActive = false`).
  const selectedProvinceActive =
    provincesQuery.isSuccess && provinces.some(p => p.code === draft.provinceCode);

  const wardsQuery = useQuery({
    queryKey: adminGeoWardsKey(draft.provinceCode),
    queryFn: ({ signal }) => geo.listWards(draft.provinceCode, signal),
    enabled: formOpen && !geoNotReady && validSelectedProvince && selectedProvinceActive,
    refetchOnWindowFocus: false
  });

  const wardsNotReady = isAdministrativeDataNotReady(wardsQuery.error);
  const adminDataUnavailable = geoNotReady || wardsNotReady;
  const wardsLoading = wardsQuery.isLoading;

  // Defense-in-depth: only accept ward items whose `provinceCode` matches `draft.provinceCode`.
  const matchedWardsData =
    wardsQuery.data && wardsQuery.data.provinceCode === draft.provinceCode ? wardsQuery.data : undefined;
  const wards = useMemo(
    () => selectWardsForProvince(wardsQuery.data, draft.provinceCode),
    [wardsQuery.data, draft.provinceCode]
  );
  const wardsEmptyForProvince = Boolean(
    validSelectedProvince && selectedProvinceActive && matchedWardsData && matchedWardsData.items.length === 0
  );

  const filteredWards = useMemo(() => {
    const q = wardFilter.trim().toLowerCase();
    if (!q) return wards;
    return wards.filter(w => w.name.toLowerCase().includes(q) || w.code.includes(q));
  }, [wards, wardFilter]);

  // Keep the currently selected active ward rendered in `<select>` even when `wardFilter` hides it,
  // so visible selection always stays synchronized with `draft.wardCode`.
  const pinnedSelectedWard = useMemo(() => {
    if (!draft.wardCode) return null;
    const active = wards.find(w => w.code === draft.wardCode);
    if (!active) return null;
    return filteredWards.some(w => w.code === active.code) ? null : active;
  }, [wards, filteredWards, draft.wardCode]);

  // Preserve historical province/ward display cleanly when editing an inactive location.
  const historicalProvinceFallback =
    editingAddress &&
    draft.provinceCode === editingAddress.provinceCode &&
    !provinces.some(p => p.code === editingAddress.provinceCode)
      ? { code: editingAddress.provinceCode, name: `${editingAddress.provinceName} (địa giới cũ)` }
      : null;

  const historicalWardFallback =
    editingAddress &&
    editingAddress.wardCode &&
    draft.provinceCode === editingAddress.provinceCode &&
    draft.wardCode === editingAddress.wardCode &&
    !wards.some(w => w.code === editingAddress.wardCode)
      ? { code: editingAddress.wardCode, name: `${editingAddress.wardName ?? editingAddress.wardCode} (địa giới cũ)` }
      : null;

  const showWardTextFallback =
    wardsEmptyForProvince ||
    Boolean(editingAddress && (!editingAddress.isLocationActive || (!editingAddress.wardCode && editingAddress.wardNameText)));

  function openCreate() {
    setFormMode("create");
    setEditingAddress(null);
    setDraft(emptyDraft(customerFullName, customerPhoneE164));
    setWardFilter("");
    setFieldErrors({});
    setServerError(undefined);
    setNotice("");
  }

  function openEdit(addr: CustomerAddress) {
    setFormMode("edit");
    setEditingAddress(addr);
    setDraft(draftFromAddress(addr));
    setWardFilter("");
    setFieldErrors({});
    setServerError(undefined);
    setNotice("");
  }

  function closeForm() {
    setFormMode("closed");
    setEditingAddress(null);
    setWardFilter("");
    setFieldErrors({});
    setServerError(undefined);
  }

  async function handleSave(event: React.FormEvent) {
    event.preventDefault();
    if (busy.current) return;
    setServerError(undefined);
    setNotice("");

    if (formMode === "create") {
      const { payload, errors } = buildCreateAddressPayload(draft, rowVersion);
      if (errors || !payload) {
        setFieldErrors(errors ?? {});
        return;
      }
      setFieldErrors({});
      busy.current = true;
      setPending(true);
      try {
        await api.createAddress(customerId, payload);
        closeForm();
        setNotice("Đã thêm địa chỉ giao hàng mới.");
        await onMutated();
      } catch (error) {
        if (isConcurrencyConflict(error)) onConflict();
        setServerError(error);
      } finally {
        busy.current = false;
        setPending(false);
      }
      return;
    }

    if (formMode === "edit" && editingAddress) {
      const { payload, errors } = buildPatchAddressPayload(editingAddress, draft, rowVersion);
      if (errors || !payload) {
        setFieldErrors(errors ?? {});
        return;
      }
      const mutableKeys = Object.keys(payload).filter(k => k !== "rowVersion");
      if (mutableKeys.length === 0) {
        setNotice("Chưa có thay đổi nào để lưu.");
        closeForm();
        return;
      }
      setFieldErrors({});
      busy.current = true;
      setPending(true);
      try {
        await api.patchAddress(customerId, editingAddress.id, payload);
        closeForm();
        setNotice("Đã cập nhật địa chỉ giao hàng.");
        await onMutated();
      } catch (error) {
        if (isConcurrencyConflict(error)) onConflict();
        setServerError(error);
      } finally {
        busy.current = false;
        setPending(false);
      }
    }
  }

  async function handleSetDefault(addr: CustomerAddress) {
    if (busy.current) return;
    setServerError(undefined);
    setNotice("");
    busy.current = true;
    setPending(true);
    try {
      await api.patchAddress(customerId, addr.id, { isDefault: true, rowVersion });
      setNotice(`Đã đặt "${addr.label ?? addr.receiverName}" làm địa chỉ mặc định.`);
      await onMutated();
    } catch (error) {
      if (isConcurrencyConflict(error)) onConflict();
      setServerError(error);
    } finally {
      busy.current = false;
      setPending(false);
    }
  }

  async function handleDelete(addr: CustomerAddress) {
    if (busy.current) return;
    setServerError(undefined);
    setNotice("");
    busy.current = true;
    setPending(true);
    try {
      await api.deleteAddress(customerId, addr.id, rowVersion);
      if (editingAddress?.id === addr.id) closeForm();
      setNotice("Đã xóa địa chỉ khỏi sổ địa chỉ khách hàng.");
      await onMutated();
    } catch (error) {
      if (isConcurrencyConflict(error)) onConflict();
      setServerError(error);
      throw error;
    } finally {
      busy.current = false;
      setPending(false);
    }
  }

  return (
    <div className="inventory-stack">
      <div className="inventory-section-header">
        <div>
          <h3>Sổ địa chỉ giao hàng ({addresses.length})</h3>
          <p className="hint">
            Dùng chuẩn địa giới hành chính Tỉnh/Thành phố và Phường/Xã của hệ thống.
          </p>
        </div>
        {canEdit && formMode === "closed" && (
          <button type="button" className="button" onClick={openCreate}>
            <Plus size={17} aria-hidden="true" />
            <span>Thêm địa chỉ mới</span>
          </button>
        )}
      </div>

      {notice && <Feedback>{notice}</Feedback>}
      {!formOpen && <FormError error={serverError} />}

      {formOpen && (
        <form className="catalog-form inventory-form-card" onSubmit={event => void handleSave(event)} noValidate>
          <h3>{formMode === "create" ? "Thêm địa chỉ mới" : "Chỉnh sửa địa chỉ"}</h3>

          {adminDataUnavailable && (
            <div className="catalog-warning customer-warning-banner" role="alert">
              <AlertTriangle size={19} aria-hidden="true" />
              <div className="inventory-stack">
                <div>
                  <strong>Danh mục địa giới hành chính chưa sẵn sàng.</strong>
                  <p>
                    Hệ thống chưa được nạp danh mục Tỉnh/Thành phố và Phường/Xã.
                    {formMode === "edit"
                      ? " Bạn vẫn có thể sửa tên, SĐT hoặc số nhà nếu giữ nguyên địa giới hiện tại."
                      : " Vui lòng nạp dữ liệu địa giới hành chính trong mục Cài đặt trước khi thêm địa chỉ mới."}
                  </p>
                </div>
                <div className="catalog-actions">
                  <button
                    type="button"
                    className="button secondary"
                    onClick={() => {
                      void provincesQuery.refetch();
                      if (validSelectedProvince && selectedProvinceActive) void wardsQuery.refetch();
                    }}
                  >
                    Kiểm tra lại danh mục
                  </button>
                </div>
              </div>
            </div>
          )}

          {!adminDataUnavailable && provincesQuery.isError && (
            <div className="inventory-stack">
              <FormError error={provincesQuery.error} />
              <div className="catalog-actions">
                <button
                  type="button"
                  className="button secondary"
                  onClick={() => void provincesQuery.refetch()}
                >
                  Thử tải lại Tỉnh/Thành phố
                </button>
              </div>
            </div>
          )}

          {!adminDataUnavailable && wardsQuery.isError && (
            <div className="inventory-stack">
              <FormError error={wardsQuery.error} />
              <div className="catalog-actions">
                <button
                  type="button"
                  className="button secondary"
                  onClick={() => void wardsQuery.refetch()}
                >
                  Thử tải lại Phường/Xã
                </button>
              </div>
            </div>
          )}

          {!adminDataUnavailable && provincesEmpty && (
            <div className="catalog-warning customer-warning-banner" role="status">
              <AlertTriangle size={19} aria-hidden="true" />
              <div>
                <strong>Chưa có Tỉnh/Thành phố đang hoạt động.</strong>
                <p>Danh mục địa giới hành chính hiện chưa có Tỉnh/Thành phố khả dụng.</p>
              </div>
            </div>
          )}

          <FormError error={serverError} />

          <div className="catalog-fields">
            <div className="product-field">
              <label htmlFor="addr-label">Nhãn địa chỉ (tùy chọn)</label>
              <input
                id="addr-label"
                type="text"
                maxLength={80}
                disabled={pending}
                value={draft.label}
                aria-invalid={Boolean(fieldErrors.label)}
                onChange={e => setDraft(prev => ({ ...prev, label: e.target.value }))}
                placeholder="Nhà riêng, Văn phòng, Quà biếu…"
              />
              {fieldErrors.label && <p className="field-error">{fieldErrors.label}</p>}
            </div>

            <div className="product-field">
              <label htmlFor="addr-receiver-name">Tên người nhận *</label>
              <input
                id="addr-receiver-name"
                type="text"
                maxLength={150}
                required
                disabled={pending}
                value={draft.receiverName}
                aria-invalid={Boolean(fieldErrors.receiverName)}
                onChange={e => setDraft(prev => ({ ...prev, receiverName: e.target.value }))}
              />
              {fieldErrors.receiverName && <p className="field-error">{fieldErrors.receiverName}</p>}
            </div>

            <div className="product-field">
              <label htmlFor="addr-receiver-phone">SĐT người nhận *</label>
              <input
                id="addr-receiver-phone"
                type="tel"
                maxLength={30}
                required
                disabled={pending}
                value={draft.receiverPhone}
                aria-invalid={Boolean(fieldErrors.receiverPhone)}
                onChange={e => setDraft(prev => ({ ...prev, receiverPhone: e.target.value }))}
                placeholder="0912 345 678"
              />
              {fieldErrors.receiverPhone && <p className="field-error">{fieldErrors.receiverPhone}</p>}
            </div>

            <div className="product-field">
              <label htmlFor="addr-province">Tỉnh / Thành phố *</label>
              {provincesQuery.isPending ? (
                <Pending>Đang tải danh mục Tỉnh/Thành phố…</Pending>
              ) : (
                <select
                  id="addr-province"
                  disabled={pending || adminDataUnavailable || (provincesEmpty && !historicalProvinceFallback)}
                  value={draft.provinceCode}
                  aria-invalid={Boolean(fieldErrors.provinceCode)}
                  onChange={e => {
                    const nextCode = e.target.value;
                    setDraft(prev => ({
                      ...prev,
                      provinceCode: nextCode,
                      wardCode: "",
                      wardNameText: ""
                    }));
                    setWardFilter("");
                  }}
                >
                  <option value="">
                    {provincesEmpty ? "-- Chưa có Tỉnh / Thành phố --" : "-- Chọn Tỉnh / Thành phố --"}
                  </option>
                  {historicalProvinceFallback && (
                    <option value={historicalProvinceFallback.code}>
                      {historicalProvinceFallback.name}
                    </option>
                  )}
                  {provinces.map(p => (
                    <option key={p.code} value={p.code}>{p.name}</option>
                  ))}
                </select>
              )}
              {fieldErrors.provinceCode && <p className="field-error">{fieldErrors.provinceCode}</p>}
            </div>

            <div className="product-field">
              <label htmlFor="addr-ward">Phường / Xã *</label>
              {wards.length > 15 && (
                <input
                  type="search"
                  aria-label="Lọc nhanh Phường / Xã"
                  value={wardFilter}
                  disabled={pending || adminDataUnavailable}
                  onChange={e => setWardFilter(e.target.value)}
                  placeholder="Gõ để lọc nhanh Phường / Xã…"
                />
              )}
              <select
                id="addr-ward"
                disabled={pending || adminDataUnavailable || !validSelectedProvince || wardsLoading}
                value={draft.wardCode}
                aria-invalid={Boolean(fieldErrors.wardCode)}
                onChange={e => {
                  const nextWard = e.target.value;
                  setDraft(prev => ({
                    ...prev,
                    wardCode: nextWard,
                    wardNameText: nextWard ? "" : prev.wardNameText
                  }));
                }}
              >
                <option value="">
                  {!validSelectedProvince
                    ? "-- Chọn Tỉnh/Thành phố trước --"
                    : wardsLoading
                      ? "Đang tải Phường/Xã…"
                      : wardsEmptyForProvince
                        ? "-- Tỉnh chưa có danh mục Phường/Xã --"
                        : "-- Chọn Phường / Xã --"}
                </option>
                {historicalWardFallback && (
                  <option value={historicalWardFallback.code}>{historicalWardFallback.name}</option>
                )}
                {pinnedSelectedWard && (
                  <option value={pinnedSelectedWard.code}>{pinnedSelectedWard.name} (đang chọn)</option>
                )}
                {filteredWards.map(w => (
                  <option key={w.code} value={w.code}>{w.name}</option>
                ))}
              </select>
              {wardsEmptyForProvince && (
                <p className="hint" role="status">
                  Tỉnh/Thành phố này hiện chưa có phường/xã hoạt động trong danh mục — vui lòng nhập tên Phường/Xã dự phòng bên dưới.
                </p>
              )}
              {fieldErrors.wardCode && <p className="field-error">{fieldErrors.wardCode}</p>}
            </div>

            {showWardTextFallback && (
              <div className="product-field">
                <label htmlFor="addr-ward-text">Tên Phường / Xã nhập tay (dự phòng)</label>
                <input
                  id="addr-ward-text"
                  type="text"
                  maxLength={150}
                  disabled={pending || Boolean(draft.wardCode)}
                  value={draft.wardNameText}
                  aria-invalid={Boolean(fieldErrors.wardNameText)}
                  onChange={e => setDraft(prev => ({ ...prev, wardNameText: e.target.value }))}
                  placeholder="Chỉ dùng khi tỉnh chưa có danh mục phường/xã"
                />
                {fieldErrors.wardNameText && <p className="field-error">{fieldErrors.wardNameText}</p>}
              </div>
            )}
          </div>

          <div className="product-field">
            <label htmlFor="addr-line">Số nhà, tên đường / thôn xóm *</label>
            <input
              id="addr-line"
              type="text"
              maxLength={255}
              required
              disabled={pending}
              value={draft.addressLine}
              aria-invalid={Boolean(fieldErrors.addressLine)}
              onChange={e => setDraft(prev => ({ ...prev, addressLine: e.target.value }))}
              placeholder="Số 12 ngõ 45 phố Tràng Tiền…"
            />
            {fieldErrors.addressLine && <p className="field-error">{fieldErrors.addressLine}</p>}
          </div>

          <label className="product-check">
            <input
              type="checkbox"
              disabled={pending}
              checked={draft.isDefault}
              onChange={e => setDraft(prev => ({ ...prev, isDefault: e.target.checked }))}
            />
            <span>Đặt làm địa chỉ giao hàng mặc định</span>
          </label>

          <div className="catalog-actions inventory-form-actions">
            <button
              type="submit"
              className="button"
              disabled={pending || (formMode === "create" && (adminDataUnavailable || provincesEmpty))}
            >
              {pending ? "Đang lưu…" : formMode === "create" ? "Lưu địa chỉ" : "Cập nhật địa chỉ"}
            </button>
            <button type="button" className="button secondary" disabled={pending} onClick={closeForm}>
              Hủy
            </button>
          </div>
        </form>
      )}

      {addresses.length === 0 ? (
        <div className="empty-state">
          <MapPin size={32} strokeWidth={1.4} aria-hidden="true" />
          <h3>Chưa có địa chỉ giao hàng</h3>
          <p>Khách hàng này chưa lưu địa chỉ giao hàng nào trong sổ địa chỉ.</p>
        </div>
      ) : (
        <ul className="customer-card-list" aria-label="Danh sách địa chỉ khách hàng">
          {addresses.map(addr => {
            const wardDisplay = addr.wardName ?? addr.wardNameText ?? "";
            const fullLocation = [addr.addressLine, wardDisplay, addr.provinceName].filter(Boolean).join(", ");
            return (
              <li key={addr.id} className="customer-card">
                <div className="customer-card-header">
                  <div>
                    <div className="inventory-badges">
                      <strong>{addr.label ?? "Địa chỉ giao hàng"}</strong>
                      {addr.isDefault && <span className="inventory-badge is-ok">Mặc định</span>}
                      {!addr.isLocationActive && (
                        <span className="inventory-badge is-warning">Địa giới cũ (ngừng hoạt động)</span>
                      )}
                    </div>
                    <p className="customer-address-recipient">
                      {addr.receiverName} · {formatPhoneDisplay(addr.receiverPhone)}
                    </p>
                    <p>{fullLocation}</p>
                  </div>
                </div>

                {canEdit && (
                  <div className="catalog-actions inventory-form-actions">
                    <button
                      type="button"
                      className="button secondary"
                      disabled={pending}
                      onClick={() => openEdit(addr)}
                    >
                      <Pencil size={15} aria-hidden="true" />
                      <span>Sửa</span>
                    </button>

                    {!addr.isDefault && (
                      <button
                        type="button"
                        className="button secondary"
                        disabled={pending}
                        onClick={() => void handleSetDefault(addr)}
                      >
                        <Star size={15} aria-hidden="true" />
                        <span>Đặt mặc định</span>
                      </button>
                    )}

                    <Confirmation
                      title="Xóa địa chỉ này?"
                      confirmLabel="Xóa địa chỉ"
                      pending={pending}
                      trigger={
                        <button type="button" className="button secondary" disabled={pending}>
                          <Trash2 size={15} aria-hidden="true" />
                          <span>Xóa</span>
                        </button>
                      }
                      onConfirm={() => handleDelete(addr)}
                    >
                      Địa chỉ <strong>{fullLocation}</strong> sẽ bị xóa khỏi sổ địa chỉ của khách hàng này.
                      Các đơn hàng cũ đã đặt không bị ảnh hưởng.
                    </Confirmation>
                  </div>
                )}
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
}
