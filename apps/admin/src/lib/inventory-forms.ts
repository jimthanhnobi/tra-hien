import { z } from "zod";
import type { components } from "@teapremium/api-client";
import { ApiError, isConcurrencyConflict } from "./api";

type Schemas = components["schemas"];
export type OpeningResult = Schemas["OpeningResult"];
export type CreateReceiptRequest = Schemas["CreateReceiptRequest"];
export type EditReceiptRequest = Schemas["EditReceiptRequest"];
export type EditReceiptLineRequest = Schemas["EditReceiptLineRequest"];
export type InventoryAdjustmentRequest = Schemas["InventoryAdjustmentRequest"];

export const uuidPattern = /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i;
export const vnDatePattern = /^\d{4}-\d{2}-\d{2}$/;
export const INT32_MAX = 2_147_483_647;
export const MAX_RECEIPT_LINES = 500;
export const OPENING_PREVIEW_TTL_MS = 30 * 60_000;
export const OPENING_FILE_MAX_BYTES = 5 * 1024 * 1024;

export function isUuid(value: string): boolean {
  return uuidPattern.test(value);
}

export function shortVariantId(id: string): string {
  return id.length > 14 ? `${id.slice(0, 8)}…${id.slice(-4)}` : id;
}

export interface InventoryPermissions {
  canRead: boolean;
  canWrite: boolean;
  canApprove: boolean;
  canReadCosts: boolean;
  canReadCatalog: boolean;
  canViewAlerts: boolean;
  canViewMovements: boolean;
  canOpenImport: boolean;
  canUseReceipts: boolean;
  canAdjust: boolean;
  canIncreaseAdjustment: boolean;
  canUseStocktakes: boolean;
  canApproveStocktake: boolean;
}

// Strict permission checks from user.permissions only; never infer from roles.
export function inventoryPermissions(permissions: readonly string[]): InventoryPermissions {
  const has = (claim: string) => permissions.includes(claim);
  const canRead = has("inventory.read");
  const canWrite = has("inventory.write");
  const canApprove = has("inventory.approve");
  const canReadCosts = has("costs.read");
  const canReadCatalog = has("catalog.read");
  return {
    canRead,
    canWrite,
    canApprove,
    canReadCosts,
    canReadCatalog,
    canViewAlerts: canRead && canWrite,
    canViewMovements: canRead && canWrite,
    canOpenImport: canRead && canApprove,
    canUseReceipts: canRead && canWrite,
    canAdjust: canRead && canWrite,
    canIncreaseAdjustment: canRead && canWrite && canApprove,
    canUseStocktakes: canRead && canWrite,
    canApproveStocktake: canRead && canWrite && canApprove
  };
}

export const availabilityLabels: Record<string, string> = {
  in_stock: "Còn hàng",
  out_of_stock: "Hết hàng"
};

export const movementTypeLabels: Record<string, string> = {
  opening: "Tồn đầu kỳ",
  receipt: "Nhập kho",
  sale: "Bán hàng",
  return_restock: "Hoàn hàng nhập kho",
  adjustment: "Điều chỉnh tồn",
  stocktake: "Kiểm kê",
  damage: "Vỡ / hỏng",
  replacement_out: "Xuất đổi hàng"
};

export const movementTypes = Object.keys(movementTypeLabels) as Array<keyof typeof movementTypeLabels>;

export const receiptStatusLabels: Record<string, string> = {
  draft: "Bản nháp",
  posted: "Đã chốt",
  cancelled: "Đã hủy"
};

export const stocktakeStatusLabels: Record<string, string> = {
  counting: "Đang đếm",
  submitted: "Chờ duyệt",
  approved: "Đã duyệt",
  cancelled: "Đã hủy"
};

const viNumber = new Intl.NumberFormat("vi-VN");
const viDateTime = new Intl.DateTimeFormat("vi-VN", {
  timeZone: "Asia/Ho_Chi_Minh",
  day: "2-digit",
  month: "2-digit",
  year: "numeric",
  hour: "2-digit",
  minute: "2-digit",
  hour12: false
});

export function formatVnDateTime(value: string | null | undefined): string {
  if (!value) return "—";
  const parsed = Date.parse(value);
  return Number.isFinite(parsed) ? viDateTime.format(new Date(parsed)) : "—";
}

export function toVnDateTimeLocal(value: string | null | undefined): string {
  if (!value) return "";
  const parsed = Date.parse(value);
  if (!Number.isFinite(parsed)) return "";
  const parts = new Intl.DateTimeFormat("en-CA", {
    timeZone: "Asia/Ho_Chi_Minh",
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    hour12: false
  }).formatToParts(new Date(parsed));
  const map = Object.fromEntries(parts.map(part => [part.type, part.value]));
  const hour = map.hour === "24" ? "00" : map.hour;
  return `${map.year}-${map.month}-${map.day}T${hour}:${map.minute}`;
}

export function fromVnDateTimeLocal(value: string): string | null {
  const raw = value.trim();
  if (!raw) return null;
  const match = /^(\d{4}-\d{2}-\d{2})T(\d{2}:\d{2})(?::(\d{2}))?$/.exec(raw);
  if (!match) throw new Error("Ngày giờ nhận hàng chưa đúng định dạng.");
  const [, date, hm, sec] = match;
  if (!isValidVnDate(date)) throw new Error("Ngày nhận hàng không hợp lệ.");
  const iso = `${date}T${hm}:${sec ?? "00"}+07:00`;
  if (!Number.isFinite(Date.parse(iso))) throw new Error("Ngày giờ nhận hàng không hợp lệ.");
  return iso;
}

export function isValidVnDate(value: string): boolean {
  if (!vnDatePattern.test(value)) return false;
  const [y, m, d] = value.split("-").map(Number);
  if (y < 1900 || y > 9999 || m < 1 || m > 12 || d < 1 || d > 31) return false;
  const check = new Date(Date.UTC(y, m - 1, d));
  return check.getUTCFullYear() === y && check.getUTCMonth() === m - 1 && check.getUTCDate() === d;
}

export function validateMovementDateRange(from: string, to: string): string | null {
  const f = from.trim();
  const t = to.trim();
  if (f && !isValidVnDate(f)) return "Ngày bắt đầu phải theo định dạng yyyy-MM-dd hợp lệ.";
  if (t && !isValidVnDate(t)) return "Ngày kết thúc phải theo định dạng yyyy-MM-dd hợp lệ.";
  if (f && t && f >= t) return "Ngày bắt đầu phải nhỏ hơn ngày kết thúc (từ ngày đến trước ngày).";
  return null;
}

export function parseCostVnd(value: string): number {
  const raw = value.trim();
  if (!raw) throw new Error("Nhập giá vốn nguyên không âm (VND).");
  const digits = /^\d{1,3}(?:\.\d{3})+$/.test(raw) ? raw.replaceAll(".", "") : raw;
  if (!/^\d+$/.test(digits)) throw new Error("Giá vốn phải là số nguyên không âm (VND).");
  const normalized = digits.replace(/^0+(?=\d)/, "");
  const amount = Number(normalized);
  if (!Number.isSafeInteger(amount) || amount < 0 || String(amount) !== normalized) {
    throw new Error("Giá vốn vượt quá giới hạn số nguyên hợp lệ.");
  }
  return amount;
}

export function parseOptionalCostVnd(value: string): number | undefined {
  if (!value.trim()) return undefined;
  return parseCostVnd(value);
}

export function formatCostVnd(value: number | string | null | undefined): string {
  if (value === null || value === undefined || value === "") return "—";
  const amount = Number(value);
  if (!Number.isFinite(amount)) return "—";
  return `${viNumber.format(amount)} đ`;
}

export function formatInt(value: number | string | null | undefined): string {
  if (value === null || value === undefined || value === "") return "—";
  const n = Number(value);
  return Number.isFinite(n) ? viNumber.format(n) : "—";
}

export function parsePositiveInt32(value: string, label = "Số lượng"): number {
  const raw = value.trim();
  if (!/^\d+$/.test(raw)) throw new Error(`${label} phải là số nguyên dương.`);
  const n = Number(raw);
  if (!Number.isSafeInteger(n) || n < 1 || n > INT32_MAX || String(n) !== raw.replace(/^0+(?=\d)/, "")) {
    throw new Error(`${label} phải từ 1 đến ${viNumber.format(INT32_MAX)}.`);
  }
  return n;
}

export function parseCountedQty(value: string): number {
  const raw = value.trim();
  if (!/^\d+$/.test(raw)) throw new Error("Số đếm thực tế phải là số nguyên không âm.");
  const n = Number(raw);
  if (!Number.isSafeInteger(n) || n < 0 || n > INT32_MAX || String(n) !== raw.replace(/^0+(?=\d)/, "")) {
    throw new Error(`Số đếm thực tế phải từ 0 đến ${viNumber.format(INT32_MAX)}.`);
  }
  return n;
}

export function stocktakeDelta(countedQty: number | string, systemQtyAtCount: number | string): number {
  return Number(countedQty) - Number(systemQtyAtCount);
}

export function formatSignedDelta(delta: number | string): string {
  const n = Number(delta);
  if (!Number.isFinite(n)) return "—";
  if (n > 0) return `+${viNumber.format(n)}`;
  if (n < 0) return `-${viNumber.format(Math.abs(n))}`;
  return "0";
}

export function countUnicodeRunes(text: string): number {
  return [...text].length;
}

export function cleanNullableText(value: string): string | null {
  const trimmed = value.trim();
  return trimmed.length > 0 ? trimmed : null;
}

export interface ReceiptLineDraft {
  variantId: string;
  quantity: string;
  unitCostVnd: string;
  isExisting: boolean;
}

export interface ReceiptFormDraft {
  supplierName: string;
  receivedAt: string;
  note: string;
  lines: ReceiptLineDraft[];
}

export interface ReceiptValidationErrors {
  supplierName?: string;
  receivedAt?: string;
  note?: string;
  lines?: string;
  lineFields: Record<string, { quantity?: string; unitCostVnd?: string }>;
}

const receiptHeaderSchema = z.object({
  supplierName: z.string().max(200, "Tên nhà cung cấp tối đa 200 ký tự.").refine(v => !v.includes("\0"), "Tên nhà cung cấp chứa ký tự không hợp lệ."),
  note: z.string().max(2000, "Ghi chú tối đa 2.000 ký tự.").refine(v => !v.includes("\0"), "Ghi chú chứa ký tự không hợp lệ.")
});

export function buildCreateReceiptPayload(draft: ReceiptFormDraft): { payload?: CreateReceiptRequest; errors?: ReceiptValidationErrors } {
  const errors: ReceiptValidationErrors = { lineFields: {} };
  const headerCheck = receiptHeaderSchema.safeParse({ supplierName: draft.supplierName.trim(), note: draft.note.trim() });
  if (!headerCheck.success) {
    for (const issue of headerCheck.error.issues) {
      if (issue.path[0] === "supplierName") errors.supplierName = issue.message;
      if (issue.path[0] === "note") errors.note = issue.message;
    }
  }
  let receivedAt: string | null = null;
  try {
    receivedAt = fromVnDateTimeLocal(draft.receivedAt);
  } catch (e) {
    errors.receivedAt = e instanceof Error ? e.message : "Ngày giờ nhận hàng không hợp lệ.";
  }
  if (draft.lines.length > MAX_RECEIPT_LINES) {
    errors.lines = `Phiếu nhập tối đa ${MAX_RECEIPT_LINES} dòng biến thể.`;
  }
  const seen = new Set<string>();
  const lines: NonNullable<CreateReceiptRequest["lines"]> = [];
  for (const line of draft.lines) {
    const key = line.variantId.toLowerCase();
    if (!isUuid(line.variantId) || seen.has(key)) {
      errors.lines = "Danh sách dòng có biến thể không hợp lệ hoặc bị trùng.";
      continue;
    }
    seen.add(key);
    let quantity: number | undefined;
    let unitCostVnd: number | undefined;
    try { quantity = parsePositiveInt32(line.quantity, "Số lượng"); } catch (e) {
      errors.lineFields[line.variantId] = { ...errors.lineFields[line.variantId], quantity: e instanceof Error ? e.message : "Số lượng không hợp lệ." };
    }
    try { unitCostVnd = parseCostVnd(line.unitCostVnd); } catch (e) {
      errors.lineFields[line.variantId] = { ...errors.lineFields[line.variantId], unitCostVnd: e instanceof Error ? e.message : "Giá vốn không hợp lệ." };
    }
    if (quantity !== undefined && unitCostVnd !== undefined) {
      lines.push({ variantId: line.variantId, quantity, unitCostVnd });
    }
  }
  if (errors.supplierName || errors.receivedAt || errors.note || errors.lines || Object.keys(errors.lineFields).length > 0) {
    return { errors };
  }
  return {
    payload: {
      supplierName: cleanNullableText(draft.supplierName),
      note: cleanNullableText(draft.note),
      receivedAt,
      lines
    }
  };
}

export function buildEditReceiptPayload(
  rowVersion: string,
  draft: ReceiptFormDraft,
  canReadCosts: boolean
): { payload?: EditReceiptRequest; errors?: ReceiptValidationErrors } {
  const errors: ReceiptValidationErrors = { lineFields: {} };
  const headerCheck = receiptHeaderSchema.safeParse({ supplierName: draft.supplierName.trim(), note: draft.note.trim() });
  if (!headerCheck.success) {
    for (const issue of headerCheck.error.issues) {
      if (issue.path[0] === "supplierName") errors.supplierName = issue.message;
      if (issue.path[0] === "note") errors.note = issue.message;
    }
  }
  let receivedAt: string | null = null;
  try {
    receivedAt = fromVnDateTimeLocal(draft.receivedAt);
  } catch (e) {
    errors.receivedAt = e instanceof Error ? e.message : "Ngày giờ nhận hàng không hợp lệ.";
  }
  if (draft.lines.length > MAX_RECEIPT_LINES) {
    errors.lines = `Phiếu nhập tối đa ${MAX_RECEIPT_LINES} dòng biến thể.`;
  }
  const seen = new Set<string>();
  const lines: EditReceiptLineRequest[] = [];
  for (const line of draft.lines) {
    const key = line.variantId.toLowerCase();
    if (!isUuid(line.variantId) || seen.has(key)) {
      errors.lines = "Danh sách dòng có biến thể không hợp lệ hoặc bị trùng.";
      continue;
    }
    seen.add(key);
    let quantity: number | undefined;
    try { quantity = parsePositiveInt32(line.quantity, "Số lượng"); } catch (e) {
      errors.lineFields[line.variantId] = { ...errors.lineFields[line.variantId], quantity: e instanceof Error ? e.message : "Số lượng không hợp lệ." };
    }
    const costRaw = line.unitCostVnd.trim();
    let unitCostVnd: number | undefined;
    let includeCost = false;
    if (!line.isExisting) {
      // New lines always require explicit unitCostVnd >= 0 (even when costs.read is absent).
      try {
        unitCostVnd = parseCostVnd(line.unitCostVnd);
        includeCost = true;
      } catch (e) {
        errors.lineFields[line.variantId] = { ...errors.lineFields[line.variantId], unitCostVnd: e instanceof Error ? e.message : "Dòng mới bắt buộc nhập giá vốn >= 0." };
      }
    } else if (costRaw !== "") {
      try {
        unitCostVnd = parseCostVnd(line.unitCostVnd);
        includeCost = true;
      } catch (e) {
        errors.lineFields[line.variantId] = { ...errors.lineFields[line.variantId], unitCostVnd: e instanceof Error ? e.message : "Giá vốn không hợp lệ." };
      }
    } else if (canReadCosts) {
      // When user can read costs, clearing an existing line's cost is invalid.
      errors.lineFields[line.variantId] = { ...errors.lineFields[line.variantId], unitCostVnd: "Nhập giá vốn nguyên không âm (VND)." };
    }
    // When !canReadCosts and line.isExisting and costRaw === "", omit unitCostVnd so server preserves existing cost.
    if (quantity !== undefined && !errors.lineFields[line.variantId]) {
      lines.push(includeCost ? { variantId: line.variantId, quantity, unitCostVnd } : { variantId: line.variantId, quantity });
    }
  }
  if (errors.supplierName || errors.receivedAt || errors.note || errors.lines || Object.keys(errors.lineFields).length > 0) {
    return { errors };
  }
  return {
    payload: {
      rowVersion,
      supplierName: cleanNullableText(draft.supplierName),
      note: cleanNullableText(draft.note),
      receivedAt,
      lines
    }
  };
}

export type AdjustmentKind = "adjustment" | "damage";
export type AdjustmentDirection = "decrease" | "increase";

export interface AdjustmentFormInput {
  variantId: string;
  type: AdjustmentKind;
  direction: AdjustmentDirection;
  quantity: string;
  reason: string;
}

export interface AdjustmentFieldErrors {
  variantId?: string;
  direction?: string;
  quantity?: string;
  reason?: string;
}

export function buildAdjustmentPayload(
  input: AdjustmentFormInput,
  canApprove: boolean
): { payload?: InventoryAdjustmentRequest; errors?: AdjustmentFieldErrors } {
  const errors: AdjustmentFieldErrors = {};
  if (!isUuid(input.variantId)) errors.variantId = "Vui lòng chọn biến thể cần điều chỉnh.";
  const direction: AdjustmentDirection = input.type === "damage" ? "decrease" : input.direction;
  if (input.type === "damage" && input.direction === "increase") {
    errors.direction = "Báo vỡ / hỏng chỉ cho phép giảm tồn kho.";
  } else if (direction === "increase" && !canApprove) {
    errors.direction = "Điều chỉnh tăng tồn yêu cầu quyền inventory.approve.";
  }
  let qty: number | undefined;
  try {
    qty = parsePositiveInt32(input.quantity, "Số lượng điều chỉnh");
  } catch (e) {
    errors.quantity = e instanceof Error ? e.message : "Số lượng phải là số nguyên dương.";
  }
  const reason = input.reason.trim();
  if (countUnicodeRunes(reason) < 3) {
    errors.reason = "Lý do bắt buộc có ít nhất 3 ký tự.";
  } else if (reason.length > 2000 || reason.includes("\0")) {
    errors.reason = "Lý do tối đa 2.000 ký tự và không chứa ký tự điều khiển.";
  }
  if (errors.variantId || errors.direction || errors.quantity || errors.reason || qty === undefined) {
    return { errors };
  }
  const quantityDelta = direction === "decrease" ? -qty : qty;
  return {
    payload: {
      variantId: input.variantId,
      type: input.type,
      quantityDelta,
      reason
    }
  };
}

export const createStocktakeSchema = z.object({
  note: z.string().max(2000, "Ghi chú tối đa 2.000 ký tự.").refine(v => !v.includes("\0"), "Ghi chú chứa ký tự không hợp lệ.")
});

// GAP-P1-13-02: only 409 + concurrency_conflict is stale rowVersion; business errors handle both 409 and 422.
export const INVENTORY_BUSINESS_CODES = new Set([
  "unique_piece_violation",
  "out_of_stock",
  "stocktake_count_stale",
  "receipt_lines_invalid",
  "stocktake_lines_invalid",
  "invalid_status_transition",
  "idempotency_key_reused",
  "stock_quantity_overflow",
  "invalid_stock_movement",
  "inventory_document_codes_exhausted"
]);

export function isInventoryBusinessError(error: unknown): error is ApiError {
  if (!(error instanceof ApiError)) return false;
  if (isConcurrencyConflict(error)) return false;
  return error.status === 409 || error.status === 422;
}

export function isInvalidStatusTransition(error: unknown): boolean {
  return error instanceof ApiError && (error.status === 409 || error.status === 422) && error.code === "invalid_status_transition";
}

export const lineErrorLabels: Record<string, string> = {
  unique_piece_violation: "Hàng độc bản chỉ cho phép tối đa 1 sản phẩm trong kho.",
  out_of_stock: "Tồn kho không đủ hoặc nhỏ hơn số lượng đang giữ.",
  stocktake_count_stale: "Số đếm đã lỗi thời do có phiên kiểm kê khác duyệt trước — hãy hủy phiên này và tạo phiên mới để đếm lại.",
  not_found: "Biến thể không còn tồn tại trong hệ thống.",
  stock_quantity_overflow: "Số lượng tồn kho vượt quá giới hạn cho phép.",
  invalid_stock_movement: "Biến động kho không hợp lệ."
};

export interface MappedLineError {
  variantId: string;
  codes: string[];
  messages: string[];
}

export function extractLineErrors(error: unknown): Record<string, MappedLineError> {
  if (!(error instanceof ApiError)) return {};
  const result: Record<string, MappedLineError> = {};
  for (const [field, codes] of Object.entries(error.fields)) {
    const match = /^lines\[([^\]]+)\]$/i.exec(field);
    if (!match) continue;
    const rawId = match[1];
    const key = rawId.toLowerCase();
    const messages = codes.map(code => lineErrorLabels[code] ?? code);
    result[key] = { variantId: rawId, codes, messages };
  }
  return result;
}

export function hasStaleStocktakeError(error: unknown): boolean {
  if (!(error instanceof ApiError)) return false;
  if (error.code === "stocktake_count_stale") return true;
  return Object.values(extractLineErrors(error)).some(entry => entry.codes.includes("stocktake_count_stale"));
}

// BR-INV-06: Opening import state machine.
export interface OpeningImportState {
  file: File | null;
  previewToken: string | null;
  previewExpiresAt: number | null;
  result: OpeningResult | null;
  applied: boolean;
  error: unknown;
}

export function openingImportInitialState(): OpeningImportState {
  return { file: null, previewToken: null, previewExpiresAt: null, result: null, applied: false, error: undefined };
}

export function openingSelectFile(file: File | null): OpeningImportState {
  return { file, previewToken: null, previewExpiresAt: null, result: null, applied: false, error: undefined };
}

export function openingDryRunSuccess(state: OpeningImportState, result: OpeningResult, nowMs = Date.now()): OpeningImportState {
  const validPreview = Number(result.invalidRows) === 0 && typeof result.previewToken === "string" && result.previewToken.length > 0;
  return {
    ...state,
    result,
    previewToken: validPreview ? result.previewToken! : null,
    previewExpiresAt: validPreview ? nowMs + OPENING_PREVIEW_TTL_MS : null,
    applied: false,
    error: undefined
  };
}

export function openingApplyResult(state: OpeningImportState, result: OpeningResult): OpeningImportState {
  if (result.committed) {
    return {
      ...state,
      result,
      previewToken: null,
      previewExpiresAt: null,
      applied: true,
      error: undefined
    };
  }
  // HTTP 200 with committed=false: keep row errors visible, clear preview token and require re-preview.
  return {
    ...state,
    result,
    previewToken: null,
    previewExpiresAt: null,
    applied: false,
    error: undefined
  };
}

export function openingApplyError(state: OpeningImportState, error: unknown): OpeningImportState {
  const expiredOrInvalid = error instanceof ApiError && error.code === "opening_preview_invalid";
  return {
    ...state,
    previewToken: expiredOrInvalid ? null : state.previewToken,
    previewExpiresAt: expiredOrInvalid ? null : state.previewExpiresAt,
    applied: false,
    error
  };
}

export function canApplyOpeningImport(state: OpeningImportState, nowMs = Date.now()): boolean {
  if (!state.file || !state.previewToken || !state.result || state.applied) return false;
  if (Number(state.result.invalidRows) > 0) return false;
  if (state.previewExpiresAt !== null && nowMs >= state.previewExpiresAt) return false;
  return true;
}

// Receipt post Idempotency-Key lifecycle: reuse same UUID for same (receiptId, rowVersion), rotate on rowVersion change.
export interface PostIdempotencyState {
  receiptId: string;
  rowVersion: string;
  key: string;
}

export function resolvePostIdempotencyKey(
  current: PostIdempotencyState | null,
  receiptId: string,
  rowVersion: string,
  generate: () => string = () => crypto.randomUUID()
): PostIdempotencyState {
  if (current && current.receiptId === receiptId && current.rowVersion === rowVersion) {
    return current;
  }
  return { receiptId, rowVersion, key: generate() };
}
