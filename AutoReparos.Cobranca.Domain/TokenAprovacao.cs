using System.Security.Cryptography;
using System.Text;

namespace AutoReparos.Cobranca.Domain;

/// <summary>
/// Token de aprovação por link: o orçamento tem no máximo um, e só o hash SHA-256 do token é guardado.
/// Um novo pedido de link renova o mesmo registro e, com isso, invalida o token anterior.
/// </summary>
public sealed class TokenAprovacao
{
    public const int BytesDoToken = 32;
    public static readonly TimeSpan Validade = TimeSpan.FromDays(7);
    private const int TamanhoDoTexto = 43; // 32 bytes em base64url, sem preenchimento

    private TokenAprovacao() { Hash = ""; }
    public TokenAprovacao(Guid orcamentoId, string hash, DateTimeOffset agora)
    {
        if (orcamentoId == Guid.Empty) throw new ArgumentException("Orçamento inválido.");
        Id = Guid.NewGuid(); OrcamentoId = orcamentoId; Hash = hash; CriadoEm = agora; ExpiraEm = agora + Validade;
    }
    public Guid Id { get; private set; }
    public Guid OrcamentoId { get; private set; }
    public string Hash { get; private set; }
    public DateTimeOffset CriadoEm { get; private set; }
    public DateTimeOffset ExpiraEm { get; private set; }
    public DateTimeOffset? UsadoEm { get; private set; }

    public bool Valido(DateTimeOffset agora) => UsadoEm is null && ExpiraEm > agora;
    public void Renovar(string hash, DateTimeOffset agora) { Hash = hash; CriadoEm = agora; ExpiraEm = agora + Validade; UsadoEm = null; }
    public void MarcarUsado(DateTimeOffset agora) { if (!Valido(agora)) throw new InvalidOperationException("Token de aprovação não está válido."); UsadoEm = agora; }

    /// <summary>Gera o token em claro (256 bits, base64url) que só é devolvido uma vez ao atendente.</summary>
    public static string GerarToken() => Base64Url(RandomNumberGenerator.GetBytes(BytesDoToken));
    public static string CalcularHash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    /// <summary>Formato esperado do token; o que não tem esse formato nem chega ao banco.</summary>
    public static bool FormatoValido(string? token) =>
        token is { Length: TamanhoDoTexto } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
