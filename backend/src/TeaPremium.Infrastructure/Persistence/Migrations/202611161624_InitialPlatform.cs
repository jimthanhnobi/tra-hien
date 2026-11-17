using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TeaPremium.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1861", Justification = "Generated EF migration arrays are used once.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1825", Justification = "Generated EF migration syntax.")]
    public partial class InitialPlatform : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "platform");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:btree_gist", ",,")
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .Annotation("Npgsql:PostgresExtension:unaccent", ",,");

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION public.f_unaccent(text) RETURNS text
                  LANGUAGE sql IMMUTABLE PARALLEL SAFE STRICT
                  AS $$ SELECT lower(public.unaccent('public.unaccent'::regdictionary, $1)) $$;
                """);

            migrationBuilder.CreateTable(
                name: "audit_logs",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    actor_type = table.Column<string>(type: "text", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    entity_type = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    entity_key = table.Column<string>(type: "text", nullable: true),
                    action = table.Column<string>(type: "text", nullable: false),
                    changes = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    correlation_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_logs", x => x.id);
                    table.CheckConstraint("ck_audit_entity_identity", "num_nonnulls(entity_id, entity_key) = 1 AND (entity_key IS NULL OR entity_key <> '')");
                    table.CheckConstraint("ck_audit_logs_action", "action IN ('create','update','delete','state_change','access')");
                    table.CheckConstraint("ck_audit_logs_actor_type", "actor_type IN ('staff','customer','system')");
                });

            migrationBuilder.CreateTable(
                name: "data_protection_keys",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.SerialColumn),
                    friendly_name = table.Column<string>(type: "text", nullable: true),
                    xml = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_protection_keys", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "idempotency_keys",
                schema: "platform",
                columns: table => new
                {
                    scope = table.Column<string>(type: "text", nullable: false),
                    key = table.Column<Guid>(type: "uuid", nullable: false),
                    request_hash = table.Column<string>(type: "text", nullable: false),
                    response_status = table.Column<int>(type: "integer", nullable: true),
                    response_body = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_keys", x => new { x.scope, x.key });
                });

            migrationBuilder.CreateTable(
                name: "inbound_webhooks",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "text", nullable: false),
                    external_id = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    received_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "received"),
                    processed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbound_webhooks", x => x.id);
                    table.UniqueConstraint("ux_inbound_webhooks_provider_external_id", x => new { x.provider, x.external_id });
                    table.CheckConstraint("ck_inbound_webhooks_status", "status IN ('received','processed','ignored','failed')");
                });

            migrationBuilder.CreateTable(
                name: "notification_logs",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "text", nullable: false),
                    template = table.Column<string>(type: "text", nullable: false),
                    recipient_hash = table.Column<string>(type: "text", nullable: false),
                    ref_type = table.Column<string>(type: "text", nullable: true),
                    ref_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    sent_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_logs", x => x.id);
                    table.CheckConstraint("ck_notification_logs_channel", "channel IN ('email','telegram','sms','zns')");
                    table.CheckConstraint("ck_notification_logs_status", "status IN ('queued','sent','failed')");
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    available_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "pending"),
                    processed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                    table.CheckConstraint("ck_outbox_messages_status", "status IN ('pending','processed','dead')");
                });

            migrationBuilder.CreateTable(
                name: "provinces",
                schema: "platform",
                columns: table => new
                {
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_provinces", x => x.code);
                });

            migrationBuilder.CreateTable(
                name: "settings",
                schema: "platform",
                columns: table => new
                {
                    key = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "jsonb", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_settings", x => x.key);
                    table.CheckConstraint("ck_settings_key_format", "key ~ '^[a-z0-9_.]{3,80}$'");
                });

            migrationBuilder.CreateTable(
                name: "wards",
                schema: "platform",
                columns: table => new
                {
                    code = table.Column<string>(type: "text", nullable: false),
                    province_code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_wards", x => x.code);
                    table.ForeignKey(
                        name: "fk_wards_provinces_province_code",
                        column: x => x.province_code,
                        principalSchema: "platform",
                        principalTable: "provinces",
                        principalColumn: "code");
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_actor",
                schema: "platform",
                table: "audit_logs",
                columns: new[] { "actor_id", "occurred_at" },
                descending: new[] { false, true },
                filter: "actor_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_audit_entity_id",
                schema: "platform",
                table: "audit_logs",
                columns: new[] { "entity_type", "entity_id", "occurred_at" },
                descending: new[] { false, false, true },
                filter: "entity_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_audit_entity_key",
                schema: "platform",
                table: "audit_logs",
                columns: new[] { "entity_type", "entity_key", "occurred_at" },
                descending: new[] { false, false, true },
                filter: "entity_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_audit_time",
                schema: "platform",
                table: "audit_logs",
                column: "occurred_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_idem_created",
                schema: "platform",
                table: "idempotency_keys",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_notif_ref",
                schema: "platform",
                table: "notification_logs",
                columns: new[] { "ref_type", "ref_id" });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_pending",
                schema: "platform",
                table: "outbox_messages",
                column: "available_at",
                filter: "status = 'pending'");

            migrationBuilder.CreateIndex(
                name: "ix_wards_province",
                schema: "platform",
                table: "wards",
                column: "province_code");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS public.f_unaccent(text);");

            migrationBuilder.DropTable(
                name: "audit_logs",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "data_protection_keys",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "idempotency_keys",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "inbound_webhooks",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "notification_logs",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "settings",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "wards",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "provinces",
                schema: "platform");
        }
    }
}
