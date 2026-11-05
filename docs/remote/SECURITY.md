# Controle remoto — segurança

Princípio: **o celular controla a fila, nunca o PC.** Mesmo com o token roubado, um
atacante só poderia ver a fila, iniciar, parar e adicionar vídeos das pastas autorizadas.
E para usar o token, ele ainda precisaria estar dentro da sua tailnet.

## Camadas

| Camada | Controle | Protege contra |
|--------|----------|----------------|
| Rede | Tailscale (WireGuard); nenhuma porta aberta no roteador; **Funnel desligado** | Scanners e acessos pela internet |
| Rede | ACL do Tailscale: só o celular (tag/usuário) alcança o PC na porta 443 | Outros aparelhos da tailnet |
| Transporte | HTTPS do `tailscale serve` com certificado `*.ts.net` válido; o app recusa HTTP | Interceptação e Wi-Fi público |
| Escuta | Kestrel em `127.0.0.1:47820` apenas; nunca `0.0.0.0`, `::` ou IP da LAN | Acesso direto pela rede local |
| Ativação | Acesso remoto desligado por padrão; indicador visível no PC quando ligado | Exposição esquecida |
| Autenticação | Token de dispositivo de 256 bits (`Authorization: Bearer`); PC guarda só SHA-256; comparação em tempo constante | Uso sem pareamento |
| Pareamento | Código de uso único no QR, validade 5 min, 5 tentativas e bloqueio; só com a janela de pareamento aberta no PC | Pareamento por terceiros |
| Identidade de rede | Opcional: exigir que o cabeçalho `Tailscale-User-Login` (adicionado pelo `serve`) seja o seu login | Outro usuário da tailnet |
| Autorização | API sem comandos, sem Modo avançado, sem caminhos livres, sem mudar configurações do encoder | Execução remota de código |
| Arquivos | Somente pastas autorizadas; caminho relativo canonizado; recusa `..`, caminho absoluto, UNC, `\\?\`, links simbólicos e junctions que saiam da raiz; só `.mkv/.mp4/.webm` | Travessia de diretório e leitura de arquivos do sistema |
| Abuso | Limite de 60 req/min por aparelho; 100 itens por adição; corpo máx. 64 KB | Força bruta e sobrecarga |
| Auditoria | Log `logs/remote.log` com aparelho, ação e resultado (sem token) | Investigação |
| Revogação | Lista de aparelhos no PC com "Revogar"; revogar invalida na hora | Celular perdido |

## Modelo de ameaças

| Ameaça | Exemplo | Resposta |
|--------|---------|----------|
| Atacante na internet | Varredura de IP | Nada escuta fora de 127.0.0.1; o Tailscale não expõe nada sem Funnel |
| Alguém no mesmo Wi-Fi do PC | Tenta `http://192.168.x.x:47820` | Conexão recusada (escuta em loopback) |
| Celular perdido ou roubado | Abre o app | Bloqueio de tela do Android, token no Keystore; revogar no PC e remover o aparelho da tailnet |
| Token copiado de backup | Backup do Android | `android:allowBackup="false"` e chave do Keystore não exportável |
| Programa malicioso no próprio PC | Chama `127.0.0.1:47820` | Precisa de token válido; se o PC já está comprometido, este app não é a fronteira |
| Pedido forjado | `POST /queue/items` com `..\..\Windows` | Política de caminhos recusa e registra |
| Repetição em rede ruim | O app reenvia "iniciar" | `Idempotency-Key` em POST; iniciar já ativo responde 409 sem efeito |
| Navegador apontado para a API | CSRF | Sem cookies, sem CORS; token só no cabeçalho |

## Regras para implementação (não negociáveis)

1. O endereço de escuta vem de uma constante `127.0.0.1`, nunca de configuração editável.
   Há um teste que falha se mudar.
2. Tokens e códigos usam `RandomNumberGenerator`. Comparação com `CryptographicOperations.FixedTimeEquals`.
3. Nada de token, código de pareamento ou caminho completo de usuário em logs ou mensagens de erro.
4. Erros da API são genéricos (`{"error":"forbidden"}`); o detalhe vai para o log local.
5. Nenhum endpoint executa processos, abre o Explorer, lê conteúdo de arquivos ou muda
   `OutputDirectory`, motor, modelo, CQ ou Modo avançado.
6. Dados do host (aparelhos, hashes, pastas autorizadas) ficam em `UserData/data/remote.json`.
   Ficam fora da pasta instalada, com gravação atômica como `SessionStore`.
7. O app Android usa `network_security_config` sem tráfego claro e TLS do sistema
   (sem certificado próprio, sem `TrustAll`).
8. Nada de segredo, keystore de assinatura ou `google-services.json` no Git (AGENTS.md).

## Configuração recomendada do Tailscale

- Instalar no PC e no celular com a mesma conta; ativar MagicDNS e HTTPS Certificates no
  painel.
- `tailscale serve --bg 47820` publica `https://<pc>.<tailnet>.ts.net` → `http://127.0.0.1:47820`
  (confirmar a sintaxe da versão instalada com `tailscale serve --help`).
- Nunca rodar `tailscale funnel`.
- ACL mínima (exemplo para conta pessoal):

```json
{
  "tagOwners": { "tag:upscale-pc": ["autogroup:admin"] },
  "acls": [
    { "action": "accept", "src": ["autogroup:member"], "dst": ["tag:upscale-pc:443"] }
  ]
}
```

- Ativar "key expiry" para o celular e revisar a lista de aparelhos periodicamente.
