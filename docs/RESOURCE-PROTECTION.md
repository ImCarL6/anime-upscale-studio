# Controle de recursos durante a fila

O modo padrão é **Equilibrado**. O seletor continua disponível durante o processamento, e o botão **Desempenho máximo** libera o ritmo quando o computador estiver disponível.

| Modo | CPU | Ritmo voluntário de vídeo |
|---|---|---|
| Trabalhar e assistir | até 35% | 0,15x |
| Equilibrado | até 75% | 0,25x |
| Desempenho máximo | sem teto adicional | sem espera voluntária |

A troca ajusta o processo atual sem reiniciar o bloco. A confirmação aparece na interface; preparação de engines ou inferências demoradas podem atrasá-la. O comando avançado recebe a alteração de CPU/prioridade, preservando seu texto.

Esses controles reduzem a disputa por recursos, mas não reservam uma porcentagem exata de GPU ou VRAM. A inferência ainda pode produzir picos. O aplicativo não usa prioridade alta ou tempo real do Windows. Um vídeo é processado por vez.

A reserva de memória e a proteção emergencial continuam em todos os modos. Pressão sustentada interrompe somente a tentativa atual; o programa espera recuperação, respeita intervalo mínimo de 30 segundos e tenta novamente com ritmo menor, no máximo duas vezes. Cancelamento manual não inicia retomada. Blocos já validados são preservados.

Qualidade, resolução e CQ não mudam ao trocar o modo. O histórico identifica execuções com troca de ritmo para manter estimativas coerentes. A seleção é lembrada entre sessões.

Os testes cobrem troca de CPU no mesmo processo, comandos de cadência, identidade de frames e retomada limitada. Responsividade prolongada com navegador e episódios completos depende de avaliação em uso real.
