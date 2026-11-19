# P1-01 — Inventory schema và chênh lệch đặt tên

Base: `8791e8ac61f5de42815a84edbc0a0602ff177c26`. Nguồn: `docs/spec/schema.sql:144–493` (trừ reservations), sale_prices `:512`, redirects `:1137`; traceability đầy đủ trong task record và local evidence. Tên explicit của DDL giữ nguyên. Các tên implicit dưới đây được đặt theo prefix tại 03 §1, theo ngoại lệ task §3.6. Không đổi cấu trúc/ý nghĩa DDL.

## Entity/table/constraint inventory

25 entity, 25 bảng, 250 cột người dùng, một sequence `catalog.sku_number_seq` (bigint, start/min/increment/cache = 1, max = 9223372036854775807, NO CYCLE). Bốn `Version` là system `xmin`, không tính vào cột người dùng.

Constraint: 25 PK, 36 FK, 17 UNIQUE, 63 CHECK, 1 EXCLUDE. 18 index tường minh, cộng index backing cho PK/UNIQUE/EXCLUDE; không thêm index FK theo convention.

| Entity | Bảng | Cột | PK | FK | UNIQUE | CHECK | EXCLUDE | Index explicit |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| `MediaAsset` | `media.assets` | 11 | 1 | 0 | 1 | 3 | 0 | 0 |
| `UploadSession` | `media.upload_sessions` | 12 | 1 | 1 | 1 | 2 | 0 | 1 |
| `ProductType` | `catalog.product_types` | 10 | 1 | 0 | 1 | 2 | 0 | 0 |
| `AttributeDefinition` | `catalog.attribute_definitions` | 13 | 1 | 1 | 1 | 5 | 0 | 0 |
| `AttributeOption` | `catalog.attribute_options` | 6 | 1 | 1 | 1 | 1 | 0 | 0 |
| `Material` | `catalog.materials` | 17 | 1 | 1 | 2 | 4 | 0 | 0 |
| `Artisan` | `catalog.artisans` | 14 | 1 | 1 | 1 | 2 | 0 | 0 |
| `Category` | `catalog.categories` | 16 | 1 | 2 | 1 | 3 | 0 | 1 |
| `Product` | `catalog.products` | 33 | 1 | 5 | 2 | 6 | 0 | 6 |
| `ProductCategory` | `catalog.product_categories` | 2 | 1 | 2 | 0 | 0 | 0 | 1 |
| `ProductRelation` | `catalog.product_relations` | 3 | 1 | 2 | 0 | 1 | 0 | 0 |
| `Variant` | `catalog.variants` | 14 | 1 | 1 | 1 | 5 | 0 | 1 |
| `ProductAttributeValue` | `catalog.product_attribute_values` | 7 | 1 | 3 | 0 | 1 | 0 | 2 |
| `VariantAttributeValue` | `catalog.variant_attribute_values` | 7 | 1 | 3 | 0 | 1 | 0 | 2 |
| `ProductMedia` | `catalog.product_media` | 7 | 1 | 3 | 1 | 1 | 0 | 1 |
| `CollectionRecord` | `catalog.collections` | 15 | 1 | 1 | 1 | 2 | 0 | 0 |
| `CollectionProduct` | `catalog.collection_products` | 3 | 1 | 2 | 0 | 0 | 0 | 0 |
| `StockLevel` | `inventory.stock_levels` | 5 | 1 | 1 | 0 | 4 | 0 | 1 |
| `StockMovement` | `inventory.stock_movements` | 13 | 1 | 1 | 0 | 8 | 0 | 2 |
| `Receipt` | `inventory.receipts` | 10 | 1 | 0 | 1 | 2 | 0 | 0 |
| `ReceiptLine` | `inventory.receipt_lines` | 5 | 1 | 2 | 1 | 2 | 0 | 0 |
| `Stocktake` | `inventory.stocktakes` | 9 | 1 | 0 | 1 | 1 | 0 | 0 |
| `StocktakeLine` | `inventory.stocktake_lines` | 6 | 1 | 2 | 0 | 2 | 0 | 0 |
| `SalePrice` | `pricing.sale_prices` | 8 | 1 | 1 | 0 | 2 | 1 | 0 |
| `Redirect` | `content.redirects` | 4 | 1 | 0 | 0 | 3 | 0 | 0 |

## Mapping cần SQL tường minh

- EF unique index có annotation `TeaPremium:UniqueConstraint` đại diện UNIQUE trong snapshot; migration tạo **constraint**, không chỉ index. Tránh alternate key ép `materials.sku_code` NOT NULL hoặc khóa việc sửa slug/code/SKU trong persistence. Mọi migration thay các UNIQUE sau này phải dùng ADD/DROP CONSTRAINT tương ứng, không DROP INDEX backing. Test persistence kiểm NULL, false và sửa slug/code.
- `ix_stock_available`: expression index `(on_hand - reserved)`; annotation `TeaPremium:ExpressionIndex`, SQL cố định trong migration.
- `ex_sale_prices_no_overlap`: GiST `(variant_id WITH =, tstzrange(starts_at,ends_at,'[)') WITH &&)`; annotation `TeaPremium:Exclusion`, SQL cố định.
- Không có generator, trigger search, publish, inventory use case hay endpoint.

## Chênh lệch đặt tên

Bảng dưới được lấy từ catalog PostgreSQL 17 của DB tham chiếu chạy DDL authoritative và DB chạy migration. Parity chỉ cho phép đổi tên constraint implicit (và backing index tương ứng), bắt buộc định nghĩa/deferrability/validation giống nhau. Tên constraint explicit và index explicit phải trùng tuyệt đối.

| Bảng | PostgreSQL implicit | EF explicit |
|---|---|---|
| `catalog.artisans` | `artisans_pkey` | `pk_artisans` |
| `catalog.artisans` | `artisans_portrait_media_id_fkey` | `fk_artisans_portrait_media_id` |
| `catalog.artisans` | `artisans_slug_check` | `ck_artisans_slug` |
| `catalog.artisans` | `artisans_slug_key` | `ux_artisans_slug` |
| `catalog.artisans` | `artisans_status_check` | `ck_artisans_status` |
| `catalog.attribute_definitions` | `attribute_definitions_code_check` | `ck_attribute_definitions_code` |
| `catalog.attribute_definitions` | `attribute_definitions_data_type_check` | `ck_attribute_definitions_data_type` |
| `catalog.attribute_definitions` | `attribute_definitions_pkey` | `pk_attribute_definitions` |
| `catalog.attribute_definitions` | `attribute_definitions_product_type_id_code_key` | `ux_attribute_definitions_product_type_id_code` |
| `catalog.attribute_definitions` | `attribute_definitions_product_type_id_fkey` | `fk_attribute_definitions_product_type_id` |
| `catalog.attribute_definitions` | `attribute_definitions_scope_check` | `ck_attribute_definitions_scope` |
| `catalog.attribute_options` | `attribute_options_attribute_id_code_key` | `ux_attribute_options_attribute_id_code` |
| `catalog.attribute_options` | `attribute_options_attribute_id_fkey` | `fk_attribute_options_attribute_id` |
| `catalog.attribute_options` | `attribute_options_code_check` | `ck_attribute_options_code` |
| `catalog.attribute_options` | `attribute_options_pkey` | `pk_attribute_options` |
| `catalog.categories` | `categories_check` | `ck_categories_row` |
| `catalog.categories` | `categories_cover_media_id_fkey` | `fk_categories_cover_media_id` |
| `catalog.categories` | `categories_parent_id_fkey` | `fk_categories_parent_id` |
| `catalog.categories` | `categories_pkey` | `pk_categories` |
| `catalog.categories` | `categories_slug_check` | `ck_categories_slug` |
| `catalog.categories` | `categories_slug_key` | `ux_categories_slug` |
| `catalog.categories` | `categories_status_check` | `ck_categories_status` |
| `catalog.collection_products` | `collection_products_collection_id_fkey` | `fk_collection_products_collection_id` |
| `catalog.collection_products` | `collection_products_pkey` | `pk_collection_products` |
| `catalog.collection_products` | `collection_products_product_id_fkey` | `fk_collection_products_product_id` |
| `catalog.collections` | `collections_cover_media_id_fkey` | `fk_collections_cover_media_id` |
| `catalog.collections` | `collections_pkey` | `pk_collections` |
| `catalog.collections` | `collections_slug_check` | `ck_collections_slug` |
| `catalog.collections` | `collections_slug_key` | `ux_collections_slug` |
| `catalog.collections` | `collections_status_check` | `ck_collections_status` |
| `catalog.materials` | `materials_cover_media_id_fkey` | `fk_materials_cover_media_id` |
| `catalog.materials` | `materials_kind_check` | `ck_materials_kind` |
| `catalog.materials` | `materials_pkey` | `pk_materials` |
| `catalog.materials` | `materials_slug_check` | `ck_materials_slug` |
| `catalog.materials` | `materials_slug_key` | `ux_materials_slug` |
| `catalog.materials` | `materials_status_check` | `ck_materials_status` |
| `catalog.product_attribute_values` | `product_attribute_values_attribute_id_fkey` | `fk_product_attribute_values_attribute_id` |
| `catalog.product_attribute_values` | `product_attribute_values_option_id_fkey` | `fk_product_attribute_values_option_id` |
| `catalog.product_attribute_values` | `product_attribute_values_pkey` | `pk_product_attribute_values` |
| `catalog.product_attribute_values` | `product_attribute_values_product_id_fkey` | `fk_product_attribute_values_product_id` |
| `catalog.product_categories` | `product_categories_category_id_fkey` | `fk_product_categories_category_id` |
| `catalog.product_categories` | `product_categories_pkey` | `pk_product_categories` |
| `catalog.product_categories` | `product_categories_product_id_fkey` | `fk_product_categories_product_id` |
| `catalog.product_media` | `product_media_alt_text_check` | `ck_product_media_alt_text` |
| `catalog.product_media` | `product_media_asset_id_fkey` | `fk_product_media_asset_id` |
| `catalog.product_media` | `product_media_pkey` | `pk_product_media` |
| `catalog.product_media` | `product_media_product_id_asset_id_key` | `ux_product_media_product_id_asset_id` |
| `catalog.product_media` | `product_media_product_id_fkey` | `fk_product_media_product_id` |
| `catalog.product_media` | `product_media_variant_id_fkey` | `fk_product_media_variant_id` |
| `catalog.product_relations` | `product_relations_check` | `ck_product_relations_row` |
| `catalog.product_relations` | `product_relations_pkey` | `pk_product_relations` |
| `catalog.product_relations` | `product_relations_product_id_fkey` | `fk_product_relations_product_id` |
| `catalog.product_relations` | `product_relations_related_product_id_fkey` | `fk_product_relations_related_product_id` |
| `catalog.product_types` | `product_types_code_check` | `ck_product_types_code` |
| `catalog.product_types` | `product_types_code_key` | `ux_product_types_code` |
| `catalog.product_types` | `product_types_pkey` | `pk_product_types` |
| `catalog.products` | `products_artisan_id_fkey` | `fk_products_artisan_id` |
| `catalog.products` | `products_material_id_fkey` | `fk_products_material_id` |
| `catalog.products` | `products_name_check` | `ck_products_name` |
| `catalog.products` | `products_og_media_id_fkey` | `fk_products_og_media_id` |
| `catalog.products` | `products_pkey` | `pk_products` |
| `catalog.products` | `products_primary_category_id_fkey` | `fk_products_primary_category_id` |
| `catalog.products` | `products_product_type_id_fkey` | `fk_products_product_type_id` |
| `catalog.products` | `products_slug_check` | `ck_products_slug` |
| `catalog.products` | `products_slug_key` | `ux_products_slug` |
| `catalog.products` | `products_status_check` | `ck_products_status` |
| `catalog.variant_attribute_values` | `variant_attribute_values_attribute_id_fkey` | `fk_variant_attribute_values_attribute_id` |
| `catalog.variant_attribute_values` | `variant_attribute_values_option_id_fkey` | `fk_variant_attribute_values_option_id` |
| `catalog.variant_attribute_values` | `variant_attribute_values_pkey` | `pk_variant_attribute_values` |
| `catalog.variant_attribute_values` | `variant_attribute_values_variant_id_fkey` | `fk_variant_attribute_values_variant_id` |
| `catalog.variants` | `variants_low_stock_threshold_check` | `ck_variants_low_stock_threshold` |
| `catalog.variants` | `variants_pkey` | `pk_variants` |
| `catalog.variants` | `variants_product_id_fkey` | `fk_variants_product_id` |
| `catalog.variants` | `variants_sku_check` | `ck_variants_sku` |
| `catalog.variants` | `variants_sku_key` | `ux_variants_sku` |
| `catalog.variants` | `variants_status_check` | `ck_variants_status` |
| `catalog.variants` | `variants_weight_g_check` | `ck_variants_weight_g` |
| `content.redirects` | `redirects_check` | `ck_redirects_row` |
| `content.redirects` | `redirects_pkey` | `pk_redirects` |
| `content.redirects` | `redirects_source_path_check` | `ck_redirects_source_path` |
| `content.redirects` | `redirects_target_path_check` | `ck_redirects_target_path` |
| `inventory.receipt_lines` | `receipt_lines_pkey` | `pk_receipt_lines` |
| `inventory.receipt_lines` | `receipt_lines_quantity_check` | `ck_receipt_lines_quantity` |
| `inventory.receipt_lines` | `receipt_lines_receipt_id_fkey` | `fk_receipt_lines_receipt_id` |
| `inventory.receipt_lines` | `receipt_lines_receipt_id_variant_id_key` | `ux_receipt_lines_receipt_id_variant_id` |
| `inventory.receipt_lines` | `receipt_lines_unit_cost_vnd_check` | `ck_receipt_lines_unit_cost_vnd` |
| `inventory.receipt_lines` | `receipt_lines_variant_id_fkey` | `fk_receipt_lines_variant_id` |
| `inventory.receipts` | `receipts_check` | `ck_receipts_row` |
| `inventory.receipts` | `receipts_code_key` | `ux_receipts_code` |
| `inventory.receipts` | `receipts_pkey` | `pk_receipts` |
| `inventory.receipts` | `receipts_status_check` | `ck_receipts_status` |
| `inventory.stock_levels` | `stock_levels_avg_cost_vnd_check` | `ck_stock_levels_avg_cost_vnd` |
| `inventory.stock_levels` | `stock_levels_on_hand_check` | `ck_stock_levels_on_hand` |
| `inventory.stock_levels` | `stock_levels_pkey` | `pk_stock_levels` |
| `inventory.stock_levels` | `stock_levels_reserved_check` | `ck_stock_levels_reserved` |
| `inventory.stock_levels` | `stock_levels_variant_id_fkey` | `fk_stock_levels_variant_id` |
| `inventory.stock_movements` | `stock_movements_actor_type_check` | `ck_stock_movements_actor_type` |
| `inventory.stock_movements` | `stock_movements_movement_type_check` | `ck_stock_movements_movement_type` |
| `inventory.stock_movements` | `stock_movements_on_hand_after_check` | `ck_stock_movements_on_hand_after` |
| `inventory.stock_movements` | `stock_movements_pkey` | `pk_stock_movements` |
| `inventory.stock_movements` | `stock_movements_quantity_check` | `ck_stock_movements_quantity` |
| `inventory.stock_movements` | `stock_movements_ref_type_check` | `ck_stock_movements_ref_type` |
| `inventory.stock_movements` | `stock_movements_unit_cost_vnd_check` | `ck_stock_movements_unit_cost_vnd` |
| `inventory.stock_movements` | `stock_movements_variant_id_fkey` | `fk_stock_movements_variant_id` |
| `inventory.stocktake_lines` | `stocktake_lines_counted_qty_check` | `ck_stocktake_lines_counted_qty` |
| `inventory.stocktake_lines` | `stocktake_lines_pkey` | `pk_stocktake_lines` |
| `inventory.stocktake_lines` | `stocktake_lines_stocktake_id_fkey` | `fk_stocktake_lines_stocktake_id` |
| `inventory.stocktake_lines` | `stocktake_lines_system_qty_at_count_check` | `ck_stocktake_lines_system_qty_at_count` |
| `inventory.stocktake_lines` | `stocktake_lines_variant_id_fkey` | `fk_stocktake_lines_variant_id` |
| `inventory.stocktakes` | `stocktakes_code_key` | `ux_stocktakes_code` |
| `inventory.stocktakes` | `stocktakes_pkey` | `pk_stocktakes` |
| `inventory.stocktakes` | `stocktakes_status_check` | `ck_stocktakes_status` |
| `media.assets` | `assets_mime_type_check` | `ck_assets_mime_type` |
| `media.assets` | `assets_pkey` | `pk_assets` |
| `media.assets` | `assets_purpose_check` | `ck_assets_purpose` |
| `media.assets` | `assets_storage_key_key` | `ux_assets_storage_key` |
| `media.upload_sessions` | `upload_sessions_asset_id_fkey` | `fk_upload_sessions_asset_id` |
| `media.upload_sessions` | `upload_sessions_pkey` | `pk_upload_sessions` |
| `media.upload_sessions` | `upload_sessions_status_check` | `ck_upload_sessions_status` |
| `media.upload_sessions` | `upload_sessions_storage_key_key` | `ux_upload_sessions_storage_key` |
| `pricing.sale_prices` | `sale_prices_check` | `ck_sale_prices_row` |
| `pricing.sale_prices` | `sale_prices_pkey` | `pk_sale_prices` |
| `pricing.sale_prices` | `sale_prices_sale_price_vnd_check` | `ck_sale_prices_sale_price_vnd` |
| `pricing.sale_prices` | `sale_prices_variant_id_fkey` | `fk_sale_prices_variant_id` |
