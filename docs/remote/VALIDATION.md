# Controle remoto — validação

Nada é dado como pronto só porque compila. Cada fase marca seus itens com evidência
(saída do teste, captura ou log).

## Automático — desktop (`tests/RemoteChecks`, console no padrão do repositório)

Usa `Microsoft.AspNetCore.TestHost` com uma implementação falsa de `IQueueRemote`.

| ID | Verificação |
|----|-------------|
| A1 | Sem token → 401 em todas as rotas, exceto `POST /pair` |
| A2 | Token errado, revogado ou de tamanho diferente → 401; comparação em tempo constante |
| A3 | Pareamento: código válido uma vez; expira em 5 min; 5 erros bloqueiam; fechado fora da janela → 403 |
| A4 | O PC guarda só o hash do token; `remote.json` não contém o token em texto |
| A5 | Endereço de escuta configurado é exatamente `127.0.0.1` (falha se mudar para `0.0.0.0`, `*`, `::` ou IP da LAN) |
| A6 | Acesso remoto desligado → 503 em tudo |
| A7 | Política de caminhos recusa: `..`, `../`, `..\\`, caminho absoluto, `C:`, UNC `\\server`, `\\?\`, `%2e%2e`, link simbólico/junction para fora da raiz, extensão não permitida |
| A8 | Lista de rotas registradas é exatamente a do API.md (nenhuma rota extra) |
| A9 | `start` com Modo avançado ou perfil Legacy → 409 com `reason`, fila não inicia |
| A10 | `start` repetido com a mesma `Idempotency-Key` → mesma resposta, um único início |
| A11 | Limite de taxa → 429 com `Retry-After` |
| A12 | Logs e respostas de erro não contêm token, código nem caminho absoluto |
| A13 | `ReleaseChecks` existentes continuam passando |

## Automático — Android

| ID | Verificação |
|----|-------------|
| B1 | ViewModels (painel, fila, adicionar) com repositório falso: estados carregando/erro/sem conexão |
| B2 | `TokenStore`: grava e lê cifrado; apagar remove; nada em texto claro no DataStore |
| B3 | Parser do QR recusa esquema/host inválidos e `http://` |
| B4 | Compose UI: iniciar/parar pedem confirmação; botões com ≥ 48 dp; textos longos com reticências |
| B5 | `network_security_config` bloqueia tráfego claro (teste instrumentado) |

## Manual — ponta a ponta (celular real)

Preparar uma cópia portátil separada (como `%LOCALAPPDATA%\AnimeUpscaleStudio2`) com uma fila
de teste de vídeos curtos, para não mexer na fila real.

| ID | Cenário | Esperado |
|----|---------|----------|
| M1 | Celular no Wi-Fi de casa, parear por QR | Pareado; painel mostra a fila |
| M2 | **Wi-Fi desligado, dados móveis** | Status, iniciar e parar funcionam |
| M3 | Outro Wi-Fi (ex.: de um celular roteador) | Igual a M2 |
| M4 | Tailscale desligado no celular | App mostra "Ative o Tailscale"; nada funciona |
| M5 | De outro aparelho na mesma LAN: `http://<ip-do-pc>:47820/api/v1/status` | Conexão recusada |
| M6 | `nmap`/teste de porta externo no IP público | Nenhuma porta do app aberta |
| M7 | Revogar o celular no PC | Próxima chamada → 401; app volta para Parear |
| M8 | Iniciar pelo celular | PC inicia sem abrir diálogo; ao terminar, nenhum diálogo bloqueante |
| M9 | Parar pelo celular no meio de um bloco | Episódio "Cancelado", blocos preservados; iniciar de novo retoma |
| M10 | Trocar Uso do PC pelo celular durante a fila | Muda ao vivo, igual ao seletor do PC |
| M11 | Adicionar 3 episódios pelo celular durante a fila | Entram na fila e são processados sem novo clique |
| M12 | Disco de saída cheio (pasta de teste num pendrive pequeno) | App mostra "Pausada: disco sem espaço" |
| M13 | Fechar o app desktop | App mostra PC offline, sem travar |
| M14 | Fonte grande do Android e tela pequena | Sem cortes nem rolagem horizontal |

## Critério de aceite final

R1–R6 do [PLAN.md](PLAN.md) atendidos, A1–A13 e B1–B5 passando, M1–M14 marcados com
evidência, CHANGELOG atualizado em "Próxima versão" e revisão de segurança (`/security-review`)
sem achados abertos de severidade alta.
