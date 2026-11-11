using AutoReparos.Cobranca.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AutoReparos.Cobranca.Infrastructure.Migrations;

[DbContext(typeof(BillingDbContext))]
[Migration("202611110001_AddReembolsoEstado")]
public sealed class AddReembolsoEstado : Migration
{
    // 0 = Pendente, 1 = Confirmado. Linhas existentes ja foram enviadas ao provedor: viram Confirmado.
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<int>("Estado", "reembolsos", type: "integer", nullable: false, defaultValue: 1);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropColumn("Estado", "reembolsos");
}
