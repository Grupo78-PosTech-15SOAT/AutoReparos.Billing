# AutoReparos Billing

Billing is the isolated service for OS budgets, customer approval, Mercado Pago Checkout Pro, payment notifications, and authorized refunds. It owns its PostgreSQL schema and does not read the OS database.

## Configuration

Set these values through environment variables or a secret manager:

| Setting | Purpose |
| --- | --- |
| `ConnectionStrings__Billing` | Billing PostgreSQL database connection |
| `Authentication__Authority` / `Authentication__Audience` | JWT issuer and audience |
| `MercadoPago__AccessToken` | Mercado Pago private access token |
| `MercadoPago__WebhookSecret` | Webhook signature secret |
| `OsService__BaseUrl` | OS Service base URL used to confirm completion |
| `OsService__BearerToken` | Service identity token authorized to query the OS API |
| `AWS_REGION` | AWS region for SQS |
| `Sqs__QueueUrl` / `Sqs__DeadLetterQueueUrl` | Entry queue of the OS Saga (result and budget events) and its dead-letter queue |
| `Sqs__ComandosQueueUrl` / `Sqs__ComandosDeadLetterQueueUrl` | Command queue consumed by Billing (`cobranca.cancelar-orcamento.v1`, plus legacy alias `billing.orcamento.cancelar.v1`) and its dead-letter queue |
| `Outbox__IntervaloMs` | Optional: polling interval of the outbox dispatcher in milliseconds (default 2000) |

The service applies EF Core migrations at startup. The Billing database and role must be provisioned separately with no access to OS tables. Never commit tokens or connection strings.

## Routes

- `POST /api/v1/orcamentos` receives an OS budget from the `ServicoOS` identity, or an administrator/receptionist.
- `POST /api/v1/orcamentos/{id}/aprovacao` records customer approval.
- `POST /api/v1/pagamentos/{id}/checkout` opens Checkout Pro only after approval and OS completion.
- `POST /api/v1/pagamentos/{id}/reembolsos` records an administrator-approved partial or full refund.
- `POST /api/v1/webhooks/mercado-pago` validates the signed notification and fetches the authoritative payment state from Mercado Pago.

Build and verify locally from this directory:

```sh
dotnet restore AutoReparos.Billing.slnx
dotnet build AutoReparos.Billing.slnx -m:1
dotnet test AutoReparos.Billing.Tests --no-build
dotnet format AutoReparos.Billing.slnx --verify-no-changes --no-restore
```

## Contrato de mensagens da Saga (v1)

O Billing participa da Saga orquestrada pelo OS Service apenas por mensagens SQS (JSON, chaves em camelCase). O contrato completo está em `.specs/features/fase4/saga-mensageria/contracts.md` do repositório principal; aqui fica o que o Billing publica e consome. Todo envelope tem `eventId`, `tipo`, `ordemServicoId` e `ocorreuEm` (UTC, ISO 8601); `correlacaoId` só aparece quando o Billing o conhece (nas respostas ao comando de cancelamento, ecoado do comando).

### Saída (fila de entrada da Saga, `Sqs__QueueUrl`)

| `tipo` | Quando | Campos extras |
| --- | --- | --- |
| `orcamento.aprovado.v1` | o cliente aprova o orçamento | `orcamentoId`, `clienteId` |
| `pagamento.resultado.v1` | webhook do Mercado Pago muda o estado do pagamento | `pagamentoId`, `status` (`Capturado`, `Rejeitado`, `Cancelado`, `Expirado`, `Reembolsado`), `referencia` |
| `orcamento.cancelado.v1` | comando de cancelamento aceito | `orcamentoId` |
| `orcamento.cancelamento-recusado.v1` | comando recusado ou inválido | `orcamentoId`, `motivo` |

- **Outbox.** As mensagens de `orcamento.aprovado.v1` e das respostas de cancelamento são gravadas na tabela `mensagens_saida` na mesma transação do fato de negócio. Um despachador em segundo plano as publica; ao iniciar ele retoma as pendentes. Cada mensagem tem 5 tentativas com backoff exponencial e jitter; ao esgotar, vai para `Sqs__DeadLetterQueueUrl`, é marcada como esgotada e gera o alerta `billing.sqs.exhausted` (`billing.sqs.dlq_failed` se nem a DLQ aceitar; nesse caso ela continua pendente). A aprovação HTTP responde 200 mesmo com o SQS fora do ar.
- **`pagamento.resultado.v1`** continua publicado direto pelo fluxo do webhook, com recuperação para frente: se a publicação falha, o webhook responde 503 `resultado_nao_publicado`, alerta, e a reentrega do Mercado Pago republica. O `eventId` é estável por pagamento + status + referência.
- O `eventId` é estável em reenvios; a Saga deduplica. A entrega é pelo menos uma vez.

### Entrada (fila de comandos, `Sqs__ComandosQueueUrl`)

| `tipo` | Campos | Efeito |
| --- | --- | --- |
| `cobranca.cancelar-orcamento.v1` (e alias legado de entrada `billing.orcamento.cancelar.v1`) | `eventId`, `ordemServicoId`, `orcamentoId`, `motivo`, `correlacaoId` | cancela o orçamento se `Pendente`, ou `Aprovado` sem pagamento iniciado; novo estado `Cancelado` |

- **Deduplicação.** O `eventId` processado é registrado em `eventos_processados` na mesma transação do efeito e da resposta; uma entrega repetida (inclusive concorrente) não repete o efeito nem a resposta.
- **Recusa.** Pagamento com checkout iniciado, capturado ou reembolsado, e orçamento `Recusado`, recusam com `motivo` `pagamento_existente` ou `orcamento_recusado`. Cancelar um orçamento já cancelado confirma de novo (idempotente). **Nenhum caminho de cancelamento chama estorno nem o provedor de pagamento.**
- **Mensagem inválida.** Tipo desconhecido, `eventId` ausente, orçamento inexistente ou `ordemServicoId` divergente geram alerta (`billing.comando.tipo_desconhecido`, `billing.comando.malformado`, `billing.comando.orcamento_inexistente`, `billing.comando.ordem_divergente`) e a resposta `orcamento.cancelamento-recusado.v1`. Sem `orcamentoId` e `ordemServicoId` válidos (ou JSON inválido) não há para quem responder: alerta `billing.comando.malformado` e DLQ de comandos.
- **Retry e DLQ.** Falha técnica no processamento usa 5 tentativas com backoff e jitter (um escopo novo por tentativa); ao esgotar, alerta `billing.comando.esgotado` e a mensagem vai para `Sqs__ComandosDeadLetterQueueUrl`. Se nem a DLQ aceitar, alerta `billing.comando.dlq_failed` e a mensagem não é apagada da fila (volta após o visibility timeout). Sem `Sqs__ComandosQueueUrl` o consumo fica desativado.

A migração `202611130001_AddMensageriaSaga` cria `mensagens_saida` e `eventos_processados` (escrita à mão, sem model snapshot, como as demais).
