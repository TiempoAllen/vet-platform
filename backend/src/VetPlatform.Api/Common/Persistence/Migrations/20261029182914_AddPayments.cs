using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VetPlatform.Api.Common.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "amount_paid",
                table: "invoices",
                type: "numeric(19,4)",
                precision: 19,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    clinic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    method = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payments", x => x.id);
                    table.UniqueConstraint("ak_payments_clinic_id_invoice_id_id", x => new { x.clinic_id, x.invoice_id, x.id });
                    table.CheckConstraint("ck_payments_amount", "amount > 0 AND amount <= 1000000000000");
                    table.CheckConstraint("ck_payments_method", "method IN ('Cash', 'Card', 'BankTransfer', 'Cheque', 'Other')");
                    table.CheckConstraint("ck_payments_reference", "reference IS NULL OR btrim(reference) <> ''");
                    table.ForeignKey(
                        name: "fk_payments_clinics_clinic_id",
                        column: x => x.clinic_id,
                        principalTable: "clinics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payments_invoices_clinic_id_invoice_id",
                        columns: x => new { x.clinic_id, x.invoice_id },
                        principalTable: "invoices",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payments_users_clinic_id_received_by_user_id",
                        columns: x => new { x.clinic_id, x.received_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "refunds",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    clinic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    method = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    refunded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    refunded_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refunds", x => x.id);
                    table.CheckConstraint("ck_refunds_amount", "amount > 0 AND amount <= 1000000000000");
                    table.CheckConstraint("ck_refunds_method", "method IN ('Cash', 'Card', 'BankTransfer', 'Cheque', 'Other')");
                    table.CheckConstraint("ck_refunds_reason", "btrim(reason) <> ''");
                    table.ForeignKey(
                        name: "fk_refunds_clinics_clinic_id",
                        column: x => x.clinic_id,
                        principalTable: "clinics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_refunds_payments_clinic_id_invoice_id_payment_id",
                        columns: x => new { x.clinic_id, x.invoice_id, x.payment_id },
                        principalTable: "payments",
                        principalColumns: new[] { "clinic_id", "invoice_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_refunds_users_clinic_id_refunded_by_user_id",
                        columns: x => new { x.clinic_id, x.refunded_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_invoices_amount_paid",
                table: "invoices",
                sql: "amount_paid >= 0 AND amount_paid <= total AND (status = 'Issued' OR amount_paid = 0)");

            migrationBuilder.CreateIndex(
                name: "ix_payments_clinic_id_invoice_id_paid_at",
                table: "payments",
                columns: new[] { "clinic_id", "invoice_id", "paid_at" });

            migrationBuilder.CreateIndex(
                name: "ix_payments_clinic_id_received_by_user_id",
                table: "payments",
                columns: new[] { "clinic_id", "received_by_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_refunds_clinic_id_invoice_id_payment_id",
                table: "refunds",
                columns: new[] { "clinic_id", "invoice_id", "payment_id" });

            migrationBuilder.CreateIndex(
                name: "ix_refunds_clinic_id_refunded_by_user_id",
                table: "refunds",
                columns: new[] { "clinic_id", "refunded_by_user_id" });

            // Payments and refunds are never changed or deleted (ADR-017). A payment needs an issued invoice; the refunds
            // of a payment never exceed it. Parents are read FOR SHARE first and tested after (the B-14 lesson).
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION invoices_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE
                    line_count integer;
                    sum_net numeric;
                    sum_discount numeric;
                    sum_tax numeric;
                    sum_total numeric;
                    paid numeric;
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
                    -- B-17: on an issued invoice only the amount paid may change, and only to what its payments minus
                    -- refunds add up to (the rows are saved before the invoice in the same transaction).
                    IF OLD.status = 'Issued' AND NEW.status = 'Issued'
                        AND (to_jsonb(NEW) - 'amount_paid' - 'updated_at') = (to_jsonb(OLD) - 'amount_paid' - 'updated_at') THEN
                        SELECT COALESCE((SELECT sum(amount) FROM payments WHERE invoice_id = NEW.id AND clinic_id = NEW.clinic_id), 0)
                             - COALESCE((SELECT sum(amount) FROM refunds WHERE invoice_id = NEW.id AND clinic_id = NEW.clinic_id), 0)
                        INTO paid;
                        IF paid = NEW.amount_paid THEN
                            RETURN NEW;
                        END IF;
                        RAISE EXCEPTION 'amount_paid must equal the invoice''s payments minus refunds'
                            USING ERRCODE = 'insufficient_privilege';
                    END IF;
                    IF OLD.status = 'Issued' AND NEW.status = 'Void'
                        AND (to_jsonb(NEW) - 'status' - 'voided_at' - 'voided_by_user_id' - 'void_reason' - 'updated_at')
                          = (to_jsonb(OLD) - 'status' - 'voided_at' - 'voided_by_user_id' - 'void_reason' - 'updated_at') THEN
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'a % invoice cannot be changed this way', OLD.status USING ERRCODE = 'insufficient_privilege';
                END;
                $$;

                CREATE FUNCTION payments_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE
                    parent_status text;
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        SELECT status INTO parent_status FROM invoices
                        WHERE id = NEW.invoice_id AND clinic_id = NEW.clinic_id
                        FOR SHARE;
                        IF parent_status IS NULL OR parent_status = 'Issued' THEN
                            RETURN NEW; -- a missing parent is left to the foreign key
                        END IF;
                        RAISE EXCEPTION 'payments are recorded only against issued invoices' USING ERRCODE = 'insufficient_privilege';
                    END IF;
                    RAISE EXCEPTION 'payments are never changed or deleted; refund them instead' USING ERRCODE = 'insufficient_privilege';
                END;
                $$;

                CREATE TRIGGER payments_guard
                    BEFORE INSERT OR UPDATE OR DELETE ON payments
                    FOR EACH ROW EXECUTE FUNCTION payments_guard();

                CREATE FUNCTION refunds_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE
                    paid numeric;
                    refunded numeric;
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        SELECT amount INTO paid FROM payments
                        WHERE id = NEW.payment_id AND invoice_id = NEW.invoice_id AND clinic_id = NEW.clinic_id
                        FOR SHARE;
                        IF paid IS NULL THEN
                            RETURN NEW; -- left to the foreign key
                        END IF;
                        SELECT COALESCE(sum(amount), 0) INTO refunded FROM refunds
                        WHERE payment_id = NEW.payment_id AND clinic_id = NEW.clinic_id;
                        IF refunded + NEW.amount > paid THEN
                            RAISE EXCEPTION 'refunds cannot exceed their payment' USING ERRCODE = 'insufficient_privilege';
                        END IF;
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'refunds are never changed or deleted' USING ERRCODE = 'insufficient_privilege';
                END;
                $$;

                CREATE TRIGGER refunds_guard
                    BEFORE INSERT OR UPDATE OR DELETE ON refunds
                    FOR EACH ROW EXECUTE FUNCTION refunds_guard();

                CREATE TRIGGER payments_no_truncate
                    BEFORE TRUNCATE ON payments
                    FOR EACH STATEMENT EXECUTE FUNCTION clinical_items_reject_truncate();

                CREATE TRIGGER refunds_no_truncate
                    BEFORE TRUNCATE ON refunds
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
                    IF EXISTS (SELECT 1 FROM payments) THEN
                        RAISE EXCEPTION 'Refusing to drop payments: they are financial records. Archive them first.';
                    END IF;
                END;
                $$;

                DROP TRIGGER IF EXISTS refunds_guard ON refunds;
                DROP TRIGGER IF EXISTS payments_guard ON payments;
                DROP TRIGGER IF EXISTS refunds_no_truncate ON refunds;
                DROP TRIGGER IF EXISTS payments_no_truncate ON payments;
                DROP FUNCTION IF EXISTS refunds_guard();
                DROP FUNCTION IF EXISTS payments_guard();
                """);

            // Restore the B-16 invoice guard.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION invoices_guard() RETURNS trigger LANGUAGE plpgsql AS $$
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
                """);

            migrationBuilder.DropTable(
                name: "refunds");

            migrationBuilder.DropTable(
                name: "payments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_invoices_amount_paid",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "amount_paid",
                table: "invoices");
        }
    }
}
