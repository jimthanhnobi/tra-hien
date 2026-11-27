using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Orders;
using TeaPremium.Application.UseCases;
using TeaPremium.Domain.Orders;
using TeaPremium.Infrastructure.Persistence;
using static TeaPremium.IntegrationTests.AdminOrdersPricingSecurityAndBackfillTests;
using static TeaPremium.IntegrationTests.CustomersApiTests;
using static TeaPremium.IntegrationTests.OrderConcurrencyTests;
using static TeaPremium.IntegrationTests.PaymentRecordingAndIdempotencyTests;

namespace TeaPremium.IntegrationTests;

public sealed class AdminOrdersApiTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    private const string OrdersRoot = "/api/v1/admin/orders";
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task CreateAdminOrderWithExistingCustomerNewCustomerAndZaloOnlyCustomerNormalizesPhoneAndRejectsDuplicate409()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await SeedAdminLocationsAsync(h);
        await h.LoginAsync();

        var existingCustomerId = await CreateHttpCustomerAsync(h, "Nguyễn Khách Cũ", "0904100001");
        var variantId = await SeedVariantOnlyAsync(h, isUniquePiece: false, onHand: 20, priceVnd: 500_000L, avgCostVnd: 200_000L, suffix: "cust1");

        // 1. Existing customer -> 201 Created
        using var respExisting = await PostIdempotentJsonAsync(h.Client, OrdersRoot, new
        {
            customerId = existingCustomerId,
            salesChannel = "zalo_personal",
            paymentMethod = "cod",
            lines = new[] { new { variantId, quantity = 1 } },
            shipping = DefaultShipping()
        }, Guid.CreateVersion7().ToString("D"));
        Assert.Equal(HttpStatusCode.Created, respExisting.StatusCode);
        var createdExisting = (await respExisting.Content.ReadFromJsonAsync<AdminOrderCreatedView>())!;
        Assert.Equal(existingCustomerId, createdExisting.CustomerId);
        Assert.Equal(OrderOrigin.Admin, createdExisting.Origin);
        Assert.Equal(OrderStatus.Pending, createdExisting.Status);
        Assert.Equal(500_000L, createdExisting.SubtotalVnd);
        Assert.Equal(30_000L, createdExisting.ShippingFeeVnd);
        Assert.Equal(530_000L, createdExisting.TotalVnd);

        // 2. Quick-create newCustomer with messy Vietnamese phone -> normalizes to +84904100002 & reserves stock
        using var respNew = await PostIdempotentJsonAsync(h.Client, OrdersRoot, new
        {
            newCustomer = new { fullName = "  Trần Khách Mới  ", phone = "0904 100-002" },
            salesChannel = "facebook",
            paymentMethod = "bank_transfer",
            lines = new[] { new { variantId, quantity = 2 } },
            shipping = DefaultShipping()
        }, Guid.CreateVersion7().ToString("D"));
        Assert.Equal(HttpStatusCode.Created, respNew.StatusCode);
        var createdNew = (await respNew.Content.ReadFromJsonAsync<AdminOrderCreatedView>())!;
        Assert.StartsWith("KH", createdNew.CustomerCode, StringComparison.Ordinal);
        Assert.Equal(1_000_000L, createdNew.SubtotalVnd);
        Assert.Equal(0L, createdNew.ShippingFeeVnd); // >= 1_000_000 free threshold
        Assert.Equal(3, await ScalarAsync<int>(h, $"SELECT reserved FROM inventory.stock_levels WHERE variant_id = '{variantId}'"));

        // 3. Duplicate phone in newCustomer -> 409 customer_phone_exists with data.existingCustomerId, 0 new order/reservation
        using var respDup = await PostIdempotentJsonAsync(h.Client, OrdersRoot, new
        {
            newCustomer = new { fullName = "Trùng Số", phone = "+84904100002" },
            salesChannel = "phone",
            paymentMethod = "cod",
            lines = new[] { new { variantId, quantity = 1 } },
            shipping = DefaultShipping()
        }, Guid.CreateVersion7().ToString("D"));
        Assert.Equal(HttpStatusCode.Conflict, respDup.StatusCode);
        using (var dupDoc = JsonDocument.Parse(await respDup.Content.ReadAsStringAsync()))
        {
            Assert.Equal("customer_phone_exists", dupDoc.RootElement.GetProperty("code").GetString());
            Assert.Equal(createdNew.CustomerId, dupDoc.RootElement.GetProperty("data").GetProperty("existingCustomerId").GetGuid());
        }
        Assert.Equal(2L, await ScalarAsync<long>(h, "SELECT count(*) FROM sales.orders"));
        Assert.Equal(3, await ScalarAsync<int>(h, $"SELECT reserved FROM inventory.stock_levels WHERE variant_id = '{variantId}'"));

        // 4. R-P206-06: Zalo-only customer (phone_e164 IS NULL) falls back to normalized receiverPhone for contact_phone
        using var zaloCustResp = await h.MutateAsync(HttpMethod.Post, CustomersRoot, new
        {
            fullName = "Khách Chỉ Zalo",
            zaloName = "ZaloTea"
        });
        Assert.Equal(HttpStatusCode.Created, zaloCustResp.StatusCode);
        var zaloCustomer = (await zaloCustResp.Content.ReadFromJsonAsync<TeaPremium.Application.Crm.CustomerDetailView>())!;
        Assert.Null(zaloCustomer.PhoneE164);

        using var respZalo = await PostIdempotentJsonAsync(h.Client, OrdersRoot, new
        {
            customerId = zaloCustomer.Id,
            salesChannel = "zalo_oa",
            paymentMethod = "cod",
            lines = new[] { new { variantId, quantity = 1 } },
            shipping = new
            {
                receiverName = "Người Nhận Zalo",
                receiverPhone = "0904 999 888",
                provinceCode = "79",
                wardCode = "79001",
                addressLine = "55 Đồng Khởi"
            }
        }, Guid.CreateVersion7().ToString("D"));
        Assert.Equal(HttpStatusCode.Created, respZalo.StatusCode);
        var zaloOrder = (await respZalo.Content.ReadFromJsonAsync<AdminOrderCreatedView>())!;
        var detail = (await h.Client.GetFromJsonAsync<AdminOrderDetailView>($"{OrdersRoot}/{zaloOrder.Id}"))!;
        Assert.Equal("+84904999888", detail.ContactPhone);
        Assert.Equal("+84904999888", detail.ReceiverPhone);
    }

    [Fact]
    public async Task PostAdminOrdersIdempotencyReplaysIdentical201RejectsMismatchedBodyWith409AndStoresZeroPiiOrCost()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await SeedAdminLocationsAsync(h);
        await h.LoginAsync();
        var variantId = await SeedVariantOnlyAsync(h, isUniquePiece: false, onHand: 10, priceVnd: 500_000L, avgCostVnd: 234_567L, suffix: "idem1");

        var idemKey = Guid.CreateVersion7().ToString("D");
        var payload = new
        {
            newCustomer = new { fullName = "Lê Idempotency SecretName", phone = "0904222333" },
            salesChannel = "zalo_personal",
            paymentMethod = "cod",
            lines = new[] { new { variantId, quantity = 2 } },
            shipping = new
            {
                receiverName = "SecretReceiverName",
                receiverPhone = "0904888777",
                provinceCode = "79",
                wardCode = "79001",
                addressLine = "999 Secret Street",
                customerNote = "SecretCustomerNote"
            },
            internalNote = "SecretInternalNote"
        };

        // Missing Idempotency-Key -> 400 validation_failed
        using var missingKey = await PostIdempotentJsonAsync(h.Client, OrdersRoot, payload, idempotencyKey: null);
        await AssertProblemAsync(missingKey, HttpStatusCode.BadRequest, "validation_failed");

        // First call -> 201 Created
        using var first = await PostIdempotentJsonAsync(h.Client, OrdersRoot, payload, idemKey);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstBytes = await first.Content.ReadAsStringAsync();

        // Replay with identical key & body -> identical 201 bytes, still 1 customer + 1 order + 2 reserved
        using var replay = await PostIdempotentJsonAsync(h.Client, OrdersRoot, payload, idemKey);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        var replayBytes = await replay.Content.ReadAsStringAsync();
        Assert.Equal(firstBytes, replayBytes);
        Assert.Equal(1L, await ScalarAsync<long>(h, "SELECT count(*) FROM sales.orders"));
        Assert.Equal(1L, await ScalarAsync<long>(h, "SELECT count(*) FROM crm.customers"));
        Assert.Equal(2, await ScalarAsync<int>(h, $"SELECT reserved FROM inventory.stock_levels WHERE variant_id = '{variantId}'"));

        // Same key, different payload -> 409 idempotency_key_reused
        var changedPayload = new
        {
            newCustomer = new { fullName = "Lê Idempotency SecretName", phone = "0904222333" },
            salesChannel = "zalo_personal",
            paymentMethod = "cod",
            lines = new[] { new { variantId, quantity = 3 } },
            shipping = DefaultShipping()
        };
        using var conflict = await PostIdempotentJsonAsync(h.Client, OrdersRoot, changedPayload, idemKey);
        await AssertProblemAsync(conflict, HttpStatusCode.Conflict, "idempotency_key_reused");

        // Inspect decoded platform.idempotency_keys.response_body -> ZERO PII and ZERO cost fields
        await using var db = h.OwnerContext();
        var idemRow = await db.IdempotencyKeys.AsNoTracking().SingleAsync();
        using var envelope = JsonDocument.Parse(idemRow.ResponseBody!);
        var decodedBody = Encoding.UTF8.GetString(Convert.FromBase64String(envelope.RootElement.GetProperty("bodyBase64").GetString()!));
        foreach (var forbidden in new[]
        {
            "SecretName", "SecretReceiverName", "0904222333", "904222333", "0904888777", "904888777",
            "Secret Street", "SecretCustomerNote", "SecretInternalNote", "234567", "unitCost", "avgCost"
        })
        {
            Assert.DoesNotContain(forbidden, decodedBody, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task CodConfirmationEnforcedOnBothCreateConfirmNowAndConfirmEndpointRespectsSettingsCatalogKey()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await SeedAdminLocationsAsync(h);
        await h.LoginAsync();

        var customerId = await CreateHttpCustomerAsync(h, "Khách G11", "0904333001");
        var variantId = await SeedVariantOnlyAsync(h, isUniquePiece: false, onHand: 20, priceVnd: 400_000L, avgCostVnd: 150_000L, suffix: "g11");

        // 1. Default orders.cod_requires_call = true: POST confirmNow=true COD without callConfirmed -> 422 cod_confirmation_required
        using var noCallConfirmNow = await PostIdempotentJsonAsync(h.Client, OrdersRoot, new
        {
            customerId,
            salesChannel = "phone",
            paymentMethod = "cod",
            lines = new[] { new { variantId, quantity = 1 } },
            shipping = DefaultShipping(),
            confirmNow = true,
            callConfirmed = false,
            callNote = "Đã gọi"
        }, Guid.CreateVersion7().ToString("D"));
        await AssertProblemAsync(noCallConfirmNow, HttpStatusCode.UnprocessableEntity, "cod_confirmation_required");

        // Missing callNote with callConfirmed=true -> 422 cod_confirmation_required
        using var noNoteConfirmNow = await PostIdempotentJsonAsync(h.Client, OrdersRoot, new
        {
            customerId,
            salesChannel = "phone",
            paymentMethod = "cod",
            lines = new[] { new { variantId, quantity = 1 } },
            shipping = DefaultShipping(),
            confirmNow = true,
            callConfirmed = true
        }, Guid.CreateVersion7().ToString("D"));
        await AssertProblemAsync(noNoteConfirmNow, HttpStatusCode.UnprocessableEntity, "cod_confirmation_required");

        // Both callConfirmed=true AND callNote -> 201 confirmed
        using var okConfirmNow = await PostIdempotentJsonAsync(h.Client, OrdersRoot, new
        {
            customerId,
            salesChannel = "phone",
            paymentMethod = "cod",
            lines = new[] { new { variantId, quantity = 1 } },
            shipping = DefaultShipping(),
            confirmNow = true,
            callConfirmed = true,
            callNote = "Đã gọi xác nhận địa chỉ"
        }, Guid.CreateVersion7().ToString("D"));
        Assert.Equal(HttpStatusCode.Created, okConfirmNow.StatusCode);
        var confirmedImmediate = (await okConfirmNow.Content.ReadFromJsonAsync<AdminOrderCreatedView>())!;
        Assert.Equal(OrderStatus.Confirmed, confirmedImmediate.Status);
        Assert.NotNull(confirmedImmediate.ConfirmedAt);

        // 2. Pending COD order confirmed via POST /confirm
        using var pendingResp = await PostIdempotentJsonAsync(h.Client, OrdersRoot, new
        {
            customerId,
            salesChannel = "zalo_personal",
            paymentMethod = "cod",
            lines = new[] { new { variantId, quantity = 1 } },
            shipping = DefaultShipping(),
            confirmNow = false
        }, Guid.CreateVersion7().ToString("D"));
        var pendingOrder = (await pendingResp.Content.ReadFromJsonAsync<AdminOrderCreatedView>())!;

        using var confirmMissingCall = await h.MutateAsync(HttpMethod.Post, $"{OrdersRoot}/{pendingOrder.Id}/confirm", new
        {
            rowVersion = pendingOrder.RowVersion,
            callConfirmed = false,
            callNote = "Chưa gọi"
        });
        await AssertProblemAsync(confirmMissingCall, HttpStatusCode.UnprocessableEntity, "cod_confirmation_required");

        using var confirmOk = await h.MutateAsync(HttpMethod.Post, $"{OrdersRoot}/{pendingOrder.Id}/confirm", new
        {
            rowVersion = pendingOrder.RowVersion,
            callConfirmed = true,
            callNote = "Khách nghe máy xác nhận"
        });
        Assert.Equal(HttpStatusCode.OK, confirmOk.StatusCode);
        var confirmedView = (await confirmOk.Content.ReadFromJsonAsync<AdminOrderDetailView>())!;
        Assert.Equal(OrderStatus.Confirmed, confirmedView.Status);
        Assert.Contains(confirmedView.History, x => x.ToStatus == OrderStatus.Confirmed && x.Reason == "Khách nghe máy xác nhận");

        // 3. Toggle orders.cod_requires_call = false via Settings API -> COD confirm succeeds without callConfirmed
        var settingsGroup = (await h.Client.GetFromJsonAsync<TeaPremium.Application.Settings.SettingsGroup>("/api/v1/admin/settings/orders"))!;
        using var putSetting = await h.MutateAsync(HttpMethod.Put, "/api/v1/admin/settings/orders", new
        {
            version = settingsGroup.Version,
            values = new Dictionary<string, object?> { ["orders.cod_requires_call"] = false }
        });
        Assert.Equal(HttpStatusCode.OK, putSetting.StatusCode);

        using var pending2Resp = await PostIdempotentJsonAsync(h.Client, OrdersRoot, new
        {
            customerId,
            salesChannel = "zalo_personal",
            paymentMethod = "cod",
            lines = new[] { new { variantId, quantity = 1 } },
            shipping = DefaultShipping(),
            confirmNow = false
        }, Guid.CreateVersion7().ToString("D"));
        var pending2 = (await pending2Resp.Content.ReadFromJsonAsync<AdminOrderCreatedView>())!;

        using var confirmNoCallOk = await h.MutateAsync(HttpMethod.Post, $"{OrdersRoot}/{pending2.Id}/confirm", new
        {
            rowVersion = pending2.RowVersion
        });
        Assert.Equal(HttpStatusCode.OK, confirmNoCallOk.StatusCode);
        Assert.Equal(OrderStatus.Confirmed, (await confirmNoCallOk.Content.ReadFromJsonAsync<AdminOrderDetailView>())!.Status);
    }

    [Fact]
    public async Task ConcurrentPatchConfirmAndCancelWithSameRowVersionSecondStaffReceives409ConcurrencyConflict()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await SeedAdminLocationsAsync(h);
        await h.LoginAsync();

        var customerId = await CreateHttpCustomerAsync(h, "Khách Concurrency", "0904444001");
        var variantId = await SeedVariantOnlyAsync(h, isUniquePiece: false, onHand: 20, priceVnd: 500_000L, avgCostVnd: 200_000L, suffix: "conc1");

        using var createdResp = await PostIdempotentJsonAsync(h.Client, OrdersRoot, new
        {
            customerId,
            salesChannel = "zalo_personal",
            paymentMethod = "bank_transfer",
            lines = new[] { new { variantId, quantity = 1 } },
            shipping = DefaultShipping()
        }, Guid.CreateVersion7().ToString("D"));
        var created = (await createdResp.Content.ReadFromJsonAsync<AdminOrderCreatedView>())!;
        var staleRowVersion = created.RowVersion;

        // First staff PATCH succeeds and advances xmin
        using var firstPatch = await h.MutateAsync(HttpMethod.Patch, $"{OrdersRoot}/{created.Id}", new
        {
            rowVersion = staleRowVersion,
            internalNote = "Nhân viên 1 sửa trước"
        });
        Assert.Equal(HttpStatusCode.OK, firstPatch.StatusCode);
        var updated = (await firstPatch.Content.ReadFromJsonAsync<AdminOrderDetailView>())!;
        Assert.NotEqual(staleRowVersion, updated.RowVersion);

        // Second staff using stale rowVersion receives 409 concurrency_conflict on PATCH, confirm, and cancel
        using var stalePatch = await h.MutateAsync(HttpMethod.Patch, $"{OrdersRoot}/{created.Id}", new
        {
            rowVersion = staleRowVersion,
            internalNote = "Nhân viên 2 sửa sau"
        });
        await AssertProblemAsync(stalePatch, HttpStatusCode.Conflict, "concurrency_conflict");

        using var staleConfirm = await h.MutateAsync(HttpMethod.Post, $"{OrdersRoot}/{created.Id}/confirm", new
        {
            rowVersion = staleRowVersion
        });
        await AssertProblemAsync(staleConfirm, HttpStatusCode.Conflict, "concurrency_conflict");

        using var staleCancel = await h.MutateAsync(HttpMethod.Post, $"{OrdersRoot}/{created.Id}/cancel", new
        {
            rowVersion = staleRowVersion,
            reason = "Thử hủy với version cũ"
        });
        await AssertProblemAsync(staleCancel, HttpStatusCode.Conflict, "concurrency_conflict");

        // Malformed rowVersion -> 400 validation_failed
        using var badVersion = await h.MutateAsync(HttpMethod.Patch, $"{OrdersRoot}/{created.Id}", new
        {
            rowVersion = "!!!not-a-version!!!",
            internalNote = "Bad"
        });
        await AssertProblemAsync(badVersion, HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task PatchOrderDroppingBelowVoucherMinOrderAutoRemovesVoucherRevertsRedemptionAndReturnsWarning()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await SeedAdminLocationsAsync(h);
        await h.LoginAsync();

        var customerId = await CreateHttpCustomerAsync(h, "Khách Voucher", "0904555001");
        var variantId = await SeedVariantOnlyAsync(h, isUniquePiece: false, onHand: 20, priceVnd: 600_000L, avgCostVnd: 200_000L, suffix: "vouch1");

        var voucherId = Guid.CreateVersion7();
        await h.ExecuteAsync($"""
INSERT INTO pricing.vouchers(id,code,kind,discount_type,discount_value,min_order_vnd,starts_at,ends_at,usage_limit,per_customer_limit,used_count,status)
VALUES ('{voucherId}','MIN1TR','manual','fixed',100000,1000000,now() - interval '1 day',now() + interval '10 days',10,2,0,'active');
""");

        // Place web order of 2 * 600_000 = 1_200_000 with voucher MIN1TR -> discount 100_000, used_count = 1
        var webOrder = await CreateWebBankOrderAsync(h, customerId, variantId, quantity: 2, voucherCode: "MIN1TR");
        Assert.Equal(voucherId, webOrder.VoucherId);
        Assert.Equal(100_000L, webOrder.DiscountVnd);
        Assert.Equal(1, await ScalarAsync<int>(h, $"SELECT used_count FROM pricing.vouchers WHERE id = '{voucherId}'"));

        var detailBefore = (await h.Client.GetFromJsonAsync<AdminOrderDetailView>($"{OrdersRoot}/{webOrder.Id}"))!;

        // PATCH reduces quantity to 1 (subtotal 600_000 < min_order 1_000_000) -> auto-removes voucher + emits warning
        using var patchResp = await h.MutateAsync(HttpMethod.Patch, $"{OrdersRoot}/{webOrder.Id}", new
        {
            rowVersion = detailBefore.RowVersion,
            lines = new[] { new { variantId, quantity = 1 } }
        });
        Assert.Equal(HttpStatusCode.OK, patchResp.StatusCode);
        var patched = (await patchResp.Content.ReadFromJsonAsync<AdminOrderDetailView>())!;

        Assert.Null(patched.VoucherId);
        Assert.Equal(0L, patched.DiscountVnd);
        Assert.Equal(600_000L, patched.SubtotalVnd);
        Assert.Equal(30_000L, patched.ShippingFeeVnd); // web order recalculates shipping on line change!
        Assert.Equal(630_000L, patched.TotalVnd);
        Assert.Contains(patched.Warnings, w => w.Code == "voucher_removed_min_order");

        Assert.Equal(0, await ScalarAsync<int>(h, $"SELECT used_count FROM pricing.vouchers WHERE id = '{voucherId}'"));
        Assert.Equal("reverted", await ScalarAsync<string>(h, $"SELECT status FROM pricing.voucher_redemptions WHERE order_id = '{webOrder.Id}'"));
    }

    [Fact]
    public async Task PatchOrderThreeWayShippingFeeVndDiscountNotePreserveClearAndPaymentStatusRecalculation()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await SeedAdminLocationsAsync(h);
        await h.LoginAsync();

        var customerId = await CreateHttpCustomerAsync(h, "Khách Patch 3-Way", "0904666001");
        var variantId = await SeedVariantOnlyAsync(h, isUniquePiece: false, onHand: 20, priceVnd: 500_000L, avgCostVnd: 200_000L, suffix: "p3w");

        // 1. Admin order with custom shippingFeeVnd = 15_000 and manual discount 50_000
        using var adminResp = await PostIdempotentJsonAsync(h.Client, OrdersRoot, new
        {
            customerId,
            salesChannel = "zalo_personal",
            paymentMethod = "bank_transfer",
            lines = new[] { new { variantId, quantity = 1 } },
            discountVnd = 50_000L,
            discountNote = "Ghi chú giảm ban đầu",
            shippingFeeVnd = 15_000L,
            shipping = DefaultShipping()
        }, Guid.CreateVersion7().ToString("D"));
        Assert.Equal(HttpStatusCode.Created, adminResp.StatusCode);
        var adminOrder = (await adminResp.Content.ReadFromJsonAsync<AdminOrderCreatedView>())!;
        Assert.Equal(465_000L, adminOrder.TotalVnd); // 500k - 50k + 15k

        // (a) shippingFeeVnd OMITTED on admin order when changing lines -> PRESERVES 15_000!
        using var patchOmittedAdmin = await h.MutateAsync(HttpMethod.Patch, $"{OrdersRoot}/{adminOrder.Id}", new
        {
            rowVersion = adminOrder.RowVersion,
            lines = new[] { new { variantId, quantity = 2 } } // subtotal 1_000_000
        });
        Assert.Equal(HttpStatusCode.OK, patchOmittedAdmin.StatusCode);
        var d1 = (await patchOmittedAdmin.Content.ReadFromJsonAsync<AdminOrderDetailView>())!;
        Assert.Equal(15_000L, d1.ShippingFeeVnd); // preserved!
        Assert.Equal(965_000L, d1.TotalVnd);

        // (b) shippingFeeVnd: null -> RECALCULATES per BR-SHP-01 (950k < 1M -> 30_000)!
        using var patchNullFee = await PatchRawJsonAsync(h.Client, $"{OrdersRoot}/{adminOrder.Id}",
            $$"""{"rowVersion":"{{d1.RowVersion}}","shippingFeeVnd":null}""");
        Assert.Equal(HttpStatusCode.OK, patchNullFee.StatusCode);
        var d2 = (await patchNullFee.Content.ReadFromJsonAsync<AdminOrderDetailView>())!;
        Assert.Equal(30_000L, d2.ShippingFeeVnd); // recalculated!
        Assert.Equal(980_000L, d2.TotalVnd);

        // (c) shippingFeeVnd: 22_000 explicit -> APPLIES 22_000; negative -> 400 validation_failed
        using var patchNegFee = await PatchRawJsonAsync(h.Client, $"{OrdersRoot}/{adminOrder.Id}",
            $$"""{"rowVersion":"{{d2.RowVersion}}","shippingFeeVnd":-1}""");
        await AssertProblemAsync(patchNegFee, HttpStatusCode.BadRequest, "validation_failed");

        using var patchExplicitFee = await PatchRawJsonAsync(h.Client, $"{OrdersRoot}/{adminOrder.Id}",
            $$"""{"rowVersion":"{{d2.RowVersion}}","shippingFeeVnd":22000}""");
        Assert.Equal(HttpStatusCode.OK, patchExplicitFee.StatusCode);
        var d3 = (await patchExplicitFee.Content.ReadFromJsonAsync<AdminOrderDetailView>())!;
        Assert.Equal(22_000L, d3.ShippingFeeVnd);

        // (d) discountVnd > 0 without discountNote preserves existing order.DiscountNote; discountVnd == 0 clears discountNote
        using var patchDiscountPreserveNote = await h.MutateAsync(HttpMethod.Patch, $"{OrdersRoot}/{adminOrder.Id}", new
        {
            rowVersion = d3.RowVersion,
            discountVnd = 80_000L
        });
        Assert.Equal(HttpStatusCode.OK, patchDiscountPreserveNote.StatusCode);
        var d4 = (await patchDiscountPreserveNote.Content.ReadFromJsonAsync<AdminOrderDetailView>())!;
        Assert.Equal(80_000L, d4.DiscountVnd);
        Assert.Equal("Ghi chú giảm ban đầu", d4.DiscountNote);

        using var patchClearDiscount = await h.MutateAsync(HttpMethod.Patch, $"{OrdersRoot}/{adminOrder.Id}", new
        {
            rowVersion = d4.RowVersion,
            discountVnd = 0L
        });
        Assert.Equal(HttpStatusCode.OK, patchClearDiscount.StatusCode);
        var d5 = (await patchClearDiscount.Content.ReadFromJsonAsync<AdminOrderDetailView>())!;
        Assert.Equal(0L, d5.DiscountVnd);
        Assert.Null(d5.DiscountNote);
        Assert.Equal(1_022_000L, d5.TotalVnd);

        // Now discountNote is null -> setting discountVnd > 0 without discountNote fails 400 validation_failed
        using var patchMissingNote = await h.MutateAsync(HttpMethod.Patch, $"{OrdersRoot}/{adminOrder.Id}", new
        {
            rowVersion = d5.RowVersion,
            discountVnd = 50_000L
        });
        await AssertProblemAsync(patchMissingNote, HttpStatusCode.BadRequest, "validation_failed");

        // (e) Web order: shippingFeeVnd omitted recalculates when lines/discount change, preserves when only shipping/internalNote change
        var webOrder = await CreateWebBankOrderAsync(h, customerId, variantId, quantity: 2); // 1_000_000 -> shippingFee = 0
        Assert.Equal(0L, webOrder.ShippingFeeVnd);
        var webDetail = (await h.Client.GetFromJsonAsync<AdminOrderDetailView>($"{OrdersRoot}/{webOrder.Id}"))!;

        using var webPatchNoteOnly = await h.MutateAsync(HttpMethod.Patch, $"{OrdersRoot}/{webOrder.Id}", new
        {
            rowVersion = webDetail.RowVersion,
            internalNote = "Ghi chú web"
        });
        var wd1 = (await webPatchNoteOnly.Content.ReadFromJsonAsync<AdminOrderDetailView>())!;
        Assert.Equal(0L, wd1.ShippingFeeVnd); // preserved

        using var webPatchDiscount = await h.MutateAsync(HttpMethod.Patch, $"{OrdersRoot}/{webOrder.Id}", new
        {
            rowVersion = wd1.RowVersion,
            discountVnd = 100_000L,
            discountNote = "Giảm làm tụt dưới ngưỡng freeship"
        });
        var wd2 = (await webPatchDiscount.Content.ReadFromJsonAsync<AdminOrderDetailView>())!;
        Assert.Equal(30_000L, wd2.ShippingFeeVnd); // recalculated because afterDiscount = 900k < 1M!
        Assert.Equal(930_000L, wd2.TotalVnd);

        // (f) BR-PAY-02 payment_status recalculation on PATCH + audit redaction of shipping/internalNote
        // Record confirmed payment of 500_000 on a 500_000 admin order (shippingFee=0 -> total=500_000) -> paid
        using var payOrderResp = await PostIdempotentJsonAsync(h.Client, OrdersRoot, new
        {
            customerId,
            salesChannel = "phone",
            paymentMethod = "bank_transfer",
            lines = new[] { new { variantId, quantity = 1 } },
            shippingFeeVnd = 0L,
            shipping = DefaultShipping()
        }, Guid.CreateVersion7().ToString("D"));
        var payOrder = (await payOrderResp.Content.ReadFromJsonAsync<AdminOrderCreatedView>())!;

        using var recPay = await PostIdempotentJsonAsync(h.Client, $"{OrdersRoot}/{payOrder.Id}/payments", new
        {
            method = "bank_transfer",
            amountVnd = 500_000L,
            receivedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            bankReference = "FT-P206-RECALC"
        }, Guid.CreateVersion7().ToString("D"));
        Assert.Equal(HttpStatusCode.OK, recPay.StatusCode);
        var payBefore = (await h.Client.GetFromJsonAsync<AdminOrderDetailView>($"{OrdersRoot}/{payOrder.Id}"))!;
        Assert.Equal(OrderPaymentStatus.Paid, payBefore.PaymentStatus);

        // Increase lines to 2 (total 1_000_000) + update address & internalNote -> paid -> partially_paid
        using var patchUp = await h.MutateAsync(HttpMethod.Patch, $"{OrdersRoot}/{payOrder.Id}", new
        {
            rowVersion = payBefore.RowVersion,
            lines = new[] { new { variantId, quantity = 2 } },
            shipping = new { addressLine = "SecretNewAddress 88", receiverName = "SecretNewReceiver" },
            internalNote = "SecretUpdatedNote"
        });
        Assert.Equal(HttpStatusCode.OK, patchUp.StatusCode);
        var afterUp = (await patchUp.Content.ReadFromJsonAsync<AdminOrderDetailView>())!;
        Assert.Equal(OrderPaymentStatus.PartiallyPaid, afterUp.PaymentStatus);
        Assert.Equal(500_000L, afterUp.RemainingToPayVnd);
        Assert.False(afterUp.IsOverpaid);

        // Decrease total to 450_000 (qty 1 + 50k discount) -> paid + isOverpaid = true + needsRefund = true (O2=A)
        using var patchDown = await h.MutateAsync(HttpMethod.Patch, $"{OrdersRoot}/{payOrder.Id}", new
        {
            rowVersion = afterUp.RowVersion,
            lines = new[] { new { variantId, quantity = 1 } },
            discountVnd = 50_000L,
            discountNote = "Giảm 50k sau khi thu tiền"
        });
        Assert.Equal(HttpStatusCode.OK, patchDown.StatusCode);
        var afterDown = (await patchDown.Content.ReadFromJsonAsync<AdminOrderDetailView>())!;
        Assert.Equal(OrderPaymentStatus.Paid, afterDown.PaymentStatus);
        Assert.True(afterDown.IsOverpaid);
        Assert.True(afterDown.NeedsRefund);
        Assert.Equal(50_000L, afterDown.OverpaidVnd);

        // Verify AuditLog for sales.orders redacted PII address/receiver/internalNote
        await using var db = h.OwnerContext();
        var auditJson = string.Join("\n", await db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityType == "sales.orders" && a.EntityId == payOrder.Id)
            .Select(a => a.Changes)
            .ToArrayAsync());
        Assert.DoesNotContain("SecretNewAddress", auditJson, StringComparison.Ordinal);
        Assert.DoesNotContain("SecretNewReceiver", auditJson, StringComparison.Ordinal);
        Assert.DoesNotContain("SecretUpdatedNote", auditJson, StringComparison.Ordinal);
        Assert.Contains("[redacted]", auditJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelOrderEnforcesActiveShipmentRuleReleasesStockAndSetsNeedsRefundWhenPaid()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await SeedAdminLocationsAsync(h);
        await h.LoginAsync();

        var customerId = await CreateHttpCustomerAsync(h, "Khách Cancel", "0904777001");
        var variantId = await SeedVariantOnlyAsync(h, isUniquePiece: false, onHand: 10, priceVnd: 500_000L, avgCostVnd: 200_000L, suffix: "canc1");

        // 1. Confirmed bank_transfer order with payment, NO active shipment -> cancel succeeds, releases stock, needsRefund=true
        using var order1Resp = await PostIdempotentJsonAsync(h.Client, OrdersRoot, new
        {
            customerId,
            salesChannel = "phone",
            paymentMethod = "bank_transfer",
            lines = new[] { new { variantId, quantity = 2 } },
            shippingFeeVnd = 0L,
            shipping = DefaultShipping(),
            confirmNow = true
        }, Guid.CreateVersion7().ToString("D"));
        var order1 = (await order1Resp.Content.ReadFromJsonAsync<AdminOrderCreatedView>())!;

        using var pay1 = await PostIdempotentJsonAsync(h.Client, $"{OrdersRoot}/{order1.Id}/payments", new
        {
            method = "bank_transfer",
            amountVnd = 1_000_000L,
            receivedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            bankReference = "FT-CANCEL-01"
        }, Guid.CreateVersion7().ToString("D"));
        Assert.Equal(HttpStatusCode.OK, pay1.StatusCode);

        var fresh1 = (await h.Client.GetFromJsonAsync<AdminOrderDetailView>($"{OrdersRoot}/{order1.Id}"))!;
        using var cancel1 = await h.MutateAsync(HttpMethod.Post, $"{OrdersRoot}/{order1.Id}/cancel", new
        {
            rowVersion = fresh1.RowVersion,
            reason = "Khách báo hủy trước khi giao"
        });
        Assert.Equal(HttpStatusCode.OK, cancel1.StatusCode);
        var cancelled1 = (await cancel1.Content.ReadFromJsonAsync<AdminOrderDetailView>())!;
        Assert.Equal(OrderStatus.Cancelled, cancelled1.Status);
        Assert.True(cancelled1.NeedsRefund);
        Assert.False(cancelled1.IsOverpaid);
        Assert.Equal(0, await ScalarAsync<int>(h, $"SELECT reserved FROM inventory.stock_levels WHERE variant_id = '{variantId}'"));
        Assert.Equal(0L, await ScalarAsync<long>(h, "SELECT count(*) FROM inventory.stock_movements WHERE movement_type = 'sale'"));

        // 2. Confirmed order with an active shipment (purpose='order', status='created') -> 409 invalid_status_transition
        using var order2Resp = await PostIdempotentJsonAsync(h.Client, OrdersRoot, new
        {
            customerId,
            salesChannel = "phone",
            paymentMethod = "cod",
            lines = new[] { new { variantId, quantity = 1 } },
            shipping = DefaultShipping(),
            confirmNow = true,
            callConfirmed = true,
            callNote = "Đã xác nhận"
        }, Guid.CreateVersion7().ToString("D"));
        var order2 = (await order2Resp.Content.ReadFromJsonAsync<AdminOrderCreatedView>())!;

        await h.ExecuteAsync($"""
INSERT INTO sales.shipments(id,order_id,purpose,provider,carrier_name,status,cod_amount_vnd,created_at)
VALUES ('{Guid.CreateVersion7()}','{order2.Id}','order','manual','GHTK','created',530000,now());
""");

        using var cancelBlocked = await h.MutateAsync(HttpMethod.Post, $"{OrdersRoot}/{order2.Id}/cancel", new
        {
            rowVersion = order2.RowVersion,
            reason = "Thử hủy khi đang có vận đơn"
        });
        await AssertProblemAsync(cancelBlocked, HttpStatusCode.Conflict, "invalid_status_transition");
    }

    [Fact]
    public async Task ReorderAllowedFromAllThreeTerminalStatesUsesCurrentCatalogPriceAndStockWithZeroCopiedFinancials()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await SeedAdminLocationsAsync(h);
        await h.LoginAsync();

        var customerId = await CreateHttpCustomerAsync(h, "Khách Reorder", "0904888001");
        var variantId = await SeedVariantOnlyAsync(h, isUniquePiece: false, onHand: 10, priceVnd: 500_000L, avgCostVnd: 200_000L, suffix: "reord1");

        // 1. Source order with price override (400k), manual discount (40k), payment, and shipment -> then delivered
        using var srcResp = await PostIdempotentJsonAsync(h.Client, OrdersRoot, new
        {
            customerId,
            salesChannel = "zalo_personal",
            paymentMethod = "bank_transfer",
            lines = new[] { new { variantId, quantity = 1, unitPriceVnd = 400_000L, priceOverrideReason = "Giá ưu đãi cũ" } },
            discountVnd = 40_000L,
            discountNote = "Giảm cũ",
            shippingFeeVnd = 0L,
            shipping = DefaultShipping(),
            confirmNow = true
        }, Guid.CreateVersion7().ToString("D"));
        var srcOrder = (await srcResp.Content.ReadFromJsonAsync<AdminOrderCreatedView>())!;

        // Reorder while confirmed (non-terminal) -> 409 invalid_status_transition
        using var nonTerminalReorder = await PostRawJsonAsync(h.Client, $"{OrdersRoot}/{srcOrder.Id}/reorder", "{}");
        await AssertProblemAsync(nonTerminalReorder, HttpStatusCode.Conflict, "invalid_status_transition");

        // Pay, ship, deliver source order
        using (var p = await PostIdempotentJsonAsync(h.Client, $"{OrdersRoot}/{srcOrder.Id}/payments", new
        {
            method = "bank_transfer",
            amountVnd = 360_000L,
            receivedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            bankReference = "FT-REORD-SRC"
        }, Guid.CreateVersion7().ToString("D")))
            Assert.Equal(HttpStatusCode.OK, p.StatusCode);

        using var shipResp = await h.MutateAsync(HttpMethod.Post, $"{OrdersRoot}/{srcOrder.Id}/shipments", new { carrierName = "GHTK" });
        Assert.Equal(HttpStatusCode.Created, shipResp.StatusCode);
        var shipment = (await shipResp.Content.ReadFromJsonAsync<ShipmentDetailView>())!;

        // Reorder while shipping (non-terminal) -> 409 invalid_status_transition
        using var shippingReorder = await PostRawJsonAsync(h.Client, $"{OrdersRoot}/{srcOrder.Id}/reorder", "{}");
        await AssertProblemAsync(shippingReorder, HttpStatusCode.Conflict, "invalid_status_transition");

        using (var delivResp = await h.MutateAsync(HttpMethod.Post, $"/api/v1/admin/shipments/{shipment.Id}/events", new
        {
            status = "delivered",
            occurredAt = DateTimeOffset.UtcNow
        }))
            Assert.Equal(HttpStatusCode.OK, delivResp.StatusCode);

        // Change current catalog price from 500_000 to 650_000!
        await h.ExecuteAsync($"UPDATE catalog.variants SET price_vnd = 650000 WHERE id = '{variantId}'");

        // Reorder from 'delivered' with COMPLETELY EMPTY HTTP body -> 201 Created with Location, current catalog price 650k, 0 copied discount/payments/shipments!
        var csrf = await GetCsrfTokenAsync(h.Client);
        using var emptyReq = new HttpRequestMessage(HttpMethod.Post, $"{OrdersRoot}/{srcOrder.Id}/reorder");
        emptyReq.Headers.Add("X-CSRF-TOKEN", csrf);
        using var reorderDelivered = await h.Client.SendAsync(emptyReq);
        Assert.Equal(HttpStatusCode.Created, reorderDelivered.StatusCode);
        Assert.NotNull(reorderDelivered.Headers.Location);
        var reordered1 = (await reorderDelivered.Content.ReadFromJsonAsync<AdminOrderDetailView>())!;
        Assert.NotEqual(srcOrder.Id, reordered1.Id);
        Assert.NotEqual(srcOrder.Code, reordered1.Code);
        Assert.Equal(OrderOrigin.Admin, reordered1.Origin);
        Assert.Equal(OrderStatus.Pending, reordered1.Status);
        Assert.Equal(SalesChannel.ZaloPersonal, reordered1.SalesChannel);
        Assert.Equal(650_000L, reordered1.SubtotalVnd); // current catalog price, NOT old 400k override!
        Assert.Equal(0L, reordered1.DiscountVnd); // NO old discount copied!
        Assert.Null(reordered1.DiscountNote);
        Assert.Equal(30_000L, reordered1.ShippingFeeVnd); // fresh BR-SHP-01 fee!
        Assert.Equal(680_000L, reordered1.TotalVnd);
        Assert.Equal(OrderPriceSource.List, Assert.Single(reordered1.Lines).PriceSource);
        Assert.Null(Assert.Single(reordered1.Lines).PriceOverrideReason);
        Assert.Empty(reordered1.Payments);
        Assert.Empty(reordered1.Refunds);
        Assert.Empty(reordered1.Shipments);
        Assert.Equal(1, await ScalarAsync<int>(h, $"SELECT reserved FROM inventory.stock_levels WHERE variant_id = '{variantId}'"));

        // 2. Reorder from 'cancelled' web order -> maps salesChannel='web' to 'other' automatically
        var webOrder = await CreateWebBankOrderAsync(h, customerId, variantId, quantity: 1);
        var webDetail = (await h.Client.GetFromJsonAsync<AdminOrderDetailView>($"{OrdersRoot}/{webOrder.Id}"))!;
        using (var cancelWeb = await h.MutateAsync(HttpMethod.Post, $"{OrdersRoot}/{webOrder.Id}/cancel", new
        {
            rowVersion = webDetail.RowVersion,
            reason = "Hủy đơn web"
        }))
            Assert.Equal(HttpStatusCode.OK, cancelWeb.StatusCode);

        using var reorderCancelledWeb = await PostRawJsonAsync(h.Client, $"{OrdersRoot}/{webOrder.Id}/reorder", "{}");
        Assert.Equal(HttpStatusCode.Created, reorderCancelledWeb.StatusCode);
        var reordered2 = (await reorderCancelledWeb.Content.ReadFromJsonAsync<AdminOrderDetailView>())!;
        Assert.Equal(OrderOrigin.Admin, reordered2.Origin);
        Assert.Equal(SalesChannel.Other, reordered2.SalesChannel);

        // 3. Reorder from 'delivery_failed' with explicit channel/payment override -> succeeds
        using var failSrcResp = await PostIdempotentJsonAsync(h.Client, OrdersRoot, new
        {
            customerId,
            salesChannel = "phone",
            paymentMethod = "cod",
            lines = new[] { new { variantId, quantity = 1 } },
            shipping = DefaultShipping(),
            confirmNow = true,
            callConfirmed = true,
            callNote = "Xác nhận"
        }, Guid.CreateVersion7().ToString("D"));
        var failSrc = (await failSrcResp.Content.ReadFromJsonAsync<AdminOrderCreatedView>())!;
        var failShip = (await (await h.MutateAsync(HttpMethod.Post, $"{OrdersRoot}/{failSrc.Id}/shipments", new { carrierName = "GHTK" }))
            .Content.ReadFromJsonAsync<ShipmentDetailView>())!;
        using (var failEvt = await h.MutateAsync(HttpMethod.Post, $"/api/v1/admin/shipments/{failShip.Id}/events", new
        {
            status = "failed",
            occurredAt = DateTimeOffset.UtcNow,
            note = "Khách không nghe máy"
        }))
            Assert.Equal(HttpStatusCode.OK, failEvt.StatusCode);

        using var reorderFailed = await h.MutateAsync(HttpMethod.Post, $"{OrdersRoot}/{failSrc.Id}/reorder", new
        {
            salesChannel = "facebook",
            paymentMethod = "bank_transfer",
            internalNote = "Tạo lại sau giao thất bại"
        });
        Assert.Equal(HttpStatusCode.Created, reorderFailed.StatusCode);
        var reordered3 = (await reorderFailed.Content.ReadFromJsonAsync<AdminOrderDetailView>())!;
        Assert.Equal(SalesChannel.Facebook, reordered3.SalesChannel);
        Assert.Equal(OrderPaymentMethod.BankTransfer, reordered3.PaymentMethod);
        Assert.Equal("Tạo lại sau giao thất bại", reordered3.InternalNote);

        // 4. Out-of-stock variant on reorder -> 409 out_of_stock
        await h.ExecuteAsync($"UPDATE inventory.stock_levels SET on_hand = reserved WHERE variant_id = '{variantId}'");
        using var oosReorder = await PostRawJsonAsync(h.Client, $"{OrdersRoot}/{failSrc.Id}/reorder", "{}");
        await AssertProblemAsync(oosReorder, HttpStatusCode.Conflict, "out_of_stock");
    }

    [Fact]
    public async Task ListAndGetAdminOrdersSearchFlagsO1O2O3ParityAndReadsAllSixRealChildTables()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await SeedAdminLocationsAsync(h);
        await h.LoginAsync();

        var customerId = await CreateHttpCustomerAsync(h, "Nguyễn Văn Tráng", "0904999123");
        var variantId = await SeedVariantOnlyAsync(h, isUniquePiece: false, onHand: 50, priceVnd: 500_000L, avgCostVnd: 200_000L, suffix: "flg1");

        // Order 1: Active OVERPAID order (total 500_000, paid 700_000 -> overpaid 200_000) -> overpaid=true, needsRefund=true (O2=A)
        var orderOverpaid = await CreateAdminOrderViaHttpAsync(h, customerId, variantId, 1, "bank_transfer", shippingFeeVnd: 0L);
        await RecordPaymentViaHttpAsync(h, orderOverpaid.Id, 700_000L, "FT-FLG-OVER");

        // Order 2: Cancelled order with netPaid > 0 -> overpaid=false, needsRefund=true
        var orderCancelledPaid = await CreateAdminOrderViaHttpAsync(h, customerId, variantId, 1, "bank_transfer", shippingFeeVnd: 0L);
        await RecordPaymentViaHttpAsync(h, orderCancelledPaid.Id, 500_000L, "FT-FLG-CANC");
        var cancelDetailBefore = (await h.Client.GetFromJsonAsync<AdminOrderDetailView>($"{OrdersRoot}/{orderCancelledPaid.Id}"))!;
        using (var cr = await h.MutateAsync(HttpMethod.Post, $"{OrdersRoot}/{orderCancelledPaid.Id}/cancel", new
        {
            rowVersion = cancelDetailBefore.RowVersion,
            reason = "Hủy sau khi trả tiền"
        }))
            Assert.Equal(HttpStatusCode.OK, cr.StatusCode);

        // Order 3: DeliveryFailed order with netPaid > 0 (O1=A) -> overpaid=false, needsRefund=true
        var orderFailedPaid = await CreateAdminOrderViaHttpAsync(h, customerId, variantId, 1, "bank_transfer", shippingFeeVnd: 0L, confirmNow: true);
        await RecordPaymentViaHttpAsync(h, orderFailedPaid.Id, 500_000L, "FT-FLG-FAIL");
        var failShip = (await (await h.MutateAsync(HttpMethod.Post, $"{OrdersRoot}/{orderFailedPaid.Id}/shipments", new { carrierName = "GHTK" }))
            .Content.ReadFromJsonAsync<ShipmentDetailView>())!;
        using (var fe = await h.MutateAsync(HttpMethod.Post, $"/api/v1/admin/shipments/{failShip.Id}/events", new
        {
            status = "failed",
            occurredAt = DateTimeOffset.UtcNow,
            note = "Giao thất bại"
        }))
            Assert.Equal(HttpStatusCode.OK, fe.StatusCode);

        // Order 4: Delivered order with approved refund return_request (O2=A) + real payments, refunds, shipments+events, returns+lines, history
        var orderWithReturn = await CreateAdminOrderViaHttpAsync(h, customerId, variantId, 2, "bank_transfer", shippingFeeVnd: 0L, confirmNow: true);
        await RecordPaymentViaHttpAsync(h, orderWithReturn.Id, 1_100_000L, "FT-FLG-RET");
        // Refund 100k overpayment -> effectivePaid = 1_000_000 == total -> NOT overpaid anymore!
        using (var refOver = await PostIdempotentJsonAsync(h.Client, $"{OrdersRoot}/{orderWithReturn.Id}/refunds", new
        {
            kind = "overpayment",
            amountVnd = 100_000L,
            method = "bank_transfer",
            reason = "Hoàn phần thu thừa",
            refundedAt = DateTimeOffset.UtcNow
        }, Guid.CreateVersion7().ToString("D")))
            Assert.Equal(HttpStatusCode.OK, refOver.StatusCode);

        var retShip = (await (await h.MutateAsync(HttpMethod.Post, $"{OrdersRoot}/{orderWithReturn.Id}/shipments", new { carrierName = "GHN", trackingCode = "GHN-TRK-01" }))
            .Content.ReadFromJsonAsync<ShipmentDetailView>())!;
        using (var delEv = await h.MutateAsync(HttpMethod.Post, $"/api/v1/admin/shipments/{retShip.Id}/events", new
        {
            status = "delivered",
            occurredAt = DateTimeOffset.UtcNow
        }))
            Assert.Equal(HttpStatusCode.OK, delEv.StatusCode);

        var orderWithReturnLineId = await ScalarAsync<Guid>(h, $"SELECT id FROM sales.order_lines WHERE order_id = '{orderWithReturn.Id}' LIMIT 1");
        var returnRequestId = Guid.CreateVersion7();
        await h.ExecuteAsync($"""
INSERT INTO sales.return_requests(id,code,order_id,reason,description,status,resolution,requested_at,decided_at)
VALUES ('{returnRequestId}','DT-261127-001','{orderWithReturn.Id}','defective','Nứt nắp ấm','approved','refund',now(),now());
INSERT INTO sales.return_lines(id,return_request_id,order_line_id,quantity,restockable)
VALUES ('{Guid.CreateVersion7()}','{returnRequestId}','{orderWithReturnLineId}',1,false);
""");

        // Order 5: Expiring web bank_transfer order (payment_due_at = now + 12h -> IN window)
        var orderExpiring = await CreateWebBankOrderAsync(h, customerId, variantId, quantity: 1);
        await h.ExecuteAsync($"UPDATE sales.orders SET payment_due_at = now() + interval '12 hours' WHERE id = '{orderExpiring.Id}'");

        // Order 6: Non-expiring web bank_transfer orders (due in 36h, already expired -1h, partially_paid in 12h, AND 0-VND in 12h) -> MUST NOT match flag=expiring
        var orderFutureDue = await CreateWebBankOrderAsync(h, customerId, variantId, quantity: 1);
        await h.ExecuteAsync($"UPDATE sales.orders SET payment_due_at = now() + interval '36 hours' WHERE id = '{orderFutureDue.Id}'");
        var orderPastDue = await CreateWebBankOrderAsync(h, customerId, variantId, quantity: 1);
        await h.ExecuteAsync($"UPDATE sales.orders SET payment_due_at = now() - interval '1 hour' WHERE id = '{orderPastDue.Id}'");

        // P1-F2: Pending web bank_transfer order that is partially_paid with due in 12h MUST NOT be expiring (matches BR-ORD-05, ix_orders_due, EfDailyDigestRead)
        var orderPartiallyPaidDueSoon = await CreateWebBankOrderAsync(h, customerId, variantId, quantity: 1);
        await h.ExecuteAsync($"UPDATE sales.orders SET payment_due_at = now() + interval '12 hours' WHERE id = '{orderPartiallyPaidDueSoon.Id}'");
        await RecordPaymentViaHttpAsync(h, orderPartiallyPaidDueSoon.Id, 100_000L, "FT-FLG-PARTIAL");

        // P1-F2: 0-VND pending bank_transfer order MUST NOT be expiring even if payment_due_at is within 24h
        using var zeroResp = await PostIdempotentJsonAsync(h.Client, OrdersRoot, new
        {
            customerId,
            salesChannel = "phone",
            paymentMethod = "bank_transfer",
            lines = new[] { new { variantId, quantity = 1 } },
            discountVnd = 500_000L,
            discountNote = "Tặng mẫu thử 100%",
            shippingFeeVnd = 0L,
            shipping = DefaultShipping()
        }, Guid.CreateVersion7().ToString("D"));
        Assert.Equal(HttpStatusCode.Created, zeroResp.StatusCode);
        var orderZeroVnd = (await zeroResp.Content.ReadFromJsonAsync<AdminOrderCreatedView>(WebJson))!;
        Assert.Equal(0L, orderZeroVnd.TotalVnd);
        await h.ExecuteAsync($"UPDATE sales.orders SET payment_due_at = now() + interval '12 hours' WHERE id = '{orderZeroVnd.Id}'");

        // Order 7: Backfilled order -> MUST NEVER match any flag
        using (var bf = await PostIdempotentJsonAsync(h.Client, OrdersRoot, new
        {
            customerId,
            salesChannel = "in_person",
            paymentMethod = "cod",
            lines = new[] { new { variantId, quantity = 1 } },
            shippingFeeVnd = 0L,
            shipping = DefaultShipping(),
            isBackfilled = true,
            deliveredAt = DateTimeOffset.UtcNow.AddDays(-3)
        }, Guid.CreateVersion7().ToString("D")))
            Assert.Equal(HttpStatusCode.Created, bf.StatusCode);

        // Verify GET /api/v1/admin/orders/{id} reads ALL 6 real child tables on orderWithReturn!
        var fullDetail = (await h.Client.GetFromJsonAsync<AdminOrderDetailView>($"{OrdersRoot}/{orderWithReturn.Id}"))!;
        Assert.Single(fullDetail.Lines);
        Assert.Single(fullDetail.Payments);
        Assert.Single(fullDetail.Refunds);
        Assert.Equal(RefundKind.Overpayment, fullDetail.Refunds[0].Kind);
        Assert.Single(fullDetail.Shipments);
        Assert.True(fullDetail.Shipments[0].Events.Count >= 2);
        Assert.Single(fullDetail.Returns);
        Assert.Single(fullDetail.Returns[0].Lines);
        Assert.True(fullDetail.History.Count >= 3);
        Assert.False(fullDetail.IsOverpaid);
        Assert.True(fullDetail.NeedsRefund); // due to approved refund return!

        // Verify flag=overpaid -> ONLY orderOverpaid
        var overpaidPage = (await h.Client.GetFromJsonAsync<CatalogPage<AdminOrderListItemView>>($"{OrdersRoot}?flag=overpaid"))!;
        Assert.Equal([orderOverpaid.Id], overpaidPage.Items.Select(x => x.Id).ToArray());

        // Verify flag=needs_refund -> orderOverpaid + orderCancelledPaid + orderFailedPaid + orderWithReturn (exact 4!)
        var needsRefundPage = (await h.Client.GetFromJsonAsync<CatalogPage<AdminOrderListItemView>>($"{OrdersRoot}?flag=needs_refund"))!;
        var needsRefundIds = needsRefundPage.Items.Select(x => x.Id).ToHashSet();
        Assert.Equal(4, needsRefundIds.Count);
        Assert.Contains(orderOverpaid.Id, needsRefundIds);
        Assert.Contains(orderCancelledPaid.Id, needsRefundIds);
        Assert.Contains(orderFailedPaid.Id, needsRefundIds);
        Assert.Contains(orderWithReturn.Id, needsRefundIds);

        // Refund the 200k overpayment on orderOverpaid -> disappears from BOTH overpaid and needs_refund!
        using (var clearOver = await PostIdempotentJsonAsync(h.Client, $"{OrdersRoot}/{orderOverpaid.Id}/refunds", new
        {
            kind = "overpayment",
            amountVnd = 200_000L,
            method = "bank_transfer",
            reason = "Hoàn đủ tiền thừa",
            refundedAt = DateTimeOffset.UtcNow
        }, Guid.CreateVersion7().ToString("D")))
            Assert.Equal(HttpStatusCode.OK, clearOver.StatusCode);

        var overpaidAfter = (await h.Client.GetFromJsonAsync<CatalogPage<AdminOrderListItemView>>($"{OrdersRoot}?flag=overpaid"))!;
        Assert.Empty(overpaidAfter.Items);
        var needsRefundAfter = (await h.Client.GetFromJsonAsync<CatalogPage<AdminOrderListItemView>>($"{OrdersRoot}?flag=needs_refund"))!;
        Assert.DoesNotContain(needsRefundAfter.Items, x => x.Id == orderOverpaid.Id);

        // Verify flag=expiring -> ONLY orderExpiring (unpaid, total_vnd > 0, now < due <= now + 24h); partially_paid & 0-VND excluded on both list & detail
        var expiringPage = (await h.Client.GetFromJsonAsync<CatalogPage<AdminOrderListItemView>>($"{OrdersRoot}?flag=expiring"))!;
        Assert.Equal([orderExpiring.Id], expiringPage.Items.Select(x => x.Id).ToArray());

        var expiringDetail = (await h.Client.GetFromJsonAsync<AdminOrderDetailView>($"{OrdersRoot}/{orderExpiring.Id}"))!;
        Assert.True(expiringDetail.IsExpiring);

        var partialDetail = (await h.Client.GetFromJsonAsync<AdminOrderDetailView>($"{OrdersRoot}/{orderPartiallyPaidDueSoon.Id}"))!;
        Assert.Equal(OrderPaymentStatus.PartiallyPaid, partialDetail.PaymentStatus);
        Assert.False(partialDetail.IsExpiring);

        var zeroDetail = (await h.Client.GetFromJsonAsync<AdminOrderDetailView>($"{OrdersRoot}/{orderZeroVnd.Id}"))!;
        Assert.Equal(0L, zeroDetail.TotalVnd);
        Assert.False(zeroDetail.IsExpiring);

        // Verify search q: order code, phone tail, unaccented Vietnamese name, and literal % wildcard
        var byCode = (await h.Client.GetFromJsonAsync<CatalogPage<AdminOrderListItemView>>($"{OrdersRoot}?q={orderOverpaid.Code}"))!;
        Assert.Single(byCode.Items);
        Assert.Equal(orderOverpaid.Id, byCode.Items[0].Id);

        var byPhoneTail = (await h.Client.GetFromJsonAsync<CatalogPage<AdminOrderListItemView>>($"{OrdersRoot}?q=999123"))!;
        Assert.True(byPhoneTail.TotalItems >= 5);

        var byUnaccentedName = (await h.Client.GetFromJsonAsync<CatalogPage<AdminOrderListItemView>>($"{OrdersRoot}?q=Nguyen%20Van%20Trang"))!;
        Assert.True(byUnaccentedName.TotalItems >= 5);

        var byPercentLiteral = (await h.Client.GetFromJsonAsync<CatalogPage<AdminOrderListItemView>>($"{OrdersRoot}?q=%25"))!;
        Assert.Equal(0L, byPercentLiteral.TotalItems);

        // Verify from/to Vietnam date filter
        var todayVn = TeaPremium.Application.Time.BusinessCalendar.ToVietnamDate(DateTimeOffset.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var byDate = (await h.Client.GetFromJsonAsync<CatalogPage<AdminOrderListItemView>>($"{OrdersRoot}?from={todayVn}&to={todayVn}"))!;
        Assert.True(byDate.TotalItems >= 6);
        Assert.DoesNotContain(byDate.Items, x => x.IsBackfilled); // backfill placedAt was 3 days ago!
    }

    [Fact]
    public async Task GetPackingSlipReturnsNoStoreCspHtmlEscapesXssAndSupportsHidePrices()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await SeedAdminLocationsAsync(h);
        await h.LoginAsync();

        var customerId = await CreateHttpCustomerAsync(h, "Khách Packing Slip", "0904999555");
        var variantId = await SeedVariantOnlyAsync(h, isUniquePiece: false, onHand: 10, priceVnd: 512_000L, avgCostVnd: 222_333L, suffix: "slip1");

        const string xss = "<script>alert('xss')</script>";
        using var orderResp = await PostIdempotentJsonAsync(h.Client, OrdersRoot, new
        {
            customerId,
            salesChannel = "phone",
            paymentMethod = "cod",
            lines = new[] { new { variantId, quantity = 1 } },
            discountVnd = 12_000L,
            discountNote = "SecretDiscountNoteForSlip",
            shippingFeeVnd = 30_000L,
            shipping = new
            {
                receiverName = $"Nguyễn {xss}",
                receiverPhone = "0904999555",
                provinceCode = "79",
                wardCode = "79001",
                addressLine = $"10 Đường {xss}",
                customerNote = $"Ghi chú {xss}"
            },
            internalNote = "SecretInternalNoteForSlip"
        }, Guid.CreateVersion7().ToString("D"));
        Assert.Equal(HttpStatusCode.Created, orderResp.StatusCode);
        var order = (await orderResp.Content.ReadFromJsonAsync<AdminOrderCreatedView>())!;

        // 1. Default packing-slip (hidePrices=false)
        using var slipResp = await h.Client.GetAsync($"{OrdersRoot}/{order.Id}/packing-slip");
        Assert.Equal(HttpStatusCode.OK, slipResp.StatusCode);
        Assert.Equal("text/html", slipResp.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", slipResp.Content.Headers.ContentType?.CharSet);
        Assert.Equal("no-store", slipResp.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", Assert.Single(slipResp.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("SAMEORIGIN", Assert.Single(slipResp.Headers.GetValues("X-Frame-Options")));
        Assert.Equal(
            "default-src 'none'; img-src 'self' data:; style-src 'unsafe-inline'; frame-ancestors 'self'; base-uri 'none'; form-action 'none'",
            Assert.Single(slipResp.Headers.GetValues("Content-Security-Policy")));

        var html = await slipResp.Content.ReadAsStringAsync();
        Assert.Contains("<svg xmlns=\"http://www.w3.org/2000/svg\"", html, StringComparison.Ordinal);
        Assert.Contains(order.Code, html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(&#39;xss&#39;)&lt;/script&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SecretInternalNoteForSlip", html, StringComparison.Ordinal);
        Assert.DoesNotContain("SecretDiscountNoteForSlip", html, StringComparison.Ordinal);
        Assert.DoesNotContain("222333", html, StringComparison.Ordinal);
        Assert.Contains("512.000 ₫", html, StringComparison.Ordinal);
        Assert.Contains("530.000 ₫", html, StringComparison.Ordinal);

        // 2. hidePrices=true hides line/summary prices while keeping COD amount
        using var hiddenResp = await h.Client.GetAsync($"{OrdersRoot}/{order.Id}/packing-slip?hidePrices=true");
        Assert.Equal(HttpStatusCode.OK, hiddenResp.StatusCode);
        var hiddenHtml = await hiddenResp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("512.000", hiddenHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Đơn giá", hiddenHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Tạm tính", hiddenHtml, StringComparison.Ordinal);
        Assert.Contains("Tiền thu hộ (COD)", hiddenHtml, StringComparison.Ordinal);
        Assert.Contains("530.000 ₫", hiddenHtml, StringComparison.Ordinal);

        // 3. Invalid hidePrices -> 400 validation_failed; missing order -> 404 not_found
        using var badQuery = await h.Client.GetAsync($"{OrdersRoot}/{order.Id}/packing-slip?hidePrices=notabool");
        await AssertProblemAsync(badQuery, HttpStatusCode.BadRequest, "validation_failed");

        using var notFound = await h.Client.GetAsync($"{OrdersRoot}/{Guid.CreateVersion7()}/packing-slip");
        await AssertProblemAsync(notFound, HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task PostAdminOrdersNullRequiredFieldsAndNullLineElementsReturn400Never500()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await SeedAdminLocationsAsync(h);
        await h.LoginAsync();

        var customerId = await CreateHttpCustomerAsync(h, "Khách Null Guard", "0904111222");
        var variantId = await SeedVariantOnlyAsync(h, isUniquePiece: false, onHand: 10, priceVnd: 500_000L, avgCostVnd: 200_000L, suffix: "null1");

        var payloads = new[]
        {
            $$$"""{"customerId":"{{{customerId}}}","salesChannel":"phone","paymentMethod":"cod","lines":null,"shipping":{"receiverName":"A","receiverPhone":"0909123456","provinceCode":"79","wardCode":"79001","addressLine":"10 Lê Lợi"}}""",
            $$$"""{"customerId":"{{{customerId}}}","salesChannel":"phone","paymentMethod":"cod","lines":[null],"shipping":{"receiverName":"A","receiverPhone":"0909123456","provinceCode":"79","wardCode":"79001","addressLine":"10 Lê Lợi"}}""",
            $$$"""{"customerId":"{{{customerId}}}","salesChannel":"phone","paymentMethod":"cod","lines":[{"variantId":"{{{variantId}}}","quantity":1}],"shipping":null}""",
            $$$"""{"customerId":"{{{customerId}}}","salesChannel":null,"paymentMethod":"cod","lines":[{"variantId":"{{{variantId}}}","quantity":1}],"shipping":{"receiverName":"A","receiverPhone":"0909123456","provinceCode":"79","wardCode":"79001","addressLine":"10 Lê Lợi"}}""",
            $$$"""{"customerId":"{{{customerId}}}","salesChannel":"phone","paymentMethod":"cod","lines":[{"variantId":"{{{variantId}}}","quantity":1}],"shipping":{"receiverName":null,"receiverPhone":"0909123456","provinceCode":"79","wardCode":"79001","addressLine":"10 Lê Lợi"}}""",
            $$$"""{"newCustomer":{"fullName":null,"phone":"0904111333"},"salesChannel":"phone","paymentMethod":"cod","lines":[{"variantId":"{{{variantId}}}","quantity":1}],"shipping":{"receiverName":"A","receiverPhone":"0909123456","provinceCode":"79","wardCode":"79001","addressLine":"10 Lê Lợi"}}"""
        };

        foreach (var raw in payloads)
        {
            using var resp = await PostRawIdempotentJsonAsync(h.Client, OrdersRoot, raw, Guid.CreateVersion7().ToString("D"));
            await AssertProblemAsync(resp, HttpStatusCode.BadRequest, "validation_failed");
        }

        Assert.Equal(0L, await ScalarAsync<long>(h, "SELECT count(*) FROM sales.orders"));
    }

    [Fact]
    public async Task NormalCreateOrderAuditLogRecordsAllSixOnAddColumnsAndRedactsAllPii()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await SeedAdminLocationsAsync(h);
        await h.LoginAsync();

        var customerId = await CreateHttpCustomerAsync(h, "Khách Audit Create Secret", "0904222888");
        var variantId = await SeedVariantOnlyAsync(h, isUniquePiece: false, onHand: 10, priceVnd: 500_000L, avgCostVnd: 200_000L, suffix: "aud1");

        using var resp = await PostIdempotentJsonAsync(h.Client, OrdersRoot, new
        {
            customerId,
            salesChannel = "zalo_personal",
            paymentMethod = "cod",
            lines = new[] { new { variantId, quantity = 1 } },
            discountVnd = 40_000L,
            discountNote = "SecretDiscountAudit",
            shippingFeeVnd = 25_000L,
            shipping = new
            {
                receiverName = "SecretReceiverAudit",
                receiverPhone = "0904999777",
                provinceCode = "79",
                wardCode = "79001",
                addressLine = "77 Secret Audit Street",
                customerNote = "SecretCustomerNoteAudit"
            },
            internalNote = "SecretInternalNoteAudit"
        }, Guid.CreateVersion7().ToString("D"));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var created = (await resp.Content.ReadFromJsonAsync<AdminOrderCreatedView>(WebJson))!;

        await using var db = h.OwnerContext();
        var createAudit = await db.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.EntityType == "sales.orders" && a.EntityId == created.Id && a.Action == "create");

        using var doc = JsonDocument.Parse(createAudit.Changes);
        var root = doc.RootElement;
        Assert.Equal(40_000L, root.GetProperty("discount_vnd").GetProperty("after").GetInt64());
        Assert.Equal(25_000L, root.GetProperty("shipping_fee_vnd").GetProperty("after").GetInt64());
        Assert.Equal(OrderPaymentStatus.Unpaid, root.GetProperty("payment_status").GetProperty("after").GetString());
        Assert.False(root.GetProperty("is_backfilled").GetProperty("after").GetBoolean());
        Assert.Equal(JsonValueKind.String, root.GetProperty("created_at").GetProperty("after").ValueKind);
        Assert.Equal(JsonValueKind.String, root.GetProperty("updated_at").GetProperty("after").ValueKind);

        foreach (var redactedCol in new[] { "contact_name", "contact_phone", "receiver_name", "receiver_phone", "address_line", "customer_note", "internal_note", "discount_note" })
            Assert.Equal("[redacted]", root.GetProperty(redactedCol).GetProperty("after").GetString());

        foreach (var secret in new[] { "Secret", "0904222888", "0904999777", "904222888", "904999777" })
            Assert.DoesNotContain(secret, createAudit.Changes, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConcurrentParallelPatchWithSameRowVersionYieldsOne200AndOne409ConcurrencyConflict()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await SeedAdminLocationsAsync(h);
        await h.LoginAsync();
        var salesEmail = await CreateStaffAsync(h, "sales");

        using var salesClient = h.Factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await LoginClientAsync(salesClient, salesEmail, StaffNewPassword);

        var customerId = await CreateHttpCustomerAsync(h, "Khách Parallel Patch", "0904333999");
        var variantId = await SeedVariantOnlyAsync(h, isUniquePiece: false, onHand: 20, priceVnd: 500_000L, avgCostVnd: 200_000L, suffix: "ppat");

        var order = await CreateAdminOrderViaHttpAsync(h, customerId, variantId, 1, "bank_transfer", shippingFeeVnd: 0L);
        var currentVersion = order.RowVersion;

        for (var round = 0; round < 3; round++)
        {
            var csrfOwner = await GetCsrfTokenAsync(h.Client);
            var csrfSales = await GetCsrfTokenAsync(salesClient);
            var payloadOwner = $$"""{"rowVersion":"{{currentVersion}}","internalNote":"Owner parallel round {{round}}"}""";
            var payloadSales = $$"""{"rowVersion":"{{currentVersion}}","internalNote":"Sales parallel round {{round}}"}""";

            var taskOwner = SendPatchWithCsrfAsync(h.Client, $"{OrdersRoot}/{order.Id}", payloadOwner, csrfOwner);
            var taskSales = SendPatchWithCsrfAsync(salesClient, $"{OrdersRoot}/{order.Id}", payloadSales, csrfSales);
            var responses = await Task.WhenAll(taskOwner, taskSales);

            try
            {
                Assert.DoesNotContain(responses, r => (int)r.StatusCode >= 500);
                var okResp = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
                var conflictResp = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);

                await AssertProblemAsync(conflictResp, HttpStatusCode.Conflict, "concurrency_conflict");
                var updated = (await okResp.Content.ReadFromJsonAsync<AdminOrderDetailView>(WebJson))!;
                Assert.NotEqual(currentVersion, updated.RowVersion);
                currentVersion = updated.RowVersion;
            }
            finally
            {
                foreach (var r in responses) r.Dispose();
            }
        }
    }

    [Fact]
    public async Task ConcurrentParallelQuickCustomerCreateWithSamePhoneYieldsOne201And409CustomerPhoneExists()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await SeedAdminLocationsAsync(h);
        await h.LoginAsync();
        var salesEmail = await CreateStaffAsync(h, "sales");

        using var salesClient = h.Factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await LoginClientAsync(salesClient, salesEmail, StaffNewPassword);

        var variantId = await SeedVariantOnlyAsync(h, isUniquePiece: false, onHand: 50, priceVnd: 500_000L, avgCostVnd: 200_000L, suffix: "pqc");

        for (var round = 0; round < 3; round++)
        {
            var localPhone = $"090477710{round}";
            var e164Phone = $"+8490477710{round}";
            var rawBody = $$"""
            {
              "newCustomer": { "fullName": "Khách Đua Song Song {{round}}", "phone": "{{localPhone}}" },
              "salesChannel": "zalo_personal",
              "paymentMethod": "cod",
              "lines": [{ "variantId": "{{variantId}}", "quantity": 1 }],
              "shipping": {
                "receiverName": "Người Nhận Đua",
                "receiverPhone": "0909123456",
                "provinceCode": "79",
                "wardCode": "79001",
                "addressLine": "10 Lê Lợi"
              }
            }
            """;

            var csrfOwner = await GetCsrfTokenAsync(h.Client);
            var csrfSales = await GetCsrfTokenAsync(salesClient);

            var tasks = new[]
            {
                SendIdempotentPostWithCsrfAsync(h.Client, OrdersRoot, rawBody, csrfOwner, Guid.CreateVersion7().ToString("D")),
                SendIdempotentPostWithCsrfAsync(salesClient, OrdersRoot, rawBody, csrfSales, Guid.CreateVersion7().ToString("D")),
                SendIdempotentPostWithCsrfAsync(h.Client, OrdersRoot, rawBody, csrfOwner, Guid.CreateVersion7().ToString("D")),
                SendIdempotentPostWithCsrfAsync(salesClient, OrdersRoot, rawBody, csrfSales, Guid.CreateVersion7().ToString("D"))
            };

            var responses = await Task.WhenAll(tasks);
            try
            {
                Assert.DoesNotContain(responses, r => (int)r.StatusCode >= 500);
                var createdResp = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
                var conflictResps = responses.Where(r => r.StatusCode == HttpStatusCode.Conflict).ToArray();
                Assert.Equal(3, conflictResps.Length);

                var created = (await createdResp.Content.ReadFromJsonAsync<AdminOrderCreatedView>(WebJson))!;
                foreach (var conflictResp in conflictResps)
                {
                    var conflictBody = await conflictResp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(conflictBody);
                    Assert.Equal("customer_phone_exists", doc.RootElement.GetProperty("code").GetString());
                    Assert.Equal(created.CustomerId, doc.RootElement.GetProperty("data").GetProperty("existingCustomerId").GetGuid());
                }

                Assert.Equal(1L, await ScalarAsync<long>(h, $"SELECT count(*) FROM crm.customers WHERE phone_e164 = '{e164Phone}'"));
                Assert.Equal(1L, await ScalarAsync<long>(h, $"SELECT count(*) FROM sales.orders WHERE customer_id = '{created.CustomerId}'"));
            }
            finally
            {
                foreach (var r in responses) r.Dispose();
            }
        }
    }

    private static async Task LoginClientAsync(HttpClient client, string email, string password)
    {
        var token = await GetCsrfTokenAsync(client);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/auth/login")
        {
            Content = JsonContent.Create(new { email, password })
        };
        req.Headers.Add("X-CSRF-TOKEN", token);
        using var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    private static async Task<HttpResponseMessage> PostRawIdempotentJsonAsync(
        HttpClient client, string path, string rawJson, string idempotencyKey)
    {
        var token = await GetCsrfTokenAsync(client);
        return await SendIdempotentPostWithCsrfAsync(client, path, rawJson, token, idempotencyKey);
    }

    private static Task<HttpResponseMessage> SendIdempotentPostWithCsrfAsync(
        HttpClient client, string path, string rawJson, string csrfToken, string idempotencyKey)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(rawJson, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("X-CSRF-TOKEN", csrfToken);
        req.Headers.Add("Idempotency-Key", idempotencyKey);
        req.Headers.Add("X-Correlation-ID", "3cfd2f4d-8d0b-4fc5-b39e-7c81701a221d");
        return SendAndDisposeRequestAsync(client, req);
    }

    private static Task<HttpResponseMessage> SendPatchWithCsrfAsync(
        HttpClient client, string path, string rawJson, string csrfToken)
    {
        var req = new HttpRequestMessage(HttpMethod.Patch, path)
        {
            Content = new StringContent(rawJson, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("X-CSRF-TOKEN", csrfToken);
        req.Headers.Add("X-Correlation-ID", "3cfd2f4d-8d0b-4fc5-b39e-7c81701a221d");
        return SendAndDisposeRequestAsync(client, req);
    }

    private static async Task<HttpResponseMessage> SendAndDisposeRequestAsync(HttpClient client, HttpRequestMessage req)
    {
        using (req)
            return await client.SendAsync(req);
    }

    private static async Task<AdminOrderCreatedView> CreateAdminOrderViaHttpAsync(
        P009Harness h,
        Guid customerId,
        Guid variantId,
        int quantity,
        string paymentMethod,
        long? shippingFeeVnd = null,
        bool confirmNow = false)
    {
        using var resp = await PostIdempotentJsonAsync(h.Client, OrdersRoot, new
        {
            customerId,
            salesChannel = "phone",
            paymentMethod,
            lines = new[] { new { variantId, quantity } },
            shippingFeeVnd,
            shipping = DefaultShipping(),
            confirmNow
        }, Guid.CreateVersion7().ToString("D"));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<AdminOrderCreatedView>(WebJson))!;
    }

    private static async Task RecordPaymentViaHttpAsync(P009Harness h, Guid orderId, long amountVnd, string bankRef)
    {
        using var resp = await PostIdempotentJsonAsync(h.Client, $"{OrdersRoot}/{orderId}/payments", new
        {
            method = "bank_transfer",
            amountVnd,
            receivedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            bankReference = bankRef
        }, Guid.CreateVersion7().ToString("D"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    private static async Task<HttpResponseMessage> PatchRawJsonAsync(HttpClient client, string path, string rawJson)
    {
        var token = await GetCsrfTokenAsync(client);
        using var req = new HttpRequestMessage(HttpMethod.Patch, path)
        {
            Content = new StringContent(rawJson, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("X-CSRF-TOKEN", token);
        req.Headers.Add("X-Correlation-ID", "3cfd2f4d-8d0b-4fc5-b39e-7c81701a221d");
        return await client.SendAsync(req);
    }
}
