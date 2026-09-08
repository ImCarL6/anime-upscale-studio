# Changelog

## Próxima versão

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
