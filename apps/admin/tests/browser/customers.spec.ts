import { expect, test, type Page } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";
import type { Auth } from "../../src/lib/api";
import type {
  CustomerAddress,
  CustomerDetail,
  CustomerImportResult,
  CustomerInteraction,
  CustomerOrderSummary,
  CustomerSummary,
  CustomerTag,
  FollowUpItem
} from "../../src/lib/customers-api";

const uid = (n: number) => `00000000-0000-7000-8000-${String(n).padStart(12, "0")}`;
const actorId = uid(901);
const custA = uid(1001);
const custB = uid(1002);
const custErased = uid(1003);
const tagTusa = uid(2001);
const tagVip = uid(2002);
const addr1 = uid(3001);
const ixManual = uid(4001);
const ixCampaign = uid(4002);
const order1 = uid(5001);
const campaign1 = uid(6001);
const stamp = "2026-11-26T03:00:00Z";
const apiRoot = "/api/v1/admin/";

const ownerPerms = ["customers.read", "customers.write", "catalog.read"];
const salesPerms = ["customers.read", "customers.write"];
const readOnlyPerms = ["customers.read"];

function makeAuth(permissions: string[], roles: string[] = ["owner"]): Auth {
  return {
    passwordChangeRequired: false,
    totpRequired: false,
    user: {
      id: actorId,
      email: "crm-test@example.invalid",
      displayName: "Nhân viên CSKH thử",
      isActive: true,
      mustChangePassword: false,
      totpEnabled: true,
      roles,
      rowVersion: "staff-v1",
      permissions
    }
  };
}

interface RecordedCall {
  method: string;
  url: string;
  path: string;
  query: URLSearchParams;
  status: number;
  body: Record<string, unknown> | null;
}

async function setupCrmFixture(
  page: Page,
  options: {
    permissions?: string[];
    roles?: string[];
    geoNotReady?: boolean;
    hasWebAccount?: boolean;
  } = {}
) {
  const perms = options.permissions ?? ownerPerms;
  const roles = options.roles ?? ["owner"];
  const canWrite = perms.includes("customers.write");
  const isOwner = roles.includes("owner");

  let geoNotReady = options.geoNotReady ?? false;
  let geoEmptyProvinces = false;
  let nextProvinceErrorStatus: number | null = null;
  let nextWardErrorStatus: number | null = null;
  let holdWardRelease: Record<
    string,
    { promise: Promise<void>; markStarted: () => void; markFinished: () => void }
  > = {};
  let csrfNonce = 0;
  let versionSeq = 1;
  let nextWriteError: { status: number; code: string; data?: Record<string, unknown> } | null = null;
  let nextImportMode: "with_errors" | "clean" = "with_errors";

  const tags: CustomerTag[] = [
    { id: tagTusa, name: "Tử sa", color: "#214f40" },
    { id: tagVip, name: "Quà biếu", color: "#967335" }
  ];

  const addressesByCustomer: Record<string, CustomerAddress[]> = {
    [custA]: [
      {
        id: addr1,
        customerId: custA,
        label: "Nhà riêng",
        receiverName: "Nguyễn Minh Quân",
        receiverPhone: "+84912345678",
        provinceCode: "01",
        provinceName: "Thành phố Hà Nội",
        wardCode: "00001",
        wardName: "Phường Phúc Xá",
        wardNameText: null,
        addressLine: "Số 12 phố Tràng Tiền",
        isDefault: true,
        isLocationActive: true,
        createdAt: stamp,
        updatedAt: stamp
      }
    ],
    [custB]: [
      {
        id: uid(3002),
        customerId: custB,
        label: "Địa chỉ cũ",
        receiverName: "Trần Thị Lan",
        receiverPhone: "+84987654321",
        provinceCode: "99",
        provinceName: "Tỉnh Lịch Sử",
        wardCode: "99001",
        wardName: "Phường Lịch Sử",
        wardNameText: null,
        addressLine: "Số 9 Đường Cũ",
        isDefault: true,
        isLocationActive: false,
        createdAt: stamp,
        updatedAt: stamp
      }
    ],
    [custErased]: []
  };

  const details: Record<string, CustomerDetail> = {
    [custA]: {
      id: custA,
      code: "KH001001",
      fullName: "Nguyễn Minh Quân",
      phoneE164: "+84912345678",
      email: "quan@example.invalid",
      zaloName: "Quân Trà",
      customerGroup: "retail",
      isVip: true,
      origin: "admin",
      acquisitionSource: "zalo_personal",
      referredByCustomerId: null,
      contactStatus: "active",
      noteSummary: "Ưu tiên ấm tử sa 150-180ml",
      mergedIntoCustomerId: null,
      erasedAt: null,
      createdAt: stamp,
      updatedAt: stamp,
      rowVersion: "cust-v1",
      stats: {
        hadLegacyPurchase: true,
        deliveredOrdersCount: 2,
        lifetimeValueVnd: 4_200_000,
        firstDeliveredAt: "2026-10-01T08:00:00Z",
        lastDeliveredAt: "2026-11-20T08:00:00Z",
        lastContactedAt: "2026-11-25T08:00:00Z"
      },
      tags: [tags[0]],
      addresses: addressesByCustomer[custA],
      interests: [
        {
          id: uid(7001),
          interestType: "price_band",
          refId: null,
          priceBand: "3m_10m",
          source: "order_auto",
          score: 3,
          updatedAt: stamp
        }
      ],
      consents: [
        {
          id: uid(7101),
          channel: "zalo_personal",
          purpose: "marketing_new_products",
          status: "granted",
          source: "staff",
          capturedAt: stamp
        }
      ],
      accountSummary: {
        hasAccount: options.hasWebAccount ?? false,
        status: options.hasWebAccount ? "active" : null,
        phoneVerifiedAt: options.hasWebAccount ? stamp : null,
        emailVerifiedAt: null,
        historyUnlockedAt: null,
        lastLoginAt: options.hasWebAccount ? stamp : null
      }
    },
    [custB]: {
      id: custB,
      code: "KH001002",
      fullName: "Trần Thị Lan",
      phoneE164: "+84987654321",
      email: null,
      zaloName: "Lan Gốm",
      customerGroup: "wholesale",
      isVip: false,
      origin: "legacy_import",
      acquisitionSource: "legacy",
      referredByCustomerId: null,
      contactStatus: "do_not_contact",
      noteSummary: null,
      mergedIntoCustomerId: null,
      erasedAt: null,
      createdAt: stamp,
      updatedAt: stamp,
      rowVersion: "cust-b-v1",
      stats: {
        hadLegacyPurchase: true,
        deliveredOrdersCount: 1,
        lifetimeValueVnd: 1_500_000,
        firstDeliveredAt: "2026-11-10T08:00:00Z",
        lastDeliveredAt: "2026-11-10T08:00:00Z",
        lastContactedAt: null
      },
      tags: [],
      addresses: [],
      interests: [],
      consents: [],
      accountSummary: {
        hasAccount: false,
        status: null,
        phoneVerifiedAt: null,
        emailVerifiedAt: null,
        historyUnlockedAt: null,
        lastLoginAt: null
      }
    },
    [custErased]: {
      id: custErased,
      code: "KH001003",
      fullName: "[Đã xóa]",
      phoneE164: null,
      email: null,
      zaloName: null,
      customerGroup: "retail",
      isVip: false,
      origin: "web",
      acquisitionSource: "direct",
      referredByCustomerId: null,
      contactStatus: "erased",
      noteSummary: null,
      mergedIntoCustomerId: null,
      erasedAt: stamp,
      createdAt: stamp,
      updatedAt: stamp,
      rowVersion: "cust-erased-v1",
      stats: {
        hadLegacyPurchase: false,
        deliveredOrdersCount: 1,
        lifetimeValueVnd: 900_000,
        firstDeliveredAt: stamp,
        lastDeliveredAt: stamp,
        lastContactedAt: null
      },
      tags: [],
      addresses: [],
      interests: [],
      consents: [],
      accountSummary: {
        hasAccount: false,
        status: "disabled",
        phoneVerifiedAt: null,
        emailVerifiedAt: null,
        historyUnlockedAt: null,
        lastLoginAt: null
      }
    }
  };

  const interactionsByCustomer: Record<string, CustomerInteraction[]> = {
    [custA]: [
      {
        id: ixManual,
        customerId: custA,
        staffId: actorId,
        staffDisplayName: "Nhân viên CSKH thử",
        channel: "zalo_personal",
        kind: "consultation",
        content: "Khách hỏi ấm Thạch Biều đất Chu Nê 180ml",
        occurredAt: "2026-11-25T02:30:00Z",
        orderId: order1,
        orderCode: "DH-261120-001",
        campaignId: null,
        followUpAt: "2026-11-27T02:00:00Z",
        followUpDoneAt: null,
        followUpResult: null,
        createdAt: "2026-11-25T02:30:00Z"
      },
      {
        id: ixCampaign,
        customerId: custA,
        staffId: actorId,
        staffDisplayName: "Hệ thống chiến dịch",
        channel: "zalo_personal",
        kind: "campaign_message",
        content: "Tin chiến dịch hàng mới tháng 11",
        occurredAt: "2026-11-24T01:00:00Z",
        orderId: null,
        orderCode: null,
        campaignId: campaign1,
        followUpAt: "2026-11-26T04:00:00Z",
        followUpDoneAt: null,
        followUpResult: null,
        createdAt: "2026-11-24T01:00:00Z"
      }
    ],
    [custB]: [],
    [custErased]: []
  };

  const ordersByCustomer: Record<string, CustomerOrderSummary[]> = {
    [custA]: [
      {
        id: order1,
        code: "DH-261120-001",
        status: "delivered",
        paymentMethod: "bank_transfer",
        paymentStatus: "paid",
        salesChannel: "zalo_personal",
        origin: "admin",
        subtotalVnd: 2_500_000,
        discountVnd: 100_000,
        shippingFeeVnd: 0,
        totalVnd: 2_400_000,
        placedAt: "2026-11-18T03:00:00Z",
        deliveredAt: "2026-11-20T08:00:00Z",
        cancelledAt: null
      }
    ]
  };

  const followUps: FollowUpItem[] = [
    {
      interactionId: ixManual,
      customerId: custA,
      customerCode: "KH001001",
      customerFullName: "Nguyễn Minh Quân",
      customerPhoneE164: "+84912345678",
      customerZaloName: "Quân Trà",
      customerGroup: "retail",
      customerIsVip: true,
      customerContactStatus: "active",
      staffId: actorId,
      staffDisplayName: "Nhân viên CSKH thử",
      channel: "zalo_personal",
      kind: "consultation",
      content: "Gửi thêm ảnh thực tế ấm Thạch Biều",
      occurredAt: "2026-11-25T02:30:00Z",
      orderId: order1,
      orderCode: "DH-261120-001",
      followUpAt: "2026-11-25T09:00:00Z",
      isOverdue: true,
      createdAt: "2026-11-25T02:30:00Z"
    },
    {
      interactionId: uid(4099),
      customerId: custB,
      customerCode: "KH001002",
      customerFullName: "Trần Thị Lan",
      customerPhoneE164: "+84987654321",
      customerZaloName: "Lan Gốm",
      customerGroup: "wholesale",
      customerIsVip: false,
      customerContactStatus: "do_not_contact",
      staffId: actorId,
      staffDisplayName: "Nhân viên CSKH thử",
      channel: "phone",
      kind: "follow_up",
      content: "Xác nhận đổi trả nắp ấm theo yêu cầu khách",
      occurredAt: "2026-11-26T01:00:00Z",
      orderId: null,
      orderCode: null,
      followUpAt: "2026-11-26T10:00:00Z",
      isOverdue: false,
      createdAt: "2026-11-26T01:00:00Z"
    }
  ];

  const provinces = [
    { code: "01", name: "Thành phố Hà Nội", sortOrder: 1 },
    { code: "79", name: "Thành phố Hồ Chí Minh", sortOrder: 2 },
    { code: "48", name: "Thành phố Đà Nẵng", sortOrder: 3 }
  ];
  const wardsByProvince: Record<string, Array<{ code: string; provinceCode: string; name: string }>> = {
    "01": [
      { code: "00001", provinceCode: "01", name: "Phường Phúc Xá" },
      { code: "00004", provinceCode: "01", name: "Phường Trúc Bạch" }
    ],
    "79": [
      { code: "26734", provinceCode: "79", name: "Phường Tân Định" },
      { code: "26737", provinceCode: "79", name: "Phường Đa Kao" },
      { code: "26740", provinceCode: "79", name: "Phường Bến Nghé" },
      { code: "26743", provinceCode: "79", name: "Phường Bến Thành" },
      { code: "26746", provinceCode: "79", name: "Phường Nguyễn Thái Bình" },
      { code: "26749", provinceCode: "79", name: "Phường Phạm Ngũ Lão" },
      { code: "26752", provinceCode: "79", name: "Phường Cầu Ông Lãnh" },
      { code: "26755", provinceCode: "79", name: "Phường Cô Giang" },
      { code: "26758", provinceCode: "79", name: "Phường Nguyễn Cư Trinh" },
      { code: "26761", provinceCode: "79", name: "Phường Cầu Kho" },
      { code: "26764", provinceCode: "79", name: "Phường Võ Thị Sáu" },
      { code: "26767", provinceCode: "79", name: "Phường Tân Thuận Đông" },
      { code: "26770", provinceCode: "79", name: "Phường Tân Thuận Tây" },
      { code: "26773", provinceCode: "79", name: "Phường Tân Kiểng" },
      { code: "26776", provinceCode: "79", name: "Phường Tân Hưng" },
      { code: "26779", provinceCode: "79", name: "Phường Bình Thuận" }
    ],
    "48": []
  };

  const calls: RecordedCall[] = [];

  function toSummary(d: CustomerDetail): CustomerSummary {
    return {
      id: d.id,
      code: d.code,
      fullName: d.fullName,
      phoneE164: d.phoneE164,
      email: d.email,
      zaloName: d.zaloName,
      customerGroup: d.customerGroup,
      isVip: d.isVip,
      origin: d.origin,
      acquisitionSource: d.acquisitionSource,
      contactStatus: d.contactStatus,
      hadLegacyPurchase: d.stats.hadLegacyPurchase,
      deliveredOrdersCount: d.stats.deliveredOrdersCount,
      lifetimeValueVnd: d.stats.lifetimeValueVnd,
      lastDeliveredAt: d.stats.lastDeliveredAt,
      lastContactedAt: d.stats.lastContactedAt,
      createdAt: d.createdAt,
      updatedAt: d.updatedAt,
      rowVersion: d.rowVersion,
      tags: d.tags
    };
  }

  // Public administrative reference routes
  await page.route("**/api/v1/store/provinces**", async route => {
    const req = route.request();
    const url = new URL(req.url());
    const respond = (json: unknown, status = 200) => {
      calls.push({ method: req.method(), url: url.pathname + url.search, path: url.pathname, query: url.searchParams, status, body: null });
      return route.fulfill({ status, json }).catch(() => {});
    };
    if (geoNotReady) {
      return respond({ code: "administrative_data_not_ready", detail: "Chưa nạp địa giới" }, 422);
    }
    if (url.pathname === "/api/v1/store/provinces") {
      if (nextProvinceErrorStatus !== null) {
        const status = nextProvinceErrorStatus;
        nextProvinceErrorStatus = null;
        return respond({ detail: "Lỗi tải tỉnh/thành tạm thời" }, status);
      }
      return respond({ items: geoEmptyProvinces ? [] : provinces });
    }
    const match = /^\/api\/v1\/store\/provinces\/(\d{2})\/wards$/.exec(url.pathname);
    if (match) {
      const code = match[1];
      if (nextWardErrorStatus !== null) {
        const status = nextWardErrorStatus;
        nextWardErrorStatus = null;
        return respond({ detail: "Lỗi tải phường/xã tạm thời" }, status);
      }
      if (holdWardRelease[code]) {
        const gate = holdWardRelease[code];
        delete holdWardRelease[code];
        gate.markStarted();
        await gate.promise;
        const prov = provinces.find(p => p.code === code);
        await respond({ provinceCode: code, provinceName: prov?.name ?? code, items: wardsByProvince[code] ?? [] });
        gate.markFinished();
        return;
      }
      const prov = provinces.find(p => p.code === code);
      if (!prov) return respond({ code: "not_found" }, 404);
      return respond({ provinceCode: code, provinceName: prov.name, items: wardsByProvince[code] ?? [] });
    }
    return respond({ code: "not_found" }, 404);
  });

  // Admin CRM & IAM routes
  await page.route("**/api/v1/admin/**", async route => {
    const req = route.request();
    const url = new URL(req.url());
    const path = url.pathname.slice(apiRoot.length);
    const method = req.method();
    const headers = req.headers();
    let body: Record<string, unknown> | null = null;
    if (method !== "GET" && (headers["content-type"] ?? "").includes("application/json")) {
      try { body = req.postDataJSON() as Record<string, unknown>; } catch { body = null; }
    }
    const respond = (json: unknown, status = 200) => {
      calls.push({ method, url: url.pathname + url.search, path, query: url.searchParams, status, body });
      return status === 204 ? route.fulfill({ status: 204 }) : route.fulfill({ status, json });
    };

    if (path === "auth/me") return respond(makeAuth(perms, roles));
    if (path === "auth/csrf") return respond({ token: `csrf-${++csrfNonce}` });

    if (method !== "GET") {
      expect(headers["x-csrf-token"]).toBe(`csrf-${csrfNonce}`);
    }

    if (nextWriteError && method !== "GET") {
      const err = nextWriteError;
      nextWriteError = null;
      return respond({ code: err.code, data: err.data }, err.status);
    }

    // Tags (requires customers.write per GAP-P202-02 = B)
    if (path === "tags") {
      if (!canWrite) return respond({ code: "forbidden" }, 403);
      if (method === "GET") return respond(tags);
      if (method === "POST") {
        const name = String(body?.name ?? "").trim();
        if (tags.some(t => t.name.toLowerCase() === name.toLowerCase())) {
          return respond({ code: "tag_name_exists" }, 409);
        }
        const created: CustomerTag = { id: uid(2100 + tags.length), name, color: (body?.color as string | null) ?? null };
        tags.push(created);
        return respond(created, 201);
      }
    }
    const tagMatch = /^tags\/([^/]+)$/.exec(path);
    if (tagMatch && method === "PATCH") {
      if (!canWrite) return respond({ code: "forbidden" }, 403);
      const found = tags.find(t => t.id === tagMatch[1]);
      if (!found) return respond({ code: "not_found" }, 404);
      if (typeof body?.name === "string") found.name = body.name;
      if ("color" in (body ?? {})) found.color = (body?.color as string | null) ?? null;
      return respond(found);
    }

    // Follow-ups queue
    if (path === "follow-ups" && method === "GET") {
      const due = url.searchParams.get("due") ?? "today";
      const filtered = due === "overdue" ? followUps.filter(f => f.isOverdue) : followUps;
      return respond({ items: filtered, page: 1, pageSize: 20, totalItems: filtered.length });
    }

    // Customer import
    if (path === "customers/import" && method === "POST") {
      if (!canWrite) return respond({ code: "forbidden" }, 403);
      const dryRun = url.searchParams.get("dryRun") === "true";
      const result: CustomerImportResult =
        nextImportMode === "with_errors"
          ? {
              summary: {
                dryRun,
                committed: false,
                totalRows: 3,
                readyCount: 1,
                importedCount: 0,
                skippedCount: 1,
                duplicateInFileCount: 0,
                existingPhoneSkippedCount: 1,
                errorCount: 1,
                createdTagsCount: 0,
                createdInteractionsCount: 0,
                newTags: ["Khách Hội An"]
              },
              rows: [
                {
                  row: 2,
                  status: "would_import",
                  reasonCode: "would_import",
                  customerId: null,
                  customerCode: null,
                  normalizedPhone: null,
                  existingCustomerId: null,
                  duplicateOfRow: null,
                  createNoteInteraction: true,
                  tags: ["Khách Hội An"],
                  warnings: ["zalo_only_reimport_risk", "possible_duplicate_zalo_customer"],
                  errors: []
                },
                {
                  row: 3,
                  status: "skipped",
                  reasonCode: "existing_phone_skipped",
                  customerId: null,
                  customerCode: null,
                  normalizedPhone: "+84912345678",
                  existingCustomerId: custA,
                  duplicateOfRow: null,
                  createNoteInteraction: false,
                  tags: [],
                  warnings: [],
                  errors: []
                },
                {
                  row: 4,
                  status: "error",
                  reasonCode: "missing_contact",
                  customerId: null,
                  customerCode: null,
                  normalizedPhone: null,
                  existingCustomerId: null,
                  duplicateOfRow: null,
                  createNoteInteraction: false,
                  tags: [],
                  warnings: [],
                  errors: [{ column: "phone", code: "missing_contact", message: "Thiếu cả SĐT và tên Zalo." }]
                }
              ]
            }
          : {
              summary: {
                dryRun,
                committed: !dryRun,
                totalRows: 2,
                readyCount: 1,
                importedCount: dryRun ? 0 : 1,
                skippedCount: 1,
                duplicateInFileCount: 0,
                existingPhoneSkippedCount: 1,
                errorCount: 0,
                createdTagsCount: dryRun ? 0 : 1,
                createdInteractionsCount: dryRun ? 0 : 1,
                newTags: ["Khách Hội An"]
              },
              rows: [
                {
                  row: 2,
                  status: dryRun ? "would_import" : "imported",
                  reasonCode: dryRun ? "would_import" : "imported",
                  customerId: dryRun ? null : custB,
                  customerCode: dryRun ? null : "KH001099",
                  normalizedPhone: "+84901112222",
                  existingCustomerId: null,
                  duplicateOfRow: null,
                  createNoteInteraction: true,
                  tags: ["Khách Hội An"],
                  warnings: [],
                  errors: []
                },
                {
                  row: 3,
                  status: "skipped",
                  reasonCode: "existing_phone_skipped",
                  customerId: null,
                  customerCode: null,
                  normalizedPhone: "+84912345678",
                  existingCustomerId: custA,
                  duplicateOfRow: null,
                  createNoteInteraction: false,
                  tags: [],
                  warnings: [],
                  errors: []
                }
              ]
            };
      return respond(result);
    }

    // Customers list / create
    if (path === "customers") {
      if (method === "GET") {
        const q = (url.searchParams.get("q") ?? "").toLowerCase();
        const group = url.searchParams.get("group") ?? "";
        const contactStatus = url.searchParams.get("contactStatus") ?? "";
        const tag = url.searchParams.get("tag") ?? "";
        const minLtv = url.searchParams.get("minLtv") ? Number(url.searchParams.get("minLtv")) : null;
        const afterRaw = url.searchParams.get("lastDeliveredAfter");
        const beforeRaw = url.searchParams.get("lastDeliveredBefore");
        const parseVnStart = (raw: string | null) =>
          raw ? Date.parse(/^\d{4}-\d{2}-\d{2}$/.test(raw) ? `${raw}T00:00:00+07:00` : raw) : null;
        const afterMs = parseVnStart(afterRaw);
        const beforeMs = parseVnStart(beforeRaw);

        const list = Object.values(details)
          .filter(d => !q || `${d.fullName} ${d.zaloName ?? ""} ${d.code} ${d.phoneE164 ?? ""}`.toLowerCase().includes(q))
          .filter(d => !group || d.customerGroup === group)
          .filter(d => !contactStatus || d.contactStatus === contactStatus)
          .filter(d => !tag || d.tags.some(t => t.id === tag))
          .filter(d => minLtv === null || Number(d.stats.lifetimeValueVnd) >= minLtv)
          .filter(d => afterMs === null || (d.stats.lastDeliveredAt !== null && Date.parse(d.stats.lastDeliveredAt) >= afterMs))
          .filter(d => beforeMs === null || (d.stats.lastDeliveredAt !== null && Date.parse(d.stats.lastDeliveredAt) < beforeMs))
          .map(toSummary);

        return respond({ items: list, page: 1, pageSize: 20, totalItems: list.length });
      }
      if (method === "POST") {
        if (!canWrite) return respond({ code: "forbidden" }, 403);
        const phone = (body?.phone as string | null) ?? null;
        const dup = phone ? Object.values(details).find(d => d.phoneE164 === phone) : null;
        if (dup) {
          return respond({ code: "customer_phone_exists", data: { existingCustomerId: dup.id } }, 409);
        }
        const newId = uid(1050);
        const created: CustomerDetail = {
          id: newId,
          code: "KH001050",
          fullName: String(body?.fullName ?? ""),
          phoneE164: phone,
          email: (body?.email as string | null) ?? null,
          zaloName: (body?.zaloName as string | null) ?? null,
          customerGroup: (body?.customerGroup as CustomerDetail["customerGroup"]) ?? "retail",
          isVip: Boolean(body?.isVip),
          origin: "admin",
          acquisitionSource: (body?.acquisitionSource as CustomerDetail["acquisitionSource"]) ?? "unknown",
          referredByCustomerId: null,
          contactStatus: (body?.contactStatus as CustomerDetail["contactStatus"]) ?? "active",
          noteSummary: (body?.noteSummary as string | null) ?? null,
          mergedIntoCustomerId: null,
          erasedAt: null,
          createdAt: stamp,
          updatedAt: stamp,
          rowVersion: `cust-v${++versionSeq}`,
          stats: {
            hadLegacyPurchase: Boolean(body?.hadLegacyPurchase),
            deliveredOrdersCount: 0,
            lifetimeValueVnd: 0,
            firstDeliveredAt: null,
            lastDeliveredAt: null,
            lastContactedAt: null
          },
          tags: tags.filter(t => ((body?.tagIds as string[] | undefined) ?? []).includes(t.id)),
          addresses: [],
          interests: [],
          consents: [],
          accountSummary: {
            hasAccount: false,
            status: null,
            phoneVerifiedAt: null,
            emailVerifiedAt: null,
            historyUnlockedAt: null,
            lastLoginAt: null
          }
        };
        details[newId] = created;
        addressesByCustomer[newId] = [];
        interactionsByCustomer[newId] = [];
        return respond(created, 201);
      }
    }

    // Customer detail / patch
    const detailMatch = /^customers\/([^/]+)$/.exec(path);
    if (detailMatch) {
      const id = detailMatch[1];
      const existing = details[id];
      if (!existing) return respond({ code: "not_found" }, 404);
      if (method === "GET") {
        return respond({ ...existing, addresses: addressesByCustomer[id] ?? [] });
      }
      if (method === "PATCH") {
        if (!canWrite) return respond({ code: "forbidden" }, 403);
        if (typeof body?.fullName === "string") existing.fullName = body.fullName;
        if ("zaloName" in (body ?? {})) existing.zaloName = (body?.zaloName as string | null) ?? null;
        if ("noteSummary" in (body ?? {})) existing.noteSummary = (body?.noteSummary as string | null) ?? null;
        if (typeof body?.isVip === "boolean") existing.isVip = body.isVip;
        existing.rowVersion = `cust-v${++versionSeq}`;
        return respond({ ...existing, addresses: addressesByCustomer[id] ?? [] });
      }
    }

    // Correct identity
    const identityMatch = /^customers\/([^/]+)\/correct-identity$/.exec(path);
    if (identityMatch && method === "POST") {
      if (!canWrite) return respond({ code: "forbidden" }, 403);
      const existing = details[identityMatch[1]];
      if (!existing) return respond({ code: "not_found" }, 404);
      if (!isOwner && ("origin" in (body ?? {}) || "acquisitionSource" in (body ?? {}) || "hadLegacyPurchase" in (body ?? {}))) {
        return respond({ code: "forbidden" }, 403);
      }
      if ("phone" in (body ?? {}) && existing.accountSummary.hasAccount) {
        return respond({ code: "account_exists" }, 409);
      }
      if ("phone" in (body ?? {})) {
        const nextPhone = (body?.phone as string | null) ?? null;
        const dup = nextPhone ? Object.values(details).find(d => d.id !== existing.id && d.phoneE164 === nextPhone) : null;
        if (dup) return respond({ code: "customer_phone_exists", data: { existingCustomerId: dup.id } }, 409);
        existing.phoneE164 = nextPhone;
      }
      if (typeof body?.origin === "string") existing.origin = body.origin as CustomerDetail["origin"];
      if (typeof body?.hadLegacyPurchase === "boolean") existing.stats.hadLegacyPurchase = body.hadLegacyPurchase;
      existing.rowVersion = `cust-v${++versionSeq}`;
      return respond({ ...existing, addresses: addressesByCustomer[existing.id] ?? [] });
    }

    // Put customer tags
    const custTagsMatch = /^customers\/([^/]+)\/tags$/.exec(path);
    if (custTagsMatch && method === "PUT") {
      if (!canWrite) return respond({ code: "forbidden" }, 403);
      const existing = details[custTagsMatch[1]];
      if (!existing) return respond({ code: "not_found" }, 404);
      const ids = (body?.tagIds as string[]) ?? [];
      existing.tags = tags.filter(t => ids.includes(t.id));
      existing.rowVersion = `cust-v${++versionSeq}`;
      return respond({ customerId: existing.id, rowVersion: existing.rowVersion, tags: existing.tags });
    }

    // Customer orders
    const ordersMatch = /^customers\/([^/]+)\/orders$/.exec(path);
    if (ordersMatch && method === "GET") {
      const items = ordersByCustomer[ordersMatch[1]] ?? [];
      return respond({ items, page: 1, pageSize: 20, totalItems: items.length });
    }

    // Customer addresses (requires customers.write per GAP-P202-02 = B)
    const addrListMatch = /^customers\/([^/]+)\/addresses$/.exec(path);
    if (addrListMatch) {
      if (!canWrite) return respond({ code: "forbidden" }, 403);
      const cid = addrListMatch[1];
      if (method === "GET") return respond(addressesByCustomer[cid] ?? []);
      if (method === "POST") {
        const provCode = String(body?.provinceCode ?? "");
        const wardCode = (body?.wardCode as string | null) ?? null;
        const prov = provinces.find(p => p.code === provCode);
        const ward = (wardsByProvince[provCode] ?? []).find(w => w.code === wardCode);
        const created: CustomerAddress = {
          id: uid(3100 + (addressesByCustomer[cid]?.length ?? 0)),
          customerId: cid,
          label: (body?.label as string | null) ?? null,
          receiverName: String(body?.receiverName ?? ""),
          receiverPhone: String(body?.receiverPhone ?? ""),
          provinceCode: provCode,
          provinceName: prov?.name ?? provCode,
          wardCode,
          wardName: ward?.name ?? null,
          wardNameText: (body?.wardNameText as string | null) ?? null,
          addressLine: String(body?.addressLine ?? ""),
          isDefault: Boolean(body?.isDefault),
          isLocationActive: true,
          createdAt: stamp,
          updatedAt: stamp
        };
        if (created.isDefault) {
          for (const existing of addressesByCustomer[cid] ?? []) existing.isDefault = false;
        }
        addressesByCustomer[cid] = [...(addressesByCustomer[cid] ?? []), created];
        details[cid].addresses = addressesByCustomer[cid];
        details[cid].rowVersion = `cust-v${++versionSeq}`;
        return respond(created, 201);
      }
    }

    const addrItemMatch = /^customers\/([^/]+)\/addresses\/([^/]+)$/.exec(path);
    if (addrItemMatch) {
      if (!canWrite) return respond({ code: "forbidden" }, 403);
      const [, cid, aid] = addrItemMatch;
      const list = addressesByCustomer[cid] ?? [];
      const target = list.find(a => a.id === aid);
      if (!target) return respond({ code: "not_found" }, 404);
      if (method === "PATCH") {
        if (typeof body?.receiverName === "string") target.receiverName = body.receiverName;
        if (typeof body?.addressLine === "string") target.addressLine = body.addressLine;
        if (typeof body?.isDefault === "boolean") {
          if (body.isDefault) {
            for (const other of list) other.isDefault = false;
          }
          target.isDefault = body.isDefault;
        }
        details[cid].rowVersion = `cust-v${++versionSeq}`;
        return respond(target);
      }
      if (method === "DELETE") {
        addressesByCustomer[cid] = list.filter(a => a.id !== aid);
        details[cid].addresses = addressesByCustomer[cid];
        details[cid].rowVersion = `cust-v${++versionSeq}`;
        return respond(null, 204);
      }
    }

    // Customer interactions
    const ixListMatch = /^customers\/([^/]+)\/interactions$/.exec(path);
    if (ixListMatch) {
      const cid = ixListMatch[1];
      if (method === "GET") {
        const items = interactionsByCustomer[cid] ?? [];
        return respond({ items, page: 1, pageSize: 20, totalItems: items.length });
      }
      if (method === "POST") {
        if (!canWrite) return respond({ code: "forbidden" }, 403);
        const created: CustomerInteraction = {
          id: uid(4100 + (interactionsByCustomer[cid]?.length ?? 0)),
          customerId: cid,
          staffId: actorId,
          staffDisplayName: "Nhân viên CSKH thử",
          channel: body?.channel as CustomerInteraction["channel"],
          kind: body?.kind as CustomerInteraction["kind"],
          content: String(body?.content ?? ""),
          occurredAt: (body?.occurredAt as string | null) ?? stamp,
          orderId: (body?.orderId as string | null) ?? null,
          orderCode: body?.orderId === order1 ? "DH-261120-001" : null,
          campaignId: null,
          followUpAt: (body?.followUpAt as string | null) ?? null,
          followUpDoneAt: null,
          followUpResult: null,
          createdAt: stamp
        };
        interactionsByCustomer[cid] = [created, ...(interactionsByCustomer[cid] ?? [])];
        return respond(created, 201);
      }
    }

    const ixPatchMatch = /^customers\/([^/]+)\/interactions\/([^/]+)$/.exec(path);
    if (ixPatchMatch && method === "PATCH") {
      if (!canWrite) return respond({ code: "forbidden" }, 403);
      const [, cid, iid] = ixPatchMatch;
      const found = (interactionsByCustomer[cid] ?? []).find(x => x.id === iid);
      if (!found) return respond({ code: "not_found" }, 404);
      if (found.kind === "campaign_message") return respond({ code: "campaign_interaction_immutable" }, 409);
      if (typeof body?.content === "string") found.content = body.content;
      return respond(found);
    }

    const ixCompleteMatch = /^customers\/([^/]+)\/interactions\/([^/]+)\/complete-follow-up$/.exec(path);
    if (ixCompleteMatch && method === "POST") {
      if (!canWrite) return respond({ code: "forbidden" }, 403);
      const [, cid, iid] = ixCompleteMatch;
      const found = (interactionsByCustomer[cid] ?? []).find(x => x.id === iid);
      if (found) {
        found.followUpDoneAt = stamp;
        found.followUpResult = String(body?.result ?? "");
      }
      const idx = followUps.findIndex(f => f.interactionId === iid);
      if (idx >= 0) followUps.splice(idx, 1);
      return respond(found ?? { id: iid, customerId: cid, followUpDoneAt: stamp, followUpResult: String(body?.result ?? "") });
    }

    return respond({ code: "not_found" }, 404);
  });

  return {
    calls,
    setGeoNotReady: (value: boolean) => { geoNotReady = value; },
    setGeoEmptyProvinces: (value: boolean) => { geoEmptyProvinces = value; },
    failNextProvinceFetch: (status: number) => { nextProvinceErrorStatus = status; },
    failNextWardFetch: (status: number) => { nextWardErrorStatus = status; },
    holdNextWardFetch: (code: string) => {
      let markStarted!: () => void;
      let release!: () => void;
      let markFinished!: () => void;
      const started = new Promise<void>(resolve => { markStarted = resolve; });
      const promise = new Promise<void>(resolve => { release = resolve; });
      const finished = new Promise<void>(resolve => { markFinished = resolve; });
      holdWardRelease = { ...holdWardRelease, [code]: { promise, markStarted, markFinished } };
      return { started, release, finished };
    },
    setImportMode: (mode: "with_errors" | "clean") => { nextImportMode = mode; },
    rejectNextWrite: (status: number, code: string, data?: Record<string, unknown>) => {
      nextWriteError = { status, code, data };
    }
  };
}

test("P2-14-B01 customer list filters, create duplicate-phone UX and redirect to 360 profile", async ({ page }) => {
  const fx = await setupCrmFixture(page, { permissions: ownerPerms, roles: ["owner"] });

  await page.goto("/customers");
  await expect(page.getByRole("heading", { name: "Danh sách khách hàng" })).toBeVisible();
  await expect(page.getByRole("link", { name: "Nguyễn Minh Quân", exact: true })).toBeVisible();
  await expect(page.getByRole("link", { name: "Trần Thị Lan", exact: true })).toBeVisible();

  // Filter by group and sort
  await page.getByLabel("Nhóm khách").selectOption("wholesale");
  await expect(page.getByRole("link", { name: "Trần Thị Lan", exact: true })).toBeVisible();
  await expect(page.getByRole("link", { name: "Nguyễn Minh Quân", exact: true })).toHaveCount(0);

  await page.getByRole("button", { name: "Xóa bộ lọc" }).click();
  await expect(page.getByRole("link", { name: "Nguyễn Minh Quân", exact: true })).toBeVisible();

  // P1-F1: same-day inclusive filter D..D sends lastDeliveredAfter=D & lastDeliveredBefore=D+1 (half-open)
  await page.getByLabel("Mua giao thành công từ ngày").fill("2026-11-20");
  await page.getByLabel("Mua giao thành công đến ngày").fill("2026-11-20");
  await expect(page.getByRole("link", { name: "Nguyễn Minh Quân", exact: true })).toBeVisible();
  await expect(page.getByRole("link", { name: "Trần Thị Lan", exact: true })).toHaveCount(0);
  expect(
    fx.calls.some(
      c =>
        c.method === "GET" &&
        c.path === "customers" &&
        c.query.get("lastDeliveredAfter") === "2026-11-20" &&
        c.query.get("lastDeliveredBefore") === "2026-11-21"
    )
  ).toBe(true);
  await page.getByRole("button", { name: "Xóa bộ lọc" }).click();
  await expect(page.getByRole("link", { name: "Trần Thị Lan", exact: true })).toBeVisible();

  // Navigate to create new customer
  await page.getByRole("link", { name: "Tạo khách mới" }).click();
  await expect(page.getByRole("heading", { name: "Tạo hồ sơ khách hàng mới" })).toBeVisible();

  // Duplicate phone triggers 409 customer_phone_exists + clickable link to existing customer
  await page.getByLabel("Họ tên khách hàng *").fill("Khách Trùng SĐT");
  await page.getByLabel("Số điện thoại di động VN").fill("0912 345 678");
  await page.getByRole("button", { name: "Tạo khách hàng" }).click();
  await expect(page.getByRole("alert").first()).toContainText("Số điện thoại đã tồn tại");
  await expect(page.getByRole("link", { name: "Mở hồ sơ khách trùng" })).toHaveAttribute("href", `/customers/${custA}`);

  // Valid new phone creates customer and redirects to 360 profile
  await page.getByLabel("Số điện thoại di động VN").fill("0909 111 222");
  await page.getByLabel("Tên Zalo").fill("Khách Mới Zalo");
  await page.getByRole("button", { name: "Tạo khách hàng" }).click();
  await expect(page).toHaveURL(/\/customers\/00000000-0000-7000-8000-000000001050$/);
  await expect(page.getByRole("heading", { name: "Khách Trùng SĐT" })).toBeVisible();
  expect(fx.calls.some(c => c.method === "POST" && c.path === "customers" && c.status === 201)).toBe(true);
});

test("P2-14-B02 customer 360 profile: edit, 409 conflict reload, tags, correct-identity, orders & read-only preferences", async ({ page }) => {
  const fx = await setupCrmFixture(page, { permissions: ownerPerms, roles: ["owner"], hasWebAccount: true });

  await page.goto(`/customers/${custA}`);
  await expect(page.getByRole("heading", { name: "Nguyễn Minh Quân" })).toBeVisible();
  await expect(page.getByText("4.200.000 đ")).toBeVisible();
  await expect(page.getByRole("link", { name: /Mở Zalo/ })).toHaveAttribute("href", "https://zalo.me/0912345678");

  // 409 concurrency_conflict banner on PATCH -> reload recovers cleanly
  fx.rejectNextWrite(409, "concurrency_conflict");
  await page.getByLabel("Ghi chú tóm tắt").fill("Ghi chú mới gây xung đột");
  await page.getByRole("button", { name: "Lưu thay đổi hồ sơ" }).click();
  await expect(page.getByRole("button", { name: "Tải bản mới nhất" })).toBeVisible();
  await page.getByRole("button", { name: "Tải bản mới nhất" }).click();

  // Save profile successfully
  await page.getByLabel("Ghi chú tóm tắt").fill("Đã cập nhật ghi chú mới");
  await page.getByRole("button", { name: "Lưu thay đổi hồ sơ" }).click();
  await expect(page.getByText("Đã lưu thay đổi hồ sơ khách hàng.")).toBeVisible();

  // Update customer tags
  await page.getByRole("checkbox", { name: "Quà biếu" }).check();
  await page.getByRole("button", { name: "Lưu nhãn khách hàng" }).click();
  await expect(page.getByText("Đã cập nhật nhãn khách hàng.")).toBeVisible();

  // Correct identity: web account rejects phone change with 409 account_exists
  await page.getByLabel("Số điện thoại di động VN").fill("0933 444 555");
  await page.getByLabel("Lý do đính chính *").fill("Thử đổi số khi có tài khoản web");
  await page.getByRole("button", { name: "Lưu đính chính danh tính" }).click();
  await expect(page.getByRole("alert").first()).toContainText("đã có tài khoản web");

  // Owner updates origin without touching phone -> succeeds!
  await page.getByLabel("Số điện thoại di động VN").fill("0912345678");
  await page.getByLabel("Nguồn tạo hồ sơ (Owner)").selectOption("legacy_import");
  await page.getByLabel("Lý do đính chính *").fill("Điều chỉnh nguồn gốc khách cũ");
  await page.getByRole("button", { name: "Lưu đính chính danh tính" }).click();
  await expect(page.getByText("Đã đính chính danh tính khách hàng")).toBeVisible();

  // Orders tab shows customer orders without cost/COGS
  await page.getByRole("tab", { name: /Đơn hàng/ }).click();
  await expect(page.getByText("DH-261120-001")).toBeVisible();
  await expect(page.getByText("2.400.000 đ")).toBeVisible();

  // Interests & Consents tab is read-only with honest notes and NO fake write buttons
  await page.getByRole("tab", { name: "Sở thích & Consent" }).click();
  await expect(page.getByText("3 – 10 triệu")).toBeVisible();
  await expect(page.getByText("Thông tin sản phẩm mới")).toBeVisible();
  await expect(page.getByRole("button", { name: /Gộp hồ sơ|Xóa dữ liệu|Xuất dữ liệu|Cập nhật consent/i })).toHaveCount(0);
});

test("P2-14-B03 addresses CRUD with province/ward dropdowns and 422 administrative_data_not_ready banner", async ({ page }) => {
  const fx = await setupCrmFixture(page, { permissions: salesPerms, roles: ["sales"], geoNotReady: true });

  await page.goto(`/customers/${custA}`);
  await page.getByRole("tab", { name: /Sổ địa chỉ/ }).click();
  await expect(page.getByText("Số 12 phố Tràng Tiền, Phường Phúc Xá, Thành phố Hà Nội")).toBeVisible();

  // Open form while geo data is not ready -> clear banner, no crash
  await page.getByRole("button", { name: "Thêm địa chỉ mới" }).click();
  await expect(page.getByText("Danh mục địa giới hành chính chưa sẵn sàng.")).toBeVisible();

  // Recover geo data and reopen form
  fx.setGeoNotReady(false);
  await page.getByRole("button", { name: "Hủy" }).click();
  await page.getByRole("button", { name: "Thêm địa chỉ mới" }).click();

  await page.getByLabel("Nhãn địa chỉ (tùy chọn)").fill("Văn phòng");
  await page.getByLabel("Tên người nhận *").fill("Anh Quân VP");
  await page.getByLabel("SĐT người nhận *").fill("0912345678");
  await page.getByLabel("Tỉnh / Thành phố *").selectOption("79");
  await expect(page.getByLabel("Phường / Xã *")).toContainText("Phường Tân Định");
  await page.getByLabel("Phường / Xã *").selectOption("26734");
  await page.getByLabel("Số nhà, tên đường / thôn xóm *").fill("88 Hai Bà Trưng");
  await page.getByRole("button", { name: "Lưu địa chỉ" }).click();

  await expect(page.getByText("88 Hai Bà Trưng, Phường Tân Định, Thành phố Hồ Chí Minh")).toBeVisible();

  // Delete the newly created address (Confirmation dialog + 204 response)
  await page.getByRole("button", { name: "Xóa" }).nth(1).click();
  await page.getByRole("dialog").getByRole("button", { name: "Xóa địa chỉ" }).click();
  await expect(page.getByText("Đã xóa địa chỉ khỏi sổ địa chỉ khách hàng.")).toBeVisible();
});

test("P2-14-B04 interactions timeline: manual 6 kinds, campaign_message read-only edit guard & complete-follow-up", async ({ page }) => {
  await setupCrmFixture(page, { permissions: salesPerms, roles: ["sales"] });

  await page.goto(`/customers/${custA}`);
  await page.getByRole("tab", { name: "Tương tác & Hẹn chăm sóc" }).click();

  // Both manual and campaign_message rows display
  await expect(page.getByText("Khách hỏi ấm Thạch Biều đất Chu Nê 180ml")).toBeVisible();
  await expect(page.getByText("Tin chiến dịch hàng mới tháng 11")).toBeVisible();

  // campaign_message row shows read-only notice and NO Edit button, but STILL allows Complete Follow-Up!
  const campaignCard = page.locator("li", { hasText: "Tin chiến dịch hàng mới tháng 11" });
  await expect(campaignCard.getByText("Tương tác chiến dịch tự động — không chỉnh sửa nội dung")).toBeVisible();
  await expect(campaignCard.getByRole("button", { name: "Chỉnh sửa" })).toHaveCount(0);
  await campaignCard.getByRole("button", { name: "Hoàn thành hẹn" }).click();
  await campaignCard.getByLabel("Kết quả chăm sóc *").fill("Khách đã phản hồi qua Zalo sau tin chiến dịch");
  await campaignCard.getByRole("button", { name: "Xác nhận hoàn thành" }).click();
  await expect(campaignCard.getByText("Đã hoàn thành hẹn")).toBeVisible();

  // Create new manual interaction: kind dropdown must NOT include campaign_message
  await page.getByRole("button", { name: "Ghi tương tác mới" }).click();
  const kindSelect = page.getByLabel("Loại tương tác *");
  await expect(kindSelect.locator("option")).toHaveCount(6);
  await expect(kindSelect).not.toContainText("Tin chiến dịch");

  await page.getByLabel("Nội dung trao đổi *").fill("Gửi báo giá ấm Tử Sa mới về");
  await page.getByRole("button", { name: "Lưu tương tác" }).click();
  await expect(page.getByText("Gửi báo giá ấm Tử Sa mới về")).toBeVisible();
});

test("P2-14-B05 follow-ups queue filters (today/overdue/week), DNC badge and inline completion", async ({ page }) => {
  await setupCrmFixture(page, { permissions: salesPerms, roles: ["sales"] });

  await page.goto("/customers/follow-ups");
  await expect(page.getByRole("heading", { name: "Hàng đợi hẹn chăm sóc" })).toBeVisible();
  await expect(page.getByText("Nguyễn Minh Quân")).toBeVisible();
  await expect(page.getByText("Không liên hệ (DNC)", { exact: true })).toBeVisible();

  // Switch to overdue filter
  await page.getByRole("button", { name: "Quá hạn" }).click();
  await expect(page.getByText("Nguyễn Minh Quân")).toBeVisible();
  await expect(page.getByText("Trần Thị Lan")).toHaveCount(0);

  // Complete overdue follow-up inline
  await page.getByRole("button", { name: "Hoàn thành hẹn" }).click();
  await page.getByLabel("Kết quả chăm sóc *").fill("Đã gọi khách, chốt đơn cuối tuần");
  await page.getByRole("button", { name: "Xác nhận hoàn thành" }).click();
  await expect(page.getByText(/Đã hoàn thành hẹn chăm sóc cho khách Nguyễn Minh Quân/)).toBeVisible();
});

test("P2-14-B06 legacy customer import dry-run/apply with warnings & errors, plus tags CRUD", async ({ page }) => {
  const fx = await setupCrmFixture(page, { permissions: ownerPerms, roles: ["owner"] });

  await page.goto("/customers/import");
  await expect(page.getByRole("heading", { name: "Import khách hàng cũ" })).toBeVisible();

  const applyButton = page.getByRole("button", { name: "Nhập dữ liệu" });
  await expect(applyButton).toBeDisabled();

  // Upload CSV -> dryRun with 1 error keeps Apply disabled & shows newTags + Zalo warnings
  await page.getByLabel("Chọn tệp khách hàng cũ (.csv, .xlsx)").setInputFiles({
    name: "khach-cu.csv",
    mimeType: "text/csv",
    buffer: Buffer.from("full_name,phone,zalo_name,tags,note\nKhach Zalo,,Zalo A,Khách Hội An,Ghi chu\n", "utf8")
  });
  await page.getByRole("button", { name: "Chạy thử (Dry-run)" }).click();

  await expect(page.getByText("Khách Hội An").first()).toBeVisible();
  await expect(page.getByText(/zalo_only_reimport_risk/).first()).toBeVisible();
  await expect(page.getByText(/possible_duplicate_zalo_customer/).first()).toBeVisible();
  await expect(applyButton).toBeDisabled();

  // Switch to clean dry-run -> unlocks Apply -> confirm -> committed = true
  fx.setImportMode("clean");
  await page.getByRole("button", { name: "Chạy thử (Dry-run)" }).click();
  await expect(applyButton).toBeEnabled();
  await applyButton.click();
  await page.getByRole("dialog").getByRole("button", { name: "Nhập dữ liệu" }).click();
  await expect(page.getByText(/Đã nhập thành công 1 khách hàng mới/)).toBeVisible();

  // Tags CRUD
  await page.goto("/customers/tags");
  await expect(page.getByRole("heading", { name: "Quản lý nhãn khách hàng" })).toBeVisible();
  await page.getByLabel("Tên nhãn *").fill("Tử sa");
  await page.getByRole("button", { name: "Thêm nhãn" }).click();
  await expect(page.getByRole("alert").first()).toContainText("Tên nhãn khách hàng đã tồn tại");

  await page.getByLabel("Tên nhãn *").fill("Sưu tầm");
  await page.getByRole("button", { name: "Thêm nhãn" }).click();
  await expect(page.getByText('Đã tạo nhãn "Sưu tầm".')).toBeVisible();

  await page.getByRole("button", { name: "Đổi tên / màu nhãn Sưu tầm" }).click();
  await page.locator('input[id^="edit-tag-name-"]').fill("Sưu tầm cao cấp");
  await page.getByRole("button", { name: "Lưu thay đổi" }).click();
  await expect(page.getByText('Đã cập nhật nhãn "Sưu tầm cao cấp".')).toBeVisible();
});

test("P2-14-B07 customers.read-only user fires ZERO 403/write/tags/addresses/geo requests across all routes & tabs", async ({ page }) => {
  const fx = await setupCrmFixture(page, { permissions: readOnlyPerms, roles: ["owner"] });

  await page.goto("/customers");
  await expect(page.getByRole("link", { name: "Nguyễn Minh Quân", exact: true })).toBeVisible();
  await expect(page.getByRole("link", { name: "Tạo khách mới" })).toHaveCount(0);
  await expect(page.getByRole("link", { name: "Import khách cũ" })).toHaveCount(0);
  await expect(page.getByRole("link", { name: "Nhãn (Tags)" })).toHaveCount(0);
  await expect(page.getByLabel("Nhãn khách hàng")).toHaveCount(0);

  // Follow-ups queue is readable without complete button
  await page.goto("/customers/follow-ups");
  await expect(page.getByText("Nguyễn Minh Quân")).toBeVisible();
  await expect(page.getByRole("button", { name: "Hoàn thành hẹn" })).toHaveCount(0);

  // 360 profile: check ALL 5 tabs for read-only rendering
  await page.goto(`/customers/${custA}`);
  await expect(page.getByRole("heading", { name: "Nguyễn Minh Quân" })).toBeVisible();
  await expect(page.getByText("Tử sa").first()).toBeVisible();
  await expect(page.getByRole("button", { name: "Lưu thay đổi hồ sơ" })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Lưu nhãn khách hàng" })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Lưu đính chính danh tính" })).toHaveCount(0);

  await page.getByRole("tab", { name: /Sổ địa chỉ/ }).click();
  await expect(page.getByText("Số 12 phố Tràng Tiền, Phường Phúc Xá, Thành phố Hà Nội")).toBeVisible();
  await expect(page.getByRole("button", { name: "Thêm địa chỉ mới" })).toHaveCount(0);

  await page.getByRole("tab", { name: "Tương tác & Hẹn chăm sóc" }).click();
  await expect(page.getByText("Khách hỏi ấm Thạch Biều đất Chu Nê 180ml")).toBeVisible();
  await expect(page.getByRole("button", { name: "Ghi tương tác mới" })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Hoàn thành hẹn" })).toHaveCount(0);

  await page.getByRole("tab", { name: /Đơn hàng/ }).click();
  await expect(page.getByText("DH-261120-001")).toBeVisible();

  await page.getByRole("tab", { name: "Sở thích & Consent" }).click();
  await expect(page.getByText("3 – 10 triệu")).toBeVisible();

  // Direct URLs to write-only sections render 403 UI immediately with 0 API calls
  for (const writePath of ["/customers/new", "/customers/import", "/customers/tags"]) {
    await page.goto(writePath);
    await expect(page.getByRole("heading", { name: "Bạn chưa có quyền truy cập" })).toBeVisible();
  }

  // Assert ZERO 403 responses, ZERO calls to tags, addresses, geo, or any write method!
  expect(fx.calls.filter(c => c.status === 403)).toEqual([]);
  expect(fx.calls.filter(c => c.path === "tags" || c.path.startsWith("tags/"))).toEqual([]);
  expect(fx.calls.filter(c => c.path.includes("/addresses"))).toEqual([]);
  expect(fx.calls.filter(c => c.path.startsWith("/api/v1/store/provinces"))).toEqual([]);
  expect(fx.calls.filter(c => c.method !== "GET")).toEqual([]);

  // Erased customer locks edits even for owner with write permissions
  await setupCrmFixture(page, { permissions: ownerPerms, roles: ["owner"] });
  await page.goto(`/customers/${custErased}`);
  await expect(page.getByText("Hồ sơ đã được xóa dữ liệu (ẩn danh hóa)")).toBeVisible();
  await expect(page.getByRole("button", { name: "Lưu thay đổi hồ sơ" })).toHaveCount(0);
});

test("P2-14-B08 mobile 375x812 responsive layout & axe-core accessibility across Customers views", async ({ page }) => {
  await page.setViewportSize({ width: 375, height: 812 });
  await setupCrmFixture(page, { permissions: ownerPerms, roles: ["owner"] });

  const routes = [
    "/customers",
    "/customers/new",
    "/customers/follow-ups",
    "/customers/import",
    "/customers/tags",
    `/customers/${custA}`
  ];

  for (const route of routes) {
    await page.goto(route);
    await expect(page.getByRole("heading", { level: 1, name: "Khách hàng" })).toBeVisible();
    const noOverflow = await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth);
    expect(noOverflow, `Horizontal overflow detected on ${route} at 375x812`).toBe(true);
    const axe = await new AxeBuilder({ page }).analyze();
    expect(axe.violations, `Axe violations on ${route}`).toEqual([]);
  }
});

test("P2-14-B09 province/ward dropdown edge cases: empty lists, 500 retry, 422 recovery, ward reset and out-of-order race", async ({ page }) => {
  const fx = await setupCrmFixture(page, { permissions: salesPerms, roles: ["sales"], geoNotReady: true });

  await page.goto(`/customers/${custA}`);
  await page.getByRole("tab", { name: /Sổ địa chỉ/ }).click();
  await page.getByRole("button", { name: "Thêm địa chỉ mới" }).click();

  // 1) 422 administrative_data_not_ready -> in-place recovery via "Kiểm tra lại danh mục"
  await expect(page.getByText("Danh mục địa giới hành chính chưa sẵn sàng.")).toBeVisible();
  fx.setGeoNotReady(false);
  fx.setGeoEmptyProvinces(true);
  await page.getByRole("button", { name: "Kiểm tra lại danh mục" }).click();

  // 2) Empty active provinces state keeps Save disabled cleanly
  await expect(page.getByText("Chưa có Tỉnh/Thành phố đang hoạt động.")).toBeVisible();
  await expect(page.getByRole("button", { name: "Lưu địa chỉ" })).toBeDisabled();

  // 3) Transient 500 error on provinces -> Retry button recovers cleanly
  fx.setGeoEmptyProvinces(false);
  await page.getByRole("button", { name: "Hủy" }).click();
  fx.failNextProvinceFetch(500);
  await page.getByRole("button", { name: "Thêm địa chỉ mới" }).click();
  await expect(page.getByRole("button", { name: "Thử tải lại Tỉnh/Thành phố" })).toBeVisible();
  await page.getByRole("button", { name: "Thử tải lại Tỉnh/Thành phố" }).click();
  await expect(page.getByLabel("Tỉnh / Thành phố *")).toContainText("Thành phố Hà Nội");

  // 4) Transient 500 error on wards -> Retry button recovers cleanly
  fx.failNextWardFetch(500);
  await page.getByLabel("Tỉnh / Thành phố *").selectOption("01");
  await expect(page.getByRole("button", { name: "Thử tải lại Phường/Xã" })).toBeVisible();
  await page.getByRole("button", { name: "Thử tải lại Phường/Xã" }).click();
  await expect(page.getByLabel("Phường / Xã *")).toContainText("Phường Phúc Xá");

  // 5) Selecting a ward then switching province resets wardCode immediately
  await page.getByLabel("Phường / Xã *").selectOption("00001");
  await expect(page.getByLabel("Phường / Xã *")).toHaveValue("00001");
  await page.getByLabel("Tỉnh / Thành phố *").selectOption("79");
  await expect(page.getByLabel("Phường / Xã *")).toHaveValue("");
  await expect(page.getByLabel("Phường / Xã *")).toContainText("Phường Tân Định");
  await expect(page.getByLabel("Phường / Xã *")).not.toContainText("Phường Phúc Xá");

  // 6) Deterministic out-of-order response race (P2-F4): Province 01 request hits interceptor and stays pending
  //    while Province 79 resolves first, then Province 01 resolves late -> UI stays strictly on Province 79!
  const hanoiGate = fx.holdNextWardFetch("01");
  await page.getByLabel("Tỉnh / Thành phố *").selectOption("01");
  await hanoiGate.started;
  await page.getByLabel("Tỉnh / Thành phố *").selectOption("79");
  await expect(page.getByLabel("Phường / Xã *")).toContainText("Phường Tân Định");
  hanoiGate.release();
  await hanoiGate.finished;
  expect(fx.calls.some(c => c.path === "/api/v1/store/provinces/01/wards")).toBe(true);
  expect(fx.calls.some(c => c.path === "/api/v1/store/provinces/79/wards")).toBe(true);
  await expect(page.getByLabel("Tỉnh / Thành phố *")).toHaveValue("79");
  await expect(page.getByLabel("Phường / Xã *")).toContainText("Phường Tân Định");
  await expect(page.getByLabel("Phường / Xã *")).not.toContainText("Phường Phúc Xá");

  // 7) P2-F3: wardFilter on >15 wards preserves currently selected active ward option visibly in <select>
  await page.getByLabel("Phường / Xã *").selectOption("26734");
  await expect(page.getByLabel("Phường / Xã *")).toHaveValue("26734");
  await page.getByLabel("Lọc nhanh Phường / Xã").fill("Đa Kao");
  await expect(page.getByLabel("Phường / Xã *")).toHaveValue("26734");
  await expect(page.getByLabel("Phường / Xã *")).toContainText("Phường Tân Định (đang chọn)");
  await expect(page.getByLabel("Phường / Xã *")).toContainText("Phường Đa Kao");

  // 8) Empty wards for Province 48 reveals wardNameText fallback and lets staff save cleanly
  await page.getByLabel("Tỉnh / Thành phố *").selectOption("48");
  await expect(page.getByText(/chưa có phường\/xã hoạt động trong danh mục/i)).toBeVisible();
  await page.getByLabel("Tên Phường / Xã nhập tay (dự phòng)").fill("Phường Hải Châu 1");
  await page.getByLabel("Tên người nhận *").fill("Anh Quân Đà Nẵng");
  await page.getByLabel("SĐT người nhận *").fill("0912345678");
  await page.getByLabel("Số nhà, tên đường / thôn xóm *").fill("15 Bạch Đằng");
  await page.getByRole("button", { name: "Lưu địa chỉ" }).click();
  await expect(page.getByText("15 Bạch Đằng, Phường Hải Châu 1, Thành phố Đà Nẵng")).toBeVisible();

  // 9) P2-F2: Editing an address on an inactive historical province ("99") sends 0 ward requests & saves cleanly
  await page.goto(`/customers/${custB}`);
  await page.getByRole("tab", { name: /Sổ địa chỉ/ }).click();
  await expect(page.getByText("Địa giới cũ (ngừng hoạt động)")).toBeVisible();
  await page.getByRole("button", { name: "Sửa" }).click();
  await expect(page.getByLabel("Tỉnh / Thành phố *")).toContainText("Tỉnh Lịch Sử (địa giới cũ)");
  await expect(page.getByLabel("Phường / Xã *")).toContainText("Phường Lịch Sử (địa giới cũ)");
  await expect(page.getByRole("button", { name: "Thử tải lại Phường/Xã" })).toHaveCount(0);
  expect(fx.calls.filter(c => c.path === "/api/v1/store/provinces/99/wards")).toEqual([]);
  await page.getByLabel("Tên người nhận *").fill("Trần Thị Lan (Đã cập nhật)");
  await page.getByRole("button", { name: "Cập nhật địa chỉ" }).click();
  await expect(page.getByText("Đã cập nhật địa chỉ giao hàng.")).toBeVisible();
  await expect(page.getByText("Trần Thị Lan (Đã cập nhật)")).toBeVisible();
});
