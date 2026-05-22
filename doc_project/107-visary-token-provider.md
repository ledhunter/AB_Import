# 🔐 Visary token-provider: OIDC refresh_token-flow + DelegatingHandler

## 📋 Описание

Раньше backend `KiloImportService.Api` слепо подставлял `Authorization: Bearer {Visary:BearerToken}` из `.env` в каждый запрос к Visary. Токен жил ~1 час и протухал — приходилось вручную обновлять `.env` и перезапускать контейнер. Теперь токен **получается перед отправкой каждого запроса** через единый pipeline:

```
Client (ListView/Crud/FileStorage)
       │
       ▼
VisaryAuthHandler : DelegatingHandler        ← ставит Authorization
       │  └─ IVisaryTokenProvider.GetAccessTokenAsync()
       │       └─ кэш или рефреш (single-flight)
       ▼
HttpClient → Visary
```

На 401 от Visary handler один раз `InvalidateAsync()` + переотправляет запрос — гасит гонку «токен умер ровно между fast-path-проверкой и SendAsync».

---

## ⚠️ Важный нюанс: почему не `authorization_code`

Изначально предполагался flow «как в `visary-ui`»:

```
POST https://id-isup-alfa-test.k8s.npc.ba/oidc/connect/token
grant_type=authorization_code
code=<...>            ← одноразовый, выдаётся редиректом из браузера
code_verifier=<...>   ← pair к code_challenge из /authorize (PKCE)
client_id=visary-ui
redirect_uri=https://isup-alfa-test.k8s.npc.ba/oidc-silent-renew
```

**Это interactive PKCE-flow UI'я**, для backend не подходит:
1. `code` выдаётся редиректом после ввода **логина/пароля/MFA** на `/authorize` — backend некому ввести креды.
2. `code` одноразовый — UI его уже потратил.
3. `code_verifier` имеет смысл только в паре с `code_challenge`, который должна была отправить та же сторона.
4. В ответе **нет `refresh_token`** (`offline_access` scope не запрошен).

### Альтернативы

| Grant | Требует | Выбор |
|---|---|---|
| `authorization_code` | Браузерный логин | ❌ не подходит для backend |
| `client_credentials` | Confidential client + secret в IdP | Канонично для M2M, требует изменений на стороне IdP |
| **`refresh_token`** | Один раз залогиниться с `offline_access` → сохранить refresh_token | ✅ **Текущая реализация** |
| ROPC (`password`) | Сервисный пользователь + пароль | ❌ deprecated, ломается при MFA |

---

## ✅ Архитектура (текущая реализация)

### Компоненты в `Visary.Api.Client/Auth/`

| Файл | Назначение |
|---|---|
| [IVisaryTokenProvider.cs](../Visary.Api.Client/Auth/IVisaryTokenProvider.cs) | Контракт: `GetAccessTokenAsync` + `InvalidateAsync` |
| [StaticVisaryTokenProvider.cs](../Visary.Api.Client/Auth/StaticVisaryTokenProvider.cs) | Bridge для legacy: читает `VisaryOptions.BearerToken` |
| [OidcVisaryTokenProvider.cs](../Visary.Api.Client/Auth/OidcVisaryTokenProvider.cs) | Refresh_token grant, кэш, single-flight, rotation |
| [VisaryAuthHandler.cs](../Visary.Api.Client/Auth/VisaryAuthHandler.cs) | DelegatingHandler: ставит Bearer + 401-retry |
| [VisaryAuthOptions.cs](../Visary.Api.Client/Auth/VisaryAuthOptions.cs) | Конфиг секции `Visary:Auth` |
| [IVisaryRefreshTokenStore.cs](../Visary.Api.Client/Auth/IVisaryRefreshTokenStore.cs) | Контракт чтения/записи refresh_token |
| [EnvironmentRefreshTokenStore.cs](../Visary.Api.Client/Auth/EnvironmentRefreshTokenStore.cs) | Env-fallback (dev/CI), ротация в памяти |
| [VaultRefreshTokenStore.cs](../Visary.Api.Client/Auth/VaultRefreshTokenStore.cs) | Stub-ready (см. «Подключение Vault» ниже) |

### Pipeline DI

```csharp
// VisaryClientExtensions.RegisterClients (всегда):
services.AddTransient<VisaryAuthHandler>();
services.AddHttpClient<IListViewClient, ListViewClient>(...)
        .AddHttpMessageHandler<VisaryAuthHandler>();   // ← Auth теперь здесь, а не в Client
// Дефолтный provider — Static (legacy/dev). Подмена через AddVisaryOidcAuth.
services.TryAddSingleton<IVisaryTokenProvider, StaticVisaryTokenProvider>();

// AddVisaryOidcAuth (опционально, если TokenEndpoint задан):
services.AddHttpClient(OidcVisaryTokenProvider.TokenHttpClientName, ...);  // отдельный канал к IdP
services.Replace(ServiceDescriptor.Singleton<IVisaryTokenProvider, OidcVisaryTokenProvider>());
services.TryAddSingleton<IVisaryRefreshTokenStore>(_ => new EnvironmentRefreshTokenStore());
```

### Single-flight через `SemaphoreSlim`

100 параллельных room-rows под `Apply`'ем НЕ порождают 100 рефрешей. Fast-path без блокировки; рефреш под семафором; double-check внутри замка.

### Rotation refresh_token

IdP может выдать **новый** `refresh_token` на каждый рефреш — старый отзывается. Без `_refreshStore.SetAsync(...)` следующий рефреш пойдёт по уже-отозванному токену → 400 `invalid_grant`. Поэтому `OidcVisaryTokenProvider` всегда пишет ротированный токен обратно в store.

### 401-retry в handler'е

Если IdP-кэш токена устарел РОВНО между fast-path-проверкой и `SendAsync` — Visary вернёт 401. Handler видит 401, `InvalidateAsync()`, клонирует запрос, заново берёт токен (на сей раз свежий — попадает в рефреш-ветку) и переотправляет. Один раз. Повторный 401 — честная ошибка, идёт дальше.

---

## 🎯 Pre-flight: получение начального refresh_token

`refresh_token` нельзя выдать API-ключом — нужно ОДИН РАЗ пройти полный OIDC-flow с `offline_access` scope:

1. Открыть Visary UI в браузере, залогиниться.
2. DevTools → Network → найти `POST /oidc/connect/token`.
3. Проверить: в payload должен быть `scope=... offline_access ...`.
   - Если **нет** `offline_access` — UI не запрашивает refresh_token. Нужно либо
     - попросить admin'а IdP добавить `offline_access` в scope'ы client'а `visary-ui` и UI,
     - либо завести отдельный client (`kilo-import-backend`) с `offline_access` в allowed scopes.
4. Из response скопировать `refresh_token` — это будет долгоживущий секрет.
5. Положить в Vault (prod) или env `VISARY_AUTH_REFRESH_TOKEN` (dev).

---

## 🔐 Подключение Vault (TODO)

Текущий `VaultRefreshTokenStore` — заглушка, бросающая `NotImplementedException`. SDK-интеграция отложена до согласования с командой infra.

### Что нужно от infra

| Параметр | Пример | Зачем |
|---|---|---|
| Vault address | `https://vault.alfa.internal:8200` | Endpoint |
| Auth mode | AppRole / Kubernetes ServiceAccount | Как backend получит Vault-токен |
| Secret path | `secret/data/visary/refresh_token` | Где хранить (KV v2) |
| Field name | `refresh_token` | Имя поля в KV-секрете |

### Шаги интеграции

1. Добавить NuGet `VaultSharp` (для HashiCorp Vault) либо `Azure.Security.KeyVault.Secrets`.
2. Реализовать `VaultRefreshTokenStore`:
   - `GetAsync` → `vaultClient.V1.Secrets.KeyValue.V2.ReadSecretAsync(path)`
   - `SetAsync` → `WriteSecretAsync(path, {refresh_token: newToken})`
3. В `Program.cs` после `AddVisaryOidcAuth(...)`:
   ```csharp
   builder.Services.Replace(
       ServiceDescriptor.Singleton<IVisaryRefreshTokenStore, VaultRefreshTokenStore>());
   ```
4. Удалить `VISARY_AUTH_REFRESH_TOKEN` из prod-`.env`.

---

## 📍 Конфигурация

### `appsettings.json` (секция `Visary:Auth`)

```json
"Visary": {
  "BearerToken": "",                      ← оставить пустым, когда OIDC настроен
  "Auth": {
    "TokenEndpoint":           "https://id-isup-alfa-test.k8s.npc.ba/oidc/connect/token",
    "ClientId":                "visary-ui",
    "GrantType":               "refresh_token",
    "Scope":                   "",        ← обычно не нужен для refresh-grant
    "ClientSecret":            "",        ← null для public client (visary-ui с PKCE)
    "RefreshSkewSeconds":      60,
    "FallbackLifetimeSeconds": 300,
    "ExtraTokenRequestParameters": {}    ← см. ниже
  }
}
```

### Env-переменные (`.env`)

```bash
Visary__Auth__TokenEndpoint=https://id-isup-alfa-test.k8s.npc.ba/oidc/connect/token
Visary__Auth__ClientId=visary-ui
VISARY_AUTH_REFRESH_TOKEN=<длинный-токен-из-pre-flight>   # dev/CI
```

### `ExtraTokenRequestParameters` — точка расширения

Если IdP в будущем потребует доп. поля (`resource`, `audience`, `acr_values`, кастомные клеймы) — добавлять **без правок кода**:

```json
"ExtraTokenRequestParameters": {
  "resource": "https://visary.api",
  "audience": "visary-backend"
}
```

Provider кладёт их в form-payload как есть.

---

## ❌ Антипаттерны

### 1. Запрашивать токен на каждом запросе без кэша

```csharp
// НЕПРАВИЛЬНО — DDoS на /oidc/connect/token
protected override async Task<HttpResponseMessage> SendAsync(...)
{
    var token = await _tokenProvider.RefreshNowAsync();  // 100 parallel rows × refresh = 💥
    ...
}
```

✅ `OidcVisaryTokenProvider` кэширует токен до `exp - RefreshSkewSeconds` и под single-flight-блокировкой обеспечивает РОВНО один рефреш на парк параллельных запросов.

### 2. Не сохранять ротированный refresh_token

```csharp
// НЕПРАВИЛЬНО — backend проработает до первого ротейта IdP
var resp = await CallTokenEndpoint(refreshToken);
_accessToken = resp.AccessToken;
// resp.RefreshToken проигнорирован → следующий refresh пойдёт по старому → invalid_grant
```

✅ `OidcVisaryTokenProvider.RefreshLockedAsync` всегда пишет `parsed.RefreshToken` в store, если он отличается от текущего.

### 3. Использовать общий HttpClient для запросов в IdP и в Visary

```csharp
// НЕПРАВИЛЬНО — бесконечная рекурсия:
// чтобы получить access_token из IdP — HttpClient вызывает VisaryAuthHandler,
// VisaryAuthHandler зовёт provider.GetAccessTokenAsync(),
// который шлёт запрос в IdP через этот же HttpClient.
services.AddHttpClient<OidcVisaryTokenProvider>()
        .AddHttpMessageHandler<VisaryAuthHandler>();  // ← петля!
```

✅ Отдельный именованный HttpClient `OidcVisaryTokenProvider.TokenHttpClientName` БЕЗ `VisaryAuthHandler`.

### 4. Хранить refresh_token в `.env` на prod

```bash
# НЕПРАВИЛЬНО — .env коммитится по ошибке / читается из git-history / leak'ит в логи docker-compose
VISARY_AUTH_REFRESH_TOKEN=eyJhbGc...
```

✅ Prod — только Vault. `EnvironmentRefreshTokenStore` существует исключительно для dev/CI.

### 5. Static-токен без `EnsureConfig`-проверки

Текущий `StaticVisaryTokenProvider` бросает явный `InvalidOperationException` при пустом `BearerToken`. Не маскируем под 401 от Visary — корневая причина видна сразу.

---

## 📍 Применение в проекте

| Компонент | Файл | Роль |
|-----------|------|------|
| Token provider abstraction | [Visary.Api.Client/Auth/IVisaryTokenProvider.cs](../Visary.Api.Client/Auth/IVisaryTokenProvider.cs) | Единственная точка, на которую смотрит handler |
| OIDC refresh | [Visary.Api.Client/Auth/OidcVisaryTokenProvider.cs](../Visary.Api.Client/Auth/OidcVisaryTokenProvider.cs) | Refresh_token grant, кэш, rotation |
| Auth handler | [Visary.Api.Client/Auth/VisaryAuthHandler.cs](../Visary.Api.Client/Auth/VisaryAuthHandler.cs) | DelegatingHandler, ставит Bearer + 401-retry |
| Refresh-token store | [Visary.Api.Client/Auth/IVisaryRefreshTokenStore.cs](../Visary.Api.Client/Auth/IVisaryRefreshTokenStore.cs) | Env / Vault |
| DI extensions | [Visary.Api.Client/VisaryClientExtensions.cs](../Visary.Api.Client/VisaryClientExtensions.cs) | `AddVisaryClient` + `AddVisaryOidcAuth` |
| Wired в backend | [KiloImportService.Api/Program.cs](../KiloImportService.Api/Program.cs) | Подключение OIDC опционально по наличию `TokenEndpoint` |
| Snapshot конфига | [KiloImportService.Api/appsettings.json](../KiloImportService.Api/appsettings.json) | секция `Visary:Auth` |
| Env-snapshot | [.env.example](../.env.example) | `Visary__Auth__*` + `VISARY_AUTH_REFRESH_TOKEN` |
| Тесты | [KiloImportService.Api.Tests/VisaryAuth/OidcVisaryTokenProviderTests.cs](../KiloImportService.Api.Tests/VisaryAuth/OidcVisaryTokenProviderTests.cs) | Cache hit, refresh, rotation, single-flight, 401, errors |
| Test factory | [KiloImportService.Api.Tests/VisaryClients/TestVisaryClientFactory.cs](../KiloImportService.Api.Tests/VisaryClients/TestVisaryClientFactory.cs) | `NewClientWithAuthPipeline` оборачивает RecordingHandler в `VisaryAuthHandler` |

---

## 🎯 Чек-лист включения OIDC на стенде

- [ ] Закомментирован `Visary__BearerToken` в `.env` (или оставлен пустым — провайдер бросит InvalidOperationException, если кто-то случайно дёрнет StaticVisaryTokenProvider)
- [ ] Заполнен `Visary__Auth__TokenEndpoint` — иначе `AddVisaryOidcAuth` НЕ зарегистрируется и backend будет молча использовать static-flow
- [ ] Заполнен `Visary__Auth__ClientId` (например `visary-ui`)
- [ ] Получен и положен `VISARY_AUTH_REFRESH_TOKEN` (dev) или путь в Vault настроен (prod)
- [ ] Контейнер пересобран и перезапущен: `docker compose build backend && docker compose up -d --force-recreate backend` (см. memory: `docker_frontend_rebuild`)
- [ ] В логе backend на старте есть `OIDC token-refresh OK: access_token действует до ...` (первый запрос к Visary)
- [ ] Через ~50 минут (за минуту до истечения) — в логе видно второй `OIDC token-refresh OK`

## 🐛 Грабли

### «refresh_token пуст» при старте

Симптом: `InvalidOperationException: OidcVisaryTokenProvider: refresh_token пуст`.

Причина: `Visary__Auth__TokenEndpoint` задан, но `VISARY_AUTH_REFRESH_TOKEN` пустой → backend перешёл на OIDC-flow, но рефрешить нечего.

Лечение: либо положить refresh_token (см. «Pre-flight»), либо очистить `TokenEndpoint` — backend откатится на static.

### 400 `invalid_grant` после ~12 часов

Симптом: после долгой работы backend начинает падать на каждом запросе с `OIDC token-refresh вернул 400`.

Причина: `EnvironmentRefreshTokenStore` хранит ротированный токен **в памяти**. Рестарт процесса — refresh_token возвращается к env-значению, который IdP уже отозвал.

Лечение: переходить на `VaultRefreshTokenStore`. Временный workaround на dev — пересохранять `VISARY_AUTH_REFRESH_TOKEN` после каждого refresh-cycle вручную (увидеть свежий — в логе `Vault: ...` или подключив hook в `IVisaryRefreshTokenStore.SetAsync`).

### Двойной 401 от Visary

Симптом: в логе `Visary вернул 401` дважды подряд на один запрос.

Причина: handler делает РОВНО один retry. Если оба 401 — это уже не гонка, а реальная проблема (отозванный токен / scope / неверный client_id). Provider кидает `VisaryAuthException`, ответ всплывает в client'е.

Лечение: проверить, что выданный `refresh_token` всё ещё валиден в IdP (`POST /token` cURL-ом из консоли); проверить, что у client'а в IdP не отозваны нужные scope'ы.

### Контракт-тесты VisaryClients падают на отсутствии Authorization-заголовка

Симптом (если кто-то откатит refactor): `Assert.Equal Authorization header expected stub-token, got <null>`.

Причина: до refactor'а `VisaryHttpBase.NewRequest` сам ставил Bearer. После — это делает только `VisaryAuthHandler`. Тестовая фабрика **обязана** обернуть `RecordingHttpHandler` в `VisaryAuthHandler` со stub-провайдером — см. `TestVisaryClientFactory.NewClientWithAuthPipeline`.

Лечение: не пытаться обходить handler в тестах. Если нужен low-level доступ — добавлять явный `WithoutAuth` helper.
