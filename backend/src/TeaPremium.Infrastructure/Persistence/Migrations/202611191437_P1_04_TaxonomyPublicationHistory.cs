using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1707 // Preserve the approved P1_04 task identifier.

namespace TeaPremium.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class P1_04_TaxonomyPublicationHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "first_published_at",
                schema: "catalog",
                table: "materials",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "first_published_at",
                schema: "catalog",
                table: "collections",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "first_published_at",
                schema: "catalog",
                table: "categories",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "first_published_at",
                schema: "catalog",
                table: "artisans",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Never discard known public history. Real-data targets use backup + roll forward.
            migrationBuilder.Sql("""
DO $guard$
BEGIN
  IF current_user <> 'tp_owner' OR (current_database() <> 'teapremium_test' AND strpos(current_database(), 'teapremium_test_') <> 1) THEN
    RAISE EXCEPTION 'P1-04 Down requires a disposable test database and tp_owner';
  END IF;
  IF EXISTS (SELECT FROM catalog.categories WHERE first_published_at IS NOT NULL)
    OR EXISTS (SELECT FROM catalog.collections WHERE first_published_at IS NOT NULL)
    OR EXISTS (SELECT FROM catalog.materials WHERE first_published_at IS NOT NULL)
    OR EXISTS (SELECT FROM catalog.artisans WHERE first_published_at IS NOT NULL) THEN
    RAISE EXCEPTION 'P1-04 Down refused: publication history exists; use recovery/roll forward';
  END IF;
END $guard$;
""");
            migrationBuilder.DropColumn(
                name: "first_published_at",
                schema: "catalog",
                table: "materials");

            migrationBuilder.DropColumn(
                name: "first_published_at",
                schema: "catalog",
                table: "collections");

            migrationBuilder.DropColumn(
                name: "first_published_at",
                schema: "catalog",
                table: "categories");

            migrationBuilder.DropColumn(
                name: "first_published_at",
                schema: "catalog",
                table: "artisans");
        }
    }
}
