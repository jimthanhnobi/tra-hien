"use client";

import { useMemo, useRef, useState } from "react";
import { InventoryApi } from "../../lib/inventory-api";
import {
  canApplyOpeningImport,
  formatInt,
  formatVnDateTime,
  openingApplyError,
  openingApplyResult,
  openingDryRunSuccess,
  openingImportInitialState,
  openingSelectFile,
  type OpeningImportState
} from "../../lib/inventory-forms";
import { useProductNavigationGuard } from "../products/product-navigation";
import { useAdmin } from "../providers";
import { Confirmation, Feedback, FormError } from "../ui";

export function OpeningImportPanel() {
  const { api: admin, queries } = useAdmin();
  const api = useMemo(() => new InventoryApi(admin), [admin]);
  const [state, setState] = useState<OpeningImportState>(openingImportInitialState);
  const [pending, setPending] = useState<"dryRun" | "apply" | null>(null);
  const busy = useRef(false);

  const canApply = canApplyOpeningImport(state);
  useProductNavigationGuard(Boolean(state.previewToken && !state.applied), pending !== null);

  async function runDryRun() {
    if (busy.current || !state.file) return;
    busy.current = true;
    setPending("dryRun");
    setState(prev => ({ ...prev, error: undefined }));
    try {
      const result = await api.openingImport(state.file, { dryRun: true });
      setState(prev => openingDryRunSuccess(prev, result));
    } catch (error) {
      setState(prev => ({ ...prev, previewToken: null, previewExpiresAt: null, error }));
    } finally {
      busy.current = false;
      setPending(null);
    }
  }

  async function runApply() {
    if (busy.current || !state.file || !state.previewToken) return;
    busy.current = true;
    setPending("apply");
    setState(prev => ({ ...prev, error: undefined }));
    try {
      const result = await api.openingImport(state.file, { dryRun: false, previewToken: state.previewToken });
      setState(prev => openingApplyResult(prev, result));
      if (result.committed) {
        void queries.invalidateQueries({ queryKey: ["inventory"] });
      }
    } catch (error) {
      setState(prev => openingApplyError(prev, error));
      throw error;
    } finally {
      busy.current = false;
      setPending(null);
    }
  }

  return (
    <section className="inventory-panel" aria-labelledby="opening-import-heading">
      <div className="inventory-section-header">
        <div>
          <h2 id="opening-import-heading">Nhập tồn đầu kỳ</h2>
          <p className="hint">
            Tải tệp CSV hoặc XLSX gồm đúng 3 cột bắt buộc: <code>sku</code>, <code>quantity</code>, <code>unit_cost_vnd</code>.
            Giới hạn tối đa 5 MiB và 500 dòng. Chỉ áp dụng cho biến thể chưa phát sinh giao dịch kho.
          </p>
        </div>
      </div>

      <div className="catalog-form inventory-form-card">
        <div className="product-field">
          <label htmlFor="opening-import-file">Tệp tồn đầu kỳ (.csv, .xlsx)</label>
          <input
            id="opening-import-file"
            type="file"
            accept=".csv,.xlsx,text/csv,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
            disabled={pending !== null}
            onChange={event => {
              const selected = event.target.files?.[0] ?? null;
              setState(openingSelectFile(selected));
            }}
          />
          <p className="hint">
            {state.file
              ? `Đã chọn tệp: ${state.file.name} (${formatInt(state.file.size)} byte). Đổi tệp sẽ tự động hủy mã xem trước cũ.`
              : "Chọn tệp để chạy thử (Dry-run) trước khi áp dụng."}
          </p>
        </div>

        <FormError error={state.error} />

        {state.applied && state.result && (
          <Feedback>
            Đã áp dụng nhập tồn đầu kỳ thành công cho {formatInt(state.result.validRows)} dòng biến thể.
          </Feedback>
        )}

        {state.result && !state.result.dryRun && !state.result.committed && (
          <div className="alert" role="alert">
            <div>
              <p>
                Chưa thể ghi tồn đầu kỳ do dữ liệu kho thay đổi sau bước chạy thử ({formatInt(state.result.invalidRows)} dòng lỗi).
                Vui lòng kiểm tra bảng lỗi bên dưới, chỉnh tệp và chạy thử lại.
              </p>
            </div>
          </div>
        )}

        <div className="catalog-actions">
          <button
            type="button"
            className="button secondary"
            disabled={!state.file || pending !== null}
            onClick={() => void runDryRun()}
          >
            {pending === "dryRun" ? "Đang chạy thử…" : "Chạy thử (Dry-run)"}
          </button>

          <Confirmation
            title="Xác nhận áp dụng nhập tồn đầu kỳ?"
            confirmLabel="Áp dụng nhập tồn đầu kỳ"
            pending={pending === "apply"}
            trigger={
              <button type="button" className="button" disabled={!canApply || pending !== null}>
                Áp dụng nhập tồn đầu kỳ
              </button>
            }
            onConfirm={async () => {
              try {
                await runApply();
              } catch {
                // Error already stored in state for main form display; close confirmation dialog.
              }
            }}
          >
            Hệ thống sẽ ghi tồn đầu kỳ cho toàn bộ {formatInt(state.result?.validRows ?? 0)} dòng hợp lệ từ tệp{" "}
            <strong>{state.file?.name}</strong>. Mã xem trước chỉ có hiệu lực trong 30 phút và gắn với đúng tệp vừa chạy thử.
          </Confirmation>
        </div>

        {state.previewToken && state.previewExpiresAt && (
          <p className="inventory-preview-ready" role="status">
            Kiểm tra hợp lệ {formatInt(state.result?.validRows ?? 0)} dòng. Mã xem trước có hiệu lực đến{" "}
            {formatVnDateTime(new Date(state.previewExpiresAt).toISOString())} (tối đa 30 phút).
          </p>
        )}
      </div>

      {state.result && (
        <div className="inventory-result-section">
          <div className="inventory-summary-grid">
            <div className="inventory-stat-card">
              <span>Dòng hợp lệ</span>
              <strong>{formatInt(state.result.validRows)}</strong>
            </div>
            <div className={`inventory-stat-card ${Number(state.result.invalidRows) > 0 ? "is-danger" : ""}`}>
              <span>Dòng lỗi</span>
              <strong>{formatInt(state.result.invalidRows)}</strong>
            </div>
            <div className="inventory-stat-card">
              <span>Trạng thái</span>
              <strong>
                {state.result.committed ? "Đã ghi vào sổ kho" : state.result.dryRun ? "Kết quả chạy thử" : "Chưa ghi (có lỗi)"}
              </strong>
            </div>
          </div>

          <div className="table-scroll" role="region" aria-label="Chi tiết từng dòng nhập tồn đầu kỳ" tabIndex={0}>
            <table>
              <caption>Chi tiết kiểm tra từng dòng ({state.result.rows.length} dòng)</caption>
              <thead>
                <tr>
                  <th scope="col">Dòng</th>
                  <th scope="col">SKU</th>
                  <th scope="col">Kết quả</th>
                  <th scope="col">Lỗi theo cột</th>
                </tr>
              </thead>
              <tbody>
                {state.result.rows.length === 0 ? (
                  <tr><td colSpan={4} className="table-empty">Tệp không có dòng dữ liệu.</td></tr>
                ) : (
                  state.result.rows.map(row => (
                    <tr key={`${row.row}-${row.sku ?? ""}`} className={row.errors.length > 0 ? "inventory-row-error" : undefined}>
                      <td>{row.row}</td>
                      <td>{row.sku || "—"}</td>
                      <td>
                        <span className={`inventory-badge ${row.errors.length > 0 ? "is-danger" : "is-ok"}`}>
                          {row.errors.length > 0 ? "Lỗi" : "Hợp lệ"}
                        </span>
                      </td>
                      <td>
                        {row.errors.length === 0 ? "—" : (
                          <ul className="inventory-cell-errors">
                            {row.errors.map((err, idx) => (
                              <li key={`${err.column}-${err.code}-${idx}`}>
                                <strong>{err.column}:</strong> {err.message} ({err.code})
                              </li>
                            ))}
                          </ul>
                        )}
                      </td>
                    </tr>
                  ))
                )}
              </tbody>
            </table>
          </div>
        </div>
      )}
    </section>
  );
}
