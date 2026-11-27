"use client";

import { useMemo, useRef, useState } from "react";
import Link from "next/link";
import { useQuery } from "@tanstack/react-query";
import { CalendarClock, CheckCircle2, ClockAlert } from "lucide-react";
import {
  CustomersApi,
  customersRootKey,
  followUpsKey,
  type FollowUpItem
} from "../../lib/customers-api";
import {
  followUpDueLabels,
  formatVnDateTime,
  interactionChannelLabels,
  interactionKindLabels,
  validateFollowUpResult,
  type FollowUpDueMode
} from "../../lib/customers-forms";
import { useAdmin } from "../providers";
import { Feedback, FormError, Pending } from "../ui";
import { ContactStatusBadge, CustomerGroupBadge, QuickContactStrip } from "./customer-badges";

const dueModes: readonly FollowUpDueMode[] = ["today", "overdue", "week"];

export function FollowUpsPanel({ canWrite }: { canWrite: boolean }) {
  const { api: admin, queries } = useAdmin();
  const api = useMemo(() => new CustomersApi(admin), [admin]);

  const [due, setDue] = useState<FollowUpDueMode>("today");
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(20);

  const [completingId, setCompletingId] = useState<string | null>(null);
  const [resultText, setResultText] = useState("");
  const [resultError, setResultError] = useState<string | null>(null);
  const [submitError, setSubmitError] = useState<unknown>();
  const [submitPending, setSubmitPending] = useState(false);
  const [notice, setNotice] = useState("");
  const busy = useRef(false);

  const followUpsQuery = useQuery({
    queryKey: [...followUpsKey, { due, page, pageSize }],
    queryFn: ({ signal }) => api.listFollowUps({ due, page, pageSize }, signal)
  });

  const totalItems = Number(followUpsQuery.data?.totalItems ?? 0);
  const totalPages = Math.max(1, Math.ceil(totalItems / pageSize));
  const items = followUpsQuery.data?.items ?? [];

  function openComplete(item: FollowUpItem) {
    setCompletingId(item.interactionId);
    setResultText("");
    setResultError(null);
    setSubmitError(undefined);
    setNotice("");
  }

  function closeComplete() {
    setCompletingId(null);
    setResultText("");
    setResultError(null);
    setSubmitError(undefined);
  }

  async function handleComplete(item: FollowUpItem) {
    if (busy.current) return;
    const validation = validateFollowUpResult(resultText);
    if (validation) {
      setResultError(validation);
      return;
    }
    setResultError(null);
    setSubmitError(undefined);
    busy.current = true;
    setSubmitPending(true);
    try {
      await api.completeFollowUp(item.customerId, item.interactionId, { result: resultText.trim() });
      setCompletingId(null);
      setResultText("");
      setNotice(`Đã hoàn thành hẹn chăm sóc cho khách ${item.customerFullName} (${item.customerCode}).`);
      await queries.invalidateQueries({ queryKey: customersRootKey });
    } catch (error) {
      setSubmitError(error);
    } finally {
      busy.current = false;
      setSubmitPending(false);
    }
  }

  return (
    <section className="inventory-panel" aria-labelledby="followups-heading">
      <div className="inventory-section-header">
        <div>
          <h2 id="followups-heading">Hàng đợi hẹn chăm sóc</h2>
          <p className="hint">
            Theo múi giờ Việt Nam (UTC+7), ưu tiên các lịch hẹn quá hạn hiển thị trước.
          </p>
        </div>
      </div>

      <div className="customer-segmented" role="group" aria-label="Lọc hạn chăm sóc">
        {dueModes.map(mode => (
          <button
            key={mode}
            type="button"
            className={`customer-segment-button ${due === mode ? "is-active" : ""}`}
            aria-pressed={due === mode}
            onClick={() => {
              setDue(mode);
              setPage(1);
              closeComplete();
            }}
          >
            {followUpDueLabels[mode]}
          </button>
        ))}
      </div>

      {notice && <Feedback>{notice}</Feedback>}

      {followUpsQuery.isPending ? (
        <Pending>Đang tải danh sách hẹn chăm sóc…</Pending>
      ) : followUpsQuery.isError ? (
        <div>
          <FormError error={followUpsQuery.error} />
          <button type="button" className="button secondary" onClick={() => void followUpsQuery.refetch()}>
            Thử tải lại
          </button>
        </div>
      ) : items.length === 0 ? (
        <div className="empty-state">
          <CalendarClock size={34} strokeWidth={1.4} aria-hidden="true" />
          <h3>Không có lịch hẹn chăm sóc nào</h3>
          <p>Không có hẹn chăm sóc đang mở trong khung thời gian &ldquo;{followUpDueLabels[due]}&rdquo;.</p>
        </div>
      ) : (
        <>
          <ul className="customer-card-list" aria-label="Danh sách hẹn chăm sóc">
            {items.map(item => {
              const isCompleting = completingId === item.interactionId;
              return (
                <li
                  key={item.interactionId}
                  className={`customer-card ${item.isOverdue ? "is-overdue" : ""}`}
                >
                  <div className="customer-card-header">
                    <div className="customer-identity-cell">
                      <div className="customer-identity-head">
                        <Link className="customer-name-link" href={`/customers/${item.customerId}`}>
                          {item.customerFullName}
                        </Link>
                        <code className="customer-code">{item.customerCode}</code>
                      </div>
                      <div className="inventory-badges">
                        {item.isOverdue ? (
                          <span className="inventory-badge is-danger">
                            <ClockAlert size={14} aria-hidden="true" />
                            <span>Quá hạn</span>
                          </span>
                        ) : (
                          <span className="inventory-badge is-warning">Đến hạn</span>
                        )}
                        <CustomerGroupBadge group={item.customerGroup} isVip={item.customerIsVip} />
                        {item.customerContactStatus === "do_not_contact" && (
                          <ContactStatusBadge status={item.customerContactStatus} />
                        )}
                      </div>
                    </div>

                    <div className="customer-followup-time">
                      <span>Hẹn lúc (giờ VN)</span>
                      <strong>{formatVnDateTime(item.followUpAt)}</strong>
                    </div>
                  </div>

                  {item.customerContactStatus === "do_not_contact" && (
                    <p className="customer-dnc-note" role="note">
                      Khách đang bật trạng thái Không liên hệ (DNC) — chỉ liên hệ khi phục vụ yêu cầu dịch vụ/đơn hàng, không gửi tin tiếp thị.
                    </p>
                  )}

                  <QuickContactStrip
                    phoneE164={item.customerPhoneE164}
                    zaloName={item.customerZaloName}
                  />

                  <div className="customer-interaction-body">
                    <div className="inventory-badges">
                      <span className="inventory-badge is-muted">
                        {interactionChannelLabels[item.channel] ?? item.channel}
                      </span>
                      <span className="inventory-badge is-muted">
                        {interactionKindLabels[item.kind] ?? item.kind}
                      </span>
                      {item.orderCode && (
                        <span className="inventory-badge is-ok">Đơn: {item.orderCode}</span>
                      )}
                    </div>
                    <p className="customer-multiline">{item.content}</p>
                    <p className="hint">
                      Ghi nhận lúc {formatVnDateTime(item.occurredAt)}
                      {item.staffDisplayName ? ` bởi ${item.staffDisplayName}` : ""}
                    </p>
                  </div>

                  {isCompleting ? (
                    <div className="customer-inline-form">
                      <FormError error={submitError} />
                      <div className="product-field">
                        <label htmlFor={`complete-followup-${item.interactionId}`}>
                          Kết quả chăm sóc *
                        </label>
                        <textarea
                          id={`complete-followup-${item.interactionId}`}
                          rows={2}
                          maxLength={4000}
                          disabled={submitPending}
                          value={resultText}
                          aria-invalid={Boolean(resultError)}
                          onChange={e => setResultText(e.target.value)}
                          placeholder="Ví dụ: Đã gọi Zalo, khách hẹn cuối tuần ghé xem ấm mới…"
                        />
                        {resultError && <p className="field-error">{resultError}</p>}
                      </div>
                      <div className="catalog-actions inventory-form-actions">
                        <button
                          type="button"
                          className="button"
                          disabled={submitPending}
                          onClick={() => void handleComplete(item)}
                        >
                          <CheckCircle2 size={16} aria-hidden="true" />
                          <span>{submitPending ? "Đang lưu…" : "Xác nhận hoàn thành"}</span>
                        </button>
                        <button
                          type="button"
                          className="button secondary"
                          disabled={submitPending}
                          onClick={closeComplete}
                        >
                          Hủy
                        </button>
                      </div>
                    </div>
                  ) : (
                    <div className="catalog-actions inventory-form-actions">
                      <Link className="button secondary" href={`/customers/${item.customerId}`}>
                        Mở hồ sơ 360°
                      </Link>
                      {canWrite && (
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
                  )}
                </li>
              );
            })}
          </ul>

          <div className="pagination">
            <p role="status">
              Trang {page} / {totalPages} · {totalItems} lịch hẹn
            </p>
            <label>
              Số mục
              <select
                aria-label="Số mục mỗi trang"
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
        </>
      )}
    </section>
  );
}
