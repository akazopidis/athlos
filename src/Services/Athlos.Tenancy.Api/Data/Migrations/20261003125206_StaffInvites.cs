using System;
using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Athlos.Tenancy.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class StaffInvites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "outbox_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    queue = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "staff_invites",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    token_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_staff_invites", x => x.id);
                    table.ForeignKey(
                        name: "fk_staff_invites_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_occurred_at",
                table: "outbox_messages",
                column: "occurred_at",
                filter: "sent_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_staff_invites_tenant_id_email",
                table: "staff_invites",
                columns: new[] { "tenant_id", "email" },
                unique: true,
                filter: "accepted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_staff_invites_tenant_id_token_hash",
                table: "staff_invites",
                columns: new[] { "tenant_id", "token_hash" },
                unique: true);

            // Layer 4 (design §3.2). staff_invites: the workspace's own rows only. The invitee is not a member,
            // so accepting enters the workspace first (AcceptInvite); reads are never widened.
            migrationBuilder.AddTenantIsolationPolicy("staff_invites");

            // As in ordering and enrollment: the outbox has no tenant-scoped reads, only tenancy-api's role reaches
            // this database, and the publisher runs without a tenant. RLS on with an explicit allow-all policy.
            migrationBuilder.EnableRowLevelSecurity("outbox_messages");
            migrationBuilder.Sql("CREATE POLICY service_only ON outbox_messages USING (true) WITH CHECK (true);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropTable(
                name: "staff_invites");
        }
    }
}
