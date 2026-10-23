using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.InventoryService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStockReservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "stock_reservation",
                columns: table => new
                {
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    inventory_owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_reservation", x => x.reservation_id);
                    table.ForeignKey(
                        name: "fk_stock_reservation_inventory_owner",
                        column: x => x.inventory_owner_id,
                        principalTable: "inventory_owner",
                        principalColumn: "inventory_owner_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_reservation_line",
                columns: table => new
                {
                    stock_reservation_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stock_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    quantity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_reservation_line", x => x.stock_reservation_line_id);
                    table.CheckConstraint("ck_stock_reservation_line_quantity_positive", "quantity > 0");
                    table.ForeignKey(
                        name: "FK_stock_reservation_line_stock_reservation_reservation_id",
                        column: x => x.reservation_id,
                        principalTable: "stock_reservation",
                        principalColumn: "reservation_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_reservation_line_stock_item",
                        column: x => x.stock_item_id,
                        principalTable: "stock_item",
                        principalColumn: "stock_item_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_stock_reservation_inventory_owner_id",
                table: "stock_reservation",
                column: "inventory_owner_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_reservation_status_expires_at",
                table: "stock_reservation",
                columns: new[] { "status", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "uq_stock_reservation_company_order_reference",
                table: "stock_reservation",
                columns: new[] { "company_id", "order_reference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_stock_reservation_line_stock_item_id",
                table: "stock_reservation_line",
                column: "stock_item_id");

            migrationBuilder.CreateIndex(
                name: "uq_stock_reservation_line_reservation_stock_item",
                table: "stock_reservation_line",
                columns: new[] { "reservation_id", "stock_item_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "stock_reservation_line");

            migrationBuilder.DropTable(
                name: "stock_reservation");
        }
    }
}
