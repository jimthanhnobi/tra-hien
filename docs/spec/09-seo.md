# 09 — SEO (cốt lõi tìm kiếm)

> SEO là kênh khách mới chính (01 §1). File này là nguồn sự thật cho mọi thứ liên quan đến tìm kiếm;
> các luật `BR-SEO-*` có hiệu lực ngang các BR khác và **phải có test** (snapshot HTML/JSON-LD, test sitemap, test redirect).
> Khi một luật ở 04 nói khác file này về SEO, **file này thắng**.

## 0. Mục tiêu và chỉ số

| Chỉ số | Nguồn | Mục tiêu 6 tháng sau go-live (tham khảo) |
|---|---|---|
| Trang được index / trang indexable | Search Console | ≥ 90% |
| Lượt hiển thị và click tự nhiên | Search Console | Tăng đều theo tháng |
| Đơn từ `google_organic` (và doanh thu thuần) | Báo cáo nguồn (BR-FIN-05) | Có số liệu theo tháng |
| Core Web Vitals đạt "Good" (p75 mobile) | Search Console / CrUX | ≥ 90% URL |
| Lỗi SEO mức "Lỗi" trong SEO Audit (BR-SEO-60) | Admin | 0 |

## 1. Cấu trúc URL

| Loại trang | URL | Ghi chú |
|---|---|---|
| Trang chủ | `/` | |
| Danh mục | `/danh-muc/{slug}` | Danh mục con cũng phẳng (`/danh-muc/am-tu-sa`), không lồng cha |
| Sản phẩm | `/san-pham/{slug}` | **Không** chứa danh mục → đổi danh mục không đổi URL |
| Biến thể | `/san-pham/{slug}?bien-the={SKU}` | Canonical luôn về `/san-pham/{slug}` |
| Bộ sưu tập | `/bo-suu-tap/{slug}` | |
| Chủ đề (landing) | `/chu-de/{slug}` | BR-SEO-40 |
| Chất liệu | `/chat-lieu/{slug}` | Trang trụ cột (pillar) |
| Nghệ nhân | `/nghe-nhan/{slug}` | |
| Kiến thức | `/kien-thuc`, `/kien-thuc/{slug}`, `/kien-thuc/chuyen-muc/{slug}` | |
| Tác giả bài viết | `/tac-gia/{slug}` | E-E-A-T |
| Trang tĩnh | `/trang/{slug}` (`gioi-thieu`, `chinh-sach-doi-tra`...) | |
| Không index | `/tim-kiem`, `/gio-hang`, `/thanh-toan`, `/tai-khoan/*`, `/tra-cuu-don`, `/r/{code}` | |

- **BR-SEO-01** URL chữ thường, không dấu, nối bằng `-`, không có ID/ngày; không có dấu `/` cuối (có → 308 về bản không có). Chữ hoa trong path → 308 về chữ thường.
- **BR-SEO-02** Tham số theo dõi (`utm_*`, `gclid`, `fbclid`, `zarsrc`...) không làm đổi canonical. Tham số lạ không được whitelist → canonical bỏ tham số.
- **BR-SEO-03** Mọi đổi slug/lưu trữ/hợp nhất trang → 301 theo BR-CAT-08; không bao giờ có chuỗi redirect > 1 bước; CI có test đi qua toàn bộ bảng redirect để chặn chuỗi và vòng.

## 2. Ma trận index

| Trang | robots meta | Canonical | Sitemap |
|---|---|---|---|
| Sản phẩm `active` đủ điều kiện index | index,follow nếu không có manual/thin/điều kiện noindex khác | chính nó | Có (kèm ảnh) |
| Sản phẩm active mô tả < 150 từ | noindex,follow (SITE_INDEXABLE=false vẫn ưu tiên) | chính nó | Không; không feed |
| Sản phẩm độc bản đã bán | cùng điều kiện manual/thin/site như trên trong thời gian giữ (BR-SEO-21) | chính nó | Chỉ khi indexable |
| Danh mục / bộ sưu tập visible | index,follow | chính nó, **kể cả `?trang=2+`** | Chỉ trang 1 |
| Danh mục có bộ lọc/sắp xếp (`?chat-lieu=`, `?gia=`, `?sap-xep=`...) | **noindex,follow** | không đặt canonical sang trang khác | Không |
| Chủ đề (landing) đủ điều kiện | index,follow | chính nó | Có |
| Chủ đề chưa đủ điều kiện | noindex,follow | — | Không |
| Chất liệu / nghệ nhân / tác giả / bài viết / trang tĩnh `published` | index,follow | chính nó | Có |
| Tìm kiếm | noindex,follow | — | Không |
| Giỏ, thanh toán, tài khoản, tra đơn, `/r/*` | noindex,nofollow + robots.txt Disallow | — | Không |
| Bất kỳ trang nào khi `SITE_INDEXABLE=false` | noindex,nofollow + robots.txt `Disallow: /` | — | Rỗng |

- **BR-SEO-10** **Phân trang** dùng `?trang=N`: mỗi trang canonical **về chính nó** và được index; tiêu đề thêm " – Trang N"; trang N vượt quá số trang → 404. (Thay thế quy định "noindex trang > 1" trước đây.)
- **BR-SEO-11** Bộ lọc là tham số query, render server-side (link `<a>` thật để Google theo được), nhưng **noindex**. Tổ hợp lọc có giá trị tìm kiếm thì làm thành **trang chủ đề** (BR-SEO-40), không mở index cho bộ lọc.
- **BR-SEO-12** `robots.txt` (sinh động):
  ```
  User-agent: *
  Disallow: /gio-hang
  Disallow: /thanh-toan
  Disallow: /tai-khoan
  Disallow: /tra-cuu-don
  Disallow: /r/
  Disallow: /api/
  Sitemap: https://shop.<domain>/sitemap.xml
  ```
  **Không** Disallow `/tim-kiem` hay URL có bộ lọc (để Google đọc được `noindex`).
- **BR-SEO-13** Subdomain `admin.` trả header `X-Robots-Tag: noindex, nofollow` cho mọi response và robots.txt `Disallow: /`.
- **BR-SEO-14** Trang 404 trả **mã 404 thật** (không soft-404), có ô tìm kiếm, danh mục chính, sản phẩm mới.

## 3. Vòng đời trang sản phẩm

- **BR-SEO-20** Variant hết hàng (có thể nhập lại): trang giữ nguyên 200, index khi không có manual/thin/site noindex theo §2/BR-SEO-34, `availability=OutOfStock`, nút "Báo khi có hàng" (mở Zalo/form liên hệ).
- **BR-SEO-21** **Hàng độc bản đã bán** (mọi variant active đều độc bản và đã bán): ghi `products.sold_out_at`. Trong `seo.sold_unique_keep_days` ngày (mặc định 180): trang 200, chỉ index nếu không manual/thin/site noindex (§2/BR-SEO-34), nhãn "Đã bán", `availability=SoldOut`, khối "Tác phẩm tương tự" (cùng nghệ nhân → cùng chất liệu → cùng loại, còn hàng, 8 mục) đặt **ngay dưới ảnh**. Hết thời hạn: job tự `archive` + 301 theo BR-SEO-22, **trừ** sản phẩm có `seo_keep_after_sold = true` (tác phẩm tiêu biểu, có nhiều backlink).
- **BR-SEO-22** Lưu trữ sản phẩm **đã từng publish** bắt buộc có đích 301. Mặc định theo thứ tự: trang nghệ nhân (nếu published) → trang chất liệu (nếu published) → danh mục chính. Admin cho chọn lại. Sản phẩm chưa từng publish lưu trữ → 404. (Thay thế BR-CAT-05 phần `archived`.)
- **BR-SEO-23** Sản phẩm/danh mục/bài viết bị `robots_noindex=true` phải có lý do trong audit; SEO Audit liệt kê tất cả trang noindex thủ công.

## 4. Metadata và nội dung trên trang

- **BR-SEO-30** Mỗi trang indexable có **đúng 1 `<h1>`**, `<title>` duy nhất, meta description duy nhất (SEO Audit báo trùng). Ngôn ngữ `<html lang="vi">`.
- **BR-SEO-31** Mẫu `<title>` mặc định khi `seo_title` trống (tối đa ~60 ký tự, cắt ở ranh giới từ; thương hiệu ở cuối, bỏ thương hiệu nếu quá dài):

| Trang | Mẫu |
|---|---|
| Sản phẩm | `{tên} – {chất liệu} {dung tích}ml \| {Thương hiệu}` (bỏ phần nào không có) |
| Danh mục | `{tên danh mục} \| {Thương hiệu}` |
| Chất liệu | `{tên chất liệu}: đặc điểm, cách chọn và tác phẩm \| {Thương hiệu}` |
| Nghệ nhân | `Nghệ nhân {tên} – tác phẩm {chất liệu chính} \| {Thương hiệu}` |
| Chủ đề | `{tiêu đề chủ đề} \| {Thương hiệu}` |
| Bài viết | `{tiêu đề bài}` (≤ 60 ký tự) |

- **BR-SEO-32** Meta description mặc định: `short_description`/`excerpt`/`intro` cắt 150–160 ký tự. Nhân viên được sửa; Admin hiện preview kết quả Google và đếm ký tự.
- **BR-SEO-33** Open Graph + Twitter Card trên mọi trang public: `og:title`, `og:description`, `og:image` (1200×630, sinh từ ảnh bìa bằng route ảnh của Next), `og:url` = canonical, `og:type` (`product`/`article`/`website`), `og:locale=vi_VN`. (Zalo và Facebook dùng OG để hiện preview khi chia sẻ link.)
- **BR-SEO-34** **Nội dung sản phẩm và điều kiện index** (bổ sung BR-CAT-04):
  - Mô tả riêng ≥ **150 từ** là điều kiện index, không chặn publish chỉ vì thiếu số từ. Không sao chép nguyên văn; giữ bảng thông số HTML crawl được và nội dung thật. Điều kiện không rỗng/metadata của BR-CAT-04 không đổi.
  - Đếm plain text description_html đã sanitize: bỏ script/style, giải mã entities, ngăn cách block, gộp whitespace Unicode/NBSP, đếm token không rỗng; không tính boilerplate/bảng thông số tự chèn. Dùng cùng phép đếm phía server khi lưu/import/audit; cập nhật description_word_count cùng transaction, backfill trước khi dùng cho dữ liệu cũ.
  - thin_description = description_word_count < 150; effective noindex là OR của thin, robots_noindex thủ công và mọi điều kiện noindex khác. Không ghi/clear robots_noindex tự động. Đủ 150 từ chỉ gỡ lý do thin; SITE_INDEXABLE=false vẫn noindex,nofollow, draft 404, archived theo vòng đời. Thin dùng noindex,follow, không Disallow để crawler đọc meta.
  - Qua ngưỡng theo cả hai chiều hoặc đổi cờ thủ công phát StorefrontInvalidate cùng transaction, làm mới product/catalog/sitemap/feed. SSR/sitemap/feed dùng cùng eligibility, feed vẫn kiểm BR-SEO-80/81 và không đưa variant inactive/giá NULL. P1-05/14 kiểm dữ liệu/warning/outbox; P3-09/15/17 kiểm HTML/robots/canonical/sitemap/feed/audit.
  - Khuyến nghị (cảnh báo, không chặn): ≥ 3 ảnh; có mục "Cách dùng & bảo quản"; có liên kết nghệ nhân/chất liệu; `focus_keyword` đã điền.
  - Trang sản phẩm tự chèn các khối: "Về chất liệu {X}" (tóm tắt + link `/chat-lieu/x`), "Về nghệ nhân {Y}" (tóm tắt + link), "Bài viết liên quan" (từ `article_links`).
  - SEO Audit báo **trùng mô tả** khi độ giống (trigram `similarity`) giữa hai mô tả > 0,8.
- **BR-SEO-35** Trang danh mục/bộ sưu tập: đoạn giới thiệu ngắn (40–80 từ) phía trên lưới sản phẩm + nội dung hướng dẫn dài (`body_html`, ≥ 300 từ khuyến nghị) phía dưới lưới. Trang chất liệu/nghệ nhân: nội dung ≥ 400 từ + lưới sản phẩm + bài viết liên quan.
- **BR-SEO-36** **Một từ khóa chính cho một trang**: `focus_keyword` trên product/category/collection/landing/material/artisan/article. Trùng `focus_keyword` (sau `f_unaccent`) giữa hai trang indexable → SEO Audit cảnh báo "ăn thịt từ khóa" (cannibalization).
- **BR-SEO-37** Nội dung AI: được dùng AI viết nháp, nhưng bài/sản phẩm phải có thông tin thực tế từ shop (ảnh thật, thông số đo thật, kinh nghiệm dùng) và người duyệt trước khi publish. Không sinh hàng loạt trang mỏng. Không khẳng định xuất xứ/chứng nhận khi không có bằng chứng.

## 5. Trang chủ đề (landing) — thay cho index bộ lọc

- **BR-SEO-40** `content.landing_pages`: `slug`, `title`, `h1`, `intro_html` (≥ 150 từ), `body_html`, `filter` (JSON: `categoryIds`, `productTypeIds`, `materialIds`, `artisanIds`, `attributes[{code, optionCodes | min, max}]`, `priceMin`, `priceMax`, `inStockOnly`), `focus_keyword`, SEO, `status`.
  Ví dụ: "Ấm tử sa chu nê", "Ấm tử sa dung tích nhỏ 100–200ml", "Ấm tử sa nghệ nhân", "Quà tặng trà cụ cao cấp", "Bộ ấm chén pha trà".
- **BR-SEO-41** Trang chủ đề chỉ index khi `published` **và** có ≥ 4 sản phẩm khớp đang hiển thị; ít hơn → tự `noindex` và ra khỏi sitemap, SEO Audit cảnh báo.
- **BR-SEO-42** Trang danh mục hiện link tới các trang chủ đề liên quan (cùng danh mục/chất liệu) dạng "Tìm theo chủ đề".

## 6. Liên kết nội bộ

- **BR-SEO-50** Breadcrumb hiển thị + JSON-LD trên sản phẩm (`Trang chủ › {danh mục cha} › {danh mục chính} › {sản phẩm}`), danh mục, bài viết, chất liệu, nghệ nhân, chủ đề.
- **BR-SEO-51** Trang sản phẩm có: link danh mục chính, chất liệu, nghệ nhân, bộ sưu tập chứa nó; "Sản phẩm liên quan" (chọn tay trước, bù tự động cùng nghệ nhân/chất liệu/loại, tối đa 8).
- **BR-SEO-52** Bài viết nhúng thẻ sản phẩm bằng node Tiptap `productCard(productId)` → render server-side với tên, ảnh, giá hiệu lực, tình trạng hàng hiện tại; sản phẩm bị lưu trữ thì node tự ẩn. Mỗi node tạo `article_links`.
- **BR-SEO-53** Trang chất liệu/nghệ nhân/danh mục hiện "Bài viết liên quan" (từ `article_links`).
- **BR-SEO-54** Menu và footer chứa link danh mục cấp 1, trang chất liệu chính, trang kiến thức, chính sách. Mọi trang indexable đến được trong ≤ 3 click từ trang chủ.
- **BR-SEO-55** Không có trang mồ côi: SEO Audit liệt kê trang indexable không có link nội bộ trỏ tới (sản phẩm không thuộc danh mục/bộ sưu tập visible nào, bài viết không nằm trong chuyên mục, chủ đề không được link).
- **BR-SEO-56** Anchor text mô tả nội dung đích; không dùng "xem thêm"/"tại đây" làm anchor duy nhất.

## 7. Dữ liệu có cấu trúc (JSON-LD)

Sinh ở server bằng các hàm thuần trong `apps/store/lib/seo/jsonld/*` có **unit test snapshot**. Giá và tình trạng trong JSON-LD **phải trùng** giá/tình trạng hiển thị và trong feed Merchant (BR-SEO-80).

- **BR-SEO-70** Toàn site (trang chủ): `Organization` (`name`, `url`, `logo`, `sameAs` [Facebook, Zalo OA khi có], `contactPoint` hotline) + `WebSite` (`name`, `url`). Nếu có cửa hàng/showroom thật đón khách: `Store` (`address`, `geo`, `openingHoursSpecification`, `telephone`), thông tin trùng khớp Google Business Profile.
- **BR-SEO-71** Sản phẩm 1 variant: `Product` (`name`, `description`, `image` [≥ 3 ảnh nếu có], `sku`, `material`, `additionalProperty` [PropertyValue: dung tích, kích thước, kiểu lọc...], `offers`).
  Sản phẩm nhiều variant: `ProductGroup` (`productGroupID`, `variesBy` [vd. `https://schema.org/size` cho dung tích], `hasVariant` [mỗi variant là `Product` có `sku`, `name`, thuộc tính khác nhau, `offers`]).
  `Offer`: `url` (kèm `?bien-the=SKU` cho variant), `price`, `priceCurrency=VND`, `availability`, `itemCondition=NewCondition`, `priceValidUntil` (= `ends_at` khi đang KM), `shippingDetails` (phí theo BR-SHP-01, `shippingDestination` VN, `deliveryTime` từ setting `seo.handling_days`, `seo.transit_days`), `hasMerchantReturnPolicy` khai đúng chính sách thật (đối chiếu tài liệu Google hiện hành khi làm để chọn giá trị phù hợp với "chỉ đổi khi vỡ/lỗi vận chuyển"), `seller` = Organization.
  **Không** có `brand` giả; không có `gtin`. `aggregateRating`/`review` chỉ khi có đánh giá thật đã duyệt (BR-SEO-90).
- **BR-SEO-72** `BreadcrumbList` theo BR-SEO-50.
- **BR-SEO-73** Bài viết: `BlogPosting` (`headline`, `image`, `datePublished`, `dateModified`, `author` = `Person` có `url` tới `/tac-gia/{slug}`, `publisher` = Organization).
- **BR-SEO-74** Nghệ nhân: `ProfilePage` với `mainEntity` = `Person` (`name`, `jobTitle`, `description`, `image`). Tác giả: tương tự.
- **BR-SEO-75** Không dùng `FAQPage`/`HowTo` với kỳ vọng rich result (Google đã giới hạn/ngừng); nội dung hỏi đáp vẫn viết cho người đọc. Không đánh dấu `SearchAction`.
- **BR-SEO-76** CI chạy test: mọi template trang có JSON-LD parse được, có các trường bắt buộc ở trên. Trước go-live kiểm tay bằng Rich Results Test cho 1 trang mỗi loại.

## 8. Sitemap và Google Merchant Center

- **BR-SEO-77** `/sitemap.xml` là sitemap index trỏ tới: `sitemap-products.xml` (có `<image:image>` cho ảnh), `sitemap-categories.xml`, `sitemap-collections.xml`, `sitemap-landing.xml`, `sitemap-materials.xml`, `sitemap-artisans.xml`, `sitemap-articles.xml`, `sitemap-authors.xml`, `sitemap-pages.xml`. Mỗi file ≤ 50.000 URL. `lastmod` = thời điểm thay đổi nội dung đáng kể (nội dung, giá, tình trạng hàng). Cache theo tag, làm mới khi có `StorefrontInvalidate`.
- **BR-SEO-80** **Feed Google Merchant Center** (hiển thị sản phẩm miễn phí trên Google Shopping/Search/Images): `GET /feeds/google-merchant.xml` (RSS 2.0, namespace `g:`), làm mới mỗi giờ, cache.
  Mỗi variant active có giá hợp lệ (BR-CAT-15) của sản phẩm indexable (không manual/thin/site noindex) là 1 item: `g:id` = SKU, `g:item_group_id` = product id (khi > 1 variant), `title` (tên + tên variant), `description`, `link`, `g:image_link`, `g:additional_image_link`, `g:availability` (`in_stock`/`out_of_stock`), `g:price` (`3500000 VND`), `g:sale_price` + `g:sale_price_effective_date` khi KM, `g:condition=new`, `g:brand` = tên thương hiệu shop (setting `seo.brand_name`), `g:identifier_exists=no`, `g:google_product_category` (map theo `product_types.google_product_category`, Admin sửa được), `g:product_type` (đường dẫn danh mục), `g:shipping` (VN, phí đồng giá), `g:shipping_weight`, `g:material`, `g:size` (dung tích).
  Hàng độc bản đã bán: `out_of_stock` trong 30 ngày rồi bỏ khỏi feed. Ảnh chính trong feed: không chữ/logo/watermark chèn lên (chính sách Merchant).
- **BR-SEO-81** Feed và trang phải khớp giá/tình trạng; test integration so sánh feed với `store/products/{slug}` cho cùng dữ liệu.

## 9. Hình ảnh

- **BR-SEO-82** Tên file trên R2 có ý nghĩa: upload intent nhận `slugHint` → `storage_key = {purpose}/{slugHint}/{slugHint}-{6 ký tự ngẫu nhiên}.{ext}` (ví dụ `catalog/van-lo-chu-ban/van-lo-chu-ban-k3m9qa.webp`).
- **BR-SEO-83** ALT: mô tả đúng ảnh, ≤ 125 ký tự, chứa loại sản phẩm + chất liệu + đặc điểm phân biệt (góc chụp, chi tiết); không nhồi từ khóa; mỗi ảnh ALT khác nhau. Admin gợi ý mẫu `{tên} – {chất liệu} – {góc nhìn}`.
- **BR-SEO-84** Ảnh bìa ≥ 1200 px cạnh ngắn (cảnh báo nếu nhỏ hơn), nền trung tính, không chữ chèn. Storefront phục vụ AVIF/WebP qua Next Image với `sizes` đúng; ảnh LCP (ảnh chính sản phẩm, banner trang chủ) `priority` + không lazy; ảnh còn lại lazy; luôn có `width/height` để không giật layout.

## 10. Tốc độ (Core Web Vitals)

- **BR-SEO-85** Ngân sách (đo p75 mobile, dữ liệu thực tế): **LCP ≤ 2,5 s, INP ≤ 200 ms, CLS ≤ 0,1**. Kiểm thử lab (CI, Lighthouse mobile) trên trang chủ, danh mục, sản phẩm, bài viết: Performance ≥ 85, SEO = 100, Accessibility ≥ 90, Best Practices ≥ 90 (thay AC-P3-08).
- **BR-SEO-86** JS tải lần đầu mỗi trang storefront ≤ 150 KB (gzip); trang sản phẩm/danh mục/bài viết là Server Component, chỉ phần tương tác (chọn variant, thêm giỏ, gallery) là Client Component. Không dùng thư viện carousel/animation nặng.
- **BR-SEO-87** Font tự host bằng `next/font`, subset `vietnamese` + `latin`, `display: swap`, tối đa 2 họ font. Script bên thứ ba (GA4) chỉ tải sau consent, chiến lược `afterInteractive`/`lazyOnload`.
- **BR-SEO-88** TTFB trang có cache ≤ 600 ms. Trang storefront ghi Web Vitals thực tế (thư viện `web-vitals`) gửi về `POST store/vitals` (chỉ metric, path, loại thiết bị; không PII) → bảng tổng hợp theo ngày (P6 hiển thị Admin; P3 chỉ lưu).

## 11. Đánh giá thật từ người mua

- **BR-SEO-90** Chỉ khách có dòng đơn `delivered` được đánh giá sản phẩm đó (1 đánh giá/dòng đơn). Sau `delivered_at + 7 ngày` gửi link đánh giá có token ký (email nếu có; nhân viên gửi tay qua Zalo từ Admin). Đánh giá gồm số sao 1–5, nội dung ≥ 20 ký tự, ảnh tùy chọn; trạng thái `pending → approved/rejected` (nhân viên duyệt; không được sửa nội dung khách, chỉ ẩn PII).
- **BR-SEO-91** Hiển thị: tên viết tắt ("Anh T."), nhãn "Đã mua hàng", ngày. `aggregateRating` trong JSON-LD chỉ khi có ≥ 1 đánh giá approved và hiển thị đúng trên trang.
- **BR-SEO-92** Không thưởng/giảm giá đổi lấy đánh giá tốt; không tự tạo đánh giá; không chỉ duyệt đánh giá tốt (từ chối chỉ khi spam, ngôn từ vi phạm, lộ thông tin cá nhân).

## 12. Đo lường

- **BR-SEO-93** Google Search Console: thuộc tính domain, gửi sitemap index; chủ shop là owner. (Kéo số liệu GSC API vào Admin: P6.)
- **BR-SEO-94** GA4 chỉ tải sau khi khách đồng ý cookie phân tích. Sự kiện thương mại điện tử: `view_item_list`, `select_item`, `view_item`, `add_to_cart`, `remove_from_cart`, `begin_checkout`, `add_shipping_info`, `add_payment_info`, `purchase` (`transaction_id` = mã đơn, `value`, `currency=VND`, `items[{item_id: SKU, item_name, item_variant, price, quantity}]`). `purchase` bắn 1 lần trên trang xác nhận (chống lặp bằng mã đơn). **Không gửi** tên, SĐT, email, địa chỉ.
- **BR-SEO-95** Nguồn tự nhiên vào CRM theo BR-MKT-09 (`google_organic`) để báo cáo doanh thu và lãi theo nguồn (BR-FIN-05) — đây là số đo SEO quan trọng nhất, không phụ thuộc GA4.

## 13. Giám sát và SEO Audit trong Admin

- **BR-SEO-96** Ghi log 404: storefront gửi `POST store/not-found` (path chuẩn hóa, referrer host, có phải bot) → `content.not_found_logs` cộng dồn theo path. Admin "Lỗi 404" sắp xếp theo lượt, nút "Tạo redirect".
- **BR-SEO-60** **SEO Audit** (Admin, chạy theo yêu cầu + hằng đêm, gửi Telegram tóm tắt hằng tuần). Mức **Lỗi**: trang indexable thiếu title/description, trùng title, thiếu H1, ảnh bìa thiếu ALT, redirect chuỗi/vòng, trang indexable trong sitemap trả ≠ 200, `SITE_INDEXABLE=true` nhưng trang chính có noindex. Noindex do mô tả mỏng đúng BR-SEO-34 là trạng thái dự kiến, không tự báo thêm lỗi noindex ngoài ý muốn; các lỗi khác giữ nguyên. Mức **Cảnh báo**: mô tả < 150 từ (đang noindex, không sitemap/feed), trùng mô tả (> 0,8), trùng `focus_keyword`, title/description ngoài độ dài khuyến nghị, < 3 ảnh, ảnh bìa < 1200 px, trang mồ côi, chủ đề < 4 sản phẩm, sản phẩm độc bản đã bán sắp hết hạn giữ, noindex thủ công.
- **BR-SEO-61** Admin form sản phẩm/bài viết có bảng "Điểm SEO" chạy cùng bộ kiểm tra cho riêng trang đó + preview kết quả Google + preview chia sẻ (OG).

## 14. SEO địa phương và thương hiệu

- **BR-SEO-97** Có địa chỉ đón khách thật → tạo Google Business Profile; tên, địa chỉ, SĐT (NAP) **giống hệt** footer, trang liên hệ, JSON-LD `Store`. Trang liên hệ nhúng bản đồ (tải lười). Không có địa chỉ đón khách → không khai `Store`, chỉ `Organization`.
- **BR-SEO-98** Trang "Giới thiệu" kể câu chuyện thật, ảnh thật cửa hàng/người bán, cách chọn hàng; trang tác giả có tiểu sử thật; chính sách đầy đủ (BR-CNT-03) — tín hiệu tin cậy (E-E-A-T).

## 15. Kế hoạch nội dung (vận hành, không phải code)

- Trang trụ cột theo **chất liệu** (tử nê, lục nê, đoạn nê, chu nê...) và theo **loại sản phẩm** ("Hướng dẫn chọn ấm tử sa"). Bài vệ tinh: cách khai ấm, phân biệt tử sa, chọn dung tích theo loại trà, bảo quản, so sánh chất đất, giới thiệu nghệ nhân. Mỗi bài link về trang trụ cột và 3–6 sản phẩm phù hợp.
- Mỗi tuần 1–2 bài: chọn từ khóa từ Search Console (truy vấn đã có hiển thị), gợi ý tìm kiếm của Google, Keyword Planner; ghi `focus_keyword` trước khi viết.
- Hằng tháng: xem SEO Audit, lỗi 404, trang có hiển thị cao nhưng tỷ lệ click thấp (sửa title/description), trang ở vị trí 8–20 (bổ sung nội dung, thêm link nội bộ).
