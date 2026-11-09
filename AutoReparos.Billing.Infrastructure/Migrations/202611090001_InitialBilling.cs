using AutoReparos.Billing.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AutoReparos.Billing.Infrastructure.Migrations;

[DbContext(typeof(BillingDbContext))]
[Migration("202611090001_InitialBilling")]
public sealed class InitialBilling : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("orcamentos", table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            OrdemServicoId = table.Column<Guid>(type: "uuid", nullable: false),
            ClienteId = table.Column<Guid>(type: "uuid", nullable: false),
            Valor = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
            CriadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            Status = table.Column<int>(type: "integer", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_orcamentos", x => x.Id));
        migrationBuilder.CreateIndex("IX_orcamentos_OrdemServicoId", "orcamentos", "OrdemServicoId", unique: true);
        migrationBuilder.CreateTable("pagamentos", table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            OrcamentoId = table.Column<Guid>(type: "uuid", nullable: false),
            Valor = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
            Status = table.Column<int>(type: "integer", nullable: false),
            ResultadoPublicado = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
            UrlCheckout = table.Column<string>(type: "text", nullable: true),
            ReferenciaProvedor = table.Column<string>(type: "text", nullable: true)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_pagamentos", x => x.Id);
            table.ForeignKey("FK_pagamentos_orcamentos_OrcamentoId", x => x.OrcamentoId, "orcamentos", "Id", onDelete: ReferentialAction.Cascade);
        });
        migrationBuilder.CreateIndex("IX_pagamentos_OrcamentoId", "pagamentos", "OrcamentoId", unique: true);
        migrationBuilder.CreateTable("reembolsos", table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            PagamentoId = table.Column<Guid>(type: "uuid", nullable: false),
            Valor = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
            Aprovador = table.Column<string>(type: "text", nullable: false),
            Referencia = table.Column<string>(type: "text", nullable: false),
            CriadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_reembolsos", x => x.Id);
            table.ForeignKey("FK_reembolsos_pagamentos_PagamentoId", x => x.PagamentoId, "pagamentos", "Id", onDelete: ReferentialAction.Cascade);
        });
        migrationBuilder.CreateIndex("IX_reembolsos_PagamentoId_Referencia", "reembolsos", new[] { "PagamentoId", "Referencia" }, unique: true);
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("reembolsos"); migrationBuilder.DropTable("pagamentos"); migrationBuilder.DropTable("orcamentos");
    }
}
