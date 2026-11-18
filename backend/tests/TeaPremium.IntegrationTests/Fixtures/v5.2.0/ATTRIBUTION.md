# Pinned administrative-data test fixture

Community source: https://github.com/thanglequoc/vietnamese-provinces-database/tree/62b1abdfe39625289c37a58ef6678e9db3d37f84

Tag v5.2.0; commit `62b1abdfe39625289c37a58ef6678e9db3d37f84`; MIT, Copyright (c) 2021 Thang Le Quoc. Full license/permission notice is retained in `LICENSE` and exact original bytes in `LICENSE.gz`.

The four `.gz` files decompress to the exact upstream raw bytes; hashes are asserted by `P009ImportTests.PinnedFixtureNormalizesToApprovedChecksumAndImportsWithoutNetwork`. Tests never download a live dataset. This fixture is a community conversion and does not assert official full-catalog reconciliation or production readiness. Normalized fixture: 34 provinces, 3321 wards, 3355 CSV data rows; CSV SHA-256 `c023b11aab28f64c652d26512a020f06a0e62005d0f64eeee5543362fb85ab4c`. Future release counts are manifest-specific.
