using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VetPlatform.Api.Common.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStockMovements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_stock_movements_quantity",
                table: "stock_movements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_stock_movements_type",
                table: "stock_movements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_stock_movements_unit_cost",
                table: "stock_movements");

            migrationBuilder.AddColumn<Guid>(
                name: "product_id",
                table: "vaccinations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "type",
                table: "stock_movements",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16);

            migrationBuilder.AddColumn<decimal>(
                name: "balance_after",
                table: "stock_movements",
                type: "numeric(15,3)",
                precision: 15,
                scale: 3,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<Guid>(
                name: "consultation_id",
                table: "stock_movements",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "invoice_item_id",
                table: "stock_movements",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "patient_id",
                table: "stock_movements",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reason",
                table: "stock_movements",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "reverses_movement_id",
                table: "stock_movements",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "witness_user_id",
                table: "stock_movements",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "controlled",
                table: "products",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "product_id",
                table: "prescriptions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "ak_stock_movements_clinic_id_stock_lot_id_id",
                table: "stock_movements",
                columns: new[] { "clinic_id", "stock_lot_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_invoice_items_clinic_id_id",
                table: "invoice_items",
                columns: new[] { "clinic_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_vaccinations_clinic_id_product_id",
                table: "vaccinations",
                columns: new[] { "clinic_id", "product_id" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_movements_clinic_id_invoice_item_id",
                table: "stock_movements",
                columns: new[] { "clinic_id", "invoice_item_id" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_movements_clinic_id_patient_id_consultation_id",
                table: "stock_movements",
                columns: new[] { "clinic_id", "patient_id", "consultation_id" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_movements_clinic_id_stock_lot_id_reverses_movement_id",
                table: "stock_movements",
                columns: new[] { "clinic_id", "stock_lot_id", "reverses_movement_id" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_movements_clinic_id_witness_user_id",
                table: "stock_movements",
                columns: new[] { "clinic_id", "witness_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_movements_register",
                table: "stock_movements",
                columns: new[] { "clinic_id", "product_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ux_stock_movements_reverses_movement_id",
                table: "stock_movements",
                column: "reverses_movement_id",
                unique: true,
                filter: "reverses_movement_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_stock_movements_balance_after",
                table: "stock_movements",
                sql: "balance_after >= 0 AND balance_after <= 1000000000");

            migrationBuilder.AddCheckConstraint(
                name: "ck_stock_movements_consultation",
                table: "stock_movements",
                sql: "consultation_id IS NULL OR type = 'Dispense'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_stock_movements_invoice_item",
                table: "stock_movements",
                sql: "invoice_item_id IS NULL OR type = 'Sell'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_stock_movements_patient",
                table: "stock_movements",
                sql: "(patient_id IS NOT NULL) = (type = 'Dispense')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_stock_movements_quantity",
                table: "stock_movements",
                sql: "quantity <> 0 AND abs(quantity) <= 1000000 AND (type NOT IN ('Receive', 'ReturnFromClient') OR quantity > 0) AND (type NOT IN ('Dispense', 'Sell', 'ReturnToSupplier', 'Expire') OR quantity < 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_stock_movements_reason",
                table: "stock_movements",
                sql: "(reason IS NULL OR btrim(reason) <> '') AND (type NOT IN ('Adjust', 'ReturnToSupplier', 'ReturnFromClient', 'Expire', 'Reversal') OR reason IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_stock_movements_reversal",
                table: "stock_movements",
                sql: "(reverses_movement_id IS NOT NULL) = (type = 'Reversal')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_stock_movements_type",
                table: "stock_movements",
                sql: "type IN ('Receive', 'Dispense', 'Sell', 'Adjust', 'ReturnToSupplier', 'ReturnFromClient', 'Expire', 'Reversal')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_stock_movements_unit_cost",
                table: "stock_movements",
                sql: "(unit_cost IS NULL) = (currency IS NULL) AND (unit_cost IS NULL OR (type = 'Receive' AND unit_cost >= 0 AND unit_cost <= 1000000000))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_stock_movements_witness",
                table: "stock_movements",
                sql: "witness_user_id IS NULL OR witness_user_id <> performed_by_user_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_products_controlled",
                table: "products",
                sql: "NOT controlled OR type = 'Medication'");

            migrationBuilder.CreateIndex(
                name: "ix_prescriptions_clinic_id_product_id",
                table: "prescriptions",
                columns: new[] { "clinic_id", "product_id" });

            migrationBuilder.AddForeignKey(
                name: "fk_prescriptions_products_clinic_id_product_id",
                table: "prescriptions",
                columns: new[] { "clinic_id", "product_id" },
                principalTable: "products",
                principalColumns: new[] { "clinic_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_stock_movements_consultations_clinic_id_patient_id_consulta",
                table: "stock_movements",
                columns: new[] { "clinic_id", "patient_id", "consultation_id" },
                principalTable: "consultations",
                principalColumns: new[] { "clinic_id", "patient_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_stock_movements_invoice_items_clinic_id_invoice_item_id",
                table: "stock_movements",
                columns: new[] { "clinic_id", "invoice_item_id" },
                principalTable: "invoice_items",
                principalColumns: new[] { "clinic_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_stock_movements_patients_clinic_id_patient_id",
                table: "stock_movements",
                columns: new[] { "clinic_id", "patient_id" },
                principalTable: "patients",
                principalColumns: new[] { "clinic_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_stock_movements_stock_movements_clinic_id_stock_lot_id_reve",
                table: "stock_movements",
                columns: new[] { "clinic_id", "stock_lot_id", "reverses_movement_id" },
                principalTable: "stock_movements",
                principalColumns: new[] { "clinic_id", "stock_lot_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_stock_movements_users_clinic_id_witness_user_id",
                table: "stock_movements",
                columns: new[] { "clinic_id", "witness_user_id" },
                principalTable: "users",
                principalColumns: new[] { "clinic_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_vaccinations_products_clinic_id_product_id",
                table: "vaccinations",
                columns: new[] { "clinic_id", "product_id" },
                principalTable: "products",
                principalColumns: new[] { "clinic_id", "id" },
                onDelete: ReferentialAction.Restrict);

            // Hand-written rules (ADR-020). The B-19 receipts get their running balance (in the order recorded) with the
            // append-only guard switched off for this one statement.
            migrationBuilder.Sql("""
                ALTER TABLE stock_movements DISABLE TRIGGER stock_movements_guard;
                UPDATE stock_movements m SET balance_after = r.balance
                FROM (SELECT id, sum(quantity) OVER (PARTITION BY clinic_id, stock_lot_id ORDER BY created_at, id) AS balance
                      FROM stock_movements) r
                WHERE m.id = r.id;
                ALTER TABLE stock_movements ENABLE TRIGGER stock_movements_guard;
                """);

            // Products keep their type, unit and controlled flag once they have a lot. A movement locks its lot, so
            // movements of one lot queue and each one's balance_after is the lot's running balance (never below 0:
            // CHECK); controlled drugs need a reason on outgoing sales and dispensing and a witness on every other
            // movement that can take stock out; a reversal exactly undoes a non-reversal of its lot; a sale names a line of an
            // issued invoice for the same product. Parents are locked first and tested after (the B-14 lesson).
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION products_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'products are never deleted; deactivate them instead' USING ERRCODE = 'insufficient_privilege';
                    END IF;
                    IF (NEW.id, NEW.clinic_id) IS DISTINCT FROM (OLD.id, OLD.clinic_id) THEN
                        RAISE EXCEPTION 'a product cannot move' USING ERRCODE = 'insufficient_privilege';
                    END IF;
                    IF (NEW.type, NEW.unit, NEW.controlled) IS DISTINCT FROM (OLD.type, OLD.unit, OLD.controlled) THEN
                        -- The UPDATE already holds the row lock, so lots being inserted (they hold the product FOR SHARE)
                        -- have committed; this statement's fresh snapshot sees them.
                        IF EXISTS (SELECT 1 FROM stock_lots WHERE clinic_id = OLD.clinic_id AND product_id = OLD.id) THEN
                            RAISE EXCEPTION 'the type, unit and controlled flag of a product with stock cannot change'
                                USING ERRCODE = 'insufficient_privilege';
                        END IF;
                    END IF;
                    RETURN NEW;
                END;
                $$;

                CREATE OR REPLACE FUNCTION stock_movements_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE
                    product_status text;
                    product_controlled boolean;
                    prior numeric;
                    original_type text;
                    original_quantity numeric;
                    linked_status text;
                BEGIN
                    IF TG_OP <> 'INSERT' THEN
                        RAISE EXCEPTION 'stock movements are never changed or deleted' USING ERRCODE = 'insufficient_privilege';
                    END IF;
                    SELECT status, controlled INTO product_status, product_controlled FROM products
                    WHERE id = NEW.product_id AND clinic_id = NEW.clinic_id
                    FOR SHARE;
                    IF product_status IS NULL THEN
                        RETURN NEW; -- a missing product is left to the foreign key
                    END IF;
                    -- Stock is received only into active products (stock may still leave inactive ones).
                    IF NEW.type = 'Receive' AND product_status <> 'Active' THEN
                        RAISE EXCEPTION 'stock is received only into active products' USING ERRCODE = 'insufficient_privilege';
                    END IF;
                    -- Movements of one lot queue here, so each sees the ones committed before it.
                    PERFORM 1 FROM stock_lots WHERE id = NEW.stock_lot_id AND clinic_id = NEW.clinic_id FOR NO KEY UPDATE;
                    SELECT COALESCE(sum(quantity), 0) INTO prior FROM stock_movements
                    WHERE clinic_id = NEW.clinic_id AND product_id = NEW.product_id AND stock_lot_id = NEW.stock_lot_id;
                    IF NEW.balance_after <> prior + NEW.quantity THEN
                        RAISE EXCEPTION 'balance_after must be the lot''s balance after this movement' USING ERRCODE = 'insufficient_privilege';
                    END IF;
                    IF product_controlled THEN
                        IF NEW.type IN ('Dispense', 'Sell') AND NEW.reason IS NULL THEN
                            RAISE EXCEPTION 'outgoing controlled-drug movements need a reason' USING ERRCODE = 'insufficient_privilege';
                        END IF;
                        IF NEW.type IN ('Sell', 'Adjust', 'ReturnToSupplier', 'Expire', 'Reversal') AND NEW.witness_user_id IS NULL THEN
                            RAISE EXCEPTION 'controlled-drug movements other than receipts, dispensing and client returns need a witness'
                                USING ERRCODE = 'insufficient_privilege';
                        END IF;
                    END IF;
                    IF NEW.type = 'Reversal' THEN
                        SELECT type, quantity INTO original_type, original_quantity FROM stock_movements
                        WHERE id = NEW.reverses_movement_id AND clinic_id = NEW.clinic_id AND stock_lot_id = NEW.stock_lot_id;
                        IF original_type = 'Reversal' OR original_quantity <> -NEW.quantity THEN
                            RAISE EXCEPTION 'a reversal exactly undoes one movement that is not a reversal'
                                USING ERRCODE = 'insufficient_privilege';
                        END IF;
                    END IF;
                    -- A dispensed consultation must not be discarded (locked, then tested, as for vaccinations).
                    IF NEW.consultation_id IS NOT NULL THEN
                        SELECT status INTO linked_status FROM consultations
                        WHERE id = NEW.consultation_id AND clinic_id = NEW.clinic_id
                        FOR SHARE;
                        IF linked_status = 'Discarded' THEN
                            RAISE EXCEPTION 'stock cannot be dispensed against a discarded consultation'
                                USING ERRCODE = 'insufficient_privilege';
                        END IF;
                    END IF;
                    IF NEW.invoice_item_id IS NOT NULL THEN
                        -- Lock the invoice first, then test its status (a concurrent void is waited for).
                        PERFORM 1 FROM invoices i JOIN invoice_items ii ON ii.invoice_id = i.id AND ii.clinic_id = i.clinic_id
                        WHERE ii.id = NEW.invoice_item_id AND ii.clinic_id = NEW.clinic_id
                        FOR SHARE OF i;
                        IF NOT EXISTS (
                            SELECT 1 FROM invoice_items ii
                            JOIN invoices i ON i.id = ii.invoice_id AND i.clinic_id = ii.clinic_id
                            WHERE ii.id = NEW.invoice_item_id AND ii.clinic_id = NEW.clinic_id
                              AND ii.product_id = NEW.product_id AND i.status = 'Issued') THEN
                            RAISE EXCEPTION 'a sale names a line of an issued invoice for the same product'
                                USING ERRCODE = 'insufficient_privilege';
                        END IF;
                    END IF;
                    RETURN NEW;
                END;
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Never silently destroy stock history or clinical product links; then restore the B-19 guard functions.
            migrationBuilder.Sql("""
                LOCK TABLE stock_movements, products, vaccinations, prescriptions IN ACCESS EXCLUSIVE MODE;

                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM stock_movements
                               WHERE type <> 'Receive' OR reason IS NOT NULL OR witness_user_id IS NOT NULL) THEN
                        RAISE EXCEPTION 'Refusing to drop B-20 stock movements: they are the stock history.';
                    END IF;
                    IF EXISTS (SELECT 1 FROM vaccinations WHERE product_id IS NOT NULL)
                       OR EXISTS (SELECT 1 FROM prescriptions WHERE product_id IS NOT NULL)
                       OR EXISTS (SELECT 1 FROM products WHERE controlled) THEN
                        RAISE EXCEPTION 'Refusing to drop clinical product links or controlled-drug flags.';
                    END IF;
                END;
                $$;

                CREATE OR REPLACE FUNCTION products_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'products are never deleted; deactivate them instead' USING ERRCODE = 'insufficient_privilege';
                    END IF;
                    IF (NEW.id, NEW.clinic_id) IS DISTINCT FROM (OLD.id, OLD.clinic_id) THEN
                        RAISE EXCEPTION 'a product cannot move' USING ERRCODE = 'insufficient_privilege';
                    END IF;
                    IF (NEW.type, NEW.unit) IS DISTINCT FROM (OLD.type, OLD.unit) THEN
                        IF EXISTS (SELECT 1 FROM stock_lots WHERE clinic_id = OLD.clinic_id AND product_id = OLD.id) THEN
                            RAISE EXCEPTION 'the type and unit of a product with stock cannot change' USING ERRCODE = 'insufficient_privilege';
                        END IF;
                    END IF;
                    RETURN NEW;
                END;
                $$;

                CREATE OR REPLACE FUNCTION stock_movements_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE
                    product_status text;
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        SELECT status INTO product_status FROM products
                        WHERE id = NEW.product_id AND clinic_id = NEW.clinic_id
                        FOR SHARE;
                        IF NEW.type = 'Receive' AND product_status IS DISTINCT FROM 'Active' AND product_status IS NOT NULL THEN
                            RAISE EXCEPTION 'stock is received only into active products' USING ERRCODE = 'insufficient_privilege';
                        END IF;
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'stock movements are never changed or deleted' USING ERRCODE = 'insufficient_privilege';
                END;
                $$;
                """);

            migrationBuilder.DropForeignKey(
                name: "fk_prescriptions_products_clinic_id_product_id",
                table: "prescriptions");

            migrationBuilder.DropForeignKey(
                name: "fk_stock_movements_consultations_clinic_id_patient_id_consulta",
                table: "stock_movements");

            migrationBuilder.DropForeignKey(
                name: "fk_stock_movements_invoice_items_clinic_id_invoice_item_id",
                table: "stock_movements");

            migrationBuilder.DropForeignKey(
                name: "fk_stock_movements_patients_clinic_id_patient_id",
                table: "stock_movements");

            migrationBuilder.DropForeignKey(
                name: "fk_stock_movements_stock_movements_clinic_id_stock_lot_id_reve",
                table: "stock_movements");

            migrationBuilder.DropForeignKey(
                name: "fk_stock_movements_users_clinic_id_witness_user_id",
                table: "stock_movements");

            migrationBuilder.DropForeignKey(
                name: "fk_vaccinations_products_clinic_id_product_id",
                table: "vaccinations");

            migrationBuilder.DropIndex(
                name: "ix_vaccinations_clinic_id_product_id",
                table: "vaccinations");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_stock_movements_clinic_id_stock_lot_id_id",
                table: "stock_movements");

            migrationBuilder.DropIndex(
                name: "ix_stock_movements_clinic_id_invoice_item_id",
                table: "stock_movements");

            migrationBuilder.DropIndex(
                name: "ix_stock_movements_clinic_id_patient_id_consultation_id",
                table: "stock_movements");

            migrationBuilder.DropIndex(
                name: "ix_stock_movements_clinic_id_stock_lot_id_reverses_movement_id",
                table: "stock_movements");

            migrationBuilder.DropIndex(
                name: "ix_stock_movements_clinic_id_witness_user_id",
                table: "stock_movements");

            migrationBuilder.DropIndex(
                name: "ix_stock_movements_register",
                table: "stock_movements");

            migrationBuilder.DropIndex(
                name: "ux_stock_movements_reverses_movement_id",
                table: "stock_movements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_stock_movements_balance_after",
                table: "stock_movements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_stock_movements_consultation",
                table: "stock_movements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_stock_movements_invoice_item",
                table: "stock_movements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_stock_movements_patient",
                table: "stock_movements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_stock_movements_quantity",
                table: "stock_movements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_stock_movements_reason",
                table: "stock_movements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_stock_movements_reversal",
                table: "stock_movements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_stock_movements_type",
                table: "stock_movements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_stock_movements_unit_cost",
                table: "stock_movements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_stock_movements_witness",
                table: "stock_movements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_products_controlled",
                table: "products");

            migrationBuilder.DropIndex(
                name: "ix_prescriptions_clinic_id_product_id",
                table: "prescriptions");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_invoice_items_clinic_id_id",
                table: "invoice_items");

            migrationBuilder.DropColumn(
                name: "product_id",
                table: "vaccinations");

            migrationBuilder.DropColumn(
                name: "balance_after",
                table: "stock_movements");

            migrationBuilder.DropColumn(
                name: "consultation_id",
                table: "stock_movements");

            migrationBuilder.DropColumn(
                name: "invoice_item_id",
                table: "stock_movements");

            migrationBuilder.DropColumn(
                name: "patient_id",
                table: "stock_movements");

            migrationBuilder.DropColumn(
                name: "reason",
                table: "stock_movements");

            migrationBuilder.DropColumn(
                name: "reverses_movement_id",
                table: "stock_movements");

            migrationBuilder.DropColumn(
                name: "witness_user_id",
                table: "stock_movements");

            migrationBuilder.DropColumn(
                name: "controlled",
                table: "products");

            migrationBuilder.DropColumn(
                name: "product_id",
                table: "prescriptions");

            migrationBuilder.AlterColumn<string>(
                name: "type",
                table: "stock_movements",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);

            migrationBuilder.AddCheckConstraint(
                name: "ck_stock_movements_quantity",
                table: "stock_movements",
                sql: "quantity <> 0 AND abs(quantity) <= 1000000 AND (type <> 'Receive' OR quantity > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_stock_movements_type",
                table: "stock_movements",
                sql: "type IN ('Receive')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_stock_movements_unit_cost",
                table: "stock_movements",
                sql: "(unit_cost IS NULL) = (currency IS NULL) AND (unit_cost IS NULL OR (unit_cost >= 0 AND unit_cost <= 1000000000))");
        }
    }
}
