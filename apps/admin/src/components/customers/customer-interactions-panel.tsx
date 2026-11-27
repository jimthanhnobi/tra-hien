"use client";

import { useMemo, useRef, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { CheckCircle2, Lock, MessageSquarePlus, Pencil } from "lucide-react";
import {
  CustomersApi,
  customerInteractionsKey,
  customerOrdersKey,
  type CustomerInteraction
} from "../../lib/customers-api";
import {
  buildCreateInteractionPayload,
  buildPatchInteractionPayload,
  canCompleteFollowUp,
  canEditInteraction,
  formatVnDateTime,
  interactionChannelLabels,
  interactionChannels,
  interactionKindLabels,
  manualInteractionKinds,
  toVnDateTimeLocal,
  validateFollowUpResult,
  type InteractionChannel,
  type InteractionFormDraft,
  type InteractionFormErrors,
  type ManualInteractionKind
} from "../../lib/customers-forms";
import { useAdmin } from "../providers";
import { Feedback, FormError, Pending } from "../ui";

const emptyInteractionDraft: InteractionFormDraft = {
  channel: "zalo_personal",
  kind: "consultation",
  content: "",
  occurredAt: "",
  followUpAt: "",
  orderId: ""
};

function draftFromInteraction(item: CustomerInteraction): InteractionFormDraft {
  const safeKind: ManualInteractionKind = manualInteractionKinds.includes(item.kind as ManualInteractionKind)
    ? (item.kind as ManualInteractionKind)
    : "note";
  return {
    channel: item.channel,
    kind: safeKind,
    content: item.content,
    occurredAt: toVnDateTimeLocal(item.occurredAt),
    followUpAt: toVnDateTimeLocal(item.followUpAt),
    orderId: item.orderId ?? ""
  };
}

export function CustomerInteractionsPanel({
  customerId,
  canEdit,
  onMutated
}: {
  customerId: string;
  canEdit: boolean;
  onMutated: () => Promise<void>;
}) {
  const { api: admin } = useAdmin();
  const api = useMemo(() => new CustomersApi(admin), [admin]);

  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(20);

  const [showCreate, setShowCreate] = useState(false);
  const [createDraft, setCreateDraft] = useState<InteractionFormDraft>(emptyInteractionDraft);
  const [createErrors, setCreateErrors] = useState<InteractionFormErrors>({});
  const [createServerError, setCreateServerError] = useState<unknown>();

  const [editingItem, setEditingItem] = useState<CustomerInteraction | null>(null);
  const [editDraft, setEditDraft] = useState<InteractionFormDraft>(emptyInteractionDraft);
  const [editErrors, setEditErrors] = useState<InteractionFormErrors>({});
  const [editServerError, setEditServerError] = useState<unknown>();

  const [completingId, setCompletingId] = useState<string | null>(null);
  const [followUpResult, setFollowUpResult] = useState("");
  const [followUpResultError, setFollowUpResultError] = useState<string | null>(null);
  const [completeServerError, setCompleteServerError] = useState<unknown>();

  const [notice, setNotice] = useState("");
  const [pending, setPending] = useState(false);
  const busy = useRef(false);

  const interactionsQuery = useQuery({
    queryKey: [...customerInteractionsKey(customerId), { page, pageSize }],
    queryFn: ({ signal }) => api.listInteractions(customerId, { page, pageSize }, signal)
  });

  // Load recent customer orders only when the editor opens create/edit to pick an optional linked order.
  const ordersForSelectQuery = useQuery({
    queryKey: [...customerOrdersKey(customerId), { page: 1, pageSize: 20 }],
    queryFn: ({ signal }) => api.listOrders(customerId, { page: 1, pageSize: 20 }, signal),
    enabled: canEdit && (showCreate || editingItem !== null),
    refetchOnWindowFocus: false
  });

  const items = interactionsQuery.data?.items ?? [];
  const totalItems = Number(interactionsQuery.data?.totalItems ?? 0);
  const totalPages = Math.max(1, Math.ceil(totalItems / pageSize));
  const selectableOrders = ordersForSelectQuery.data?.items ?? [];

  function openCreate() {
    setShowCreate(true);
    setEditingItem(null);
    setCompletingId(null);
    setCreateDraft(emptyInteractionDraft);
    setCreateErrors({});
    setCreateServerError(undefined);
    setNotice("");
  }

  function openEdit(item: CustomerInteraction) {
    if (!canEditInteraction(item)) return;
    setEditingItem(item);
    setShowCreate(false);
    setCompletingId(null);
    setEditDraft(draftFromInteraction(item));
    setEditErrors({});
    setEditServerError(undefined);
    setNotice("");
  }

  function openComplete(item: CustomerInteraction) {
    setCompletingId(item.id);
    setEditingItem(null);
    setFollowUpResult("");
    setFollowUpResultError(null);
    setCompleteServerError(undefined);
    setNotice("");
  }

  async function handleCreate(event: React.FormEvent) {
    event.preventDefault();
    if (busy.current) return;
    setCreateServerError(undefined);
    setNotice("");
    const { payload, errors } = buildCreateInteractionPayload(createDraft);
    if (errors || !payload) {
      setCreateErrors(errors ?? {});
      return;
    }
    setCreateErrors({});
    busy.current = true;
    setPending(true);
    try {
      await api.createInteraction(customerId, payload);
      setShowCreate(false);
      setCreateDraft(emptyInteractionDraft);
      setPage(1);
      setNotice("Đã ghi nhận tương tác mới.");
      await onMutated();
      await interactionsQuery.refetch();
    } catch (error) {
      setCreateServerError(error);
    } finally {
      busy.current = false;
      setPending(false);
    }
  }

  async function handleUpdate(event: React.FormEvent) {
    event.preventDefault();
    if (busy.current || !editingItem) return;
    setEditServerError(undefined);
    setNotice("");
    const { payload, errors } = buildPatchInteractionPayload(editingItem, editDraft);
    if (errors || !payload) {
      setEditErrors(errors ?? {});
      return;
    }
    setEditErrors({});
    busy.current = true;
    setPending(true);
    try {
      await api.patchInteraction(customerId, editingItem.id, payload);
      setEditingItem(null);
      setNotice("Đã cập nhật tương tác.");
      await onMutated();
      await interactionsQuery.refetch();
    } catch (error) {
      setEditServerError(error);
    } finally {
      busy.current = false;
      setPending(false);
    }
  }

  async function handleComplete(item: CustomerInteraction) {
    if (busy.current) return;
    const validation = validateFollowUpResult(followUpResult);
    if (validation) {
      setFollowUpResultError(validation);
      return;
    }
    setFollowUpResultError(null);
    setCompleteServerError(undefined);
    setNotice("");
    busy.current = true;
    setPending(true);
    try {
      await api.completeFollowUp(customerId, item.id, { result: followUpResult.trim() });
      setCompletingId(null);
      setFollowUpResult("");
      setNotice("Đã hoàn thành lịch hẹn chăm sóc.");
      await onMutated();
      await interactionsQuery.refetch();
    } catch (error) {
      setCompleteServerError(error);
    } finally {
      busy.current = false;
      setPending(false);
    }
  }

  return (
    <div className="inventory-stack">
      <div className="inventory-section-header">
        <div>
          <h3>Lịch sử tương tác &amp; hẹn chăm sóc ({totalItems})</h3>
          <p className="hint">
            Mọi mốc thời gian hiển thị theo giờ Việt Nam (Asia/Ho_Chi_Minh). Tương tác chiến dịch được hệ thống ghi tự động và chỉ đọc.
          </p>
        </div>
        {canEdit && !showCreate && (
          <button type="button" className="button" onClick={openCreate}>
            <MessageSquarePlus size={17} aria-hidden="true" />
            <span>Ghi tương tác mới</span>
          </button>
        )}
      </div>

      {notice && <Feedback>{notice}</Feedback>}

      {canEdit && showCreate && (
        <form className="catalog-form inventory-form-card" onSubmit={event => void handleCreate(event)} noValidate>
          <h3>Ghi nhận tương tác mới</h3>
          <FormError error={createServerError} />

          <div className="catalog-fields">
            <div className="product-field">
              <label htmlFor="ix-create-channel">Kênh liên hệ *</label>
              <select
                id="ix-create-channel"
                disabled={pending}
                value={createDraft.channel}
                onChange={e => setCreateDraft(prev => ({ ...prev, channel: e.target.value as InteractionChannel }))}
              >
                {interactionChannels.map(ch => (
                  <option key={ch} value={ch}>{interactionChannelLabels[ch]}</option>
                ))}
              </select>
            </div>

            <div className="product-field">
              <label htmlFor="ix-create-kind">Loại tương tác *</label>
              <select
                id="ix-create-kind"
                disabled={pending}
                value={createDraft.kind}
                onChange={e => setCreateDraft(prev => ({ ...prev, kind: e.target.value as ManualInteractionKind }))}
              >
                {manualInteractionKinds.map(k => (
                  <option key={k} value={k}>{interactionKindLabels[k]}</option>
                ))}
              </select>
            </div>

            <div className="product-field">
              <label htmlFor="ix-create-occurred">Thời điểm tương tác (giờ VN, để trống = hiện tại)</label>
              <input
                id="ix-create-occurred"
                type="datetime-local"
                disabled={pending}
                value={createDraft.occurredAt}
                aria-invalid={Boolean(createErrors.occurredAt)}
                onChange={e => setCreateDraft(prev => ({ ...prev, occurredAt: e.target.value }))}
              />
              {createErrors.occurredAt && <p className="field-error">{createErrors.occurredAt}</p>}
            </div>

            <div className="product-field">
              <label htmlFor="ix-create-followup">Hẹn chăm sóc lại (giờ VN, tùy chọn)</label>
              <input
                id="ix-create-followup"
                type="datetime-local"
                disabled={pending}
                value={createDraft.followUpAt}
                aria-invalid={Boolean(createErrors.followUpAt)}
                onChange={e => setCreateDraft(prev => ({ ...prev, followUpAt: e.target.value }))}
              />
              {createErrors.followUpAt && <p className="field-error">{createErrors.followUpAt}</p>}
            </div>

            <div className="product-field">
              <label htmlFor="ix-create-order">Đơn hàng liên kết (tùy chọn)</label>
              <select
                id="ix-create-order"
                disabled={pending}
                value={createDraft.orderId}
                aria-invalid={Boolean(createErrors.orderId)}
                onChange={e => setCreateDraft(prev => ({ ...prev, orderId: e.target.value }))}
              >
                <option value="">Không gắn đơn hàng</option>
                {selectableOrders.map(ord => (
                  <option key={ord.id} value={ord.id}>
                    {ord.code} ({formatVnDateTime(ord.placedAt)})
                  </option>
                ))}
              </select>
              {createErrors.orderId && <p className="field-error">{createErrors.orderId}</p>}
            </div>
          </div>

          <div className="product-field">
            <label htmlFor="ix-create-content">Nội dung trao đổi *</label>
            <textarea
              id="ix-create-content"
              rows={3}
              maxLength={4000}
              required
              disabled={pending}
              value={createDraft.content}
              aria-invalid={Boolean(createErrors.content)}
              onChange={e => setCreateDraft(prev => ({ ...prev, content: e.target.value }))}
              placeholder="Ghi ngắn gọn nhu cầu khách, mẫu ấm/trà đang quan tâm, cam kết gọi lại…"
            />
            {createErrors.content && <p className="field-error">{createErrors.content}</p>}
          </div>

          <div className="catalog-actions inventory-form-actions">
            <button type="submit" className="button" disabled={pending}>
              {pending ? "Đang lưu…" : "Lưu tương tác"}
            </button>
            <button
              type="button"
              className="button secondary"
              disabled={pending}
              onClick={() => setShowCreate(false)}
            >
              Hủy
            </button>
          </div>
        </form>
      )}

      {interactionsQuery.isPending ? (
        <Pending>Đang tải lịch sử tương tác…</Pending>
      ) : interactionsQuery.isError ? (
        <div>
          <FormError error={interactionsQuery.error} />
          <button type="button" className="button secondary" onClick={() => void interactionsQuery.refetch()}>
            Thử tải lại
          </button>
        </div>
      ) : items.length === 0 ? (
        <div className="empty-state">
          <h3>Chưa có lịch sử tương tác</h3>
          <p>Ghi nhận cuộc gọi, tin nhắn Zalo hoặc hẹn chăm sóc để đội ngũ cùng nắm bối cảnh khách hàng.</p>
        </div>
      ) : (
        <>
          <ol className="customer-timeline" aria-label="Dòng thời gian tương tác khách hàng">
            {items.map(item => {
              const isCampaign = !canEditInteraction(item);
              const isEditing = editingItem?.id === item.id;
              const isCompleting = completingId === item.id;
              const followUpPending = canCompleteFollowUp(item);

              return (
                <li key={item.id} className={`customer-card ${isCampaign ? "is-campaign" : ""}`}>
                  {isEditing ? (
                    <form className="inventory-stack" onSubmit={event => void handleUpdate(event)} noValidate>
                      <h4>Chỉnh sửa tương tác</h4>
                      <FormError error={editServerError} />
                      <div className="catalog-fields">
                        <div className="product-field">
                          <label htmlFor={`ix-edit-channel-${item.id}`}>Kênh liên hệ</label>
                          <select
                            id={`ix-edit-channel-${item.id}`}
                            disabled={pending}
                            value={editDraft.channel}
                            onChange={e => setEditDraft(prev => ({ ...prev, channel: e.target.value as InteractionChannel }))}
                          >
                            {interactionChannels.map(ch => (
                              <option key={ch} value={ch}>{interactionChannelLabels[ch]}</option>
                            ))}
                          </select>
                        </div>

                        <div className="product-field">
                          <label htmlFor={`ix-edit-kind-${item.id}`}>Loại tương tác</label>
                          <select
                            id={`ix-edit-kind-${item.id}`}
                            disabled={pending}
                            value={editDraft.kind}
                            onChange={e => setEditDraft(prev => ({ ...prev, kind: e.target.value as ManualInteractionKind }))}
                          >
                            {manualInteractionKinds.map(k => (
                              <option key={k} value={k}>{interactionKindLabels[k]}</option>
                            ))}
                          </select>
                        </div>

                        <div className="product-field">
                          <label htmlFor={`ix-edit-occurred-${item.id}`}>Thời điểm tương tác (giờ VN)</label>
                          <input
                            id={`ix-edit-occurred-${item.id}`}
                            type="datetime-local"
                            disabled={pending}
                            value={editDraft.occurredAt}
                            aria-invalid={Boolean(editErrors.occurredAt)}
                            onChange={e => setEditDraft(prev => ({ ...prev, occurredAt: e.target.value }))}
                          />
                          {editErrors.occurredAt && <p className="field-error">{editErrors.occurredAt}</p>}
                        </div>

                        <div className="product-field">
                          <label htmlFor={`ix-edit-followup-${item.id}`}>Hẹn chăm sóc lại (giờ VN)</label>
                          <input
                            id={`ix-edit-followup-${item.id}`}
                            type="datetime-local"
                            disabled={pending || item.followUpDoneAt !== null}
                            value={editDraft.followUpAt}
                            aria-invalid={Boolean(editErrors.followUpAt)}
                            onChange={e => setEditDraft(prev => ({ ...prev, followUpAt: e.target.value }))}
                          />
                          {item.followUpDoneAt !== null && (
                            <p className="hint">Lịch hẹn đã hoàn thành — không thể thay đổi thời điểm hẹn.</p>
                          )}
                          {editErrors.followUpAt && <p className="field-error">{editErrors.followUpAt}</p>}
                        </div>

                        <div className="product-field">
                          <label htmlFor={`ix-edit-order-${item.id}`}>Đơn hàng liên kết</label>
                          <select
                            id={`ix-edit-order-${item.id}`}
                            disabled={pending}
                            value={editDraft.orderId}
                            onChange={e => setEditDraft(prev => ({ ...prev, orderId: e.target.value }))}
                          >
                            <option value="">Không gắn đơn hàng</option>
                            {item.orderId && !selectableOrders.some(o => o.id === item.orderId) && (
                              <option value={item.orderId}>{item.orderCode ?? item.orderId}</option>
                            )}
                            {selectableOrders.map(ord => (
                              <option key={ord.id} value={ord.id}>
                                {ord.code} ({formatVnDateTime(ord.placedAt)})
                              </option>
                            ))}
                          </select>
                        </div>
                      </div>

                      <div className="product-field">
                        <label htmlFor={`ix-edit-content-${item.id}`}>Nội dung trao đổi *</label>
                        <textarea
                          id={`ix-edit-content-${item.id}`}
                          rows={3}
                          maxLength={4000}
                          required
                          disabled={pending}
                          value={editDraft.content}
                          aria-invalid={Boolean(editErrors.content)}
                          onChange={e => setEditDraft(prev => ({ ...prev, content: e.target.value }))}
                        />
                        {editErrors.content && <p className="field-error">{editErrors.content}</p>}
                      </div>

                      <div className="catalog-actions inventory-form-actions">
                        <button type="submit" className="button" disabled={pending}>
                          {pending ? "Đang lưu…" : "Lưu thay đổi"}
                        </button>
                        <button
                          type="button"
                          className="button secondary"
                          disabled={pending}
                          onClick={() => setEditingItem(null)}
                        >
                          Hủy
                        </button>
                      </div>
                    </form>
                  ) : (
                    <>
                      <div className="customer-card-header">
                        <div className="inventory-badges">
                          <span className={`inventory-badge ${isCampaign ? "is-warning" : "is-ok"}`}>
                            {interactionKindLabels[item.kind] ?? item.kind}
                          </span>
                          <span className="inventory-badge is-muted">
                            {interactionChannelLabels[item.channel] ?? item.channel}
                          </span>
                          {item.orderCode && (
                            <span className="inventory-badge is-muted">Đơn: {item.orderCode}</span>
                          )}
                        </div>
                        <span className="customer-timestamp">
                          {formatVnDateTime(item.occurredAt)}
                          {item.staffDisplayName ? ` · ${item.staffDisplayName}` : ""}
                        </span>
                      </div>

                      <p className="customer-multiline">{item.content}</p>

                      {item.followUpAt && (
                        <div className={`customer-followup-box ${item.followUpDoneAt ? "is-done" : "is-pending"}`}>
                          <div className="inventory-badges">
                            <span className={`inventory-badge ${item.followUpDoneAt ? "is-ok" : "is-warning"}`}>
                              {item.followUpDoneAt ? "Đã hoàn thành hẹn" : "Đang hẹn chăm sóc"}
                            </span>
                            <span>Hẹn lúc: <strong>{formatVnDateTime(item.followUpAt)}</strong></span>
                          </div>
                          {item.followUpDoneAt && (
                            <p className="hint">
                              Hoàn thành lúc {formatVnDateTime(item.followUpDoneAt)}
                              {item.followUpResult ? ` — Kết quả: ${item.followUpResult}` : ""}
                            </p>
                          )}
                        </div>
                      )}

                      {isCampaign && (
                        <p className="customer-readonly-pill">
                          <Lock size={14} aria-hidden="true" />
                          <span>Tương tác chiến dịch tự động — không chỉnh sửa nội dung</span>
                        </p>
                      )}

                      {isCompleting ? (
                        <div className="customer-inline-form">
                          <FormError error={completeServerError} />
                          <div className="product-field">
                            <label htmlFor={`ix-complete-result-${item.id}`}>Kết quả chăm sóc *</label>
                            <textarea
                              id={`ix-complete-result-${item.id}`}
                              rows={2}
                              maxLength={4000}
                              disabled={pending}
                              value={followUpResult}
                              aria-invalid={Boolean(followUpResultError)}
                              onChange={e => setFollowUpResult(e.target.value)}
                              placeholder="Nhập kết quả trao đổi với khách…"
                            />
                            {followUpResultError && <p className="field-error">{followUpResultError}</p>}
                          </div>
                          <div className="catalog-actions inventory-form-actions">
                            <button
                              type="button"
                              className="button"
                              disabled={pending}
                              onClick={() => void handleComplete(item)}
                            >
                              <CheckCircle2 size={16} aria-hidden="true" />
                              <span>{pending ? "Đang lưu…" : "Xác nhận hoàn thành"}</span>
                            </button>
                            <button
                              type="button"
                              className="button secondary"
                              disabled={pending}
                              onClick={() => setCompletingId(null)}
                            >
                              Hủy
                            </button>
                          </div>
                        </div>
                      ) : (
                        canEdit && (
                          <div className="catalog-actions inventory-form-actions">
                            {!isCampaign && (
                              <button
                                type="button"
                                className="button secondary"
                                onClick={() => openEdit(item)}
                              >
                                <Pencil size={15} aria-hidden="true" />
                                <span>Chỉnh sửa</span>
                              </button>
                            )}
                            {followUpPending && (
                              <button
                                type="button"
                                className="button"
                                onClick={() => openComplete(item)}
                              >
                                <CheckCircle2 size={16} aria-hidden="true" />
                                <span>Hoàn thành hẹn</span>
                              </button>
                            )}
                          </div>
                        )
                      )}
                    </>
                  )}
                </li>
              );
            })}
          </ol>

          {totalPages > 1 && (
            <div className="pagination">
              <p role="status">
                Trang {page} / {totalPages} · {totalItems} tương tác
              </p>
              <label>
                Số mục
                <select
                  aria-label="Số tương tác mỗi trang"
                  value={pageSize}
                  onChange={e => {
                    setPageSize(Number(e.target.value));
                    setPage(1);
                  }}
                >
                  {[10, 20, 50, 100].map(size => (
                    <option key={size} value={size}>{size}</option>
                  ))}
                </select>
              </label>
              <div>
                <button
                  type="button"
                  className="button secondary"
                  disabled={page <= 1}
                  onClick={() => setPage(prev => Math.max(1, prev - 1))}
                >
                  Trang trước
                </button>
                <button
                  type="button"
                  className="button secondary"
                  disabled={page >= totalPages}
                  onClick={() => setPage(prev => Math.min(totalPages, prev + 1))}
                >
                  Trang sau
                </button>
              </div>
            </div>
          )}
        </>
      )}
    </div>
  );
}
