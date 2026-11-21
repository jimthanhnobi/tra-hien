import type { components, paths } from "./generated/schema";

type Query = NonNullable<paths["/api/v1/store/products"]["get"]["parameters"]["query"]>;
type Assert<T extends true> = T;

export type StoreCatalogContractAssertions = [
  Assert<"attr.capacity" extends keyof Query ? true : false>,
  Assert<"attr._texture" extends keyof Query ? true : false>,
  Assert<"attributes" extends keyof Query ? false : true>,
  Assert<"unrelated" extends keyof Query ? false : true>,
  Assert<number extends Query["attr.capacity"] ? false : true>,
  Assert<boolean extends Query["inStock"] ? false : true>,
  Assert<"cheap" extends Query["sort"] ? false : true>,
  Assert<"newest" | "price_asc" | "price_desc" extends Query["sort"] ? true : false>,
  Assert<"in_stock" | "out_of_stock" | "sold" extends components["schemas"]["StoreVariant"]["availability"] ? true : false>,
  Assert<"unknown" extends components["schemas"]["StoreProductCard"]["availability"] ? false : true>,
  Assert<{ "attr.capacity": "240-260"; "attr._texture": "smooth"; sort: "price_desc"; page: "2";
    pageSize: "20"; q: "am"; category: "tea"; collection: "tea-gifts"; type: "_review";
    material: "clay"; artisan: "chu-ban"; inStock: "true"; priceMin: "1200"; priceMax: "2400" } extends Query ? true : false>
];
