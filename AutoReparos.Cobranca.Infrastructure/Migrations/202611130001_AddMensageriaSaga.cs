using AutoReparos.Cobranca.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AutoReparos.Cobranca.Infrastructure.Migrations;

[DbContext(typeof(BillingDbContext))]
[Migration("202611130001_AddMensageriaSaga")]
public sealed class AddMensageriaSaga : Migration
{
    // mensagens_saida: outbox das mensagens para a fila de entrada da Saga (gravadas na transação do fato de negócio).
    // eventos_processados: eventIds de comandos já tratados, para deduplicar entregas repetidas.
    // StatusOrcamento.Cancelado (3) reaproveita a coluna integer existente; nenhuma linha antiga muda.
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("mensagens_saida", table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            Tipo = table.Column<string>(type: "text", nullable: false),
            Payload = table.Column<string>(type: "text", nullable: false),
            CriadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            EnviadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            EsgotadaEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            Tentativas = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
        }, constraints: table => table.PrimaryKey("PK_mensagens_saida", x => x.Id));
        migrationBuilder.CreateIndex("IX_mensagens_saida_pendentes", "mensagens_saida", "CriadoEm",
            filter: "\"EnviadoEm\" IS NULL AND \"EsgotadaEm\" IS NULL");
        migrationBuilder.CreateTable("eventos_processados", table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            Tipo = table.Column<string>(type: "text", nullable: false),
            ProcessadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_eventos_processados", x => x.Id));
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("eventos_processados"); migrationBuilder.DropTable("mensagens_saida");
    }
}
