import { z } from "zod";
import type { CatalogResource, CatalogRow, ReferenceRow, ReferenceScope } from "./catalog-api";

export type Values = Record<string, string | number | boolean>;
export type FormField = { name: string; label: string; type?: "textarea" | "number" | "checkbox" | "select"; hint?: string; required?: boolean; options?: readonly (readonly [string, string])[] };
export const statuses: Record<string, string> = { hidden: "Ẩn", visible: "Hiển thị", draft: "Bản nháp", published: "Đã xuất bản", archived: "Đã lưu trữ" };
export const materialKinds = [["clay", "Đất"], ["metal", "Kim loại"], ["glass", "Thủy tinh"], ["ceramic", "Gốm"], ["porcelain", "Sứ"], ["wood", "Gỗ"], ["stone", "Đá"], ["other", "Khác"]] as const;
const name: FormField = { name: "name", label: "Tên", required: true };
const order: FormField = { name: "sortOrder", label: "Thứ tự", type: "number" };
const active: FormField = { name: "isActive", label: "Đang hoạt động", type: "checkbox" };
export function referenceFields(kind: ReferenceScope["kind"]): FormField[] {
  const code: FormField = { name: "code", label: "Mã", required: true, hint: "Chữ thường, chữ số hoặc dấu gạch dưới." };
  if (kind === "types") return [name, code, { name: "skuPrefix", label: "Tiền tố SKU", hint: "Để trống hoặc 2–4 chữ cái in hoa. Không đổi SKU đã cấp." }, { name: "description", label: "Mô tả", type: "textarea" }, { name: "googleProductCategory", label: "Danh mục Google" }, order, active];
  if (kind === "options") return [{ name: "label", label: "Nhãn lựa chọn", required: true }, code, order, active];
  return [name, code, { name: "dataType", label: "Kiểu dữ liệu (dataType)", type: "select", options: [["text", "Văn bản (text)"], ["number", "Số (number)"], ["option", "Lựa chọn (option)"], ["boolean", "Có/không (boolean)"], ["dimensions", "Kích thước (dimensions)"]] },
    { name: "scope", label: "Phạm vi (scope)", type: "select", options: [["product", "Sản phẩm (product)"], ["variant", "Biến thể (variant)"]] },
    { name: "unit", label: "Đơn vị", hint: "Chỉ dùng cho số hoặc kích thước." }, { name: "isVariantAxis", label: "Trục biến thể", type: "checkbox", hint: "Phạm vi biến thể; kiểu văn bản, số hoặc lựa chọn. Thuộc tính đã dùng có thể bị khóa cấu trúc." },
    { name: "isRequiredForPublish", label: "Bắt buộc khi xuất bản", type: "checkbox" }, { name: "isFilterable", label: "Dùng để lọc", type: "checkbox" }, { name: "isVisible", label: "Hiển thị thuộc tính", type: "checkbox" }, order, active];
}
export function taxonomyFields(resource: CatalogResource, row?: CatalogRow): FormField[] {
  const editorial = resource === "materials" || resource === "artisans";
  const stateOptions: (readonly [string, string])[] = (editorial ? ["draft", ...(row?.status === "published" ? ["published"] : []), "archived"] : ["hidden", "visible"]).map(x => [x, statuses[x]]);
  return [name, { name: "slug", label: "Đường dẫn (slug)", hint: "Tối đa 80 ký tự, chữ thường/số nối bằng dấu -. Khi tạo có thể để trống để hệ thống sinh." },
    { name: "status", label: "Trạng thái", type: "select", options: stateOptions },
    ...(resource === "artisans" ? [{ name: "title", label: "Danh xưng" }] : [order]),
    ...(resource === "materials" ? [{ name: "kind", label: "Loại chất liệu", type: "select" as const, options: materialKinds }, { name: "skuCode", label: "Mã SKU chất liệu", hint: "Để trống hoặc 2–3 chữ cái in hoa; XX được dành riêng. Không đổi SKU sản phẩm đã cấp." }, { name: "summary", label: "Tóm tắt", type: "textarea" as const }] : []),
    { name: "content", label: resource === "artisans" ? "Tiểu sử" : "Mô tả", type: "textarea", hint: "Văn bản theo đoạn. Sửa nội dung sẽ thay định dạng cũ bằng các đoạn văn; không sửa thì giữ nguyên nội dung đã lưu." },
    ...(!editorial ? [{ name: "body", label: "Nội dung bổ sung", type: "textarea" as const }] : []),
    { name: "seoTitle", label: "Tiêu đề SEO" }, { name: "seoDescription", label: "Mô tả SEO", type: "textarea" }, { name: "focusKeyword", label: "Từ khóa trọng tâm" },
    { name: "robotsNoindex", label: "Không lập chỉ mục tìm kiếm", type: "checkbox" },
    ...(resource === "categories" ? [{ name: "noindexReason", label: "Lý do không lập chỉ mục", hint: "Bắt buộc khi bật cờ không lập chỉ mục cho danh mục." }] : [])];
}
export function formSchema(fields: FormField[], kind: string, row?: CatalogRow) {
  return z.record(z.string(), z.union([z.string(), z.number(), z.boolean()])).superRefine((v, context) => {
    const fail = (field: string, message: string) => context.addIssue({ code: "custom", path: [field], message });
    for (const f of fields) {
      const value = v[f.name];
      if (f.required && (typeof value !== "string" || !value.trim())) fail(f.name, "Vui lòng nhập trường này.");
      if (typeof value === "string" && value.includes("\0")) fail(f.name, "Nội dung chứa ký tự không hợp lệ.");
      if (f.type === "select" && !f.options?.some(([key]) => key === value)) fail(f.name, "Chọn giá trị trong danh sách.");
      if (f.type === "number" && (!Number.isInteger(value) || Number(value) < -2147483648 || Number(value) > 2147483647)) fail(f.name, "Nhập số nguyên hợp lệ.");
    }
    if (v.code !== undefined && !(kind === "options" ? /^[a-z0-9_]{1,60}$/ : /^[a-z0-9_]{2,40}$/).test(String(v.code))) fail("code", "Kiểm tra mã và độ dài cho phép.");
    if (v.skuPrefix && !/^[A-Z]{2,4}$/.test(String(v.skuPrefix))) fail("skuPrefix", "Nhập 2–4 chữ cái in hoa.");
    if (v.skuCode && (!/^[A-Z]{2,3}$/.test(String(v.skuCode)) || v.skuCode === "XX")) fail("skuCode", "Nhập 2–3 chữ cái in hoa, khác XX.");
    if ((v.slug || row) && v.slug !== undefined && !/^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(String(v.slug))) fail("slug", "Đường dẫn chưa hợp lệ.");
    if (String(v.slug ?? "").length > 80) fail("slug", "Đường dẫn tối đa 80 ký tự.");
    if (v.unit && !["number", "dimensions"].includes(String(v.dataType))) fail("unit", "Đơn vị chỉ dùng cho số hoặc kích thước.");
    if (v.isVariantAxis && (v.scope !== "variant" || !["text", "number", "option"].includes(String(v.dataType)))) fail("isVariantAxis", "Trục cần phạm vi biến thể và kiểu văn bản, số hoặc lựa chọn.");
    if (kind === "categories" && v.robotsNoindex && !row?.robotsNoindex && !String(v.noindexReason ?? "").trim()) fail("noindexReason", "Nhập lý do không lập chỉ mục.");
  });
}
export function defaults(fields: FormField[], row?: ReferenceRow | CatalogRow): Values {
  const source = row ? Object.fromEntries(Object.entries(row)) : {};
  return Object.fromEntries(fields.map(f => [f.name, source[f.name] ?? (f.type === "checkbox" ? ["isActive", "isVisible"].includes(f.name) : f.type === "number" ? 0 : f.type === "select" ? f.options?.[0]?.[0] ?? "" : "")]));
}
export function plainText(html: string | null): string {
  if (!html) return "";
  const doc = new DOMParser().parseFromString(html, "text/html");
  doc.querySelectorAll("script,style").forEach(x => x.remove());
  doc.querySelectorAll("p,div,li,h1,h2,h3,h4,h5,h6,blockquote,br").forEach(x => x.append(doc.createTextNode("\n")));
  return (doc.body.textContent ?? "").replace(/\n{3,}/g, "\n\n").trim();
}
export function paragraphHtml(text: string): string {
  const escape = (s: string) => s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;").replace(/'/g, "&#39;");
  return text.split(/\r?\n/).map(line => `<p>${escape(line)}</p>`).join("");
}

// Full tree validation includes the moving node's descendants. Server still decides races.
export function validParent(rows: CatalogRow[], id: string, parentId: string | null): boolean {
  const parents = new Map(rows.map(row => [row.id, row.parentId]));
  parents.set(id, parentId);
  for (const key of parents.keys()) {
    const seen = new Set<string>(); let cursor: string | null = key;
    while (cursor !== null) {
      if (seen.has(cursor) || seen.size >= 3 || !parents.has(cursor)) return false;
      seen.add(cursor); cursor = parents.get(cursor) ?? null;
    }
  }
  return true;
}
export function categoryPath(row: CatalogRow, rows: CatalogRow[]): string {
  const names = [row.name]; const seen = new Set([row.id]); let cursor = row.parentId;
  while (cursor && names.length <= 3 && !seen.has(cursor)) {
    seen.add(cursor); const parent = rows.find(x => x.id === cursor);
    if (!parent) return `… / ${names.join(" / ")}`;
    names.unshift(parent.name); cursor = parent.parentId;
  }
  return names.join(" / ");
}
