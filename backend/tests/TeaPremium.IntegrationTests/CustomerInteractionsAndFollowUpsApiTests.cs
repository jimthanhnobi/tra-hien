using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TeaPremium.Api.Iam;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Crm;
using TeaPremium.Application.Time;
using TeaPremium.Application.UseCases;
using TeaPremium.Infrastructure.Persistence;
using PersistenceOrder = TeaPremium.Infrastructure.Persistence.Order;
using static TeaPremium.IntegrationTests.CustomersApiTests;

namespace TeaPremium.IntegrationTests;

public sealed class CustomerInteractionsAndFollowUpsApiTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    private const string FollowUpsRoot = "/api/v1/admin/follow-ups";
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task BrCrm06CreateAndListCustomerInteractionsSupportsBackdatingPaginationAndStaffResolution()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero));
        await using var h = await P009Harness.CreateAsync(fixture, services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(clock);
        });
        await h.LoginAsync();
        var salesEmail = await CreateStaffAsync(h, "sales");
        await h.LoginAsync(salesEmail, StaffNewPassword);

        var customer = await CreateCustomerAsync(h, new { fullName = "Khách Tương Tác", phone = "0905111001" });

        // 1. Create interaction omitting occurredAt -> defaults to clock.UtcNow, with 4000 emoji Unicode content
        var emoji4000 = string.Concat(Enumerable.Repeat("🍵", 4000));
        using var createNow = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{customer.Id}/interactions", new
        {
            channel = "zalo_personal",
            kind = "consultation",
            content = emoji4000
        });
        Assert.Equal(HttpStatusCode.Created, createNow.StatusCode);
        Assert.NotNull(createNow.Headers.Location);
        var nowItem = (await createNow.Content.ReadFromJsonAsync<CustomerInteractionView>(WebJson))!;
        Assert.Equal($"/api/v1/admin/customers/{customer.Id}/interactions/{nowItem.Id}", createNow.Headers.Location!.OriginalString);
        Assert.Equal(clock.UtcNow.UtcDateTime, nowItem.OccurredAt);
        Assert.Equal("Test sales", nowItem.StaffDisplayName); // sales resolves displayName without staff.manage
        Assert.Equal(emoji4000, nowItem.Content);

        // 2. Backdated interaction with +07:00 offset
        using var createBackdated = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{customer.Id}/interactions", new
        {
            channel = "phone",
            kind = "inquiry",
            content = "  Khách hỏi ấm Chu Nê hôm qua  ",
            occurredAt = "2026-11-24T15:30:00+07:00",
            followUpAt = "2026-11-25T14:00:00+07:00"
        });
        Assert.Equal(HttpStatusCode.Created, createBackdated.StatusCode);
        var backdated = (await createBackdated.Content.ReadFromJsonAsync<CustomerInteractionView>(WebJson))!;
        Assert.Equal("Khách hỏi ấm Chu Nê hôm qua", backdated.Content);
        Assert.Equal(new DateTime(2026, 11, 24, 8, 30, 0, DateTimeKind.Utc), backdated.OccurredAt);
        Assert.Equal(new DateTime(2026, 11, 25, 7, 0, 0, DateTimeKind.Utc), backdated.FollowUpAt);

        // 3. Seed an interaction with a non-existent staff_id (R-P201-03 / P5-05 system compatibility)
        var orphanStaffId = Guid.CreateVersion7();
        var orphanInteractionId = Guid.CreateVersion7();
        await using (var db = h.OwnerContext())
        {
            db.Set<Interaction>().Add(new Interaction
            {
                Id = orphanInteractionId,
                CustomerId = customer.Id,
                StaffId = orphanStaffId,
                Channel = "other",
                Kind = "follow_up",
                Content = "Hẹn tự động từ hệ thống",
                OccurredAt = new DateTime(2026, 11, 23, 8, 0, 0, DateTimeKind.Utc),
                CreatedAt = new DateTime(2026, 11, 23, 8, 0, 0, DateTimeKind.Utc)
            });
            await db.SaveChangesAsync();
        }

        // 4. Paginated GET .../interactions ordered by occurredAt DESC, id DESC
        var page1 = (await h.Client.GetFromJsonAsync<CatalogPage<CustomerInteractionView>>(
            $"{CustomersRoot}/{customer.Id}/interactions?page=1&pageSize=2", WebJson))!;
        Assert.Equal(3L, page1.TotalItems);
        Assert.Equal(1, page1.Page);
        Assert.Equal(2, page1.PageSize);
        Assert.Equal(2, page1.Items.Count);
        Assert.Equal(nowItem.Id, page1.Items[0].Id);
        Assert.Equal("Test sales", page1.Items[0].StaffDisplayName);
        Assert.Equal(backdated.Id, page1.Items[1].Id);

        var page2 = (await h.Client.GetFromJsonAsync<CatalogPage<CustomerInteractionView>>(
            $"{CustomersRoot}/{customer.Id}/interactions?page=2&pageSize=2", WebJson))!;
        Assert.Equal(3L, page2.TotalItems);
        var orphanRow = Assert.Single(page2.Items);
        Assert.Equal(orphanInteractionId, orphanRow.Id);
        Assert.Equal(orphanStaffId, orphanRow.StaffId);
        Assert.Null(orphanRow.StaffDisplayName); // left-lookup preserves row safely

        // Unknown customer returns 404
        using var missingCustomer = await h.Client.GetAsync($"{CustomersRoot}/{Guid.CreateVersion7()}/interactions");
        await AssertProblemAsync(missingCustomer, HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task BrCrm06CreateAndPatchInteractionRejectFutureOccurredAtInvalidFollowUpOrderAndStrictJsonViolations()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero));
        await using var h = await P009Harness.CreateAsync(fixture, services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(clock);
        });
        await h.LoginAsync();

        var customer = await CreateCustomerAsync(h, new { fullName = "Khách Validation", phone = "0905111002" });
        var baseUrl = $"{CustomersRoot}/{customer.Id}/interactions";

        // Future occurredAt (+1 second -> strict, zero clock skew)
        using var futurePost = await h.MutateAsync(HttpMethod.Post, baseUrl, new
        {
            channel = "phone",
            kind = "consultation",
            content = "Tương lai",
            occurredAt = "2026-11-25T10:00:01Z"
        });
        var futureJson = await AssertProblemAsync(futurePost, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Contains("occurred_at_in_future", futureJson.GetProperty("errors").GetProperty("occurredAt").EnumerateArray().Select(x => x.GetString()));

        // followUpAt <= occurredAt on POST
        using var badFollowUp = await h.MutateAsync(HttpMethod.Post, baseUrl, new
        {
            channel = "phone",
            kind = "consultation",
            content = "Hẹn bằng giờ xảy ra",
            occurredAt = "2026-11-25T09:00:00Z",
            followUpAt = "2026-11-25T09:00:00Z"
        });
        var badFollowUpJson = await AssertProblemAsync(badFollowUp, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Contains("follow_up_before_or_equal_occurred_at",
            badFollowUpJson.GetProperty("errors").GetProperty("followUpAt").EnumerateArray().Select(x => x.GetString()));

        // Missing UTC offset
        using var noOffset = await h.MutateAsync(HttpMethod.Post, baseUrl, new
        {
            channel = "phone",
            kind = "note",
            content = "Thiếu offset",
            occurredAt = "2026-11-25T09:00:00"
        });
        await AssertProblemAsync(noOffset, HttpStatusCode.BadRequest, "validation_failed");

        // Unmapped campaignId and duplicate JSON keys on POST
        using var withCampaignId = await SendRawJsonAsync(h.Client, HttpMethod.Post, baseUrl,
            """{"channel":"phone","kind":"note","content":"Hi","campaignId":"01936000-0000-7000-8000-000000000001"}""");
        await AssertProblemAsync(withCampaignId, HttpStatusCode.BadRequest, "validation_failed");

        using var duplicateKeys = await SendRawJsonAsync(h.Client, HttpMethod.Post, baseUrl,
            """{"channel":"phone","channel":"email","kind":"note","content":"Hi"}""");
        await AssertProblemAsync(duplicateKeys, HttpStatusCode.BadRequest, "validation_failed");

        // Create valid interaction to test PATCH validation
        using var createdResp = await h.MutateAsync(HttpMethod.Post, baseUrl, new
        {
            channel = "phone",
            kind = "consultation",
            content = "Tư vấn ban đầu",
            occurredAt = "2026-11-25T08:00:00Z",
            followUpAt = "2026-11-25T09:00:00Z"
        });
        Assert.Equal(HttpStatusCode.Created, createdResp.StatusCode);
        var created = (await createdResp.Content.ReadFromJsonAsync<CustomerInteractionView>(WebJson))!;

        // Partial PATCH pushing occurredAt past existing followUpAt -> 400 follow_up_before_or_equal_occurred_at
        using var patchConflict = await h.MutateAsync(HttpMethod.Patch, $"{baseUrl}/{created.Id}", new
        {
            occurredAt = "2026-11-25T09:30:00Z"
        });
        var patchConflictJson = await AssertProblemAsync(patchConflict, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Contains("follow_up_before_or_equal_occurred_at",
            patchConflictJson.GetProperty("errors").GetProperty("followUpAt").EnumerateArray().Select(x => x.GetString()));

        // Partial PATCH pushing occurredAt into future -> 400 occurred_at_in_future
        using var patchFuture = await h.MutateAsync(HttpMethod.Patch, $"{baseUrl}/{created.Id}", new
        {
            occurredAt = "2026-11-25T10:00:01Z",
            followUpAt = "2026-11-25T11:00:00Z"
        });
        var patchFutureJson = await AssertProblemAsync(patchFuture, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Contains("occurred_at_in_future",
            patchFutureJson.GetProperty("errors").GetProperty("occurredAt").EnumerateArray().Select(x => x.GetString()));

        // Empty PATCH / disallowed completion fields / campaignId -> 400
        foreach (var badPatch in new[]
        {
            """{}""",
            """{"followUpDoneAt":"2026-11-25T09:30:00Z"}""",
            """{"followUpResult":"Bypass"}""",
            """{"campaignId":"01936000-0000-7000-8000-000000000001"}""",
            """{"occurredAt":null}"""
        })
        {
            using var resp = await SendRawJsonAsync(h.Client, HttpMethod.Patch, $"{baseUrl}/{created.Id}", badPatch);
            await AssertProblemAsync(resp, HttpStatusCode.BadRequest, "validation_failed");
        }
    }

    [Fact]
    public async Task BrCrm07CreateAndPatchInteractionUpdatesLastContactedAtExceptNoteUsingGreatestAndRecompute()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero));
        await using var h = await P009Harness.CreateAsync(fixture, services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(clock);
        });
        await h.LoginAsync();

        var customer = await CreateCustomerAsync(h, new { fullName = "Khách BR-CRM-07", phone = "0905111003" });
        var initialUpdatedAt = customer.UpdatedAt;
        var baseUrl = $"{CustomersRoot}/{customer.Id}/interactions";

        // Advance clock so any unintended Customer.UpdatedAt change would be visible
        clock.UtcNow = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

        // (a) POST kind="note" leaves last_contacted_at == null and customer.UpdatedAt untouched
        using (var noteResp = await h.MutateAsync(HttpMethod.Post, baseUrl, new
        {
            channel = "other",
            kind = "note",
            content = "Ghi chú nội bộ",
            occurredAt = "2026-11-25T11:00:00Z"
        }))
        {
            Assert.Equal(HttpStatusCode.Created, noteResp.StatusCode);
        }

        var afterNote = (await h.Client.GetFromJsonAsync<CustomerDetailView>($"{CustomersRoot}/{customer.Id}", WebJson))!;
        Assert.Null(afterNote.Stats.LastContactedAt);
        Assert.Equal(initialUpdatedAt, afterNote.UpdatedAt);

        // (b) POST kind="consultation" at T2 sets last_contacted_at == T2
        var t2 = new DateTime(2026, 11, 25, 9, 0, 0, DateTimeKind.Utc);
        using var c2Resp = await h.MutateAsync(HttpMethod.Post, baseUrl, new
        {
            channel = "zalo_personal",
            kind = "consultation",
            content = "Tư vấn T2",
            occurredAt = "2026-11-25T09:00:00Z"
        });
        Assert.Equal(HttpStatusCode.Created, c2Resp.StatusCode);
        var i2 = (await c2Resp.Content.ReadFromJsonAsync<CustomerInteractionView>(WebJson))!;

        var afterT2 = (await h.Client.GetFromJsonAsync<CustomerDetailView>($"{CustomersRoot}/{customer.Id}", WebJson))!;
        Assert.Equal(t2, afterT2.Stats.LastContactedAt);

        // (c) Backdated POST kind="inquiry" at T1 < T2 preserves T2 via GREATEST
        var t1 = new DateTime(2026, 11, 24, 9, 0, 0, DateTimeKind.Utc);
        using var c1Resp = await h.MutateAsync(HttpMethod.Post, baseUrl, new
        {
            channel = "phone",
            kind = "inquiry",
            content = "Gọi hỏi hàng T1",
            occurredAt = "2026-11-24T09:00:00Z"
        });
        Assert.Equal(HttpStatusCode.Created, c1Resp.StatusCode);
        var i1 = (await c1Resp.Content.ReadFromJsonAsync<CustomerInteractionView>(WebJson))!;

        var afterT1 = (await h.Client.GetFromJsonAsync<CustomerDetailView>($"{CustomersRoot}/{customer.Id}", WebJson))!;
        Assert.Equal(t2, afterT1.Stats.LastContactedAt);

        // (d) PATCH i2 from consultation -> note recomputes last_contacted_at down to T1
        using (var patchToNote = await h.MutateAsync(HttpMethod.Patch, $"{baseUrl}/{i2.Id}", new { kind = "note" }))
        {
            Assert.Equal(HttpStatusCode.OK, patchToNote.StatusCode);
        }

        var afterPatchToNote = (await h.Client.GetFromJsonAsync<CustomerDetailView>($"{CustomersRoot}/{customer.Id}", WebJson))!;
        Assert.Equal(t1, afterPatchToNote.Stats.LastContactedAt);

        // (e) PATCH i1 occurredAt earlier to T0 < T1 recomputes to T0
        var t0 = new DateTime(2026, 11, 23, 8, 0, 0, DateTimeKind.Utc);
        using (var patchEarlier = await h.MutateAsync(HttpMethod.Patch, $"{baseUrl}/{i1.Id}", new { occurredAt = "2026-11-23T08:00:00Z" }))
        {
            Assert.Equal(HttpStatusCode.OK, patchEarlier.StatusCode);
        }

        var afterPatchEarlier = (await h.Client.GetFromJsonAsync<CustomerDetailView>($"{CustomersRoot}/{customer.Id}", WebJson))!;
        Assert.Equal(t0, afterPatchEarlier.Stats.LastContactedAt);

        // (f) PATCH i1 to note leaves zero non-note interactions -> last_contacted_at becomes null
        using (var patchAllNotes = await h.MutateAsync(HttpMethod.Patch, $"{baseUrl}/{i1.Id}", new { kind = "note" }))
        {
            Assert.Equal(HttpStatusCode.OK, patchAllNotes.StatusCode);
        }

        var afterAllNotes = (await h.Client.GetFromJsonAsync<CustomerDetailView>($"{CustomersRoot}/{customer.Id}", WebJson))!;
        Assert.Null(afterAllNotes.Stats.LastContactedAt);
    }

    [Fact]
    public async Task BrCrm07ConcurrentPostAndPatchWithSeededCampaignMessageMaintainExactLastContactedAtWithoutLostUpdates()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 11, 25, 16, 0, 0, TimeSpan.Zero));
        await using var h = await P009Harness.CreateAsync(fixture, services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(clock);
        });
        await h.LoginAsync();

        var customer = await CreateCustomerAsync(h, new { fullName = "Khách Đua LastContacted", phone = "0905111004" });
        var baseUrl = $"{CustomersRoot}/{customer.Id}/interactions";

        // Seed a system campaign_message interaction at T_campaign = 12:00Z
        var campaignOccurredAt = new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
        await using (var db = h.OwnerContext())
        {
            db.Set<Interaction>().Add(new Interaction
            {
                Id = Guid.CreateVersion7(),
                CustomerId = customer.Id,
                StaffId = h.OwnerId,
                Channel = "zalo_oa",
                Kind = "campaign_message",
                Content = "Tin chiến dịch trà mới",
                OccurredAt = campaignOccurredAt,
                CampaignId = Guid.CreateVersion7(),
                CreatedAt = campaignOccurredAt
            });
            var row = await db.Set<Customer>().SingleAsync(x => x.Id == customer.Id);
            row.LastContactedAt = campaignOccurredAt;
            await db.SaveChangesAsync();
        }

        // Create two editable interactions to PATCH concurrently
        var seedA = await CreateInteractionAsync(h, customer.Id, new
        {
            channel = "phone",
            kind = "consultation",
            content = "Seed A",
            occurredAt = "2026-11-25T15:00:00Z"
        });
        var seedB = await CreateInteractionAsync(h, customer.Id, new
        {
            channel = "zalo_personal",
            kind = "quote",
            content = "Seed B",
            occurredAt = "2026-11-25T14:00:00Z"
        });

        // 8 concurrent operations under barrier:
        // - 2 PATCHes lowering seedA/seedB (to note / earlier than campaign_message)
        // - 3 POSTs of non-note interactions at 10:00Z, 11:00Z, 13:30Z
        // - 2 POSTs of note interactions at 15:50Z and 15:55Z (must NOT win)
        // - 1 PATCH raising seedB back or changing content
        // Expected final MAX(occurred_at) WHERE kind <> 'note' == 13:30Z (or campaign_message 12:00Z in phase 2!)
        var operations = new (HttpMethod Method, string Url, object Body)[]
        {
            (HttpMethod.Patch, $"{baseUrl}/{seedA.Id}", new { kind = "note" }),
            (HttpMethod.Patch, $"{baseUrl}/{seedB.Id}", new { occurredAt = "2026-11-25T09:00:00Z" }),
            (HttpMethod.Post, baseUrl, new { channel = "phone", kind = "inquiry", content = "Concurrent 10:00", occurredAt = "2026-11-25T10:00:00Z" }),
            (HttpMethod.Post, baseUrl, new { channel = "email", kind = "quote", content = "Concurrent 11:00", occurredAt = "2026-11-25T11:00:00Z" }),
            (HttpMethod.Post, baseUrl, new { channel = "zalo_personal", kind = "consultation", content = "Concurrent 13:30", occurredAt = "2026-11-25T13:30:00Z" }),
            (HttpMethod.Post, baseUrl, new { channel = "other", kind = "note", content = "Note 15:50", occurredAt = "2026-11-25T15:50:00Z" }),
            (HttpMethod.Post, baseUrl, new { channel = "other", kind = "note", content = "Note 15:55", occurredAt = "2026-11-25T15:55:00Z" }),
            (HttpMethod.Post, baseUrl, new { channel = "facebook", kind = "follow_up", content = "Concurrent 11:30", occurredAt = "2026-11-25T11:30:00Z" })
        };

        var clients = new List<HttpClient>(operations.Length);
        var tokens = new List<string>(operations.Length);
        try
        {
            for (var i = 0; i < operations.Length; i++)
            {
                var client = h.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
                await LoginClientAsync(client, "owner@example.test", P009Harness.Password);
                var csrf = (await client.GetFromJsonAsync<CsrfResponse>("/api/v1/admin/auth/csrf"))!.Token;
                clients.Add(client);
                tokens.Add(csrf);
            }

            var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = Enumerable.Range(0, operations.Length).Select(async idx =>
            {
                await barrier.Task;
                var (method, url, body) = operations[idx];
                using var req = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
                req.Headers.Add("X-CSRF-TOKEN", tokens[idx]);
                using var resp = await clients[idx].SendAsync(req);
                var text = await resp.Content.ReadAsStringAsync();
                return (resp.StatusCode, text);
            }).ToArray();

            barrier.SetResult();
            var results = await Task.WhenAll(tasks);

            foreach (var (status, text) in results)
            {
                Assert.True(status is HttpStatusCode.OK or HttpStatusCode.Created, $"Expected 200/201, got {(int)status}: {text}");
            }
        }
        finally
        {
            foreach (var c in clients) c.Dispose();
        }

        // Verify exact consistency with PostgreSQL MAX(occurred_at) WHERE kind <> 'note'
        await using (var db = h.OwnerContext())
        {
            var expectedMax = await db.Set<Interaction>().AsNoTracking()
                .Where(x => x.CustomerId == customer.Id && x.Kind != "note")
                .MaxAsync(x => (DateTime?)x.OccurredAt);
            var persisted = await db.Set<Customer>().AsNoTracking().SingleAsync(x => x.Id == customer.Id);
            Assert.Equal(new DateTime(2026, 11, 25, 13, 30, 0, DateTimeKind.Utc), expectedMax);
            Assert.Equal(expectedMax, persisted.LastContactedAt);
        }

        // Phase 2: PATCH the 13:30 consultation to note -> recompute MUST land on seeded campaign_message (12:00Z)!
        var listed = (await h.Client.GetFromJsonAsync<CatalogPage<CustomerInteractionView>>($"{baseUrl}?page=1&pageSize=20", WebJson))!;
        var winner1330 = listed.Items.Single(x => x.Content == "Concurrent 13:30");
        using (var patchToNote = await h.MutateAsync(HttpMethod.Patch, $"{baseUrl}/{winner1330.Id}", new { kind = "note" }))
        {
            Assert.Equal(HttpStatusCode.OK, patchToNote.StatusCode);
        }

        await using (var db = h.OwnerContext())
        {
            var persisted = await db.Set<Customer>().AsNoTracking().SingleAsync(x => x.Id == customer.Id);
            Assert.Equal(campaignOccurredAt, persisted.LastContactedAt);
        }
    }

    [Fact]
    public async Task InteractionOrderLinkAcceptsCustomerOwnOrderAndRejectsMissingOrOtherCustomerOrder()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        var customerA = await CreateCustomerAsync(h, new { fullName = "Khách Đơn A", phone = "0905111005" });
        var customerB = await CreateCustomerAsync(h, new { fullName = "Khách Đơn B", phone = "0905111006" });

        var orderAId = await SeedOrderAsync(h, customerA.Id, "TP261125AAAA");
        var orderBId = await SeedOrderAsync(h, customerB.Id, "TP261125BBBB");
        var urlA = $"{CustomersRoot}/{customerA.Id}/interactions";

        // Attach Customer A's own order -> 201 with orderCode resolved
        using var okResp = await h.MutateAsync(HttpMethod.Post, urlA, new
        {
            channel = "phone",
            kind = "follow_up",
            content = "Chăm sóc đơn A",
            orderId = orderAId
        });
        Assert.Equal(HttpStatusCode.Created, okResp.StatusCode);
        var created = (await okResp.Content.ReadFromJsonAsync<CustomerInteractionView>(WebJson))!;
        Assert.Equal(orderAId, created.OrderId);
        Assert.Equal("TP261125AAAA", created.OrderCode);

        // Attach unknown orderId -> 400 order_not_found
        using var missingOrder = await h.MutateAsync(HttpMethod.Post, urlA, new
        {
            channel = "phone",
            kind = "inquiry",
            content = "Đơn không tồn tại",
            orderId = Guid.CreateVersion7()
        });
        var missingJson = await AssertProblemAsync(missingOrder, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Contains("order_not_found", missingJson.GetProperty("errors").GetProperty("orderId").EnumerateArray().Select(x => x.GetString()));

        // Attach Customer B's order to Customer A -> 400 order_customer_mismatch
        using var wrongCustomer = await h.MutateAsync(HttpMethod.Post, urlA, new
        {
            channel = "phone",
            kind = "inquiry",
            content = "Đơn khách khác",
            orderId = orderBId
        });
        var mismatchJson = await AssertProblemAsync(wrongCustomer, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Contains("order_customer_mismatch", mismatchJson.GetProperty("errors").GetProperty("orderId").EnumerateArray().Select(x => x.GetString()));

        // PATCH with Customer B's order -> 400 order_customer_mismatch
        using var patchWrongOrder = await h.MutateAsync(HttpMethod.Patch, $"{urlA}/{created.Id}", new { orderId = orderBId });
        var patchMismatchJson = await AssertProblemAsync(patchWrongOrder, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Contains("order_customer_mismatch", patchMismatchJson.GetProperty("errors").GetProperty("orderId").EnumerateArray().Select(x => x.GetString()));

        // PATCH orderId = null clears link cleanly
        using var patchClear = await h.MutateAsync(HttpMethod.Patch, $"{urlA}/{created.Id}", new { orderId = (Guid?)null });
        Assert.Equal(HttpStatusCode.OK, patchClear.StatusCode);
        var cleared = (await patchClear.Content.ReadFromJsonAsync<CustomerInteractionView>(WebJson))!;
        Assert.Null(cleared.OrderId);
        Assert.Null(cleared.OrderCode);

        // Direct store test proving savepoint catches PostgreSQL 23503 fk_interactions_order without 500
        await using var scope = h.Factory.Services.CreateAsyncScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var store = scope.ServiceProvider.GetRequiredService<IInteractionStore>();
        var nowUtc = new DateTime(2026, 11, 25, 10, 0, 0, DateTimeKind.Utc);
        var fkResult = await uow.ExecuteAsync(async ct =>
        {
            var locked = await store.LockCustomerAsync(customerA.Id, ct);
            Assert.NotNull(locked);
            return await store.CreateAsync(new InteractionRecord
            {
                Id = Guid.CreateVersion7(),
                CustomerId = customerA.Id,
                StaffId = h.OwnerId,
                Channel = "phone",
                Kind = "note",
                Content = "FK savepoint test",
                OccurredAt = nowUtc,
                OrderId = Guid.CreateVersion7(), // non-existent order -> triggers 23503 fk_interactions_order
                CreatedAt = nowUtc
            }, nowUtc, ct);
        }, CancellationToken.None);
        Assert.False(fkResult.IsSuccess);
        Assert.Equal("validation_failed", fkResult.Error.Code);
        Assert.Contains("order_not_found", fkResult.Error.ValidationErrors!["orderId"]);
    }

    [Fact]
    public async Task PatchInteractionUpdatesPartialFieldsAndCancelsPendingFollowUpWhenSetToNull()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero));
        await using var h = await P009Harness.CreateAsync(fixture, services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(clock);
        });
        await h.LoginAsync();

        var customer = await CreateCustomerAsync(h, new { fullName = "Khách Hủy Hẹn", phone = "0905111007" });
        var created = await CreateInteractionAsync(h, customer.Id, new
        {
            channel = "zalo_personal",
            kind = "consultation",
            content = "Nội dung ban đầu",
            occurredAt = "2026-11-25T08:00:00Z",
            followUpAt = "2026-11-25T15:00:00Z"
        });

        var beforeQueue = (await h.Client.GetFromJsonAsync<CatalogPage<FollowUpItemView>>($"{FollowUpsRoot}?due=today", WebJson))!;
        Assert.Contains(beforeQueue.Items, x => x.InteractionId == created.Id);

        // Partial PATCH updating only content preserves channel, kind, occurredAt, followUpAt
        using var patchContent = await h.MutateAsync(HttpMethod.Patch, $"{CustomersRoot}/{customer.Id}/interactions/{created.Id}", new
        {
            content = "Nội dung đã chỉnh sửa"
        });
        Assert.Equal(HttpStatusCode.OK, patchContent.StatusCode);
        var updated1 = (await patchContent.Content.ReadFromJsonAsync<CustomerInteractionView>(WebJson))!;
        Assert.Equal("Nội dung đã chỉnh sửa", updated1.Content);
        Assert.Equal("zalo_personal", updated1.Channel);
        Assert.Equal("consultation", updated1.Kind);
        Assert.Equal(created.OccurredAt, updated1.OccurredAt);
        Assert.Equal(created.FollowUpAt, updated1.FollowUpAt);

        // PATCH followUpAt = null cancels pending follow-up and removes it from GET admin/follow-ups
        using var cancelFollowUp = await h.MutateAsync(HttpMethod.Patch, $"{CustomersRoot}/{customer.Id}/interactions/{created.Id}", new
        {
            followUpAt = (string?)null
        });
        Assert.Equal(HttpStatusCode.OK, cancelFollowUp.StatusCode);
        var updated2 = (await cancelFollowUp.Content.ReadFromJsonAsync<CustomerInteractionView>(WebJson))!;
        Assert.Null(updated2.FollowUpAt);

        var afterQueue = (await h.Client.GetFromJsonAsync<CatalogPage<FollowUpItemView>>($"{FollowUpsRoot}?due=today", WebJson))!;
        Assert.DoesNotContain(afterQueue.Items, x => x.InteractionId == created.Id);
    }

    [Fact]
    public async Task CampaignMessageKindIsRejectedOnManualWriteImmutableOnAnyPatchWhileReadableAndCompletable()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero));
        await using var h = await P009Harness.CreateAsync(fixture, services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(clock);
        });
        await h.LoginAsync();

        var customer = await CreateCustomerAsync(h, new { fullName = "Khách Chiến Dịch", phone = "0905111008" });
        var ownOrderId = await SeedOrderAsync(h, customer.Id, "TP261125CAMP");
        var baseUrl = $"{CustomersRoot}/{customer.Id}/interactions";

        // 1. POST kind="campaign_message" -> 400 invalid_interaction_kind
        using var postCamp = await h.MutateAsync(HttpMethod.Post, baseUrl, new
        {
            channel = "zalo_oa",
            kind = "campaign_message",
            content = "Cố tình tạo thủ công"
        });
        var postCampJson = await AssertProblemAsync(postCamp, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Contains("invalid_interaction_kind", postCampJson.GetProperty("errors").GetProperty("kind").EnumerateArray().Select(x => x.GetString()));

        // 2. PATCH existing manual row to kind="campaign_message" -> 400 invalid_interaction_kind
        var manual = await CreateInteractionAsync(h, customer.Id, new
        {
            channel = "phone",
            kind = "inquiry",
            content = "Tương tác thường"
        });
        using var patchToCamp = await h.MutateAsync(HttpMethod.Patch, $"{baseUrl}/{manual.Id}", new { kind = "campaign_message" });
        var patchToCampJson = await AssertProblemAsync(patchToCamp, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Contains("invalid_interaction_kind", patchToCampJson.GetProperty("errors").GetProperty("kind").EnumerateArray().Select(x => x.GetString()));

        // 3. Seed system-generated campaign_message row (P5-03 simulation) with campaign_id and follow_up_at
        var campaignId = Guid.CreateVersion7();
        var campaignInteractionId = Guid.CreateVersion7();
        var campaignOccurredAt = new DateTime(2026, 11, 25, 7, 0, 0, DateTimeKind.Utc);
        var campaignFollowUpAt = new DateTime(2026, 11, 25, 14, 0, 0, DateTimeKind.Utc);
        await using (var db = h.OwnerContext())
        {
            db.Set<Interaction>().Add(new Interaction
            {
                Id = campaignInteractionId,
                CustomerId = customer.Id,
                StaffId = h.OwnerId,
                Channel = "zalo_oa",
                Kind = "campaign_message",
                Content = "Tin nhắn chiến dịch mùa thu",
                OccurredAt = campaignOccurredAt,
                CampaignId = campaignId,
                FollowUpAt = campaignFollowUpAt,
                CreatedAt = campaignOccurredAt
            });
            await db.SaveChangesAsync();
        }

        // 4. GET .../interactions reads all 7 kinds including campaign_message and campaignId
        var list = (await h.Client.GetFromJsonAsync<CatalogPage<CustomerInteractionView>>(baseUrl, WebJson))!;
        var readCamp = list.Items.Single(x => x.Id == campaignInteractionId);
        Assert.Equal("campaign_message", readCamp.Kind);
        Assert.Equal(campaignId, readCamp.CampaignId);
        Assert.Equal("Tin nhắn chiến dịch mùa thu", readCamp.Content);

        // 5. ZERO PATCH bypass: ANY PATCH mutation on campaign_message row -> 409 campaign_interaction_immutable
        var bypassPayloads = new object[]
        {
            new { content = "Sửa nội dung chiến dịch" },
            new { orderId = ownOrderId },
            new { followUpAt = "2026-11-26T08:00:00Z" },
            new { followUpAt = (string?)null },
            new { channel = "phone" },
            new { kind = "note" },
            new { occurredAt = "2026-11-25T06:00:00Z" }
        };

        foreach (var payload in bypassPayloads)
        {
            using var resp = await h.MutateAsync(HttpMethod.Patch, $"{baseUrl}/{campaignInteractionId}", payload);
            await AssertProblemAsync(resp, HttpStatusCode.Conflict, "campaign_interaction_immutable");
        }

        // Confirm DB row stayed completely untouched after all PATCH attempts
        await using (var db = h.OwnerContext())
        {
            var unchanged = await db.Set<Interaction>().AsNoTracking().SingleAsync(x => x.Id == campaignInteractionId);
            Assert.Equal("campaign_message", unchanged.Kind);
            Assert.Equal("zalo_oa", unchanged.Channel);
            Assert.Equal("Tin nhắn chiến dịch mùa thu", unchanged.Content);
            Assert.Null(unchanged.OrderId);
            Assert.Equal(campaignFollowUpAt, unchanged.FollowUpAt);
        }

        // 6. POST .../complete-follow-up IS still allowed on campaign_message row when follow_up_at != null
        using var completeCamp = await h.MutateAsync(HttpMethod.Post, $"{baseUrl}/{campaignInteractionId}/complete-follow-up", new
        {
            result = "Khách đã phản hồi chiến dịch và hẹn ghé showroom"
        });
        Assert.Equal(HttpStatusCode.OK, completeCamp.StatusCode);
        var completed = (await completeCamp.Content.ReadFromJsonAsync<CustomerInteractionView>(WebJson))!;
        Assert.Equal("campaign_message", completed.Kind);
        Assert.Equal(campaignId, completed.CampaignId);
        Assert.Equal(clock.UtcNow.UtcDateTime, completed.FollowUpDoneAt);
        Assert.Equal("Khách đã phản hồi chiến dịch và hẹn ghé showroom", completed.FollowUpResult);
    }

    [Fact]
    public async Task BrCrm06CompleteFollowUpRequiresResultSetsDoneAtHandlesConcurrencyAndBlocksPostCompletionMutation()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero));
        await using var h = await P009Harness.CreateAsync(fixture, services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(clock);
        });
        await h.LoginAsync();

        var customer = await CreateCustomerAsync(h, new { fullName = "Khách Hoàn Thành Hẹn", phone = "0905111009" });
        var baseUrl = $"{CustomersRoot}/{customer.Id}/interactions";

        // Unscheduled interaction -> 409 follow_up_not_scheduled
        var unscheduled = await CreateInteractionAsync(h, customer.Id, new
        {
            channel = "phone",
            kind = "inquiry",
            content = "Không có lịch hẹn",
            occurredAt = "2026-11-25T08:00:00Z"
        });
        using var noScheduleResp = await h.MutateAsync(HttpMethod.Post, $"{baseUrl}/{unscheduled.Id}/complete-follow-up", new { result = "Gọi lại" });
        await AssertProblemAsync(noScheduleResp, HttpStatusCode.Conflict, "follow_up_not_scheduled");

        // Scheduled interaction -> blank result rejected with 400 follow_up_result_required
        var scheduled = await CreateInteractionAsync(h, customer.Id, new
        {
            channel = "phone",
            kind = "consultation",
            content = "Có lịch hẹn",
            occurredAt = "2026-11-25T08:30:00Z",
            followUpAt = "2026-11-25T09:30:00Z"
        });
        var lastContactedBeforeComplete = (await h.Client.GetFromJsonAsync<CustomerDetailView>($"{CustomersRoot}/{customer.Id}", WebJson))!.Stats.LastContactedAt;
        Assert.Equal(new DateTime(2026, 11, 25, 8, 30, 0, DateTimeKind.Utc), lastContactedBeforeComplete);

        using var blankResult = await h.MutateAsync(HttpMethod.Post, $"{baseUrl}/{scheduled.Id}/complete-follow-up", new { result = "   " });
        var blankJson = await AssertProblemAsync(blankResult, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Contains("follow_up_result_required", blankJson.GetProperty("errors").GetProperty("result").EnumerateArray().Select(x => x.GetString()));

        // Advance clock to prove complete-follow-up does NOT mutate last_contacted_at
        clock.UtcNow = new DateTimeOffset(2026, 11, 25, 11, 0, 0, TimeSpan.Zero);

        // Concurrent race: 6 parallel complete-follow-up requests -> exactly 1x 200 OK, 5x 409 follow_up_already_completed
        var clients = new List<HttpClient>(6);
        var tokens = new List<string>(6);
        try
        {
            for (var i = 0; i < 6; i++)
            {
                var client = h.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
                await LoginClientAsync(client, "owner@example.test", P009Harness.Password);
                var csrf = (await client.GetFromJsonAsync<CsrfResponse>("/api/v1/admin/auth/csrf"))!.Token;
                clients.Add(client);
                tokens.Add(csrf);
            }

            var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = Enumerable.Range(0, 6).Select(async idx =>
            {
                await barrier.Task;
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/{scheduled.Id}/complete-follow-up")
                {
                    Content = JsonContent.Create(new { result = $"Hoàn thành #{idx}" })
                };
                req.Headers.Add("X-CSRF-TOKEN", tokens[idx]);
                using var resp = await clients[idx].SendAsync(req);
                var body = await resp.Content.ReadAsStringAsync();
                return (resp.StatusCode, body);
            }).ToArray();

            barrier.SetResult();
            var raceResults = await Task.WhenAll(tasks);

            var okCount = raceResults.Count(x => x.StatusCode == HttpStatusCode.OK);
            var conflictItems = raceResults.Where(x => x.StatusCode == HttpStatusCode.Conflict).ToArray();
            Assert.Equal(1, okCount);
            Assert.Equal(5, conflictItems.Length);
            foreach (var (_, body) in conflictItems)
            {
                using var doc = JsonDocument.Parse(body);
                Assert.Equal("follow_up_already_completed", doc.RootElement.GetProperty("code").GetString());
            }
        }
        finally
        {
            foreach (var c in clients) c.Dispose();
        }

        // Verify disappeared from follow-ups queue and last_contacted_at unchanged
        var queue = (await h.Client.GetFromJsonAsync<CatalogPage<FollowUpItemView>>($"{FollowUpsRoot}?due=today", WebJson))!;
        Assert.DoesNotContain(queue.Items, x => x.InteractionId == scheduled.Id);
        var customerAfter = (await h.Client.GetFromJsonAsync<CustomerDetailView>($"{CustomersRoot}/{customer.Id}", WebJson))!;
        Assert.Equal(lastContactedBeforeComplete, customerAfter.Stats.LastContactedAt);

        // PATCH followUpAt = null (or new timestamp) after completion -> 409 follow_up_already_completed (never 23514/500)
        using var patchNullFollowUp = await h.MutateAsync(HttpMethod.Patch, $"{baseUrl}/{scheduled.Id}", new { followUpAt = (string?)null });
        await AssertProblemAsync(patchNullFollowUp, HttpStatusCode.Conflict, "follow_up_already_completed");

        using var patchNewFollowUp = await h.MutateAsync(HttpMethod.Patch, $"{baseUrl}/{scheduled.Id}", new { followUpAt = "2026-11-26T08:00:00Z" });
        await AssertProblemAsync(patchNewFollowUp, HttpStatusCode.Conflict, "follow_up_already_completed");
    }

    [Fact]
    public async Task BrCrm06FollowUpsTodayOverdueAndWeekFilterByVietnamCalendarAndSortOverdueFirst()
    {
        // Pin clock at 2026-11-25T16:30:00Z == 2026-11-25 23:30:00 +07:00 (30 minutes before Vietnam midnight!)
        // - vnToday = 2026-11-25
        // - endOfTodayUtc = 2026-11-25T17:00:00Z (2026-11-26 00:00 VN)
        // - endOfWeekUtc  = 2026-12-01T17:00:00Z (2026-12-02 00:00 VN)
        var clock = new FixedClock(new DateTimeOffset(2026, 11, 25, 16, 30, 0, TimeSpan.Zero));
        await using var h = await P009Harness.CreateAsync(fixture, services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(clock);
        });
        await h.LoginAsync();

        var active = await CreateCustomerAsync(h, new { fullName = "Khách Hoạt Động", phone = "0905111010", customerGroup = "wholesale", isVip = true });
        var dnc = await CreateCustomerAsync(h, new { fullName = "Khách DNC", phone = "0905111011", contactStatus = "do_not_contact" });
        var erased = await CreateCustomerAsync(h, new { fullName = "Khách Đã Xóa", phone = "0905111012" });
        var mergedTarget = await CreateCustomerAsync(h, new { fullName = "Khách Đích", phone = "0905111013" });
        var mergedSource = await CreateCustomerAsync(h, new { fullName = "Khách Bị Gộp", phone = "0905111014" });

        // 1. Overdue 2 days (oldest)
        var overdue2d = await CreateInteractionAsync(h, active.Id, new
        {
            channel = "phone",
            kind = "follow_up",
            content = "Quá hạn 2 ngày",
            occurredAt = "2026-11-22T08:00:00Z",
            followUpAt = "2026-11-23T16:00:00Z"
        });
        // 2. Overdue 30 mins same VN day (23:00 VN = 16:00Z) on DNC customer
        var overdue30m = await CreateInteractionAsync(h, dnc.Id, new
        {
            channel = "zalo_personal",
            kind = "complaint",
            content = "Quá hạn 30 phút (DNC)",
            occurredAt = "2026-11-25T10:00:00Z",
            followUpAt = "2026-11-25T16:00:00Z"
        });
        // 3. Upcoming today VN (23:50 VN = 16:50Z, < 17:00Z)
        var upcomingToday = await CreateInteractionAsync(h, active.Id, new
        {
            channel = "phone",
            kind = "quote",
            content = "Hẹn 23:50 hôm nay VN",
            occurredAt = "2026-11-25T12:00:00Z",
            followUpAt = "2026-11-25T16:50:00Z"
        });
        // 4. Tomorrow VN just past midnight (00:10 VN Nov 26 = 17:10Z -> excluded from today, included in week)
        var tomorrowVn = await CreateInteractionAsync(h, active.Id, new
        {
            channel = "phone",
            kind = "consultation",
            content = "Hẹn 00:10 ngày mai VN",
            occurredAt = "2026-11-25T12:00:00Z",
            followUpAt = "2026-11-25T17:10:00Z"
        });
        // 5. Last minute inside 7-day VN window (Dec 1 23:59 VN = 2026-12-01T16:59:00Z < 2026-12-01T17:00:00Z)
        var day6LastMin = await CreateInteractionAsync(h, active.Id, new
        {
            channel = "email",
            kind = "follow_up",
            content = "Cuối ngày thứ 7 VN",
            occurredAt = "2026-11-25T12:00:00Z",
            followUpAt = "2026-12-01T16:59:00Z"
        });
        // 6. Exact exclusive boundary (Dec 2 00:00 VN = 2026-12-01T17:00:00Z -> excluded from week!)
        var day7Excluded = await CreateInteractionAsync(h, active.Id, new
        {
            channel = "email",
            kind = "follow_up",
            content = "Ngoài cửa sổ tuần",
            occurredAt = "2026-11-25T12:00:00Z",
            followUpAt = "2026-12-01T17:00:00Z"
        });
        // 7. Completed follow-up -> excluded from all queues
        var doneItem = await CreateInteractionAsync(h, active.Id, new
        {
            channel = "phone",
            kind = "follow_up",
            content = "Đã hoàn thành",
            occurredAt = "2026-11-24T08:00:00Z",
            followUpAt = "2026-11-24T12:00:00Z"
        });
        using (var doneResp = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{active.Id}/interactions/{doneItem.Id}/complete-follow-up", new { result = "Xong" }))
            Assert.Equal(HttpStatusCode.OK, doneResp.StatusCode);

        // 8. Follow-ups on erased and merged customers -> excluded from all queues
        await CreateInteractionAsync(h, erased.Id, new
        {
            channel = "phone",
            kind = "follow_up",
            content = "Hẹn của khách sẽ bị xóa",
            occurredAt = "2026-11-24T08:00:00Z",
            followUpAt = "2026-11-25T10:00:00Z"
        });
        await CreateInteractionAsync(h, mergedSource.Id, new
        {
            channel = "phone",
            kind = "follow_up",
            content = "Hẹn của khách sẽ bị gộp",
            occurredAt = "2026-11-24T08:00:00Z",
            followUpAt = "2026-11-25T10:00:00Z"
        });
        await using (var db = h.OwnerContext())
        {
            var eRow = await db.Set<Customer>().SingleAsync(x => x.Id == erased.Id);
            eRow.ContactStatus = "erased";
            eRow.ErasedAt = clock.UtcNow.UtcDateTime;
            var mRow = await db.Set<Customer>().SingleAsync(x => x.Id == mergedSource.Id);
            mRow.MergedIntoCustomerId = mergedTarget.Id;
            mRow.PhoneE164 = null;
            await db.SaveChangesAsync();
        }

        // Check default (omitted due) == due=today: [overdue2d, overdue30m, upcomingToday]
        var defaultPage = (await h.Client.GetFromJsonAsync<CatalogPage<FollowUpItemView>>(FollowUpsRoot, WebJson))!;
        var todayPage = (await h.Client.GetFromJsonAsync<CatalogPage<FollowUpItemView>>($"{FollowUpsRoot}?due=today", WebJson))!;
        Assert.Equal([overdue2d.Id, overdue30m.Id, upcomingToday.Id], defaultPage.Items.Select(x => x.InteractionId).ToArray());
        Assert.Equal([overdue2d.Id, overdue30m.Id, upcomingToday.Id], todayPage.Items.Select(x => x.InteractionId).ToArray());
        Assert.True(todayPage.Items[0].IsOverdue);
        Assert.Equal("active", todayPage.Items[0].CustomerContactStatus);
        Assert.Equal("wholesale", todayPage.Items[0].CustomerGroup);
        Assert.True(todayPage.Items[0].CustomerIsVip);
        Assert.True(todayPage.Items[1].IsOverdue);
        Assert.Equal("do_not_contact", todayPage.Items[1].CustomerContactStatus);
        Assert.False(todayPage.Items[2].IsOverdue);

        // Check due=overdue: [overdue2d, overdue30m]
        var overduePage = (await h.Client.GetFromJsonAsync<CatalogPage<FollowUpItemView>>($"{FollowUpsRoot}?due=overdue", WebJson))!;
        Assert.Equal([overdue2d.Id, overdue30m.Id], overduePage.Items.Select(x => x.InteractionId).ToArray());
        Assert.All(overduePage.Items, item => Assert.True(item.IsOverdue));

        // Check due=week: [overdue2d, overdue30m, upcomingToday, tomorrowVn, day6LastMin] (excludes day7Excluded, done, erased, merged)
        var weekPage = (await h.Client.GetFromJsonAsync<CatalogPage<FollowUpItemView>>($"{FollowUpsRoot}?due=week", WebJson))!;
        Assert.Equal([overdue2d.Id, overdue30m.Id, upcomingToday.Id, tomorrowVn.Id, day6LastMin.Id],
            weekPage.Items.Select(x => x.InteractionId).ToArray());
        Assert.DoesNotContain(weekPage.Items, x => x.InteractionId == day7Excluded.Id);

        // Invalid / duplicate due query param -> 400 validation_failed
        using var badDue = await h.Client.GetAsync($"{FollowUpsRoot}?due=tomorrow");
        await AssertProblemAsync(badDue, HttpStatusCode.BadRequest, "validation_failed");
        using var dupDue = await h.Client.GetAsync($"{FollowUpsRoot}?due=today&due=week");
        await AssertProblemAsync(dupDue, HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task ErasedAndMergedCustomersRejectInteractionMutationsWith409WhileDoNotContactPreservesStatusAndReadsSucceed()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero));
        await using var h = await P009Harness.CreateAsync(fixture, services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(clock);
        });
        await h.LoginAsync();

        var erased = await CreateCustomerAsync(h, new { fullName = "Khách Erased", phone = "0905111015" });
        var target = await CreateCustomerAsync(h, new { fullName = "Khách Đích Merge", phone = "0905111016" });
        var merged = await CreateCustomerAsync(h, new { fullName = "Khách Merged", phone = "0905111017" });
        var dnc = await CreateCustomerAsync(h, new { fullName = "Khách DNC Service", phone = "0905111018", contactStatus = "do_not_contact" });

        var erasedInteraction = await CreateInteractionAsync(h, erased.Id, new
        {
            channel = "phone",
            kind = "inquiry",
            content = "[Đã xóa]",
            occurredAt = "2026-11-24T08:00:00Z",
            followUpAt = "2026-11-25T12:00:00Z"
        });
        var mergedInteraction = await CreateInteractionAsync(h, merged.Id, new
        {
            channel = "phone",
            kind = "inquiry",
            content = "Tương tác trước gộp",
            occurredAt = "2026-11-24T08:00:00Z",
            followUpAt = "2026-11-25T12:00:00Z"
        });

        await using (var db = h.OwnerContext())
        {
            var eRow = await db.Set<Customer>().SingleAsync(x => x.Id == erased.Id);
            eRow.ContactStatus = "erased";
            eRow.ErasedAt = clock.UtcNow.UtcDateTime;
            var mRow = await db.Set<Customer>().SingleAsync(x => x.Id == merged.Id);
            mRow.MergedIntoCustomerId = target.Id;
            mRow.PhoneE164 = null;
            await db.SaveChangesAsync();
        }

        // Erased customer: GET -> 200, POST/PATCH/complete-follow-up -> 409 customer_erased
        var erasedRead = await h.Client.GetAsync($"{CustomersRoot}/{erased.Id}/interactions");
        Assert.Equal(HttpStatusCode.OK, erasedRead.StatusCode);
        using (var r = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{erased.Id}/interactions", new { channel = "phone", kind = "note", content = "Cấm" }))
            await AssertProblemAsync(r, HttpStatusCode.Conflict, "customer_erased");
        using (var r = await h.MutateAsync(HttpMethod.Patch, $"{CustomersRoot}/{erased.Id}/interactions/{erasedInteraction.Id}", new { content = "Cấm sửa" }))
            await AssertProblemAsync(r, HttpStatusCode.Conflict, "customer_erased");
        using (var r = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{erased.Id}/interactions/{erasedInteraction.Id}/complete-follow-up", new { result = "Cấm đóng" }))
            await AssertProblemAsync(r, HttpStatusCode.Conflict, "customer_erased");

        // Merged customer: GET -> 200, POST/PATCH/complete-follow-up -> 409 customer_merged
        var mergedRead = await h.Client.GetAsync($"{CustomersRoot}/{merged.Id}/interactions");
        Assert.Equal(HttpStatusCode.OK, mergedRead.StatusCode);
        using (var r = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{merged.Id}/interactions", new { channel = "phone", kind = "note", content = "Cấm" }))
            await AssertProblemAsync(r, HttpStatusCode.Conflict, "customer_merged");
        using (var r = await h.MutateAsync(HttpMethod.Patch, $"{CustomersRoot}/{merged.Id}/interactions/{mergedInteraction.Id}", new { content = "Cấm sửa" }))
            await AssertProblemAsync(r, HttpStatusCode.Conflict, "customer_merged");
        using (var r = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{merged.Id}/interactions/{mergedInteraction.Id}/complete-follow-up", new { result = "Cấm đóng" }))
            await AssertProblemAsync(r, HttpStatusCode.Conflict, "customer_merged");

        // do_not_contact customer: service POST, PATCH, complete-follow-up ALL succeed AND contact_status remains strictly "do_not_contact" in DB!
        var dncCreated = await CreateInteractionAsync(h, dnc.Id, new
        {
            channel = "phone",
            kind = "complaint",
            content = "Khách phản ánh lỗi vận chuyển",
            occurredAt = "2026-11-25T08:00:00Z",
            followUpAt = "2026-11-25T14:00:00Z"
        });
        using (var patchDnc = await h.MutateAsync(HttpMethod.Patch, $"{CustomersRoot}/{dnc.Id}/interactions/{dncCreated.Id}", new
        {
            content = "Đã xác nhận đổi nắp ấm"
        }))
            Assert.Equal(HttpStatusCode.OK, patchDnc.StatusCode);

        using (var completeDnc = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{dnc.Id}/interactions/{dncCreated.Id}/complete-follow-up", new
        {
            result = "Đã gửi nắp thay thế, khách hài lòng"
        }))
            Assert.Equal(HttpStatusCode.OK, completeDnc.StatusCode);

        await using (var db = h.OwnerContext())
        {
            var persistedDnc = await db.Set<Customer>().AsNoTracking().SingleAsync(x => x.Id == dnc.Id);
            Assert.Equal("do_not_contact", persistedDnc.ContactStatus);
            Assert.Equal(new DateTime(2026, 11, 25, 8, 0, 0, DateTimeKind.Utc), persistedDnc.LastContactedAt);
        }
    }

    [Fact]
    public async Task InteractionCrossCustomerRouteMismatchReturns404WithoutSideEffects()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        var customerA = await CreateCustomerAsync(h, new { fullName = "Khách A", phone = "0905111019" });
        var customerB = await CreateCustomerAsync(h, new { fullName = "Khách B", phone = "0905111020" });

        var interactionB = await CreateInteractionAsync(h, customerB.Id, new
        {
            channel = "zalo_personal",
            kind = "consultation",
            content = "Bí mật Khách B",
            occurredAt = "2026-11-25T08:00:00Z",
            followUpAt = "2026-11-25T12:00:00Z"
        });

        using (var badPatch = await h.MutateAsync(HttpMethod.Patch, $"{CustomersRoot}/{customerA.Id}/interactions/{interactionB.Id}", new { content = "Sửa chéo" }))
            await AssertProblemAsync(badPatch, HttpStatusCode.NotFound, "not_found");

        using (var badComplete = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{customerA.Id}/interactions/{interactionB.Id}/complete-follow-up", new { result = "Đóng chéo" }))
            await AssertProblemAsync(badComplete, HttpStatusCode.NotFound, "not_found");

        await using var db = h.OwnerContext();
        var untouched = await db.Set<Interaction>().AsNoTracking().SingleAsync(x => x.Id == interactionB.Id);
        Assert.Equal("Bí mật Khách B", untouched.Content);
        Assert.Null(untouched.FollowUpDoneAt);
        Assert.Null(untouched.FollowUpResult);
    }

    [Fact]
    public async Task AcP209ContentRoleUnauthenticatedUnreadyHttpMissingCsrfAndUnsupportedMediaTypeAreRejectedAcrossAllEndpoints()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        var customer = await CreateCustomerAsync(h, new { fullName = "Khách Security", phone = "0905111021" });
        var interaction = await CreateInteractionAsync(h, customer.Id, new
        {
            channel = "phone",
            kind = "consultation",
            content = "Tư vấn",
            occurredAt = "2026-11-25T08:00:00Z",
            followUpAt = "2026-11-25T12:00:00Z"
        });

        var contentEmail = await CreateStaffAsync(h, "content", ready: true);
        var unreadySalesEmail = await CreateStaffAsync(h, "sales", ready: false);

        var listUrl = $"{CustomersRoot}/{customer.Id}/interactions";
        var itemUrl = $"{listUrl}/{interaction.Id}";
        var completeUrl = $"{itemUrl}/complete-follow-up";

        // 1. content role -> 403 on all 5 endpoints (AC-P2-09)
        using (var contentClient = h.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false }))
        {
            await LoginClientAsync(contentClient, contentEmail, StaffNewPassword);
            var csrf = (await contentClient.GetFromJsonAsync<CsrfResponse>("/api/v1/admin/auth/csrf"))!.Token;

            Assert.Equal(HttpStatusCode.Forbidden, (await contentClient.GetAsync(listUrl)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await contentClient.GetAsync(FollowUpsRoot)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await SendWithCsrfAsync(contentClient, HttpMethod.Post, listUrl, new { channel = "phone", kind = "note", content = "x" }, csrf)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await SendWithCsrfAsync(contentClient, HttpMethod.Patch, itemUrl, new { content = "x" }, csrf)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await SendWithCsrfAsync(contentClient, HttpMethod.Post, completeUrl, new { result = "x" }, csrf)).StatusCode);
        }

        // 2. Unauthenticated -> 401 on all 5 endpoints
        using (var anonClient = h.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false }))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonClient.GetAsync(listUrl)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonClient.GetAsync(FollowUpsRoot)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonClient.PostAsJsonAsync(listUrl, new { channel = "phone", kind = "note", content = "x" })).StatusCode);
            using var patchReq = new HttpRequestMessage(HttpMethod.Patch, itemUrl) { Content = JsonContent.Create(new { content = "x" }) };
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonClient.SendAsync(patchReq)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonClient.PostAsJsonAsync(completeUrl, new { result = "x" })).StatusCode);
        }

        // 3. Unready sales (MustChangePassword = true) -> 403 on all 5 endpoints
        using (var unreadyClient = h.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false }))
        {
            await LoginClientAsync(unreadyClient, unreadySalesEmail, StaffInitialPassword);
            var csrf = (await unreadyClient.GetFromJsonAsync<CsrfResponse>("/api/v1/admin/auth/csrf"))!.Token;

            Assert.Equal(HttpStatusCode.Forbidden, (await unreadyClient.GetAsync(listUrl)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await unreadyClient.GetAsync(FollowUpsRoot)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await SendWithCsrfAsync(unreadyClient, HttpMethod.Post, listUrl, new { channel = "phone", kind = "note", content = "x" }, csrf)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await SendWithCsrfAsync(unreadyClient, HttpMethod.Patch, itemUrl, new { content = "x" }, csrf)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await SendWithCsrfAsync(unreadyClient, HttpMethod.Post, completeUrl, new { result = "x" }, csrf)).StatusCode);
        }

        // 4. Plain HTTP -> 400 https_required
        using (var httpClient = h.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("http://localhost"), AllowAutoRedirect = false }))
        {
            using var r1 = await httpClient.GetAsync(FollowUpsRoot);
            await AssertProblemAsync(r1, HttpStatusCode.BadRequest, "https_required");
            using var r2 = await httpClient.GetAsync(listUrl);
            await AssertProblemAsync(r2, HttpStatusCode.BadRequest, "https_required");
        }

        // 5. Missing/invalid CSRF on all 3 mutating endpoints -> 400 csrf_invalid
        using (var noCsrfPost = await h.Client.PostAsJsonAsync(listUrl, new { channel = "phone", kind = "note", content = "x" }))
            await AssertProblemAsync(noCsrfPost, HttpStatusCode.BadRequest, "csrf_invalid");

        using (var badCsrfPatchReq = new HttpRequestMessage(HttpMethod.Patch, itemUrl) { Content = JsonContent.Create(new { content = "x" }) })
        {
            badCsrfPatchReq.Headers.Add("X-CSRF-TOKEN", "bad-token");
            using var badCsrfPatch = await h.Client.SendAsync(badCsrfPatchReq);
            await AssertProblemAsync(badCsrfPatch, HttpStatusCode.BadRequest, "csrf_invalid");
        }

        using (var noCsrfComplete = await h.Client.PostAsJsonAsync(completeUrl, new { result = "x" }))
            await AssertProblemAsync(noCsrfComplete, HttpStatusCode.BadRequest, "csrf_invalid");

        // 6. Non-JSON Content-Type -> 415 unsupported_media_type on all 3 mutating endpoints
        var validCsrf = (await h.Client.GetFromJsonAsync<CsrfResponse>("/api/v1/admin/auth/csrf"))!.Token;
        foreach (var (method, url) in new[] { (HttpMethod.Post, listUrl), (HttpMethod.Patch, itemUrl), (HttpMethod.Post, completeUrl) })
        {
            using var req = new HttpRequestMessage(method, url) { Content = new StringContent("plain=text", Encoding.UTF8, "text/plain") };
            req.Headers.Add("X-CSRF-TOKEN", validCsrf);
            using var resp = await h.Client.SendAsync(req);
            await AssertProblemAsync(resp, HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
        }
    }

    [Fact]
    public async Task AuditLogsRedactInteractionContentAndFollowUpResultWithZeroRawPiiInDatabaseOrLogs()
    {
        var capturedLogs = new ConcurrentQueue<string>();
        await using var h = await P009Harness.CreateAsync(fixture, services =>
        {
            services.AddLogging(logging => logging.AddProvider(new CapturingLoggerProvider(capturedLogs)));
        });
        await h.LoginAsync();

        var customer = await CreateCustomerAsync(h, new { fullName = "Khách Kiểm Audit", phone = "0905111022" });
        var baseUrl = $"{CustomersRoot}/{customer.Id}/interactions";

        const string secretPerson = "Đặng Hiên";
        const string secretPhone = "0918765432";
        const string secretAddress = "123 Nguyễn Huệ";
        const string secretPatched = "SĐT phụ 0987000999";

        var created = await CreateInteractionAsync(h, customer.Id, new
        {
            channel = "zalo_personal",
            kind = "consultation",
            content = $"Khách {secretPerson} {secretPhone} hỏi ấm Chu Nê",
            occurredAt = "2026-11-25T08:00:00Z",
            followUpAt = "2026-11-25T12:00:00Z"
        });

        using (var patchResp = await h.MutateAsync(HttpMethod.Patch, $"{baseUrl}/{created.Id}", new
        {
            content = $"Cập nhật {secretPatched} cho {secretPerson}"
        }))
            Assert.Equal(HttpStatusCode.OK, patchResp.StatusCode);

        using (var completeResp = await h.MutateAsync(HttpMethod.Post, $"{baseUrl}/{created.Id}/complete-follow-up", new
        {
            result = $"Đã gọi số {secretPhone} hẹn giao tại {secretAddress}"
        }))
            Assert.Equal(HttpStatusCode.OK, completeResp.StatusCode);

        // Also trigger validation failure containing PII to confirm ProblemDetails + logs never echo PII
        using var badResp = await h.MutateAsync(HttpMethod.Post, baseUrl, new
        {
            channel = "invalid_channel",
            kind = "note",
            content = $"Lỗi {secretPerson} {secretPhone} {secretAddress}"
        });
        var badBody = await badResp.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, badResp.StatusCode);

        await using var db = h.OwnerContext();
        var interactionLogs = await db.AuditLogs.AsNoTracking()
            .Where(x => x.EntityType == "crm.interactions" && x.EntityId == created.Id)
            .OrderBy(x => x.OccurredAt)
            .ThenBy(x => x.Id)
            .ToArrayAsync();

        Assert.Equal(3, interactionLogs.Length);
        Assert.Equal("create", interactionLogs[0].Action);
        Assert.Equal("update", interactionLogs[1].Action);
        Assert.Equal("update", interactionLogs[2].Action);

        using (var createDoc = JsonDocument.Parse(interactionLogs[0].Changes))
        {
            Assert.Equal("[redacted]", createDoc.RootElement.GetProperty("content").GetProperty("after").GetString());
            Assert.Equal("zalo_personal", createDoc.RootElement.GetProperty("channel").GetProperty("after").GetString());
            Assert.Equal("consultation", createDoc.RootElement.GetProperty("kind").GetProperty("after").GetString());
            Assert.True(createDoc.RootElement.TryGetProperty("created_at", out _));
        }

        using (var patchDoc = JsonDocument.Parse(interactionLogs[1].Changes))
        {
            Assert.Equal("[redacted]", patchDoc.RootElement.GetProperty("content").GetProperty("before").GetString());
            Assert.Equal("[redacted]", patchDoc.RootElement.GetProperty("content").GetProperty("after").GetString());
        }

        using (var completeDoc = JsonDocument.Parse(interactionLogs[2].Changes))
        {
            Assert.Equal(JsonValueKind.Null, completeDoc.RootElement.GetProperty("follow_up_result").GetProperty("before").ValueKind);
            Assert.Equal("[redacted]", completeDoc.RootElement.GetProperty("follow_up_result").GetProperty("after").GetString());
            Assert.Equal(JsonValueKind.String, completeDoc.RootElement.GetProperty("follow_up_done_at").GetProperty("after").ValueKind);
        }

        var allAuditLogs = await db.AuditLogs.AsNoTracking().Select(x => x.Changes).ToArrayAsync();
        var combined = string.Join("\n", allAuditLogs.Concat(capturedLogs).Append(badBody));
        foreach (var needle in new[] { secretPerson, secretPhone, secretAddress, secretPatched })
        {
            Assert.DoesNotContain(needle, combined, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task RealTpAppDatabaseRoleExecutesAllInteractionAndFollowUpQueriesAndMutations()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero));
        await using var h = await P009Harness.CreateAsync(fixture, services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(clock);
        });
        await h.LoginAsync();

        // P009Harness executes API strictly under runtime tp_app PostgreSQL role
        Assert.Contains("Username=tp_app", h.RuntimeConnection, StringComparison.OrdinalIgnoreCase);

        var customer = await CreateCustomerAsync(h, new { fullName = "Khách TpApp Interaction", phone = "0905111023" });
        var orderId = await SeedOrderAsync(h, customer.Id, "TP261125TPAP");

        // 1. POST interaction with orderId and followUpAt
        var created = await CreateInteractionAsync(h, customer.Id, new
        {
            channel = "zalo_personal",
            kind = "consultation",
            content = "Tư vấn dưới role tp_app",
            occurredAt = "2026-11-25T08:00:00Z",
            orderId,
            followUpAt = "2026-11-25T12:00:00Z"
        });
        Assert.Equal("TP261125TPAP", created.OrderCode);

        // 2. GET follow-ups
        var followUps = (await h.Client.GetFromJsonAsync<CatalogPage<FollowUpItemView>>($"{FollowUpsRoot}?due=today", WebJson))!;
        var item = Assert.Single(followUps.Items, x => x.InteractionId == created.Id);
        Assert.Equal("TP261125TPAP", item.OrderCode);

        // 3. PATCH interaction
        using (var patchResp = await h.MutateAsync(HttpMethod.Patch, $"{CustomersRoot}/{customer.Id}/interactions/{created.Id}", new
        {
            kind = "quote",
            content = "Báo giá dưới role tp_app"
        }))
            Assert.Equal(HttpStatusCode.OK, patchResp.StatusCode);

        // 4. POST complete-follow-up
        using (var completeResp = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{customer.Id}/interactions/{created.Id}/complete-follow-up", new
        {
            result = "Hoàn tất dưới role tp_app"
        }))
            Assert.Equal(HttpStatusCode.OK, completeResp.StatusCode);

        // 5. GET customer interactions
        var listed = (await h.Client.GetFromJsonAsync<CatalogPage<CustomerInteractionView>>($"{CustomersRoot}/{customer.Id}/interactions", WebJson))!;
        var row = Assert.Single(listed.Items);
        Assert.Equal("quote", row.Kind);
        Assert.Equal("Hoàn tất dưới role tp_app", row.FollowUpResult);
    }

    [Fact]
    public async Task G4R0RegressionRejectsRfc2822TimestampsEmitsCleanMissingFieldCodesAndNormalizesSubMicrosecondSchedule()
    {
        var clock = new FixedClock(new DateTimeOffset(new DateTime(2026, 11, 25, 10, 0, 0, DateTimeKind.Utc).AddTicks(1_234_567), TimeSpan.Zero));
        await using var h = await P009Harness.CreateAsync(fixture, services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(clock);
        });
        await h.LoginAsync();

        var customer = await CreateCustomerAsync(h, new { fullName = "Khách R1 Regression", phone = "0905111024" });
        var baseUrl = $"{CustomersRoot}/{customer.Id}/interactions";

        // (a) RFC 2822 strings containing 'T' ("Tue", "Thu") are rejected with 400 validation_failed
        foreach (var rfc2822 in new[] { "Tue, 24 Nov 2026 10:00:00 +07:00", "Thu, 26 Nov 2026 10:00:00 +07:00" })
        {
            using var resp = await h.MutateAsync(HttpMethod.Post, baseUrl, new
            {
                channel = "phone",
                kind = "consultation",
                content = "Kiểm RFC 2822",
                occurredAt = rfc2822
            });
            var json = await AssertProblemAsync(resp, HttpStatusCode.BadRequest, "validation_failed");
            Assert.Contains("invalid_occurred_at", json.GetProperty("errors").GetProperty("occurredAt").EnumerateArray().Select(x => x.GetString()));
        }

        // (b) POST with {} emits ONLY clean machine codes (zero default English FluentValidation messages)
        using (var emptyResp = await SendRawJsonAsync(h.Client, HttpMethod.Post, baseUrl, "{}"))
        {
            var emptyJson = await AssertProblemAsync(emptyResp, HttpStatusCode.BadRequest, "validation_failed");
            var errors = emptyJson.GetProperty("errors");
            Assert.Equal(["invalid_channel"], errors.GetProperty("channel").EnumerateArray().Select(x => x.GetString()!).ToArray());
            Assert.Equal(["invalid_interaction_kind"], errors.GetProperty("kind").EnumerateArray().Select(x => x.GetString()!).ToArray());
            Assert.Equal(["invalid_content"], errors.GetProperty("content").EnumerateArray().Select(x => x.GetString()!).ToArray());
            Assert.DoesNotContain("must not be empty", emptyJson.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }

        // (c) Sub-microsecond occurredAt (.0000001Z) vs followUpAt (.0000002Z) -> identical PG microsecond -> 400 & 0 rows inserted
        using (var sameMicroResp = await h.MutateAsync(HttpMethod.Post, baseUrl, new
        {
            channel = "phone",
            kind = "consultation",
            content = "Cùng microsecond",
            occurredAt = "2026-11-25T09:00:00.0000001Z",
            followUpAt = "2026-11-25T09:00:00.0000002Z"
        }))
        {
            var json = await AssertProblemAsync(sameMicroResp, HttpStatusCode.BadRequest, "validation_failed");
            Assert.Contains("follow_up_before_or_equal_occurred_at",
                json.GetProperty("errors").GetProperty("followUpAt").EnumerateArray().Select(x => x.GetString()));
        }

        await using (var db = h.OwnerContext())
        {
            Assert.Equal(0, await db.Set<Interaction>().CountAsync(x => x.CustomerId == customer.Id));
        }

        // (d) Sub-microsecond occurredAt (.0000009Z) & followUpAt (.0000014Z) -> 201, exact PG microseconds, partial PATCH -> 200
        var expectedOccurred = new DateTime(2026, 11, 25, 9, 0, 0, DateTimeKind.Utc);
        var expectedFollowUp = expectedOccurred.AddMicroseconds(1);
        using var okResp = await h.MutateAsync(HttpMethod.Post, baseUrl, new
        {
            channel = "zalo_personal",
            kind = "consultation",
            content = "Khác microsecond",
            occurredAt = "2026-11-25T09:00:00.0000009Z",
            followUpAt = "2026-11-25T09:00:00.0000014Z"
        });
        Assert.Equal(HttpStatusCode.Created, okResp.StatusCode);
        var created = (await okResp.Content.ReadFromJsonAsync<CustomerInteractionView>(WebJson))!;
        Assert.Equal(expectedOccurred, created.OccurredAt);
        Assert.Equal(expectedFollowUp, created.FollowUpAt);

        await using (var db = h.OwnerContext())
        {
            var persistedRow = await db.Set<Interaction>().AsNoTracking().SingleAsync(x => x.Id == created.Id);
            Assert.Equal(expectedOccurred, persistedRow.OccurredAt);
            Assert.Equal(expectedFollowUp, persistedRow.FollowUpAt);
            Assert.True(persistedRow.FollowUpAt > persistedRow.OccurredAt);

            var persistedCustomer = await db.Set<Customer>().AsNoTracking().SingleAsync(x => x.Id == customer.Id);
            Assert.Equal(expectedOccurred, persistedCustomer.LastContactedAt);
        }

        using (var patchContent = await h.MutateAsync(HttpMethod.Patch, $"{baseUrl}/{created.Id}", new
        {
            content = "Updated after DB reload"
        }))
        {
            Assert.Equal(HttpStatusCode.OK, patchContent.StatusCode);
            var patched = (await patchContent.Content.ReadFromJsonAsync<CustomerInteractionView>(WebJson))!;
            Assert.Equal("Updated after DB reload", patched.Content);
            Assert.Equal(expectedOccurred, patched.OccurredAt);
            Assert.Equal(expectedFollowUp, patched.FollowUpAt);
        }

        // PATCH followUpAt collapsing to the same microsecond as occurredAt is rejected
        using (var patchCollapse = await h.MutateAsync(HttpMethod.Patch, $"{baseUrl}/{created.Id}", new
        {
            followUpAt = "2026-11-25T09:00:00.0000009Z"
        }))
        {
            var json = await AssertProblemAsync(patchCollapse, HttpStatusCode.BadRequest, "validation_failed");
            Assert.Contains("follow_up_before_or_equal_occurred_at",
                json.GetProperty("errors").GetProperty("followUpAt").EnumerateArray().Select(x => x.GetString()));
        }

        // Strict future check still rejects rawOccurredAt == rawNow + 1 tick, while omitted occurredAt normalizes to PG microseconds
        using (var futureTickResp = await h.MutateAsync(HttpMethod.Post, baseUrl, new
        {
            channel = "phone",
            kind = "inquiry",
            content = "Tương lai +1 tick",
            occurredAt = "2026-11-25T10:00:00.1234568Z"
        }))
        {
            var json = await AssertProblemAsync(futureTickResp, HttpStatusCode.BadRequest, "validation_failed");
            Assert.Contains("occurred_at_in_future", json.GetProperty("errors").GetProperty("occurredAt").EnumerateArray().Select(x => x.GetString()));
        }

        var expectedDefaultOccurred = new DateTime(2026, 11, 25, 10, 0, 0, DateTimeKind.Utc).AddTicks(1_234_560);
        var defaultCreated = await CreateInteractionAsync(h, customer.Id, new
        {
            channel = "phone",
            kind = "inquiry",
            content = "Mặc định now chuẩn microsecond"
        });
        Assert.Equal(expectedDefaultOccurred, defaultCreated.OccurredAt);

        await using (var db = h.OwnerContext())
        {
            var persistedDefault = await db.Set<Interaction>().AsNoTracking().SingleAsync(x => x.Id == defaultCreated.Id);
            var persistedCustomer = await db.Set<Customer>().AsNoTracking().SingleAsync(x => x.Id == customer.Id);
            Assert.Equal(expectedDefaultOccurred, persistedDefault.OccurredAt);
            Assert.Equal(expectedDefaultOccurred, persistedCustomer.LastContactedAt);
        }
    }

    private static async Task<CustomerInteractionView> CreateInteractionAsync(P009Harness h, Guid customerId, object payload)
    {
        using var resp = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{customerId}/interactions", payload);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == HttpStatusCode.Created, $"Expected 201 Created, got {(int)resp.StatusCode}: {body}");
        return JsonSerializer.Deserialize<CustomerInteractionView>(body, WebJson)!;
    }

    private static async Task<HttpResponseMessage> SendRawJsonAsync(HttpClient client, HttpMethod method, string path, string rawJson)
    {
        var csrf = (await client.GetFromJsonAsync<CsrfResponse>("/api/v1/admin/auth/csrf"))!.Token;
        using var req = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(rawJson, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("X-CSRF-TOKEN", csrf);
        return await client.SendAsync(req);
    }

    private static async Task<HttpResponseMessage> SendWithCsrfAsync(HttpClient client, HttpMethod method, string path, object body, string csrf)
    {
        using var req = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        req.Headers.Add("X-CSRF-TOKEN", csrf);
        return await client.SendAsync(req);
    }

    private static async Task<Guid> SeedOrderAsync(P009Harness h, Guid customerId, string code)
    {
        var id = Guid.CreateVersion7();
        var now = new DateTime(2026, 11, 25, 6, 0, 0, DateTimeKind.Utc);
        await using var db = h.OwnerContext();
        db.Set<PersistenceOrder>().Add(new PersistenceOrder
        {
            Id = id,
            Code = code,
            Origin = "admin",
            SalesChannel = "zalo_personal",
            CustomerId = customerId,
            Status = "pending",
            PaymentMethod = "cod",
            PaymentStatus = "unpaid",
            ContactName = "Người Đặt",
            ContactPhone = "+84905111099",
            ReceiverName = "Người Nhận",
            ReceiverPhone = "+84905111099",
            ProvinceCode = "79",
            ProvinceName = "TP Hồ Chí Minh",
            WardName = "Phường Bến Nghé",
            AddressLine = "10 Lê Lợi",
            SubtotalVnd = 500_000,
            DiscountVnd = 0,
            ShippingFeeVnd = 0,
            TotalVnd = 500_000,
            AcquisitionSource = "direct",
            IsFirstPurchase = true,
            PlacedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
        return id;
    }

    private sealed class FixedClock(DateTimeOffset initial) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = initial;
    }

    private sealed class CapturingLoggerProvider(ConcurrentQueue<string> logs) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(logs);
        public void Dispose() { }
    }

    private sealed class CapturingLogger(ConcurrentQueue<string> logs) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            logs.Enqueue(formatter(state, exception));
            if (exception is not null) logs.Enqueue(exception.ToString());
        }
    }
}
