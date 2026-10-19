# Implantação Kubernetes de Notifications

Os manifestos serão adicionados e validados na etapa de implantação. Este repositório ainda contém somente o host inicial e seu Dockerfile.

A futura implantação deve usar porta 8080, liveness em `/health/live` e readiness em `/health/ready`. A readiness atual cobre apenas o bootstrap; banco, broker e consumidores ainda não estão conectados.
