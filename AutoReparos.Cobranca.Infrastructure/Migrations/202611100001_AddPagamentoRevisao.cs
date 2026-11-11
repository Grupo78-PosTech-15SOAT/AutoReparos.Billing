using AutoReparos.Cobranca.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AutoReparos.Cobranca.Infrastructure.Migrations;

[DbContext(typeof(BillingDbContext))]
[Migration("202611100001_AddPagamentoRevisao")]
public sealed class AddPagamentoRevisao : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<int>("Revisao", "pagamentos", type: "integer", nullable: false, defaultValue: 0);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropColumn("Revisao", "pagamentos");
}
