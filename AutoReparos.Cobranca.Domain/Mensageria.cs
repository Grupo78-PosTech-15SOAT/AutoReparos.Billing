namespace AutoReparos.Cobranca.Domain;

/// <summary>
/// Registro de outbox: mensagem a publicar na fila de entrada da Saga, gravada na mesma transação do fato de negócio.
/// O <see cref="Id"/> é o <c>eventId</c> do envelope e é estável em reenvios.
/// </summary>
public sealed class MensagemSaida
{
    private MensagemSaida() { Tipo = ""; Payload = ""; }
    public MensagemSaida(Guid id, string tipo, string payload, DateTimeOffset criadoEm)
    {
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(tipo) || string.IsNullOrWhiteSpace(payload)) throw new ArgumentException("Mensagem de saída inválida.");
        Id = id; Tipo = tipo; Payload = payload; CriadoEm = criadoEm;
    }
    public Guid Id { get; private set; }
    public string Tipo { get; private set; }
    public string Payload { get; private set; }
    public DateTimeOffset CriadoEm { get; private set; }
    /// <summary>Preenchido quando a mensagem foi aceita pela fila de entrada da Saga.</summary>
    public DateTimeOffset? EnviadoEm { get; private set; }
    /// <summary>Preenchido quando as tentativas esgotaram e a mensagem foi encaminhada à DLQ (com alerta).</summary>
    public DateTimeOffset? EsgotadaEm { get; private set; }
    public int Tentativas { get; private set; }
}

/// <summary>Registro persistido de comandos já tratados, para deduplicar entregas repetidas pelo <c>eventId</c>.</summary>
public sealed class EventoProcessado
{
    private EventoProcessado() { Tipo = ""; }
    public EventoProcessado(Guid id, string tipo, DateTimeOffset processadoEm) { Id = id; Tipo = tipo; ProcessadoEm = processadoEm; }
    public Guid Id { get; private set; }
    public string Tipo { get; private set; }
    public DateTimeOffset ProcessadoEm { get; private set; }
}
