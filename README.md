# FCG Notifications

Serviço .NET 10 de projeção de identidade e intenções duráveis de notificações
simuladas no console. Mantém banco próprio: não consulta o banco de Users.
Consome identidade e resultados de pagamento, mantém intenções próprias e emite
boas-vindas/confirmação simuladas no console por um dispatcher recuperável.

## Executar

Requer SDK **10.0.400** (global.json sem roll-forward), PostgreSQL 16, RabbitMQ e
Docker acessível para os testes Testcontainers. Configure por ambiente ou provedor
externo de segredos:

| Variável | Comportamento |
|---|---|
| `ConnectionStrings__Default` | Banco exclusivo Notifications, obrigatório |
| `PGGSSENCMODE` | Perfil local: `disable`, passado ao Npgsql para autenticação por senha sem dependência Kerberos; não altera `SslMode` |
| `RabbitMq__Host` | Host/IP do broker, obrigatório, sem URL com credenciais |
| `RabbitMq__Port` | AMQP, padrão 5672 |
| `RabbitMq__VirtualHost` | `/fcg`, obrigatório |
| `RabbitMq__Username` / `RabbitMq__Password` | Credencial própria, obrigatória, sem valor padrão |
| `Queues__Payments` | Fila própria de resultados; padrão `fcg-notifications-payments`, distinta da fila de identidade |
| `Queues__Identity` | Fila própria durável; padrão `fcg-notifications-identity` |
| `Dispatcher__PollSeconds` | Pulso do worker, padrão1, intervalo1–60s |
| `Dispatcher__LeaseSeconds` | Posse recuperável, padrão30, intervalo1–3600s |
| `Dispatcher__BackoffSeconds` | Espera após falha de emissão, padrão5, intervalo1–3600s |
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

## Intenção de boas-vindas

`UserCreatedEvent` válido registra uma única intenção `Welcome` por usuário em
`NotificationIntents`, na mesma transação da projeção. A unicidade permanente usa
`Kind` e `BusinessId`, independentemente de MessageId/EventId, restart ou concorrência.
Uma divergência de identidade registra conflito e não prepara boas-vindas.

Cadastro atrasado usa o contato mais recente já projetado, inclusive de usuário
excluído; snapshot isolado nunca cria boas-vindas. Ao preparar a intenção como
`Ready`, nome, e-mail e versão do contato ficam congelados. Mudanças posteriores
não redirecionam a intenção já preparada. Falha antes do commit desfaz projeção e
intenção juntas, permitindo reentrega. O dispatcher emite a intenção e só então confirma o progresso.

## Resultados e confirmação de compra

A fila própria de pagamentos recebe `PaymentProcessedEvent` canônico. A primeira
observação válida de cada OrderId fica em `PaymentObservations`, inclusive Rejected.
O lock transacional por pedido protege a comparação e a gravação; reentrega
equivalente com outro MessageId não gera nova intenção. Divergência em usuário,
jogo, preço, status, razão ou instante preserva a primeira observação e registra
`MessageConflicts` antes de confirmar o consumo. Não se recalcula pagamento aqui.

Approved persiste uma `PurchaseConfirmation` única por OrderId, atomicamente com a
observação, com UserId/GameId/Price originais. Rejected não cria confirmação.
Contato conhecido, inclusive Deleted, prepara Ready e congela destinatário/versão.
Sem contato, fica PendingContact sem destinatário inventado. Eventos de identidade
preparam pendências existentes com o contato efetivo daquela transação.
`PendingContactReconciler` também recupera pendências sem novo evento; o worker
hospedado o aciona em cada pulso antes de despachar até25 intenções.
Sua seleção ignora contatos ainda ausentes para evitar que bloqueiem outros pedidos.

Novos diagnósticos preservam o tipo concreto da mensagem e o payload original.
O campo vazio em diagnósticos anteriores à migration significa procedência
indeterminada; não se infere cadastro de snapshot. Replay operacional é oferecido pela CLI local descrita abaixo; não há endpoint HTTP de recuperação. Migrations acrescentam observações/metadata e ampliam a chave do
conflito com o tipo, preservando registros existentes.

## Dispatcher e recuperação

O worker inicia automaticamente. `Ready` recebe uma posse transacional `Sending`
com token, versão e expiração; `NotificationAttempts` registra a tentativa antes
da emissão. A disputa usa lock por IntentId e revalida o estado depois de adquiri-lo.
Duas réplicas compartilham apenas o banco Notifications; uma posse válida é
registrada por vez. Relógios UTC das réplicas devem estar sincronizados.

A saída contém `[E-MAIL SIMULADO]`, tipo, IntentId, AttemptId, UserId e, para compra,
OrderId/GameId/Price. O destinatário preserva somente a primeira letra local
(quando houver mais de uma) e o domínio, como `p***@example.com`; entrada inválida
é totalmente mascarada. Nome e endereço completo não são registrados.
É uma simulação no console, sem servidor de e-mail ou envio externo. Mantenha
a categoria `FCG.Notifications.Delivery.ConsoleNotificationSink` em Information
e o provedor console habilitado. Se o logger estiver desabilitado, a tentativa
falha e mantém backoff em vez de registrar Sent sem emissão.

Somente depois do logger retornar o worker marca `Sent`, exigindo token/versão
atuais e posse ainda válida. Falha de emissão conserva a intenção com backoff e
razão fixa, sem texto da exceção. Interrupção/cancelamento ou falha no banco deixa
`Sending` recuperável após expiração. A tentativa abandonada fica no histórico.
Dono obsoleto não confirma nem altera posse nova. `Sent` não é despachada novamente.

Console e PostgreSQL não formam uma transação. Crash depois do log e antes de Sent,
ou atraso além da posse durante a emissão, pode repetir a saída: o mesmo IntentId
permite correlacionar, e cada tentativa tem AttemptId distinto. A garantia é de
intenção única e retomada com emissão pelo menos uma vez quando as dependências
voltam; não se promete exatamente uma linha. Pendências sem contato continuam
pendentes, sem marcar envio fictício. Não remover linhas/ledger para forçar replay.

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
`_error`, intenções únicas, contato congelado e rollback atômico, além dos
contratos/probes herdados. A compra distribuída em Compose e Kubernetes é verificada separadamente pela orquestração.

Os testes anteriores removem somente o worker hospedado para inspecionar contratos
dos incrementos de persistência. Os cenários do dispatcher ativam o worker real ou
controlam relógio e pulsos com PostgreSQL real, ledger e captura do logger real.

## Operação local de recuperação

O executável da imagem também aceita comandos locais com apenas a conexão do
próprio banco. Esse modo não inicia HTTP, broker, dispatcher, migrations ou seed.
O schema deve ser provisionado pelo procedimento normal do ambiente.

```sh
dotnet FCG.Notifications.dll recover-notifications conflicts --limit 20
dotnet FCG.Notifications.dll recover-notifications conflict --id <conflict-guid>
dotnet FCG.Notifications.dll recover-notifications replay --id <conflict-guid> --dry-run
dotnet FCG.Notifications.dll recover-notifications replay --id <conflict-guid> --apply
dotnet FCG.Notifications.dll recover-notifications intents --limit 20
dotnet FCG.Notifications.dll recover-notifications intent --id <intent-guid>
dotnet FCG.Notifications.dll recover-notifications resume --id <intent-guid> --apply
```

Prévia é o padrão: snapshot read-only, sem efeito, contador ou registro de tentativa.
A previsão pode mudar antes da aplicação. Os comandos `conflicts`/`conflict` e `intents`/`intent` exibem metadados de conflito,
estado e tentativas, sem payload, nome/e-mail de destinatário ou corpo de mensagem.
O replay valida tipo/hash/identificadores/versão e chama a mesma regra do consumidor.
Efeito, resolução e RecoveryAttempt confirmam juntos; erro no commit reverte tudo.
Conflito persistente mantém o primeiro terminal e seu diagnóstico. Uma confirmação
existente sem a observação correspondente é restauração inconsistente: permanece
em conflito até restaurar a observação original, sem escolher outro terminal. Não editar payload
ou apagar um terminal para escolher outra decisão. Restauração é um procedimento
separado do dono, a partir de fonte consistente.

Resume pode preparar PendingContact se o contato já existe, ou antecipar o retry
de Ready em backoff. Não modifica o destinatário de Ready. Lease ativo retorna
LeaseActive; expirado continua disponível ao dispatcher normal, que registra a
tentativa abandonada antes de tomar nova posse. Sent retorna AlreadySent. O comando
não emite e-mail nem escreve Sent: a entrega continua pelo dispatcher, com a mesma
garantia de console ao menos uma vez em falhas. Tentativas do operador ficam em
RecoveryAttempts, separadas das tentativas de entrega em NotificationAttempts.

Códigos de saída:0 sucesso/estado já atendido;1 falha segura;2 argumento inválido;
3 alvo ausente;4 conflito, contato ausente ou lease ativo. Snapshots de identidade
não geram Welcome; um fato de cadastro atrasado continua sujeito à deduplicação
por usuário. `_error` usa redrive de transporte separado, sem substituir o replay
local de um conflito já confirmado. A migração RecoveryOperations e sua tabela
fazem parte da readiness.

[Manifestos Kubernetes próprios](k8s/README.md) para renderização e implantação pela orquestração.

## Ambiente integrado

A [orquestração](https://github.com/Gabriel-Silva-7/fcg-orchestration) prepara fontes limpas, credenciais locais, banco e broker; seus guias descrevem Compose, Kind, jornada real e recuperação. Use as revisões selecionadas da combinação, pois o README não certifica acesso público nem integração em `main`. Este repositório contém apenas o código e os dados de sua responsabilidade.
