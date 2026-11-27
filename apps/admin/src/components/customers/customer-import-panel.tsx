"use client";

import { useMemo, useRef, useState } from "react";
import Link from "next/link";
import { AlertTriangle, FileSpreadsheet } from "lucide-react";
import { CustomersApi, customersRootKey, type CustomerImportRow } from "../../lib/customers-api";
import {
  canApplyCustomerImport,
  countImportWarnings,
  customerImportApplyResult,
  customerImportDryRunSuccess,
  customerImportInitialState,
  customerImportSelectFile,
  formatInt,
  formatPhoneDisplay,
  importReasonLabels,
  importRowStatusLabels,
  importWarningLabels,
  isUuid,
  type CustomerImportState,
  type ImportRowFilter
} from "../../lib/customers-forms";
import { useProductNavigationGuard } from "../products/product-navigation";
import { useAdmin } from "../providers";
import { Confirmation, Feedback, FormError } from "../ui";

export function CustomerImportPanel() {
  const { api: admin, queries } = useAdmin();
  const api = useMemo(() => new CustomersApi(admin), [admin]);

  const [state, setState] = useState<CustomerImportState>(customerImportInitialState);
  const [rowFilter, setRowFilter] = useState<ImportRowFilter>("all");
  const [pending, setPending] = useState<"dryRun" | "apply" | null>(null);
  const busy = useRef(false);

  const canApply = canApplyCustomerImport(state);
  useProductNavigationGuard(Boolean(state.previewed && !state.applied), pending !== null);

  const warningStats = useMemo(() => countImportWarnings(state.result), [state.result]);

  const filteredRows = useMemo<CustomerImportRow[]>(() => {
    const rows = state.result?.rows ?? [];
    switch (rowFilter) {
      case "ready":
        return rows.filter(r => r.status === "would_import" || r.status === "imported");
      case "skipped":
        return rows.filter(r => r.status === "skipped");
      case "error":
        return rows.filter(r => r.status === "error");
      case "warnings":
        return rows.filter(r => r.warnings.length > 0);
      default:
        return rows;
    }
  }, [state.result, rowFilter]);

  async function runDryRun() {
    if (busy.current || !state.file) return;
    busy.current = true;
    setPending("dryRun");
    setState(prev => ({ ...prev, error: undefined }));
    try {
      const result = await api.importCustomers(state.file, true);
      setState(prev => customerImportDryRunSuccess(prev, result));
      setRowFilter("all");
    } catch (error) {
      setState(prev => ({ ...prev, previewed: false, error }));
    } finally {
      busy.current = false;
      setPending(null);
    }
  }

  async function runApply() {
    if (busy.current || !state.file || !canApply) return;
    busy.current = true;
    setPending("apply");
    setState(prev => ({ ...prev, error: undefined }));
    try {
      const result = await api.importCustomers(state.file, false);
      setState(prev => customerImportApplyResult(prev, result));
      if (result.summary.committed) {
        await queries.invalidateQueries({ queryKey: customersRootKey });
      }
    } catch (error) {
      setState(prev => ({ ...prev, previewed: false, error }));
      throw error;
    } finally {
      busy.current = false;
      setPending(null);
    }
  }

  const summary = state.result?.summary;

  return (
    <section className="inventory-panel" aria-labelledby="customer-import-heading">
      <div className="inventory-section-header">
        <div>
          <h2 id="customer-import-heading">Import khách hàng cũ</h2>
          <p className="hint">
            Hỗ trợ tệp CSV UTF-8 hoặc XLSX (sheet duy nhất hoặc tên <code>KhachHang</code>), tối đa 5 MiB và 2.000 dòng.
            Cột cho phép: <code>full_name*</code>, <code>phone</code>, <code>zalo_name</code>, <code>customer_group</code>,{" "}
            <code>tags</code> (ngăn cách bằng dấu phẩy hoặc chấm phẩy), <code>note</code>, <code>had_purchase</code> (mặc định true).
            Trùng SĐT sẽ được bỏ qua, không ghi đè hồ sơ có sẵn.
          </p>
        </div>
      </div>

      <div className="catalog-form inventory-form-card">
        <div className="product-field">
          <label htmlFor="customer-import-file">Chọn tệp khách hàng cũ (.csv, .xlsx)</label>
          <input
            id="customer-import-file"
            type="file"
            accept=".csv,.xlsx,text/csv,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
            disabled={pending !== null}
            onChange={event => {
              const selected = event.target.files?.[0] ?? null;
              setState(customerImportSelectFile(selected));
              setRowFilter("all");
            }}
          />
          <p className="hint">
            {state.file
              ? `Đã chọn tệp: ${state.file.name} (${formatInt(state.file.size)} byte). Đổi tệp sẽ yêu cầu chạy thử lại trước khi nhập.`
              : "Luôn chạy thử (Dry-run) để kiểm tra lỗi, nhãn mới và cảnh báo trùng Zalo trước khi bấm Nhập dữ liệu."}
          </p>
          {state.fileError && <p className="field-error" role="alert">{state.fileError}</p>}
        </div>

        <FormError error={state.error} />

        {summary && !summary.dryRun && summary.committed && (
          <Feedback>
            Đã nhập thành công {formatInt(summary.importedCount)} khách hàng mới, bỏ qua {formatInt(summary.skippedCount)} dòng trùng,
            tạo {formatInt(summary.createdTagsCount)} nhãn mới và {formatInt(summary.createdInteractionsCount)} ghi chú tương tác.
          </Feedback>
        )}

        {summary && !summary.dryRun && !summary.committed && (
          <div className="alert" role="alert">
            <div>
              <p>
                Chưa thể ghi dữ liệu do còn {formatInt(summary.errorCount)} dòng lỗi (không có hồ sơ nào được tạo).
                Vui lòng sửa tệp theo bảng lỗi bên dưới và chạy thử lại.
              </p>
            </div>
          </div>
        )}

        {summary && summary.dryRun && Number(summary.errorCount) === 0 && Number(summary.readyCount) === 0 && (
          <div className="catalog-warning" role="status">
            Tất cả {formatInt(summary.totalRows)} dòng đều bị bỏ qua do trùng SĐT — không có khách hàng mới nào để nhập.
          </div>
        )}

        <div className="catalog-actions inventory-form-actions">
          <button
            type="button"
            className="button secondary"
            disabled={!state.file || pending !== null}
            onClick={() => void runDryRun()}
          >
            <FileSpreadsheet size={17} aria-hidden="true" />
            <span>{pending === "dryRun" ? "Đang chạy thử…" : "Chạy thử (Dry-run)"}</span>
          </button>

          <Confirmation
            title="Xác nhận nhập danh sách khách hàng cũ?"
            confirmLabel="Nhập dữ liệu"
            pending={pending === "apply"}
            trigger={
              <button type="button" className="button" disabled={!canApply || pending !== null}>
                Nhập dữ liệu
              </button>
            }
            onConfirm={async () => {
              try {
                await runApply();
              } catch {
                // Error already stored in state for main form display.
              }
            }}
          >
            Hệ thống sẽ kiểm tra lại và tạo mới khoảng{" "}
            <strong>{formatInt(summary?.readyCount ?? 0)}</strong> hồ sơ khách hàng từ tệp{" "}
            <strong>{state.file?.name}</strong>
            {summary && summary.newTags.length > 0
              ? ` cùng ${formatInt(summary.newTags.length)} nhãn mới (${summary.newTags.join(", ")})`
              : ""}
            . Các dòng trùng SĐT sẽ được bỏ qua, không ghi đè dữ liệu cũ.
          </Confirmation>
        </div>
      </div>

      {state.result && summary && (
        <div className="inventory-result-section">
          <div className="inventory-summary-grid">
            <div className="inventory-stat-card">
              <span>Tổng số dòng</span>
              <strong>{formatInt(summary.totalRows)}</strong>
            </div>
            <div className="inventory-stat-card">
              <span>{summary.dryRun ? "Sẽ nhập (would_import)" : "Đã nhập"}</span>
              <strong>{formatInt(summary.dryRun ? summary.readyCount : summary.importedCount)}</strong>
            </div>
            <div className="inventory-stat-card">
              <span>Bỏ qua (trùng SĐT)</span>
              <strong>{formatInt(summary.skippedCount)}</strong>
              <small className="hint">
                Trùng file: {formatInt(summary.duplicateInFileCount)} · Đã có hệ thống: {formatInt(summary.existingPhoneSkippedCount)}
              </small>
            </div>
            <div className={`inventory-stat-card ${Number(summary.errorCount) > 0 ? "is-danger" : ""}`}>
              <span>Dòng lỗi</span>
              <strong>{formatInt(summary.errorCount)}</strong>
            </div>
            <div className="inventory-stat-card">
              <span>Trạng thái</span>
              <strong>
                {summary.committed
                  ? "Đã ghi vào hệ thống"
                  : summary.dryRun
                    ? "Kết quả chạy thử"
                    : "Chưa ghi (committed = false)"}
              </strong>
            </div>
          </div>

          {summary.newTags.length > 0 && (
            <div className="customer-info-banner" role="status">
              <strong>
                {summary.committed ? "Nhãn mới đã tạo tự động:" : "Nhãn mới sẽ được tạo tự động:"}
              </strong>{" "}
              <div className="customer-tag-list">
                {summary.newTags.map(tagName => (
                  <span key={tagName} className="customer-tag-chip">{tagName}</span>
                ))}
              </div>
            </div>
          )}

          {(warningStats.zaloOnlyCount > 0 || warningStats.possibleDuplicateZaloCount > 0) && (
            <div className="catalog-warning customer-warning-banner" role="status">
              <AlertTriangle size={19} aria-hidden="true" />
              <div>
                <strong>Cảnh báo khách chỉ có tên Zalo (không tự gộp/bỏ qua):</strong>
                <ul>
                  {warningStats.zaloOnlyCount > 0 && (
                    <li>
                      <strong>{formatInt(warningStats.zaloOnlyCount)} dòng</strong> có cảnh báo{" "}
                      <code>zalo_only_reimport_risk</code> — {importWarningLabels.zalo_only_reimport_risk}
                    </li>
                  )}
                  {warningStats.possibleDuplicateZaloCount > 0 && (
                    <li>
                      <strong>{formatInt(warningStats.possibleDuplicateZaloCount)} dòng</strong> có cảnh báo{" "}
                      <code>possible_duplicate_zalo_customer</code> — {importWarningLabels.possible_duplicate_zalo_customer}
                    </li>
                  )}
                </ul>
              </div>
            </div>
          )}

          <div className="customer-segmented" role="group" aria-label="Lọc dòng kết quả import">
            {(
              [
                ["all", `Tất cả (${state.result.rows.length})`],
                ["ready", `${summary.dryRun ? "Sẽ nhập" : "Đã nhập"} (${formatInt(summary.dryRun ? summary.readyCount : summary.importedCount)})`],
                ["skipped", `Bỏ qua (${formatInt(summary.skippedCount)})`],
                ["error", `Lỗi (${formatInt(summary.errorCount)})`],
                ["warnings", `Cảnh báo Zalo (${formatInt(warningStats.zaloOnlyCount)})`]
              ] as const
            ).map(([key, label]) => (
              <button
                key={key}
                type="button"
                className={`customer-segment-button ${rowFilter === key ? "is-active" : ""}`}
                aria-pressed={rowFilter === key}
                onClick={() => setRowFilter(key)}
              >
                {label}
              </button>
            ))}
          </div>

          <div className="table-scroll" role="region" aria-label="Chi tiết từng dòng import khách hàng" tabIndex={0}>
            <table>
              <caption>
                Chi tiết từng dòng ({filteredRows.length} / {state.result.rows.length} dòng)
              </caption>
              <thead>
                <tr>
                  <th scope="col">Dòng</th>
                  <th scope="col">Trạng thái</th>
                  <th scope="col">SĐT chuẩn hóa</th>
                  <th scope="col">Hồ sơ / Lý do</th>
                  <th scope="col">Nhãn &amp; Ghi chú</th>
                  <th scope="col">Cảnh báo &amp; Lỗi cột</th>
                </tr>
              </thead>
              <tbody>
                {filteredRows.length === 0 ? (
                  <tr>
                    <td colSpan={6} className="table-empty">Không có dòng nào phù hợp bộ lọc.</td>
                  </tr>
                ) : (
                  filteredRows.map(row => {
                    const badgeClass =
                      row.status === "error"
                        ? "is-danger"
                        : row.status === "skipped"
                          ? "is-warning"
                          : "is-ok";
                    const linkedCustomerId =
                      row.customerId && isUuid(row.customerId)
                        ? row.customerId
                        : row.existingCustomerId && isUuid(row.existingCustomerId)
                          ? row.existingCustomerId
                          : null;

                    return (
                      <tr key={String(row.row)} className={row.status === "error" ? "inventory-row-error" : undefined}>
                        <td>{row.row}</td>
                        <td>
                          <span className={`inventory-badge ${badgeClass}`}>
                            {importRowStatusLabels[row.status] ?? row.status}
                          </span>
                        </td>
                        <td>{formatPhoneDisplay(row.normalizedPhone)}</td>
                        <td>
                          <div className="customer-import-reason">
                            <span>{importReasonLabels[row.reasonCode] ?? row.reasonCode}</span>
                            {row.customerCode && <code className="customer-code">{row.customerCode}</code>}
                            {row.duplicateOfRow !== null && row.duplicateOfRow !== undefined && (
                              <small className="hint">Trùng với dòng {row.duplicateOfRow}</small>
                            )}
                            {linkedCustomerId && (
                              <Link className="product-link-button" href={`/customers/${linkedCustomerId}`}>
                                {row.customerId ? "Mở hồ sơ mới" : "Mở hồ sơ có sẵn"}
                              </Link>
                            )}
                          </div>
                        </td>
                        <td>
                          <div className="customer-tag-list">
                            {row.tags.map(t => (
                              <span key={t} className="customer-tag-chip">{t}</span>
                            ))}
                            {row.createNoteInteraction && (
                              <span className="inventory-badge is-muted">+ Ghi chú</span>
                            )}
                            {row.tags.length === 0 && !row.createNoteInteraction && "—"}
                          </div>
                        </td>
                        <td>
                          {row.warnings.length === 0 && row.errors.length === 0 ? (
                            "—"
                          ) : (
                            <div className="inventory-stack">
                              {row.warnings.length > 0 && (
                                <ul className="customer-warning-list">
                                  {row.warnings.map(code => (
                                    <li key={code}>
                                      <strong>{code}:</strong> {importWarningLabels[code] ?? code}
                                    </li>
                                  ))}
                                </ul>
                              )}
                              {row.errors.length > 0 && (
                                <ul className="inventory-cell-errors">
                                  {row.errors.map((err, idx) => (
                                    <li key={`${err.column}-${err.code}-${idx}`}>
                                      <strong>{err.column}:</strong> {err.message} ({err.code})
                                    </li>
                                  ))}
                                </ul>
                              )}
                            </div>
                          )}
                        </td>
                      </tr>
                    );
                  })
                )}
              </tbody>
            </table>
          </div>
        </div>
      )}
    </section>
  );
}
