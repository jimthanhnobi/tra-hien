import { ApiError } from "./api";
import { PROVINCE_CODE_PATTERN, WARD_CODE_PATTERN } from "./administrative-geo";
import type {
  CorrectCustomerIdentityRequest,
  CreateCustomerAddressRequest,
  CreateCustomerInteractionRequest,
  CreateCustomerRequest,
  CustomerAddress,
  CustomerDetail,
  CustomerImportResult,
  CustomerInteraction,
  PatchCustomerAddressRequest,
  PatchCustomerInteractionRequest,
  PatchCustomerRequest
} from "./customers-api";
import {
  cleanNullableText,
  countUnicodeRunes,
  formatCostVnd,
  formatInt,
  formatVnDateTime,
  fromVnDateTimeLocal,
  isUuid,
  isValidVnDate,
  toVnDateTimeLocal
} from "./inventory-forms";

export {
  cleanNullableText,
  countUnicodeRunes,
  formatInt,
  formatVnDateTime,
  isUuid,
  isValidVnDate,
  toVnDateTimeLocal
};

export const CUSTOMER_IMPORT_MAX_BYTES = 5 * 1024 * 1024;
export const CUSTOMER_IMPORT_MAX_ROWS = 2_000;

export interface CustomerPermissions {
  canRead: boolean;
  canWrite: boolean;
  canCreate: boolean;
  canImport: boolean;
  // GAP-P202-02 = B (`IMPLEMENTED_BUT_NOT_CONFIRMED` per ADR-P2-01):
  // `GET /api/v1/admin/tags` and `GET /api/v1/admin/customers/{id}/addresses` currently require `customers.write`.
  canManageTags: boolean;
  canManageAddresses: boolean;
  // BR-CRM-04 & CustomerEndpoints.cs:226,235,454:
  // phone correction requires `customers.write`; origin/acquisitionSource/hadLegacyPurchase additionally require `owner` role.
  canCorrectPhone: boolean;
  canCorrectOwnerIdentity: boolean;
}

export function customerPermissions(
  permissions: readonly string[],
  roles: readonly string[] = []
): CustomerPermissions {
  const canRead = permissions.includes("customers.read");
  const canWrite = permissions.includes("customers.write");
  const isOwner = roles.includes("owner");
  return {
    canRead,
    canWrite,
    canCreate: canRead && canWrite,
    canImport: canRead && canWrite,
    canManageTags: canRead && canWrite,
    canManageAddresses: canRead && canWrite,
    canCorrectPhone: canRead && canWrite,
    canCorrectOwnerIdentity: canRead && canWrite && isOwner
  };
}

export type CustomerGroup = "retail" | "wholesale" | "reseller";
export type CustomerContactStatus = "active" | "do_not_contact" | "erased";
export type WritableContactStatus = "active" | "do_not_contact";
export type CustomerOrigin = "admin" | "web" | "legacy_import";
export type CustomerAcquisitionSource =
  | "unknown"
  | "legacy"
  | "google_organic"
  | "google_ads"
  | "facebook"
  | "zalo_personal"
  | "zalo_oa"
  | "referral"
  | "direct"
  | "offline"
  | "other";
export type CustomerSort =
  | "createdAt:desc"
  | "createdAt:asc"
  | "lastDeliveredAt:desc"
  | "lastDeliveredAt:asc"
  | "lifetimeValueVnd:desc"
  | "lifetimeValueVnd:asc"
  | "fullName:asc"
  | "fullName:desc";
export type InteractionChannel =
  | "zalo_personal"
  | "zalo_oa"
  | "phone"
  | "facebook"
  | "in_person"
  | "email"
  | "web"
  | "other";
export type ManualInteractionKind =
  | "inquiry"
  | "consultation"
  | "quote"
  | "follow_up"
  | "complaint"
  | "note";
export type AnyInteractionKind = ManualInteractionKind | "campaign_message";
export type FollowUpDueMode = "today" | "overdue" | "week";

export const customerGroupLabels: Record<CustomerGroup, string> = {
  retail: "Khách lẻ",
  wholesale: "Khách sỉ",
  reseller: "Đại lý"
};

export const customerGroups: readonly CustomerGroup[] = ["retail", "wholesale", "reseller"];

export const contactStatusLabels: Record<CustomerContactStatus, string> = {
  active: "Đang hoạt động",
  do_not_contact: "Không liên hệ (DNC)",
  erased: "Đã xóa dữ liệu"
};

export const writableContactStatuses: readonly WritableContactStatus[] = ["active", "do_not_contact"];

export const originLabels: Record<CustomerOrigin, string> = {
  admin: "Tạo từ Admin",
  web: "Website",
  legacy_import: "Import khách cũ"
};

export const customerOrigins: readonly CustomerOrigin[] = ["admin", "web", "legacy_import"];

export const acquisitionSourceLabels: Record<CustomerAcquisitionSource, string> = {
  unknown: "Chưa rõ",
  legacy: "Khách cũ",
  google_organic: "Google tìm kiếm",
  google_ads: "Google Ads",
  facebook: "Facebook",
  zalo_personal: "Zalo cá nhân",
  zalo_oa: "Zalo OA",
  referral: "Giới thiệu",
  direct: "Trực tiếp",
  offline: "Tại cửa hàng",
  other: "Khác"
};

export const acquisitionSources: readonly CustomerAcquisitionSource[] = [
  "unknown",
  "legacy",
  "zalo_personal",
  "zalo_oa",
  "facebook",
  "google_organic",
  "google_ads",
  "referral",
  "direct",
  "offline",
  "other"
];

export const customerSortLabels: Record<CustomerSort, string> = {
  "createdAt:desc": "Mới tạo nhất",
  "createdAt:asc": "Cũ nhất",
  "lifetimeValueVnd:desc": "Chi tiêu cao nhất",
  "lifetimeValueVnd:asc": "Chi tiêu thấp nhất",
  "lastDeliveredAt:desc": "Mua gần nhất",
  "lastDeliveredAt:asc": "Lâu chưa mua",
  "fullName:asc": "Tên A → Z",
  "fullName:desc": "Tên Z → A"
};

export const customerSortOptions: readonly CustomerSort[] = [
  "createdAt:desc",
  "createdAt:asc",
  "lifetimeValueVnd:desc",
  "lifetimeValueVnd:asc",
  "lastDeliveredAt:desc",
  "lastDeliveredAt:asc",
  "fullName:asc",
  "fullName:desc"
];

export const interactionChannelLabels: Record<InteractionChannel, string> = {
  zalo_personal: "Zalo cá nhân",
  zalo_oa: "Zalo OA",
  phone: "Điện thoại",
  facebook: "Facebook",
  in_person: "Gặp trực tiếp",
  email: "Email",
  web: "Website",
  other: "Khác"
};

export const interactionChannels: readonly InteractionChannel[] = [
  "zalo_personal",
  "zalo_oa",
  "phone",
  "facebook",
  "in_person",
  "email",
  "web",
  "other"
];

// GAP-P204-01 = A (OWNER_APPROVED 2026-11-26): strictly 6 manual kinds for create/edit;
// `campaign_message` is system-generated by P5-03 and read-only on display.
export const manualInteractionKinds: readonly ManualInteractionKind[] = [
  "inquiry",
  "consultation",
  "quote",
  "follow_up",
  "complaint",
  "note"
];

export const interactionKindLabels: Record<AnyInteractionKind, string> = {
  inquiry: "Hỏi mua / Quan tâm",
  consultation: "Tư vấn",
  quote: "Báo giá",
  follow_up: "Chăm sóc lại",
  complaint: "Khiếu nại / Góp ý",
  note: "Ghi chú nội bộ",
  campaign_message: "Tin chiến dịch (hệ thống)"
};

export const followUpDueLabels: Record<FollowUpDueMode, string> = {
  today: "Cần liên hệ hôm nay",
  overdue: "Quá hạn",
  week: "7 ngày tới"
};

export const interestTypeLabels: Record<string, string> = {
  material: "Chất liệu",
  artisan: "Nghệ nhân",
  product_type: "Loại sản phẩm",
  category: "Danh mục",
  price_band: "Khoảng giá"
};

export const priceBandLabels: Record<string, string> = {
  under_1m: "Dưới 1 triệu",
  "1m_3m": "1 – 3 triệu",
  "3m_10m": "3 – 10 triệu",
  over_10m: "Trên 10 triệu"
};

export const interestSourceLabels: Record<string, string> = {
  order_auto: "Tự động từ đơn giao",
  manual: "Gắn thủ công"
};

export const consentChannelLabels: Record<string, string> = {
  zalo_personal: "Zalo cá nhân",
  zalo_oa: "Zalo OA",
  sms: "SMS",
  email: "Email",
  phone: "Điện thoại"
};

export const consentPurposeLabels: Record<string, string> = {
  marketing_new_products: "Thông tin sản phẩm mới",
  marketing_promotions: "Khuyến mãi & ưu đãi",
  customer_care: "Chăm sóc khách hàng"
};

export const consentStatusLabels: Record<string, string> = {
  granted: "Đã đồng ý",
  revoked: "Đã rút đồng ý"
};

export const consentSourceLabels: Record<string, string> = {
  checkout: "Khi đặt hàng",
  staff: "Nhân viên ghi nhận",
  account: "Tài khoản web",
  zalo_oa: "Zalo OA"
};

export const orderStatusLabels: Record<string, string> = {
  pending: "Chờ xác nhận",
  confirmed: "Đã xác nhận",
  shipping: "Đang giao",
  delivered: "Đã giao",
  delivery_failed: "Giao thất bại",
  cancelled: "Đã hủy"
};

export const orderPaymentStatusLabels: Record<string, string> = {
  unpaid: "Chưa thanh toán",
  partially_paid: "Thanh toán một phần",
  paid: "Đã thanh toán",
  partially_refunded: "Hoàn tiền một phần",
  refunded: "Đã hoàn tiền"
};

export const paymentMethodLabels: Record<string, string> = {
  bank_transfer: "Chuyển khoản",
  cash: "Tiền mặt",
  cod: "COD"
};

export const salesChannelLabels: Record<string, string> = {
  web: "Website",
  zalo_personal: "Zalo cá nhân",
  zalo_oa: "Zalo OA",
  phone: "Điện thoại",
  facebook: "Facebook",
  in_person: "Tại cửa hàng",
  other: "Khác"
};

export const importRowStatusLabels: Record<string, string> = {
  would_import: "Sẽ nhập",
  imported: "Đã nhập",
  skipped: "Bỏ qua",
  error: "Lỗi"
};

export const importReasonLabels: Record<string, string> = {
  imported: "Đã nhập thành công",
  would_import: "Hợp lệ, sẵn sàng nhập",
  duplicate_in_file: "Trùng SĐT trong cùng tệp",
  existing_phone_skipped: "SĐT đã có trên hệ thống (giữ nguyên hồ sơ cũ)",
  missing_full_name: "Thiếu họ tên khách hàng",
  invalid_full_name: "Họ tên không hợp lệ hoặc quá 150 ký tự",
  missing_contact: "Thiếu cả SĐT lẫn tên Zalo",
  invalid_phone: "SĐT di động Việt Nam không hợp lệ",
  invalid_zalo_name: "Tên Zalo không hợp lệ hoặc quá 150 ký tự",
  invalid_customer_group: "Nhóm khách không hợp lệ (chỉ retail/wholesale/reseller)",
  invalid_tag: "Nhãn không hợp lệ (tối đa 60 ký tự/nhãn, 50 nhãn/dòng)",
  invalid_note: "Ghi chú không hợp lệ hoặc quá 4.000 ký tự",
  invalid_had_purchase: "Cột had_purchase chỉ nhận true/false/1/0 hoặc để trống"
};

export const importWarningLabels: Record<string, string> = {
  zalo_only_reimport_risk: "Khách chỉ có tên Zalo (chưa có SĐT): nhập lại tệp lần sau có thể tạo trùng hồ sơ.",
  possible_duplicate_zalo_customer: "Có thể trùng khách Zalo đã tồn tại (trùng họ tên & tên Zalo). Kiểm tra kỹ trước khi nhập."
};

export const presetTagColors: readonly string[] = [
  "#214f40",
  "#5a7956",
  "#967335",
  "#a84a28",
  "#6b4c7a",
  "#2b5d83",
  "#5c695b"
];

const viDate = new Intl.DateTimeFormat("vi-VN", {
  timeZone: "Asia/Ho_Chi_Minh",
  day: "2-digit",
  month: "2-digit",
  year: "numeric"
});

export function formatVnd(value: number | string | null | undefined): string {
  return formatCostVnd(value);
}

export function formatVnDate(value: string | null | undefined): string {
  if (!value) return "—";
  const parsed = Date.parse(value);
  return Number.isFinite(parsed) ? viDate.format(new Date(parsed)) : "—";
}

// BR-CRM-01: VN mobile numbers only (second digit in {3,5,7,8,9}) -> +84xxxxxxxxx.
export function normalizeVnMobilePhone(raw: string | null | undefined): { valid: boolean; e164: string | null } {
  if (raw === null || raw === undefined) return { valid: true, e164: null };
  if (raw.includes("\0")) return { valid: false, e164: null };
  const cleaned = raw.replace(/[\s.\-()]/g, "");
  if (cleaned === "") return { valid: true, e164: null };
  if (/^0[35789]\d{8}$/.test(cleaned)) {
    return { valid: true, e164: `+84${cleaned.slice(1)}` };
  }
  if (/^84[35789]\d{8}$/.test(cleaned)) {
    return { valid: true, e164: `+${cleaned}` };
  }
  if (/^\+84[35789]\d{8}$/.test(cleaned)) {
    return { valid: true, e164: cleaned };
  }
  return { valid: false, e164: null };
}

export function toLocalPhone(phoneE164: string | null | undefined): string | null {
  if (!phoneE164) return null;
  if (/^\+84\d{9}$/.test(phoneE164)) {
    return `0${phoneE164.slice(3)}`;
  }
  return phoneE164;
}

export function formatPhoneDisplay(phoneE164: string | null | undefined): string {
  const local = toLocalPhone(phoneE164);
  if (!local) return "—";
  if (/^0\d{9}$/.test(local)) {
    return `${local.slice(0, 4)} ${local.slice(4, 7)} ${local.slice(7)}`;
  }
  return local;
}

// Strict safe Zalo deep link: only generated for validated VN mobile numbers (`https://zalo.me/0xxxxxxxxx`).
export function buildSafeZaloUrl(phoneE164: string | null | undefined): string | null {
  if (!phoneE164 || !/^\+84[35789]\d{8}$/.test(phoneE164)) return null;
  return `https://zalo.me/0${phoneE164.slice(3)}`;
}

const emailPattern = /^[^@\s]+@[^@\s]+\.[^@\s]+$/;

export function isValidEmail(raw: string): boolean {
  const value = raw.trim();
  if (!value || value.length > 254 || value.includes("\0")) return false;
  return emailPattern.test(value);
}

const hexColorPattern = /^#[0-9a-fA-F]{6}$/;

export function isValidHexColor(raw: string): boolean {
  return hexColorPattern.test(raw);
}

export function extractExistingCustomerId(error: unknown): string | null {
  if (!(error instanceof ApiError) || error.code !== "customer_phone_exists") return null;
  const candidate = error.data.existingCustomerId;
  return typeof candidate === "string" && isUuid(candidate) ? candidate : null;
}

export function isImmutableCustomer(
  customer: Pick<CustomerDetail, "contactStatus" | "mergedIntoCustomerId">
): boolean {
  return customer.contactStatus === "erased" || customer.mergedIntoCustomerId !== null;
}

// GAP-P204-01 = A: campaign_message rows are strictly immutable on PATCH; manual kinds remain editable.
export function canEditInteraction(interaction: Pick<CustomerInteraction, "kind">): boolean {
  return interaction.kind !== "campaign_message";
}

// BR-CRM-06 & GAP-P204-01 = A: follow-ups can be completed even on `campaign_message` rows if scheduled & pending.
export function canCompleteFollowUp(
  interaction: Pick<CustomerInteraction, "followUpAt" | "followUpDoneAt">
): boolean {
  return interaction.followUpAt !== null && interaction.followUpDoneAt === null;
}

export function parseMinLtvVnd(value: string): number | undefined {
  const raw = value.trim();
  if (!raw) return undefined;
  const digits = /^\d{1,3}(?:\.\d{3})+$/.test(raw) ? raw.replaceAll(".", "") : raw;
  if (!/^\d+$/.test(digits)) throw new Error("Chi tiêu tối thiểu phải là số nguyên không âm (VND).");
  const normalized = digits.replace(/^0+(?=\d)/, "");
  const amount = Number(normalized);
  if (!Number.isSafeInteger(amount) || amount < 0 || String(amount) !== normalized) {
    throw new Error("Chi tiêu tối thiểu vượt quá giới hạn số nguyên hợp lệ.");
  }
  return amount;
}

/**
 * Returns the next calendar date (`yyyy-MM-dd`) using pure UTC arithmetic (zero local TZ drift).
 * Translates inclusive VN UI filter `"đến ngày D"` into backend half-open `[after, before)` where
 * `CustomerFilterParser.TryParseDateFilter` maps `yyyy-MM-dd` to `00:00:00+07:00` (`EfCustomerQueries.cs:60-68`).
 */
export function nextVnDate(value: string): string {
  const trimmed = value.trim();
  if (!isValidVnDate(trimmed)) {
    throw new Error("Ngày phải theo định dạng yyyy-MM-dd hợp lệ.");
  }
  const [y, m, d] = trimmed.split("-").map(Number);
  const next = new Date(Date.UTC(y, m - 1, d + 1));
  if (next.getUTCFullYear() > 9999) {
    throw new Error("Ngày mua đến vượt quá giới hạn hỗ trợ.");
  }
  const ny = String(next.getUTCFullYear()).padStart(4, "0");
  const nm = String(next.getUTCMonth() + 1).padStart(2, "0");
  const nd = String(next.getUTCDate()).padStart(2, "0");
  return `${ny}-${nm}-${nd}`;
}

export function validateDeliveredDateRange(after: string, before: string): string | null {
  const a = after.trim();
  const b = before.trim();
  if (a && !isValidVnDate(a)) return "Ngày mua từ phải theo định dạng yyyy-MM-dd hợp lệ.";
  if (b && !isValidVnDate(b)) return "Ngày mua đến phải theo định dạng yyyy-MM-dd hợp lệ.";
  if (b === "9999-12-31") return "Ngày mua đến phải nhỏ hơn 9999-12-31.";
  if (a && b && a > b) return "Ngày mua từ không được lớn hơn ngày mua đến.";
  return null;
}

// --- Create Customer Form ---

export interface CreateCustomerDraft {
  fullName: string;
  phone: string;
  zaloName: string;
  email: string;
  customerGroup: CustomerGroup;
  isVip: boolean;
  acquisitionSource: CustomerAcquisitionSource;
  contactStatus: WritableContactStatus;
  hadLegacyPurchase: boolean;
  noteSummary: string;
  tagIds: string[];
}

export interface CustomerFormErrors {
  fullName?: string;
  phone?: string;
  zaloName?: string;
  email?: string;
  noteSummary?: string;
  contact?: string;
}

export function buildCreateCustomerPayload(
  draft: CreateCustomerDraft
): { payload?: CreateCustomerRequest; errors?: CustomerFormErrors } {
  const errors: CustomerFormErrors = {};
  const fullName = draft.fullName.trim();
  if (fullName.length < 1 || fullName.length > 150 || fullName.includes("\0")) {
    errors.fullName = "Họ tên bắt buộc (1–150 ký tự).";
  }

  const phoneCheck = normalizeVnMobilePhone(draft.phone);
  if (!phoneCheck.valid) {
    errors.phone = "Số điện thoại di động Việt Nam không hợp lệ (10 số đầu 03/05/07/08/09 hoặc +84).";
  }

  const zaloName = cleanNullableText(draft.zaloName);
  if (zaloName && (zaloName.length > 150 || zaloName.includes("\0"))) {
    errors.zaloName = "Tên Zalo tối đa 150 ký tự.";
  }

  if (!phoneCheck.e164 && !zaloName) {
    errors.contact = "Khách hàng phải có ít nhất số điện thoại hoặc tên Zalo.";
  }

  const email = cleanNullableText(draft.email);
  if (email && !isValidEmail(email)) {
    errors.email = "Email chưa đúng định dạng.";
  }

  const noteSummary = cleanNullableText(draft.noteSummary);
  if (noteSummary && (noteSummary.length > 2000 || noteSummary.includes("\0"))) {
    errors.noteSummary = "Ghi chú tóm tắt tối đa 2.000 ký tự.";
  }

  if (Object.keys(errors).length > 0) return { errors };

  return {
    payload: {
      fullName,
      phone: phoneCheck.e164,
      zaloName,
      email,
      customerGroup: draft.customerGroup,
      isVip: draft.isVip,
      acquisitionSource: draft.acquisitionSource,
      contactStatus: draft.contactStatus,
      hadLegacyPurchase: draft.hadLegacyPurchase,
      noteSummary,
      tagIds: draft.tagIds.length > 0 ? draft.tagIds : undefined
    }
  };
}

// --- Patch Customer Profile Form ---

export interface PatchCustomerDraft {
  fullName: string;
  zaloName: string;
  email: string;
  customerGroup: CustomerGroup;
  isVip: boolean;
  contactStatus: WritableContactStatus;
  noteSummary: string;
}

export function buildPatchCustomerPayload(
  original: CustomerDetail,
  draft: PatchCustomerDraft
): { payload?: PatchCustomerRequest; errors?: CustomerFormErrors } {
  const errors: CustomerFormErrors = {};
  const fullName = draft.fullName.trim();
  if (fullName.length < 1 || fullName.length > 150 || fullName.includes("\0")) {
    errors.fullName = "Họ tên bắt buộc (1–150 ký tự).";
  }

  const zaloName = cleanNullableText(draft.zaloName);
  if (zaloName && (zaloName.length > 150 || zaloName.includes("\0"))) {
    errors.zaloName = "Tên Zalo tối đa 150 ký tự.";
  }

  if (!original.phoneE164 && !zaloName) {
    errors.zaloName = "Khách chưa có SĐT nên không thể xóa trống tên Zalo.";
  }

  const email = cleanNullableText(draft.email);
  if (email && !isValidEmail(email)) {
    errors.email = "Email chưa đúng định dạng.";
  }

  const noteSummary = cleanNullableText(draft.noteSummary);
  if (noteSummary && (noteSummary.length > 2000 || noteSummary.includes("\0"))) {
    errors.noteSummary = "Ghi chú tóm tắt tối đa 2.000 ký tự.";
  }

  if (Object.keys(errors).length > 0) return { errors };

  const payload: PatchCustomerRequest = { rowVersion: original.rowVersion };
  if (fullName !== original.fullName) payload.fullName = fullName;
  if (zaloName !== (original.zaloName ?? null)) payload.zaloName = zaloName;
  if (email !== (original.email ?? null)) payload.email = email;
  if (draft.customerGroup !== original.customerGroup) payload.customerGroup = draft.customerGroup;
  if (draft.isVip !== original.isVip) payload.isVip = draft.isVip;
  if (draft.contactStatus !== original.contactStatus) payload.contactStatus = draft.contactStatus;
  if (noteSummary !== (original.noteSummary ?? null)) payload.noteSummary = noteSummary;

  return { payload };
}

// --- Correct Customer Identity Form (BR-CRM-04) ---

export interface CorrectIdentityDraft {
  phone: string;
  origin: CustomerOrigin;
  acquisitionSource: CustomerAcquisitionSource;
  hadLegacyPurchase: boolean;
  reason: string;
}

export interface CorrectIdentityErrors {
  phone?: string;
  reason?: string;
  target?: string;
}

export function buildCorrectIdentityPayload(
  original: CustomerDetail,
  draft: CorrectIdentityDraft,
  canCorrectOwnerIdentity: boolean
): { payload?: CorrectCustomerIdentityRequest; errors?: CorrectIdentityErrors } {
  const errors: CorrectIdentityErrors = {};
  const reason = draft.reason.trim();
  if (countUnicodeRunes(reason) < 3 || reason.length > 500 || reason.includes("\0")) {
    errors.reason = "Lý do đính chính bắt buộc từ 3 đến 500 ký tự.";
  }

  const phoneCheck = normalizeVnMobilePhone(draft.phone);
  if (!phoneCheck.valid) {
    errors.phone = "Số điện thoại di động Việt Nam không hợp lệ.";
  }

  const payload: CorrectCustomerIdentityRequest = {
    rowVersion: original.rowVersion,
    reason
  };

  let hasChange = false;
  const originalPhone = original.phoneE164 ?? null;
  if (phoneCheck.valid && phoneCheck.e164 !== originalPhone) {
    if (!phoneCheck.e164 && !original.zaloName) {
      errors.phone = "Không thể xóa SĐT khi khách chưa có tên Zalo.";
    } else {
      payload.phone = phoneCheck.e164;
      hasChange = true;
    }
  }

  if (canCorrectOwnerIdentity) {
    if (draft.origin !== original.origin) {
      payload.origin = draft.origin;
      hasChange = true;
    }
    if (draft.acquisitionSource !== original.acquisitionSource) {
      payload.acquisitionSource = draft.acquisitionSource;
      hasChange = true;
    }
    if (draft.hadLegacyPurchase !== original.stats.hadLegacyPurchase) {
      payload.hadLegacyPurchase = draft.hadLegacyPurchase;
      hasChange = true;
    }
  }

  if (!hasChange && !errors.phone) {
    errors.target = "Hãy thay đổi ít nhất một trường danh tính trước khi gửi đính chính.";
  }

  if (Object.keys(errors).length > 0) return { errors };
  return { payload };
}

// --- Address Form ---

export interface CustomerAddressDraft {
  label: string;
  receiverName: string;
  receiverPhone: string;
  provinceCode: string;
  wardCode: string;
  wardNameText: string;
  addressLine: string;
  isDefault: boolean;
}

export interface AddressFormErrors {
  label?: string;
  receiverName?: string;
  receiverPhone?: string;
  provinceCode?: string;
  wardCode?: string;
  wardNameText?: string;
  addressLine?: string;
}

export function buildCreateAddressPayload(
  draft: CustomerAddressDraft,
  rowVersion?: string | null
): { payload?: CreateCustomerAddressRequest; errors?: AddressFormErrors } {
  const errors: AddressFormErrors = {};
  const label = cleanNullableText(draft.label);
  if (label && (label.length > 80 || label.includes("\0"))) {
    errors.label = "Nhãn địa chỉ tối đa 80 ký tự.";
  }

  const receiverName = draft.receiverName.trim();
  if (receiverName.length < 1 || receiverName.length > 150 || receiverName.includes("\0")) {
    errors.receiverName = "Tên người nhận bắt buộc (1–150 ký tự).";
  }

  const phoneCheck = normalizeVnMobilePhone(draft.receiverPhone);
  if (!phoneCheck.valid || !phoneCheck.e164) {
    errors.receiverPhone = "SĐT người nhận phải là số di động Việt Nam hợp lệ.";
  }

  if (!PROVINCE_CODE_PATTERN.test(draft.provinceCode)) {
    errors.provinceCode = "Vui lòng chọn Tỉnh/Thành phố.";
  }

  const wardCode = cleanNullableText(draft.wardCode);
  const wardNameText = cleanNullableText(draft.wardNameText);
  if (wardCode && !WARD_CODE_PATTERN.test(wardCode)) {
    errors.wardCode = "Mã Phường/Xã không hợp lệ.";
  }
  if (wardNameText && (wardNameText.length > 150 || wardNameText.includes("\0"))) {
    errors.wardNameText = "Tên Phường/Xã nhập tay tối đa 150 ký tự.";
  }
  if (!wardCode && !wardNameText) {
    errors.wardCode = "Vui lòng chọn Phường/Xã (hoặc nhập tên Phường/Xã nếu tỉnh chưa có danh mục).";
  }

  const addressLine = draft.addressLine.trim();
  if (addressLine.length < 1 || addressLine.length > 255 || addressLine.includes("\0")) {
    errors.addressLine = "Địa chỉ chi tiết bắt buộc (1–255 ký tự).";
  }

  if (Object.keys(errors).length > 0) return { errors };

  return {
    payload: {
      label,
      receiverName,
      receiverPhone: phoneCheck.e164!,
      provinceCode: draft.provinceCode,
      wardCode,
      wardNameText: wardCode ? null : wardNameText,
      addressLine,
      isDefault: draft.isDefault,
      rowVersion: rowVersion ?? undefined
    }
  };
}

export function buildPatchAddressPayload(
  original: CustomerAddress,
  draft: CustomerAddressDraft,
  rowVersion?: string | null
): { payload?: PatchCustomerAddressRequest; errors?: AddressFormErrors } {
  const { payload: full, errors } = buildCreateAddressPayload(draft, rowVersion);
  if (errors || !full) return { errors };

  const patch: PatchCustomerAddressRequest = {};
  if (rowVersion) patch.rowVersion = rowVersion;
  if ((full.label ?? null) !== (original.label ?? null)) patch.label = full.label ?? null;
  if (full.receiverName !== original.receiverName) patch.receiverName = full.receiverName;
  if (full.receiverPhone !== original.receiverPhone) patch.receiverPhone = full.receiverPhone;

  // 03-database.md §3.6: omit unchanged provinceCode/wardCode so editing receiver info on
  // an inactive historical location does not trigger `invalid_address_location`.
  const locationChanged =
    full.provinceCode !== original.provinceCode || (full.wardCode ?? null) !== (original.wardCode ?? null);
  if (locationChanged) {
    patch.provinceCode = full.provinceCode;
    patch.wardCode = full.wardCode ?? null;
    patch.wardNameText = full.wardNameText ?? null;
  } else if ((full.wardNameText ?? null) !== (original.wardNameText ?? null)) {
    patch.wardNameText = full.wardNameText ?? null;
  }

  if (full.addressLine !== original.addressLine) patch.addressLine = full.addressLine;
  if (full.isDefault !== original.isDefault) patch.isDefault = full.isDefault;

  return { payload: patch };
}

// --- Interaction Forms (BR-CRM-06, GAP-P204-01 = A) ---

export interface InteractionFormDraft {
  channel: InteractionChannel;
  kind: ManualInteractionKind;
  content: string;
  occurredAt: string; // VN datetime-local (yyyy-MM-ddTHH:mm) or empty
  followUpAt: string; // VN datetime-local (yyyy-MM-ddTHH:mm) or empty
  orderId: string;
}

export interface InteractionFormErrors {
  content?: string;
  occurredAt?: string;
  followUpAt?: string;
  orderId?: string;
}

export function buildCreateInteractionPayload(
  draft: InteractionFormDraft,
  nowMs = Date.now()
): { payload?: CreateCustomerInteractionRequest; errors?: InteractionFormErrors } {
  const errors: InteractionFormErrors = {};
  const content = draft.content.trim();
  const contentRunes = countUnicodeRunes(content);
  if (contentRunes < 1 || contentRunes > 4000 || content.includes("\0")) {
    errors.content = "Nội dung tương tác bắt buộc (1–4.000 ký tự).";
  }

  let occurredIso: string | null = null;
  let occurredMs = nowMs;
  if (draft.occurredAt.trim()) {
    try {
      occurredIso = fromVnDateTimeLocal(draft.occurredAt);
      if (occurredIso) {
        occurredMs = Date.parse(occurredIso);
        if (occurredMs > nowMs) {
          errors.occurredAt = "Thời điểm tương tác không được ở trong tương lai.";
        }
      }
    } catch {
      errors.occurredAt = "Thời điểm tương tác chưa đúng định dạng.";
    }
  }

  let followUpIso: string | null = null;
  if (draft.followUpAt.trim()) {
    try {
      followUpIso = fromVnDateTimeLocal(draft.followUpAt);
      if (followUpIso && Date.parse(followUpIso) <= occurredMs) {
        errors.followUpAt = "Thời điểm hẹn chăm sóc phải sau thời điểm tương tác.";
      }
    } catch {
      errors.followUpAt = "Thời điểm hẹn chăm sóc chưa đúng định dạng.";
    }
  }

  const orderId = cleanNullableText(draft.orderId);
  if (orderId && !isUuid(orderId)) {
    errors.orderId = "Mã định danh đơn hàng không hợp lệ.";
  }

  if (Object.keys(errors).length > 0) return { errors };

  return {
    payload: {
      channel: draft.channel,
      kind: draft.kind,
      content,
      occurredAt: occurredIso,
      followUpAt: followUpIso,
      orderId
    }
  };
}

export function buildPatchInteractionPayload(
  original: CustomerInteraction,
  draft: InteractionFormDraft,
  nowMs = Date.now()
): { payload?: PatchCustomerInteractionRequest; errors?: InteractionFormErrors } {
  const errors: InteractionFormErrors = {};
  const content = draft.content.trim();
  const contentRunes = countUnicodeRunes(content);
  if (contentRunes < 1 || contentRunes > 4000 || content.includes("\0")) {
    errors.content = "Nội dung tương tác bắt buộc (1–4.000 ký tự).";
  }

  let occurredIso: string | undefined;
  let effectiveOccurredMs = Date.parse(original.occurredAt);
  if (draft.occurredAt.trim()) {
    try {
      const converted = fromVnDateTimeLocal(draft.occurredAt);
      if (converted) {
        const parsedMs = Date.parse(converted);
        if (parsedMs > nowMs) {
          errors.occurredAt = "Thời điểm tương tác không được ở trong tương lai.";
        } else {
          occurredIso = converted;
          effectiveOccurredMs = parsedMs;
        }
      }
    } catch {
      errors.occurredAt = "Thời điểm tương tác chưa đúng định dạng.";
    }
  }

  const followUpLocked = original.followUpDoneAt !== null;
  let followUpIso: string | null | undefined;
  if (!followUpLocked) {
    if (draft.followUpAt.trim()) {
      try {
        const converted = fromVnDateTimeLocal(draft.followUpAt);
        if (converted && Date.parse(converted) <= effectiveOccurredMs) {
          errors.followUpAt = "Thời điểm hẹn chăm sóc phải sau thời điểm tương tác.";
        } else {
          followUpIso = converted;
        }
      } catch {
        errors.followUpAt = "Thời điểm hẹn chăm sóc chưa đúng định dạng.";
      }
    } else if (original.followUpAt !== null) {
      followUpIso = null;
    }
  }

  const orderId = cleanNullableText(draft.orderId);
  if (orderId && !isUuid(orderId)) {
    errors.orderId = "Mã định danh đơn hàng không hợp lệ.";
  }

  if (Object.keys(errors).length > 0) return { errors };

  const payload: PatchCustomerInteractionRequest = {
    channel: draft.channel,
    kind: draft.kind,
    content,
    orderId
  };
  if (occurredIso && toVnDateTimeLocal(original.occurredAt) !== draft.occurredAt.trim()) {
    payload.occurredAt = occurredIso;
  }
  if (!followUpLocked && followUpIso !== undefined && toVnDateTimeLocal(original.followUpAt) !== draft.followUpAt.trim()) {
    payload.followUpAt = followUpIso;
  }

  return { payload };
}

export function validateFollowUpResult(raw: string): string | null {
  const trimmed = raw.trim();
  const runes = countUnicodeRunes(trimmed);
  if (runes < 1) return "Vui lòng nhập kết quả chăm sóc trước khi hoàn thành hẹn.";
  if (runes > 4000 || trimmed.includes("\0")) return "Kết quả chăm sóc tối đa 4.000 ký tự.";
  return null;
}

// --- Tag Form ---

export function validateTagForm(
  nameRaw: string,
  colorRaw: string
): { payload?: { name: string; color: string | null }; error?: string } {
  const name = nameRaw.trim();
  if (name.length < 1 || name.length > 60 || name.includes("\0")) {
    return { error: "Tên nhãn bắt buộc từ 1 đến 60 ký tự." };
  }
  const color = cleanNullableText(colorRaw);
  if (color && !isValidHexColor(color)) {
    return { error: "Màu nhãn phải theo định dạng mã hex 6 ký tự (ví dụ #214f40) hoặc để trống." };
  }
  return { payload: { name, color } };
}

// --- Legacy Customer Import State Machine (BR-CRM-03, O4) ---

export type ImportRowFilter = "all" | "ready" | "skipped" | "error" | "warnings";

export interface CustomerImportState {
  file: File | null;
  fileError: string | null;
  previewed: boolean;
  result: CustomerImportResult | null;
  applied: boolean;
  error: unknown;
}

export function customerImportInitialState(): CustomerImportState {
  return { file: null, fileError: null, previewed: false, result: null, applied: false, error: undefined };
}

export function customerImportSelectFile(file: File | null): CustomerImportState {
  if (!file) return customerImportInitialState();
  const lower = file.name.toLowerCase();
  if (!lower.endsWith(".csv") && !lower.endsWith(".xlsx")) {
    return {
      file: null,
      fileError: "Chỉ hỗ trợ tệp định dạng .csv (UTF-8) hoặc .xlsx.",
      previewed: false,
      result: null,
      applied: false,
      error: undefined
    };
  }
  if (file.size > CUSTOMER_IMPORT_MAX_BYTES) {
    return {
      file: null,
      fileError: "Tệp vượt quá giới hạn 5 MiB. Vui lòng tách nhỏ tệp trước khi tải lên.",
      previewed: false,
      result: null,
      applied: false,
      error: undefined
    };
  }
  return { file, fileError: null, previewed: false, result: null, applied: false, error: undefined };
}

export function customerImportDryRunSuccess(
  state: CustomerImportState,
  result: CustomerImportResult
): CustomerImportState {
  return {
    ...state,
    previewed: true,
    result,
    applied: false,
    error: undefined
  };
}

export function customerImportApplyResult(
  state: CustomerImportState,
  result: CustomerImportResult
): CustomerImportState {
  if (result.summary.committed) {
    return {
      ...state,
      previewed: false,
      result,
      applied: true,
      error: undefined
    };
  }
  // HTTP 200 with summary.committed = false (O4.1 = B): keep row errors visible, require re-preview.
  return {
    ...state,
    previewed: false,
    result,
    applied: false,
    error: undefined
  };
}

export function canApplyCustomerImport(state: CustomerImportState): boolean {
  if (!state.file || !state.previewed || !state.result || state.applied) return false;
  if (!state.result.summary.dryRun) return false;
  if (Number(state.result.summary.errorCount) > 0) return false;
  if (Number(state.result.summary.readyCount) <= 0) return false;
  return true;
}

export function countImportWarnings(result: CustomerImportResult | null): {
  zaloOnlyCount: number;
  possibleDuplicateZaloCount: number;
} {
  if (!result) return { zaloOnlyCount: 0, possibleDuplicateZaloCount: 0 };
  let zaloOnlyCount = 0;
  let possibleDuplicateZaloCount = 0;
  for (const row of result.rows) {
    if (row.warnings.includes("zalo_only_reimport_risk")) zaloOnlyCount++;
    if (row.warnings.includes("possible_duplicate_zalo_customer")) possibleDuplicateZaloCount++;
  }
  return { zaloOnlyCount, possibleDuplicateZaloCount };
}
