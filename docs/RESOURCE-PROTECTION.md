# Segundo plano v2 — prévia local

## Controle ao vivo — 0.1.6-background.3

A prévia atual está em `C:\anime-studio\background-live-preview`, com atalho próprio
“Anime Upscale Studio - TESTE controle ao vivo”. A prévia v2 anterior e a estável permanecem separadas.

O seletor fica habilitado durante a fila. Trabalhar e assistir aplica CPU 35% e
cadência 0,15x; Equilibrado aplica CPU 75% e 0,25x; Desempenho máximo remove o teto
de CPU e a espera voluntária entre frames. Não usa prioridade alta/realtime do Windows.
A proteção emergencial de memória, reserva de entrada e orçamento de duas retomadas
permanecem ativos em todos os modos. Um vídeo por vez em todos os modos.

O ajuste de CPU altera o mesmo Job Object. A cadência usa filtros FFmpeg `zmq` e
`realtime`, com canal de comandos restrito a 127.0.0.1, um endpoint por processo.
A interface mostra quando o ritmo foi confirmado. A confirmação costuma levar de frações de segundo
a poucos segundos; processos travados, preparação TensorRT ou inferências demoradas
podem atrasar a resposta. Não existe teto exato de GPU/VRAM. O comando avançado
preserva seu texto e recebe somente a troca de CPU/prioridade.

Trocar o modo não apaga caches nem reinicia blocos. Processamentos com troca de
modo recebem marcador próprio no histórico, para não contaminar as estimativas
de velocidade de um modo fixo. Qualidade, CQ, resolução e conteúdo permanecem iguais.

Validação: CPU nativa no mesmo processo 35→75→máximo→35; comandos de cadência
com 200 hashes e timestamps de frames idênticos; troca máximo→limitado→máximo;
troca pelo seletor durante um bloco Compact Sharp1, saída de 48 frames validada,
áudio conferido e temporários removidos. Uso prolongado com YouTube depende do teste manual.

As seções abaixo documentam a prévia v2 anterior; os modos e a cadência fixa foram
substituídos pelo controle ao vivo descrito acima.

Abra `Anime4KEncoder.exe` da pasta desta prévia ou o atalho “Anime Upscale Studio - TESTE segundo plano v2”. A versão estável continua separada.

Padrão: AnimeJaNai, Compact Sharp1 FP16, CQ 18 e “Trabalhar e assistir”. A primeira rodada sugerida é um episódio enquanto trabalha e assiste YouTube. O processamento fica deliberadamente mais lento. A GPU ainda pode apresentar picos de 100%; não há cota rígida de VRAM no backend atual.

## Como funciona

- CPU agregada em 35% no modo padrão ou 50% em “Segundo plano mais rápido”, prioridade baixa de CPU/GPU e um vídeo por vez.
- Cadência de leitura de 0,15x/0,25x no pipe; a contrapressão reduz o ritmo do produtor. Mantém modelo, CQ, frames, resolução e parâmetros finais de qualidade. Não promete teto exato de utilização da GPU.
- Antes do trabalho e de cada bloco: exige 4 GiB de RAM livre, 4 GiB de memória comprometível livre e 1 GiB de VRAM livre em cada GPU monitorada. Aguarda 5 leituras saudáveis, até 5 minutos, em vez de falhar imediatamente. Esses valores são reservas cooperativas, não partições exclusivas de memória.
- Remove o limite rígido de memória do Job Object: esse limite podia provocar o próprio erro de alocação. Não altera o arquivo de paginação nem as configurações globais da GPU.
- Remove os gatilhos de 70%/80% de memória total. A proteção emergencial permanece apenas com RAM/compromisso livres abaixo de 1 GiB ou VRAM livre abaixo de 256 MiB por 12 leituras consecutivas (aproximadamente 3s). Sensores indisponíveis por 5s também encerram a tentativa.
- Blocos AnimeJaNai de 60s, com cache próprio; caches antigos de 300s e mídia original permanecem intactos. A preparação da engine TensorRT ainda pode causar picos e não é limitada pela cadência do pipe.
- Na fila AnimeJaNai segmentada, a proteção encerra apenas a tentativa. Aguarda pelo menos 30s e reserva estável antes de retomar. Máximo de duas retomadas por episódio: ritmo de 0,075x e 0,05x no modo padrão. A terceira falha pausa a fila com explicação. Blocos concluídos são validados e reutilizados; o bloco incompleto é refeito.
- Erros de formato/caminho/validação não entram na retomada; somente pressão de recursos, perda prolongada de monitoramento e mensagens específicas de falta de memória. Cancelar manualmente cancela também a espera e impede retomada.
- Pilotos, Anime4K e comandos avançados não recebem retomada automática por blocos. O modo avançado conserva o comando escrito pelo usuário.
- Temporários são removidos após validação e gravação final, conforme 0.1.5. Falhas/cancelamentos preservam arquivos. Logs registram reservas e retomadas.

## Limites da validação

Testes de CPU real do Windows e herança por processos filhos, ausência do antigo teto de alocação, política de reserva, pressão transitória/persistente simulada, cancelamento e limite de tentativas. Uma conversão sintética curta verifica o pipe e a validação final. Isso não comprova fluidez em episódio completo com navegador aberto; a aprovação depende do uso real.

Não foi publicada nem instalada por cima da versão estável.
