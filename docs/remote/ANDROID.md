# Controle remoto — app Android

## Stack

| Item | Escolha |
|------|---------|
| Linguagem / UI | Kotlin, Jetpack Compose, Material 3 (tema escuro com a paleta do desktop: fundo `#0B0F17`, cartão `#121926`, destaque `#806BFF`) |
| SDK | minSdk 26, targetSdk e compileSdk = último estável instalado no Android Studio |
| Rede | OkHttp + Retrofit + kotlinx.serialization; timeout 10 s; sem tráfego claro |
| QR | CameraX + ML Kit Barcode Scanning (modelo embutido, funciona offline) |
| Token | Chave AES-GCM no Android Keystore (não exportável) cifrando o token salvo em DataStore |
| Estado | ViewModel + StateFlow; polling 3 s no painel, 10 s na fila, pausado em segundo plano |
| Segundo plano (fase 4) | WorkManager periódico (15 min) para notificar "fila concluída", "pausada por disco cheio" e "falha" |
| Pacote | `com.imcarl6.animeupscale.remote`, nome "Upscale Remoto" |
| Distribuição | APK de release assinado com keystore fora do Git, instalado via `adb install` ou arquivo |

Ambiente já presente na máquina: Android Studio (`C:\Program Files\Android\Android Studio`, com JDK embutido),
SDK em `%LOCALAPPDATA%\Android\Sdk`, `adb` em `C:\platform-tools`. Gradle via wrapper do projeto.

## Telas

```
┌ Parear ─────────────┐  ┌ Painel ──────────────────┐  ┌ Fila ───────────────────┐  ┌ Adicionar ──────────────┐
│                     │  │ CARLO-PC  ● online        │  │ 3/36 concluídos          │  │ Animes (T:) ›  JoJo ›    │
│  [ câmera / QR ]    │  │ Processando agora         │  │ ✓ S03E01   3,38 GB 27:00 │  │ ☐ S03E07  1,5 GB         │
│                     │  │ S03E04 – Diamond is…      │  │ ✓ S03E02   3,44 GB 28:00 │  │ ☑ S03E08  1,5 GB         │
│ Abra Acesso remoto  │  │ ████████░░░░  42,3%       │  │ ⟳ S03E04  42%   16:23    │  │ ☑ S03E09  1,5 GB         │
│ → Parear no PC e    │  │ Restam 16:23 • 1.04x      │  │ ◷ S03E05  ~6,2 GB ~37:30 │  │ • S03E10  na fila        │
│ aponte a câmera.    │  │ Fila 11% • ETA 22h19      │  │ ✕ S03E06  Falhou: …      │  │                          │
│                     │  │ [Assistir|Equil.|Máx.]    │  │                          │  │                          │
│ [Digitar código]    │  │ [  Parar fila  ]          │  │                          │  │ [ Adicionar 2 episódios ]│
└─────────────────────┘  └─[Painel][Fila][Adicionar]┘  └──────────────────────────┘  └──────────────────────────┘
```

- **Painel**: estado (rodando, parada, pausada por disco cheio com a mensagem do PC),
  cartão do episódio atual, progresso total e ETA, seletor de Uso do PC, botão grande
  **Iniciar fila** / **Parar fila**. Parar pede confirmação ("O episódio atual será
  interrompido; os blocos prontos são mantidos"). Mostra espaço livre da saída e o
  motivo quando não é possível iniciar (`startBlockedReason`).
- **Fila**: lista com ícone de status, nome curto do episódio (sem prefixo repetido da
  série quando todos compartilham), detalhe, tamanho e tempo; toque abre o detalhe com o erro.
- **Adicionar** (fase 3): navega nas pastas autorizadas, seleção múltipla, "selecionar
  todos que não estão na fila", botão fixo no rodapé.
- **Configurações**: PC pareado, endereço, "Desconectar este celular".
- **Sem conexão**: mensagem clara ("Ative o Tailscale no celular" ou "Acesso remoto
  desligado no PC") e botão Tentar de novo. Nunca mostra dados antigos como se fossem atuais
  (exibe "atualizado há X s").

Requisitos de UX móvel: alvos de toque ≥ 48 dp, uma mão (ações no rodapé), textos com
`maxLines` e reticências, suporte a fonte grande, modo retrato, sem rolagem horizontal.

## Estrutura do projeto

```
android/
  settings.gradle.kts, build.gradle.kts, gradle/libs.versions.toml, gradlew(.bat), gradle/wrapper/
  app/src/main/java/com/imcarl6/animeupscale/remote/
    data/   ApiService.kt, Dto.kt, RemoteRepository.kt, TokenStore.kt
    ui/     theme/, pairing/, dashboard/, queue/, library/, settings/
    work/   QueueStatusWorker.kt (fase 4)
  app/src/main/res/xml/network_security_config.xml
  app/src/test/ (ViewModels com repositório falso)  app/src/androidTest/ (Compose UI)
```

`AndroidManifest.xml`: `android:allowBackup="false"`, `android:usesCleartextTraffic="false"`,
permissões apenas `INTERNET`, `CAMERA` (pedida só na tela de pareamento) e
`POST_NOTIFICATIONS` (fase 4).

## Build e instalação

```powershell
cd android
.\gradlew.bat testDebugUnitTest assembleDebug
adb install -r app\build\outputs\apk\debug\app-debug.apk
```

Release: keystore em local fora do repositório (ex.: `%USERPROFILE%\.android-keys\upscale-remote.jks`),
referenciado por `keystore.properties` ignorado no `.gitignore`.
