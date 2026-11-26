using Microsoft.EntityFrameworkCore;

namespace TeaPremium.Infrastructure.Persistence;

internal static class CrmSalesFinanceModel
{
    internal static void Configure(ModelBuilder builder)
    {
        builder.HasSequence<long>("customer_code_seq", "crm").StartsAt(1000);

        builder.Entity<Customer>(b =>
        {
            b.ToTable("customers", "crm", t =>
            {
                t.HasCheckConstraint("ck_customers_full_name", "length(trim(full_name)) >= 1");
                t.HasCheckConstraint("ck_customers_phone_e164", "phone_e164 ~ '^\\+84[0-9]{9}$'");
                t.HasCheckConstraint("ck_customers_email", "email IS NULL OR email ~ '^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$'");
                t.HasCheckConstraint("ck_customers_customer_group", "customer_group IN ('retail','wholesale','reseller')");
                t.HasCheckConstraint("ck_customers_origin", "origin IN ('legacy_import','web','admin')");
                t.HasCheckConstraint("ck_customers_acquisition_source", "acquisition_source IN ('unknown','legacy','google_organic','google_ads','facebook','zalo_personal','zalo_oa','referral','direct','offline','other')");
                t.HasCheckConstraint("ck_customers_contact_status", "contact_status IN ('active','do_not_contact','erased')");
                t.HasCheckConstraint("ck_customers_delivered_orders_count", "delivered_orders_count >= 0");
                t.HasCheckConstraint("ck_customers_lifetime_value_vnd", "lifetime_value_vnd >= 0");
                t.HasCheckConstraint("ck_customer_contact", "contact_status = 'erased' OR merged_into_customer_id IS NOT NULL OR phone_e164 IS NOT NULL OR zalo_name IS NOT NULL");
                t.HasCheckConstraint("ck_customer_not_self_ref", "referred_by_customer_id IS NULL OR referred_by_customer_id <> id");
            });
            b.HasKey(x => x.Id).HasName("pk_customers");
            b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.Code).HasColumnName("code").HasColumnType("text").IsRequired();
            b.Property(x => x.FullName).HasColumnName("full_name").HasColumnType("text").IsRequired();
            b.Property(x => x.PhoneE164).HasColumnName("phone_e164").HasColumnType("text").IsRequired(false);
            b.Property(x => x.Email).HasColumnName("email").HasColumnType("text").IsRequired(false);
            b.Property(x => x.ZaloName).HasColumnName("zalo_name").HasColumnType("text").IsRequired(false);
            b.Property(x => x.CustomerGroup).HasColumnName("customer_group").HasColumnType("text").IsRequired().HasDefaultValueSql("'retail'");
            b.Property(x => x.IsVip).HasColumnName("is_vip").HasColumnType("boolean").IsRequired().HasDefaultValueSql("false");
            b.Property(x => x.Origin).HasColumnName("origin").HasColumnType("text").IsRequired();
            b.Property(x => x.AcquisitionSource).HasColumnName("acquisition_source").HasColumnType("text").IsRequired().HasDefaultValueSql("'unknown'");
            b.Property(x => x.ReferredByCustomerId).HasColumnName("referred_by_customer_id").HasColumnType("uuid").IsRequired(false);
            b.Property(x => x.ContactStatus).HasColumnName("contact_status").HasColumnType("text").IsRequired().HasDefaultValueSql("'active'");
            b.Property(x => x.NoteSummary).HasColumnName("note_summary").HasColumnType("text").IsRequired(false);
            b.Property(x => x.HadLegacyPurchase).HasColumnName("had_legacy_purchase").HasColumnType("boolean").IsRequired().HasDefaultValueSql("false");
            b.Property(x => x.FirstDeliveredAt).HasColumnName("first_delivered_at").HasColumnType("timestamp with time zone").IsRequired(false);
            b.Property(x => x.LastDeliveredAt).HasColumnName("last_delivered_at").HasColumnType("timestamp with time zone").IsRequired(false);
            b.Property(x => x.DeliveredOrdersCount).HasColumnName("delivered_orders_count").HasColumnType("integer").IsRequired().HasDefaultValueSql("0");
            b.Property(x => x.LifetimeValueVnd).HasColumnName("lifetime_value_vnd").HasColumnType("bigint").IsRequired().HasDefaultValueSql("0");
            b.Property(x => x.LastContactedAt).HasColumnName("last_contacted_at").HasColumnType("timestamp with time zone").IsRequired(false);
            b.Property(x => x.SearchText).HasColumnName("search_text").HasColumnType("text").IsRequired().HasDefaultValueSql("''");
            b.Property(x => x.MergedIntoCustomerId).HasColumnName("merged_into_customer_id").HasColumnType("uuid").IsRequired(false);
            b.Property(x => x.ErasedAt).HasColumnName("erased_at").HasColumnType("timestamp with time zone").IsRequired(false);
            b.Property(x => x.CreatedBy).HasColumnName("created_by").HasColumnType("uuid").IsRequired(false);
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("now()");
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("now()");
            b.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            b.HasIndex(x => x.Code).IsUnique().HasDatabaseName("ux_customers_code").HasAnnotation("TeaPremium:UniqueConstraint", true);
            b.HasOne<Customer>().WithMany().HasForeignKey(x => x.ReferredByCustomerId).HasConstraintName("fk_customers_referred_by_customer_id").OnDelete(DeleteBehavior.NoAction);
            b.HasOne<Customer>().WithMany().HasForeignKey(x => x.MergedIntoCustomerId).HasConstraintName("fk_customers_merged_into_customer_id").OnDelete(DeleteBehavior.NoAction);
            b.HasIndex(x => x.PhoneE164).IsUnique().HasDatabaseName("ux_customers_phone").HasFilter("phone_e164 IS NOT NULL AND merged_into_customer_id IS NULL");
            b.HasIndex(x => x.SearchText).HasDatabaseName("ix_customers_search").HasMethod("gin").HasOperators("gin_trgm_ops");
            b.HasIndex(x => x.LastDeliveredAt).HasDatabaseName("ix_customers_last_delivered");
            b.HasIndex(x => x.CustomerGroup).HasDatabaseName("ix_customers_group");
        });

        builder.Entity<Tag>(b =>
        {
            b.ToTable("tags", "crm");
            b.HasKey(x => x.Id).HasName("pk_tags");
            b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.Name).HasColumnName("name").HasColumnType("text").IsRequired();
            b.Property(x => x.Color).HasColumnName("color").HasColumnType("text").IsRequired(false);
            b.HasIndex(x => x.Name).IsUnique().HasDatabaseName("ux_tags_name").HasAnnotation("TeaPremium:UniqueConstraint", true);
        });

        builder.Entity<CustomerTag>(b =>
        {
            b.ToTable("customer_tags", "crm");
            b.HasKey(x => new { x.CustomerId, x.TagId }).HasName("pk_customer_tags");
            b.Property(x => x.CustomerId).HasColumnName("customer_id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.TagId).HasColumnName("tag_id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).HasConstraintName("fk_customer_tags_customer_id").OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Tag>().WithMany().HasForeignKey(x => x.TagId).HasConstraintName("fk_customer_tags_tag_id").OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(x => x.TagId).HasDatabaseName("ix_customer_tags_tag");
        });

        builder.Entity<CustomerAddress>(b =>
        {
            b.ToTable("customer_addresses", "crm", t =>
            {
                t.HasCheckConstraint("ck_customer_addresses_receiver_phone", "receiver_phone ~ '^\\+84[0-9]{9}$'");
                t.HasCheckConstraint("ck_customer_addresses_row", "ward_code IS NOT NULL OR ward_name_text IS NOT NULL");
            });
            b.HasKey(x => x.Id).HasName("pk_customer_addresses");
            b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.CustomerId).HasColumnName("customer_id").HasColumnType("uuid").IsRequired();
            b.Property(x => x.Label).HasColumnName("label").HasColumnType("text").IsRequired(false);
            b.Property(x => x.ReceiverName).HasColumnName("receiver_name").HasColumnType("text").IsRequired();
            b.Property(x => x.ReceiverPhone).HasColumnName("receiver_phone").HasColumnType("text").IsRequired();
            b.Property(x => x.ProvinceCode).HasColumnName("province_code").HasColumnType("text").IsRequired();
            b.Property(x => x.WardCode).HasColumnName("ward_code").HasColumnType("text").IsRequired(false);
            b.Property(x => x.WardNameText).HasColumnName("ward_name_text").HasColumnType("text").IsRequired(false);
            b.Property(x => x.AddressLine).HasColumnName("address_line").HasColumnType("text").IsRequired();
            b.Property(x => x.IsDefault).HasColumnName("is_default").HasColumnType("boolean").IsRequired().HasDefaultValueSql("false");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("now()");
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("now()");
            b.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).HasConstraintName("fk_customer_addresses_customer_id").OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Province>().WithMany().HasForeignKey(x => x.ProvinceCode).HasConstraintName("fk_customer_addresses_province_code").OnDelete(DeleteBehavior.NoAction);
            b.HasOne<Ward>().WithMany().HasForeignKey(x => x.WardCode).HasConstraintName("fk_customer_addresses_ward_code").OnDelete(DeleteBehavior.NoAction);
            b.HasIndex(x => x.CustomerId).IsUnique().HasDatabaseName("ux_customer_default_address").HasFilter("is_default");
        });

        builder.Entity<CustomerAccount>(b =>
        {
            b.ToTable("customer_accounts", "crm", t =>
            {
                t.HasCheckConstraint("ck_customer_accounts_login_phone", "login_phone ~ '^\\+84[0-9]{9}$'");
                t.HasCheckConstraint("ck_customer_accounts_status", "status IN ('active','locked','disabled')");
                t.HasCheckConstraint("ck_account_login_phone", "status = 'disabled' OR login_phone IS NOT NULL");
            });
            b.HasKey(x => x.Id).HasName("pk_customer_accounts");
            b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.CustomerId).HasColumnName("customer_id").HasColumnType("uuid").IsRequired();
            b.Property(x => x.LoginPhone).HasColumnName("login_phone").HasColumnType("text").IsRequired(false);
            b.Property(x => x.PasswordHash).HasColumnName("password_hash").HasColumnType("text").IsRequired(false);
            b.Property(x => x.Email).HasColumnName("email").HasColumnType("text").IsRequired(false);
            b.Property(x => x.EmailVerifiedAt).HasColumnName("email_verified_at").HasColumnType("timestamp with time zone").IsRequired(false);
            b.Property(x => x.PhoneVerifiedAt).HasColumnName("phone_verified_at").HasColumnType("timestamp with time zone").IsRequired(false);
            b.Property(x => x.HistoryUnlockedAt).HasColumnName("history_unlocked_at").HasColumnType("timestamp with time zone").IsRequired(false);
            b.Property(x => x.Status).HasColumnName("status").HasColumnType("text").IsRequired().HasDefaultValueSql("'active'");
            b.Property(x => x.FailedLoginCount).HasColumnName("failed_login_count").HasColumnType("integer").IsRequired().HasDefaultValueSql("0");
            b.Property(x => x.LockoutEnd).HasColumnName("lockout_end").HasColumnType("timestamp with time zone").IsRequired(false);
            b.Property(x => x.SecurityStamp).HasColumnName("security_stamp").HasColumnType("text").IsRequired();
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("now()");
            b.Property(x => x.LastLoginAt).HasColumnName("last_login_at").HasColumnType("timestamp with time zone").IsRequired(false);
            b.HasIndex(x => x.CustomerId).IsUnique().HasDatabaseName("ux_customer_accounts_customer_id").HasAnnotation("TeaPremium:UniqueConstraint", true);
            b.HasIndex(x => x.LoginPhone).IsUnique().HasDatabaseName("ux_customer_accounts_login_phone").HasAnnotation("TeaPremium:UniqueConstraint", true);
            b.HasAnnotation("TeaPremium:NullableUniqueConstraint", "ux_customer_accounts_login_phone (login_phone)");
            b.HasAnnotation("TeaPremium:ExpressionIndex", "ux_account_email (lower(email)) WHERE email IS NOT NULL");
            b.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).HasConstraintName("fk_customer_accounts_customer_id").OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<CustomerLogin>(b =>
        {
            b.ToTable("customer_logins", "crm", t =>
            {
                t.HasCheckConstraint("ck_customer_logins_provider", "provider IN ('google','zalo')");
            });
            b.HasKey(x => new { x.Provider, x.ProviderKey }).HasName("pk_customer_logins");
            b.Property(x => x.Provider).HasColumnName("provider").HasColumnType("text").IsRequired();
            b.Property(x => x.ProviderKey).HasColumnName("provider_key").HasColumnType("text").IsRequired();
            b.Property(x => x.AccountId).HasColumnName("account_id").HasColumnType("uuid").IsRequired();
            b.Property(x => x.Email).HasColumnName("email").HasColumnType("text").IsRequired(false);
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("now()");
            b.HasOne<CustomerAccount>().WithMany().HasForeignKey(x => x.AccountId).HasConstraintName("fk_customer_logins_account_id").OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Interaction>(b =>
        {
            b.ToTable("interactions", "crm", t =>
            {
                t.HasCheckConstraint("ck_interactions_channel", "channel IN ('zalo_personal','zalo_oa','phone','facebook','in_person','email','web','other')");
                t.HasCheckConstraint("ck_interactions_kind", "kind IN ('inquiry','consultation','quote','follow_up','complaint','note','campaign_message')");
                t.HasCheckConstraint("ck_interactions_content", "length(content) BETWEEN 1 AND 4000");
                t.HasCheckConstraint("ck_interactions_row", "follow_up_done_at IS NULL OR follow_up_at IS NOT NULL");
            });
            b.HasKey(x => x.Id).HasName("pk_interactions");
            b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.CustomerId).HasColumnName("customer_id").HasColumnType("uuid").IsRequired();
            b.Property(x => x.StaffId).HasColumnName("staff_id").HasColumnType("uuid").IsRequired();
            b.Property(x => x.Channel).HasColumnName("channel").HasColumnType("text").IsRequired();
            b.Property(x => x.Kind).HasColumnName("kind").HasColumnType("text").IsRequired();
            b.Property(x => x.Content).HasColumnName("content").HasColumnType("text").IsRequired();
            b.Property(x => x.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamp with time zone").IsRequired();
            b.Property(x => x.OrderId).HasColumnName("order_id").HasColumnType("uuid").IsRequired(false);
            b.Property(x => x.CampaignId).HasColumnName("campaign_id").HasColumnType("uuid").IsRequired(false);
            b.Property(x => x.FollowUpAt).HasColumnName("follow_up_at").HasColumnType("timestamp with time zone").IsRequired(false);
            b.Property(x => x.FollowUpDoneAt).HasColumnName("follow_up_done_at").HasColumnType("timestamp with time zone").IsRequired(false);
            b.Property(x => x.FollowUpResult).HasColumnName("follow_up_result").HasColumnType("text").IsRequired(false);
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("now()");
            b.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).HasConstraintName("fk_interactions_customer_id").OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).HasConstraintName("fk_interactions_order").OnDelete(DeleteBehavior.SetNull);
            b.HasIndex(x => new { x.CustomerId, x.OccurredAt }).HasDatabaseName("ix_interactions_customer").IsDescending(false, true);
            b.HasIndex(x => x.FollowUpAt).HasDatabaseName("ix_interactions_follow_up").HasFilter("follow_up_at IS NOT NULL AND follow_up_done_at IS NULL");
        });

        builder.Entity<CustomerInterest>(b =>
        {
            b.ToTable("customer_interests", "crm", t =>
            {
                t.HasCheckConstraint("ck_customer_interests_interest_type", "interest_type IN ('material','artisan','product_type','category','price_band')");
                t.HasCheckConstraint("ck_customer_interests_price_band", "price_band IN ('under_1m','1m_3m','3m_10m','over_10m')");
                t.HasCheckConstraint("ck_customer_interests_source", "source IN ('order','manual')");
                t.HasCheckConstraint("ck_customer_interests_score", "score > 0");
                t.HasCheckConstraint("ck_customer_interests_price_band_match", "(interest_type = 'price_band') = (price_band IS NOT NULL)");
                t.HasCheckConstraint("ck_customer_interests_ref_id_match", "(interest_type = 'price_band') = (ref_id IS NULL)");
            });
            b.HasKey(x => x.Id).HasName("pk_customer_interests");
            b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.CustomerId).HasColumnName("customer_id").HasColumnType("uuid").IsRequired();
            b.Property(x => x.InterestType).HasColumnName("interest_type").HasColumnType("text").IsRequired();
            b.Property(x => x.RefId).HasColumnName("ref_id").HasColumnType("uuid").IsRequired(false);
            b.Property(x => x.PriceBand).HasColumnName("price_band").HasColumnType("text").IsRequired(false);
            b.Property(x => x.Source).HasColumnName("source").HasColumnType("text").IsRequired();
            b.Property(x => x.Score).HasColumnName("score").HasColumnType("integer").IsRequired().HasDefaultValueSql("1");
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("now()");
            b.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).HasConstraintName("fk_customer_interests_customer_id").OnDelete(DeleteBehavior.Cascade);
            b.HasAnnotation("TeaPremium:ExpressionIndex", "ux_customer_interest (customer_id, interest_type, coalesce(ref_id, '00000000-0000-0000-0000-000000000000'::uuid), coalesce(price_band, ''), source)");
            b.HasIndex(x => new { x.InterestType, x.RefId }).HasDatabaseName("ix_interest_lookup");
        });

        builder.Entity<Consent>(b =>
        {
            b.ToTable("consents", "crm", t =>
            {
                t.HasCheckConstraint("ck_consents_channel", "channel IN ('zalo_personal','zalo_oa','sms','email','phone')");
                t.HasCheckConstraint("ck_consents_purpose", "purpose IN ('marketing_new_products','marketing_promotions','customer_care')");
                t.HasCheckConstraint("ck_consents_status", "status IN ('granted','revoked')");
                t.HasCheckConstraint("ck_consents_source", "source IN ('checkout','account','staff','import','link')");
                t.HasCheckConstraint("ck_consents_evidence", "length(trim(evidence)) >= 3");
            });
            b.HasKey(x => x.Id).HasName("pk_consents");
            b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.CustomerId).HasColumnName("customer_id").HasColumnType("uuid").IsRequired();
            b.Property(x => x.Channel).HasColumnName("channel").HasColumnType("text").IsRequired();
            b.Property(x => x.Purpose).HasColumnName("purpose").HasColumnType("text").IsRequired();
            b.Property(x => x.Status).HasColumnName("status").HasColumnType("text").IsRequired();
            b.Property(x => x.Source).HasColumnName("source").HasColumnType("text").IsRequired();
            b.Property(x => x.Evidence).HasColumnName("evidence").HasColumnType("text").IsRequired();
            b.Property(x => x.CapturedAt).HasColumnName("captured_at").HasColumnType("timestamp with time zone").IsRequired();
            b.Property(x => x.CapturedBy).HasColumnName("captured_by").HasColumnType("uuid").IsRequired(false);
            b.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).HasConstraintName("fk_consents_customer_id").OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(x => new { x.CustomerId, x.Channel, x.Purpose }).IsUnique().HasDatabaseName("ux_consents_customer_id_channel_purpose").HasAnnotation("TeaPremium:UniqueConstraint", true);
        });

        builder.Entity<DataRequest>(b =>
        {
            b.ToTable("data_requests", "crm", t =>
            {
                t.HasCheckConstraint("ck_data_requests_request_type", "request_type IN ('export','erase','correct','withdraw_consent')");
                t.HasCheckConstraint("ck_data_requests_status", "status IN ('open','completed','rejected')");
            });
            b.HasKey(x => x.Id).HasName("pk_data_requests");
            b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.CustomerId).HasColumnName("customer_id").HasColumnType("uuid").IsRequired();
            b.Property(x => x.RequestType).HasColumnName("request_type").HasColumnType("text").IsRequired();
            b.Property(x => x.Status).HasColumnName("status").HasColumnType("text").IsRequired().HasDefaultValueSql("'open'");
            b.Property(x => x.Channel).HasColumnName("channel").HasColumnType("text").IsRequired();
            b.Property(x => x.Note).HasColumnName("note").HasColumnType("text").IsRequired(false);
            b.Property(x => x.RequestedAt).HasColumnName("requested_at").HasColumnType("timestamp with time zone").IsRequired();
            b.Property(x => x.CompletedAt).HasColumnName("completed_at").HasColumnType("timestamp with time zone").IsRequired(false);
            b.Property(x => x.HandledBy).HasColumnName("handled_by").HasColumnType("uuid").IsRequired(false);
            b.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).HasConstraintName("fk_data_requests_customer_id").OnDelete(DeleteBehavior.NoAction);
        });

        builder.Entity<Voucher>(b =>
        {
            b.ToTable("vouchers", "pricing", t =>
            {
                t.HasCheckConstraint("ck_vouchers_code", "code ~ '^[A-Z0-9]{4,20}$'");
                t.HasCheckConstraint("ck_vouchers_kind", "kind IN ('referral_reward','manual')");
                t.HasCheckConstraint("ck_vouchers_discount_type", "discount_type IN ('fixed','percent')");
                t.HasCheckConstraint("ck_vouchers_discount_value", "discount_value > 0");
                t.HasCheckConstraint("ck_vouchers_max_discount_vnd", "max_discount_vnd IS NULL OR max_discount_vnd > 0");
                t.HasCheckConstraint("ck_vouchers_min_order_vnd", "min_order_vnd >= 0");
                t.HasCheckConstraint("ck_vouchers_usage_limit", "usage_limit IS NULL OR usage_limit > 0");
                t.HasCheckConstraint("ck_vouchers_per_customer_limit", "per_customer_limit > 0");
                t.HasCheckConstraint("ck_vouchers_used_count", "used_count >= 0");
                t.HasCheckConstraint("ck_vouchers_status", "status IN ('active','disabled')");
                t.HasCheckConstraint("ck_vouchers_dates", "ends_at > starts_at");
                t.HasCheckConstraint("ck_vouchers_percent_bounds", "discount_type <> 'percent' OR discount_value BETWEEN 1 AND 50");
                t.HasCheckConstraint("ck_vouchers_usage_cap", "usage_limit IS NULL OR used_count <= usage_limit");
            });
            b.HasKey(x => x.Id).HasName("pk_vouchers");
            b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.Code).HasColumnName("code").HasColumnType("text").IsRequired();
            b.Property(x => x.Kind).HasColumnName("kind").HasColumnType("text").IsRequired();
            b.Property(x => x.DiscountType).HasColumnName("discount_type").HasColumnType("text").IsRequired();
            b.Property(x => x.DiscountValue).HasColumnName("discount_value").HasColumnType("bigint").IsRequired();
            b.Property(x => x.MaxDiscountVnd).HasColumnName("max_discount_vnd").HasColumnType("bigint").IsRequired(false);
            b.Property(x => x.MinOrderVnd).HasColumnName("min_order_vnd").HasColumnType("bigint").IsRequired().HasDefaultValueSql("0");
            b.Property(x => x.StartsAt).HasColumnName("starts_at").HasColumnType("timestamp with time zone").IsRequired();
            b.Property(x => x.EndsAt).HasColumnName("ends_at").HasColumnType("timestamp with time zone").IsRequired();
            b.Property(x => x.UsageLimit).HasColumnName("usage_limit").HasColumnType("integer").IsRequired(false);
            b.Property(x => x.PerCustomerLimit).HasColumnName("per_customer_limit").HasColumnType("integer").IsRequired().HasDefaultValueSql("1");
            b.Property(x => x.BoundCustomerId).HasColumnName("bound_customer_id").HasColumnType("uuid").IsRequired(false);
            b.Property(x => x.UsedCount).HasColumnName("used_count").HasColumnType("integer").IsRequired().HasDefaultValueSql("0");
            b.Property(x => x.Status).HasColumnName("status").HasColumnType("text").IsRequired().HasDefaultValueSql("'active'");
            b.Property(x => x.CreatedBy).HasColumnName("created_by").HasColumnType("uuid").IsRequired(false);
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("now()");
            b.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            b.HasIndex(x => x.Code).IsUnique().HasDatabaseName("ux_vouchers_code").HasAnnotation("TeaPremium:UniqueConstraint", true);
            b.HasOne<Customer>().WithMany().HasForeignKey(x => x.BoundCustomerId).HasConstraintName("fk_vouchers_customer").OnDelete(DeleteBehavior.NoAction);
        });

        builder.Entity<VoucherRedemption>(b =>
        {
            b.ToTable("voucher_redemptions", "pricing", t =>
            {
                t.HasCheckConstraint("ck_voucher_redemptions_amount_vnd", "amount_vnd > 0");
                t.HasCheckConstraint("ck_voucher_redemptions_status", "status IN ('applied','reverted')");
            });
            b.HasKey(x => x.Id).HasName("pk_voucher_redemptions");
            b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.VoucherId).HasColumnName("voucher_id").HasColumnType("uuid").IsRequired();
            b.Property(x => x.OrderId).HasColumnName("order_id").HasColumnType("uuid").IsRequired();
            b.Property(x => x.CustomerId).HasColumnName("customer_id").HasColumnType("uuid").IsRequired();
            b.Property(x => x.AmountVnd).HasColumnName("amount_vnd").HasColumnType("bigint").IsRequired();
            b.Property(x => x.Status).HasColumnName("status").HasColumnType("text").IsRequired().HasDefaultValueSql("'applied'");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("now()");
            b.HasIndex(x => x.OrderId).IsUnique().HasDatabaseName("ux_voucher_redemptions_order_id").HasAnnotation("TeaPremium:UniqueConstraint", true);
            b.HasOne<Voucher>().WithMany().HasForeignKey(x => x.VoucherId).HasConstraintName("fk_voucher_redemptions_voucher_id").OnDelete(DeleteBehavior.NoAction);
            b.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).HasConstraintName("fk_redemptions_order").OnDelete(DeleteBehavior.NoAction);
            b.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).HasConstraintName("fk_redemptions_customer").OnDelete(DeleteBehavior.NoAction);
            b.HasIndex(x => new { x.VoucherId, x.CustomerId }).HasDatabaseName("ix_redemptions_voucher_customer").HasFilter("status = 'applied'");
        });

        builder.Entity<Cart>(b =>
        {
            b.ToTable("carts", "sales");
            b.HasKey(x => x.Id).HasName("pk_carts");
            b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.TokenHash).HasColumnName("token_hash").HasColumnType("text").IsRequired();
            b.Property(x => x.CustomerAccountId).HasColumnName("customer_account_id").HasColumnType("uuid").IsRequired(false);
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("now()");
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("now()");
            b.Property(x => x.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamp with time zone").IsRequired();
            b.HasIndex(x => x.TokenHash).IsUnique().HasDatabaseName("ux_carts_token_hash").HasAnnotation("TeaPremium:UniqueConstraint", true);
            b.HasOne<CustomerAccount>().WithMany().HasForeignKey(x => x.CustomerAccountId).HasConstraintName("fk_carts_customer_account_id").OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(x => x.CustomerAccountId).IsUnique().HasDatabaseName("ux_cart_account").HasFilter("customer_account_id IS NOT NULL");
            b.HasIndex(x => x.ExpiresAt).HasDatabaseName("ix_cart_expires");
        });

        builder.Entity<CartItem>(b =>
        {
            b.ToTable("cart_items", "sales", t =>
            {
                t.HasCheckConstraint("ck_cart_items_quantity", "quantity BETWEEN 1 AND 99");
            });
            b.HasKey(x => new { x.CartId, x.VariantId }).HasName("pk_cart_items");
            b.Property(x => x.CartId).HasColumnName("cart_id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.VariantId).HasColumnName("variant_id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.Quantity).HasColumnName("quantity").HasColumnType("integer").IsRequired();
            b.Property(x => x.AddedAt).HasColumnName("added_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("now()");
            b.HasOne<Cart>().WithMany().HasForeignKey(x => x.CartId).HasConstraintName("fk_cart_items_cart_id").OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Variant>().WithMany().HasForeignKey(x => x.VariantId).HasConstraintName("fk_cart_items_variant_id").OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Order>(b =>
        {
            b.ToTable("orders", "sales", t =>
            {
                t.HasCheckConstraint("ck_orders_code", "code ~ '^TP[0-9]{6}[A-Z0-9]{4}$'");
                t.HasCheckConstraint("ck_orders_origin", "origin IN ('web','admin')");
                t.HasCheckConstraint("ck_orders_sales_channel", "sales_channel IN ('web','zalo_personal','zalo_oa','facebook','phone','in_person','other')");
                t.HasCheckConstraint("ck_orders_status", "status IN ('pending','confirmed','shipping','delivered','delivery_failed','cancelled')");
                t.HasCheckConstraint("ck_orders_payment_method", "payment_method IN ('cod','bank_transfer')");
                t.HasCheckConstraint("ck_orders_payment_status", "payment_status IN ('unpaid','partially_paid','paid','partially_refunded','refunded')");
                t.HasCheckConstraint("ck_orders_contact_phone", "contact_phone ~ '^\\+84[0-9]{9}$'");
                t.HasCheckConstraint("ck_orders_receiver_phone", "receiver_phone ~ '^\\+84[0-9]{9}$'");
                t.HasCheckConstraint("ck_orders_subtotal_vnd", "subtotal_vnd >= 0");
                t.HasCheckConstraint("ck_orders_discount_vnd", "discount_vnd >= 0");
                t.HasCheckConstraint("ck_orders_shipping_fee_vnd", "shipping_fee_vnd >= 0");
                t.HasCheckConstraint("ck_orders_total_vnd", "total_vnd >= 0");
                t.HasCheckConstraint("ck_order_total", "total_vnd = subtotal_vnd - discount_vnd + shipping_fee_vnd");
                t.HasCheckConstraint("ck_order_discount", "discount_vnd <= subtotal_vnd");
                t.HasCheckConstraint("ck_order_delivered", "(status = 'delivered') = (delivered_at IS NOT NULL)");
                t.HasCheckConstraint("ck_order_cancelled", "(status = 'cancelled') = (cancelled_at IS NOT NULL)");
                t.HasCheckConstraint("ck_order_web_channel", "origin <> 'web' OR sales_channel = 'web'");
                t.HasCheckConstraint("ck_order_due", "payment_method <> 'bank_transfer' OR origin <> 'web' OR payment_due_at IS NOT NULL");
            });
            b.HasKey(x => x.Id).HasName("pk_orders");
            b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.Code).HasColumnName("code").HasColumnType("text").IsRequired();
            b.Property(x => x.Origin).HasColumnName("origin").HasColumnType("text").IsRequired();
            b.Property(x => x.SalesChannel).HasColumnName("sales_channel").HasColumnType("text").IsRequired();
            b.Property(x => x.CustomerId).HasColumnName("customer_id").HasColumnType("uuid").IsRequired();
            b.Property(x => x.CustomerAccountId).HasColumnName("customer_account_id").HasColumnType("uuid").IsRequired(false);
            b.Property(x => x.Status).HasColumnName("status").HasColumnType("text").IsRequired();
            b.Property(x => x.PaymentMethod).HasColumnName("payment_method").HasColumnType("text").IsRequired();
            b.Property(x => x.PaymentStatus).HasColumnName("payment_status").HasColumnType("text").IsRequired().HasDefaultValueSql("'unpaid'");
            b.Property(x => x.PaymentDueAt).HasColumnName("payment_due_at").HasColumnType("timestamp with time zone").IsRequired(false);
            b.Property(x => x.ContactName).HasColumnName("contact_name").HasColumnType("text").IsRequired();
            b.Property(x => x.ContactPhone).HasColumnName("contact_phone").HasColumnType("text").IsRequired();
            b.Property(x => x.ContactEmail).HasColumnName("contact_email").HasColumnType("text").IsRequired(false);
            b.Property(x => x.ReceiverName).HasColumnName("receiver_name").HasColumnType("text").IsRequired();
            b.Property(x => x.ReceiverPhone).HasColumnName("receiver_phone").HasColumnType("text").IsRequired();
            b.Property(x => x.ProvinceCode).HasColumnName("province_code").HasColumnType("text").IsRequired();
            b.Property(x => x.ProvinceName).HasColumnName("province_name").HasColumnType("text").IsRequired();
            b.Property(x => x.WardCode).HasColumnName("ward_code").HasColumnType("text").IsRequired(false);
            b.Property(x => x.WardName).HasColumnName("ward_name").HasColumnType("text").IsRequired();
            b.Property(x => x.AddressLine).HasColumnName("address_line").HasColumnType("text").IsRequired();
            b.Property(x => x.SubtotalVnd).HasColumnName("subtotal_vnd").HasColumnType("bigint").IsRequired();
            b.Property(x => x.DiscountVnd).HasColumnName("discount_vnd").HasColumnType("bigint").IsRequired().HasDefaultValueSql("0");
            b.Property(x => x.ShippingFeeVnd).HasColumnName("shipping_fee_vnd").HasColumnType("bigint").IsRequired().HasDefaultValueSql("0");
            b.Property(x => x.TotalVnd).HasColumnName("total_vnd").HasColumnType("bigint").IsRequired();
            b.Property(x => x.DiscountNote).HasColumnName("discount_note").HasColumnType("text").IsRequired(false);
            b.Property(x => x.VoucherId).HasColumnName("voucher_id").HasColumnType("uuid").IsRequired(false);
            b.Property(x => x.ReferralCode).HasColumnName("referral_code").HasColumnType("text").IsRequired(false);
            b.Property(x => x.AcquisitionSource).HasColumnName("acquisition_source").HasColumnType("text").IsRequired();
            b.Property(x => x.IsFirstPurchase).HasColumnName("is_first_purchase").HasColumnType("boolean").IsRequired();
            b.Property(x => x.TrackingLinkId).HasColumnName("tracking_link_id").HasColumnType("uuid").IsRequired(false);
            b.Property(x => x.CampaignId).HasColumnName("campaign_id").HasColumnType("uuid").IsRequired(false);
            b.Property(x => x.Utm).HasColumnName("utm").HasColumnType("jsonb").IsRequired(false);
            b.Property(x => x.CustomerNote).HasColumnName("customer_note").HasColumnType("text").IsRequired(false);
            b.Property(x => x.InternalNote).HasColumnName("internal_note").HasColumnType("text").IsRequired(false);
            b.Property(x => x.StatusReason).HasColumnName("status_reason").HasColumnType("text").IsRequired(false);
            b.Property(x => x.PlacedAt).HasColumnName("placed_at").HasColumnType("timestamp with time zone").IsRequired();
            b.Property(x => x.ConfirmedAt).HasColumnName("confirmed_at").HasColumnType("timestamp with time zone").IsRequired(false);
            b.Property(x => x.ShippedAt).HasColumnName("shipped_at").HasColumnType("timestamp with time zone").IsRequired(false);
            b.Property(x => x.DeliveredAt).HasColumnName("delivered_at").HasColumnType("timestamp with time zone").IsRequired(false);
            b.Property(x => x.FailedAt).HasColumnName("failed_at").HasColumnType("timestamp with time zone").IsRequired(false);
            b.Property(x => x.CancelledAt).HasColumnName("cancelled_at").HasColumnType("timestamp with time zone").IsRequired(false);
            b.Property(x => x.InvoiceNumber).HasColumnName("invoice_number").HasColumnType("text").IsRequired(false);
            b.Property(x => x.InvoiceIssuedAt).HasColumnName("invoice_issued_at").HasColumnType("timestamp with time zone").IsRequired(false);
            b.Property(x => x.IsBackfilled).HasColumnName("is_backfilled").HasColumnType("boolean").IsRequired().HasDefaultValueSql("false");
            b.Property(x => x.CreatedByStaffId).HasColumnName("created_by_staff_id").HasColumnType("uuid").IsRequired(false);
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("now()");
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("now()");
            b.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            b.HasIndex(x => x.Code).IsUnique().HasDatabaseName("ux_orders_code").HasAnnotation("TeaPremium:UniqueConstraint", true);
            b.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).HasConstraintName("fk_orders_customer_id").OnDelete(DeleteBehavior.NoAction);
            b.HasOne<CustomerAccount>().WithMany().HasForeignKey(x => x.CustomerAccountId).HasConstraintName("fk_orders_customer_account_id").OnDelete(DeleteBehavior.NoAction);
            b.HasOne<Voucher>().WithMany().HasForeignKey(x => x.VoucherId).HasConstraintName("fk_orders_voucher_id").OnDelete(DeleteBehavior.NoAction);
            b.HasIndex(x => new { x.Status, x.PlacedAt }).HasDatabaseName("ix_orders_status_placed").IsDescending(false, true);
            b.HasIndex(x => new { x.CustomerId, x.PlacedAt }).HasDatabaseName("ix_orders_customer").IsDescending(false, true);
            b.HasIndex(x => x.DeliveredAt).HasDatabaseName("ix_orders_delivered").HasFilter("delivered_at IS NOT NULL");
            b.HasIndex(x => new { x.PaymentStatus, x.Status }).HasDatabaseName("ix_orders_payment");
            b.HasIndex(x => x.PaymentDueAt).HasDatabaseName("ix_orders_due").HasFilter("status = 'pending' AND payment_method = 'bank_transfer' AND payment_status = 'unpaid'");
            b.HasIndex(x => x.CampaignId).HasDatabaseName("ix_orders_campaign").HasFilter("campaign_id IS NOT NULL");
            b.HasIndex(x => x.ContactPhone).HasDatabaseName("ix_orders_contact_phone");
        });

        builder.Entity<OrderLine>(b =>
        {
            b.ToTable("order_lines", "sales", t =>
            {
                t.HasCheckConstraint("ck_order_lines_quantity", "quantity BETWEEN 1 AND 1000");
                t.HasCheckConstraint("ck_order_lines_list_price_vnd", "list_price_vnd >= 0");
                t.HasCheckConstraint("ck_order_lines_unit_price_vnd", "unit_price_vnd >= 0");
                t.HasCheckConstraint("ck_order_lines_price_source", "price_source IN ('list','sale','override')");
                t.HasCheckConstraint("ck_order_lines_unit_cost_vnd", "unit_cost_vnd IS NULL OR unit_cost_vnd >= 0");
                t.HasCheckConstraint("ck_line_total", "line_total_vnd = quantity * unit_price_vnd");
                t.HasCheckConstraint("ck_line_override", "price_source <> 'override' OR length(trim(coalesce(price_override_reason,''))) >= 3");
                t.HasCheckConstraint("ck_line_returned", "returned_quantity BETWEEN 0 AND quantity");
            });
            b.HasKey(x => x.Id).HasName("pk_order_lines");
            b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.OrderId).HasColumnName("order_id").HasColumnType("uuid").IsRequired();
            b.Property(x => x.VariantId).HasColumnName("variant_id").HasColumnType("uuid").IsRequired();
            b.Property(x => x.ProductId).HasColumnName("product_id").HasColumnType("uuid").IsRequired();
            b.Property(x => x.Sku).HasColumnName("sku").HasColumnType("text").IsRequired();
            b.Property(x => x.ProductName).HasColumnName("product_name").HasColumnType("text").IsRequired();
            b.Property(x => x.VariantName).HasColumnName("variant_name").HasColumnType("text").IsRequired(false);
            b.Property(x => x.Quantity).HasColumnName("quantity").HasColumnType("integer").IsRequired();
            b.Property(x => x.ListPriceVnd).HasColumnName("list_price_vnd").HasColumnType("bigint").IsRequired();
            b.Property(x => x.UnitPriceVnd).HasColumnName("unit_price_vnd").HasColumnType("bigint").IsRequired();
            b.Property(x => x.PriceSource).HasColumnName("price_source").HasColumnType("text").IsRequired();
            b.Property(x => x.PriceOverrideReason).HasColumnName("price_override_reason").HasColumnType("text").IsRequired(false);
            b.Property(x => x.LineTotalVnd).HasColumnName("line_total_vnd").HasColumnType("bigint").IsRequired();
            b.Property(x => x.UnitCostVnd).HasColumnName("unit_cost_vnd").HasColumnType("bigint").IsRequired(false);
            b.Property(x => x.ReturnedQuantity).HasColumnName("returned_quantity").HasColumnType("integer").IsRequired().HasDefaultValueSql("0");
            b.Property(x => x.ReviewRequestedAt).HasColumnName("review_requested_at").HasColumnType("timestamp with time zone").IsRequired(false);
            b.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).HasConstraintName("fk_order_lines_order_id").OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Variant>().WithMany().HasForeignKey(x => x.VariantId).HasConstraintName("fk_order_lines_variant_id").OnDelete(DeleteBehavior.NoAction);
            b.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).HasConstraintName("fk_order_lines_product_id").OnDelete(DeleteBehavior.NoAction);
            b.HasIndex(x => new { x.OrderId, x.VariantId }).IsUnique().HasDatabaseName("ux_order_lines_order_id_variant_id").HasAnnotation("TeaPremium:UniqueConstraint", true);
            b.HasIndex(x => x.VariantId).HasDatabaseName("ix_order_lines_variant");
            b.HasIndex(x => x.ProductId).HasDatabaseName("ix_order_lines_product");
        });

        builder.Entity<OrderStatusHistory>(b =>
        {
            b.ToTable("order_status_history", "sales", t =>
            {
                t.HasCheckConstraint("ck_order_status_history_actor_type", "actor_type IN ('staff','customer','system')");
            });
            b.HasKey(x => x.Id).HasName("pk_order_status_history");
            b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.OrderId).HasColumnName("order_id").HasColumnType("uuid").IsRequired();
            b.Property(x => x.FromStatus).HasColumnName("from_status").HasColumnType("text").IsRequired(false);
            b.Property(x => x.ToStatus).HasColumnName("to_status").HasColumnType("text").IsRequired();
            b.Property(x => x.ActorType).HasColumnName("actor_type").HasColumnType("text").IsRequired();
            b.Property(x => x.ActorId).HasColumnName("actor_id").HasColumnType("uuid").IsRequired(false);
            b.Property(x => x.Reason).HasColumnName("reason").HasColumnType("text").IsRequired(false);
            b.Property(x => x.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamp with time zone").IsRequired();
            b.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).HasConstraintName("fk_order_status_history_order_id").OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(x => new { x.OrderId, x.OccurredAt }).HasDatabaseName("ix_order_history");
        });

        builder.Entity<CodSettlement>(b =>
        {
            b.ToTable("cod_settlements", "sales", t =>
            {
                t.HasCheckConstraint("ck_cod_settlements_gross_cod_vnd", "gross_cod_vnd >= 0");
                t.HasCheckConstraint("ck_cod_settlements_fee_vnd", "fee_vnd >= 0");
                t.HasCheckConstraint("ck_cod_settlements_row", "net_received_vnd = gross_cod_vnd - fee_vnd");
            });
            b.HasKey(x => x.Id).HasName("pk_cod_settlements");
            b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.CarrierName).HasColumnName("carrier_name").HasColumnType("text").IsRequired();
            b.Property(x => x.Reference).HasColumnName("reference").HasColumnType("text").IsRequired(false);
            b.Property(x => x.SettledAt).HasColumnName("settled_at").HasColumnType("timestamp with time zone").IsRequired();
            b.Property(x => x.GrossCodVnd).HasColumnName("gross_cod_vnd").HasColumnType("bigint").IsRequired();
            b.Property(x => x.FeeVnd).HasColumnName("fee_vnd").HasColumnType("bigint").IsRequired().HasDefaultValueSql("0");
            b.Property(x => x.NetReceivedVnd).HasColumnName("net_received_vnd").HasColumnType("bigint").IsRequired();
            b.Property(x => x.Note).HasColumnName("note").HasColumnType("text").IsRequired(false);
            b.Property(x => x.CreatedBy).HasColumnName("created_by").HasColumnType("uuid").IsRequired();
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("now()");
        });

        builder.Entity<Payment>(b =>
        {
            b.ToTable("payments", "sales", t =>
            {
                t.HasCheckConstraint("ck_payments_method", "method IN ('bank_transfer','cod','cash')");
                t.HasCheckConstraint("ck_payments_provider", "provider IN ('manual','sepay')");
                t.HasCheckConstraint("ck_payments_amount_vnd", "amount_vnd > 0");
                t.HasCheckConstraint("ck_payments_status", "status IN ('awaiting_settlement','confirmed','voided')");
                t.HasCheckConstraint("ck_payments_confirmed", "(status = 'confirmed') = (confirmed_at IS NOT NULL)");
                t.HasCheckConstraint("ck_payments_awaiting_cod", "status <> 'awaiting_settlement' OR method = 'cod'");
                t.HasCheckConstraint("ck_payments_void_reason", "status <> 'voided' OR void_reason IS NOT NULL");
            });
            b.HasKey(x => x.Id).HasName("pk_payments");
            b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.OrderId).HasColumnName("order_id").HasColumnType("uuid").IsRequired();
            b.Property(x => x.Method).HasColumnName("method").HasColumnType("text").IsRequired();
            b.Property(x => x.Provider).HasColumnName("provider").HasColumnType("text").IsRequired().HasDefaultValueSql("'manual'");
            b.Property(x => x.AmountVnd).HasColumnName("amount_vnd").HasColumnType("bigint").IsRequired();
            b.Property(x => x.Status).HasColumnName("status").HasColumnType("text").IsRequired();
            b.Property(x => x.BankReference).HasColumnName("bank_reference").HasColumnType("text").IsRequired(false);
            b.Property(x => x.ReceivedAt).HasColumnName("received_at").HasColumnType("timestamp with time zone").IsRequired(false);
            b.Property(x => x.CodSettlementId).HasColumnName("cod_settlement_id").HasColumnType("uuid").IsRequired(false);
            b.Property(x => x.Note).HasColumnName("note").HasColumnType("text").IsRequired(false);
            b.Property(x => x.VoidReason).HasColumnName("void_reason").HasColumnType("text").IsRequired(false);
            b.Property(x => x.CreatedBy).HasColumnName("created_by").HasColumnType("uuid").IsRequired(false);
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("now()");
            b.Property(x => x.ConfirmedBy).HasColumnName("confirmed_by").HasColumnType("uuid").IsRequired(false);
            b.Property(x => x.ConfirmedAt).HasColumnName("confirmed_at").HasColumnType("timestamp with time zone").IsRequired(false);
            b.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).HasConstraintName("fk_payments_order_id").OnDelete(DeleteBehavior.Restrict);
            b.HasOne<CodSettlement>().WithMany().HasForeignKey(x => x.CodSettlementId).HasConstraintName("fk_payments_cod_settlement_id").OnDelete(DeleteBehavior.NoAction);
            b.HasIndex(x => x.OrderId).HasDatabaseName("ix_payments_order");
            b.HasIndex(x => x.CreatedAt).HasDatabaseName("ix_payments_awaiting").HasFilter("status = 'awaiting_settlement'");
            b.HasIndex(x => new { x.Provider, x.BankReference }).IsUnique().HasDatabaseName("ux_payments_bank_ref").HasFilter("bank_reference IS NOT NULL AND status <> 'voided'");
        });

        builder.Entity<ReturnRequest>(b =>
        {
            b.ToTable("return_requests", "sales", t =>
            {
                t.HasCheckConstraint("ck_return_requests_reason", "reason IN ('damaged_in_transit','defective','wrong_item')");
                t.HasCheckConstraint("ck_return_requests_status", "status IN ('requested','approved','rejected','resolved')");
                t.HasCheckConstraint("ck_return_requests_resolution", "resolution IN ('refund','replacement')");
                t.HasCheckConstraint("ck_return_requests_row", "status NOT IN ('approved','resolved') OR resolution IS NOT NULL");
            });
            b.HasKey(x => x.Id).HasName("pk_return_requests");
            b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.Code).HasColumnName("code").HasColumnType("text").IsRequired();
            b.Property(x => x.OrderId).HasColumnName("order_id").HasColumnType("uuid").IsRequired();
            b.Property(x => x.Reason).HasColumnName("reason").HasColumnType("text").IsRequired();
            b.Property(x => x.Description).HasColumnName("description").HasColumnType("text").IsRequired();
            b.Property(x => x.Status).HasColumnName("status").HasColumnType("text").IsRequired().HasDefaultValueSql("'requested'");
            b.Property(x => x.Resolution).HasColumnName("resolution").HasColumnType("text").IsRequired(false);
            b.Property(x => x.DecisionNote).HasColumnName("decision_note").HasColumnType("text").IsRequired(false);
            b.Property(x => x.RequestedAt).HasColumnName("requested_at").HasColumnType("timestamp with time zone").IsRequired();
            b.Property(x => x.DecidedBy).HasColumnName("decided_by").HasColumnType("uuid").IsRequired(false);
            b.Property(x => x.DecidedAt).HasColumnName("decided_at").HasColumnType("timestamp with time zone").IsRequired(false);
            b.Property(x => x.ResolvedAt).HasColumnName("resolved_at").HasColumnType("timestamp with time zone").IsRequired(false);
            b.Property(x => x.CreatedBy).HasColumnName("created_by").HasColumnType("uuid").IsRequired(false);
            b.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            b.HasIndex(x => x.Code).IsUnique().HasDatabaseName("ux_return_requests_code").HasAnnotation("TeaPremium:UniqueConstraint", true);
            b.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).HasConstraintName("fk_return_requests_order_id").OnDelete(DeleteBehavior.NoAction);
        });

        builder.Entity<ReturnLine>(b =>
        {
            b.ToTable("return_lines", "sales", t =>
            {
                t.HasCheckConstraint("ck_return_lines_quantity", "quantity > 0");
            });
            b.HasKey(x => x.Id).HasName("pk_return_lines");
            b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.ReturnRequestId).HasColumnName("return_request_id").HasColumnType("uuid").IsRequired();
            b.Property(x => x.OrderLineId).HasColumnName("order_line_id").HasColumnType("uuid").IsRequired();
            b.Property(x => x.Quantity).HasColumnName("quantity").HasColumnType("integer").IsRequired();
            b.Property(x => x.Restockable).HasColumnName("restockable").HasColumnType("boolean").IsRequired().HasDefaultValueSql("false");
            b.HasOne<ReturnRequest>().WithMany().HasForeignKey(x => x.ReturnRequestId).HasConstraintName("fk_return_lines_return_request_id").OnDelete(DeleteBehavior.Cascade);
            b.HasOne<OrderLine>().WithMany().HasForeignKey(x => x.OrderLineId).HasConstraintName("fk_return_lines_order_line_id").OnDelete(DeleteBehavior.NoAction);
            b.HasIndex(x => new { x.ReturnRequestId, x.OrderLineId }).IsUnique().HasDatabaseName("ux_return_lines_return_request_id_order_line_id").HasAnnotation("TeaPremium:UniqueConstraint", true);
        });

        builder.Entity<ReturnEvidence>(b =>
        {
            b.ToTable("return_evidence", "sales");
            b.HasKey(x => new { x.ReturnRequestId, x.AssetId }).HasName("pk_return_evidence");
            b.Property(x => x.ReturnRequestId).HasColumnName("return_request_id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.AssetId).HasColumnName("asset_id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.HasOne<ReturnRequest>().WithMany().HasForeignKey(x => x.ReturnRequestId).HasConstraintName("fk_return_evidence_return_request_id").OnDelete(DeleteBehavior.Cascade);
            b.HasOne<MediaAsset>().WithMany().HasForeignKey(x => x.AssetId).HasConstraintName("fk_return_evidence_asset_id").OnDelete(DeleteBehavior.NoAction);
        });

        builder.Entity<Refund>(b =>
        {
            b.ToTable("refunds", "sales", t =>
            {
                t.HasCheckConstraint("ck_refunds_kind", "kind IN ('overpayment','return','cancellation','adjustment')");
                t.HasCheckConstraint("ck_refunds_amount_vnd", "amount_vnd > 0");
                t.HasCheckConstraint("ck_refunds_shipping_amount_vnd", "shipping_amount_vnd >= 0 AND shipping_amount_vnd <= amount_vnd");
                t.HasCheckConstraint("ck_refunds_method", "method IN ('bank_transfer','cash')");
                t.HasCheckConstraint("ck_refunds_kind_return_request", "(kind = 'return') = (return_request_id IS NOT NULL)");
                t.HasCheckConstraint("ck_refunds_kind_shipping", "kind IN ('return','adjustment') OR shipping_amount_vnd = 0");
            });
            b.HasKey(x => x.Id).HasName("pk_refunds");
            b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.OrderId).HasColumnName("order_id").HasColumnType("uuid").IsRequired();
            b.Property(x => x.ReturnRequestId).HasColumnName("return_request_id").HasColumnType("uuid").IsRequired(false);
            b.Property(x => x.Kind).HasColumnName("kind").HasColumnType("text").IsRequired();
            b.Property(x => x.AmountVnd).HasColumnName("amount_vnd").HasColumnType("bigint").IsRequired();
            b.Property(x => x.ShippingAmountVnd).HasColumnName("shipping_amount_vnd").HasColumnType("bigint").IsRequired().HasDefaultValueSql("0");
            b.Property(x => x.Method).HasColumnName("method").HasColumnType("text").IsRequired();
            b.Property(x => x.BankReference).HasColumnName("bank_reference").HasColumnType("text").IsRequired(false);
            b.Property(x => x.Reason).HasColumnName("reason").HasColumnType("text").IsRequired();
            b.Property(x => x.RefundedAt).HasColumnName("refunded_at").HasColumnType("timestamp with time zone").IsRequired();
            b.Property(x => x.CreatedBy).HasColumnName("created_by").HasColumnType("uuid").IsRequired();
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("now()");
            b.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).HasConstraintName("fk_refunds_order_id").OnDelete(DeleteBehavior.NoAction);
            b.HasOne<ReturnRequest>().WithMany().HasForeignKey(x => x.ReturnRequestId).HasConstraintName("fk_refunds_return_request_id").OnDelete(DeleteBehavior.NoAction);
            b.HasIndex(x => x.OrderId).HasDatabaseName("ix_refunds_order");
            b.HasIndex(x => x.ReturnRequestId).IsUnique().HasDatabaseName("ux_refunds_return_request").HasFilter("return_request_id IS NOT NULL");
        });

        builder.Entity<Shipment>(b =>
        {
            b.ToTable("shipments", "sales", t =>
            {
                t.HasCheckConstraint("ck_shipments_purpose", "purpose IN ('order','replacement')");
                t.HasCheckConstraint("ck_shipments_provider", "provider IN ('manual','ghn','ghtk')");
                t.HasCheckConstraint("ck_shipments_status", "status IN ('created','picked_up','in_transit','delivered','failed','returned','cancelled')");
                t.HasCheckConstraint("ck_shipments_cod_amount_vnd", "cod_amount_vnd >= 0");
                t.HasCheckConstraint("ck_shipments_shipping_cost_vnd", "shipping_cost_vnd IS NULL OR shipping_cost_vnd >= 0");
                t.HasCheckConstraint("ck_shipments_return_cost_vnd", "return_cost_vnd IS NULL OR return_cost_vnd >= 0");
                t.HasCheckConstraint("ck_shipments_row", "purpose <> 'replacement' OR return_request_id IS NOT NULL");
            });
            b.HasKey(x => x.Id).HasName("pk_shipments");
            b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.OrderId).HasColumnName("order_id").HasColumnType("uuid").IsRequired();
            b.Property(x => x.Purpose).HasColumnName("purpose").HasColumnType("text").IsRequired().HasDefaultValueSql("'order'");
            b.Property(x => x.ReturnRequestId).HasColumnName("return_request_id").HasColumnType("uuid").IsRequired(false);
            b.Property(x => x.Provider).HasColumnName("provider").HasColumnType("text").IsRequired().HasDefaultValueSql("'manual'");
            b.Property(x => x.CarrierName).HasColumnName("carrier_name").HasColumnType("text").IsRequired();
            b.Property(x => x.TrackingCode).HasColumnName("tracking_code").HasColumnType("text").IsRequired(false);
            b.Property(x => x.Status).HasColumnName("status").HasColumnType("text").IsRequired().HasDefaultValueSql("'created'");
            b.Property(x => x.CodAmountVnd).HasColumnName("cod_amount_vnd").HasColumnType("bigint").IsRequired().HasDefaultValueSql("0");
            b.Property(x => x.ShippingCostVnd).HasColumnName("shipping_cost_vnd").HasColumnType("bigint").IsRequired(false);
            b.Property(x => x.ReturnCostVnd).HasColumnName("return_cost_vnd").HasColumnType("bigint").IsRequired(false);
            b.Property(x => x.Note).HasColumnName("note").HasColumnType("text").IsRequired(false);
            b.Property(x => x.CreatedBy).HasColumnName("created_by").HasColumnType("uuid").IsRequired(false);
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("now()");
            b.Property(x => x.PickedUpAt).HasColumnName("picked_up_at").HasColumnType("timestamp with time zone").IsRequired(false);
            b.Property(x => x.DeliveredAt).HasColumnName("delivered_at").HasColumnType("timestamp with time zone").IsRequired(false);
            b.Property(x => x.FailedAt).HasColumnName("failed_at").HasColumnType("timestamp with time zone").IsRequired(false);
            b.Property(x => x.ReturnedAt).HasColumnName("returned_at").HasColumnType("timestamp with time zone").IsRequired(false);
            b.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).HasConstraintName("fk_shipments_order_id").OnDelete(DeleteBehavior.NoAction);
            b.HasOne<ReturnRequest>().WithMany().HasForeignKey(x => x.ReturnRequestId).HasConstraintName("fk_shipments_return_request_id").OnDelete(DeleteBehavior.NoAction);
            b.HasIndex(x => x.OrderId).IsUnique().HasDatabaseName("ux_shipment_active_order").HasFilter("purpose = 'order' AND status NOT IN ('cancelled','failed','returned')");
            b.HasIndex(x => new { x.CarrierName, x.TrackingCode }).IsUnique().HasDatabaseName("ux_shipment_tracking").HasFilter("tracking_code IS NOT NULL");
        });

        builder.Entity<ShipmentEvent>(b =>
        {
            b.ToTable("shipment_events", "sales", t =>
            {
                t.HasCheckConstraint("ck_shipment_events_source", "source IN ('manual','webhook')");
            });
            b.HasKey(x => x.Id).HasName("pk_shipment_events");
            b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.ShipmentId).HasColumnName("shipment_id").HasColumnType("uuid").IsRequired();
            b.Property(x => x.Status).HasColumnName("status").HasColumnType("text").IsRequired();
            b.Property(x => x.Note).HasColumnName("note").HasColumnType("text").IsRequired(false);
            b.Property(x => x.Source).HasColumnName("source").HasColumnType("text").IsRequired();
            b.Property(x => x.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamp with time zone").IsRequired();
            b.Property(x => x.CreatedBy).HasColumnName("created_by").HasColumnType("uuid").IsRequired(false);
            b.HasOne<Shipment>().WithMany().HasForeignKey(x => x.ShipmentId).HasConstraintName("fk_shipment_events_shipment_id").OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Expense>(b =>
        {
            b.ToTable("expenses", "finance", t =>
            {
                t.HasCheckConstraint("ck_expenses_category", "category IN ('marketing','fixed','packaging','shipping_other','payment_fee','damage_other','other')");
                t.HasCheckConstraint("ck_expenses_amount_vnd", "amount_vnd > 0");
                t.HasCheckConstraint("ck_expenses_marketing_channel", "category <> 'marketing' OR channel IS NOT NULL");
                t.HasCheckConstraint("ck_expenses_fixed_first_day", "category <> 'fixed' OR extract(day FROM incurred_on) = 1");
            });
            b.HasKey(x => x.Id).HasName("pk_expenses");
            b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.Category).HasColumnName("category").HasColumnType("text").IsRequired();
            b.Property(x => x.Subcategory).HasColumnName("subcategory").HasColumnType("text").IsRequired(false);
            b.Property(x => x.AmountVnd).HasColumnName("amount_vnd").HasColumnType("bigint").IsRequired();
            b.Property(x => x.IncurredOn).HasColumnName("incurred_on").HasColumnType("date").IsRequired();
            b.Property(x => x.Channel).HasColumnName("channel").HasColumnType("text").IsRequired(false);
            b.Property(x => x.CampaignId).HasColumnName("campaign_id").HasColumnType("uuid").IsRequired(false);
            b.Property(x => x.Description).HasColumnName("description").HasColumnType("text").IsRequired();
            b.Property(x => x.CreatedBy).HasColumnName("created_by").HasColumnType("uuid").IsRequired();
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("now()");
            b.HasIndex(x => new { x.IncurredOn, x.Category }).HasDatabaseName("ix_expenses_date");
        });
    }
}
