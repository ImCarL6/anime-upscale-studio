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

## Funcionalidades de fila e comparação — 2026-09-08

- Build e testes de persistência/alinhamento passaram.
- Reordenação muda a coleção processada e fica bloqueada com uma fila ativa.
- Ações de abrir saída são habilitadas somente em sucesso, com caminho final e sem processamento ativo.
- Piloto real V3.1 Balanced com entrada sintética 720p/24 fps: três trechos,
  15 segundos, saída 1440p, seis imagens PNG e reload das projeções passaram.
- Quadros originais 86, 240 e 394 da comparação foram confrontados com extração
  direta da fonte: diferença máxima RGB zero nos três pares. É validação temporal,
  não avaliação de qualidade do modelo com anime real.
- O comparador passou nos extremos da divisória, troca de frame e carregamento do
  relatório persistido; interface renderizada e inspecionada localmente.
- Primeira engine foi construída na GPU local. O aviso de preparação não entra
  no cronômetro de inferência e a geração das imagens acontece depois da medição.
- Candidato local 0.1.1 empacotado com Velopack. Delta de 296.908 bytes aplicado
  ao pacote 0.1.0 reconstruiu o pacote completo 0.1.1 com SHA-256 idêntico.
  Nenhuma release pública foi publicada.
