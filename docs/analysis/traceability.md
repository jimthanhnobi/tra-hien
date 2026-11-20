# Truy vết yêu cầu → task → nghiệm thu/kiểm thử

**Cập nhật hiện hành A01 (18/11/2026):** owner-approved amendment local READY_FOR_LOCAL_G4; xem mục A01 cuối file. Các trạng thái/số dòng baseline bên dưới là lịch sử; implementation/CI/G4 chưa được xác nhận từ amendment này.

**Nguồn:** commit `cadce575597738bf40ad7c45689482de5a5fbc3d`. Gồm 99 FR, 176 BR, 13 NFR; giữ mã gốc, không tạo `REQ-###`.

Ánh xạ task dưới đây là kết quả audit từ phạm vi và tham chiếu trong `07-roadmap.md`; đây là **đề xuất truy vết/kiểm thử**, không phải quyết định sửa roadmap. Mỗi BR cần test mang mã luật theo `00-README.md:31–36` và NFR-09. Chưa chạy bất kỳ test nào.

## Yêu cầu và luật

| ID | Nguồn: dòng · mục | Task liên quan | Bằng chứng cần có |
|---|---|---|---|
| FR-CAT-01 | `docs/spec/01-requirements.md:64` · 4.2 CAT — Catalog | P1-03 | Integration/E2E theo hành vi |
| FR-CAT-02 | `docs/spec/01-requirements.md:66` · 4.2 CAT — Catalog | P1-04 | Integration/E2E theo hành vi |
| FR-CAT-03 | `docs/spec/01-requirements.md:67` · 4.2 CAT — Catalog | P1-04 | Integration/E2E theo hành vi |
| FR-CAT-04 | `docs/spec/01-requirements.md:68` · 4.2 CAT — Catalog | P1-05 | Integration/E2E theo hành vi |
| FR-CAT-05 | `docs/spec/01-requirements.md:69` · 4.2 CAT — Catalog | P1-05 | Integration/E2E theo hành vi |
| FR-CAT-06 | `docs/spec/01-requirements.md:70` · 4.2 CAT — Catalog | P1-02 | Integration/E2E theo hành vi |
| FR-CAT-07 | `docs/spec/01-requirements.md:71` · 4.2 CAT — Catalog | P1-05 | Integration/E2E theo hành vi |
| FR-CAT-08 | `docs/spec/01-requirements.md:72` · 4.2 CAT — Catalog | P1-04 | Integration/E2E theo hành vi |
| FR-CAT-09 | `docs/spec/01-requirements.md:73` · 4.2 CAT — Catalog | P1-07 | Integration/E2E theo hành vi |
| FR-CAT-10 | `docs/spec/01-requirements.md:74` · 4.2 CAT — Catalog | P1-14, P3-08 | Integration/E2E theo hành vi |
| FR-CAT-11 | `docs/spec/01-requirements.md:75` · 4.2 CAT — Catalog | P1-14, P3-08 | Integration/E2E theo hành vi |
| FR-CNT-01 | `docs/spec/01-requirements.md:164` · 4.12 CNT — Nội dung và SEO | P3-07 | Integration/E2E theo hành vi |
| FR-CNT-02 | `docs/spec/01-requirements.md:165` · 4.12 CNT — Nội dung và SEO | P3-07 | Integration/E2E theo hành vi |
| FR-CNT-03 | `docs/spec/01-requirements.md:166` · 4.12 CNT — Nội dung và SEO | P3-09 | Integration/E2E theo hành vi |
| FR-CNT-04 | `docs/spec/01-requirements.md:167` · 4.12 CNT — Nội dung và SEO | P3-09 | Integration/E2E theo hành vi |
| FR-CNT-05 | `docs/spec/01-requirements.md:168` · 4.12 CNT — Nội dung và SEO | P3-09 | Integration/E2E theo hành vi |
| FR-CNT-06 | `docs/spec/01-requirements.md:175` · 4.12 CNT — Nội dung và SEO | P0-09 | Integration/E2E theo hành vi |
| FR-CNT-07 | `docs/spec/01-requirements.md:169` · 4.12 CNT — Nội dung và SEO | P3-14 | Integration/E2E theo hành vi |
| FR-CNT-08 | `docs/spec/01-requirements.md:170` · 4.12 CNT — Nội dung và SEO | P3-14 | Integration/E2E theo hành vi |
| FR-CNT-09 | `docs/spec/01-requirements.md:171` · 4.12 CNT — Nội dung và SEO | P3-15 | Integration/E2E theo hành vi |
| FR-CNT-10 | `docs/spec/01-requirements.md:172` · 4.12 CNT — Nội dung và SEO | P5-07 | Integration/E2E theo hành vi |
| FR-CNT-11 | `docs/spec/01-requirements.md:173` · 4.12 CNT — Nội dung và SEO | P3-17 | Integration/E2E theo hành vi |
| FR-CNT-12 | `docs/spec/01-requirements.md:174` · 4.12 CNT — Nội dung và SEO | P3-16, P3-18 | Integration/E2E theo hành vi |
| FR-CRM-01 | `docs/spec/01-requirements.md:138` · 4.10 CRM — Khách hàng | P2-02 | Integration/E2E theo hành vi |
| FR-CRM-02 | `docs/spec/01-requirements.md:139` · 4.10 CRM — Khách hàng | P2-02 | Integration/E2E theo hành vi |
| FR-CRM-03 | `docs/spec/01-requirements.md:140` · 4.10 CRM — Khách hàng | P2-02 | Integration/E2E theo hành vi |
| FR-CRM-04 | `docs/spec/01-requirements.md:141` · 4.10 CRM — Khách hàng | P2-03 | Integration/E2E theo hành vi |
| FR-CRM-05 | `docs/spec/01-requirements.md:142` · 4.10 CRM — Khách hàng | P2-04 | Integration/E2E theo hành vi |
| FR-CRM-06 | `docs/spec/01-requirements.md:143` · 4.10 CRM — Khách hàng | P2-04 | Integration/E2E theo hành vi |
| FR-CRM-07 | `docs/spec/01-requirements.md:144` · 4.10 CRM — Khách hàng | P2-11 | Integration/E2E theo hành vi |
| FR-CRM-08 | `docs/spec/01-requirements.md:145` · 4.10 CRM — Khách hàng | P2-12, P5-06 | Integration/E2E theo hành vi |
| FR-CRM-09 | `docs/spec/01-requirements.md:146` · 4.10 CRM — Khách hàng | P2-02 | Integration/E2E theo hành vi |
| FR-CRM-10 | `docs/spec/01-requirements.md:147` · 4.10 CRM — Khách hàng | P2-12 | Integration/E2E theo hành vi |
| FR-CRM-11 | `docs/spec/01-requirements.md:148` · 4.10 CRM — Khách hàng | P2-12 | Integration/E2E theo hành vi |
| FR-CRM-12 | `docs/spec/01-requirements.md:149` · 4.10 CRM — Khách hàng | P2-12 | Integration/E2E theo hành vi |
| FR-CRT-01 | `docs/spec/01-requirements.md:97` · 4.5 CRT — Giỏ hàng và checkout | P3-01 | Integration/E2E theo hành vi |
| FR-CRT-02 | `docs/spec/01-requirements.md:98` · 4.5 CRT — Giỏ hàng và checkout | P3-01 | Integration/E2E theo hành vi |
| FR-CRT-03 | `docs/spec/01-requirements.md:99` · 4.5 CRT — Giỏ hàng và checkout | P3-02 | Integration/E2E theo hành vi |
| FR-CRT-04 | `docs/spec/01-requirements.md:100` · 4.5 CRT — Giỏ hàng và checkout | P3-02 | Integration/E2E theo hành vi |
| FR-CRT-05 | `docs/spec/01-requirements.md:101` · 4.5 CRT — Giỏ hàng và checkout | P3-02 | Integration/E2E theo hành vi |
| FR-CRT-06 | `docs/spec/01-requirements.md:102` · 4.5 CRT — Giỏ hàng và checkout | P2-13, P3-06 | Integration/E2E theo hành vi |
| FR-CRT-07 | `docs/spec/01-requirements.md:103` · 4.5 CRT — Giỏ hàng và checkout | P3-04 | Integration/E2E theo hành vi |
| FR-FIN-01 | `docs/spec/01-requirements.md:179` · 4.13 FIN — Tài chính và báo cáo | P4-01 | Integration/E2E theo hành vi |
| FR-FIN-02 | `docs/spec/01-requirements.md:180` · 4.13 FIN — Tài chính và báo cáo | P4-02 | Integration/E2E theo hành vi |
| FR-FIN-03 | `docs/spec/01-requirements.md:181` · 4.13 FIN — Tài chính và báo cáo | P4-03 | Integration/E2E theo hành vi |
| FR-FIN-04 | `docs/spec/01-requirements.md:182` · 4.13 FIN — Tài chính và báo cáo | P4-03 | Integration/E2E theo hành vi |
| FR-FIN-05 | `docs/spec/01-requirements.md:183` · 4.13 FIN — Tài chính và báo cáo | P4-04 | Integration/E2E theo hành vi |
| FR-FIN-06 | `docs/spec/01-requirements.md:184` · 4.13 FIN — Tài chính và báo cáo | P4-04 | Integration/E2E theo hành vi |
| FR-FIN-07 | `docs/spec/01-requirements.md:185` · 4.13 FIN — Tài chính và báo cáo | P4-05 | Integration/E2E theo hành vi |
| FR-FIN-08 | `docs/spec/01-requirements.md:186` · 4.13 FIN — Tài chính và báo cáo | P2-16 | Integration/E2E theo hành vi |
| FR-IAM-01 | `docs/spec/01-requirements.md:54` · 4.1 IAM — Định danh và phân quyền | P0-08 | Integration/E2E theo hành vi |
| FR-IAM-02 | `docs/spec/01-requirements.md:55` · 4.1 IAM — Định danh và phân quyền | P0-08 | Integration/E2E theo hành vi |
| FR-IAM-03 | `docs/spec/01-requirements.md:56` · 4.1 IAM — Định danh và phân quyền | P0-08 | Integration/E2E theo hành vi |
| FR-IAM-04 | `docs/spec/01-requirements.md:57` · 4.1 IAM — Định danh và phân quyền | P3-05 | Integration/E2E theo hành vi |
| FR-IAM-05 | `docs/spec/01-requirements.md:58` · 4.1 IAM — Định danh và phân quyền | P3-05 | Integration/E2E theo hành vi |
| FR-IAM-06 | `docs/spec/01-requirements.md:59` · 4.1 IAM — Định danh và phân quyền | P3-05, P6-04 | Integration/E2E theo hành vi |
| FR-IAM-07 | `docs/spec/01-requirements.md:60` · 4.1 IAM — Định danh và phân quyền | P6-03, P6-04 | Integration/E2E theo hành vi |
| FR-INV-01 | `docs/spec/01-requirements.md:79` · 4.3 INV — Kho | P1-08 | Integration/E2E theo hành vi |
| FR-INV-02 | `docs/spec/01-requirements.md:80` · 4.3 INV — Kho | P1-09 | Integration/E2E theo hành vi |
| FR-INV-03 | `docs/spec/01-requirements.md:81` · 4.3 INV — Kho | P1-08 | Integration/E2E theo hành vi |
| FR-INV-04 | `docs/spec/01-requirements.md:82` · 4.3 INV — Kho | P1-09 | Integration/E2E theo hành vi |
| FR-INV-05 | `docs/spec/01-requirements.md:83` · 4.3 INV — Kho | P1-09 | Integration/E2E theo hành vi |
| FR-INV-06 | `docs/spec/01-requirements.md:84` · 4.3 INV — Kho | P1-08 | Integration/E2E theo hành vi |
| FR-INV-07 | `docs/spec/01-requirements.md:85` · 4.3 INV — Kho | P1-10 | Integration/E2E theo hành vi |
| FR-INV-08 | `docs/spec/01-requirements.md:86` · 4.3 INV — Kho | P2-05 | Integration/E2E theo hành vi |
| FR-MKT-01 | `docs/spec/01-requirements.md:153` · 4.11 MKT — Marketing và giới thiệu | P5-02 | Integration/E2E theo hành vi |
| FR-MKT-02 | `docs/spec/01-requirements.md:154` · 4.11 MKT — Marketing và giới thiệu | P5-02 | Integration/E2E theo hành vi |
| FR-MKT-03 | `docs/spec/01-requirements.md:155` · 4.11 MKT — Marketing và giới thiệu | P5-03 | Integration/E2E theo hành vi |
| FR-MKT-04 | `docs/spec/01-requirements.md:156` · 4.11 MKT — Marketing và giới thiệu | P5-04 | Integration/E2E theo hành vi |
| FR-MKT-05 | `docs/spec/01-requirements.md:157` · 4.11 MKT — Marketing và giới thiệu | P5-01 | Integration/E2E theo hành vi |
| FR-MKT-06 | `docs/spec/01-requirements.md:158` · 4.11 MKT — Marketing và giới thiệu | P5-05 | Integration/E2E theo hành vi |
| FR-MKT-07 | `docs/spec/01-requirements.md:159` · 4.11 MKT — Marketing và giới thiệu | P4-01, P5-02 | Integration/E2E theo hành vi |
| FR-MKT-08 | `docs/spec/01-requirements.md:160` · 4.11 MKT — Marketing và giới thiệu | P3-02, P5-01 | Integration/E2E theo hành vi |
| FR-ORD-01 | `docs/spec/01-requirements.md:107` · 4.6 ORD — Đơn hàng | P2-15 | Integration/E2E theo hành vi |
| FR-ORD-02 | `docs/spec/01-requirements.md:108` · 4.6 ORD — Đơn hàng | P2-15 | Integration/E2E theo hành vi |
| FR-ORD-03 | `docs/spec/01-requirements.md:109` · 4.6 ORD — Đơn hàng | P2-06, P2-15 | Integration/E2E theo hành vi |
| FR-ORD-04 | `docs/spec/01-requirements.md:110` · 4.6 ORD — Đơn hàng | P2-05 | Integration/E2E theo hành vi |
| FR-ORD-05 | `docs/spec/01-requirements.md:111` · 4.6 ORD — Đơn hàng | P2-06 | Integration/E2E theo hành vi |
| FR-ORD-06 | `docs/spec/01-requirements.md:112` · 4.6 ORD — Đơn hàng | P3-03 | Integration/E2E theo hành vi |
| FR-ORD-07 | `docs/spec/01-requirements.md:113` · 4.6 ORD — Đơn hàng | P2-15 | Integration/E2E theo hành vi |
| FR-PAY-01 | `docs/spec/01-requirements.md:117` · 4.7 PAY — Thanh toán | P2-07 | Integration/E2E theo hành vi |
| FR-PAY-02 | `docs/spec/01-requirements.md:118` · 4.7 PAY — Thanh toán | P2-08, P2-09 | Integration/E2E theo hành vi |
| FR-PAY-03 | `docs/spec/01-requirements.md:119` · 4.7 PAY — Thanh toán | P2-07 | Integration/E2E theo hành vi |
| FR-PAY-04 | `docs/spec/01-requirements.md:120` · 4.7 PAY — Thanh toán | P2-07 | Integration/E2E theo hành vi |
| FR-PAY-05 | `docs/spec/01-requirements.md:121` · 4.7 PAY — Thanh toán | P2-15 | Integration/E2E theo hành vi |
| FR-PLT-01 | `docs/spec/01-requirements.md:190` · 4.14 PLT — Nền tảng | P0-05 | Integration/E2E theo hành vi |
| FR-PLT-02 | `docs/spec/01-requirements.md:191` · 4.14 PLT — Nền tảng | P0-06, P3-03 | Integration/E2E theo hành vi |
| FR-PLT-03 | `docs/spec/01-requirements.md:192` · 4.14 PLT — Nền tảng | P2-13 | Integration/E2E theo hành vi |
| FR-PRC-01 | `docs/spec/01-requirements.md:90` · 4.4 PRC — Giá và khuyến mãi | P1-05 | Integration/E2E theo hành vi |
| FR-PRC-02 | `docs/spec/01-requirements.md:91` · 4.4 PRC — Giá và khuyến mãi | P1-06 | Integration/E2E theo hành vi |
| FR-PRC-03 | `docs/spec/01-requirements.md:92` · 4.4 PRC — Giá và khuyến mãi | P2-01, P5-05 | Integration/E2E theo hành vi |
| FR-PRC-04 | `docs/spec/01-requirements.md:93` · 4.4 PRC — Giá và khuyến mãi | P1-06 | Integration/E2E theo hành vi |
| FR-RET-01 | `docs/spec/01-requirements.md:132` · 4.9 RET — Đổi trả | P2-10 | Integration/E2E theo hành vi |
| FR-RET-02 | `docs/spec/01-requirements.md:133` · 4.9 RET — Đổi trả | P2-10 | Integration/E2E theo hành vi |
| FR-RET-03 | `docs/spec/01-requirements.md:134` · 4.9 RET — Đổi trả | P2-10 | Integration/E2E theo hành vi |
| FR-SHP-01 | `docs/spec/01-requirements.md:125` · 4.8 SHP — Giao hàng | P0-09 | Integration/E2E theo hành vi |
| FR-SHP-02 | `docs/spec/01-requirements.md:126` · 4.8 SHP — Giao hàng | P2-08 | Integration/E2E theo hành vi |
| FR-SHP-03 | `docs/spec/01-requirements.md:127` · 4.8 SHP — Giao hàng | P2-08 | Integration/E2E theo hành vi |
| FR-SHP-04 | `docs/spec/01-requirements.md:128` · 4.8 SHP — Giao hàng | P6-02 | Integration/E2E theo hành vi |
| BR-CAT-01 | `docs/spec/04-business-rules.md:24` · 2. CAT — Catalog | P1-04, P1-05 | Test tên `BR_CAT_01` (unit/integration) |
| BR-CAT-02 | `docs/spec/04-business-rules.md:25` · 2. CAT — Catalog | P1-05 | Test tên `BR_CAT_02` (unit/integration) |
| BR-CAT-03 | `docs/spec/04-business-rules.md:26` · 2. CAT — Catalog | P1-05 | Test tên `BR_CAT_03` (unit/integration) |
| BR-CAT-04 | `docs/spec/04-business-rules.md:27` · 2. CAT — Catalog | P1-05 | Test tên `BR_CAT_04` (unit/integration) |
| BR-CAT-05 | `docs/spec/04-business-rules.md:37` · 2. CAT — Catalog | P1-05, P1-14 | Test tên `BR_CAT_05` (unit/integration) |
| BR-CAT-06 | `docs/spec/04-business-rules.md:41` · 2. CAT — Catalog | P1-05 | Test tên `BR_CAT_06` (unit/integration) |
| BR-CAT-07 | `docs/spec/04-business-rules.md:42` · 2. CAT — Catalog | P1-05 | Test tên `BR_CAT_07` (unit/integration) |
| BR-CAT-08 | `docs/spec/04-business-rules.md:43` · 2. CAT — Catalog | P1-04, P1-05 | Test tên `BR_CAT_08` (unit/integration) |
| BR-CAT-09 | `docs/spec/04-business-rules.md:49` · 2. CAT — Catalog | P1-04 | Test tên `BR_CAT_09` (unit/integration) |
| BR-CAT-10 | `docs/spec/04-business-rules.md:50` · 2. CAT — Catalog | P1-05 | Test tên `BR_CAT_10` (unit/integration) |
| BR-CAT-11 | `docs/spec/04-business-rules.md:51` · 2. CAT — Catalog | P1-14 | Test tên `BR_CAT_11` (unit/integration) |
| BR-CAT-12 | `docs/spec/04-business-rules.md:52` · 2. CAT — Catalog | P3-09 | Test tên `BR_CAT_12` (unit/integration) |
| BR-CAT-13 | `docs/spec/04-business-rules.md:53` · 2. CAT — Catalog | P1-07 | Test tên `BR_CAT_13` (unit/integration) |
| BR-CNT-01 | `docs/spec/04-business-rules.md:237` · 12. CNT — Nội dung và SEO | P3-07 | Test tên `BR_CNT_01` (unit/integration) |
| BR-CNT-02 | `docs/spec/04-business-rules.md:238` · 12. CNT — Nội dung và SEO | P3-07 | Test tên `BR_CNT_02` (unit/integration) |
| BR-CNT-03 | `docs/spec/04-business-rules.md:239` · 12. CNT — Nội dung và SEO | P3-07 | Test tên `BR_CNT_03` (unit/integration) |
| BR-CNT-04 | `docs/spec/04-business-rules.md:240` · 12. CNT — Nội dung và SEO | P3-09 | Test tên `BR_CNT_04` (unit/integration) |
| BR-CNT-05 | `docs/spec/04-business-rules.md:241` · 12. CNT — Nội dung và SEO | P3-09 | Test tên `BR_CNT_05` (unit/integration) |
| BR-CNT-06 | `docs/spec/04-business-rules.md:242` · 12. CNT — Nội dung và SEO | P3-07 | Test tên `BR_CNT_06` (unit/integration) |
| BR-CRM-01 | `docs/spec/04-business-rules.md:200` · 10. CRM — Khách hàng | P2-02 | Test tên `BR_CRM_01` (unit/integration) |
| BR-CRM-02 | `docs/spec/04-business-rules.md:201` · 10. CRM — Khách hàng | P2-02 | Test tên `BR_CRM_02` (unit/integration) |
| BR-CRM-03 | `docs/spec/04-business-rules.md:202` · 10. CRM — Khách hàng | P2-03 | Test tên `BR_CRM_03` (unit/integration) |
| BR-CRM-04 | `docs/spec/04-business-rules.md:203` · 10. CRM — Khách hàng | P2-02 | Test tên `BR_CRM_04` (unit/integration) |
| BR-CRM-05 | `docs/spec/04-business-rules.md:204` · 10. CRM — Khách hàng | P2-12 | Test tên `BR_CRM_05` (unit/integration) |
| BR-CRM-06 | `docs/spec/04-business-rules.md:205` · 10. CRM — Khách hàng | P2-04 | Test tên `BR_CRM_06` (unit/integration) |
| BR-CRM-07 | `docs/spec/04-business-rules.md:206` · 10. CRM — Khách hàng | P2-04 | Test tên `BR_CRM_07` (unit/integration) |
| BR-CRM-08 | `docs/spec/04-business-rules.md:207` · 10. CRM — Khách hàng | P2-11 | Test tên `BR_CRM_08` (unit/integration) |
| BR-CRM-09 | `docs/spec/04-business-rules.md:208` · 10. CRM — Khách hàng | P2-12, P5-02, P5-06 | Test tên `BR_CRM_09` (unit/integration) |
| BR-CRM-10 | `docs/spec/04-business-rules.md:213` · 10. CRM — Khách hàng | P2-12, P5-02 | Test tên `BR_CRM_10` (unit/integration) |
| BR-CRM-11 | `docs/spec/04-business-rules.md:214` · 10. CRM — Khách hàng | P2-12 | Test tên `BR_CRM_11` (unit/integration) |
| BR-CRM-12 | `docs/spec/04-business-rules.md:215` · 10. CRM — Khách hàng | P2-12 | Test tên `BR_CRM_12` (unit/integration) |
| BR-CRM-13 | `docs/spec/04-business-rules.md:216` · 10. CRM — Khách hàng | P2-12 | Test tên `BR_CRM_13` (unit/integration) |
| BR-CRM-14 | `docs/spec/04-business-rules.md:217` · 10. CRM — Khách hàng | P2-02 | Test tên `BR_CRM_14` (unit/integration) |
| BR-CRT-01 | `docs/spec/04-business-rules.md:91` · 5. CRT — Giỏ hàng và checkout | P3-01 | Test tên `BR_CRT_01` (unit/integration) |
| BR-CRT-02 | `docs/spec/04-business-rules.md:92` · 5. CRT — Giỏ hàng và checkout | P3-01 | Test tên `BR_CRT_02` (unit/integration) |
| BR-CRT-03 | `docs/spec/04-business-rules.md:93` · 5. CRT — Giỏ hàng và checkout | P3-01 | Test tên `BR_CRT_03` (unit/integration) |
| BR-CRT-04 | `docs/spec/04-business-rules.md:94` · 5. CRT — Giỏ hàng và checkout | P3-01 | Test tên `BR_CRT_04` (unit/integration) |
| BR-CRT-05 | `docs/spec/04-business-rules.md:95` · 5. CRT — Giỏ hàng và checkout | P2-05, P3-02 | Test tên `BR_CRT_05` (unit/integration) |
| BR-CRT-06 | `docs/spec/04-business-rules.md:102` · 5. CRT — Giỏ hàng và checkout | P3-02 | Test tên `BR_CRT_06` (unit/integration) |
| BR-CRT-07 | `docs/spec/04-business-rules.md:113` · 5. CRT — Giỏ hàng và checkout | P3-02 | Test tên `BR_CRT_07` (unit/integration) |
| BR-CRT-08 | `docs/spec/04-business-rules.md:118` · 5. CRT — Giỏ hàng và checkout | P3-02 | Test tên `BR_CRT_08` (unit/integration) |
| BR-CRT-09 | `docs/spec/04-business-rules.md:119` · 5. CRT — Giỏ hàng và checkout | P3-02 | Test tên `BR_CRT_09` (unit/integration) |
| BR-FIN-01 | `docs/spec/04-business-rules.md:248` · 13. FIN — Báo cáo tài chính | P4-02 | Test tên `BR_FIN_01` (unit/integration) |
| BR-FIN-02 | `docs/spec/04-business-rules.md:249` · 13. FIN — Báo cáo tài chính | P4-02 | Test tên `BR_FIN_02` (unit/integration) |
| BR-FIN-03 | `docs/spec/04-business-rules.md:270` · 13. FIN — Báo cáo tài chính | P4-02 | Test tên `BR_FIN_03` (unit/integration) |
| BR-FIN-04 | `docs/spec/04-business-rules.md:271` · 13. FIN — Báo cáo tài chính | P4-03 | Test tên `BR_FIN_04` (unit/integration) |
| BR-FIN-05 | `docs/spec/04-business-rules.md:272` · 13. FIN — Báo cáo tài chính | P4-03 | Test tên `BR_FIN_05` (unit/integration) |
| BR-FIN-06 | `docs/spec/04-business-rules.md:273` · 13. FIN — Báo cáo tài chính | P4-04 | Test tên `BR_FIN_06` (unit/integration) |
| BR-FIN-07 | `docs/spec/04-business-rules.md:274` · 13. FIN — Báo cáo tài chính | P4-04 | Test tên `BR_FIN_07` (unit/integration) |
| BR-FIN-08 | `docs/spec/04-business-rules.md:275` · 13. FIN — Báo cáo tài chính | P4-05 | Test tên `BR_FIN_08` (unit/integration) |
| BR-IAM-01 | `docs/spec/04-business-rules.md:10` · 1. IAM — Định danh | P0-08 | Test tên `BR_IAM_01` (unit/integration) |
| BR-IAM-02 | `docs/spec/04-business-rules.md:11` · 1. IAM — Định danh | P0-08 | Test tên `BR_IAM_02` (unit/integration) |
| BR-IAM-03 | `docs/spec/04-business-rules.md:12` · 1. IAM — Định danh | P3-05 | Test tên `BR_IAM_03` (unit/integration) |
| BR-IAM-04 | `docs/spec/04-business-rules.md:16` · 1. IAM — Định danh | P3-05 | Test tên `BR_IAM_04` (unit/integration) |
| BR-IAM-05 | `docs/spec/04-business-rules.md:19` · 1. IAM — Định danh | P3-04 | Test tên `BR_IAM_05` (unit/integration) |
| BR-IAM-06 | `docs/spec/04-business-rules.md:20` · 1. IAM — Định danh | P3-05 | Test tên `BR_IAM_06` (unit/integration) |
| BR-INV-01 | `docs/spec/04-business-rules.md:57` · 3. INV — Kho | P1-08 | Test tên `BR_INV_01` (unit/integration) |
| BR-INV-02 | `docs/spec/04-business-rules.md:58` · 3. INV — Kho | P1-08 | Test tên `BR_INV_02` (unit/integration) |
| BR-INV-03 | `docs/spec/04-business-rules.md:59` · 3. INV — Kho | P2-05 | Test tên `BR_INV_03` (unit/integration) |
| BR-INV-04 | `docs/spec/04-business-rules.md:60` · 3. INV — Kho | P2-05 | Test tên `BR_INV_04` (unit/integration) |
| BR-INV-05 | `docs/spec/04-business-rules.md:64` · 3. INV — Kho | P1-08 | Test tên `BR_INV_05` (unit/integration) |
| BR-INV-06 | `docs/spec/04-business-rules.md:68` · 3. INV — Kho | P1-08 | Test tên `BR_INV_06` (unit/integration) |
| BR-INV-07 | `docs/spec/04-business-rules.md:69` · 3. INV — Kho | P1-09 | Test tên `BR_INV_07` (unit/integration) |
| BR-INV-08 | `docs/spec/04-business-rules.md:70` · 3. INV — Kho | P1-09 | Test tên `BR_INV_08` (unit/integration) |
| BR-INV-09 | `docs/spec/04-business-rules.md:71` · 3. INV — Kho | P1-08, P1-09 | Test tên `BR_INV_09` (unit/integration) |
| BR-INV-10 | `docs/spec/04-business-rules.md:72` · 3. INV — Kho | P1-09 | Test tên `BR_INV_10` (unit/integration) |
| BR-INV-11 | `docs/spec/04-business-rules.md:73` · 3. INV — Kho | P1-10 | Test tên `BR_INV_11` (unit/integration) |
| BR-INV-12 | `docs/spec/04-business-rules.md:74` · 3. INV — Kho | P1-08 | Test tên `BR_INV_12` (unit/integration) |
| BR-INV-13 | `docs/spec/04-business-rules.md:75` · 3. INV — Kho | P1-10 | Test tên `BR_INV_13` (unit/integration) |
| BR-MKT-01 | `docs/spec/04-business-rules.md:221` · 11. MKT — Marketing và giới thiệu | P5-02 | Test tên `BR_MKT_01` (unit/integration) |
| BR-MKT-02 | `docs/spec/04-business-rules.md:222` · 11. MKT — Marketing và giới thiệu | P5-02, P5-03 | Test tên `BR_MKT_02` (unit/integration) |
| BR-MKT-03 | `docs/spec/04-business-rules.md:223` · 11. MKT — Marketing và giới thiệu | P5-03 | Test tên `BR_MKT_03` (unit/integration) |
| BR-MKT-04 | `docs/spec/04-business-rules.md:224` · 11. MKT — Marketing và giới thiệu | P5-01 | Test tên `BR_MKT_04` (unit/integration) |
| BR-MKT-05 | `docs/spec/04-business-rules.md:225` · 11. MKT — Marketing và giới thiệu | P5-05 | Test tên `BR_MKT_05` (unit/integration) |
| BR-MKT-06 | `docs/spec/04-business-rules.md:226` · 11. MKT — Marketing và giới thiệu | P5-05 | Test tên `BR_MKT_06` (unit/integration) |
| BR-MKT-07 | `docs/spec/04-business-rules.md:227` · 11. MKT — Marketing và giới thiệu | P5-05 | Test tên `BR_MKT_07` (unit/integration) |
| BR-MKT-08 | `docs/spec/04-business-rules.md:228` · 11. MKT — Marketing và giới thiệu | P5-05 | Test tên `BR_MKT_08` (unit/integration) |
| BR-MKT-09 | `docs/spec/04-business-rules.md:229` · 11. MKT — Marketing và giới thiệu | P3-02, P5-01 | Test tên `BR_MKT_09` (unit/integration) |
| BR-MKT-10 | `docs/spec/04-business-rules.md:233` · 11. MKT — Marketing và giới thiệu | P5-04 | Test tên `BR_MKT_10` (unit/integration) |
| BR-ORD-01 | `docs/spec/04-business-rules.md:143` · 6.1 State machine | P2-05 | Test tên `BR_ORD_01` (unit/integration) |
| BR-ORD-02 | `docs/spec/04-business-rules.md:144` · 6.1 State machine | P2-05 | Test tên `BR_ORD_02` (unit/integration) |
| BR-ORD-03 | `docs/spec/04-business-rules.md:145` · 6.1 State machine | P2-05 | Test tên `BR_ORD_03` (unit/integration) |
| BR-ORD-04 | `docs/spec/04-business-rules.md:146` · 6.1 State machine | P2-06 | Test tên `BR_ORD_04` (unit/integration) |
| BR-ORD-05 | `docs/spec/04-business-rules.md:147` · 6.1 State machine | P3-03 | Test tên `BR_ORD_05` (unit/integration) |
| BR-ORD-06 | `docs/spec/04-business-rules.md:148` · 6.1 State machine | P3-03 | Test tên `BR_ORD_06` (unit/integration) |
| BR-ORD-07 | `docs/spec/04-business-rules.md:149` · 6.1 State machine | P2-08, P2-11, P5-05 | Test tên `BR_ORD_07` (unit/integration) |
| BR-ORD-08 | `docs/spec/04-business-rules.md:155` · 6.1 State machine | P2-06 | Test tên `BR_ORD_08` (unit/integration) |
| BR-ORD-09 | `docs/spec/04-business-rules.md:156` · 6.1 State machine | P2-06 | Test tên `BR_ORD_09` (unit/integration) |
| BR-ORD-10 | `docs/spec/04-business-rules.md:157` · 6.1 State machine | P2-05 | Test tên `BR_ORD_10` (unit/integration) |
| BR-ORD-11 | `docs/spec/04-business-rules.md:158` · 6.1 State machine | P3-08 | Test tên `BR_ORD_11` (unit/integration) |
| BR-ORD-12 | `docs/spec/04-business-rules.md:159` · 6.1 State machine | P2-15 | Test tên `BR_ORD_12` (unit/integration) |
| BR-PAY-01 | `docs/spec/04-business-rules.md:163` · 7. PAY — Thanh toán | P2-07 | Test tên `BR_PAY_01` (unit/integration) |
| BR-PAY-02 | `docs/spec/04-business-rules.md:164` · 7. PAY — Thanh toán | P2-07 | Test tên `BR_PAY_02` (unit/integration) |
| BR-PAY-03 | `docs/spec/04-business-rules.md:175` · 7. PAY — Thanh toán | P3-03 | Test tên `BR_PAY_03` (unit/integration) |
| BR-PAY-04 | `docs/spec/04-business-rules.md:176` · 7. PAY — Thanh toán | P3-02 | Test tên `BR_PAY_04` (unit/integration) |
| BR-PAY-05 | `docs/spec/04-business-rules.md:177` · 7. PAY — Thanh toán | P2-09 | Test tên `BR_PAY_05` (unit/integration) |
| BR-PAY-06 | `docs/spec/04-business-rules.md:178` · 7. PAY — Thanh toán | P2-07 | Test tên `BR_PAY_06` (unit/integration) |
| BR-PAY-07 | `docs/spec/04-business-rules.md:179` · 7. PAY — Thanh toán | P2-07 | Test tên `BR_PAY_07` (unit/integration) |
| BR-PLT-01 | `docs/spec/04-business-rules.md:279` · 14. PLT — Nền tảng | P0-05 | Test tên `BR_PLT_01` (unit/integration) |
| BR-PLT-02 | `docs/spec/04-business-rules.md:280` · 14. PLT — Nền tảng | P0-06 | Test tên `BR_PLT_02` (unit/integration) |
| BR-PLT-03 | `docs/spec/04-business-rules.md:281` · 14. PLT — Nền tảng | P2-13 | Test tên `BR_PLT_03` (unit/integration) |
| BR-PLT-04 | `docs/spec/04-business-rules.md:293` · 14. PLT — Nền tảng | P2-13 | Test tên `BR_PLT_04` (unit/integration) |
| BR-PRC-01 | `docs/spec/04-business-rules.md:79` · 4. PRC — Giá và khuyến mãi | P1-06 | Test tên `BR_PRC_01` (unit/integration) |
| BR-PRC-02 | `docs/spec/04-business-rules.md:80` · 4. PRC — Giá và khuyến mãi | P1-06 | Test tên `BR_PRC_02` (unit/integration) |
| BR-PRC-03 | `docs/spec/04-business-rules.md:81` · 4. PRC — Giá và khuyến mãi | P1-06 | Test tên `BR_PRC_03` (unit/integration) |
| BR-PRC-04 | `docs/spec/04-business-rules.md:82` · 4. PRC — Giá và khuyến mãi | P5-05 | Test tên `BR_PRC_04` (unit/integration) |
| BR-PRC-05 | `docs/spec/04-business-rules.md:85` · 4. PRC — Giá và khuyến mãi | P5-05 | Test tên `BR_PRC_05` (unit/integration) |
| BR-PRC-06 | `docs/spec/04-business-rules.md:86` · 4. PRC — Giá và khuyến mãi | P2-06 | Test tên `BR_PRC_06` (unit/integration) |
| BR-PRC-07 | `docs/spec/04-business-rules.md:87` · 4. PRC — Giá và khuyến mãi | P2-06 | Test tên `BR_PRC_07` (unit/integration) |
| BR-RET-01 | `docs/spec/04-business-rules.md:191` · 9. RET — Đổi trả | P2-10 | Test tên `BR_RET_01` (unit/integration) |
| BR-RET-02 | `docs/spec/04-business-rules.md:192` · 9. RET — Đổi trả | P2-10 | Test tên `BR_RET_02` (unit/integration) |
| BR-RET-03 | `docs/spec/04-business-rules.md:195` · 9. RET — Đổi trả | P2-10 | Test tên `BR_RET_03` (unit/integration) |
| BR-RET-04 | `docs/spec/04-business-rules.md:196` · 9. RET — Đổi trả | P2-10 | Test tên `BR_RET_04` (unit/integration) |
| BR-SEO-01 | `docs/spec/09-seo.md:34` · 1. Cấu trúc URL | P3-09 | Test tên `BR_SEO_01` (unit/integration) |
| BR-SEO-02 | `docs/spec/09-seo.md:35` · 1. Cấu trúc URL | P3-09 | Test tên `BR_SEO_02` (unit/integration) |
| BR-SEO-03 | `docs/spec/09-seo.md:36` · 1. Cấu trúc URL | P3-09 | Test tên `BR_SEO_03` (unit/integration) |
| BR-SEO-10 | `docs/spec/09-seo.md:53` · 2. Ma trận index | P3-09 | Test tên `BR_SEO_10` (unit/integration) |
| BR-SEO-11 | `docs/spec/09-seo.md:54` · 2. Ma trận index | P3-09 | Test tên `BR_SEO_11` (unit/integration) |
| BR-SEO-12 | `docs/spec/09-seo.md:55` · 2. Ma trận index | P3-09 | Test tên `BR_SEO_12` (unit/integration) |
| BR-SEO-13 | `docs/spec/09-seo.md:67` · 2. Ma trận index | P3-09 | Test tên `BR_SEO_13` (unit/integration) |
| BR-SEO-14 | `docs/spec/09-seo.md:68` · 2. Ma trận index | P3-09 | Test tên `BR_SEO_14` (unit/integration) |
| BR-SEO-20 | `docs/spec/09-seo.md:72` · 3. Vòng đời trang sản phẩm | P3-13 | Test tên `BR_SEO_20` (unit/integration) |
| BR-SEO-21 | `docs/spec/09-seo.md:73` · 3. Vòng đời trang sản phẩm | P3-13 | Test tên `BR_SEO_21` (unit/integration) |
| BR-SEO-22 | `docs/spec/09-seo.md:74` · 3. Vòng đời trang sản phẩm | P3-13 | Test tên `BR_SEO_22` (unit/integration) |
| BR-SEO-23 | `docs/spec/09-seo.md:75` · 3. Vòng đời trang sản phẩm | P3-13 | Test tên `BR_SEO_23` (unit/integration) |
| BR-SEO-30 | `docs/spec/09-seo.md:79` · 4. Metadata và nội dung trên trang | P3-09 | Test tên `BR_SEO_30` (unit/integration) |
| BR-SEO-31 | `docs/spec/09-seo.md` §4 · BR-SEO-31, P1-04 fallback title | P1-04, P3-09 | BR_SEO_31: explicit SEO/missing brand/material template/artisan không main material/word boundaries; contract matrix trong docs/plans/p1-04-implementation.md; runtime NOT RUN |
| BR-SEO-32 | `docs/spec/09-seo.md` §4 · BR-SEO-32, P1-04 description priority | P1-04, P3-09 | BR_SEO_32: source priority/null-empty/sanitize/neutral template tên thực tế/custom SEO không overwrite; contract matrix trong docs/plans/p1-04-implementation.md; runtime NOT RUN |
| BR-SEO-33 | `docs/spec/09-seo.md:92` · 4. Metadata và nội dung trên trang | P3-09 | Test tên `BR_SEO_33` (unit/integration) |
| BR-SEO-34 | `docs/spec/09-seo.md:93` · 4. Metadata và nội dung trên trang | P1-05 | Test tên `BR_SEO_34` (unit/integration) |
| BR-SEO-35 | `docs/spec/09-seo.md` §4 · BR-SEO-35; `docs/spec/05-api.md` §4.2 | P1-04, P3-08, P3-09 | BR_SEO_35: source descriptionHtml/bioHtml, 0/399/400/401, entities/Unicode/NBSP/script-style/exclusions, HTTP 200 warnings không noindex/bypass; contract matrix trong docs/plans/p1-04-implementation.md; runtime NOT RUN |
| BR-SEO-36 | `docs/spec/09-seo.md:99` · 4. Metadata và nội dung trên trang | P3-08, P3-09 | Test tên `BR_SEO_36` (unit/integration) |
| BR-SEO-37 | `docs/spec/09-seo.md:100` · 4. Metadata và nội dung trên trang | P3-08, P3-09 | Test tên `BR_SEO_37` (unit/integration) |
| BR-SEO-40 | `docs/spec/09-seo.md:104` · 5. Trang chủ đề (landing) — thay cho index bộ lọc | P3-14 | Test tên `BR_SEO_40` (unit/integration) |
| BR-SEO-41 | `docs/spec/09-seo.md:106` · 5. Trang chủ đề (landing) — thay cho index bộ lọc | P3-14 | Test tên `BR_SEO_41` (unit/integration) |
| BR-SEO-42 | `docs/spec/09-seo.md:107` · 5. Trang chủ đề (landing) — thay cho index bộ lọc | P3-14 | Test tên `BR_SEO_42` (unit/integration) |
| BR-SEO-50 | `docs/spec/09-seo.md:111` · 6. Liên kết nội bộ | P3-09 | Test tên `BR_SEO_50` (unit/integration) |
| BR-SEO-51 | `docs/spec/09-seo.md:112` · 6. Liên kết nội bộ | P3-09 | Test tên `BR_SEO_51` (unit/integration) |
| BR-SEO-52 | `docs/spec/09-seo.md:113` · 6. Liên kết nội bộ | P3-09 | Test tên `BR_SEO_52` (unit/integration) |
| BR-SEO-53 | `docs/spec/09-seo.md:114` · 6. Liên kết nội bộ | P3-09 | Test tên `BR_SEO_53` (unit/integration) |
| BR-SEO-54 | `docs/spec/09-seo.md:115` · 6. Liên kết nội bộ | P3-09 | Test tên `BR_SEO_54` (unit/integration) |
| BR-SEO-55 | `docs/spec/09-seo.md:116` · 6. Liên kết nội bộ | P3-09 | Test tên `BR_SEO_55` (unit/integration) |
| BR-SEO-56 | `docs/spec/09-seo.md:117` · 6. Liên kết nội bộ | P3-09 | Test tên `BR_SEO_56` (unit/integration) |
| BR-SEO-60 | `docs/spec/09-seo.md:170` · 13. Giám sát và SEO Audit trong Admin | P3-17 | Test tên `BR_SEO_60` (unit/integration) |
| BR-SEO-61 | `docs/spec/09-seo.md:171` · 13. Giám sát và SEO Audit trong Admin | P3-17 | Test tên `BR_SEO_61` (unit/integration) |
| BR-SEO-70 | `docs/spec/09-seo.md:123` · 7. Dữ liệu có cấu trúc (JSON-LD) | P3-09 | Test tên `BR_SEO_70` (unit/integration) |
| BR-SEO-71 | `docs/spec/09-seo.md:124` · 7. Dữ liệu có cấu trúc (JSON-LD) | P3-09 | Test tên `BR_SEO_71` (unit/integration) |
| BR-SEO-72 | `docs/spec/09-seo.md:128` · 7. Dữ liệu có cấu trúc (JSON-LD) | P3-09 | Test tên `BR_SEO_72` (unit/integration) |
| BR-SEO-73 | `docs/spec/09-seo.md:129` · 7. Dữ liệu có cấu trúc (JSON-LD) | P3-09, P3-14 | Test tên `BR_SEO_73` (unit/integration) |
| BR-SEO-74 | `docs/spec/09-seo.md:130` · 7. Dữ liệu có cấu trúc (JSON-LD) | P3-09, P3-14 | Test tên `BR_SEO_74` (unit/integration) |
| BR-SEO-75 | `docs/spec/09-seo.md:131` · 7. Dữ liệu có cấu trúc (JSON-LD) | P3-09 | Test tên `BR_SEO_75` (unit/integration) |
| BR-SEO-76 | `docs/spec/09-seo.md:132` · 7. Dữ liệu có cấu trúc (JSON-LD) | P3-09 | Test tên `BR_SEO_76` (unit/integration) |
| BR-SEO-77 | `docs/spec/09-seo.md:136` · 8. Sitemap và Google Merchant Center | P3-09 | Test tên `BR_SEO_77` (unit/integration) |
| BR-SEO-80 | `docs/spec/09-seo.md:137` · 8. Sitemap và Google Merchant Center | P3-15 | Test tên `BR_SEO_80` (unit/integration) |
| BR-SEO-81 | `docs/spec/09-seo.md:140` · 8. Sitemap và Google Merchant Center | P3-15 | Test tên `BR_SEO_81` (unit/integration) |
| BR-SEO-82 | `docs/spec/09-seo.md:144` · 9. Hình ảnh | P1-02, P3-16 | Test tên `BR_SEO_82` (unit/integration) |
| BR-SEO-83 | `docs/spec/09-seo.md:145` · 9. Hình ảnh | P3-16 | Test tên `BR_SEO_83` (unit/integration) |
| BR-SEO-84 | `docs/spec/09-seo.md:146` · 9. Hình ảnh | P3-16 | Test tên `BR_SEO_84` (unit/integration) |
| BR-SEO-85 | `docs/spec/09-seo.md:150` · 10. Tốc độ (Core Web Vitals) | P3-16 | Test tên `BR_SEO_85` (unit/integration) |
| BR-SEO-86 | `docs/spec/09-seo.md:151` · 10. Tốc độ (Core Web Vitals) | P3-16 | Test tên `BR_SEO_86` (unit/integration) |
| BR-SEO-87 | `docs/spec/09-seo.md:152` · 10. Tốc độ (Core Web Vitals) | P3-16 | Test tên `BR_SEO_87` (unit/integration) |
| BR-SEO-88 | `docs/spec/09-seo.md:153` · 10. Tốc độ (Core Web Vitals) | P3-16, P6-08 | Test tên `BR_SEO_88` (unit/integration) |
| BR-SEO-90 | `docs/spec/09-seo.md:157` · 11. Đánh giá thật từ người mua | P5-07 | Test tên `BR_SEO_90` (unit/integration) |
| BR-SEO-91 | `docs/spec/09-seo.md:158` · 11. Đánh giá thật từ người mua | P5-07 | Test tên `BR_SEO_91` (unit/integration) |
| BR-SEO-92 | `docs/spec/09-seo.md:159` · 11. Đánh giá thật từ người mua | P5-07 | Test tên `BR_SEO_92` (unit/integration) |
| BR-SEO-93 | `docs/spec/09-seo.md:163` · 12. Đo lường | P3-18, P6-08 | Test tên `BR_SEO_93` (unit/integration) |
| BR-SEO-94 | `docs/spec/09-seo.md:164` · 12. Đo lường | P3-18 | Test tên `BR_SEO_94` (unit/integration) |
| BR-SEO-95 | `docs/spec/09-seo.md:165` · 12. Đo lường | P3-18, P4-03, P5-01 | Test tên `BR_SEO_95` (unit/integration) |
| BR-SEO-96 | `docs/spec/09-seo.md:169` · 13. Giám sát và SEO Audit trong Admin | P3-17 | Test tên `BR_SEO_96` (unit/integration) |
| BR-SEO-97 | `docs/spec/09-seo.md:175` · 14. SEO địa phương và thương hiệu | P3-08, P3-12 | Test tên `BR_SEO_97` (unit/integration) |
| BR-SEO-98 | `docs/spec/09-seo.md:176` · 14. SEO địa phương và thương hiệu | P3-08, P3-12 | Test tên `BR_SEO_98` (unit/integration) |
| BR-SHP-01 | `docs/spec/04-business-rules.md:183` · 8. SHP — Giao hàng | P0-09, P3-02 | Test tên `BR_SHP_01` (unit/integration) |
| BR-SHP-02 | `docs/spec/04-business-rules.md:184` · 8. SHP — Giao hàng | P2-08 | Test tên `BR_SHP_02` (unit/integration) |
| BR-SHP-03 | `docs/spec/04-business-rules.md:185` · 8. SHP — Giao hàng | P2-08 | Test tên `BR_SHP_03` (unit/integration) |
| BR-SHP-04 | `docs/spec/04-business-rules.md:186` · 8. SHP — Giao hàng | P2-08 | Test tên `BR_SHP_04` (unit/integration) |
| BR-SHP-05 | `docs/spec/04-business-rules.md:187` · 8. SHP — Giao hàng | P2-08 | Test tên `BR_SHP_05` (unit/integration) |
| NFR-01 | `docs/spec/01-requirements.md:198` · 5. Yêu cầu phi chức năng (NFR) | P3-08, P3-09, P3-16 | Test/đo NFR theo chỉ số gốc |
| NFR-02 | `docs/spec/01-requirements.md:199` · 5. Yêu cầu phi chức năng (NFR) | P2-05, P3-11 | Test/đo NFR theo chỉ số gốc |
| NFR-03 | `docs/spec/01-requirements.md:200` · 5. Yêu cầu phi chức năng (NFR) | P0-07, P2-05, P2-06, P2-07, P3-02, P3-11, P6-01 | Test/đo NFR theo chỉ số gốc |
| NFR-04 | `docs/spec/01-requirements.md:201` · 5. Yêu cầu phi chức năng (NFR) | P0-03, P0-05, P1-14, P2-02, P3-10, P3-18 | Test/đo NFR theo chỉ số gốc |
| NFR-05 | `docs/spec/01-requirements.md:202` · 5. Yêu cầu phi chức năng (NFR) | P3-10, P3-12, P6-07 | Test/đo NFR theo chỉ số gốc |
| NFR-06 | `docs/spec/01-requirements.md:203` · 5. Yêu cầu phi chức năng (NFR) | P3-10, P3-12 | Test/đo NFR theo chỉ số gốc |
| NFR-07 | `docs/spec/01-requirements.md:204` · 5. Yêu cầu phi chức năng (NFR) | P0-08, P1-02, P2-02, P2-06, P2-07, P3-01, P3-02, P3-04, P3-05, P3-10, P3-12 | Test/đo NFR theo chỉ số gốc |
| NFR-08 | `docs/spec/01-requirements.md:205` · 5. Yêu cầu phi chức năng (NFR) | P0-04, P1-01, P2-01, P3-10 | Test/đo NFR theo chỉ số gốc |
| NFR-09 | `docs/spec/01-requirements.md:206` · 5. Yêu cầu phi chức năng (NFR) | P0-10, P0-11; mọi task thực hiện BR | Test/đo NFR theo chỉ số gốc |
| NFR-10 | `docs/spec/01-requirements.md:207` · 5. Yêu cầu phi chức năng (NFR) | P0-12, P1-12, P1-13, P2-14, P2-15, P3-08, P4-06 | Test/đo NFR theo chỉ số gốc |
| NFR-11 | `docs/spec/01-requirements.md:208` · 5. Yêu cầu phi chức năng (NFR) | P0-12, P1-12, P3-08 | Test/đo NFR theo chỉ số gốc |
| NFR-12 | `docs/spec/01-requirements.md:209` · 5. Yêu cầu phi chức năng (NFR) | P2-12, P3-10, P3-12, P3-18, P5-02 | Test/đo NFR theo chỉ số gốc |
| NFR-13 | `docs/spec/01-requirements.md:210` · 5. Yêu cầu phi chức năng (NFR) | P3-10, P3-12 | Test/đo NFR theo chỉ số gốc |

## AC chính thức và task nghiệm thu

AC theo `07-roadmap.md`; số AC không tương đương số task. Những task chưa có AC riêng vẫn phải chứng minh luật/FR liên quan bằng test và DoD pha.

| AC | Nguồn | Task áp dụng | Nội dung cần chứng minh |
|---|---|---|---|
| AC-P0-01 | `docs/spec/07-roadmap.md:33` · P0 — Nền móng | P0-08 | Sai mật khẩu 5 lần → tài khoản bị khóa 15 phút (BR-IAM-01). |
| AC-P0-02 | `docs/spec/07-roadmap.md:34` · P0 — Nền móng | P0-08 | Owner chưa cài TOTP ở Production chỉ gọi được `auth/*` (BR-IAM-02). |
| AC-P0-03 | `docs/spec/07-roadmap.md:35` · P0 — Nền móng | P0-08 | Request POST không có CSRF → 400/403. |
| AC-P0-04 | `docs/spec/07-roadmap.md:36` · P0 — Nền móng | P0-05,P0-09 | Sửa 1 setting → có dòng audit với giá trị trước/sau. |
| AC-P0-05 | `docs/spec/07-roadmap.md:37` · P0 — Nền móng | P0-10 | Test kiến trúc fail nếu Application tham chiếu EF Core hoặc gọi `DateTime.Now`. |
| AC-P1-01 | `docs/spec/07-roadmap.md:63` · P1 — Catalog, Media, Kho | P1-05 | Publish sản phẩm thiếu ALT ảnh bìa và thiếu dung tích (thuộc tính bắt buộc) → 422 liệt kê **cả hai** lỗi. |
| AC-P1-02 | `docs/spec/07-roadmap.md:64` · P1 — Catalog, Media, Kho | P1-05 | Ấm 2 biến thể 180ml/245ml cùng giá trị trục → không publish được (BR-CAT-04.6). |
| AC-P1-03 | `docs/spec/07-roadmap.md:65` · P1 — Catalog, Media, Kho | P1-04,P1-05 | Đổi slug sản phẩm đã publish 2 lần A→B→C → redirect A→C và B→C, không có A→B. |
| AC-P1-04 | `docs/spec/07-roadmap.md:66` · P1 — Catalog, Media, Kho | P1-08 | Nhập kho 2 lần (10 cái × 100.000, 10 cái × 130.000) → `avg_cost = 115.000`; sổ kho có 2 dòng, Σ = on_hand. |
| AC-P1-05 | `docs/spec/07-roadmap.md:67` · P1 — Catalog, Media, Kho | P1-08,P1-09 | Variant độc bản đang có 1 → phiếu nhập thêm 1 bị từ chối `unique_piece_violation`. |
| AC-P1-06 | `docs/spec/07-roadmap.md:68` · P1 — Catalog, Media, Kho | P1-08,P1-14 | Tài khoản `content` gọi API sản phẩm/kho không nhận được field `avgCostVnd` (kiểm JSON, không chỉ UI). |
| AC-P1-07 | `docs/spec/07-roadmap.md:69` · P1 — Catalog, Media, Kho | P1-05,P1-14 | Tìm "am tu sa chu ban" ra ấm tử sa của nghệ nhân Chu Bân. |
| AC-P1-08 | `docs/spec/07-roadmap.md:70` · P1 — Catalog, Media, Kho | P1-09 | Kiểm kê: đếm 5 khi hệ thống 6; trong lúc chờ duyệt bán 1 (on_hand 5) → duyệt xong on_hand = 4. |
| AC-P2-01 | `docs/spec/07-roadmap.md:98` · P2 — CRM + Đơn hàng nội bộ (dùng ngay cho đơn Zalo/điện thoại) | P2-05 | 20 request đồng thời tạo đơn cho chiếc ấm độc bản cuối → đúng 1 đơn thành công, 19 lỗi `out_of_stock`, `reserved = 1`. |
| AC-P2-02 | `docs/spec/07-roadmap.md:99` · P2 — CRM + Đơn hàng nội bộ (dùng ngay cho đơn Zalo/điện thoại) | P2-05 | Hủy đơn `confirmed` → `reserved` giảm, sổ kho không có dòng mới (chưa xuất kho). |
| AC-P2-03 | `docs/spec/07-roadmap.md:100` · P2 — CRM + Đơn hàng nội bộ (dùng ngay cho đơn Zalo/điện thoại) | P2-05 | Chuyển `shipping` → `on_hand` giảm, movement `sale` có `unit_cost`, `order_lines.unit_cost_vnd` được chốt. |
| AC-P2-04 | `docs/spec/07-roadmap.md:101` · P2 — CRM + Đơn hàng nội bộ (dùng ngay cho đơn Zalo/điện thoại) | P2-07 | Ghi 2 lần thanh toán cùng `Idempotency-Key` → 1 payment. Cùng key khác số tiền → 409. |
| AC-P2-05 | `docs/spec/07-roadmap.md:102` · P2 — CRM + Đơn hàng nội bộ (dùng ngay cho đơn Zalo/điện thoại) | P2-06 | Hai nhân viên sửa cùng đơn với cùng `rowVersion` → người thứ hai nhận 409. |
| AC-P2-06 | `docs/spec/07-roadmap.md:103` · P2 — CRM + Đơn hàng nội bộ (dùng ngay cho đơn Zalo/điện thoại) | P2-06 | Nhân viên `sales` giảm giá 15% → 422 `discount_requires_owner`; owner thì được. |
| AC-P2-07 | `docs/spec/07-roadmap.md:104` · P2 — CRM + Đơn hàng nội bộ (dùng ngay cho đơn Zalo/điện thoại) | P2-08,P2-09 | Đơn COD giao thành công → payment `awaiting_settlement`; đối soát 3 đơn với phí 15.000 → `net = gross − 15.000`, 3 payment `confirmed`, đơn `paid`. |
| AC-P2-08 | `docs/spec/07-roadmap.md:105` · P2 — CRM + Đơn hàng nội bộ (dùng ngay cho đơn Zalo/điện thoại) | P2-03 | Import file 500 dòng có 3 SĐT trùng nhau trong file, 2 SĐT đã có → kết quả dòng đúng lý do; hồ sơ có sẵn không bị đổi. |
| AC-P2-09 | `docs/spec/07-roadmap.md:106` · P2 — CRM + Đơn hàng nội bộ (dùng ngay cho đơn Zalo/điện thoại) | P2-02 | Tài khoản `content` gọi `admin/customers` → 403. |
| AC-P2-10 | `docs/spec/07-roadmap.md:107` · P2 — CRM + Đơn hàng nội bộ (dùng ngay cho đơn Zalo/điện thoại) | P2-12 | Xóa dữ liệu khách có đơn đã giao → hồ sơ và đơn bị ẩn danh, báo cáo doanh thu tổng không đổi. |
| AC-P3-01 | `docs/spec/07-roadmap.md:137` · P3 — Website bán hàng (go-live công khai) | P3-02 | Đổi giá sản phẩm khi khách đang ở checkout → đặt đơn nhận 409 `price_changed` với báo giá mới; không tạo đơn. |
| AC-P3-02 | `docs/spec/07-roadmap.md:138` · P3 — Website bán hàng (go-live công khai) | P3-03 | Đơn QR không thanh toán → sau 24h tự `cancelled` (`payment_expired`), hàng trở lại bán được, email gửi khách. |
| AC-P3-03 | `docs/spec/07-roadmap.md:139` · P3 — Website bán hàng (go-live công khai) | P3-03 | Admin ghi đủ tiền cho đơn QR → đơn tự `confirmed`, khách nhận email. |
| AC-P3-04 | `docs/spec/07-roadmap.md:140` · P3 — Website bán hàng (go-live công khai) | P3-05 | Đăng ký tài khoản bằng SĐT của khách cũ → **không** thấy đơn cũ cho tới khi nhân viên xác minh (BR-IAM-04). |
| AC-P3-05 | `docs/spec/07-roadmap.md:141` · P3 — Website bán hàng (go-live công khai) | P3-04 | Tra đơn đúng mã sai SĐT → 404; 11 lần sai trong 1 giờ → bị chặn. |
| AC-P3-06 | `docs/spec/07-roadmap.md:142` · P3 — Website bán hàng (go-live công khai) | P3-09 | Trang sản phẩm có JSON-LD Offer đúng giá hiệu lực và `availability`; hàng độc bản đã bán → `SoldOut`. |
| AC-P3-07 | `docs/spec/07-roadmap.md:143` · P3 — Website bán hàng (go-live công khai) | P3-09 | Sửa giá KM trong Admin → trang sản phẩm cập nhật trong ≤ 1 phút (revalidate tag). |
| AC-P3-08 | `docs/spec/07-roadmap.md:144` · P3 — Website bán hàng (go-live công khai) | P3-16 | Lighthouse CI mobile trên trang chủ, danh mục, sản phẩm, bài viết: Performance ≥ 85, SEO = 100, Accessibility ≥ 90, Best Practices ≥ 90 (BR-SEO-85). |
| AC-P3-09 | `docs/spec/07-roadmap.md:152` · P3 — Website bán hàng (go-live công khai) | P3-09 | `SITE_INDEXABLE=false` → robots chặn toàn bộ, sitemap rỗng. |
| AC-P3-10 | `docs/spec/07-roadmap.md:145` · P3 — Website bán hàng (go-live công khai) | P3-09 | `/danh-muc/am-tu-sa?trang=2` có canonical về chính nó và index; `/danh-muc/am-tu-sa?chat-lieu=chu-ne` có `noindex,follow`; `/san-pham/x?utm_source=zalo` canonical về `/san-pham/x`; `/San-Pham/X/` → 308 `/san-pham/x`. |
| AC-P3-11 | `docs/spec/07-roadmap.md:146` · P3 — Website bán hàng (go-live công khai) | P3-09,P3-15 | Sản phẩm 2 biến thể render `ProductGroup` có 2 `hasVariant`, giá/`availability` từng variant khớp trang và feed Merchant. |
| AC-P3-12 | `docs/spec/07-roadmap.md:147` · P3 — Website bán hàng (go-live công khai) | P3-13 | Ấm độc bản bán xong: trang 200 + `SoldOut` + khối tác phẩm tương tự; giả lập qua 180 ngày → job lưu trữ, URL trả 301 về trang nghệ nhân. |
| AC-P3-13 | `docs/spec/07-roadmap.md:148` · P3 — Website bán hàng (go-live công khai) | P3-13 | Lưu trữ sản phẩm đã publish mà không có đích → API tự chọn đích theo BR-SEO-22; sản phẩm chưa từng publish → 404. |
| AC-P3-14 | `docs/spec/07-roadmap.md:149` · P3 — Website bán hàng (go-live công khai) | P3-14 | Trang chủ đề chỉ có 3 sản phẩm khớp → `noindex`, không có trong sitemap; thêm sản phẩm thứ 4 → index và vào sitemap. |
| AC-P3-15 | `docs/spec/07-roadmap.md:150` · P3 — Website bán hàng (go-live công khai) | P3-17 | SEO Audit phát hiện: 2 sản phẩm trùng title, 1 ảnh bìa thiếu ALT, 1 chuỗi redirect giả lập, 1 sản phẩm mồ côi. |
| AC-P3-16 | `docs/spec/07-roadmap.md:151` · P3 — Website bán hàng (go-live công khai) | P3-17 | Trang không tồn tại trả mã 404 thật; sau 3 lượt truy cập, path xuất hiện trong Admin "Lỗi 404" với hits = 3. |
| AC-P4-01 | `docs/spec/07-roadmap.md:168` · P4 — Báo cáo lãi lỗ | P4-02 | Bộ dữ liệu mẫu cố định (fixture) cho tháng 11 → mọi dòng R1, S, RF, NR, COGS, GP, F, CM1, M, CM2, X, NP khớp bảng tính tay đính kèm trong test. |
| AC-P4-02 | `docs/spec/07-roadmap.md:169` · P4 — Báo cáo lãi lỗ | P4-02 | Đơn giao 31/10 23:30 giờ VN thuộc kỳ tháng 10, không thuộc tháng 11. |
| AC-P4-03 | `docs/spec/07-roadmap.md:170` · P4 — Báo cáo lãi lỗ | P4-03 | Báo cáo kênh và nguồn có cùng tổng NR; không có chỗ nào cộng hai bảng. |
| AC-P4-04 | `docs/spec/07-roadmap.md:171` · P4 — Báo cáo lãi lỗ | P4-02 | Có 1 shipment thiếu chi phí → báo cáo hiện cảnh báo "Lãi chưa chính xác". |
| AC-P5-01 | `docs/spec/07-roadmap.md:188` · P5 — Khai thác khách cũ và giới thiệu | P5-02 | Chiến dịch `new_arrival` loại hết khách chưa có consent; chiến dịch `care` chọn được nhưng không chọn lại khách đã nhận `care` trong 180 ngày. |
| AC-P5-02 | `docs/spec/07-roadmap.md:189` · P5 — Khai thác khách cũ và giới thiệu | P5-01,P5-04 | Khách click link chiến dịch rồi 3 ngày sau đặt đơn → đơn có `campaign_id`; chỉ số chiến dịch tăng. |
| AC-P5-03 | `docs/spec/07-roadmap.md:190` · P5 — Khai thác khách cũ và giới thiệu | P5-05 | Khách cũ (legacy) dùng mã giới thiệu → 422 `referral_not_eligible`. |
| AC-P5-04 | `docs/spec/07-roadmap.md:191` · P5 — Khai thác khách cũ và giới thiệu | P5-05 | Đơn đầu của người được giới thiệu giao xong 7 ngày, không đổi trả → người giới thiệu có voucher; đơn bị hoàn tiền → referral `rejected`. |

## Quyết định và task gốc

DEC-01…24 là quyết định trong `00-README.md:81–108`, không phải AC. Task P0–P6 là 81 hàng trong `07-roadmap.md`; giữ nguyên ID và phụ thuộc gốc. Các sai lệch phụ thuộc/pha được ghi trong [spec-gaps.md](spec-gaps.md).

| DEC | Nguồn | Đối tượng thực hiện/kiểm tra |
|---|---|---|
| DEC-01 | `docs/spec/00-README.md:85` · 6. Nhật ký quyết định (DEC) | P0-01 |
| DEC-02 | `docs/spec/00-README.md:86` · 6. Nhật ký quyết định (DEC) | P3-02 |
| DEC-03 | `docs/spec/00-README.md:87` · 6. Nhật ký quyết định (DEC) | P2-06 |
| DEC-04 | `docs/spec/00-README.md:88` · 6. Nhật ký quyết định (DEC) | P1-03/P1-05 |
| DEC-05 | `docs/spec/00-README.md:89` · 6. Nhật ký quyết định (DEC) | P1-08/P2-05 |
| DEC-06 | `docs/spec/00-README.md:90` · 6. Nhật ký quyết định (DEC) | P1-08 |
| DEC-07 | `docs/spec/00-README.md:91` · 6. Nhật ký quyết định (DEC) | P4-02 |
| DEC-08 | `docs/spec/00-README.md:92` · 6. Nhật ký quyết định (DEC) | P3-02/P6-01 |
| DEC-09 | `docs/spec/00-README.md:93` · 6. Nhật ký quyết định (DEC) | P2-08/P6-02 |
| DEC-10 | `docs/spec/00-README.md:94` · 6. Nhật ký quyết định (DEC) | P3-03 |
| DEC-11 | `docs/spec/00-README.md:95` · 6. Nhật ký quyết định (DEC) | P0-09/P3-02 |
| DEC-12 | `docs/spec/00-README.md:96` · 6. Nhật ký quyết định (DEC) | P2-10 |
| DEC-13 | `docs/spec/00-README.md:97` · 6. Nhật ký quyết định (DEC) | P3-05/P6-03 |
| DEC-14 | `docs/spec/00-README.md:98` · 6. Nhật ký quyết định (DEC) | P0-08/P3-05 |
| DEC-15 | `docs/spec/00-README.md:99` · 6. Nhật ký quyết định (DEC) | P0-01/P3-08 |
| DEC-16 | `docs/spec/00-README.md:100` · 6. Nhật ký quyết định (DEC) | P3-10 |
| DEC-17 | `docs/spec/00-README.md:101` · 6. Nhật ký quyết định (DEC) | P5-02/P5-03 |
| DEC-18 | `docs/spec/00-README.md:102` · 6. Nhật ký quyết định (DEC) | P4-05/P6-05 |
| DEC-19 | `docs/spec/00-README.md:103` · 6. Nhật ký quyết định (DEC) | P3-10 |
| DEC-20 | `docs/spec/00-README.md:104` · 6. Nhật ký quyết định (DEC) | P0-03 |
| DEC-21 | `docs/spec/00-README.md:105` · 6. Nhật ký quyết định (DEC) | P3-09 |
| DEC-22 | `docs/spec/00-README.md:106` · 6. Nhật ký quyết định (DEC) | P3-09/P3-14 |
| DEC-23 | `docs/spec/00-README.md:107` · 6. Nhật ký quyết định (DEC) | P3-13 |
| DEC-24 | `docs/spec/00-README.md:108` · 6. Nhật ký quyết định (DEC) | P3-15/P5-07 |

## Kịch bản nghiệm thu cần làm rõ theo task

Các kịch bản dưới đây là **đề xuất kiểm thử**, không tạo AC ID mới. Chỉ thực hiện sau khi quyết định tại [spec-gaps.md](spec-gaps.md) được chốt cho task tương ứng.

| Task | Given | When | Then và loại kiểm thử |
|---|---|---|---|
| P0-01 | Checkout sạch tại commit đã chốt và toolchain .NET 10/pnpm có sẵn | Build solution và chạy kiểm tra workspace | Đúng 4 project nguồn + 3 test project, hai app và api-client; build không warning, lint/typecheck chạy được. Trường hợp thiếu toolchain báo NOT RUN thay vì PASS. Bảo mật/đồng thời chưa áp dụng vì chưa có endpoint. |
| P0-04/P0-09 | Setting có khóa text, account owner hợp lệ | Đổi setting rồi xem audit | Đúng một log truy ra khóa setting, giá trị trước/sau đúng; field secret/PII bị che. Test migration từ DB rỗng và trường hợp key sai. Phụ thuộc G26/G01. |
| P1-08/P1-09 | Variant độc bản `on_hand=1` | Hai thao tác nhập hoặc điều chỉnh tăng chạy đồng thời | Không thao tác nào đưa `on_hand>1`; lỗi `unique_piece_violation`; số movement khớp số dư. Test biên 0/1 và rollback khi lỗi. |
| P2-05 | Variant cuối có `available=1` | 20 lệnh đặt cùng lúc | Một đơn giữ được hàng, 19 lỗi `out_of_stock`, `reserved=1`, không có order/movement dư; AC-P2-01. |
| P2-10 | Sales có `returns.write` nhưng không có `refunds.write` | Duyệt return với `resolution=refund` | Quyền và trạng thái trả đúng theo quyết định G04/G29; không tạo refund trái phép hoặc nửa chừng. Test owner, sales, thiếu tiền, idempotency, hàng thay thế hết tồn. |
| P3-01/P3-02 | Khách vãng lai có cookie cart | Gửi PUT cart/POST quote/order với token thiếu/sai/hợp lệ | Thiếu/sai token bị 400/403, hợp lệ theo đúng nghiệp vụ; cùng origin và cookie scope đúng; kiểm tra no duplicate order khi retry. Phụ thuộc G09. |
| P3-09/P3-14 | Trang danh mục phân trang, lọc, landing có 3 rồi 4 sản phẩm | Render HTML, sitemap và JSON-LD | Canonical/index/robots đúng AC-P3-09/10/14, sitemap không chứa trang noindex, JSON-LD khớp giá và tình trạng; test 404 và redirect một bước. Phụ thuộc G07. |

## Kiểm tra bao phủ và giới hạn

- FR/BR/NFR chưa có task liên quan theo ánh xạ này: 0.
- `P0-01` không có AC riêng trong roadmap; bằng chứng phù hợp là solution 4 project backend + 3 test project, hai Next app, `packages/api-client`, pnpm workspace, `global.json`, `Directory.Packages.props`, `.editorconfig`, analyzer warning-as-error theo `07-roadmap.md:17` và `02-architecture.md:27–62`. Việc chạy build/test thuộc implementation-slice.
- Ma trận trên chỉ ra nơi cần chứng minh, không tuyên bố đã implement hay đã chạy test. Các luật có nhiều task cần test ở đúng phần hành vi của từng task; không ép task P0-01 làm hành vi của pha sau.
- Các gap G01–G33 và mốc chốt liên quan nằm ở [spec-gaps.md](spec-gaps.md); Q1–Q8 không mặc định chặn P0-01.

## A01 — scoped delta owner-approved 18/11/2026

Input 288ca8b55cab2017c9ee42bd35036e2d2e5bd87c, amendment local UNCOMMITTED/READY_FOR_LOCAL_G4. Dòng baseline ở trên giữ lịch sử. Các nguồn mới dùng heading/ID (line cũ đã dịch chuyển), không tạo task ID hay REQ mới. DDL constraints PASS riêng, các hành vi sau NOT RUN.

| ID/contract | Nguồn hiện hành | Task / acceptance / evidence cần có |
|---|---|---|
| BR-CAT-02/14, DEC-26 | 04 §CAT, schema catalog | P1-01 DDL UNIQUE/regex/sequence PASS; P1-05/07 concurrent generator/bounded retry/stability/manual collision NOT RUN |
| BR-CAT-04/15, BR-PRC-01/02, DEC-25 | 04 §CAT/PRC, schema variants | P1-05/06/07/14, P2-05/06; AC-P1-09 active/inactive/activation/publish/no NULL sale; DDL nullable/range PASS, app NOT RUN |
| BR-CAT-13, FR-CAT-09 | 04 §CAT | P1-07: dry-run no writes, stable key OPEN, không auto-upsert thiếu khóa |
| BR-SEO-34/60/77/80, DEC-27, AC-P1-10 | 09 §2/§3/§4/§8/§13, 04 BR-CAT-04 | P1-05/14 count/warning/outbox; P3-09/15/17 SSR/canonical/robots/sitemap/feed, manual/site priority, 149/150/151, metadata validation unchanged; NOT RUN |
| BR-IAM-07, DEC-29, AC-P0-06 | 04 §IAM, 05 §4.1 | P0-08/12 closure #2 operator/authz/atomic audit/revoke/password+TOTP/rollback/concurrency/coverage; NOT RUN |
| BR-IAM-08, FR-IAM-02/03 | 04 §IAM, 05 §4.1, runbook | Closure owner A staff.manage/CSRF tạo B qua API; B session riêng password/TOTP/login/me + negative 401/403/CSRF; không Staff UI; NOT RUN |
| DEC-30, NFR-04/07/08/09 | 03 §4, 06 §5/§6/§11 | P0-02/04 remediation #1: empty/existing provisioning/grants; backup controlled/read investigation riêng/no IAM creds/DP keys; fingerprints/deny tests NOT RUN |
| BR-RET-01, DEC-28 | schema media, 02 §5.10/05 §4.2 | P1-01 DDL limits PASS; P1-02 whitelist/complete/private/R2 token/slugHint; P2-10 return policy G27 OPEN |
| BR-INV-11/13 | 04 §INV, 03 §3.3, 07 | P1-10/13 internal idempotent/bounded retry; P2-05 reservation reconciliation; P2-13 current alerts/no stale replay; NOT RUN |
| G13, AC-P1-06 | 05 §4.3/02 §7 | P1-08/14 response permission, restricted chỉ còn/hết/no low/count/cost; NOT RUN |
| D1/D9, G02/G21/G32/G38 | 07 P1-01/P1-05/P2-01/P2-05/P5-07 | Closure #1/#2/acceptance trước P1-01; 25 bảng + sequence, deferred FK; redirects nền P1/editor P3; EF chain NOT RUN |
| D10 | AGENTS.md G4, WORKFLOW §5 | A/B vẫn fresh independent reviewer/same worktree/full change set, owner publish, CI published head; G4/CI NOT RUN |

New IDs định nghĩa chính thức: BR-CAT-14/15, BR-IAM-07/08 ở 04; AC-P0-06/AC-P1-09/10 ở 07; DEC-25…30 ở 00. Trạng thái evidence/commands/SQL hash tại docs/reviews/a01-amendment-evidence.md.

## P1-04 — G3 local sau owner approval amendment (19/11/2026)

Input commit `f2227a07aeb75e9e8e5af655378a3d85c6b4e6e7` cùng amendment local
owner-approved sau independent G4 PASS do owner cung cấp. Các mục NOT RUN cũ là
baseline/amendment history; trạng thái implementation hiện hành và command/TRX tại
[P1-04 G3 evidence](../plans/p1-04-g3-evidence.md). Không nhận implementation G4 PASS.

| Requirement | P1-04 code/test trace |
|---|---|
| FR-CAT-02/03/08; 05 §4.2; NFR-07 | CatalogEndpoints + CatalogWorkflow; TaxonomyApiTests CRUD, permissions/CSRF, media/FK, collection order/atomic replace/cross-entity negatives |
| BR-CAT-01/06/08/09; BR-SEO-03 | TaxonomyRules + EF namespace/row locks; TaxonomyRulesTests và TaxonomyApiTests depth/cycles/subtree/concurrent reparent, ever-public/hidden/archive/reuse/flatten/rollback |
| BR-CAT-08; 03 §3.10/§8; NFR-08/09 | P1_04_TaxonomyPublicationHistory và TaxonomyMigrationTests: bốn nullable/no-default columns, preserve rows/FKs, idempotence, runtime role, safe Down/unknown history; EF/DDL parity regression |
| BR-CAT-10/14 | RebuildProductSearchTextHandler; reference rename/outbox rollback/redelivery/current-state search, SKU/reference metadata invariants trong TaxonomyApiTests; không seed CLI/product CRUD |
| BR-SEO-31/32/35; BR-CNT-02 | CatalogSeo/CatalogContentSanitizer; TaxonomyRulesTests/TaxonomyContentTests/TaxonomyApiTests explicit/neutral/brand/HTML/entities/Unicode/0-399-400-401/warnings/no-auto-noindex |
| BR-SEO-23; NFR-04 | AuditValuePolicy scoped taxonomy; protected noindex reason round-trip và PII-negative audit/ProblemDetails tests |

Tests là implementation evidence theo từng command, không thay G4 độc lập; future
P1-03 seed command, live R2/production rollout/CI chưa được xác nhận bởi các fixture này.

### P1-04 — focused fix sau implementation G4 BLOCK

Owner giao sửa P1 redirect graph concurrency và P2 HTML boundaries/null items/NUL.
Giữ spec/amendment/migration và roadmap; exact commands/red-green/full regression
tại [finding fix evidence](../plans/p1-04-g4-fix-evidence.md), không self-approve G4.

| Nguồn | Focused code / tests |
|---|---|
| BR-CAT-08, BR-SEO-03, NFR-09 | EfCatalogStore shared graph lock trước snapshot; TaxonomyG4FindingTests barriers/pg_locks, hai thứ tự cross-prefix/same-prefix, reclamation/rollback/retry/stale-version |
| BR-CNT-02, BR-SEO-31/32/35 | CatalogContentSanitizer bảo toàn separators trước unwrap; TaxonomyContentTests và persisted 399/400/401/SEO/warning/XSS cases |
| 05 §1/§4.2, FR-CAT-08, NFR-07/09 | PutCollectionProductsValidator null-before-dereference; TaxonomyG4ValidationTests và HTTP rejection/no DB/version/audit/outbox changes |
| 05 §1/§4.2, NFR-07/09 | CatalogInput/SaveCatalogValidator/ListCatalogValidator chặn U+0000 decoded strings/JSON keys; unit + POST/PATCH/query bốn resources, nested JSON, valid Unicode/whitespace/null |

### P1-04 — tích hợp với P1-03 đã merge PR #29

Owner cho phép checkpoint/rebase local sau P1-04 G4 PASS. Checkpoint
`c7f279ec687e444d733284b847dc72359c8d511c`; integrated HEAD
`81afe9e3e837f7f5df347368bffb092bc978f5b8` trên main
`0737dea96fa0e93d34b840d2bc8db5e56e4ffe13`. Không push/PR/merge/apply production.
Giữ nguyên spec/migration; exact conflicts, manifest, combined command/results và
accepted P2 tại [integration evidence](../plans/p1-04-integration-evidence.md).

| Nguồn | Integrated verification |
|---|---|
| FR-CAT-01/02/03/08, 05 §4.2 | 32 operations=12 reference+20 taxonomy; preserve typed validator/metadata guards/seed và publication/redirect contracts; integrated OpenAPI/DI test |
| BR-CAT-10/14, NFR-09 | Một canonical message/handler, producer đầy đủ chunk≤200; CatalogIntegrationTests: 0/1/200/201/350/401 cho ba resources, latest names, replay, SKU stability và second-batch failure/rollback/retry |
| FR-CAT-01/03, BR-CAT-08/14 | Seed9/23/14/13→Materials CRUD, repeated seed/no metadata overwrite, publication timestamp, stable IDs/FKs/SKUs, Admin data→seed và conflict atomic rollback |
| BR-CAT-08/09, BR-SEO-03/31/32/35, NFR-07/08/09 | Chạy lại cả Reference/Taxonomy, PG17 migration/parity/graph lock race/HTML/null-item/NUL/auth/CSRF/rowVersion trên integrated suite; không cộng totals cũ |
