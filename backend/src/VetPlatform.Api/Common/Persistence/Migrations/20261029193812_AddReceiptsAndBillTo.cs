using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VetPlatform.Api.Common.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReceiptsAndBillTo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_invoices_lifecycle",
                table: "invoices");

            migrationBuilder.AddColumn<string>(
                name: "bill_to_address_line1",
                table: "invoices",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bill_to_address_line2",
                table: "invoices",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bill_to_city",
                table: "invoices",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bill_to_country",
                table: "invoices",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bill_to_name",
                table: "invoices",
                type: "character varying(201)",
                maxLength: 201,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bill_to_patient_name",
                table: "invoices",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bill_to_postal_code",
                table: "invoices",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bill_to_region",
                table: "invoices",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "ak_refunds_clinic_id_payment_id_id",
                table: "refunds",
                columns: new[] { "clinic_id", "payment_id", "id" });

            migrationBuilder.CreateTable(
                name: "receipt_number_sequences",
                columns: table => new
                {
                    clinic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    next_number = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_receipt_number_sequences", x => x.clinic_id);
                    table.CheckConstraint("ck_receipt_number_sequences_next_number", "next_number > 0");
                    table.ForeignKey(
                        name: "fk_receipt_number_sequences_clinics_clinic_id",
                        column: x => x.clinic_id,
                        principalTable: "clinics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "receipts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    clinic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    refund_id = table.Column<Guid>(type: "uuid", nullable: true),
                    clinic_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    invoice_number = table.Column<int>(type: "integer", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    invoice_total = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    method = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    amount_paid_after = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    balance_due_after = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    issued_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issued_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_receipts", x => x.id);
                    table.CheckConstraint("ck_receipts_amounts", "amount > 0 AND amount <= 1000000000000 AND amount_paid_after >= 0 AND amount_paid_after <= invoice_total AND balance_due_after = invoice_total - amount_paid_after");
                    table.CheckConstraint("ck_receipts_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_receipts_kind", "kind IN ('Payment', 'Refund')");
                    table.CheckConstraint("ck_receipts_method", "method IN ('Cash', 'Card', 'BankTransfer', 'Cheque', 'Other')");
                    table.CheckConstraint("ck_receipts_names", "btrim(clinic_name) <> '' AND btrim(issued_by_name) <> ''");
                    table.CheckConstraint("ck_receipts_number", "number > 0 AND invoice_number > 0");
                    table.CheckConstraint("ck_receipts_refund", "(kind = 'Refund') = (refund_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_receipts_clinics_clinic_id",
                        column: x => x.clinic_id,
                        principalTable: "clinics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_receipts_payments_clinic_id_invoice_id_payment_id",
                        columns: x => new { x.clinic_id, x.invoice_id, x.payment_id },
                        principalTable: "payments",
                        principalColumns: new[] { "clinic_id", "invoice_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_receipts_refund_clinic_id_payment_id_refund_id",
                        columns: x => new { x.clinic_id, x.payment_id, x.refund_id },
                        principalTable: "refunds",
                        principalColumns: new[] { "clinic_id", "payment_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_receipts_users_clinic_id_issued_by_user_id",
                        columns: x => new { x.clinic_id, x.issued_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql("""
                ALTER TABLE invoices DISABLE TRIGGER invoices_guard;
                UPDATE invoices i
                SET bill_to_name = c.first_name || ' ' || c.last_name,
                    bill_to_address_line1 = c.address_line1, bill_to_address_line2 = c.address_line2, bill_to_city = c.city,
                    bill_to_region = c.region, bill_to_postal_code = c.postal_code, bill_to_country = c.country
                FROM clients c
                WHERE i.status <> 'Draft' AND c.id = i.client_id AND c.clinic_id = i.clinic_id;
                UPDATE invoices i SET bill_to_patient_name = p.name
                FROM patients p
                WHERE i.status <> 'Draft' AND p.id = i.patient_id AND p.clinic_id = i.clinic_id;
                ALTER TABLE invoices ENABLE TRIGGER invoices_guard;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "ck_invoices_lifecycle",
                table: "invoices",
                sql: "CASE status WHEN 'Draft' THEN number IS NULL AND issued_at IS NULL AND due_on IS NULL AND issued_by_user_id IS NULL AND bill_to_name IS NULL AND bill_to_patient_name IS NULL AND bill_to_address_line1 IS NULL AND bill_to_address_line2 IS NULL AND bill_to_city IS NULL AND bill_to_region IS NULL AND bill_to_postal_code IS NULL AND bill_to_country IS NULL AND voided_at IS NULL AND voided_by_user_id IS NULL AND void_reason IS NULL WHEN 'Issued' THEN number IS NOT NULL AND issued_at IS NOT NULL AND due_on IS NOT NULL AND issued_by_user_id IS NOT NULL AND bill_to_name IS NOT NULL AND (patient_id IS NULL) = (bill_to_patient_name IS NULL) AND voided_at IS NULL AND voided_by_user_id IS NULL AND void_reason IS NULL WHEN 'Void' THEN number IS NOT NULL AND issued_at IS NOT NULL AND due_on IS NOT NULL AND issued_by_user_id IS NOT NULL AND bill_to_name IS NOT NULL AND (patient_id IS NULL) = (bill_to_patient_name IS NULL) AND voided_at IS NOT NULL AND voided_at >= issued_at AND voided_by_user_id IS NOT NULL AND void_reason IS NOT NULL AND btrim(void_reason) <> '' ELSE false END");

            migrationBuilder.CreateIndex(
                name: "ix_receipts_clinic_id_invoice_id_number",
                table: "receipts",
                columns: new[] { "clinic_id", "invoice_id", "number" });

            migrationBuilder.CreateIndex(
                name: "ix_receipts_clinic_id_invoice_id_payment_id",
                table: "receipts",
                columns: new[] { "clinic_id", "invoice_id", "payment_id" });

            migrationBuilder.CreateIndex(
                name: "ix_receipts_clinic_id_issued_by_user_id",
                table: "receipts",
                columns: new[] { "clinic_id", "issued_by_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_receipts_clinic_id_payment_id_refund_id",
                table: "receipts",
                columns: new[] { "clinic_id", "payment_id", "refund_id" });

            migrationBuilder.CreateIndex(
                name: "ix_receipts_payment_id",
                table: "receipts",
                column: "payment_id",
                unique: true,
                filter: "kind = 'Payment'");

            migrationBuilder.CreateIndex(
                name: "ix_receipts_refund_id",
                table: "receipts",
                column: "refund_id",
                unique: true,
                filter: "refund_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_receipts_clinic_number",
                table: "receipts",
                columns: new[] { "clinic_id", "number" },
                unique: true);

            // Receipts are never changed or deleted (ADR-018); a new one must state its payment's or refund's own amount
            // and method, read FOR SHARE first and tested after.
            migrationBuilder.Sql("""
                CREATE FUNCTION receipts_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE
                    source_amount numeric;
                    source_method text;
                    source_reference text;
                    source_at timestamptz;
                    inv_number integer;
                    inv_currency text;
                    inv_total numeric;
                    next_receipt integer;
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        IF NEW.refund_id IS NULL THEN
                            SELECT amount, method, reference, paid_at INTO source_amount, source_method, source_reference, source_at
                            FROM payments
                            WHERE id = NEW.payment_id AND invoice_id = NEW.invoice_id AND clinic_id = NEW.clinic_id FOR SHARE;
                        ELSE
                            SELECT amount, method, NULL, refunded_at INTO source_amount, source_method, source_reference, source_at
                            FROM refunds
                            WHERE id = NEW.refund_id AND payment_id = NEW.payment_id AND clinic_id = NEW.clinic_id FOR SHARE;
                        END IF;
                        IF source_amount IS NULL THEN
                            RETURN NEW; -- left to the foreign keys
                        END IF;
                        SELECT number, currency, total INTO inv_number, inv_currency, inv_total FROM invoices
                        WHERE id = NEW.invoice_id AND clinic_id = NEW.clinic_id;
                        SELECT next_number INTO next_receipt FROM receipt_number_sequences WHERE clinic_id = NEW.clinic_id;
                        -- Everything the receipt copies from its sources must match them, and its number must come from
                        -- the clinic's sequence (already handed out), so a hand-made receipt cannot misstate or skip.
                        IF source_amount <> NEW.amount OR source_method <> NEW.method
                            OR source_reference IS DISTINCT FROM NEW.reference OR source_at <> NEW.occurred_at
                            OR inv_number IS DISTINCT FROM NEW.invoice_number OR inv_currency IS DISTINCT FROM NEW.currency
                            OR inv_total IS DISTINCT FROM NEW.invoice_total
                            OR next_receipt IS NULL OR NEW.number >= next_receipt THEN
                            RAISE EXCEPTION 'a receipt states exactly its payment''s or refund''s and invoice''s details and a number from the sequence'
                                USING ERRCODE = 'insufficient_privilege';
                        END IF;
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'receipts are never changed or deleted' USING ERRCODE = 'insufficient_privilege';
                END;
                $$;

                CREATE TRIGGER receipts_guard
                    BEFORE INSERT OR UPDATE OR DELETE ON receipts
                    FOR EACH ROW EXECUTE FUNCTION receipts_guard();

                CREATE TRIGGER receipts_no_truncate
                    BEFORE TRUNCATE ON receipts
                    FOR EACH STATEMENT EXECUTE FUNCTION clinical_items_reject_truncate();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Never silently destroy financial records; issued invoices keep their bill-to block until then.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM receipts) OR EXISTS (SELECT 1 FROM invoices WHERE status <> 'Draft') THEN
                        RAISE EXCEPTION 'Refusing to drop receipts and bill-to blocks: they are financial records. Archive them first.';
                    END IF;
                END;
                $$;

                DROP TRIGGER IF EXISTS receipts_guard ON receipts;
                DROP TRIGGER IF EXISTS receipts_no_truncate ON receipts;
                DROP FUNCTION IF EXISTS receipts_guard();
                """);

            migrationBuilder.DropTable(
                name: "receipt_number_sequences");

            migrationBuilder.DropTable(
                name: "receipts");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_refunds_clinic_id_payment_id_id",
                table: "refunds");

            migrationBuilder.DropCheckConstraint(
                name: "ck_invoices_lifecycle",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "bill_to_address_line1",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "bill_to_address_line2",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "bill_to_city",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "bill_to_country",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "bill_to_name",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "bill_to_patient_name",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "bill_to_postal_code",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "bill_to_region",
                table: "invoices");

            migrationBuilder.AddCheckConstraint(
                name: "ck_invoices_lifecycle",
                table: "invoices",
                sql: "CASE status WHEN 'Draft' THEN number IS NULL AND issued_at IS NULL AND due_on IS NULL AND issued_by_user_id IS NULL AND voided_at IS NULL AND voided_by_user_id IS NULL AND void_reason IS NULL WHEN 'Issued' THEN number IS NOT NULL AND issued_at IS NOT NULL AND due_on IS NOT NULL AND issued_by_user_id IS NOT NULL AND voided_at IS NULL AND voided_by_user_id IS NULL AND void_reason IS NULL WHEN 'Void' THEN number IS NOT NULL AND issued_at IS NOT NULL AND due_on IS NOT NULL AND issued_by_user_id IS NOT NULL AND voided_at IS NOT NULL AND voided_at >= issued_at AND voided_by_user_id IS NOT NULL AND void_reason IS NOT NULL AND btrim(void_reason) <> '' ELSE false END");
        }
    }
}
