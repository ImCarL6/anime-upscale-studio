# Controle remoto — contrato da API v1

Base: `https://<pc>.<tailnet>.ts.net/api/v1` (via `tailscale serve`) → `http://127.0.0.1:47820/api/v1`.
JSON UTF-8, `camelCase`, datas ISO 8601, durações em segundos, tamanhos em bytes.
Todas as rotas exigem `Authorization: Bearer <token>`, exceto `POST /pair`.
POSTs aceitam `Idempotency-Key` (UUID); repetir a mesma chave em 10 min devolve a mesma resposta.

Erros: `{ "error": "<código>", "message": "<texto curto em português>" }`.

| HTTP | `error` | Quando |
|------|---------|--------|
| 400 | `invalid_request` | Corpo ou parâmetro inválido |
| 401 | `unauthorized` | Sem token, token inválido ou revogado |
| 403 | `forbidden` | Caminho fora das pastas autorizadas, ação não permitida remotamente |
| 404 | `not_found` | Item ou pasta inexistente |
| 409 | `conflict` | Fila já em execução, item em processamento, início bloqueado (ver `reason`) |
| 429 | `rate_limited` | Limite excedido (`Retry-After`) |
| 503 | `remote_disabled` | Acesso remoto desligado no PC |

## Pareamento

`POST /pair` (sem token; só funciona com a janela "Parear celular" aberta no PC)

```json
// pedido
{ "code": "K7Q2-9XMP", "deviceName": "Pixel 8" }
// resposta 200
{ "deviceId": "d_3f9c…", "token": "<43 caracteres base64url>", "pcName": "CARLO-PC", "apiVersion": 1 }
```

Conteúdo do QR mostrado no PC: `animeupscale://pair?host=<pc>.<tailnet>.ts.net&code=K7Q2-9XMP&v=1`.

`DELETE /devices/self` — o próprio app se desconecta (revoga o token atual).

## Status

`GET /status` — tela principal; o app consulta a cada 3 s enquanto aberta.

```json
{
  "pcName": "CARLO-PC",
  "appVersion": "2.0.0",
  "state": "running",                 // idle | running | pausedDiskFull | pausedResources | environmentNotReady
  "stateMessage": null,
  "canStart": false,
  "startBlockedReason": null,         // empty | noPending | invalidCq | needsLocalConfirmation | advancedModeLocal | environmentNotReady
  "resourceMode": "max",              // watch | balanced | max
  "queue": {
    "total": 36, "completed": 3, "running": 1, "pending": 31, "failed": 1,
    "percent": 11.4, "etaSeconds": 80340, "elapsedSeconds": 5576
  },
  "current": {
    "id": "a1b2c3d4e5f6", "fileName": "…S03E04….mkv", "isPilot": false,
    "progress": 42.3, "stage": "Bloco 3/5 • AnimeJaNai", "speed": "1.04x",
    "elapsedSeconds": 720, "etaSeconds": 983, "durationSeconds": 1422
  },
  "resources": { "cpuLimitPercent": 100, "ramFreeBytes": 18511284224, "vramUsedPercent": [59] },
  "output": { "freeBytes": 63350000000, "estimatedQueueBytes": 258900000000 },
  "settings": { "engine": "AnimeJaNai", "profile": "V3.1 Balanced Sharp1", "cq": 26 }
}
```

`GET /queue` — lista completa.

```json
{
  "items": [
    {
      "id": "a1b2c3d4e5f6", "fileName": "…S03E01….mkv", "status": "Concluído", "kind": "done",
      "progress": 100, "detail": "Concluído • 16.9x", "durationSeconds": 1424,
      "elapsedSeconds": 1696, "etaSeconds": 0, "sizeBytes": 3629247365, "estimatedBytes": 6657199308,
      "error": null
    }
  ]
}
```

`kind`: `waiting | running | done | failed | warning | cancelled` (mesma regra de `EncodeItem.StatusKind`).
`id`: primeiros 12 hex do SHA-256 do caminho de entrada normalizado. Caminhos completos não são enviados.

## Controle

| Rota | Efeito | Respostas |
|------|--------|-----------|
| `POST /queue/start` | Inicia a fila com a configuração atual do PC | 202 iniciado; 409 `{reason}` |
| `POST /queue/stop` | Cancela o episódio atual (blocos validados preservados) e para a fila | 202; 409 se não está rodando |
| `PUT /resource-mode` `{ "mode": "watch" }` | Troca Uso do PC ao vivo | 200 |
| `DELETE /queue/items/{id}` | Remove item que não está em processamento (fase 3) | 204; 409 se em processamento |

## Adicionar episódios (fase 3)

`GET /library/roots`

```json
{ "roots": [ { "id": "r1", "name": "Animes (T:)" }, { "id": "r2", "name": "Animes (D:)" } ] }
```

`GET /library/browse?root=r1&path=JoJo's Bizarre Adventure/Diamond is Unbreakable`

```json
{
  "root": "r1", "path": "JoJo's Bizarre Adventure/Diamond is Unbreakable",
  "folders": [ { "name": "Extras", "path": "JoJo's…/Extras" } ],
  "files": [
    { "name": "…S03E07….mkv", "path": "JoJo's…/…S03E07….mkv", "sizeBytes": 1544000000, "state": "inQueue" }
  ]
}
```

`state`: `available | inQueue | done` (`done` = já existe a saída final na pasta de saída).
`path` é sempre relativo à raiz e com `/`; o servidor recusa `..`, raízes, UNC e links que saiam da raiz.

`POST /queue/items`

```json
// pedido
{ "root": "r1", "paths": ["JoJo's…/…S03E07….mkv", "JoJo's…/…S03E08….mkv"] }
// resposta 200
{ "added": ["b7…", "c9…"], "skipped": [ { "path": "…", "reason": "alreadyInQueue" } ] }
```

Máximo 100 caminhos por pedido. A análise (FFprobe) acontece no PC como em "Adicionar arquivos";
o item aparece como `Analisando` até terminar.
