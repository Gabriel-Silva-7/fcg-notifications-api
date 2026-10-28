# FCG Notifications

Serviço .NET 10 de projeção de identidade e, nos próximos incrementos, notificações
simuladas no console. Mantém banco próprio: não consulta o banco de Users.
Este incremento recebe eventos de identidade; ainda não emite e-mails simulados.

## Executar

Requer SDK **10.0.400** (global.json sem roll-forward), PostgreSQL 16, RabbitMQ e
Docker acessível para os testes Testcontainers. Configure por ambiente ou provedor
externo de segredos:

| Variável | Comportamento |
|---|---|
| `ConnectionStrings__Default` | Banco exclusivo Notifications, obrigatório |
| `RabbitMq__Host` | Host/IP do broker, obrigatório, sem URL com credenciais |
| `RabbitMq__Port` | AMQP, padrão 5672 |
| `RabbitMq__VirtualHost` | `/fcg`, obrigatório |
| `RabbitMq__Username` / `RabbitMq__Password` | Credencial própria, obrigatória, sem valor padrão |
| `Queues__Identity` | Fila própria durável; padrão `fcg-notifications-identity` |
| `Database__MigrateOnStartup` | `true` aplica migrations antes de atender; padrão `false` |

Use role PostgreSQL própria, sem superuser/criação de bancos/roles. Para migrar,
a role precisa criar os objetos no seu banco. O usuário AMQP opera somente no
vhost configurado, sem tag administrativa; não compartilhar uma fila de consumo
com Catalog. A topologia de implantação deve existir antes das publicações de Users.

```sh
dotnet restore FCG.Notifications.sln --locked-mode
dotnet build FCG.Notifications.sln -c Release --no-restore
dotnet test FCG.Notifications.sln -c Release --no-restore
dotnet run --project src/FCG.Notifications -c Release --no-build --no-launch-profile --urls http://127.0.0.1:8080
```

## Identidade e conflitos

MassTransit 8.5.11 consome `UserCreatedEvent`, `UserStateChangedEvent` e
`UserStateSnapshotEvent`, namespace `FCG.Contracts`, na fila de identidade própria.
Payloads seguem o contrato canônico: contato normalizado, enums nomeados, GUIDs e
versão válidos, UTC de seis casas decimais. Não recebem senha, hash ou JWT.

A aplicação serializa por UserId com lock transacional PostgreSQL e aplica a
política de projeção na mesma transação da gravação. Versão maior atualiza o
snapshot completo; menor ou equivalente mantém a projeção. Excluído não volta a
ativo/bloqueado. Divergência da mesma versão e restauração incompatível preservam
o estado anterior e gravam `MessageConflicts` para diagnóstico, com IDs, payload,
razão, datas e contagem. O consumo só termina com sucesso depois desse commit.
Não se corrige payload nem se apaga estado automaticamente para forçar replay.

`UserContacts` preserva contato inclusive depois da exclusão lógica, conforme a
política de notificações pendentes. Proteja o banco, backups, fila principal e
fila de erro: eles contêm contato. Nomes não recebem um limite adicional ao do
contrato de eventos. Não há FK nem leitura de dados de outro serviço.

Erros transitórios recebem até três novas tentativas (1s, 5s, 15s), depois seguem
para a fila durável `_error`; payload inválido não aplica estado. Interrupção antes
do commit deixa a mensagem recuperável, e reentregas não revertem a projeção.
Snapshot de recuperação é estado, sem inventar fato de cadastro ou boas-vindas.
O processamento de cadastro atrasado será ligado à intenção Welcome separadamente.

## Saúde e logs

- `/health/live`: anônimo, 200 `Healthy`, independente das dependências.
- `/health/ready`: schema, estado do bus e conectividade/autenticação AMQP;
  saudável 200, dependência indisponível/degraded/unhealthy 503.
- `dotnet FCG.Notifications.dll --healthcheck`: probe HTTP local na porta 8080,
  timeout de três segundos, retorno 0/1, sem abrir servidor.

A API pode iniciar com broker fora e consumir o backlog quando ele voltar.
Migrations dependem do banco disponível antes da partida quando habilitadas.
Readiness retorna descrições fixas sem exceções. Fora de Development, logs brutos
de MassTransit e eventos EF com exceção/stack são suprimidos. Conflitos registram
somente IDs/versão; não habilite logging de dados sensíveis, payload ou credenciais.

## Container e testes

O Dockerfile usa build multi-stage, SDK/runtime fixados por digest, porta 8080 e
usuário `app` (UID 1654). Passe as variáveis da tabela por configuração externa;
o arquivo de ambiente não deve entrar no contexto de build.

```sh
docker --context orbstack build -t fcg-notifications:local .
container=$(docker --context orbstack run -d -p 127.0.0.1::8080 \
  --env-file /caminho/externo/notifications.env fcg-notifications:local)
docker --context orbstack port "$container" 8080/tcp
docker --context orbstack exec "$container" dotnet FCG.Notifications.dll --healthcheck
# Remova somente o recurso criado acima:
docker --context orbstack rm -f "$container"
```

A conexão configurada precisa alcançar PostgreSQL/RabbitMQ pelo container.
Os testes usam bancos/roles e filas isolados, PostgreSQL/RabbitMQ reais, verificam
ordem, duplicatas, conflito durável, concorrência, restart e payload inválido em
`_error`, além dos contratos/probes herdados. Este incremento não comprova envio de
boas-vindas, confirmação, compra distribuída, Compose completo ou Kubernetes.
