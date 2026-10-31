using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VetPlatform.Api.Common.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProductsAndStock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "product_id",
                table: "invoice_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "products",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    clinic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false, collation: "case_insensitive"),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true, collation: "case_insensitive"),
                    type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    unit = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    reorder_level = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: true),
                    default_unit_price = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    default_tax_rate_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_products", x => x.id);
                    table.UniqueConstraint("ak_products_clinic_id_id", x => new { x.clinic_id, x.id });
                    table.CheckConstraint("ck_products_category", "category IS NULL OR btrim(category) <> ''");
                    table.CheckConstraint("ck_products_default_unit_price", "default_unit_price IS NULL OR (default_unit_price >= 0 AND default_unit_price <= 1000000000)");
                    table.CheckConstraint("ck_products_name", "btrim(name) <> ''");
                    table.CheckConstraint("ck_products_reorder_level", "reorder_level IS NULL OR (type <> 'Service' AND reorder_level >= 0 AND reorder_level <= 1000000)");
                    table.CheckConstraint("ck_products_sku", "sku COLLATE \"C\" ~ '^[A-Za-z0-9][A-Za-z0-9._/-]*$'");
                    table.CheckConstraint("ck_products_status", "status IN ('Active', 'Inactive')");
                    table.CheckConstraint("ck_products_type", "type IN ('Medication', 'Vaccine', 'Supply', 'Retail', 'Service')");
                    table.CheckConstraint("ck_products_unit", "unit IN ('Each', 'Tablet', 'Capsule', 'Millilitre', 'Litre', 'Gram', 'Kilogram', 'Dose', 'Vial', 'Ampoule', 'Bottle', 'Box', 'Pack')");
                    table.ForeignKey(
                        name: "fk_products_clinics_clinic_id",
                        column: x => x.clinic_id,
                        principalTable: "clinics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_products_tax_rates_clinic_id_default_tax_rate_id",
                        columns: x => new { x.clinic_id, x.default_tax_rate_id },
                        principalTable: "tax_rates",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_lots",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    clinic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lot_number = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true, collation: "case_insensitive"),
                    expires_on = table.Column<DateOnly>(type: "date", nullable: true),
                    location = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true, collation: "case_insensitive"),
                    quantity = table.Column<decimal>(type: "numeric(15,3)", precision: 15, scale: 3, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_lots", x => x.id);
                    table.UniqueConstraint("ak_stock_lots_clinic_id_product_id_id", x => new { x.clinic_id, x.product_id, x.id });
                    table.CheckConstraint("ck_stock_lots_expires_on", "expires_on IS NULL OR expires_on BETWEEN '2000-01-01' AND '2100-12-31'");
                    table.CheckConstraint("ck_stock_lots_location", "location IS NULL OR btrim(location) <> ''");
                    table.CheckConstraint("ck_stock_lots_lot_number", "lot_number IS NULL OR btrim(lot_number) <> ''");
                    table.CheckConstraint("ck_stock_lots_quantity", "quantity >= 0 AND quantity <= 1000000000");
                    table.ForeignKey(
                        name: "fk_stock_lots_clinics_clinic_id",
                        column: x => x.clinic_id,
                        principalTable: "clinics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_lots_products_clinic_id_product_id",
                        columns: x => new { x.clinic_id, x.product_id },
                        principalTable: "products",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_movements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    clinic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stock_lot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: false),
                    unit_cost = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: true),
                    reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    performed_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_movements", x => x.id);
                    table.CheckConstraint("ck_stock_movements_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_stock_movements_notes", "notes IS NULL OR btrim(notes) <> ''");
                    table.CheckConstraint("ck_stock_movements_quantity", "quantity <> 0 AND abs(quantity) <= 1000000 AND (type <> 'Receive' OR quantity > 0)");
                    table.CheckConstraint("ck_stock_movements_reference", "reference IS NULL OR btrim(reference) <> ''");
                    table.CheckConstraint("ck_stock_movements_type", "type IN ('Receive')");
                    table.CheckConstraint("ck_stock_movements_unit_cost", "(unit_cost IS NULL) = (currency IS NULL) AND (unit_cost IS NULL OR (unit_cost >= 0 AND unit_cost <= 1000000000))");
                    table.ForeignKey(
                        name: "fk_stock_movements_clinics_clinic_id",
                        column: x => x.clinic_id,
                        principalTable: "clinics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_movements_products_clinic_id_product_id",
                        columns: x => new { x.clinic_id, x.product_id },
                        principalTable: "products",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_movements_stock_lots_clinic_id_product_id_stock_lot_id",
                        columns: x => new { x.clinic_id, x.product_id, x.stock_lot_id },
                        principalTable: "stock_lots",
                        principalColumns: new[] { "clinic_id", "product_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_movements_users_clinic_id_performed_by_user_id",
                        columns: x => new { x.clinic_id, x.performed_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_invoice_items_clinic_id_product_id",
                table: "invoice_items",
                columns: new[] { "clinic_id", "product_id" });

            migrationBuilder.CreateIndex(
                name: "ix_products_clinic_id_default_tax_rate_id",
                table: "products",
                columns: new[] { "clinic_id", "default_tax_rate_id" });

            migrationBuilder.CreateIndex(
                name: "ix_products_clinic_id_name",
                table: "products",
                columns: new[] { "clinic_id", "name" });

            migrationBuilder.CreateIndex(
                name: "ux_products_clinic_id_sku",
                table: "products",
                columns: new[] { "clinic_id", "sku" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_stock_lots_identity",
                table: "stock_lots",
                columns: new[] { "clinic_id", "product_id", "lot_number", "expires_on", "location" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_stock_movements_clinic_id_occurred_at_id",
                table: "stock_movements",
                columns: new[] { "clinic_id", "occurred_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_movements_clinic_id_performed_by_user_id",
                table: "stock_movements",
                columns: new[] { "clinic_id", "performed_by_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_movements_clinic_id_product_id_occurred_at",
                table: "stock_movements",
                columns: new[] { "clinic_id", "product_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_movements_lot_occurred_at",
                table: "stock_movements",
                columns: new[] { "clinic_id", "product_id", "stock_lot_id", "occurred_at" });

            migrationBuilder.AddForeignKey(
                name: "fk_invoice_items_products_clinic_id_product_id",
                table: "invoice_items",
                columns: new[] { "clinic_id", "product_id" },
                principalTable: "products",
                principalColumns: new[] { "clinic_id", "id" },
                onDelete: ReferentialAction.Restrict);

            // Hand-written rules (ADR-019). Products are deactivated, never deleted, and keep their type and unit once they
            // have a lot. A lot changes only its quantity, which must equal the sum of its movements at commit; movements
            // are append-only. Parents are locked first and tested after (the B-14 lesson).
            migrationBuilder.Sql("""
                CREATE FUNCTION products_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'products are never deleted; deactivate them instead' USING ERRCODE = 'insufficient_privilege';
                    END IF;
                    IF (NEW.id, NEW.clinic_id) IS DISTINCT FROM (OLD.id, OLD.clinic_id) THEN
                        RAISE EXCEPTION 'a product cannot move' USING ERRCODE = 'insufficient_privilege';
                    END IF;
                    IF (NEW.type, NEW.unit) IS DISTINCT FROM (OLD.type, OLD.unit) THEN
                        -- The UPDATE already holds the row lock, so lots being inserted (they hold the product FOR SHARE)
                        -- have committed; this statement's fresh snapshot sees them.
                        IF EXISTS (SELECT 1 FROM stock_lots WHERE clinic_id = OLD.clinic_id AND product_id = OLD.id) THEN
                            RAISE EXCEPTION 'the type and unit of a product with stock cannot change' USING ERRCODE = 'insufficient_privilege';
                        END IF;
                    END IF;
                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER products_guard
                    BEFORE UPDATE OR DELETE ON products
                    FOR EACH ROW EXECUTE FUNCTION products_guard();

                CREATE FUNCTION stock_lots_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE
                    product_type text;
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        SELECT type INTO product_type FROM products
                        WHERE id = NEW.product_id AND clinic_id = NEW.clinic_id
                        FOR SHARE;
                        IF product_type = 'Service' THEN
                            RAISE EXCEPTION 'services hold no stock' USING ERRCODE = 'insufficient_privilege';
                        END IF;
                        IF product_type IN ('Medication', 'Vaccine') AND NEW.expires_on IS NULL THEN
                            RAISE EXCEPTION 'medication and vaccine lots need an expiry date' USING ERRCODE = 'insufficient_privilege';
                        END IF;
                        RETURN NEW; -- a missing product is left to the foreign key
                    END IF;
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'stock lots are never deleted' USING ERRCODE = 'insufficient_privilege';
                    END IF;
                    IF (to_jsonb(NEW) - 'quantity' - 'updated_at') = (to_jsonb(OLD) - 'quantity' - 'updated_at') THEN
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'only a lot''s quantity changes, through stock movements' USING ERRCODE = 'insufficient_privilege';
                END;
                $$;

                CREATE TRIGGER stock_lots_guard
                    BEFORE INSERT OR UPDATE OR DELETE ON stock_lots
                    FOR EACH ROW EXECUTE FUNCTION stock_lots_guard();

                CREATE FUNCTION stock_movements_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE
                    product_status text;
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        -- Stock is received only into active products (B-20 may let stock leave inactive ones).
                        SELECT status INTO product_status FROM products
                        WHERE id = NEW.product_id AND clinic_id = NEW.clinic_id
                        FOR SHARE;
                        IF NEW.type = 'Receive' AND product_status IS DISTINCT FROM 'Active' AND product_status IS NOT NULL THEN
                            RAISE EXCEPTION 'stock is received only into active products' USING ERRCODE = 'insufficient_privilege';
                        END IF;
                        RETURN NEW; -- a missing product is left to the foreign key
                    END IF;
                    RAISE EXCEPTION 'stock movements are never changed or deleted' USING ERRCODE = 'insufficient_privilege';
                END;
                $$;

                CREATE TRIGGER stock_movements_guard
                    BEFORE INSERT OR UPDATE OR DELETE ON stock_movements
                    FOR EACH ROW EXECUTE FUNCTION stock_movements_guard();

                -- At commit (deferred), every lot that was created, changed or moved must hold exactly the sum of its
                -- movements: a quantity changed without a movement, or a movement without the lot's change, cannot commit.
                CREATE FUNCTION stock_lot_matches_movements() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE
                    lot_id uuid := COALESCE((to_jsonb(NEW) ->> 'stock_lot_id')::uuid, NEW.id);
                    stored numeric;
                    moved numeric;
                BEGIN
                    SELECT quantity INTO stored FROM stock_lots WHERE id = lot_id AND clinic_id = NEW.clinic_id;
                    SELECT COALESCE(sum(quantity), 0) INTO moved FROM stock_movements
                    WHERE clinic_id = NEW.clinic_id AND product_id = NEW.product_id AND stock_lot_id = lot_id;
                    IF stored IS DISTINCT FROM moved THEN
                        RAISE EXCEPTION 'a stock lot''s quantity must equal the sum of its movements'
                            USING ERRCODE = 'insufficient_privilege';
                    END IF;
                    RETURN NULL;
                END;
                $$;

                CREATE CONSTRAINT TRIGGER stock_lots_match_movements
                    AFTER INSERT OR UPDATE ON stock_lots
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION stock_lot_matches_movements();

                CREATE CONSTRAINT TRIGGER stock_movements_match_lot
                    AFTER INSERT ON stock_movements
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION stock_lot_matches_movements();

                CREATE TRIGGER products_no_truncate
                    BEFORE TRUNCATE ON products
                    FOR EACH STATEMENT EXECUTE FUNCTION clinical_items_reject_truncate();

                CREATE TRIGGER stock_lots_no_truncate
                    BEFORE TRUNCATE ON stock_lots
                    FOR EACH STATEMENT EXECUTE FUNCTION clinical_items_reject_truncate();

                CREATE TRIGGER stock_movements_no_truncate
                    BEFORE TRUNCATE ON stock_movements
                    FOR EACH STATEMENT EXECUTE FUNCTION clinical_items_reject_truncate();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Never silently destroy stock records or the product links of issued invoices.
            migrationBuilder.Sql("""
                LOCK TABLE stock_movements, stock_lots, products, invoice_items IN ACCESS EXCLUSIVE MODE;

                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM stock_movements) THEN
                        RAISE EXCEPTION 'Refusing to drop stock movements: they are the stock history. Archive them first.';
                    END IF;
                    IF EXISTS (SELECT 1 FROM invoice_items ii JOIN invoices i ON i.id = ii.invoice_id AND i.clinic_id = ii.clinic_id
                               WHERE ii.product_id IS NOT NULL AND i.status <> 'Draft') THEN
                        RAISE EXCEPTION 'Refusing to drop products: issued invoice lines link to them.';
                    END IF;
                END;
                $$;

                DROP TRIGGER IF EXISTS stock_movements_match_lot ON stock_movements;
                DROP TRIGGER IF EXISTS stock_lots_match_movements ON stock_lots;
                DROP FUNCTION IF EXISTS stock_lot_matches_movements();
                DROP TRIGGER IF EXISTS stock_movements_guard ON stock_movements;
                DROP TRIGGER IF EXISTS stock_lots_guard ON stock_lots;
                DROP TRIGGER IF EXISTS products_guard ON products;
                DROP TRIGGER IF EXISTS stock_movements_no_truncate ON stock_movements;
                DROP TRIGGER IF EXISTS stock_lots_no_truncate ON stock_lots;
                DROP TRIGGER IF EXISTS products_no_truncate ON products;
                DROP FUNCTION IF EXISTS stock_movements_guard();
                DROP FUNCTION IF EXISTS stock_lots_guard();
                DROP FUNCTION IF EXISTS products_guard();
                """);

            migrationBuilder.DropForeignKey(
                name: "fk_invoice_items_products_clinic_id_product_id",
                table: "invoice_items");

            migrationBuilder.DropTable(
                name: "stock_movements");

            migrationBuilder.DropTable(
                name: "stock_lots");

            migrationBuilder.DropTable(
                name: "products");

            migrationBuilder.DropIndex(
                name: "ix_invoice_items_clinic_id_product_id",
                table: "invoice_items");

            migrationBuilder.DropColumn(
                name: "product_id",
                table: "invoice_items");
        }
    }
}
