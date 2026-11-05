# Controle remoto pelo Android — plano

Status: planejamento (nada implementado). Base: branch `ui/v2` (interface 2.0).

## Objetivo

Um app Android que, de qualquer rede (Wi-Fi de casa, outro Wi-Fi ou dados móveis):

| # | Requisito | Prioridade |
|---|-----------|-----------|
| R1 | Ver o status da fila (episódio atual, progresso, ETA, itens) | Obrigatório |
| R2 | Iniciar e parar a fila | Obrigatório |
| R3 | Adicionar episódios que já estão no PC à fila | Desejado |
| R4 | Funcionar fora de casa | Obrigatório |
| R5 | Não permitir que terceiros controlem ou acessem o PC | Obrigatório |
| R6 | Interface feita para celular | Obrigatório |

Fora do escopo: enviar vídeos do celular para o PC (arquivos de vários GB pelos dados
móveis), assistir aos vídeos pelo app, mudar o pipeline de upscale, controlar qualquer
coisa além da fila.

## Arquitetura recomendada

```
Celular Android                     Rede privada Tailscale (WireGuard)              PC
┌──────────────────┐   HTTPS   ┌──────────────────────────────┐   HTTP local  ┌───────────────────────────┐
│ App (Kotlin +    │ ────────► │ tailscale serve               │ ────────────► │ Anime Upscale Studio      │
│ Jetpack Compose) │           │ https://<pc>.<tailnet>.ts.net │  127.0.0.1    │  Host remoto (Kestrel)    │
│ token no Keystore│ ◄──────── │ só aparelhos da sua conta     │   :47820      │  → fila (MainWindow)      │
└──────────────────┘           └──────────────────────────────┘               └───────────────────────────┘
```

1. **Rede: Tailscale.** O PC não abre porta no roteador e não fica visível na internet.
   O celular só alcança o PC quando está na sua rede Tailscale (tailnet), em qualquer
   Wi-Fi ou nos dados móveis. O `tailscale serve` publica o serviço apenas para a
   tailnet, com HTTPS e certificado válido (`*.ts.net`). **Funnel fica proibido**:
   ele exporia o serviço à internet.
2. **Host remoto dentro do app desktop.** É um servidor HTTP (ASP.NET Core Kestrel) que
   escuta **apenas em 127.0.0.1**. Ele só é alcançável pelo próprio PC e pelo
   `tailscale serve`. Fica desligado por padrão e é ligado em Configuração → Acesso remoto.
3. **Pareamento por QR.** O PC mostra um QR com o endereço e um código de uso único
   (5 minutos). O app troca o código por um token de dispositivo de 256 bits, guardado
   no Android Keystore. No PC fica só o hash. Cada aparelho pode ser revogado.
4. **API mínima.** Só expõe o que o app precisa (status, iniciar, parar, modo de uso do PC,
   navegar nas pastas autorizadas, adicionar episódios). Não há endpoint para comandos,
   modo avançado, caminhos livres ou configuração do encoder.
5. **App Android nativo.** Kotlin, Jetpack Compose, Material 3, tema escuro igual ao desktop.
   Atualiza o status a cada 3 s enquanto está aberto.

Detalhes: [SECURITY.md](SECURITY.md) (ameaças e controles), [API.md](API.md) (contrato),
[ANDROID.md](ANDROID.md) (app), [VALIDATION.md](VALIDATION.md) (testes),
[PROMPTS.md](PROMPTS.md) (prompts para os próximos chats).

### Por que estas escolhas

- **Tailscale e não redirecionamento de porta.** Uma porta aberta no roteador é
  encontrada por scanners em minutos. Com Tailscale, o serviço não existe para quem está
  fora da sua conta. A segurança não depende só do código do app. É grátis para uso
  pessoal e funciona em CGNAT/4G sem configurar o roteador.
- **Plano B, se não quiser Tailscale:** Cloudflare Tunnel + Cloudflare Access (login por
  e-mail/Google antes de chegar ao PC). Exige um domínio próprio e mais configuração.
  O host e o app não mudam; só a camada de rede.
- **Kestrel e não `HttpListener`.** Permite testar a API inteira sem rede
  (`Microsoft.AspNetCore.TestHost`), tem limitação de taxa e JSON prontos, e não precisa
  de permissão de administrador para escutar em 127.0.0.1.
- **App nativo e não página web.** O token fica protegido no Keystore, a leitura do QR é
  nativa e há notificações (fase 3). Uma página web servida pelo PC seria mais simples,
  mas guardaria o token no navegador.
- **Adicionar arquivos navegando nas pastas do PC.** Os episódios já estão nos discos do PC
  (`T:\Animes`, `D:\Animes`…). O app lista as **pastas autorizadas** que você cadastra no PC,
  você marca os episódios e o PC os adiciona à fila como se fosse "Adicionar arquivos".
  Nada é enviado do celular.

## Mudanças necessárias no desktop

O controle da fila hoje está em `MainWindow` e depende de caixas de diálogo
(`Start_Click` usa `MessageBox` para validar). Para controle remoto:

1. **Extrair a lógica de iniciar** para `TryStartQueue(QueueStartOrigin origin)`, que
   retorna um resultado (`Started`, `Empty`, `NoPending`, `InvalidCq`, `NeedsConfirmation`,
   `MissingShaders`, `AdvancedModeLocal`) em vez de abrir diálogos. O botão do PC mostra
   o diálogo a partir do resultado; o remoto devolve o motivo em JSON.
   Perfis Legacy/Experimental e o Modo avançado **não iniciam remotamente**.
2. **Avisos ao fim da fila** (falhas, disco cheio) não podem travar o PC esperando clique
   quando a fila foi iniciada remotamente: viram status consultável pela API e aviso na
   linha de status.
3. **Identificador estável por item**: hash do caminho normalizado, para o app referenciar
   episódios entre atualizações.
4. **Fachada `IQueueRemote`** implementada em `MainWindow.Remote.cs`, sempre executada no
   thread da interface (`Dispatcher.InvokeAsync`). O host remoto só conhece essa interface.
5. **Interface no PC:** seção "Acesso remoto" no painel Configuração (ligar/desligar,
   endereço, "Parear celular" com QR, lista de aparelhos com revogar, pastas autorizadas)
   e um indicador "Acesso remoto ativo" na barra superior.

Estrutura proposta no repositório:

```
src/AnimeStudio.Remote/         biblioteca net10.0: contratos, pareamento, tokens,
                                política de pastas, endpoints, limitação de taxa
src/Anime4KEncoder/             MainWindow.Remote.cs (IQueueRemote), UI de Acesso remoto
tests/RemoteChecks/             verificações em console (padrão do repositório) com TestHost
android/                        projeto Gradle (wrapper incluído, sem keystore)
docs/remote/                    estes documentos
```

## Fases

| Fase | Entrega | Critério de pronto |
|------|---------|--------------------|
| 0 | Preparação: Tailscale no PC e no celular, `tailscale serve`, decisões confirmadas | Celular nos dados móveis abre `https://<pc>.<tailnet>.ts.net` de um serviço de teste |
| 1 | Host remoto no desktop: pareamento, status, fila, iniciar/parar, modo de uso do PC | `RemoteChecks` passa; checklist de segurança da fase 1 em [VALIDATION.md](VALIDATION.md) |
| 2 | App Android MVP: parear por QR, painel, fila, iniciar/parar | APK instalado; R1, R2, R4, R6 verificados nos dados móveis |
| 3 | Adicionar episódios: pastas autorizadas no PC, navegação e seleção múltipla no app | R3 verificado; testes de travessia de caminho passam |
| 4 | Notificações (fila concluída, pausada por disco cheio, falha) e acabamento | Notificação recebida com o app fechado |
| 5 | Revisão de segurança e validação ponta a ponta | Todos os itens de [VALIDATION.md](VALIDATION.md) marcados |

Cada fase é um chat e um branch próprio a partir de `ui/v2` (ou do branch da fase anterior).

## Decisões a confirmar antes da fase 1

1. Usar Tailscale (recomendado) ou Cloudflare Tunnel + Access?
2. Distribuição do app: APK instalado direto no celular (recomendado; sem Play Store).
3. Pastas autorizadas iniciais para adicionar episódios (ex.: `T:\Animes`, `D:\Animes`).
4. Aceita que iniciar remotamente use a configuração atual do PC (motor, modelo, CQ,
   pasta de saída)? Alterar essas opções pelo celular fica fora da v1.

## Riscos

| Risco | Mitigação |
|-------|-----------|
| Host exposto na rede local ou internet por engano | Escuta fixa em 127.0.0.1, teste automático do endereço de escuta, Funnel proibido |
| Token vazado (celular perdido) | Revogar no PC; token só no Keystore; Tailscale também exige o aparelho na tailnet |
| Remoto vira execução de comandos | Sem endpoints de comando, modo avançado bloqueado, caminhos só nas pastas autorizadas |
| Iniciar remoto trava em diálogo no PC | `TryStartQueue` sem diálogos; avisos viram status |
| Concorrência UI × API | Toda operação passa pelo `Dispatcher`; a API nunca toca em `Items` diretamente |
| Tamanho do pacote | `Microsoft.AspNetCore.App` aumenta o instalador; aceitável e medido na fase 1 |
| Mudanças no `packages.lock.json` | Restaurar com `--locked-mode` após atualizar o lock, conforme AGENTS.md |
