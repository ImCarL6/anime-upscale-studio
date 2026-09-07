# Validação da integração inicial — 2026-09-07

| Verificação | Resultado |
|---|---|
| Build Release Windows x64 | Passou, zero avisos/erros |
| Parsing dos scripts PowerShell | Passou |
| Persistência entre versões do app | Passou com fixtures sintéticas |
| Invalidação por troca de runtime | Passou; caches anteriores preservados |
| Build de instalador Velopack 0.1.0 | Passou, self-contained, sem assinatura |
| Reconhecimento de VelopackApp.Run no entrypoint | Passou |
| Hook `--veloapp-install 0.1.0` | Saiu com código 0 |
| EXE real com diretório de trabalho fora do pacote | Abriu e respondeu |
| Startup da cópia final com PATH limitado ao Windows | Passou: TensorRT, NVENC, seis modelos e SM120 |
| Consulta/download do feed local com TestVelopackLocator | Passou |
| Versão atual não recebe update de si mesma | Passou |
| Pacote corrompido | Rejeitado |

Setup e pacotes foram gerados localmente. Nenhuma release pública foi criada por
esta validação. Não foi executado o ciclo completo instalar→atualizar→reiniciar em
uma instalação limpa do Windows. Esse teste continua necessário antes de anunciar
o updater como validado de ponta a ponta.

Os testes anteriores do pipeline portátil incluíram Anime4K e um piloto AnimeJaNai
real na RTX 4070 Ti. Não são evidência de execução na RTX 5070, nem de benchmark
ou validação do instalador naquela placa.
