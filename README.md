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
| `Sqs__QueueUrl` / `Sqs__DeadLetterQueueUrl` | Result queue and dead-letter queue |

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
