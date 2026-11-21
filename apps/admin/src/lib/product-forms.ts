import type { AttributeRow } from "./catalog-api";
import type { Product, ProductValue, VariantPatch } from "./products-api";

export const productStatuses: Record<string, string> = {
  draft: "Bản nháp", active: "Đang công khai", archived: "Đã lưu trữ"
};
export const priceLimit = 10_000_000_000;
export const vnd = new Intl.NumberFormat("vi-VN", { style: "currency", currency: "VND" });
export const productTabs = ["information", "attributes", "variants", "images", "seo"] as const;
export type ProductTab = typeof productTabs[number];
export const tabLabels: Record<ProductTab, string> = {
  information: "Thông tin", attributes: "Thuộc tính", variants: "Biến thể & giá", images: "Ảnh", seo: "SEO"
};

export function nullable(value: string) { return value.trim() ? value : null; }

export function parseVnd(value: string) {
  const raw = value.trim();
  if (!raw) return null;
  const digits = /^\d{1,3}(?:\.\d{3})+$/.test(raw) ? raw.replaceAll(".", "") : raw;
  const amount = Number(digits);
  if (!/^\d+$/.test(digits) || !Number.isSafeInteger(amount) || amount < 1 || amount > priceLimit) {
    throw new Error("Nhập giá nguyên từ 1 đến 10.000.000.000 đồng, hoặc để trống.");
  }
  return amount;
}

export function variantPayload(rowVersion: string, values: VariantPatch, canPrice: boolean, owner: boolean) {
  const { priceVnd, sku, ...rest } = values;
  return { ...rest, rowVersion, ...(canPrice && priceVnd !== undefined ? { priceVnd } : {}), ...(owner && sku !== undefined ? { sku } : {}) };
}

export const dimensionKeys = ["d", "h", "mouth", "l", "w"] as const;
export type DimensionKey = typeof dimensionKeys[number];
export type DimensionDraft = Partial<Record<DimensionKey, string | null>>;

export function readDimensions(value: unknown): DimensionDraft {
  try {
    const parsed: unknown = typeof value === "string" ? JSON.parse(value) : value;
    if (!parsed || typeof parsed !== "object") return {};
    return Object.fromEntries(dimensionKeys.flatMap(key => {
      const entry: unknown = Reflect.get(parsed, key);
      return typeof entry === "number" || entry === null ? [[key, entry === null ? null : String(entry)]] : [];
    }));
  } catch { return {}; }
}

export function parseDimensions(value: DimensionDraft) {
  const result: Partial<Record<DimensionKey, number | null>> = {};
  for (const key of dimensionKeys) {
    const raw = value[key];
    if (raw === undefined || raw === "") continue;
    if (raw === null) { result[key] = null; continue; }
    const normalized = raw.trim().replace(",", ".");
    const amount = Number(normalized);
    if (!/^-?\d+(?:\.\d+)?$/.test(normalized) || !Number.isFinite(amount) || Math.abs(amount) > Number.MAX_SAFE_INTEGER) {
      throw new Error("Nhập kích thước bằng số hợp lệ.");
    }
    result[key] = amount;
  }
  return result;
}

export function attributeValue(def: AttributeRow, value: string | DimensionDraft): ProductValue | undefined {
  if (typeof value === "object") return { attributeId: def.id, valueJson: parseDimensions(value) };
  if (value === "") return undefined;
  const attributeId = def.id;
  switch (def.dataType) {
    case "text": return { attributeId, valueText: value };
    case "option": return { attributeId, optionId: value };
    case "boolean": return { attributeId, valueBool: value === "true" };
    case "number": {
      const normalized = value.trim().replace(",", ".");
      const number = Number(normalized);
      if (!/^-?\d+(?:\.\d{1,2})?$/.test(normalized) || Math.abs(number) > 9_999_999_999.99) {
        throw new Error("Nhập số có tối đa 2 chữ số thập phân và trị tuyệt đối không quá 9.999.999.999,99.");
      }
      return { attributeId, valueNumber: number };
    }
    default: throw new Error("Kiểu thuộc tính chưa được hỗ trợ.");
  }
}

export function initialAttributeValue(def: AttributeRow, value?: ProductValue): string | DimensionDraft {
  if (def.dataType === "dimensions" && value?.valueJson != null) return readDimensions(value.valueJson);
  return String(value?.valueText ?? value?.valueNumber ?? value?.optionId ?? value?.valueBool ?? "");
}

export function buildAttributes(original: ProductValue[], definitions: AttributeRow[], changes: Record<string, string | DimensionDraft>) {
  const values = new Map(original.map(value => [value.attributeId, value]));
  for (const def of definitions) {
    if (!def.isActive || !Object.hasOwn(changes, def.id)) continue;
    const value = attributeValue(def, changes[def.id]);
    if (value) values.set(def.id, value);
    else values.delete(def.id);
  }
  return [...values.values()];
}

const issueText: Record<string, string> = {
  product_name_missing: "Thiếu tên sản phẩm.", short_description_missing: "Thiếu mô tả ngắn.",
  description_missing: "Thiếu mô tả chi tiết.", primary_category_invalid: "Danh mục chính phải hiển thị và nằm trong danh mục của sản phẩm.",
  active_variant_missing: "Cần ít nhất một biến thể hoạt động.", variant_price_missing: "Biến thể hoạt động chưa có giá.",
  variant_price_invalid: "Giá biến thể nằm ngoài khoảng cho phép.", cover_alt_missing: "Thiếu ảnh bìa có ALT hợp lệ.",
  required_attribute_missing: "Thiếu giá trị thuộc tính bắt buộc.", variant_axis_missing: "Thiếu giá trị trục biến thể.",
  variant_axis_duplicate: "Các biến thể hoạt động có tổ hợp trục trùng nhau.",
  seo_title_invalid: "Tiêu đề SEO cần có nội dung và tối đa 70 ký tự.",
  seo_description_invalid: "Mô tả SEO cần từ 50 đến 160 ký tự."
};

export function publishIssues(fields: Record<string, string[]>, product: Product, definitions: AttributeRow[]) {
  return Object.entries(fields).flatMap(([field, codes]) => {
    const variantId = /^variants\[([^\]]+)\]/.exec(field)?.[1];
    const attrId = /attributes\[([^\]]+)\]/.exec(field)?.[1];
    const variant = product.variants?.find(value => value.id === variantId);
    const attribute = definitions.find(value => value.id === attrId);
    const label = [variant?.sku ?? variant?.name, attribute?.name].filter(Boolean).join(" · ");
    const tab: ProductTab | undefined = field.startsWith("variants") ? "variants" : field.startsWith("attributes") ? "attributes"
      : field.startsWith("media") ? "images" : field.startsWith("seo") ? "seo"
        : ["name", "shortDescription", "descriptionHtml", "primaryCategoryId"].includes(field) ? "information" : undefined;
    return codes.map(code => ({ tab, field, variantId, attrId, label, message: issueText[code] ?? `${field} · ${code}` }));
  });
}

export function thinDescription(product: Product) {
  return product.thinDescription ?? product.warnings?.some(warning => warning.code === "thin_description_noindex") ?? false;
}
