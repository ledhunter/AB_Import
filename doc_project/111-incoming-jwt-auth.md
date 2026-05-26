# 🔐 Аутентификация ВХОДЯЩИХ запросов (JWT bearer)

## 📋 Описание

До этой ветки backend `KiloImportService.Api` принимал **любые** запросы — `[Authorize]` не использовался, `UseAuthentication`/`UseAuthorization` в `Program.cs` отсутствовали. Допустимо только в доверенной dev-сети.

Этот документ описывает добавленный JWT-bearer middleware: backend валидирует входящие токены через тот же IdP, что уже используется `OidcVisaryTokenProvider` для исходящих вызовов в Visary (см. [doc 107](./107-visary-token-provider.md)). Дополнительный IdP не нужен.

Включение **поэтапное и обратимо**: пока `Auth:Authority` пуст — middleware не регистрируется, backend ведёт себя как раньше.

---

## ✅ Правильная конфигурация

### Backend (`appsettings.json` + `.env`)

```jsonc
// appsettings.json
"Auth": {
  "Authority":            "https://id-isup-alfa-test.k8s.npc.ba/oidc",
  "Audience":             "kilo-import-api",
  "RequireHttpsMetadata": true
}
```

```bash
# .env (override на конкретное окружение)
Auth__Authority=https://id-isup-alfa-test.k8s.npc.ba/oidc
Auth__Audience=kilo-import-api
Auth__RequireHttpsMetadata=true
```

### Backend (`Program.cs`)

```csharp
var incomingAuthSection = builder.Configuration.GetSection("Auth");
var incomingAuthority   = incomingAuthSection["Authority"];
var incomingAuthEnabled = !string.IsNullOrWhiteSpace(incomingAuthority);

if (incomingAuthEnabled)
{
    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(opt =>
        {
            opt.Authority           = incomingAuthority;
            opt.Audience            = incomingAuthSection["Audience"];
            opt.RequireHttpsMetadata = incomingAuthSection.GetValue("RequireHttpsMetadata", true);
            opt.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer           = true,
                ValidateAudience         = !string.IsNullOrWhiteSpace(incomingAuthSection["Audience"]),
                ValidateLifetime         = true,
                ValidateIssuerSigningKey = true,
                ClockSkew                = TimeSpan.FromMinutes(2)
            };
            // SignalR не может слать Authorization-header при WebSocket-апгрейде —
            // берём токен из query `?access_token=…` для путей под /hubs.
            opt.Events = new JwtBearerEvents
            {
                OnMessageReceived = ctx =>
                {
                    var token = ctx.Request.Query["access_token"];
                    var path  = ctx.HttpContext.Request.Path;
                    if (!string.IsNullOrEmpty(token) && path.StartsWithSegments("/hubs"))
                        ctx.Token = token;
                    return Task.CompletedTask;
                }
            };
        });
    builder.Services.AddAuthorization();
}
```

И в pipeline (порядок важен — `UseAuthentication` ДО `UseAuthorization`):

```csharp
app.UseRouting();
app.UseCors("ui");
if (incomingAuthEnabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapControllers().RequireAuthorization();              // 👈 deny-by-default
    app.MapHub<ImportProgressHub>("/hubs/imports").RequireAuthorization();
}
else
{
    app.MapControllers();
    app.MapHub<ImportProgressHub>("/hubs/imports");
}
```

### ⚠️ Важно

- **`Authority` пуст → auth ВЫКЛЮЧЕНА** — поведение как до патча. Это сознательный fallback, чтобы локальный dev продолжал работать. **На prod `Authority` ОБЯЗАТЕЛЬНО задаётся**.
- **`RequireAuthorization()` на `MapControllers()` — это deny-by-default**. Что должно быть публичным (health, swagger) — помечается `[AllowAnonymous]`.
- **`Audience` валидируется только если он непустой** в конфиге — иначе пропускаем чек (для случая, когда IdP не кладёт `aud`-claim).
- **`ClockSkew = 2 минуты`** — компенсирует расхождение часов backend ↔ IdP. По умолчанию у `JwtBearer` стоит 5 минут — мы ужесточили.

### Frontend (`services/auth.ts` + `main.tsx`)

```typescript
// services/auth.ts — единый поставщик токена для importsService.ts и importsHub.ts
export type TokenGetter = () => string | undefined | Promise<string | undefined>;
let _getter: TokenGetter = () => undefined;
export const setAccessTokenGetter = (g: TokenGetter) => { _getter = g; };
export const getAccessToken       = async () => _getter();
```

```typescript
// main.tsx — регистрация поставщика на старте приложения
setAccessTokenGetter(() => localStorage.getItem('access_token') ?? undefined);
// TODO: заменить на oidc-client-ts.getUser()?.access_token после подключения PKCE flow.
```

```typescript
// importsService.ts — fetch с Authorization-header
async function withAuth(init: RequestInit): Promise<RequestInit> {
  const token = await getAccessToken();
  if (!token) return init;
  const headers = new Headers(init.headers ?? {});
  headers.set('Authorization', `Bearer ${token}`);
  return { ...init, headers };
}
// внутри fetchJson: fetch(path, await withAuth(init))
```

```typescript
// importsHub.ts — SignalR с accessTokenFactory
new HubConnectionBuilder()
  .withUrl(HUB_PATH, {
    accessTokenFactory: async () => (await getAccessToken()) ?? '',
  })
  .withAutomaticReconnect()
  .build();
```

---

## ❌ Типичные ошибки

### Ошибка 1 — `UseAuthorization()` без `UseAuthentication()`

```csharp
// НЕПРАВИЛЬНО
app.UseRouting();
app.UseAuthorization();        // ← нет UseAuthentication выше
app.MapControllers().RequireAuthorization();
```

Все запросы → 401, даже с валидным токеном (некому положить `User` в `HttpContext`).

### Ошибка 2 — `RequireAuthorization` ДО `UseAuthentication`

Порядок middleware важен — `UseAuthentication` обязан идти **до** `UseAuthorization`.

### Ошибка 3 — забыли про SignalR в `OnMessageReceived`

```csharp
// НЕПРАВИЛЬНО — без обработки query-token для /hubs
opt.Events = null;   // или просто не задали
```

WebSocket-апгрейд не может нести `Authorization`-header. SignalR-клиент кладёт токен в `?access_token=…`, и сервер обязан это распарсить — иначе все хабы будут возвращать 401.

### Ошибка 4 — `ValidateAudience=true` при пустом `Audience`

```csharp
// НЕПРАВИЛЬНО
ValidateAudience = true,     // ← всегда true, даже если Audience пуст
Audience         = null
```

Все валидные токены будут отвергнуты с `IDX10208: Unable to validate audience. Audiences is null`. Правильно — `ValidateAudience = !string.IsNullOrWhiteSpace(Audience)`.

### Ошибка 5 — фронт шлёт токен, backend не настроен

Безопасно — JWT-middleware просто не извлечёт `User`, но `RequireAuthorization` всё равно вернёт 401. Это правильное поведение.

### Ошибка 6 — слать `accessTokenFactory: () => ''` без условия

При пустом токене SignalR всё равно добавит `?access_token=` в URL → backend получит пустую строку, попытается её валидировать → шум в логах. На фронте лучше проверять `?? ''`-fallback только в dev-режиме.

---

## 🚦 Поэтапная выкатка

| Этап | `Auth:Authority` | Поведение | Когда |
|---|---|---|---|
| **0 — сейчас** | `""` | Без auth (legacy). UI работает как раньше. | Текущий dev / прод до миграции |
| **1 — приёмка backend** | задан | Auth ВКЛЮЧЕНА на backend. Фронт без токена → 401. | Dev-стенд: проверить что middleware не ломает startup |
| **2 — фронт пробрасывает токен** | задан | UI логинится в IdP, шлёт `Authorization: Bearer …`. SignalR — через `accessTokenFactory`. | Дев → стейдж → прод |
| **3 — отозвать legacy** | задан | Удалить ветку `else` в `Program.cs`, оставить только включённый путь. | После того как все окружения прошли этап 2 |

---

## 📍 Применение в проекте

| Слой | Файл | Что добавлено |
|------|------|---------------|
| NuGet | [KiloImportService.Api/KiloImportService.Api.csproj](../KiloImportService.Api/KiloImportService.Api.csproj) | `Microsoft.AspNetCore.Authentication.JwtBearer 10.0.7` |
| Конфиг | [KiloImportService.Api/appsettings.json](../KiloImportService.Api/appsettings.json) | секция `Auth` (`Authority`/`Audience`/`RequireHttpsMetadata`) |
| Env | [.env.example](../.env.example) | `Auth__Authority`, `Auth__Audience`, `Auth__RequireHttpsMetadata` |
| Backend | [KiloImportService.Api/Program.cs](../KiloImportService.Api/Program.cs) | `AddJwtBearer` + `RequireAuthorization()` опционально |
| Frontend (auth) | [KiloImportService.Web/src/services/auth.ts](../KiloImportService.Web/src/services/auth.ts) | `setAccessTokenGetter` / `getAccessToken` |
| Frontend (REST) | [KiloImportService.Web/src/services/importsService.ts](../KiloImportService.Web/src/services/importsService.ts) | `withAuth(init)` обёртка перед `fetch` |
| Frontend (SignalR) | [KiloImportService.Web/src/services/importsHub.ts](../KiloImportService.Web/src/services/importsHub.ts) | `accessTokenFactory` в `withUrl` |
| Frontend (bootstrap) | [KiloImportService.Web/src/main.tsx](../KiloImportService.Web/src/main.tsx) | `setAccessTokenGetter(...)` на старте |

---

## 🎯 Чек-лист перед prod-выкаткой

- [ ] У админа IdP уточнено **точное значение `audience`** (claim `aud`) для нашего API
- [ ] В IdP **зарегистрирован client_id для UI** с redirect URI и flow `authorization_code + PKCE`
- [ ] Подключён `oidc-client-ts` (или аналог) на фронте; `localStorage`-заглушка в `main.tsx` заменена на `userManager.getUser()?.access_token`
- [ ] `Auth__Authority` задан в `.env` prod-окружения
- [ ] `Auth__RequireHttpsMetadata=true` на prod
- [ ] Health-эндпоинт (если есть) помечен `[AllowAnonymous]`
- [ ] Smoke-тест: без токена → 401, с валидным → 200, с истёкшим → 401
- [ ] Smoke-тест SignalR: подключение к `/hubs/imports` без токена → 401, с токеном — успех
- [ ] CORS-конфиг согласован: фронт+backend на разных origin → `AllowCredentials` и явный `WithOrigins`
- [ ] (Опционально) Ролевая модель: `[Authorize(Roles="ImportManager")]` или policy-based authz

---

## 🔗 Связанные документы

- [107-visary-token-provider.md](./107-visary-token-provider.md) — OIDC refresh_token flow для **исходящих** запросов в Visary (тот же IdP)
- [93-security-audit-workflow.md](./93-security-audit-workflow.md) — полный security-audit workflow (включая «нет auth на backend» как риск R0)
