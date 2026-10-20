# Testes

## Prévia local: segundo plano v2

- `dotnet run --project tests/ResourceChecks/ResourceChecks.csproj`: CPU nativa 35%/50%, herança, remoção do limite artificial de memória, reserva, pressão transitória/sustentada e orçamento de duas retomadas.
- `Test-BackgroundV2.ps1 -Package <prévia> -FixturePath <fixture-sintética>`: fila real, cadência, saída validada e limpeza após sucesso.
- Acrescentar `-ForceRecovery`: interrompe apenas o Job Object da tentativa, simulando pressão sem esgotar memória; verifica espera mínima de 30s, retomada mais lenta e resultado final.
- Acrescentar `-ForceRecovery -CancelRecovery`: cancela manualmente durante a espera e verifica ausência de retomada.
- `-ProfileCode V3-Compact-Sharp1` verifica o perfil padrão com a mesma fixture sintética.

Estas verificações não comprovam responsividade com episódio completo e navegador aberto.

## Limpeza e padrões de abertura (0.1.5)

`ReleaseChecks` verifica remoção após sucesso, preservação em falha/cancelamento,
isolamento entre trabalhos, limpeza repetida, remoção do diretório pai vazio e
rejeição de caminhos fora de `.anime-upscale-temp`.
`dotnet run --project tests/DefaultsChecks/DefaultsChecks.csproj -- <runtime-completo>`
instancia a janela WPF sem iniciar encoders e confere AnimeJaNai, Compact Sharp1 e CQ 18.
Isso não substitui uma conversão completa com validação final e limpeza em uso real.

## Sem GPU / CI

`dotnet run --project tests/ReleaseChecks/ReleaseChecks.csproj` usa apenas fixtures
sintéticas para verificar dados persistentes, reaproveitamento de engines em update
do app, invalidação por toolchain e preservação de históricos. Não executa inferência.

## Com GPU

Scripts em `src/Anime4KEncoder/Test-*.ps1` incluem checks de fingerprint, metadata,
piloto e pipeline segmentado. Use PowerShell em STA para os que instanciam WPF.
Os scripts de integração precisam de uma distribuição completa e fixtures locais;
nenhum vídeo ou relatório privado é incluído no Git. Passe `InputPath` explicitamente
nos testes de vídeo. Não interprete paths de saída/validation como fixtures distribuídas.

`Test-ResumeValidation.ps1` gera mídia sintética; compara idiomas, resolução 2x,
frames, timestamps e áudio. O teste de piloto realiza três trechos de cinco segundos
e verifica persistência das projeções. Testes antigos carregam DLLs por reflexão;
execute-os no ambiente de desenvolvimento com suas dependências resolvidas.

## Instalador e updater

Build de Setup.exe e validação de bootstrap são smoke tests. Uma publicação pública
deve também passar pela instalação e por um ciclo real entre duas versões em Windows
de teste, conforme docs/RELEASING.md. Separar claramente esses resultados no relatório.

O teste de feed usa o TestVelopackLocator oficial e os pacotes gerados:

```powershell
dotnet run --project tests/UpdateFeedChecks/UpdateFeedChecks.csproj -- artifacts/Releases artifacts/feed-check
```

Confere consulta, changelog, download, ausência de update para versão atual e
rejeição de pacote corrompido. Não instala/aplica nem simula uma RTX 5070.

## Fila e comparador

`Test-QueueFeatures.ps1 -Package <pasta-completa>` verifica reordenação da coleção,
bloqueio durante processamento e ações de saída somente após sucesso. Com
`-PilotReport <pilot-report.json> -RenderPath <imagem.png>`, verifica o comparador
usando imagens reais do piloto e renderiza sua interface para inspeção.

`Test-PilotEndToEnd.ps1` também exige pares de comparação, tamanhos iguais e acesso
pela interface. ReleaseChecks testa offsets não contíguos da origem, índices do
piloto concatenado e amostras curtas/ímpares. As imagens são PNG RGB de inspeção;
não são métricas de qualidade nem uma referência de apresentação HDR.
