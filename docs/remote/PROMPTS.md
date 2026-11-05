# Prompts para os próximos chats

Use um chat por fase, nesta ordem. Abra o chat na pasta `C:\anime-studio\ui-v2`
(worktree do branch `ui/v2`) ou numa worktree nova criada a partir dele. Cada prompt é
autossuficiente: copie o bloco inteiro.

Antes da fase 1, faça a fase 0 manualmente (abaixo) e responda às "Decisões a confirmar"
do [PLAN.md](PLAN.md).

---

## Fase 0 — preparação (manual, ~15 min)

1. Instale o Tailscale no PC (tailscale.com/download) e no celular (Play Store) com a mesma conta.
2. No painel admin do Tailscale: ative **MagicDNS** e **HTTPS Certificates**.
3. No PC, anote o nome completo: `tailscale status` → `<pc>.<tailnet>.ts.net`.
4. Teste rápido de alcance, sem o app:
   `python -m http.server 47820 --bind 127.0.0.1` num terminal e `tailscale serve --bg 47820` em outro;
   no celular **com Wi-Fi desligado**, abra `https://<pc>.<tailnet>.ts.net`. Deve listar a pasta.
   Depois: `tailscale serve reset` e feche o servidor.
5. **Não** use `tailscale funnel`.

---

## Prompt — Fase 1: host remoto no desktop

```text
Projeto: Anime Upscale Studio (C# / WPF, .NET 10), pasta C:\anime-studio\ui-v2, branch ui/v2.
Leia primeiro AGENTS.md, README.md e todos os arquivos de docs/remote/ (PLAN, SECURITY, API,
VALIDATION). Eles são a especificação; siga-os e aponte qualquer conflito antes de mudar algo.

Tarefa: implementar a FASE 1 do docs/remote/PLAN.md — o host remoto no app desktop.
Crie um branch feature/remote-host a partir de ui/v2.

Escopo:
1. Nova biblioteca src/AnimeStudio.Remote (net10.0, ASP.NET Core via FrameworkReference):
   contratos do API.md (status, queue, start, stop, resource-mode, pair, devices/self),
   pareamento com código de uso único, tokens (só hash SHA-256 salvo, comparação em tempo
   constante), limitação de taxa, Idempotency-Key, log de auditoria sem segredos,
   armazenamento em UserData/data/remote.json com gravação atômica.
   Kestrel escuta SOMENTE em 127.0.0.1:47820 (constante, não configurável).
2. Interface IQueueRemote na biblioteca; implementação em src/Anime4KEncoder/MainWindow.Remote.cs,
   sempre via Dispatcher. Extraia de Start_Click um TryStartQueue sem MessageBox que retorna
   um resultado; o botão do PC continua mostrando os mesmos diálogos a partir dele.
   Fila iniciada remotamente não pode terminar em diálogo bloqueante: o aviso vira status.
   Modo avançado e perfis Legacy/Experimental não iniciam remotamente (409 com reason).
   Id estável por item = 12 hex do SHA-256 do caminho normalizado.
3. UI no painel Configuração da interface 2.0: seção "Acesso remoto" (desligado por padrão,
   endereço, botão "Parear celular" que mostra QR + código por 5 min, lista de aparelhos com
   Revogar) e indicador "Acesso remoto ativo" na barra superior. Para o QR, use uma biblioteca
   .NET de licença permissiva e registre-a em THIRD_PARTY_NOTICES.md.
4. tests/RemoteChecks (console, mesmo estilo de tests/ReleaseChecks) cobrindo A1–A13 do
   VALIDATION.md (A7 fica para a fase 3, mas já crie a estrutura).

Fora do escopo: app Android, adicionar arquivos (fase 3), notificações, qualquer mudança no
pipeline de upscale, encoder, blocos ou validações de vídeo.

Restrições: manter todos os x:Name existentes (scripts Test-*.ps1 dependem deles); atualizar
packages.lock.json e restaurar com --locked-mode; CHANGELOG em "Próxima versão"; não commitar
segredos. Use Velopack e StoragePaths como estão.

Pronto quando: build Release sem avisos novos; ReleaseChecks e RemoteChecks passam; um curl em
http://127.0.0.1:47820/api/v1/status com token devolve o JSON do API.md com a fila real; o mesmo
curl pelo IP da LAN é recusado; tailscale serve --bg 47820 e um curl pelo endereço .ts.net de
outro aparelho funcionam. Reporte os itens A1–A13 com a saída dos testes.
```

---

## Prompt — Fase 2: app Android (MVP)

```text
Projeto: Anime Upscale Studio, pasta C:\anime-studio\ui-v2. O host remoto da fase 1 já existe
(branch feature/remote-host). Leia AGENTS.md e docs/remote/ (PLAN, SECURITY, API, ANDROID,
VALIDATION). A API do docs/remote/API.md é o contrato; não invente rotas.

Tarefa: criar o app Android da FASE 2 em android/ (branch feature/remote-android a partir de
feature/remote-host).

Escopo: projeto Gradle Kotlin DSL com wrapper; Jetpack Compose + Material 3 com o tema escuro
do ANDROID.md; telas Parear (QR com CameraX + ML Kit e digitação manual do código), Painel,
Fila e Configurações; token cifrado com chave do Android Keystore; Retrofit/OkHttp com
kotlinx.serialization; polling 3 s/10 s só em primeiro plano; network_security_config sem
tráfego claro; allowBackup=false; confirmação antes de Parar; estado "sem conexão" com dica do
Tailscale; testes B1–B5 do VALIDATION.md.

Ambiente: Android Studio em C:\Program Files\Android\Android Studio (use o JDK embutido em
jbr como JAVA_HOME), SDK em %LOCALAPPDATA%\Android\Sdk, adb em C:\platform-tools.

Fora do escopo: adicionar arquivos, notificações, Play Store.

Pronto quando: .\gradlew.bat testDebugUnitTest assembleDebug passa; APK instalado no meu
celular via adb; pareado com o PC; M1, M2, M4, M7, M8, M9, M10, M13 e M14 do VALIDATION.md
verificados comigo (peça para eu executar no celular o que você não puder). Não gere nem
commite keystore de release; deixe instruções.
```

---

## Prompt — Fase 3: adicionar episódios pelo celular

```text
Projeto: Anime Upscale Studio, pasta C:\anime-studio\ui-v2, branches feature/remote-host e
feature/remote-android prontos. Leia AGENTS.md e docs/remote/ (em especial SECURITY.md,
seção Arquivos, e API.md, seção Adicionar episódios).

Tarefa: FASE 3 nos dois lados.
Desktop: cadastro de pastas autorizadas na seção Acesso remoto; rotas /library/roots,
/library/browse e POST /queue/items exatamente como no API.md; política de caminhos que
canoniza e recusa tudo listado em A7 (incluindo junctions e links simbólicos que saem da raiz);
adição reutiliza AddInputsAsync (análise FFprobe igual a "Adicionar arquivos"); estado done
quando a saída final já existe. Testes A7 completos em RemoteChecks.
Android: tela Adicionar com navegação por pastas, seleção múltipla, "selecionar os que não estão
na fila", botão fixo "Adicionar N episódios", feedback de adicionados/ignorados.

Pronto quando: RemoteChecks (A1–A13) e testes Android passam; M11 verificado no celular
pelos dados móveis; tentativa manual de path traversal pela API devolve 403 e aparece no
remote.log sem o caminho absoluto.
```

---

## Prompt — Fase 4: notificações e acabamento

```text
Projeto: Anime Upscale Studio, pasta C:\anime-studio\ui-v2, fases 1–3 concluídas. Leia AGENTS.md
e docs/remote/.

Tarefa: notificações no Android com WorkManager periódico (15 min, rede obrigatória) para
"fila concluída", "fila pausada: disco sem espaço" e "episódio falhou", sem serviço de push de
terceiros; canal de notificação próprio; POST_NOTIFICATIONS pedido no momento certo; tocar abre
o Painel. Ajustes de UX encontrados nas fases anteriores e remoção de item da fila
(DELETE /queue/items/{id}) com confirmação.

Pronto quando: notificação recebida com o app fechado e o celular nos dados móveis; M12
verificado; testes passam.
```

---

## Prompt — Fase 5: revisão de segurança e validação final

```text
Projeto: Anime Upscale Studio, pasta C:\anime-studio\ui-v2, recurso de controle remoto completo
(fases 1–4). Leia AGENTS.md e docs/remote/.

Tarefa: revisão independente. Rode /security-review no diff do recurso remoto. Confira cada
controle de docs/remote/SECURITY.md contra o código (escuta em 127.0.0.1, hash de token,
comparação em tempo constante, pareamento, limite de taxa, política de caminhos, ausência de
rotas extras, logs sem segredos, Android sem tráfego claro e sem backup). Rode todos os testes
automáticos e conduza comigo a matriz manual M1–M14 do VALIDATION.md, registrando evidências
em docs/remote/VALIDATION-REPORT.md. Corrija apenas o que for falha real e reporte o restante.

Pronto quando: critério de aceite final do VALIDATION.md cumprido ou cada pendência listada
com motivo.
```
