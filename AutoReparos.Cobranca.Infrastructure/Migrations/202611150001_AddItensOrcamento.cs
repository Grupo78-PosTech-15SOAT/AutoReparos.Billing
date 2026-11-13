using AutoReparos.Cobranca.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AutoReparos.Cobranca.Infrastructure.Migrations;

// Snapshot dos itens do orçamento (feature insumos): o preço unitário lido da Execução fica gravado e não muda com reajustes do catálogo.
[DbContext(typeof(BillingDbContext))]
[Migration("202611150001_AddItensOrcamento")]
public sealed class AddItensOrcamento : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("itens_orcamento", table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            OrcamentoId = table.Column<Guid>(type: "uuid", nullable: false),
            Tipo = table.Column<int>(type: "integer", nullable: false),
            ReferenciaId = table.Column<Guid>(type: "uuid", nullable: false),
            Descricao = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
            Quantidade = table.Column<int>(type: "integer", nullable: false),
            PrecoUnitario = table.Column<decimal>(type: "numeric(18,2)", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_itens_orcamento", x => x.Id);
            table.ForeignKey("FK_itens_orcamento_orcamentos_OrcamentoId", x => x.OrcamentoId, "orcamentos", "Id", onDelete: ReferentialAction.Cascade);
        });
        migrationBuilder.CreateIndex("IX_itens_orcamento_OrcamentoId", "itens_orcamento", "OrcamentoId");
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("itens_orcamento");
}
