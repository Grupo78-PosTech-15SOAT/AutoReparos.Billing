using AutoReparos.Billing.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AutoReparos.Billing.Infrastructure.Migrations;

[DbContext(typeof(BillingDbContext))]
[Migration("202611120001_AddPagamentoTentativas")]
public sealed class AddPagamentoTentativas : Migration
{
    // Tentativas conta checkouts efetivamente iniciados. Regra para linhas existentes: Status <> 0 (SemTentativa)
    // significa que um checkout ja foi iniciado, entao viram 1; as demais ficam 0.
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>("Tentativas", "pagamentos", type: "integer", nullable: false, defaultValue: 0);
        migrationBuilder.Sql("UPDATE pagamentos SET \"Tentativas\" = 1 WHERE \"Status\" <> 0;");
    }
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropColumn("Tentativas", "pagamentos");
}
