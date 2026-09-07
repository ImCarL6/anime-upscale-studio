# Testes

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
