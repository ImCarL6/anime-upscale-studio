# Preparação antecipada de blocos — 2.0.2

## O que veio da V2

A interface V2 (`a18a58d`) reorganizou fila, trabalho ativo e configuração,
aplicou tema escuro, adicionou arraste de arquivos/pastas e persistência da
visibilidade da configuração. O processamento permaneceu o mesmo.
`b914350` passou a pausar a fila inteira em falta de espaço, removeu a saída
final parcial não recuperável e restringiu o resumo de falhas à execução atual.
`9a8788a` já isolava falhas por arquivo e aceitava avisos benignos de DTS no
muxer descartável de validação. `20c4052` acrescentou somente os documentos
do controle Android em `docs/remote/`; não implementou servidor ou aplicativo.

O segundo chat propôs estimativas locais à pasta/fila e sobreposição da
extração. Nenhuma das duas propostas havia sido implementada nesses commits.
Esta mudança implementa a sobreposição e coleta as fases necessárias para
futuro aprendizado. A reformulação estatística e o Android continuam pendentes.

## Causa confirmada

`BuildLosslessSourceSegmentArguments` abre o original sem seek e usa
`trim=start_frame=…:end_frame=…`. O decodificador percorre os frames anteriores
novamente em cada extração. Assim, o custo de decodificar o prefixo cresce
com a posição, embora só os frames selecionados sejam codificados em HEVC.
Não é um `-ss` colocado depois de `-i`: o comando não usa `-ss`.

O [filtro trim](https://ffmpeg.org/ffmpeg-filters.html#trim) conta frames na
entrada do filtro. Um [seek de entrada](https://ffmpeg.org/ffmpeg.html#Main-options)
exigiria demonstrar correspondência exata de frames com timestamps, time bases,
B-frames e origens que não começam em zero. Não foi introduzido neste patch;
os cortes por frame, overlaps, codecs, qualidade e fingerprint permanecem iguais.

## Desenho implementado

- Apenas no modo Máximo, prepara no máximo uma fonte adiante. O processo
  auxiliar pertence à mesma proteção de CPU/memória e ao mesmo cancelamento.
  Retomadas automáticas por pressão de recursos usam extração serial.
- Usa uma previsão de 300 s ou a fronteira existente se houver cache posterior.
  Essa previsão não escreve no manifesto e não cria um bloco AV1 antecipado.
- Ao chegar à fronteira, `SegmentPlanning.SelectNext` continua sendo a única
  autoridade. Só se aproveita a fonte quando o plano completo coincide,
  incluindo início, fim e overlap. Senão, drena a tarefa e extrai em série.
- Trocar para Assistir/Equilibrado cancela a antecipação. O tamanho do bloco
  em processamento permanece intacto; o próximo considera o modo atual.
- Extrair em segundo plano não altera progresso, estágio ou velocidade
  apresentados para o encoder ativo. A fonte é validada por contagem de frames
  antes de ser disponibilizada; a saída segue a validação final existente.
- Falha auxiliar comum permite nova extração em série. Falta de espaço na
  fonte a consumir propaga para a pausa de disco cheio, preservando os blocos.
  Cancelamento/falha da tentativa sempre encerra e aguarda a tarefa auxiliar.
- Manifestos antigos continuam aceitos; os campos de medição são opcionais.
  O modo registrado é o do início do bloco, não uma classificação de
  interferência nem uma garantia de modo constante durante todo o bloco.
- Após validar e salvar a saída final, remove somente o cache daquele trabalho,
  incluindo blocos e fontes temporárias. Em falhas e cancelamentos, os caches
  permanecem. Preparações abandonadas não são adotadas como checkpoints após
  reiniciar. As medições de fase ficam num pequeno `.segments.json` junto ao log.

## Ganho esperado e riscos

Pelos números fornecidos nos chats, aproximadamente 150 s por episódio eram
gastos em extração; cerca de 20 s pertencem ao primeiro bloco e continuam
visíveis. Com modo Máximo constante, a ordem de grandeza é ocultar até 130 s
por episódio (aproximadamente 7–8% num episódio de 27–31 min), antes de descontar
contenção. Em 39 episódios seriam até 1h24min30s. É uma projeção histórica,
não um benchmark medido da temporada Golden Wind.

A carga de decodificação não diminui: ela passa a coincidir com a IA/AV1.
Há leitura adicional concorrente no disco da origem, escrita no destino,
uso de CPU, mais uma sessão HEVC NVENC e buffers de VRAM. Não se usa NVDEC
explicitamente. Pode piorar o desempenho em disco lento/compartilhado ou com
GPU pressionada. A proteção existente continua ativa; os modos de uso reduzido
não iniciam antecipação. O espaço transitório cresce aproximadamente uma
fonte lossless. Durante a finalização de cada episódio, seus blocos AV1 ainda
coexistem com a saída final; após o sucesso, esse espaço é liberado.

## Validação

`ReleaseChecks` cobre coincidência de plano, fronteira diferente, cancelamento
da fila, cancelamento por redução de modo, drenagem da tarefa auxiliar, propagação
de disco cheio e abandono de tarefa com falha. `ResourceChecks` mantém a
verificação de CPU, monitoramento de memória e recuperação.

`Test-DynamicSegments.ps1 -Package <runtime-isolado> -StableMaximum` usa 480
frames CFR com áudio, verifica extração antecipada, adoção do próximo bloco,
telemetria e validação final de frames, timestamps e áudio.
Acrescente `-CancelDuringPrefetch` para cancelar enquanto a fonte seguinte
é preparada e retomar. Sem `-StableMaximum`, `-CancelAndResume` verifica
5 min → 1 min → cauda equilibrada e preservação dos hashes/datas dos blocos
já concluídos. Os dados desses testes ficam no runtime isolado.

Não substitui benchmark de episódio real, avaliação visual, disco enchendo
fisicamente ou ciclo completo de atualização em Windows limpo.

## Resultado da validação local — 10/11/2026

- Build Release sem avisos/erros; `ReleaseChecks` e `ResourceChecks` aprovados.
- Pipeline real com fixture sintética CFR de 480 frames/8 min: antecipação
  aproveitada, saída 720p → 1440p e 1080p → 2160p validada, incluindo áudio
  e timestamps. A fixture tem 1 fps; não mede o desempenho de anime a 23,976 fps.
- Cancelamento durante antecipação e retomada aprovados. Trocas 5 min → 1 min
  → cauda equilibrada aprovadas, com recuperação sem reescrever os dois
  primeiros blocos concluídos.
- Pacote, feed, download e rejeição de pacote corrompido aprovados. Instalador
  aplicado sobre a instalação local anterior; aplicação reaberta pelo atalho
  com versão 2.0.1, ferramentas NVIDIA disponíveis e sessão restaurada.
- DLL instalada coincide por SHA-256 com a DLL usada no teste de GPU. Dados
  anteriores foram copiados para backup antes da migração da sessão portátil.

Isso valida instalação local e restauração, não download/aplicação pelo
updater entre duas versões nem instalação em Windows limpo.

### Ajuste 2.0.2

A política foi alterada para limpar automaticamente o cache do trabalho após
validar e salvar a saída final. `ReleaseChecks` e o pipeline GPU com cancelamento
durante antecipação/retomada passaram: cache preservado no cancelamento e removido
após sucesso, com medições mantidas junto ao log. Pacote/feed e instalação local
2.0.2 aprovados; a fila pendente permaneceu intacta e a DLL instalada coincide
com a testada. Não foi feita limpeza retroativa de outros caches.
