-- =====================================================================
-- TeaPremium v2 — DDL tham chiếu (PostgreSQL 17)
-- Đây là schema ĐÍCH. EF Core migrations phải sinh ra cấu trúc tương đương
-- (tên bảng/cột/constraint/index giữ nguyên). Bảng ASP.NET Identity (schema iam)
-- do Identity tạo, chỉ ghi chú ở cuối file.
-- Quy ước: tiền = bigint VND; thời gian = timestamptz UTC; id = uuid v7 do app sinh;
-- enum = text + CHECK; concurrency = cột hệ thống xmin (không khai báo trong DDL).
-- A01 owner-approved 18/11/2026: chuẩn bị tp_owner/tp_app/tp_readonly trước DDL.
-- Artifact triển khai: infra/postgres/init-roles.sql (closure #1, chưa tạo ở amendment).
-- Kiểm DDL amendment có thể tạo role fixture chỉ trên PostgreSQL 17 dùng một lần.
-- tp_readonly chỉ dùng backup có kiểm soát, không cấp để truy vấn điều tra tùy ý (03 §4).
-- =====================================================================

CREATE EXTENSION IF NOT EXISTS unaccent;
CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE EXTENSION IF NOT EXISTS btree_gist;

-- Bỏ dấu tiếng Việt (gồm đ→d), IMMUTABLE để dùng được trong index.
CREATE OR REPLACE FUNCTION public.f_unaccent(text) RETURNS text
  LANGUAGE sql IMMUTABLE PARALLEL SAFE STRICT
  AS $$ SELECT lower(public.unaccent('public.unaccent'::regdictionary, $1)) $$;

CREATE SCHEMA platform;
CREATE SCHEMA media;
CREATE SCHEMA catalog;
CREATE SCHEMA inventory;
CREATE SCHEMA pricing;
CREATE SCHEMA crm;
CREATE SCHEMA sales;
CREATE SCHEMA marketing;
CREATE SCHEMA content;
CREATE SCHEMA finance;
CREATE SCHEMA iam;

-- =====================================================================
-- PLATFORM
-- =====================================================================

-- Cấu hình nghiệp vụ dạng key → jsonb. Danh sách key ở 03-database.md §5.
CREATE TABLE platform.settings (
  key            text CONSTRAINT pk_settings PRIMARY KEY CONSTRAINT ck_settings_key_format CHECK (key ~ '^[a-z0-9_.]{3,80}$'),
  value          jsonb NOT NULL,
  updated_at     timestamptz NOT NULL DEFAULT now(),
  updated_by     uuid
);

-- Địa giới hành chính 2 cấp (sau sắp xếp từ 01/07/2025): tỉnh/thành → phường/xã/đặc khu.
CREATE TABLE platform.provinces (
  code           text CONSTRAINT pk_provinces PRIMARY KEY,
  name           text NOT NULL,
  is_active      boolean NOT NULL DEFAULT true,
  sort_order     int NOT NULL DEFAULT 0
);
CREATE TABLE platform.wards (
  code           text CONSTRAINT pk_wards PRIMARY KEY,
  province_code  text NOT NULL CONSTRAINT fk_wards_provinces_province_code REFERENCES platform.provinces(code),
  name           text NOT NULL,
  is_active      boolean NOT NULL DEFAULT true
);
CREATE INDEX ix_wards_province ON platform.wards(province_code);

CREATE TABLE platform.audit_logs (
  id             uuid CONSTRAINT pk_audit_logs PRIMARY KEY,
  occurred_at    timestamptz NOT NULL,
  actor_type     text NOT NULL CONSTRAINT ck_audit_logs_actor_type CHECK (actor_type IN ('staff','customer','system')),
  actor_id       uuid,
  entity_type    text NOT NULL,
  entity_id      uuid,
  entity_key     text,
  action         text NOT NULL CONSTRAINT ck_audit_logs_action CHECK (action IN ('create','update','delete','state_change','access')),
  changes        jsonb NOT NULL DEFAULT '{}'::jsonb,
  correlation_id text,
  CONSTRAINT ck_audit_entity_identity CHECK (
    num_nonnulls(entity_id, entity_key) = 1
    AND (entity_key IS NULL OR entity_key <> '')
  )
);
CREATE INDEX ix_audit_entity_id ON platform.audit_logs(entity_type, entity_id, occurred_at DESC)
  WHERE entity_id IS NOT NULL;
CREATE INDEX ix_audit_entity_key ON platform.audit_logs(entity_type, entity_key, occurred_at DESC)
  WHERE entity_key IS NOT NULL;
CREATE INDEX ix_audit_time ON platform.audit_logs(occurred_at DESC);
CREATE INDEX ix_audit_actor ON platform.audit_logs(actor_id, occurred_at DESC) WHERE actor_id IS NOT NULL;

CREATE TABLE platform.outbox_messages (
  id             uuid CONSTRAINT pk_outbox_messages PRIMARY KEY,
  type           text NOT NULL,
  payload        jsonb NOT NULL,
  occurred_at    timestamptz NOT NULL,
  available_at   timestamptz NOT NULL,
  attempts       int NOT NULL DEFAULT 0,
  status         text NOT NULL DEFAULT 'pending' CONSTRAINT ck_outbox_messages_status CHECK (status IN ('pending','processed','dead')),
  processed_at   timestamptz,
  last_error     text
);
CREATE INDEX ix_outbox_pending ON platform.outbox_messages(available_at) WHERE status = 'pending';

CREATE TABLE platform.idempotency_keys (
  key            uuid NOT NULL,
  scope          text NOT NULL,
  request_hash   text NOT NULL,
  response_status int,
  response_body  jsonb,
  created_at     timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT pk_idempotency_keys PRIMARY KEY (scope, key)
);
CREATE INDEX ix_idem_created ON platform.idempotency_keys(created_at);

CREATE TABLE platform.inbound_webhooks (
  id             uuid CONSTRAINT pk_inbound_webhooks PRIMARY KEY,
  provider       text NOT NULL,
  external_id    text NOT NULL,
  payload        jsonb NOT NULL,
  received_at    timestamptz NOT NULL DEFAULT now(),
  status         text NOT NULL DEFAULT 'received' CONSTRAINT ck_inbound_webhooks_status CHECK (status IN ('received','processed','ignored','failed')),
  processed_at   timestamptz,
  error          text,
  CONSTRAINT ux_inbound_webhooks_provider_external_id UNIQUE (provider, external_id)
);

CREATE TABLE platform.notification_logs (
  id             uuid CONSTRAINT pk_notification_logs PRIMARY KEY,
  channel        text NOT NULL CONSTRAINT ck_notification_logs_channel CHECK (channel IN ('email','telegram','sms','zns')),
  template       text NOT NULL,
  recipient_hash text NOT NULL,          -- SHA-256 của địa chỉ nhận, không lưu PII
  ref_type       text,
  ref_id         uuid,
  status         text NOT NULL CONSTRAINT ck_notification_logs_status CHECK (status IN ('queued','sent','failed')),
  error          text,
  created_at     timestamptz NOT NULL DEFAULT now(),
  sent_at        timestamptz
);
CREATE INDEX ix_notif_ref ON platform.notification_logs(ref_type, ref_id);

CREATE TABLE platform.data_protection_keys (
  id             serial CONSTRAINT pk_data_protection_keys PRIMARY KEY,
  friendly_name  text,
  xml            text
);

-- =====================================================================
-- MEDIA
-- =====================================================================
CREATE TABLE media.assets (
  id             uuid PRIMARY KEY,
  storage_key    text NOT NULL UNIQUE,
  mime_type      text NOT NULL CHECK (mime_type IN ('image/jpeg','image/png','image/webp','image/avif','video/mp4','video/quicktime')),
  byte_size      bigint NOT NULL,
  width          int,
  height         int,
  checksum_sha256 text,
  default_alt    text,
  purpose        text NOT NULL DEFAULT 'catalog' CHECK (purpose IN ('catalog','content','evidence','branding')),
  created_by     uuid,
  created_at     timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT ck_assets_size_by_kind CHECK (
    (mime_type IN ('image/jpeg','image/png','image/webp','image/avif') AND byte_size > 0 AND byte_size <= 15728640) OR
    (purpose = 'evidence' AND mime_type IN ('video/mp4','video/quicktime') AND byte_size > 0 AND byte_size <= 52428800))
);

CREATE TABLE media.upload_sessions (
  id             uuid PRIMARY KEY,
  storage_key    text NOT NULL UNIQUE,
  original_name  text NOT NULL,
  mime_type      text NOT NULL,
  expected_bytes bigint NOT NULL,
  purpose        text NOT NULL,
  status         text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending','completed','expired','failed')),
  created_by     uuid,
  created_at     timestamptz NOT NULL DEFAULT now(),
  expires_at     timestamptz NOT NULL,
  completed_at   timestamptz,
  asset_id       uuid REFERENCES media.assets(id),
  CONSTRAINT ck_upload_size_by_kind CHECK (
    purpose IN ('catalog','content','evidence','branding') AND (
      (mime_type IN ('image/jpeg','image/png','image/webp','image/avif') AND expected_bytes > 0 AND expected_bytes <= 15728640) OR
      (purpose = 'evidence' AND mime_type IN ('video/mp4','video/quicktime') AND expected_bytes > 0 AND expected_bytes <= 52428800)))
);
CREATE INDEX ix_upload_pending ON media.upload_sessions(expires_at) WHERE status = 'pending';

-- =====================================================================
-- CATALOG
-- =====================================================================
CREATE TABLE catalog.product_types (
  id             uuid PRIMARY KEY,
  code           text NOT NULL UNIQUE CHECK (code ~ '^[a-z0-9_]{2,40}$'),
  sku_prefix     text CONSTRAINT ck_product_types_sku_prefix CHECK (sku_prefix ~ '^[A-Z]{2,4}$'),
  name           text NOT NULL,
  description    text,
  google_product_category text,                 -- map cho feed Merchant (BR-SEO-80)
  sort_order     int NOT NULL DEFAULT 0,
  is_active      boolean NOT NULL DEFAULT true,
  created_at     timestamptz NOT NULL DEFAULT now(),
  updated_at     timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE catalog.attribute_definitions (
  id             uuid PRIMARY KEY,
  product_type_id uuid NOT NULL REFERENCES catalog.product_types(id) ON DELETE RESTRICT,
  code           text NOT NULL CHECK (code ~ '^[a-z0-9_]{2,40}$'),
  name           text NOT NULL,
  data_type      text NOT NULL CHECK (data_type IN ('text','number','option','boolean','dimensions')),
  unit           text,
  scope          text NOT NULL DEFAULT 'product' CHECK (scope IN ('product','variant')),
  is_variant_axis boolean NOT NULL DEFAULT false,
  is_required_for_publish boolean NOT NULL DEFAULT false,
  is_filterable  boolean NOT NULL DEFAULT false,
  is_visible     boolean NOT NULL DEFAULT true,
  sort_order     int NOT NULL DEFAULT 0,
  is_active      boolean NOT NULL DEFAULT true,
  UNIQUE (product_type_id, code),
  CONSTRAINT ck_attr_unit CHECK (unit IS NULL OR data_type IN ('number','dimensions')),
  CONSTRAINT ck_attr_axis CHECK (NOT is_variant_axis OR (scope = 'variant' AND data_type IN ('number','option','text')))
);

CREATE TABLE catalog.attribute_options (
  id             uuid PRIMARY KEY,
  attribute_id   uuid NOT NULL REFERENCES catalog.attribute_definitions(id) ON DELETE RESTRICT,
  code           text NOT NULL CHECK (code ~ '^[a-z0-9_]{1,60}$'),
  label          text NOT NULL,
  sort_order     int NOT NULL DEFAULT 0,
  is_active      boolean NOT NULL DEFAULT true,
  UNIQUE (attribute_id, code)
);

CREATE TABLE catalog.materials (
  id             uuid PRIMARY KEY,
  sku_code       text CONSTRAINT ux_materials_sku_code UNIQUE CONSTRAINT ck_materials_sku_code CHECK (sku_code ~ '^[A-Z]{2,3}$' AND sku_code <> 'XX'),
  slug           text NOT NULL UNIQUE CHECK (slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'),
  name           text NOT NULL,
  kind           text NOT NULL CHECK (kind IN ('clay','metal','glass','ceramic','porcelain','wood','stone','other')),
  summary        text,
  description_json jsonb,
  description_html text,
  cover_media_id uuid REFERENCES media.assets(id),
  status         text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft','published','archived')),
  focus_keyword  text,
  seo_title      text, seo_description text, robots_noindex boolean NOT NULL DEFAULT false,
  sort_order     int NOT NULL DEFAULT 0,
  created_at     timestamptz NOT NULL DEFAULT now(),
  updated_at     timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE catalog.artisans (
  id             uuid PRIMARY KEY,
  slug           text NOT NULL UNIQUE CHECK (slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'),
  name           text NOT NULL,                 -- "Chu Bân"
  title          text,                          -- "Nghệ nhân mỹ thuật thủ công"
  bio_json       jsonb,
  bio_html       text,
  portrait_media_id uuid REFERENCES media.assets(id),
  status         text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft','published','archived')),
  focus_keyword  text,
  seo_title      text, seo_description text, robots_noindex boolean NOT NULL DEFAULT false,
  created_at     timestamptz NOT NULL DEFAULT now(),
  updated_at     timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE catalog.categories (
  id             uuid PRIMARY KEY,
  parent_id      uuid REFERENCES catalog.categories(id) ON DELETE RESTRICT,
  slug           text NOT NULL UNIQUE CHECK (slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'),
  name           text NOT NULL,
  description_html text,                        -- đoạn giới thiệu trên lưới sản phẩm (BR-SEO-35)
  body_html      text,                          -- nội dung hướng dẫn dưới lưới
  body_json      jsonb,
  cover_media_id uuid REFERENCES media.assets(id),
  status         text NOT NULL DEFAULT 'hidden' CHECK (status IN ('visible','hidden')),
  sort_order     int NOT NULL DEFAULT 0,
  focus_keyword  text,
  seo_title      text, seo_description text, robots_noindex boolean NOT NULL DEFAULT false,
  created_at     timestamptz NOT NULL DEFAULT now(),
  updated_at     timestamptz NOT NULL DEFAULT now(),
  CHECK (parent_id IS NULL OR parent_id <> id)
);
CREATE INDEX ix_categories_parent ON catalog.categories(parent_id);

CREATE TABLE catalog.products (
  id             uuid PRIMARY KEY,
  sku_base       text NOT NULL CONSTRAINT ux_products_sku_base UNIQUE CONSTRAINT ck_products_sku_base CHECK (sku_base ~ '^[A-Z]{2,4}-[A-Z]{2,3}-[0-9]{4,}$' AND length(sku_base) <= 40),
  product_type_id uuid NOT NULL REFERENCES catalog.product_types(id),
  slug           text NOT NULL UNIQUE CHECK (slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'),
  name           text NOT NULL CHECK (length(name) BETWEEN 2 AND 200),
  subtitle       text,                          -- "Quốc công trọng khí khuyến nghị"
  short_description text,
  description_json jsonb,
  description_html text,
  care_html      text,
  description_word_count int NOT NULL DEFAULT 0 CONSTRAINT ck_products_word_count CHECK (description_word_count >= 0),
  primary_category_id uuid REFERENCES catalog.categories(id),
  material_id    uuid REFERENCES catalog.materials(id),
  artisan_id     uuid REFERENCES catalog.artisans(id),
  origin_note    text,                          -- chỉ ghi khi có bằng chứng
  status         text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft','active','archived')),
  published_at   timestamptz,
  first_published_at timestamptz,               -- quyết định 301 hay 404 khi lưu trữ (BR-SEO-22)
  archived_redirect_path text,
  sold_out_at    timestamptz,                   -- BR-SEO-21
  seo_keep_after_sold boolean NOT NULL DEFAULT false,
  focus_keyword  text,
  is_featured    boolean NOT NULL DEFAULT false,
  search_text    text NOT NULL DEFAULT '',      -- app duy trì (BR-CAT-10)
  seo_title      text, seo_description text, canonical_url text,
  robots_noindex boolean NOT NULL DEFAULT false, -- cờ thủ công; thin_description suy từ word_count (BR-SEO-34)
  og_media_id    uuid REFERENCES media.assets(id),
  created_by     uuid, updated_by uuid,
  created_at     timestamptz NOT NULL DEFAULT now(),
  updated_at     timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT ck_products_published CHECK (status <> 'active' OR published_at IS NOT NULL)
);
CREATE INDEX ix_products_status_pub ON catalog.products(status, published_at DESC);
CREATE INDEX ix_products_type ON catalog.products(product_type_id);
CREATE INDEX ix_products_category ON catalog.products(primary_category_id);
CREATE INDEX ix_products_material ON catalog.products(material_id);
CREATE INDEX ix_products_artisan ON catalog.products(artisan_id);
CREATE INDEX ix_products_search ON catalog.products USING gin (search_text gin_trgm_ops);
CREATE SEQUENCE catalog.sku_number_seq START 1; -- app nextval một lần; không MAX+1 (BR-CAT-14)

CREATE TABLE catalog.product_categories (
  product_id     uuid NOT NULL REFERENCES catalog.products(id) ON DELETE CASCADE,
  category_id    uuid NOT NULL REFERENCES catalog.categories(id) ON DELETE RESTRICT,
  PRIMARY KEY (product_id, category_id)
);
CREATE INDEX ix_product_categories_cat ON catalog.product_categories(category_id);

CREATE TABLE catalog.product_relations (
  product_id     uuid NOT NULL REFERENCES catalog.products(id) ON DELETE CASCADE,
  related_product_id uuid NOT NULL REFERENCES catalog.products(id) ON DELETE CASCADE,
  sort_order     int NOT NULL DEFAULT 0,
  PRIMARY KEY (product_id, related_product_id),
  CHECK (product_id <> related_product_id)
);

CREATE TABLE catalog.variants (
  id             uuid PRIMARY KEY,
  product_id     uuid NOT NULL REFERENCES catalog.products(id) ON DELETE RESTRICT,
  sku            text NOT NULL UNIQUE CHECK (sku ~ '^[A-Z0-9][A-Z0-9-]{1,39}$'),
  name           text,                          -- "245ml"; NULL nếu product chỉ có 1 variant
  position       int NOT NULL DEFAULT 0,
  price_vnd      bigint CONSTRAINT ck_variants_price CHECK (price_vnd IS NULL OR (price_vnd > 0 AND price_vnd <= 10000000000)), -- BR-CAT-04/15 app: active/publish/kích hoạt
  is_unique_piece boolean NOT NULL DEFAULT false,
  weight_g       int CHECK (weight_g IS NULL OR weight_g > 0),
  package_dims_cm jsonb,                        -- {"l":20,"w":15,"h":12}
  barcode        text,
  low_stock_threshold int NOT NULL DEFAULT 0 CHECK (low_stock_threshold >= 0),
  status         text NOT NULL DEFAULT 'active' CHECK (status IN ('active','inactive')),
  created_at     timestamptz NOT NULL DEFAULT now(),
  updated_at     timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_variants_product ON catalog.variants(product_id, position);

CREATE TABLE catalog.product_attribute_values (
  product_id     uuid NOT NULL REFERENCES catalog.products(id) ON DELETE CASCADE,
  attribute_id   uuid NOT NULL REFERENCES catalog.attribute_definitions(id) ON DELETE RESTRICT,
  value_text     text,
  value_number   numeric(12,2),
  option_id      uuid REFERENCES catalog.attribute_options(id),
  value_bool     boolean,
  value_json     jsonb,                         -- dimensions: {"d":14.3,"h":7.6,"mouth":4.6,"l":null,"w":null}
  PRIMARY KEY (product_id, attribute_id),
  CONSTRAINT ck_pav_one_value CHECK (num_nonnulls(value_text, value_number, option_id, value_bool, value_json) = 1)
);
CREATE INDEX ix_pav_filter_option ON catalog.product_attribute_values(attribute_id, option_id) WHERE option_id IS NOT NULL;
CREATE INDEX ix_pav_filter_number ON catalog.product_attribute_values(attribute_id, value_number) WHERE value_number IS NOT NULL;

CREATE TABLE catalog.variant_attribute_values (
  variant_id     uuid NOT NULL REFERENCES catalog.variants(id) ON DELETE CASCADE,
  attribute_id   uuid NOT NULL REFERENCES catalog.attribute_definitions(id) ON DELETE RESTRICT,
  value_text     text,
  value_number   numeric(12,2),
  option_id      uuid REFERENCES catalog.attribute_options(id),
  value_bool     boolean,
  value_json     jsonb,
  PRIMARY KEY (variant_id, attribute_id),
  CONSTRAINT ck_vav_one_value CHECK (num_nonnulls(value_text, value_number, option_id, value_bool, value_json) = 1)
);
CREATE INDEX ix_vav_filter_option ON catalog.variant_attribute_values(attribute_id, option_id) WHERE option_id IS NOT NULL;
CREATE INDEX ix_vav_filter_number ON catalog.variant_attribute_values(attribute_id, value_number) WHERE value_number IS NOT NULL;

CREATE TABLE catalog.product_media (
  id             uuid PRIMARY KEY,
  product_id     uuid NOT NULL REFERENCES catalog.products(id) ON DELETE CASCADE,
  variant_id     uuid REFERENCES catalog.variants(id) ON DELETE SET NULL,
  asset_id       uuid NOT NULL REFERENCES media.assets(id) ON DELETE RESTRICT,
  alt_text       text NOT NULL CHECK (length(trim(alt_text)) BETWEEN 3 AND 200),
  sort_order     int NOT NULL DEFAULT 0,
  is_cover       boolean NOT NULL DEFAULT false,
  UNIQUE (product_id, asset_id)
);
CREATE UNIQUE INDEX ux_product_media_cover ON catalog.product_media(product_id) WHERE is_cover;

CREATE TABLE catalog.collections (
  id             uuid PRIMARY KEY,
  slug           text NOT NULL UNIQUE CHECK (slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'),
  name           text NOT NULL,
  description_html text,
  body_html      text,
  body_json      jsonb,
  cover_media_id uuid REFERENCES media.assets(id),
  status         text NOT NULL DEFAULT 'hidden' CHECK (status IN ('visible','hidden')),
  sort_order     int NOT NULL DEFAULT 0,
  focus_keyword  text,
  seo_title      text, seo_description text, robots_noindex boolean NOT NULL DEFAULT false,
  created_at     timestamptz NOT NULL DEFAULT now(),
  updated_at     timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE catalog.collection_products (
  collection_id  uuid NOT NULL REFERENCES catalog.collections(id) ON DELETE CASCADE,
  product_id     uuid NOT NULL REFERENCES catalog.products(id) ON DELETE CASCADE,
  sort_order     int NOT NULL DEFAULT 0,
  PRIMARY KEY (collection_id, product_id)
);

-- =====================================================================
-- INVENTORY
-- =====================================================================
CREATE TABLE inventory.stock_levels (
  variant_id     uuid PRIMARY KEY REFERENCES catalog.variants(id) ON DELETE RESTRICT,
  on_hand        int NOT NULL DEFAULT 0 CHECK (on_hand >= 0),
  reserved       int NOT NULL DEFAULT 0 CHECK (reserved >= 0),
  avg_cost_vnd   bigint CHECK (avg_cost_vnd IS NULL OR avg_cost_vnd >= 0),   -- NULL = chưa biết giá vốn
  updated_at     timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT ck_stock_reserved CHECK (reserved <= on_hand)
);
CREATE INDEX ix_stock_available ON inventory.stock_levels((on_hand - reserved));

-- Sổ kho append-only. Σ quantity theo variant = stock_levels.on_hand (BR-INV-01).
CREATE TABLE inventory.stock_movements (
  id             uuid PRIMARY KEY,
  variant_id     uuid NOT NULL REFERENCES catalog.variants(id) ON DELETE RESTRICT,
  movement_type  text NOT NULL CHECK (movement_type IN
                   ('opening','receipt','sale','return_restock','adjustment','stocktake','damage','replacement_out')),
  quantity       int NOT NULL CHECK (quantity <> 0),   -- dương = vào kho, âm = ra kho
  unit_cost_vnd  bigint CHECK (unit_cost_vnd IS NULL OR unit_cost_vnd >= 0),
  on_hand_after  int NOT NULL CHECK (on_hand_after >= 0),
  ref_type       text CHECK (ref_type IN ('receipt','order','return','adjustment','stocktake','import')),
  ref_id         uuid,
  reason         text,
  actor_type     text NOT NULL CHECK (actor_type IN ('staff','system')),
  actor_id       uuid,
  occurred_at    timestamptz NOT NULL,
  created_at     timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT ck_mv_direction CHECK (
    (movement_type IN ('opening','receipt','return_restock') AND quantity > 0) OR
    (movement_type IN ('sale','damage','replacement_out') AND quantity < 0) OR
    (movement_type IN ('adjustment','stocktake'))),
  CONSTRAINT ck_mv_reason CHECK (movement_type NOT IN ('adjustment','damage') OR length(trim(coalesce(reason,''))) >= 3)
);
CREATE INDEX ix_mv_variant_time ON inventory.stock_movements(variant_id, occurred_at DESC);
CREATE INDEX ix_mv_ref ON inventory.stock_movements(ref_type, ref_id);

CREATE TABLE inventory.receipts (
  id             uuid PRIMARY KEY,
  code           text NOT NULL UNIQUE,          -- PN-261116-001
  supplier_name  text,
  note           text,
  status         text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft','posted','cancelled')),
  received_at    timestamptz,
  posted_at      timestamptz,
  posted_by      uuid,
  created_by     uuid NOT NULL,
  created_at     timestamptz NOT NULL DEFAULT now(),
  CHECK ((status = 'posted') = (posted_at IS NOT NULL))
);
CREATE TABLE inventory.receipt_lines (
  id             uuid PRIMARY KEY,
  receipt_id     uuid NOT NULL REFERENCES inventory.receipts(id) ON DELETE CASCADE,
  variant_id     uuid NOT NULL REFERENCES catalog.variants(id),
  quantity       int NOT NULL CHECK (quantity > 0),
  unit_cost_vnd  bigint NOT NULL CHECK (unit_cost_vnd >= 0),
  UNIQUE (receipt_id, variant_id)
);

CREATE TABLE inventory.stocktakes (
  id             uuid PRIMARY KEY,
  code           text NOT NULL UNIQUE,
  note           text,
  status         text NOT NULL DEFAULT 'counting' CHECK (status IN ('counting','submitted','approved','cancelled')),
  created_by     uuid NOT NULL,
  created_at     timestamptz NOT NULL DEFAULT now(),
  submitted_at   timestamptz,
  approved_by    uuid,
  approved_at    timestamptz
);
CREATE TABLE inventory.stocktake_lines (
  stocktake_id   uuid NOT NULL REFERENCES inventory.stocktakes(id) ON DELETE CASCADE,
  variant_id     uuid NOT NULL REFERENCES catalog.variants(id),
  counted_qty    int NOT NULL CHECK (counted_qty >= 0),
  system_qty_at_count int NOT NULL CHECK (system_qty_at_count >= 0),
  counted_at     timestamptz NOT NULL,
  counted_by     uuid NOT NULL,
  PRIMARY KEY (stocktake_id, variant_id)
);

CREATE TABLE inventory.reservations (
  id             uuid PRIMARY KEY,
  variant_id     uuid NOT NULL REFERENCES catalog.variants(id),
  order_line_id  uuid NOT NULL,                 -- FK thêm sau khi tạo sales.order_lines
  quantity       int NOT NULL CHECK (quantity > 0),
  status         text NOT NULL DEFAULT 'active' CHECK (status IN ('active','consumed','released')),
  created_at     timestamptz NOT NULL DEFAULT now(),
  closed_at      timestamptz,
  close_reason   text,
  CHECK ((status = 'active') = (closed_at IS NULL))
);
CREATE UNIQUE INDEX ux_reservation_active_line ON inventory.reservations(order_line_id) WHERE status = 'active';
CREATE INDEX ix_reservation_variant_active ON inventory.reservations(variant_id) WHERE status = 'active';

-- =====================================================================
-- PRICING
-- =====================================================================
CREATE TABLE pricing.sale_prices (
  id             uuid PRIMARY KEY,
  variant_id     uuid NOT NULL REFERENCES catalog.variants(id) ON DELETE CASCADE,
  sale_price_vnd bigint NOT NULL CHECK (sale_price_vnd > 0),
  starts_at      timestamptz NOT NULL,
  ends_at        timestamptz NOT NULL,
  note           text,
  created_by     uuid NOT NULL,
  created_at     timestamptz NOT NULL DEFAULT now(),
  CHECK (ends_at > starts_at),
  CONSTRAINT ex_sale_prices_no_overlap EXCLUDE USING gist
    (variant_id WITH =, tstzrange(starts_at, ends_at, '[)') WITH &&)
);

CREATE TABLE pricing.vouchers (
  id             uuid PRIMARY KEY,
  code           text NOT NULL UNIQUE CHECK (code ~ '^[A-Z0-9]{4,20}$'),
  kind           text NOT NULL CHECK (kind IN ('referral_reward','manual')),
  discount_type  text NOT NULL CHECK (discount_type IN ('fixed','percent')),
  discount_value bigint NOT NULL CHECK (discount_value > 0),
  max_discount_vnd bigint CHECK (max_discount_vnd IS NULL OR max_discount_vnd > 0),
  min_order_vnd  bigint NOT NULL DEFAULT 0 CHECK (min_order_vnd >= 0),
  starts_at      timestamptz NOT NULL,
  ends_at        timestamptz NOT NULL,
  usage_limit    int CHECK (usage_limit IS NULL OR usage_limit > 0),
  per_customer_limit int NOT NULL DEFAULT 1 CHECK (per_customer_limit > 0),
  bound_customer_id uuid,                       -- FK thêm sau crm.customers
  used_count     int NOT NULL DEFAULT 0 CHECK (used_count >= 0),
  status         text NOT NULL DEFAULT 'active' CHECK (status IN ('active','disabled')),
  created_by     uuid,
  created_at     timestamptz NOT NULL DEFAULT now(),
  CHECK (ends_at > starts_at),
  CHECK (discount_type <> 'percent' OR discount_value BETWEEN 1 AND 50),
  CHECK (usage_limit IS NULL OR used_count <= usage_limit)
);

-- =====================================================================
-- CRM
-- =====================================================================
CREATE SEQUENCE crm.customer_code_seq START 1000;

CREATE TABLE crm.customers (
  id             uuid PRIMARY KEY,
  code           text NOT NULL UNIQUE,          -- KH001000
  full_name      text NOT NULL CHECK (length(trim(full_name)) >= 1),
  phone_e164     text CHECK (phone_e164 ~ '^\+84[0-9]{9}$'),
  email          text CHECK (email IS NULL OR email ~ '^[^@\s]+@[^@\s]+\.[^@\s]+$'),
  zalo_name      text,
  customer_group text NOT NULL DEFAULT 'retail' CHECK (customer_group IN ('retail','wholesale','reseller')),
  is_vip         boolean NOT NULL DEFAULT false,
  origin         text NOT NULL CHECK (origin IN ('legacy_import','web','admin')),
  acquisition_source text NOT NULL DEFAULT 'unknown' CHECK (acquisition_source IN
                   ('unknown','legacy','google_organic','google_ads','facebook','zalo_personal','zalo_oa',
                    'referral','direct','offline','other')),
  referred_by_customer_id uuid REFERENCES crm.customers(id),
  contact_status text NOT NULL DEFAULT 'active' CHECK (contact_status IN ('active','do_not_contact','erased')),
  note_summary   text,
  had_legacy_purchase boolean NOT NULL DEFAULT false,  -- đã mua trước khi có hệ thống
  first_delivered_at timestamptz,
  last_delivered_at  timestamptz,
  delivered_orders_count int NOT NULL DEFAULT 0 CHECK (delivered_orders_count >= 0),
  lifetime_value_vnd bigint NOT NULL DEFAULT 0 CHECK (lifetime_value_vnd >= 0),
  last_contacted_at  timestamptz,
  search_text    text NOT NULL DEFAULT '',
  merged_into_customer_id uuid REFERENCES crm.customers(id),
  erased_at      timestamptz,
  created_by     uuid,
  created_at     timestamptz NOT NULL DEFAULT now(),
  updated_at     timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT ck_customer_contact CHECK (contact_status = 'erased' OR phone_e164 IS NOT NULL OR zalo_name IS NOT NULL),
  CONSTRAINT ck_customer_not_self_ref CHECK (referred_by_customer_id IS NULL OR referred_by_customer_id <> id)
);
CREATE UNIQUE INDEX ux_customers_phone ON crm.customers(phone_e164)
  WHERE phone_e164 IS NOT NULL AND merged_into_customer_id IS NULL;
CREATE INDEX ix_customers_search ON crm.customers USING gin (search_text gin_trgm_ops);
CREATE INDEX ix_customers_last_delivered ON crm.customers(last_delivered_at);
CREATE INDEX ix_customers_group ON crm.customers(customer_group);

ALTER TABLE pricing.vouchers
  ADD CONSTRAINT fk_vouchers_customer FOREIGN KEY (bound_customer_id) REFERENCES crm.customers(id);

CREATE TABLE crm.tags (
  id             uuid PRIMARY KEY,
  name           text NOT NULL UNIQUE,
  color          text
);
CREATE TABLE crm.customer_tags (
  customer_id    uuid NOT NULL REFERENCES crm.customers(id) ON DELETE CASCADE,
  tag_id         uuid NOT NULL REFERENCES crm.tags(id) ON DELETE CASCADE,
  PRIMARY KEY (customer_id, tag_id)
);
CREATE INDEX ix_customer_tags_tag ON crm.customer_tags(tag_id);

CREATE TABLE crm.customer_addresses (
  id             uuid PRIMARY KEY,
  customer_id    uuid NOT NULL REFERENCES crm.customers(id) ON DELETE CASCADE,
  label          text,
  receiver_name  text NOT NULL,
  receiver_phone text NOT NULL CHECK (receiver_phone ~ '^\+84[0-9]{9}$'),
  province_code  text NOT NULL REFERENCES platform.provinces(code),
  ward_code      text REFERENCES platform.wards(code),
  ward_name_text text,                          -- dự phòng khi mã phường không khớp
  address_line   text NOT NULL,
  is_default     boolean NOT NULL DEFAULT false,
  created_at     timestamptz NOT NULL DEFAULT now(),
  updated_at     timestamptz NOT NULL DEFAULT now(),
  CHECK (ward_code IS NOT NULL OR ward_name_text IS NOT NULL)
);
CREATE UNIQUE INDEX ux_customer_default_address ON crm.customer_addresses(customer_id) WHERE is_default;

CREATE TABLE crm.customer_accounts (
  id             uuid PRIMARY KEY,
  customer_id    uuid NOT NULL UNIQUE REFERENCES crm.customers(id) ON DELETE RESTRICT,
  login_phone    text UNIQUE CHECK (login_phone ~ '^\+84[0-9]{9}$'),   -- NULL chỉ khi tài khoản bị xóa dữ liệu (BR-CRM-11)
  password_hash  text,                          -- NULL nếu chỉ đăng nhập Google/Zalo
  email          text,
  email_verified_at timestamptz,
  phone_verified_at timestamptz,
  history_unlocked_at timestamptz,              -- BR-IAM-04
  status         text NOT NULL DEFAULT 'active' CHECK (status IN ('active','locked','disabled')),
  failed_login_count int NOT NULL DEFAULT 0,
  lockout_end    timestamptz,
  security_stamp text NOT NULL,
  created_at     timestamptz NOT NULL DEFAULT now(),
  last_login_at  timestamptz,
  CONSTRAINT ck_account_login_phone CHECK (status = 'disabled' OR login_phone IS NOT NULL)
);
CREATE UNIQUE INDEX ux_account_email ON crm.customer_accounts(lower(email)) WHERE email IS NOT NULL;

CREATE TABLE crm.customer_logins (
  provider       text NOT NULL CHECK (provider IN ('google','zalo')),
  provider_key   text NOT NULL,
  account_id     uuid NOT NULL REFERENCES crm.customer_accounts(id) ON DELETE CASCADE,
  email          text,
  created_at     timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (provider, provider_key)
);

CREATE TABLE crm.interactions (
  id             uuid PRIMARY KEY,
  customer_id    uuid NOT NULL REFERENCES crm.customers(id) ON DELETE CASCADE,
  staff_id       uuid NOT NULL,
  channel        text NOT NULL CHECK (channel IN ('zalo_personal','zalo_oa','phone','facebook','in_person','email','web','other')),
  kind           text NOT NULL CHECK (kind IN ('inquiry','consultation','quote','follow_up','complaint','note','campaign_message')),
  content        text NOT NULL CHECK (length(content) BETWEEN 1 AND 4000),
  occurred_at    timestamptz NOT NULL,
  order_id       uuid,                          -- FK thêm sau sales.orders
  campaign_id    uuid,                          -- FK thêm sau marketing.campaigns
  follow_up_at   timestamptz,
  follow_up_done_at timestamptz,
  follow_up_result text,
  created_at     timestamptz NOT NULL DEFAULT now(),
  CHECK (follow_up_done_at IS NULL OR follow_up_at IS NOT NULL)
);
CREATE INDEX ix_interactions_customer ON crm.interactions(customer_id, occurred_at DESC);
CREATE INDEX ix_interactions_follow_up ON crm.interactions(follow_up_at)
  WHERE follow_up_at IS NOT NULL AND follow_up_done_at IS NULL;

CREATE TABLE crm.customer_interests (
  id             uuid PRIMARY KEY,
  customer_id    uuid NOT NULL REFERENCES crm.customers(id) ON DELETE CASCADE,
  interest_type  text NOT NULL CHECK (interest_type IN ('material','artisan','product_type','category','price_band')),
  ref_id         uuid,                          -- material/artisan/type/category id
  price_band     text CHECK (price_band IN ('under_1m','1m_3m','3m_10m','over_10m')),
  source         text NOT NULL CHECK (source IN ('order','manual')),
  score          int NOT NULL DEFAULT 1 CHECK (score > 0),
  updated_at     timestamptz NOT NULL DEFAULT now(),
  CHECK ((interest_type = 'price_band') = (price_band IS NOT NULL)),
  CHECK ((interest_type = 'price_band') = (ref_id IS NULL))
);
CREATE UNIQUE INDEX ux_customer_interest ON crm.customer_interests
  (customer_id, interest_type, coalesce(ref_id, '00000000-0000-0000-0000-000000000000'::uuid), coalesce(price_band, ''), source);
CREATE INDEX ix_interest_lookup ON crm.customer_interests(interest_type, ref_id);

CREATE TABLE crm.consents (
  id             uuid PRIMARY KEY,
  customer_id    uuid NOT NULL REFERENCES crm.customers(id) ON DELETE CASCADE,
  channel        text NOT NULL CHECK (channel IN ('zalo_personal','zalo_oa','sms','email','phone')),
  purpose        text NOT NULL CHECK (purpose IN ('marketing_new_products','marketing_promotions','customer_care')),
  status         text NOT NULL CHECK (status IN ('granted','revoked')),
  source         text NOT NULL CHECK (source IN ('checkout','account','staff','import','link')),
  evidence       text NOT NULL CHECK (length(trim(evidence)) >= 3),
  captured_at    timestamptz NOT NULL,
  captured_by    uuid,
  UNIQUE (customer_id, channel, purpose)
);

CREATE TABLE crm.data_requests (
  id             uuid PRIMARY KEY,
  customer_id    uuid NOT NULL REFERENCES crm.customers(id),
  request_type   text NOT NULL CHECK (request_type IN ('export','erase','correct','withdraw_consent')),
  status         text NOT NULL DEFAULT 'open' CHECK (status IN ('open','completed','rejected')),
  channel        text NOT NULL,
  note           text,
  requested_at   timestamptz NOT NULL,
  completed_at   timestamptz,
  handled_by     uuid
);

-- =====================================================================
-- MARKETING
-- =====================================================================
CREATE TABLE marketing.campaigns (
  id             uuid PRIMARY KEY,
  code           text NOT NULL UNIQUE CHECK (code ~ '^[a-z0-9-]{3,40}$'),
  name           text NOT NULL,
  kind           text NOT NULL CHECK (kind IN ('new_arrival','promotion','care','reactivation','content','other')),
  channel        text NOT NULL CHECK (channel IN ('zalo_personal','zalo_oa','facebook','google_ads','sms','email','offline','other')),
  status         text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft','active','completed','cancelled')),
  message_template text,
  landing_path   text CHECK (landing_path IS NULL OR landing_path ~ '^/'),
  starts_at      timestamptz,
  ends_at        timestamptz,
  budget_vnd     bigint CHECK (budget_vnd IS NULL OR budget_vnd >= 0),
  created_by     uuid NOT NULL,
  created_at     timestamptz NOT NULL DEFAULT now(),
  updated_at     timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE marketing.campaign_products (
  campaign_id    uuid NOT NULL REFERENCES marketing.campaigns(id) ON DELETE CASCADE,
  product_id     uuid NOT NULL REFERENCES catalog.products(id),
  PRIMARY KEY (campaign_id, product_id)
);

CREATE TABLE marketing.tracking_links (
  id             uuid PRIMARY KEY,
  code           text NOT NULL UNIQUE CHECK (code ~ '^[A-Za-z0-9]{6,12}$'),
  target_path    text NOT NULL CHECK (target_path ~ '^/[^/]'  OR target_path = '/'),
  campaign_id    uuid REFERENCES marketing.campaigns(id),
  customer_id    uuid REFERENCES crm.customers(id),
  channel        text,
  label          text,
  click_count    int NOT NULL DEFAULT 0,
  created_by     uuid,
  created_at     timestamptz NOT NULL DEFAULT now(),
  expires_at     timestamptz
);
CREATE INDEX ix_links_campaign ON marketing.tracking_links(campaign_id);

CREATE TABLE marketing.link_clicks (
  id             uuid PRIMARY KEY,
  link_id        uuid NOT NULL REFERENCES marketing.tracking_links(id) ON DELETE CASCADE,
  visitor_id     uuid NOT NULL,                 -- cookie ngẫu nhiên first-party, không phải PII
  clicked_at     timestamptz NOT NULL DEFAULT now(),
  referrer_host  text,
  is_bot         boolean NOT NULL DEFAULT false
);
CREATE INDEX ix_clicks_link_time ON marketing.link_clicks(link_id, clicked_at);

CREATE TABLE marketing.campaign_recipients (
  id             uuid PRIMARY KEY,
  campaign_id    uuid NOT NULL REFERENCES marketing.campaigns(id) ON DELETE CASCADE,
  customer_id    uuid NOT NULL REFERENCES crm.customers(id),
  tracking_link_id uuid NOT NULL UNIQUE REFERENCES marketing.tracking_links(id),
  status         text NOT NULL DEFAULT 'selected' CHECK (status IN ('selected','sent','skipped')),
  sent_at        timestamptz,
  sent_by        uuid,
  skip_reason    text,
  UNIQUE (campaign_id, customer_id),
  CHECK ((status = 'sent') = (sent_at IS NOT NULL))
);

CREATE TABLE marketing.referral_codes (
  customer_id    uuid PRIMARY KEY REFERENCES crm.customers(id) ON DELETE CASCADE,
  code           text NOT NULL UNIQUE CHECK (code ~ '^[A-Z0-9]{6,10}$'),
  is_active      boolean NOT NULL DEFAULT true,
  created_at     timestamptz NOT NULL DEFAULT now()
);

-- =====================================================================
-- SALES (giỏ, đơn, thanh toán, giao hàng, đổi trả)
-- =====================================================================
CREATE TABLE sales.carts (
  id             uuid PRIMARY KEY,
  token_hash     text NOT NULL UNIQUE,          -- SHA-256 của token trong cookie
  customer_account_id uuid REFERENCES crm.customer_accounts(id) ON DELETE CASCADE,
  created_at     timestamptz NOT NULL DEFAULT now(),
  updated_at     timestamptz NOT NULL DEFAULT now(),
  expires_at     timestamptz NOT NULL
);
CREATE UNIQUE INDEX ux_cart_account ON sales.carts(customer_account_id) WHERE customer_account_id IS NOT NULL;
CREATE INDEX ix_cart_expires ON sales.carts(expires_at);

CREATE TABLE sales.cart_items (
  cart_id        uuid NOT NULL REFERENCES sales.carts(id) ON DELETE CASCADE,
  variant_id     uuid NOT NULL REFERENCES catalog.variants(id) ON DELETE CASCADE,
  quantity       int NOT NULL CHECK (quantity BETWEEN 1 AND 99),
  added_at       timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (cart_id, variant_id)
);

CREATE TABLE sales.orders (
  id             uuid PRIMARY KEY,
  code           text NOT NULL UNIQUE CHECK (code ~ '^TP[0-9]{6}[A-Z0-9]{4}$'),   -- TP261116K7Q2
  origin         text NOT NULL CHECK (origin IN ('web','admin')),
  sales_channel  text NOT NULL CHECK (sales_channel IN ('web','zalo_personal','zalo_oa','facebook','phone','in_person','other')),
  customer_id    uuid NOT NULL REFERENCES crm.customers(id),
  customer_account_id uuid REFERENCES crm.customer_accounts(id),
  status         text NOT NULL CHECK (status IN ('pending','confirmed','shipping','delivered','delivery_failed','cancelled')),
  payment_method text NOT NULL CHECK (payment_method IN ('cod','bank_transfer')),
  payment_status text NOT NULL DEFAULT 'unpaid' CHECK (payment_status IN
                   ('unpaid','partially_paid','paid','partially_refunded','refunded')),
  payment_due_at timestamptz,
  -- người đặt (snapshot)
  contact_name   text NOT NULL,
  contact_phone  text NOT NULL CHECK (contact_phone ~ '^\+84[0-9]{9}$'),
  contact_email  text,
  -- giao tới (snapshot)
  receiver_name  text NOT NULL,
  receiver_phone text NOT NULL CHECK (receiver_phone ~ '^\+84[0-9]{9}$'),
  province_code  text NOT NULL,
  province_name  text NOT NULL,
  ward_code      text,
  ward_name      text NOT NULL,
  address_line   text NOT NULL,
  -- tiền
  subtotal_vnd   bigint NOT NULL CHECK (subtotal_vnd >= 0),
  discount_vnd   bigint NOT NULL DEFAULT 0 CHECK (discount_vnd >= 0),
  shipping_fee_vnd bigint NOT NULL DEFAULT 0 CHECK (shipping_fee_vnd >= 0),
  total_vnd      bigint NOT NULL CHECK (total_vnd >= 0),
  discount_note  text,
  voucher_id     uuid REFERENCES pricing.vouchers(id),
  referral_code  text,
  -- phân bổ nguồn (snapshot lúc đặt)
  acquisition_source text NOT NULL,
  is_first_purchase boolean NOT NULL,
  tracking_link_id uuid REFERENCES marketing.tracking_links(id),
  campaign_id    uuid REFERENCES marketing.campaigns(id),
  utm            jsonb,
  -- ghi chú
  customer_note  text,
  internal_note  text,
  status_reason  text,
  -- mốc thời gian
  placed_at      timestamptz NOT NULL,
  confirmed_at   timestamptz,
  shipped_at     timestamptz,
  delivered_at   timestamptz,
  failed_at      timestamptz,
  cancelled_at   timestamptz,
  -- hóa đơn điện tử (DEC-18)
  invoice_number text,
  invoice_issued_at timestamptz,
  is_backfilled  boolean NOT NULL DEFAULT false,
  created_by_staff_id uuid,
  created_at     timestamptz NOT NULL DEFAULT now(),
  updated_at     timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT ck_order_total CHECK (total_vnd = subtotal_vnd - discount_vnd + shipping_fee_vnd),
  CONSTRAINT ck_order_discount CHECK (discount_vnd <= subtotal_vnd),
  CONSTRAINT ck_order_delivered CHECK ((status = 'delivered') = (delivered_at IS NOT NULL)),
  CONSTRAINT ck_order_cancelled CHECK ((status = 'cancelled') = (cancelled_at IS NOT NULL)),
  CONSTRAINT ck_order_web_channel CHECK (origin <> 'web' OR sales_channel = 'web'),
  CONSTRAINT ck_order_due CHECK (payment_method <> 'bank_transfer' OR origin <> 'web' OR payment_due_at IS NOT NULL)
);
CREATE INDEX ix_orders_status_placed ON sales.orders(status, placed_at DESC);
CREATE INDEX ix_orders_customer ON sales.orders(customer_id, placed_at DESC);
CREATE INDEX ix_orders_delivered ON sales.orders(delivered_at) WHERE delivered_at IS NOT NULL;
CREATE INDEX ix_orders_payment ON sales.orders(payment_status, status);
CREATE INDEX ix_orders_due ON sales.orders(payment_due_at)
  WHERE status = 'pending' AND payment_method = 'bank_transfer' AND payment_status = 'unpaid';
CREATE INDEX ix_orders_campaign ON sales.orders(campaign_id) WHERE campaign_id IS NOT NULL;
CREATE INDEX ix_orders_contact_phone ON sales.orders(contact_phone);

ALTER TABLE crm.interactions
  ADD CONSTRAINT fk_interactions_order FOREIGN KEY (order_id) REFERENCES sales.orders(id) ON DELETE SET NULL,
  ADD CONSTRAINT fk_interactions_campaign FOREIGN KEY (campaign_id) REFERENCES marketing.campaigns(id) ON DELETE SET NULL;

CREATE TABLE sales.order_lines (
  id             uuid PRIMARY KEY,
  order_id       uuid NOT NULL REFERENCES sales.orders(id) ON DELETE CASCADE,
  variant_id     uuid NOT NULL REFERENCES catalog.variants(id),
  product_id     uuid NOT NULL REFERENCES catalog.products(id),
  sku            text NOT NULL,
  product_name   text NOT NULL,
  variant_name   text,
  quantity       int NOT NULL CHECK (quantity BETWEEN 1 AND 1000),
  list_price_vnd bigint NOT NULL CHECK (list_price_vnd >= 0),
  unit_price_vnd bigint NOT NULL CHECK (unit_price_vnd >= 0),
  price_source   text NOT NULL CHECK (price_source IN ('list','sale','override')),
  price_override_reason text,
  line_total_vnd bigint NOT NULL,
  unit_cost_vnd  bigint CHECK (unit_cost_vnd IS NULL OR unit_cost_vnd >= 0),  -- chốt khi xuất kho
  returned_quantity int NOT NULL DEFAULT 0,
  review_requested_at timestamptz,              -- BR-SEO-90
  CONSTRAINT ck_line_total CHECK (line_total_vnd = quantity * unit_price_vnd),
  CONSTRAINT ck_line_override CHECK (price_source <> 'override' OR length(trim(coalesce(price_override_reason,''))) >= 3),
  CONSTRAINT ck_line_returned CHECK (returned_quantity BETWEEN 0 AND quantity),
  UNIQUE (order_id, variant_id)
);
CREATE INDEX ix_order_lines_variant ON sales.order_lines(variant_id);
CREATE INDEX ix_order_lines_product ON sales.order_lines(product_id);

ALTER TABLE inventory.reservations
  ADD CONSTRAINT fk_reservations_line FOREIGN KEY (order_line_id) REFERENCES sales.order_lines(id) ON DELETE RESTRICT;

CREATE TABLE sales.order_status_history (
  id             uuid PRIMARY KEY,
  order_id       uuid NOT NULL REFERENCES sales.orders(id) ON DELETE CASCADE,
  from_status    text,
  to_status      text NOT NULL,
  actor_type     text NOT NULL CHECK (actor_type IN ('staff','customer','system')),
  actor_id       uuid,
  reason         text,
  occurred_at    timestamptz NOT NULL
);
CREATE INDEX ix_order_history ON sales.order_status_history(order_id, occurred_at);

CREATE TABLE sales.cod_settlements (
  id             uuid PRIMARY KEY,
  carrier_name   text NOT NULL,
  reference      text,
  settled_at     timestamptz NOT NULL,
  gross_cod_vnd  bigint NOT NULL CHECK (gross_cod_vnd >= 0),
  fee_vnd        bigint NOT NULL DEFAULT 0 CHECK (fee_vnd >= 0),   -- phí thu hộ/đối soát
  net_received_vnd bigint NOT NULL,
  note           text,
  created_by     uuid NOT NULL,
  created_at     timestamptz NOT NULL DEFAULT now(),
  CHECK (net_received_vnd = gross_cod_vnd - fee_vnd)
);

CREATE TABLE sales.payments (
  id             uuid PRIMARY KEY,
  order_id       uuid NOT NULL REFERENCES sales.orders(id) ON DELETE RESTRICT,
  method         text NOT NULL CHECK (method IN ('bank_transfer','cod','cash')),
  provider       text NOT NULL DEFAULT 'manual' CHECK (provider IN ('manual','sepay')),
  amount_vnd     bigint NOT NULL CHECK (amount_vnd > 0),
  status         text NOT NULL CHECK (status IN ('awaiting_settlement','confirmed','voided')),
  bank_reference text,
  received_at    timestamptz,
  cod_settlement_id uuid REFERENCES sales.cod_settlements(id),
  note           text,
  void_reason    text,
  created_by     uuid,
  created_at     timestamptz NOT NULL DEFAULT now(),
  confirmed_by   uuid,
  confirmed_at   timestamptz,
  CHECK ((status = 'confirmed') = (confirmed_at IS NOT NULL)),
  CHECK (status <> 'awaiting_settlement' OR method = 'cod'),
  CHECK (status <> 'voided' OR void_reason IS NOT NULL)
);
CREATE INDEX ix_payments_order ON sales.payments(order_id);
CREATE INDEX ix_payments_awaiting ON sales.payments(created_at) WHERE status = 'awaiting_settlement';
CREATE UNIQUE INDEX ux_payments_bank_ref ON sales.payments(provider, bank_reference)
  WHERE bank_reference IS NOT NULL AND status <> 'voided';

CREATE TABLE sales.return_requests (
  id             uuid PRIMARY KEY,
  code           text NOT NULL UNIQUE,          -- DT-261116-001
  order_id       uuid NOT NULL REFERENCES sales.orders(id),
  reason         text NOT NULL CHECK (reason IN ('damaged_in_transit','defective','wrong_item')),
  description    text NOT NULL,
  status         text NOT NULL DEFAULT 'requested' CHECK (status IN ('requested','approved','rejected','resolved')),
  resolution     text CHECK (resolution IN ('refund','replacement')),
  decision_note  text,
  requested_at   timestamptz NOT NULL,
  decided_by     uuid,
  decided_at     timestamptz,
  resolved_at    timestamptz,
  created_by     uuid,
  CHECK (status NOT IN ('approved','resolved') OR resolution IS NOT NULL)
);
CREATE TABLE sales.return_lines (
  id             uuid PRIMARY KEY,
  return_request_id uuid NOT NULL REFERENCES sales.return_requests(id) ON DELETE CASCADE,
  order_line_id  uuid NOT NULL REFERENCES sales.order_lines(id),
  quantity       int NOT NULL CHECK (quantity > 0),
  restockable    boolean NOT NULL DEFAULT false,
  UNIQUE (return_request_id, order_line_id)
);
CREATE TABLE sales.return_evidence (
  return_request_id uuid NOT NULL REFERENCES sales.return_requests(id) ON DELETE CASCADE,
  asset_id       uuid NOT NULL REFERENCES media.assets(id),
  PRIMARY KEY (return_request_id, asset_id)
);

CREATE TABLE sales.refunds (
  id             uuid PRIMARY KEY,
  order_id       uuid NOT NULL REFERENCES sales.orders(id),
  return_request_id uuid REFERENCES sales.return_requests(id),
  amount_vnd     bigint NOT NULL CHECK (amount_vnd > 0),
  method         text NOT NULL CHECK (method IN ('bank_transfer','cash')),
  bank_reference text,
  reason         text NOT NULL,
  refunded_at    timestamptz NOT NULL,
  created_by     uuid NOT NULL,
  created_at     timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_refunds_order ON sales.refunds(order_id);

CREATE TABLE sales.shipments (
  id             uuid PRIMARY KEY,
  order_id       uuid NOT NULL REFERENCES sales.orders(id),
  purpose        text NOT NULL DEFAULT 'order' CHECK (purpose IN ('order','replacement')),
  return_request_id uuid REFERENCES sales.return_requests(id),
  provider       text NOT NULL DEFAULT 'manual' CHECK (provider IN ('manual','ghn','ghtk')),
  carrier_name   text NOT NULL,
  tracking_code  text,
  status         text NOT NULL DEFAULT 'created' CHECK (status IN
                   ('created','picked_up','in_transit','delivered','failed','returned','cancelled')),
  cod_amount_vnd bigint NOT NULL DEFAULT 0 CHECK (cod_amount_vnd >= 0),
  shipping_cost_vnd bigint CHECK (shipping_cost_vnd IS NULL OR shipping_cost_vnd >= 0),   -- chi phí thật shop trả
  return_cost_vnd bigint CHECK (return_cost_vnd IS NULL OR return_cost_vnd >= 0),         -- phí hoàn hàng
  note           text,
  created_by     uuid,
  created_at     timestamptz NOT NULL DEFAULT now(),
  picked_up_at   timestamptz,
  delivered_at   timestamptz,
  failed_at      timestamptz,
  returned_at    timestamptz,
  CHECK (purpose <> 'replacement' OR return_request_id IS NOT NULL)
);
CREATE UNIQUE INDEX ux_shipment_active_order ON sales.shipments(order_id)
  WHERE purpose = 'order' AND status NOT IN ('cancelled','failed','returned');
CREATE UNIQUE INDEX ux_shipment_tracking ON sales.shipments(carrier_name, tracking_code)
  WHERE tracking_code IS NOT NULL;

CREATE TABLE sales.shipment_events (
  id             uuid PRIMARY KEY,
  shipment_id    uuid NOT NULL REFERENCES sales.shipments(id) ON DELETE CASCADE,
  status         text NOT NULL,
  note           text,
  source         text NOT NULL CHECK (source IN ('manual','webhook')),
  occurred_at    timestamptz NOT NULL,
  created_by     uuid
);

CREATE TABLE pricing.voucher_redemptions (
  id             uuid PRIMARY KEY,
  voucher_id     uuid NOT NULL REFERENCES pricing.vouchers(id),
  order_id       uuid NOT NULL UNIQUE REFERENCES sales.orders(id),
  customer_id    uuid NOT NULL REFERENCES crm.customers(id),
  amount_vnd     bigint NOT NULL CHECK (amount_vnd > 0),
  status         text NOT NULL DEFAULT 'applied' CHECK (status IN ('applied','reverted')),
  created_at     timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_redemptions_voucher_customer ON pricing.voucher_redemptions(voucher_id, customer_id) WHERE status = 'applied';

CREATE TABLE marketing.referrals (
  id             uuid PRIMARY KEY,
  referrer_customer_id uuid NOT NULL REFERENCES crm.customers(id),
  referee_customer_id  uuid NOT NULL UNIQUE REFERENCES crm.customers(id),
  first_order_id uuid NOT NULL UNIQUE REFERENCES sales.orders(id),
  welcome_discount_vnd bigint NOT NULL CHECK (welcome_discount_vnd >= 0),
  status         text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending','qualified','rewarded','rejected')),
  reject_reason  text,
  reward_voucher_id uuid REFERENCES pricing.vouchers(id),
  created_at     timestamptz NOT NULL DEFAULT now(),
  qualified_at   timestamptz,
  CHECK (referrer_customer_id <> referee_customer_id)
);

-- =====================================================================
-- CONTENT
-- =====================================================================
CREATE TABLE content.article_categories (
  id             uuid PRIMARY KEY,
  slug           text NOT NULL UNIQUE CHECK (slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'),
  name           text NOT NULL,
  description    text,
  sort_order     int NOT NULL DEFAULT 0
);

CREATE TABLE content.authors (
  id             uuid PRIMARY KEY,
  staff_id       uuid UNIQUE,                   -- nhân viên tương ứng (nếu có)
  slug           text NOT NULL UNIQUE CHECK (slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'),
  name           text NOT NULL,
  job_title      text,
  bio_html       text,
  avatar_media_id uuid REFERENCES media.assets(id),
  status         text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft','published')),
  created_at     timestamptz NOT NULL DEFAULT now(),
  updated_at     timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE content.articles (
  id             uuid PRIMARY KEY,
  slug           text NOT NULL UNIQUE CHECK (slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'),
  title          text NOT NULL,
  excerpt        text,
  content_json   jsonb NOT NULL,
  content_html   text NOT NULL,
  cover_media_id uuid REFERENCES media.assets(id),
  category_id    uuid REFERENCES content.article_categories(id),
  author_id      uuid NOT NULL REFERENCES content.authors(id),
  created_by_staff_id uuid NOT NULL,
  status         text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft','in_review','published','archived')),
  published_at   timestamptz,
  focus_keyword  text,
  search_text    text NOT NULL DEFAULT '',
  seo_title      text, seo_description text, canonical_url text,
  robots_noindex boolean NOT NULL DEFAULT false,
  og_media_id    uuid REFERENCES media.assets(id),
  created_at     timestamptz NOT NULL DEFAULT now(),
  updated_at     timestamptz NOT NULL DEFAULT now(),
  CHECK (status <> 'published' OR published_at IS NOT NULL)
);
CREATE INDEX ix_articles_status_pub ON content.articles(status, published_at DESC);
CREATE INDEX ix_articles_search ON content.articles USING gin (search_text gin_trgm_ops);

CREATE TABLE content.article_links (
  article_id     uuid NOT NULL REFERENCES content.articles(id) ON DELETE CASCADE,
  target_type    text NOT NULL CHECK (target_type IN ('product','material','artisan','category','product_type')),
  target_id      uuid NOT NULL,
  sort_order     int NOT NULL DEFAULT 0,
  PRIMARY KEY (article_id, target_type, target_id)
);
CREATE INDEX ix_article_links_target ON content.article_links(target_type, target_id);

CREATE TABLE content.pages (
  id             uuid PRIMARY KEY,
  slug           text NOT NULL UNIQUE CHECK (slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'),
  kind           text NOT NULL CHECK (kind IN ('about','policy_return','policy_shipping','policy_payment',
                                               'policy_privacy','terms','custom')),
  title          text NOT NULL,
  content_json   jsonb NOT NULL,
  content_html   text NOT NULL,
  status         text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft','published')),
  seo_title      text, seo_description text,
  updated_by     uuid,
  updated_at     timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX ux_pages_kind ON content.pages(kind) WHERE kind <> 'custom';

CREATE TABLE content.redirects (
  source_path    text PRIMARY KEY CHECK (source_path ~ '^/[^?#]*$'),
  target_path    text NOT NULL CHECK (target_path ~ '^/[^?#]*$'),
  created_at     timestamptz NOT NULL DEFAULT now(),
  hit_count      bigint NOT NULL DEFAULT 0,
  CHECK (source_path <> target_path)
);

CREATE TABLE content.landing_pages (
  id             uuid PRIMARY KEY,
  slug           text NOT NULL UNIQUE CHECK (slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'),
  title          text NOT NULL,
  h1             text NOT NULL,
  intro_json     jsonb NOT NULL,
  intro_html     text NOT NULL,
  body_json      jsonb,
  body_html      text,
  filter         jsonb NOT NULL,                -- BR-SEO-40
  cover_media_id uuid REFERENCES media.assets(id),
  status         text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft','published','archived')),
  published_at   timestamptz,
  focus_keyword  text,
  seo_title      text, seo_description text, robots_noindex boolean NOT NULL DEFAULT false,
  created_at     timestamptz NOT NULL DEFAULT now(),
  updated_at     timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE content.not_found_logs (
  path           text PRIMARY KEY,
  hits           bigint NOT NULL DEFAULT 0,
  bot_hits       bigint NOT NULL DEFAULT 0,
  last_referrer_host text,
  first_seen_at  timestamptz NOT NULL DEFAULT now(),
  last_seen_at   timestamptz NOT NULL DEFAULT now(),
  resolved       boolean NOT NULL DEFAULT false
);
CREATE INDEX ix_not_found_hits ON content.not_found_logs(hits DESC) WHERE NOT resolved;

-- =====================================================================
-- REVIEWS (đánh giá người mua thật — BR-SEO-90..92)
-- =====================================================================
CREATE TABLE catalog.product_reviews (
  id             uuid PRIMARY KEY,
  product_id     uuid NOT NULL REFERENCES catalog.products(id),
  order_line_id  uuid NOT NULL UNIQUE REFERENCES sales.order_lines(id),
  customer_id    uuid NOT NULL REFERENCES crm.customers(id),
  rating         smallint NOT NULL CHECK (rating BETWEEN 1 AND 5),
  content        text NOT NULL CHECK (length(trim(content)) >= 20 AND length(content) <= 3000),
  display_name   text NOT NULL,                 -- "Anh T."
  status         text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending','approved','rejected')),
  reject_reason  text,
  created_at     timestamptz NOT NULL DEFAULT now(),
  moderated_by   uuid,
  moderated_at   timestamptz,
  CHECK (status <> 'rejected' OR reject_reason IS NOT NULL)
);
CREATE INDEX ix_reviews_product ON catalog.product_reviews(product_id, created_at DESC) WHERE status = 'approved';
CREATE TABLE catalog.review_media (
  review_id      uuid NOT NULL REFERENCES catalog.product_reviews(id) ON DELETE CASCADE,
  asset_id       uuid NOT NULL REFERENCES media.assets(id),
  PRIMARY KEY (review_id, asset_id)
);

CREATE TABLE platform.web_vitals_samples (
  id             bigserial PRIMARY KEY,
  recorded_at    timestamptz NOT NULL DEFAULT now(),
  page_type      text NOT NULL CHECK (page_type IN ('home','category','product','article','landing','material','artisan','other')),
  metric         text NOT NULL CHECK (metric IN ('LCP','INP','CLS','TTFB','FCP')),
  value          double precision NOT NULL CHECK (value >= 0),
  device         text NOT NULL CHECK (device IN ('mobile','desktop'))
);
CREATE INDEX ix_vitals_time ON platform.web_vitals_samples(recorded_at);

-- =====================================================================
-- FINANCE
-- =====================================================================
CREATE TABLE finance.expenses (
  id             uuid PRIMARY KEY,
  category       text NOT NULL CHECK (category IN
                   ('marketing','fixed','packaging','shipping_other','payment_fee','damage_other','other')),
  subcategory    text,                          -- 'salary','rent','software','server','ads_facebook'...
  amount_vnd     bigint NOT NULL CHECK (amount_vnd > 0),
  incurred_on    date NOT NULL,                 -- chi phí cố định: ngày 1 của tháng
  channel        text,                          -- bắt buộc khi category='marketing'
  campaign_id    uuid REFERENCES marketing.campaigns(id),
  description    text NOT NULL,
  created_by     uuid NOT NULL,
  created_at     timestamptz NOT NULL DEFAULT now(),
  CHECK (category <> 'marketing' OR channel IS NOT NULL),
  CHECK (category <> 'fixed' OR extract(day FROM incurred_on) = 1)
);
CREATE INDEX ix_expenses_date ON finance.expenses(incurred_on, category);

-- =====================================================================
-- IAM (ASP.NET Core Identity — do Identity/EF tạo, chỉ liệt kê)
-- iam.staff_users (+ display_name text, is_active bool, must_change_password bool, created_at)
-- iam.roles, iam.user_roles, iam.role_claims, iam.user_claims, iam.user_logins, iam.user_tokens
-- Role seed: owner, sales, content. Permission claims seed theo 02-architecture.md §7.
-- =====================================================================

-- =====================================================================
-- PLATFORM — P0-09 / F-02 (contract/DDL đã được owner duyệt)
-- Append-only import ledger; không thuộc chín bảng migration P0-04.
-- Quy trình import dùng transaction advisory lock (8300,9); xem 03 §3.6.
-- Các role tp_app/tp_readonly được tạo theo 03 §4 trước khi áp grants.
-- Khối này bổ sung schema đích, chưa phải migration hay dữ liệu seed.
-- =====================================================================
CREATE TABLE platform.administrative_data_imports (
  id uuid CONSTRAINT pk_administrative_data_imports PRIMARY KEY,
  previous_import_id uuid,
  dataset_version text NOT NULL,
  release_order bigint NOT NULL,
  effective_on date NOT NULL,
  operation text NOT NULL,
  manifest jsonb NOT NULL,
  manifest_sha256 text NOT NULL,
  csv_sha256 text NOT NULL,
  plan_sha256 text NOT NULL,
  catalog_sha256 text NOT NULL,
  receipt jsonb NOT NULL,
  applied_at timestamptz NOT NULL,
  recovery_of uuid,
  re_advance_of uuid,
  CONSTRAINT ux_administrative_import_previous
    UNIQUE NULLS NOT DISTINCT (previous_import_id),
  CONSTRAINT fk_administrative_import_previous FOREIGN KEY (previous_import_id)
    REFERENCES platform.administrative_data_imports(id),
  CONSTRAINT fk_administrative_import_recovery FOREIGN KEY (recovery_of)
    REFERENCES platform.administrative_data_imports(id),
  CONSTRAINT fk_administrative_import_re_advance FOREIGN KEY (re_advance_of)
    REFERENCES platform.administrative_data_imports(id),
  CONSTRAINT ck_administrative_import_version CHECK (length(dataset_version) > 0 AND release_order > 0),
  CONSTRAINT ck_administrative_import_operation CHECK (
    (operation = 'import' AND recovery_of IS NULL AND re_advance_of IS NULL) OR
    (operation = 'recovery' AND recovery_of IS NOT NULL AND re_advance_of IS NULL) OR
    (operation = 're_advance' AND recovery_of IS NULL AND re_advance_of IS NOT NULL)),
  CONSTRAINT ck_administrative_import_genesis CHECK (
    previous_import_id IS NOT NULL OR operation = 'import'),
  CONSTRAINT ck_administrative_import_hashes CHECK (
    manifest_sha256 ~ '^[0-9a-f]{64}$' AND csv_sha256 ~ '^[0-9a-f]{64}$' AND
    plan_sha256 ~ '^[0-9a-f]{64}$' AND catalog_sha256 ~ '^[0-9a-f]{64}$'),
  CONSTRAINT ck_administrative_import_json CHECK (
    jsonb_typeof(manifest) = 'object' AND jsonb_typeof(receipt) = 'object'),
  CONSTRAINT ck_administrative_import_no_self CHECK (
    previous_import_id IS DISTINCT FROM id AND recovery_of IS DISTINCT FROM id AND
    re_advance_of IS DISTINCT FROM id)
);
GRANT SELECT, INSERT ON platform.administrative_data_imports TO tp_app;
REVOKE UPDATE, DELETE, TRUNCATE ON platform.administrative_data_imports FROM tp_app;
GRANT SELECT ON platform.administrative_data_imports TO tp_readonly;
GRANT EXECUTE ON FUNCTION pg_catalog.pg_try_advisory_xact_lock(integer, integer) TO tp_app;
