# Changelog

## Próxima versão

## 0.1.7 — 2026-10-20

- Ajusta dinamicamente os próximos blocos do AnimeJaNai ao modo selecionado: 1 minuto em Trabalhar e assistir, 3 minutos em Equilibrado e 5 minutos em Desempenho máximo. Preserva o bloco atual, as fronteiras dos blocos recuperáveis e a cobertura exata de frames; permite retomar planos mistos e caches fixos da 0.1.6. CPU e cadência continuam ajustáveis durante o bloco.

## 0.1.6 — 2026-10-20

- Valida a entrada antes do upscale: leitura integral dos pacotes, duração real e decodificação de vídeo e todos os áudios; bloqueia arquivo truncado mesmo quando o leitor retorna código zero. Legendas que terminam após o vídeo são aceitas quando explicam a duração anunciada.
- Integra controle de recursos ao vivo: Trabalhar e assistir, Equilibrado e Desempenho máximo. Troca CPU e cadência no mesmo bloco, com proteção de memória e até duas retomadas automáticas.
- A fila normal aceita episódios adicionados durante a execução e os processa sem novo clique em Iniciar. Comandos avançados com caminhos literais continuam restritos a um arquivo por execução.
- Salva fila, ordem, resultados, parâmetros dos itens pendentes e tempo acumulado entre sessões, com gravação atômica e cópia de recuperação. Restaura trabalhos interrompidos como pendentes, sem iniciar sozinho nem repetir concluídos.
- Lembra pasta de saída e diretório da seleção de episódios.
- Padrões: AnimeJaNai, V3.1 Balanced Sharp1, CQ 18 e Equilibrado. Destaca o botão para ativar Desempenho máximo.
- Remove referências a um aparelho específico da interface e apresenta compatibilidade de reprodução de forma geral.
- Corrige o relógio da fila: mostra tempo acumulado em execução e mantém o total após conclusão e entre sessões.



## 0.1.5 — 2026-10-20

- Remove a pasta temporária do trabalho somente após validar e salvar o vídeo final, incluindo os blocos recuperáveis do AnimeJaNai. Preserva trabalhos com falha ou cancelados e registra falhas de limpeza no log.
- Preserva os temporários dos testes de qualidade quando falham ou são cancelados.
- Abre por padrão em AnimeJaNai, V3 Compact Sharp1 FP16 e CQ 18.

- Corrige a leitura UTF-8 dos metadados e logs do FFprobe/FFmpeg: títulos com acentos permanecem intactos no remux e na validação final de legendas, evitando falha após processar todos os blocos.

- Corrige cortes segmentados no limite de frames: seleciona os trechos por índice exato, evitando perder um frame por arredondamento temporal em vídeos CFR fracionários como 23,976 fps.

- Simplifica os parâmetros opcionais: AQ strength 10 e B-adapt visíveis e marcados por padrão; remove os controles indisponíveis.
- Esclarece o comparador com indicação contínua do conteúdo e controle da esquerda (original) para a direita (resultado).
- Renomeia a ação para “Testar um trecho” e explica a comparação de qualidade e as estimativas antes do episódio completo.

- Explica a preparação inicial do TensorRT em um aviso visível com indicador de atividade; informa que pode levar minutos, permite cancelar e reutiliza engines compatíveis.
- Permite reordenar a fila por arraste na alça de cada episódio antes de iniciar; a ordem visual corresponde à ordem da coleção processada.
- Adiciona ações por episódio para abrir o vídeo final e selecionar o arquivo na pasta, habilitadas somente após conclusão bem-sucedida.
- Gera automaticamente comparações do piloto com o mesmo frame de cada trecho: original ampliado sem IA versus resultado, divisória deslizante, zoom e alternância de trechos. Persiste as imagens para reabrir depois e mantém sua geração fora do cronômetro de desempenho.

- Organiza o repositório público com fontes, shaders, CI, documentação e manifesto de dependências, excluindo mídia, dados locais e binários pesados.
- Integra Velopack 1.2.0: consulta releases estáveis ao abrir e a cada seis horas, exibe changelog, baixa sob comando e solicita confirmação para reiniciar.
- Bloqueia atualização durante fila/piloto e evita múltiplas instâncias; mantém dados e engines fora da pasta instalada, com namespace por runtime para preservar caches em updates só do app.
- Adiciona empacotamento versionado self-contained com dependências conferidas por SHA-256 e testes de persistência. Esta entrada não anuncia uma release já publicada.

- Adiciona distribuição portátil Windows x64 com .NET incluído, runtimes Visual C++, builder TensorRT SM120 para RTX 50 e manifesto SHA-256; exclui engines, históricos e caches do computador de origem.
- Prioriza a pasta do executável ao localizar recursos, remove caminho fixo de outra instalação e verifica escrita, carregamento do TensorRT e AV1 NVENC na abertura, com diagnóstico de falhas.
- Remove a identificação fixa RTX 4070 Ti da interface e documenta o primeiro piloto no computador de destino.

- Corrige a comparação de idioma: `und` e tag ausente/vazia representam idioma indefinido, sem dispensar a preservação dos idiomas identificados e dos demais metadados.
- Valida blocos, pilotos e saída segmentada AnimeJaNai conforme a resolução da origem multiplicada por 2; aceita 720p → 1440p e mantém 1080p → 4K. Anime4K continua com alvo 4K.
- Registra cancelamentos e blocos recuperados nos logs e preserva o cache segmentado também após a conclusão, para permitir recuperação até a conferência da reprodução.
- Adiciona recuperação assistida por manifesto em `Resume-ValidatedQueue.ps1`: mantém perfil/CQ de cada item, confere a identidade da origem e a toolchain para retomar blocos incompletos e permite remontar um conjunto já concluído sem misturar segmentos de outras configurações.
- Adiciona fingerprint SHA-256 canônico da toolchain, cobrindo FFmpeg/FFprobe, executáveis e DLLs do AnimeJaNai/TensorRT, modelo ONNX, engine e timing cache selecionados, shader combinado, parâmetros do pipeline, GPU, compute capability, driver e arquitetura do sistema.
- Isola blocos recuperáveis em namespaces `toolchain-<hash>` e persiste o fingerprint nos manifestos, impedindo a mistura de segmentos produzidos por toolchains ou configurações diferentes sem apagar os caches legados.
- Persiste snapshots auditáveis e um cache de hashes verificado por tamanho, data e amostras SHA-256 de início/fim; pilotos só são recuperados após reiniciar quando o fingerprint completo coincide e voltam a preencher tamanho aproximado e ETA.
- Corrige a assinatura estável dos parâmetros beta: execuções não beta agora usam assinatura vazia de forma consistente, recuperando o aprendizado histórico e a aplicação das projeções de piloto no modo estável.
- Adiciona um piloto determinístico de três trechos representativos de 5 segundos que executa o pipeline selecionado, valida a amostra AV1 4K e projeta coeficiente de velocidade, tempo com faixa de ±15%, bitrate de vídeo/final e tamanho do episódio.
- Gera a engine TensorRT antes do cronômetro do piloto, mantém a amostra e um relatório JSON para inspeção e aplica a medição somente ao arquivo e à combinação exata de motor, perfil, CQ e parâmetros beta usados.
- Adiciona `V3 Compact` e `V3 Compact Sharp1` como perfis `Legacy/Experimental` usando cópias ONNX convertidas para FP16 forte; os ONNX FP32 originais permanecem preservados e fora do pipeline.
- Valida os dois Compact FP16 em engines TensorRT com I/O `Half` e em 120 quadros 4K sem corrupção, mantendo um aviso experimental até o piloto longo.
- Torna `V3.1 Balanced` o perfil AnimeJaNai padrão e exige confirmação antes de executar os Compact legados.
