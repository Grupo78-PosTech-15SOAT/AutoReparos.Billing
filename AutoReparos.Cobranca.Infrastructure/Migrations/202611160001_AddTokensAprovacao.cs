using AutoReparos.Cobranca.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AutoReparos.Cobranca.Infrastructure.Migrations;

// Link de aprovação do cliente (feature web-adaptacao): um token por orçamento, guardado só como hash SHA-256, com validade e uso único.
[DbContext(typeof(BillingDbContext))]
[Migration("202611160001_AddTokensAprovacao")]
public sealed class AddTokensAprovacao : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("tokens_aprovacao", table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            OrcamentoId = table.Column<Guid>(type: "uuid", nullable: false),
            Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
            CriadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            ExpiraEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            UsadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_tokens_aprovacao", x => x.Id);
            table.ForeignKey("FK_tokens_aprovacao_orcamentos_OrcamentoId", x => x.OrcamentoId, "orcamentos", "Id", onDelete: ReferentialAction.Cascade);
        });
        migrationBuilder.CreateIndex("IX_tokens_aprovacao_Hash", "tokens_aprovacao", "Hash", unique: true);
        migrationBuilder.CreateIndex("IX_tokens_aprovacao_OrcamentoId", "tokens_aprovacao", "OrcamentoId", unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("tokens_aprovacao");
}
