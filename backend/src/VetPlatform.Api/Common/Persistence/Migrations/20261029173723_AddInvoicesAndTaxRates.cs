using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VetPlatform.Api.Common.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoicesAndTaxRates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "currency",
                table: "clinics",
                type: "character(3)",
                fixedLength: true,
                maxLength: 3,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "invoice_number_sequences",
                columns: table => new
                {
                    clinic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    next_number = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invoice_number_sequences", x => x.clinic_id);
                    table.CheckConstraint("ck_invoice_number_sequences_next_number", "next_number > 0");
                    table.ForeignKey(
                        name: "fk_invoice_number_sequences_clinics_clinic_id",
                        column: x => x.clinic_id,
                        principalTable: "clinics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "invoices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    clinic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: true),
                    number = table.Column<int>(type: "integer", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    subtotal = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    discount_total = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    tax_total = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    total = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    due_on = table.Column<DateOnly>(type: "date", nullable: true),
                    issued_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    voided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    voided_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    void_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invoices", x => x.id);
                    table.UniqueConstraint("ak_invoices_clinic_id_id", x => new { x.clinic_id, x.id });
                    table.CheckConstraint("ck_invoices_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_invoices_lifecycle", "CASE status WHEN 'Draft' THEN number IS NULL AND issued_at IS NULL AND due_on IS NULL AND issued_by_user_id IS NULL AND voided_at IS NULL AND voided_by_user_id IS NULL AND void_reason IS NULL WHEN 'Issued' THEN number IS NOT NULL AND issued_at IS NOT NULL AND due_on IS NOT NULL AND issued_by_user_id IS NOT NULL AND voided_at IS NULL AND voided_by_user_id IS NULL AND void_reason IS NULL WHEN 'Void' THEN number IS NOT NULL AND issued_at IS NOT NULL AND due_on IS NOT NULL AND issued_by_user_id IS NOT NULL AND voided_at IS NOT NULL AND voided_at >= issued_at AND voided_by_user_id IS NOT NULL AND void_reason IS NOT NULL AND btrim(void_reason) <> '' ELSE false END");
                    table.CheckConstraint("ck_invoices_number", "number > 0");
                    table.CheckConstraint("ck_invoices_status", "status IN ('Draft', 'Issued', 'Void')");
                    table.CheckConstraint("ck_invoices_totals", "subtotal >= 0 AND discount_total >= 0 AND discount_total <= subtotal AND tax_total >= 0 AND total = subtotal - discount_total + tax_total AND total <= 1000000000000");
                    table.ForeignKey(
                        name: "fk_invoices_clients_clinic_id_client_id",
                        columns: x => new { x.clinic_id, x.client_id },
                        principalTable: "clients",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_invoices_clinics_clinic_id",
                        column: x => x.clinic_id,
                        principalTable: "clinics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_invoices_patients_clinic_id_patient_id",
                        columns: x => new { x.clinic_id, x.patient_id },
                        principalTable: "patients",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_invoices_users_clinic_id_created_by_user_id",
                        columns: x => new { x.clinic_id, x.created_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_invoices_users_clinic_id_issued_by_user_id",
                        columns: x => new { x.clinic_id, x.issued_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_invoices_users_clinic_id_voided_by_user_id",
                        columns: x => new { x.clinic_id, x.voided_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tax_rates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    clinic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    rate_percent = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tax_rates", x => x.id);
                    table.UniqueConstraint("ak_tax_rates_clinic_id_id", x => new { x.clinic_id, x.id });
                    table.CheckConstraint("ck_tax_rates_name", "btrim(name) <> ''");
                    table.CheckConstraint("ck_tax_rates_rate_percent", "rate_percent BETWEEN 0 AND 100");
                    table.CheckConstraint("ck_tax_rates_status", "status IN ('Active', 'Archived')");
                    table.ForeignKey(
                        name: "fk_tax_rates_clinics_clinic_id",
                        column: x => x.clinic_id,
                        principalTable: "clinics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "invoice_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    clinic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    discount_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    discount_value = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    tax_rate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tax_rate_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    tax_rate_percent = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    consultation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    net = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    discount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    tax = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    total = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invoice_items", x => x.id);
                    table.CheckConstraint("ck_invoice_items_amounts", "net >= 0 AND discount >= 0 AND discount <= net AND tax >= 0 AND total = net - discount + tax AND total <= 1000000000000");
                    table.CheckConstraint("ck_invoice_items_description", "btrim(description) <> ''");
                    table.CheckConstraint("ck_invoice_items_discount_type", "discount_type IN ('None', 'Percent', 'Amount')");
                    table.CheckConstraint("ck_invoice_items_discount_value", "CASE discount_type WHEN 'None' THEN discount_value = 0 WHEN 'Percent' THEN discount_value > 0 AND discount_value <= 100 WHEN 'Amount' THEN discount_value > 0 ELSE false END");
                    table.CheckConstraint("ck_invoice_items_position", "position > 0");
                    table.CheckConstraint("ck_invoice_items_quantity", "quantity > 0 AND quantity <= 1000000");
                    table.CheckConstraint("ck_invoice_items_tax_rate_percent", "tax_rate_percent BETWEEN 0 AND 100");
                    table.CheckConstraint("ck_invoice_items_unit_price", "unit_price >= 0 AND unit_price <= 1000000000");
                    table.ForeignKey(
                        name: "fk_invoice_items_clinics_clinic_id",
                        column: x => x.clinic_id,
                        principalTable: "clinics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_invoice_items_consultations_clinic_id_consultation_id",
                        columns: x => new { x.clinic_id, x.consultation_id },
                        principalTable: "consultations",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_invoice_items_invoices_clinic_id_invoice_id",
                        columns: x => new { x.clinic_id, x.invoice_id },
                        principalTable: "invoices",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_invoice_items_tax_rates_clinic_id_tax_rate_id",
                        columns: x => new { x.clinic_id, x.tax_rate_id },
                        principalTable: "tax_rates",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_clinics_currency",
                table: "clinics",
                sql: "currency ~ '^[A-Z]{3}$'");

            migrationBuilder.CreateIndex(
                name: "ix_invoice_items_clinic_id_consultation_id",
                table: "invoice_items",
                columns: new[] { "clinic_id", "consultation_id" });

            migrationBuilder.CreateIndex(
                name: "ix_invoice_items_clinic_id_invoice_id_position",
                table: "invoice_items",
                columns: new[] { "clinic_id", "invoice_id", "position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_invoice_items_clinic_id_tax_rate_id",
                table: "invoice_items",
                columns: new[] { "clinic_id", "tax_rate_id" });

            migrationBuilder.CreateIndex(
                name: "ix_invoices_clinic_id_client_id_created_at",
                table: "invoices",
                columns: new[] { "clinic_id", "client_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_invoices_clinic_id_created_at",
                table: "invoices",
                columns: new[] { "clinic_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_invoices_clinic_id_created_by_user_id",
                table: "invoices",
                columns: new[] { "clinic_id", "created_by_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_invoices_clinic_id_issued_by_user_id",
                table: "invoices",
                columns: new[] { "clinic_id", "issued_by_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_invoices_clinic_id_patient_id_created_at",
                table: "invoices",
                columns: new[] { "clinic_id", "patient_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_invoices_clinic_id_voided_by_user_id",
                table: "invoices",
                columns: new[] { "clinic_id", "voided_by_user_id" });

            migrationBuilder.CreateIndex(
                name: "ux_invoices_clinic_number",
                table: "invoices",
                columns: new[] { "clinic_id", "number" },
                unique: true,
                filter: "number IS NOT NULL");

            // One active tax rate per name, ignoring case (ADR-016).
            migrationBuilder.Sql("CREATE UNIQUE INDEX ux_tax_rates_active_name ON tax_rates (clinic_id, lower(name)) WHERE status = 'Active';");

            // Invoices are editable only as drafts. Issuing (Draft -> Issued) is accepted only when the stored totals equal
            // the sums of the lines and there is at least one line; an issued invoice may only become Void, changing
            // nothing but the status, the void fields and updated_at (whole-row comparison, so no amount, number or
            // party can change). Lines follow their invoice: any change only while it is a draft, read FOR SHARE first
            // and tested after (the B-14 lesson), and a line never moves to another invoice (the B-13 lesson).
            migrationBuilder.Sql("""
                CREATE FUNCTION invoices_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE
                    line_count integer;
                    sum_net numeric;
                    sum_discount numeric;
                    sum_tax numeric;
                    sum_total numeric;
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        IF NEW.status <> 'Draft' THEN
                            RAISE EXCEPTION 'an invoice starts as a draft' USING ERRCODE = 'insufficient_privilege';
                        END IF;
                        RETURN NEW;
                    END IF;
                    IF TG_OP = 'DELETE' THEN
                        IF OLD.status = 'Draft' THEN
                            RETURN OLD;
                        END IF;
                        RAISE EXCEPTION 'issued invoices are never deleted; void them instead' USING ERRCODE = 'insufficient_privilege';
                    END IF;
                    IF (NEW.id, NEW.clinic_id) IS DISTINCT FROM (OLD.id, OLD.clinic_id) THEN
                        RAISE EXCEPTION 'an invoice cannot move' USING ERRCODE = 'insufficient_privilege';
                    END IF;
                    IF OLD.status = 'Draft' AND NEW.status = 'Draft' THEN
                        RETURN NEW;
                    END IF;
                    IF OLD.status = 'Draft' AND NEW.status = 'Issued' THEN
                        SELECT count(*), COALESCE(sum(net), 0), COALESCE(sum(discount), 0), COALESCE(sum(tax), 0), COALESCE(sum(total), 0)
                        INTO line_count, sum_net, sum_discount, sum_tax, sum_total
                        FROM invoice_items WHERE invoice_id = NEW.id AND clinic_id = NEW.clinic_id;
                        IF line_count = 0 OR sum_net <> NEW.subtotal OR sum_discount <> NEW.discount_total
                            OR sum_tax <> NEW.tax_total OR sum_total <> NEW.total THEN
                            RAISE EXCEPTION 'an invoice is issued only with lines whose sums equal its totals'
                                USING ERRCODE = 'insufficient_privilege';
                        END IF;
                        RETURN NEW;
                    END IF;
                    IF OLD.status = 'Issued' AND NEW.status = 'Void'
                        AND (to_jsonb(NEW) - 'status' - 'voided_at' - 'voided_by_user_id' - 'void_reason' - 'updated_at')
                          = (to_jsonb(OLD) - 'status' - 'voided_at' - 'voided_by_user_id' - 'void_reason' - 'updated_at') THEN
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'a % invoice cannot be changed this way', OLD.status USING ERRCODE = 'insufficient_privilege';
                END;
                $$;

                CREATE TRIGGER invoices_guard
                    BEFORE INSERT OR UPDATE OR DELETE ON invoices
                    FOR EACH ROW EXECUTE FUNCTION invoices_guard();

                CREATE FUNCTION invoice_items_follow_invoice() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE
                    parent_status text;
                    parent_patient uuid;
                    linked_patient uuid;
                    linked_status text;
                BEGIN
                    IF TG_OP = 'UPDATE' AND (NEW.invoice_id, NEW.clinic_id) IS DISTINCT FROM (OLD.invoice_id, OLD.clinic_id) THEN
                        RAISE EXCEPTION 'an invoice line cannot move to another invoice' USING ERRCODE = 'insufficient_privilege';
                    END IF;
                    SELECT status, patient_id INTO parent_status, parent_patient FROM invoices
                    WHERE id = COALESCE(NEW.invoice_id, OLD.invoice_id) AND clinic_id = COALESCE(NEW.clinic_id, OLD.clinic_id)
                    FOR SHARE;
                    IF parent_status IS NOT NULL AND parent_status <> 'Draft' THEN
                        RAISE EXCEPTION 'lines of a % invoice cannot be changed', parent_status USING ERRCODE = 'insufficient_privilege';
                    END IF;
                    -- A billed consultation must be a non-discarded one of the invoice's patient (locked, then tested).
                    IF TG_OP <> 'DELETE' AND NEW.consultation_id IS NOT NULL
                        AND (TG_OP = 'INSERT' OR NEW.consultation_id IS DISTINCT FROM OLD.consultation_id) THEN
                        SELECT patient_id, status INTO linked_patient, linked_status FROM consultations
                        WHERE id = NEW.consultation_id AND clinic_id = NEW.clinic_id
                        FOR SHARE;
                        IF linked_status = 'Discarded' OR linked_patient IS DISTINCT FROM parent_patient THEN
                            RAISE EXCEPTION 'an invoice line can only bill a non-discarded consultation of the invoice''s patient'
                                USING ERRCODE = 'insufficient_privilege';
                        END IF;
                    END IF;
                    RETURN COALESCE(NEW, OLD); -- a missing parent is left to the foreign key
                END;
                $$;

                CREATE TRIGGER invoice_items_follow_invoice
                    BEFORE INSERT OR UPDATE OR DELETE ON invoice_items
                    FOR EACH ROW EXECUTE FUNCTION invoice_items_follow_invoice();

                -- A rate's percentage never changes and rates are never deleted (lines keep their own copy anyway).
                CREATE FUNCTION tax_rates_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'UPDATE' AND (NEW.id, NEW.clinic_id, NEW.rate_percent) IS NOT DISTINCT FROM (OLD.id, OLD.clinic_id, OLD.rate_percent) THEN
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'tax rates are never deleted and their percentage never changes; archive and create a new one'
                        USING ERRCODE = 'insufficient_privilege';
                END;
                $$;

                CREATE TRIGGER tax_rates_guard
                    BEFORE UPDATE OR DELETE ON tax_rates
                    FOR EACH ROW EXECUTE FUNCTION tax_rates_guard();

                CREATE TRIGGER tax_rates_no_truncate
                    BEFORE TRUNCATE ON tax_rates
                    FOR EACH STATEMENT EXECUTE FUNCTION clinical_items_reject_truncate();

                CREATE TRIGGER invoices_no_truncate
                    BEFORE TRUNCATE ON invoices
                    FOR EACH STATEMENT EXECUTE FUNCTION clinical_items_reject_truncate();

                CREATE TRIGGER invoice_items_no_truncate
                    BEFORE TRUNCATE ON invoice_items
                    FOR EACH STATEMENT EXECUTE FUNCTION clinical_items_reject_truncate();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Never silently destroy financial records.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM invoices WHERE status <> 'Draft') THEN
                        RAISE EXCEPTION 'Refusing to drop invoices: issued invoices are financial records. Archive them first.';
                    END IF;
                END;
                $$;
                """);

            // Drafts may go; the guard trigger would refuse nothing here, but drop it first so the drop is plain.
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS invoice_items_follow_invoice ON invoice_items;
                DROP TRIGGER IF EXISTS invoices_guard ON invoices;
                DROP TRIGGER IF EXISTS invoice_items_no_truncate ON invoice_items;
                DROP TRIGGER IF EXISTS invoices_no_truncate ON invoices;
                DROP FUNCTION IF EXISTS invoice_items_follow_invoice();
                DROP FUNCTION IF EXISTS invoices_guard();
                DROP TRIGGER IF EXISTS tax_rates_guard ON tax_rates;
                DROP TRIGGER IF EXISTS tax_rates_no_truncate ON tax_rates;
                DROP FUNCTION IF EXISTS tax_rates_guard();
                """);

            migrationBuilder.DropTable(
                name: "invoice_items");

            migrationBuilder.DropTable(
                name: "invoice_number_sequences");

            migrationBuilder.DropTable(
                name: "invoices");

            migrationBuilder.DropTable(
                name: "tax_rates");

            migrationBuilder.DropCheckConstraint(
                name: "ck_clinics_currency",
                table: "clinics");

            migrationBuilder.DropColumn(
                name: "currency",
                table: "clinics");
        }
    }
}
