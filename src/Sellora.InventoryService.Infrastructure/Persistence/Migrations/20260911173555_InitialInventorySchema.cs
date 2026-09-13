using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.InventoryService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialInventorySchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "inventory_owner",
                columns: table => new
                {
                    inventory_owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    external_owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inventory_owner", x => x.inventory_owner_id);
                    table.CheckConstraint("ck_inventory_owner_type", "owner_type IN ('Company', 'Agency', 'SalesRep')");
                });

            migrationBuilder.CreateTable(
                name: "stock_item",
                columns: table => new
                {
                    stock_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    inventory_owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    quantity_on_hand = table.Column<int>(type: "integer", nullable: false),
                    quantity_reserved = table.Column<int>(type: "integer", nullable: false),
                    reorder_threshold = table.Column<int>(type: "integer", nullable: true),
                    low_stock_notified = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    row_version = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_item", x => x.stock_item_id);
                    table.CheckConstraint("ck_stock_item_on_hand_non_negative", "quantity_on_hand >= 0");
                    table.CheckConstraint("ck_stock_item_reserved_non_negative", "quantity_reserved >= 0");
                    table.CheckConstraint("ck_stock_item_reserved_not_above_on_hand", "quantity_reserved <= quantity_on_hand");
                    table.ForeignKey(
                        name: "fk_stock_item_inventory_owner",
                        column: x => x.inventory_owner_id,
                        principalTable: "inventory_owner",
                        principalColumn: "inventory_owner_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_movement",
                columns: table => new
                {
                    stock_movement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stock_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    movement_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    on_hand_delta = table.Column<int>(type: "integer", nullable: false),
                    reserved_delta = table.Column<int>(type: "integer", nullable: false),
                    actor_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    reference_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    reference_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    reason = table.Column<string>(type: "text", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_movement", x => x.stock_movement_id);
                    table.CheckConstraint("ck_stock_movement_type", "movement_type IN (" + "'Adjustment', 'Reserved', 'Released', 'Sold', 'Returned', 'Transferred')");
                    table.ForeignKey(
                        name: "fk_stock_movement_stock_item",
                        column: x => x.stock_item_id,
                        principalTable: "stock_item",
                        principalColumn: "stock_item_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "uq_inventory_owner_company_type_external_owner",
                table: "inventory_owner",
                columns: new[] { "company_id", "owner_type", "external_owner_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_stock_item_owner_product_batch",
                table: "stock_item",
                columns: new[] { "inventory_owner_id", "product_id", "batch_id" },
                unique: true,
                filter: "\"batch_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "uq_stock_item_owner_product_without_batch",
                table: "stock_item",
                columns: new[] { "inventory_owner_id", "product_id" },
                unique: true,
                filter: "\"batch_id\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_stock_movement_stock_item_occurred_at",
                table: "stock_movement",
                columns: new[] { "stock_item_id", "occurred_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "stock_movement");

            migrationBuilder.DropTable(
                name: "stock_item");

            migrationBuilder.DropTable(
                name: "inventory_owner");
        }
    }
}
