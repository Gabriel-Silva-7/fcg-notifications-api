# Kubernetes

Este serviço mantém seus próprios templates de ConfigMap, Secret, Deployment e
Service. Os placeholders são renderizados pelo `scripts/kubernetes/render.py
--applications` do repositório fcg-orchestration, usando este checkout limpo e seu
SHA completo. Não aplique os templates originais com placeholders.

O pacote final privado contém namespace e referências de configuração/Secrets
específicos da revisão. A orquestração verifica fontes e divergência dos arquivos,
carrega a imagem local identificada e só aplica a fase das aplicações depois dos
rollouts PostgreSQL/RabbitMQ e do Job de topologia Complete. Leia o guia Kubernetes
da orquestração para preparo, contexto/kubeconfig privados e fases do apply.

Deployment executa UID 1654, com startup/liveness em `/health/live` e readiness em
`/health/ready`. Service interno usa porta 80 para 8080. Migrations próprias entram
antes de servir/bus; DB e broker são acessados pelos Services postgres/rabbitmq.
Secrets reais e chaves são gerados fora do Git; ConfigMap contém apenas parâmetros
públicos. Mudança de revisão troca referências no PodTemplate e exige rollout.

Réplicas/limites são do ambiente local exercitado. Persistência está nos PVCs da
infraestrutura; substituir API Pod preserva os bancos e o trabalho durável.
