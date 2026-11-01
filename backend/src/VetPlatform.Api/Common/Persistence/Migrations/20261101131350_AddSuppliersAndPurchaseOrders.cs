using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VetPlatform.Api.Common.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSuppliersAndPurchaseOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "purchase_order_item_id",
                table: "stock_movements",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "supplier_id",
                table: "stock_movements",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "purchase_order_number_sequences",
                columns: table => new
                {
                    clinic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    next_number = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_purchase_order_number_sequences", x => x.clinic_id);
                    table.CheckConstraint("ck_purchase_order_number_sequences_next_number", "next_number > 0");
                    table.ForeignKey(
                        name: "fk_purchase_order_number_sequences_clinics_clinic_id",
                        column: x => x.clinic_id,
                        principalTable: "clinics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "suppliers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    clinic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "case_insensitive"),
                    contact_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    phone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    address_line1 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    address_line2 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    city = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    region = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    postal_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    country = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    account_number = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_suppliers", x => x.id);
                    table.UniqueConstraint("ak_suppliers_clinic_id_id", x => new { x.clinic_id, x.id });
                    table.CheckConstraint("ck_suppliers_name", "btrim(name) <> ''");
                    table.CheckConstraint("ck_suppliers_status", "status IN ('Active', 'Inactive')");
                    table.ForeignKey(
                        name: "fk_suppliers_clinics_clinic_id",
                        column: x => x.clinic_id,
                        principalTable: "clinics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "purchase_orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    clinic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    total = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    ordered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ordered_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cancel_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    close_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_purchase_orders", x => x.id);
                    table.UniqueConstraint("ak_purchase_orders_clinic_id_id", x => new { x.clinic_id, x.id });
                    table.CheckConstraint("ck_purchase_orders_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_purchase_orders_lifecycle", "(status = 'Draft') = (number IS NULL) AND (status = 'Draft') = (ordered_at IS NULL) AND (ordered_at IS NULL) = (ordered_by_user_id IS NULL) AND (status = 'Received') = (received_at IS NOT NULL) AND (received_at IS NULL OR received_at >= ordered_at) AND (status = 'Cancelled') = (cancelled_at IS NOT NULL) AND (cancelled_at IS NULL) = (cancelled_by_user_id IS NULL) AND (cancelled_at IS NULL) = (cancel_reason IS NULL) AND (cancelled_at IS NULL OR cancelled_at >= ordered_at) AND (status = 'Closed') = (closed_at IS NOT NULL) AND (closed_at IS NULL) = (closed_by_user_id IS NULL) AND (closed_at IS NULL) = (close_reason IS NULL) AND (closed_at IS NULL OR closed_at >= ordered_at)");
                    table.CheckConstraint("ck_purchase_orders_number", "number > 0");
                    table.CheckConstraint("ck_purchase_orders_reasons", "btrim(cancel_reason) <> '' AND btrim(close_reason) <> ''");
                    table.CheckConstraint("ck_purchase_orders_status", "status IN ('Draft', 'Ordered', 'PartiallyReceived', 'Received', 'Cancelled', 'Closed')");
                    table.CheckConstraint("ck_purchase_orders_total", "total >= 0 AND total <= 1000000000000");
                    table.ForeignKey(
                        name: "fk_purchase_orders_clinics_clinic_id",
                        column: x => x.clinic_id,
                        principalTable: "clinics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_purchase_orders_suppliers_clinic_id_supplier_id",
                        columns: x => new { x.clinic_id, x.supplier_id },
                        principalTable: "suppliers",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_purchase_orders_users_clinic_id_cancelled_by_user_id",
                        columns: x => new { x.clinic_id, x.cancelled_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_purchase_orders_users_clinic_id_closed_by_user_id",
                        columns: x => new { x.clinic_id, x.closed_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_purchase_orders_users_clinic_id_created_by_user_id",
                        columns: x => new { x.clinic_id, x.created_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_purchase_orders_users_clinic_id_ordered_by_user_id",
                        columns: x => new { x.clinic_id, x.ordered_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "purchase_order_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    clinic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purchase_order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: false),
                    unit_cost = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    line_total = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    quantity_received = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_purchase_order_items", x => x.id);
                    table.UniqueConstraint("ak_purchase_order_items_clinic_id_product_id_id", x => new { x.clinic_id, x.product_id, x.id });
                    table.CheckConstraint("ck_purchase_order_items_line_total", "line_total >= 0 AND line_total <= 1000000000000");
                    table.CheckConstraint("ck_purchase_order_items_position", "position > 0");
                    table.CheckConstraint("ck_purchase_order_items_quantity", "quantity > 0 AND quantity <= 1000000");
                    table.CheckConstraint("ck_purchase_order_items_quantity_received", "quantity_received >= 0 AND quantity_received <= quantity");
                    table.CheckConstraint("ck_purchase_order_items_unit_cost", "unit_cost >= 0 AND unit_cost <= 1000000000");
                    table.ForeignKey(
                        name: "fk_purchase_order_items_clinics_clinic_id",
                        column: x => x.clinic_id,
                        principalTable: "clinics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_purchase_order_items_products_clinic_id_product_id",
                        columns: x => new { x.clinic_id, x.product_id },
                        principalTable: "products",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_purchase_order_items_purchase_orders_clinic_id_purchase_ord",
                        columns: x => new { x.clinic_id, x.purchase_order_id },
                        principalTable: "purchase_orders",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_stock_movements_clinic_id_product_id_purchase_order_item_id",
                table: "stock_movements",
                columns: new[] { "clinic_id", "product_id", "purchase_order_item_id" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_movements_clinic_id_supplier_id",
                table: "stock_movements",
                columns: new[] { "clinic_id", "supplier_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_stock_movements_purchase_order_item",
                table: "stock_movements",
                sql: "purchase_order_item_id IS NULL OR (type IN ('Receive', 'Reversal') AND supplier_id IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_stock_movements_supplier",
                table: "stock_movements",
                sql: "supplier_id IS NULL OR type IN ('Receive', 'ReturnToSupplier', 'Reversal')");

            migrationBuilder.CreateIndex(
                name: "ix_purchase_order_items_clinic_id_purchase_order_id_position",
                table: "purchase_order_items",
                columns: new[] { "clinic_id", "purchase_order_id", "position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_purchase_order_items_clinic_id_purchase_order_id_product_id",
                table: "purchase_order_items",
                columns: new[] { "clinic_id", "purchase_order_id", "product_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_purchase_orders_clinic_id_cancelled_by_user_id",
                table: "purchase_orders",
                columns: new[] { "clinic_id", "cancelled_by_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_purchase_orders_clinic_id_closed_by_user_id",
                table: "purchase_orders",
                columns: new[] { "clinic_id", "closed_by_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_purchase_orders_clinic_id_created_at",
                table: "purchase_orders",
                columns: new[] { "clinic_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_purchase_orders_clinic_id_created_by_user_id",
                table: "purchase_orders",
                columns: new[] { "clinic_id", "created_by_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_purchase_orders_clinic_id_ordered_by_user_id",
                table: "purchase_orders",
                columns: new[] { "clinic_id", "ordered_by_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_purchase_orders_clinic_id_supplier_id_created_at",
                table: "purchase_orders",
                columns: new[] { "clinic_id", "supplier_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_purchase_orders_clinic_number",
                table: "purchase_orders",
                columns: new[] { "clinic_id", "number" },
                unique: true,
                filter: "number IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_suppliers_clinic_id_name",
                table: "suppliers",
                columns: new[] { "clinic_id", "name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_stock_movements_purchase_order_items_clinic_id_product_id",
                table: "stock_movements",
                columns: new[] { "clinic_id", "product_id", "purchase_order_item_id" },
                principalTable: "purchase_order_items",
                principalColumns: new[] { "clinic_id", "product_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_stock_movements_suppliers_clinic_id_supplier_id",
                table: "stock_movements",
                columns: new[] { "clinic_id", "supplier_id" },
                principalTable: "suppliers",
                principalColumns: new[] { "clinic_id", "id" },
                onDelete: ReferentialAction.Restrict);

            // Hand-written rules (ADR-021). Orders: drafts change freely; ordering needs lines whose totals add up; after
            // that only the receiving status, cancellation and closing change, and nothing is deleted. Lines change only
            // on drafts, except the received quantity. At commit, each line's received quantity equals its deliveries
            // minus their reversals, and each placed order's status matches its lines. Parents are locked first and
            // tested after (the B-14 lesson).
            migrationBuilder.Sql("""
                CREATE FUNCTION purchase_orders_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE
                    line_count integer;
                    line_sum numeric;
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        IF NEW.status <> 'Draft' THEN
                            RAISE EXCEPTION 'a purchase order starts as a draft' USING ERRCODE = 'insufficient_privilege';
                        END IF;
                        RETURN NEW;
                    END IF;
                    IF TG_OP = 'DELETE' THEN
                        IF OLD.status = 'Draft' THEN
                            RETURN OLD;
                        END IF;
                        RAISE EXCEPTION 'placed purchase orders are never deleted; cancel or close them' USING ERRCODE = 'insufficient_privilege';
                    END IF;
                    IF (NEW.id, NEW.clinic_id) IS DISTINCT FROM (OLD.id, OLD.clinic_id) THEN
                        RAISE EXCEPTION 'a purchase order cannot move' USING ERRCODE = 'insufficient_privilege';
                    END IF;
                    IF OLD.status = 'Draft' AND NEW.status = 'Draft' THEN
                        RETURN NEW;
                    END IF;
                    IF OLD.status = 'Draft' AND NEW.status = 'Ordered' THEN
                        SELECT count(*), COALESCE(sum(line_total), 0) INTO line_count, line_sum
                        FROM purchase_order_items WHERE purchase_order_id = NEW.id AND clinic_id = NEW.clinic_id;
                        IF line_count = 0 OR line_sum <> NEW.total THEN
                            RAISE EXCEPTION 'a purchase order is placed only with lines whose totals add up to its total'
                                USING ERRCODE = 'insufficient_privilege';
                        END IF;
                        RETURN NEW;
                    END IF;
                    IF (OLD.status, NEW.status) IN (
                            ('Ordered', 'Ordered'), ('Ordered', 'PartiallyReceived'), ('Ordered', 'Received'),
                            ('PartiallyReceived', 'Ordered'), ('PartiallyReceived', 'PartiallyReceived'), ('PartiallyReceived', 'Received'),
                            ('Received', 'Ordered'), ('Received', 'PartiallyReceived'), ('Received', 'Received'),
                            ('Ordered', 'Cancelled'), ('PartiallyReceived', 'Closed'), ('Closed', 'Closed'))
                        AND (to_jsonb(NEW) - 'status' - 'received_at' - 'cancelled_at' - 'cancelled_by_user_id' - 'cancel_reason'
                             - 'closed_at' - 'closed_by_user_id' - 'close_reason' - 'updated_at')
                          = (to_jsonb(OLD) - 'status' - 'received_at' - 'cancelled_at' - 'cancelled_by_user_id' - 'cancel_reason'
                             - 'closed_at' - 'closed_by_user_id' - 'close_reason' - 'updated_at')
                        AND (OLD.status <> 'Closed' OR (to_jsonb(NEW) - 'updated_at') = (to_jsonb(OLD) - 'updated_at'))
                        AND (OLD.status <> 'Received' OR NEW.status <> 'Received' OR NEW.received_at IS NOT DISTINCT FROM OLD.received_at) THEN
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'a % purchase order cannot be changed this way', OLD.status USING ERRCODE = 'insufficient_privilege';
                END;
                $$;

                CREATE TRIGGER purchase_orders_guard
                    BEFORE INSERT OR UPDATE OR DELETE ON purchase_orders
                    FOR EACH ROW EXECUTE FUNCTION purchase_orders_guard();

                CREATE FUNCTION purchase_order_items_follow_order() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE
                    parent_status text;
                BEGIN
                    IF TG_OP = 'UPDATE' AND (NEW.purchase_order_id, NEW.clinic_id) IS DISTINCT FROM (OLD.purchase_order_id, OLD.clinic_id) THEN
                        RAISE EXCEPTION 'a purchase order line cannot move to another order' USING ERRCODE = 'insufficient_privilege';
                    END IF;
                    IF TG_OP = 'UPDATE' AND NEW.quantity_received <> OLD.quantity_received THEN
                        -- Exclusive, so concurrent writers of different lines queue and the commit-time status check sees
                        -- them all (the API takes this lock first anyway).
                        SELECT status INTO parent_status FROM purchase_orders
                        WHERE id = NEW.purchase_order_id AND clinic_id = NEW.clinic_id
                        FOR NO KEY UPDATE;
                    ELSE
                        SELECT status INTO parent_status FROM purchase_orders
                        WHERE id = COALESCE(NEW.purchase_order_id, OLD.purchase_order_id) AND clinic_id = COALESCE(NEW.clinic_id, OLD.clinic_id)
                        FOR SHARE;
                    END IF;
                    IF parent_status IS NULL OR parent_status = 'Draft' THEN
                        IF TG_OP <> 'DELETE' AND NEW.quantity_received <> 0 THEN
                            RAISE EXCEPTION 'nothing is received on a draft' USING ERRCODE = 'insufficient_privilege';
                        END IF;
                        RETURN COALESCE(NEW, OLD); -- a missing parent is left to the foreign key
                    END IF;
                    IF TG_OP = 'UPDATE' AND parent_status IN ('Ordered', 'PartiallyReceived', 'Received', 'Closed')
                        AND (to_jsonb(NEW) - 'quantity_received' - 'updated_at') = (to_jsonb(OLD) - 'quantity_received' - 'updated_at') THEN
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'lines of a % purchase order cannot be changed this way', parent_status USING ERRCODE = 'insufficient_privilege';
                END;
                $$;

                CREATE TRIGGER purchase_order_items_follow_order
                    BEFORE INSERT OR UPDATE OR DELETE ON purchase_order_items
                    FOR EACH ROW EXECUTE FUNCTION purchase_order_items_follow_order();

                -- At commit: a line's received quantity is its deliveries minus their reversals.
                CREATE FUNCTION purchase_order_item_matches_movements() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE
                    item_id uuid := CASE WHEN TG_TABLE_NAME = 'stock_movements'
                        THEN (to_jsonb(NEW) ->> 'purchase_order_item_id')::uuid ELSE NEW.id END;
                    stored numeric;
                    moved numeric;
                BEGIN
                    IF item_id IS NULL THEN
                        RETURN NULL;
                    END IF;
                    SELECT quantity_received INTO stored FROM purchase_order_items WHERE id = item_id AND clinic_id = NEW.clinic_id;
                    SELECT COALESCE(sum(quantity), 0) INTO moved FROM stock_movements
                    WHERE clinic_id = NEW.clinic_id AND product_id = NEW.product_id AND purchase_order_item_id = item_id;
                    IF stored IS NOT NULL AND stored <> moved THEN
                        RAISE EXCEPTION 'a purchase order line''s received quantity must equal its deliveries'
                            USING ERRCODE = 'insufficient_privilege';
                    END IF;
                    RETURN NULL;
                END;
                $$;

                CREATE CONSTRAINT TRIGGER purchase_order_items_match_movements
                    AFTER INSERT OR UPDATE ON purchase_order_items
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION purchase_order_item_matches_movements();

                CREATE CONSTRAINT TRIGGER stock_movements_match_purchase_order_item
                    AFTER INSERT ON stock_movements
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION purchase_order_item_matches_movements();

                -- At commit: a placed order's status agrees with what its lines received.
                CREATE FUNCTION purchase_order_matches_items() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE
                    order_id uuid := CASE WHEN TG_TABLE_NAME = 'purchase_order_items'
                        THEN (to_jsonb(NEW) ->> 'purchase_order_id')::uuid ELSE NEW.id END;
                    order_status text;
                    nothing boolean;
                    everything boolean;
                BEGIN
                    SELECT status INTO order_status FROM purchase_orders WHERE id = order_id AND clinic_id = NEW.clinic_id;
                    IF order_status IS NULL OR order_status = 'Draft' THEN
                        RETURN NULL;
                    END IF;
                    SELECT bool_and(quantity_received = 0), bool_and(quantity_received = quantity) INTO nothing, everything
                    FROM purchase_order_items WHERE purchase_order_id = order_id AND clinic_id = NEW.clinic_id;
                    IF (order_status = 'Ordered' AND NOT nothing)
                        OR (order_status = 'PartiallyReceived' AND (nothing OR everything))
                        OR (order_status = 'Received' AND NOT everything)
                        OR (order_status = 'Cancelled' AND NOT nothing)
                        OR (order_status = 'Closed' AND everything) THEN
                        RAISE EXCEPTION 'a % purchase order does not match what its lines received', order_status
                            USING ERRCODE = 'insufficient_privilege';
                    END IF;
                    RETURN NULL;
                END;
                $$;

                CREATE CONSTRAINT TRIGGER purchase_orders_match_items
                    AFTER INSERT OR UPDATE ON purchase_orders
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION purchase_order_matches_items();

                CREATE CONSTRAINT TRIGGER purchase_order_items_match_order
                    AFTER INSERT OR UPDATE ON purchase_order_items
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION purchase_order_matches_items();

                CREATE FUNCTION suppliers_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'UPDATE' AND (NEW.id, NEW.clinic_id) IS NOT DISTINCT FROM (OLD.id, OLD.clinic_id) THEN
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'suppliers are never deleted or moved; deactivate them' USING ERRCODE = 'insufficient_privilege';
                END;
                $$;

                CREATE TRIGGER suppliers_guard
                    BEFORE UPDATE OR DELETE ON suppliers
                    FOR EACH ROW EXECUTE FUNCTION suppliers_guard();

                CREATE TRIGGER suppliers_no_truncate
                    BEFORE TRUNCATE ON suppliers
                    FOR EACH STATEMENT EXECUTE FUNCTION clinical_items_reject_truncate();

                CREATE TRIGGER purchase_orders_no_truncate
                    BEFORE TRUNCATE ON purchase_orders
                    FOR EACH STATEMENT EXECUTE FUNCTION clinical_items_reject_truncate();

                CREATE TRIGGER purchase_order_items_no_truncate
                    BEFORE TRUNCATE ON purchase_order_items
                    FOR EACH STATEMENT EXECUTE FUNCTION clinical_items_reject_truncate();

                CREATE OR REPLACE FUNCTION stock_movements_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE
                    product_status text;
                    product_controlled boolean;
                    prior numeric;
                    original_type text;
                    original_quantity numeric;
                    linked_status text;
                    original_supplier uuid;
                    original_item uuid;
                    order_status text;
                    order_supplier uuid;
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
                        SELECT type, quantity, supplier_id, purchase_order_item_id
                        INTO original_type, original_quantity, original_supplier, original_item FROM stock_movements
                        WHERE id = NEW.reverses_movement_id AND clinic_id = NEW.clinic_id AND stock_lot_id = NEW.stock_lot_id;
                        IF original_type = 'Reversal' OR original_quantity <> -NEW.quantity
                            OR (original_type IS NOT NULL AND (original_supplier, original_item) IS DISTINCT FROM (NEW.supplier_id, NEW.purchase_order_item_id)) THEN
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
                    -- A delivery against an order line (B-21): the order is locked (exclusively, see the line trigger) first,
                    -- then tested; it must have been
                    -- placed and be neither cancelled nor closed, and the movement names the order's supplier.
                    IF NEW.purchase_order_item_id IS NOT NULL THEN
                        SELECT o.status, o.supplier_id INTO order_status, order_supplier
                        FROM purchase_orders o JOIN purchase_order_items i ON i.purchase_order_id = o.id AND i.clinic_id = o.clinic_id
                        WHERE i.id = NEW.purchase_order_item_id AND i.clinic_id = NEW.clinic_id
                        FOR NO KEY UPDATE OF o;
                        IF order_supplier IS DISTINCT FROM NEW.supplier_id
                            OR (NEW.type = 'Receive' AND order_status IN ('Draft', 'Cancelled', 'Closed')) THEN
                            RAISE EXCEPTION 'a delivery is received only against a placed, open order and names its supplier'
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
            // Never silently destroy purchasing records; then restore the B-20 movement guard.
            migrationBuilder.Sql("""
                LOCK TABLE stock_movements, purchase_orders, purchase_order_items, suppliers IN ACCESS EXCLUSIVE MODE;

                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM suppliers)
                       OR EXISTS (SELECT 1 FROM stock_movements WHERE supplier_id IS NOT NULL OR purchase_order_item_id IS NOT NULL) THEN
                        RAISE EXCEPTION 'Refusing to drop purchasing records: suppliers, orders or supplier-linked stock movements exist.';
                    END IF;
                END;
                $$;

                DROP TRIGGER IF EXISTS purchase_orders_match_items ON purchase_orders;
                DROP TRIGGER IF EXISTS purchase_order_items_match_order ON purchase_order_items;
                DROP FUNCTION IF EXISTS purchase_order_matches_items();
                DROP TRIGGER IF EXISTS purchase_order_items_match_movements ON purchase_order_items;
                DROP TRIGGER IF EXISTS stock_movements_match_purchase_order_item ON stock_movements;
                DROP FUNCTION IF EXISTS purchase_order_item_matches_movements();
                DROP TRIGGER IF EXISTS purchase_order_items_follow_order ON purchase_order_items;
                DROP TRIGGER IF EXISTS purchase_orders_guard ON purchase_orders;
                DROP TRIGGER IF EXISTS suppliers_guard ON suppliers;
                DROP TRIGGER IF EXISTS suppliers_no_truncate ON suppliers;
                DROP TRIGGER IF EXISTS purchase_orders_no_truncate ON purchase_orders;
                DROP TRIGGER IF EXISTS purchase_order_items_no_truncate ON purchase_order_items;
                DROP FUNCTION IF EXISTS purchase_order_items_follow_order();
                DROP FUNCTION IF EXISTS purchase_orders_guard();
                DROP FUNCTION IF EXISTS suppliers_guard();

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

            migrationBuilder.DropForeignKey(
                name: "fk_stock_movements_purchase_order_items_clinic_id_product_id",
                table: "stock_movements");

            migrationBuilder.DropForeignKey(
                name: "fk_stock_movements_suppliers_clinic_id_supplier_id",
                table: "stock_movements");

            migrationBuilder.DropTable(
                name: "purchase_order_items");

            migrationBuilder.DropTable(
                name: "purchase_order_number_sequences");

            migrationBuilder.DropTable(
                name: "purchase_orders");

            migrationBuilder.DropTable(
                name: "suppliers");

            migrationBuilder.DropIndex(
                name: "ix_stock_movements_clinic_id_product_id_purchase_order_item_id",
                table: "stock_movements");

            migrationBuilder.DropIndex(
                name: "ix_stock_movements_clinic_id_supplier_id",
                table: "stock_movements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_stock_movements_purchase_order_item",
                table: "stock_movements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_stock_movements_supplier",
                table: "stock_movements");

            migrationBuilder.DropColumn(
                name: "purchase_order_item_id",
                table: "stock_movements");

            migrationBuilder.DropColumn(
                name: "supplier_id",
                table: "stock_movements");
        }
    }
}
