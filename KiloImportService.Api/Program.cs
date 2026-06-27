using KiloImportService.Api.Configuration;
using KiloImportService.Api.Data;
using KiloImportService.Api.Data.Visary;
using KiloImportService.Api.Domain.Importing;
using KiloImportService.Api.Domain.Importing.Parsers;
using KiloImportService.Api.Domain.Mapping;
using KiloImportService.Api.Domain.Pipeline;
using KiloImportService.Api.Domain.Projects;
using KiloImportService.Api.Domain.Sites;
using KiloImportService.Api.Hubs;
using KiloImportService.Api.Visary;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Microsoft.Extensions.Options;
using Visary.Api;
using Visary.Api.CRUD;
using Visary.Api.Dto;
using Visary.Api.Exceptions;
using Visary.Api.ListView;


// ─────────────────────────── Serilog (раннее логирование) ───────────────────────────
// Минимальный уровень снижен до Debug — на целевом стенде это первая зацепка
// для диагностики «белого экрана». Все ключевые этапы (старт хоста, миграции,
// маппинг роутов, HTTP-запросы, Pipeline, Visary-вызовы) пишутся в Console
// и попадают в `docker logs backend` / `kubectl logs <pod>` без доп. конфигурации.
// См. doc_project/147-base-path-and-logging.md.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("App", "KiloImportService.Api")
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

Log.Information("════════════════════════════════════════════════════════════");
Log.Information("KiloImportService.Api запускается. PID={Pid} Machine={Machine} .NET={Framework}",
    Environment.ProcessId, Environment.MachineName, Environment.Version);
Log.Information("CWD={Cwd}  ASPNETCORE_ENVIRONMENT={Env}",
    Directory.GetCurrentDirectory(),
    Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "(пусто)");
Log.Information("════════════════════════════════════════════════════════════");

try
{
    // SSOT для секретов — корневой `.env` (gitignored). Те же переменные читают
    // docker-compose, Vite (через envDir='..'), и backend здесь.
    // В контейнере docker-compose сам инжектит env'ы — этот вызов тогда no-op.
    DotEnvLoader.LoadFromAncestors(Directory.GetCurrentDirectory());

    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();

    // Имена секций конфига приведены к эталону `service-dev` (см. doc 145 / doc 132):
    //   EndpointsConfiguration:VisaryApi:Endpoint    — URL Visary API (был Visary:BaseUrl)
    //   EndpointsConfiguration:VisaryApi:BearerToken — токен legacy/dev (был Visary:BearerToken)
    //   EndpointsConfiguration:VisaryAuthApi:*       — OIDC refresh-flow (был Visary:Auth:*)
    //   JwtConfiguration:Authority/Audience/UseSsl   — JWT-валидация входящих (был Auth:*)
    //   Features:Swagger/RequireJwt                  — фичефлаги (был только Auth:Authority!='')
    //   ConnectionStrings:AbFmImport                 — ОДНА БД, две схемы (import + Data)
    // В env маппится двойным подчёркиванием: `EndpointsConfiguration__VisaryApi__Endpoint`.

    // ─── EF Core: одна БД (две схемы), два DbContext'а — см. doc 145 (вариант B1) ───
    // Обе схемы (`import` и `Data`) живут в одной БД `ab_fm_import`,
    // подключаемой через единый ConnectionStrings:AbFmImport. История миграций
    // у каждого контекста своя — таблица `__ef_migrations_history` ставится
    // в свою схему, чтобы не пересекаться.
    var abFmImportConn = builder.Configuration.GetConnectionString("AbFmImport");

    builder.Services.AddDbContext<ImportServiceDbContext>(opt =>
        opt.UseNpgsql(abFmImportConn,
            npg => npg.MigrationsHistoryTable("__ef_migrations_history", ImportServiceDbContext.SchemaName)));

    builder.Services.AddDbContext<VisaryDbContext>(opt =>
        opt.UseNpgsql(abFmImportConn,
            npg => npg.MigrationsHistoryTable("__ef_migrations_history", VisaryDbContext.DataSchema)));

    // ─── Парсеры (Strategy) ───
    builder.Services.AddSingleton<IFileParser, XlsxParser>();
    builder.Services.AddSingleton<IFileParser, CsvParser>();
    builder.Services.AddSingleton<IFileParser, XlsParser>();
    builder.Services.AddSingleton<IFileParser, XlsbParser>();
    builder.Services.AddSingleton<IFileParserFactory, FileParserFactory>();

    // ─── Мапперы (Strategy per importType) ───
    // Эталонный справочник статей бюджета (Title→Code КБК) — один на жизнь приложения.
    // Загружается лениво из embedded-ресурса при первом запросе.
    builder.Services.AddSingleton<KiloImportService.Api.Domain.Mapping.Budget.IBudgetReferenceProvider,
        KiloImportService.Api.Domain.Mapping.Budget.BudgetReferenceProvider>();
    builder.Services.AddSingleton<IImportMapper, FinModelImportMapper>();
    builder.Services.AddSingleton<IImportMapper, RoomsFormImportMapper>();
    builder.Services.AddSingleton<IImportMapperRegistry, ImportMapperRegistry>();

    // Snapshot store для инкрементального импорта «rooms»: хранит хэш применённых
    // MappedValues по бизнес-ключу (Site+Sheet+Section+Kind+RoomNumber+BuildingSection),
    // маппер диффает повторный импорт и skip-ает неизменённые строки. Scoped (зависит
    // от ImportServiceDbContext), маппер берёт через IServiceScopeFactory.
    builder.Services.AddScoped<KiloImportService.Api.Domain.Mapping.RoomApplySnapshotStore>();

    // ─── Pipeline + Storage ───
    builder.Services.AddScoped<ImportPipeline>();
    builder.Services.AddSingleton<IFileStorage, LocalFileStorage>();
     builder.Services.AddSingleton<IImportSessionCancellation, ImportSessionCancellation>();

    // ─── PDF-экспорт отчётов по сессиям (PDFsharp + DejaVu Sans для кириллицы) ───
    builder.Services.AddScoped<KiloImportService.Api.Pdf.ImportPdfReportService>();

    // ─── XLSX-экспорт бюджета по эталонному шаблону «Бюджет_А4.1» ───
    // (см. doc_project/78-budget-xlsx-export.md)
    builder.Services.AddScoped<KiloImportService.Api.Budget.BudgetXlsxExporter>();
    // Pipeline загрузки бюджета в Visary FileStorage + создание typedimportwbs
    // (см. doc_project/82-visary-file-storage-upload.md).
    builder.Services.AddScoped<KiloImportService.Api.Budget.IBudgetVisaryUploader, KiloImportService.Api.Budget.BudgetVisaryUploader>();

    // ─── Visary HTTP API клиент + кэш проектов ───
    builder.Services.AddVisaryClient(builder.Configuration.GetSection(VisaryOptions.SectionName));

    // ─── Visary auth: OIDC refresh_token flow (см. doc_project/107-visary-token-provider.md) ───
    // Включается только если задан TokenEndpoint — иначе остаётся дефолтный StaticVisaryTokenProvider
    // (читает Visary:BearerToken из .env, legacy/dev-fallback). Это позволяет включать OIDC
    // поэтапно: на dev-стенде сначала с .env-токеном, на prod — после настройки Vault.
    var visaryAuthSection = builder.Configuration.GetSection(Visary.Api.Auth.VisaryAuthOptions.SectionName);
    if (!string.IsNullOrWhiteSpace(visaryAuthSection["TokenEndpoint"]))
    {
        builder.Services.AddVisaryOidcAuth(visaryAuthSection);

        // TODO(doc 107): на prod-окружении заменить EnvironmentRefreshTokenStore на VaultRefreshTokenStore:
        //   builder.Services.Replace(
        //       ServiceDescriptor.Singleton<IVisaryRefreshTokenStore, VaultRefreshTokenStore>());
        // SDK-интеграция Vault (VaultSharp + AppRole/K8s auth) — отдельный коммит после
        // согласования endpoint'а и auth-mode с командой infra.
    }

    // ─── Visary справочники: прокси-эндпоинты /api/visary/{name} и /{name}/{id} ───
    // Расширение: чтобы добавить новый справочник — одна строка ниже.
    builder.Services
        .AddVisaryDictionary<TownRaw>("towns",
            (lv, q, ct) => lv.ListTownsAsync(q, ct),
            (cr, id, ct) => cr.GetTownByIdAsync(id, ct))
        .AddVisaryDictionary<RegionRaw>("regions",
            (lv, q, ct) => lv.ListRegionsAsync(q, ct),
            (cr, id, ct) => cr.GetRegionByIdAsync(id, ct))
        .AddVisaryDictionary<ProjectTypeRaw>("projecttypes",
            (lv, _, ct) => lv.ListProjectTypesAsync(ct),
            (cr, id, ct) => cr.GetProjectTypeByIdAsync(id, ct))
        .AddVisaryDictionary<InflationCalcMethodRaw>("inflationcalcmethods",
            (lv, _, ct) => lv.ListInflationCalcMethodsAsync(ct),
            (cr, id, ct) => cr.GetInflationCalcMethodByIdAsync(id, ct))
        .AddVisaryDictionary<EstateClassRaw>("estateclasses",
            (lv, _, ct) => lv.ListEstateClassesAsync(ct),
            (cr, id, ct) => cr.GetEstateClassByIdAsync(id, ct))
        .AddVisaryDictionary<BuildingMaterialRaw>("buildingmaterials",
            (lv, _, ct) => lv.ListBuildingMaterialsAsync(ct),
            (cr, id, ct) => cr.GetBuildingMaterialByIdAsync(id, ct))
        .AddVisaryDictionary<FinishingMaterialRaw>("finishingmaterials",
            (lv, _, ct) => lv.ListFinishingMaterialsAsync(ct),
            (cr, id, ct) => cr.GetFinishingMaterialByIdAsync(id, ct))
        .AddVisaryDictionary<RoomKindRaw>("roomkinds",
            (lv, _, ct) => lv.ListRoomKindsAsync(ct),
            (cr, id, ct) => cr.GetRoomKindByIdAsync(id, ct));
    
    // ProjectsCacheService теперь использует IListViewClient (см. AddVisaryClient выше),
    // а не сырой HttpClient. Регистрируем как обычный scoped-сервис.
    builder.Services.AddScoped<IProjectsCacheService, ProjectsCacheService>();
    builder.Services.AddScoped<ISitesSyncService, SitesSyncService>();

    // ─── SignalR ───
    builder.Services.AddSignalR();

    // ─── Web API ───
    builder.Services.AddControllers();
    
    builder.Services.AddHealthChecks();

    // ─── Swagger по эталону service-dev (см. doc 132 / doc 145) ───
    // Регистрация + UI оба под `Features:Swagger`. На prod выключен, на dev-yaml
    // включается через `Features__Swagger: true`. От `IsDevelopment()` намеренно
    // ушли — `ASPNETCORE_ENVIRONMENT: Test` на dev-стенде даёт IsDevelopment()=false.
    var featuresSwagger = builder.Configuration.GetValue("Features:Swagger", defaultValue: false);
    if (featuresSwagger)
    {
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(o =>
        {
            o.SwaggerDoc("v1", new() { Title = "KiloImportService API", Version = "v1" });
        });
    }

    // ─── CORS для UI ───
    // Прод-deploy в k8s — same-origin: фронт и backend на одном Ingress
    // (`https://abdev.moscow.alfaintra.net/` → UI, `/api/ab-fm-import/...` → backend),
    // браузер делает запросы без CORS. На таких контурах `Cors` в helm values
    // не задаётся → политика не регистрируется, middleware не подключается.
    //
    // Локальный dev запускает Vite на `http://localhost:5173` отдельно от backend
    // (порт 5000/8080), и тогда CORS нужен — `Cors` в .env задаётся явно.
    // Несколько origin'ов — через запятую.
    var corsRaw = builder.Configuration["Cors"];
    var corsEnabled = !string.IsNullOrWhiteSpace(corsRaw);
    string[] allowedOrigins = corsEnabled
        ? corsRaw!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        : Array.Empty<string>();

    if (corsEnabled)
    {
        builder.Services.AddCors(o => o.AddPolicy("ui", p => p
            .WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()));
    }

    // ─── Аутентификация ВХОДЯЩИХ запросов (JWT bearer от того же IdP, что и Visary) ───
    // Эталон service-dev (см. doc 132 / doc 145): включение по `Features:RequireJwt`,
    // параметры — в `JwtConfiguration:{Authority,Audience,ValidIssuer,UseSsl,Secret}`.
    // `RequireJwt=false` → backend работает БЕЗ auth (legacy/dev-режим).
    // `RequireJwt=true` И пустой Authority → fatal на старте (видно сразу, без молчаливого OFF).
    var jwtConfig = builder.Configuration.GetSection("JwtConfiguration");
    var requireJwt = builder.Configuration.GetValue("Features:RequireJwt", defaultValue: false);
    var incomingAuthority = jwtConfig["Authority"];

    if (requireJwt && string.IsNullOrWhiteSpace(incomingAuthority))
        throw new InvalidOperationException(
            "Features:RequireJwt=true, но JwtConfiguration:Authority пуст. " +
            "В helm values задаётся как `$(IDENTIRY_URL)` (см. yaml + doc 145).");

    var incomingAuthEnabled = requireJwt && !string.IsNullOrWhiteSpace(incomingAuthority);

    if (incomingAuthEnabled)
    {
        var incomingAudience = jwtConfig["Audience"];
        // UseSsl=true (эталонное имя) ⇒ требуем HTTPS у IdP metadata-endpoint'а.
        var requireHttps = jwtConfig.GetValue("UseSsl", defaultValue: true);

        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(opt =>
            {
                opt.Authority = incomingAuthority;
                opt.Audience  = incomingAudience;
                opt.RequireHttpsMetadata = requireHttps;

                opt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer           = true,
                    ValidateAudience         = !string.IsNullOrWhiteSpace(incomingAudience),
                    ValidateLifetime         = true,
                    ValidateIssuerSigningKey = true,
                    ClockSkew                = TimeSpan.FromMinutes(2)
                };

                // SignalR не может слать Authorization-header при WebSocket-апгрейде,
                // поэтому при запросе на /hubs/* берём токен из query string ?access_token=…
                opt.Events = new JwtBearerEvents
                {
                    OnMessageReceived = ctx =>
                    {
                        var token = ctx.Request.Query["access_token"];
                        var path  = ctx.HttpContext.Request.Path;
                        if (!string.IsNullOrEmpty(token) &&
                            path.StartsWithSegments("/hubs"))
                        {
                            ctx.Token = token;
                        }
                        return Task.CompletedTask;
                    }
                };
            });

        builder.Services.AddAuthorization();
        Log.Information("Incoming JWT auth ENABLED. Authority={Authority} Audience={Audience}",
            incomingAuthority, incomingAudience);
    }
    else
    {
        Log.Warning("Incoming JWT auth DISABLED (Features:RequireJwt=false или JwtConfiguration:Authority пуст). " +
                    "Backend принимает запросы БЕЗ аутентификации — допустимо только в dev/legacy.");
    }

    // ─── Forwarded headers (за reverse-proxy / k8s ingress) ───
    // ingress присылает X-Forwarded-Proto/Host/Prefix/For, без UseForwardedHeaders
    // фреймворк считает схему `http` (а не `https`) и собирает редиректы/Hub-URL
    // некорректно. KnownNetworks/KnownProxies очищаем — в k8s pod-CIDR заранее
    // неизвестен, доверяем любому upstream'у внутри кластера (граница — ingress).
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor
                           | ForwardedHeaders.XForwardedProto
                           | ForwardedHeaders.XForwardedHost
                           | ForwardedHeaders.XForwardedPrefix;
        // В .NET 10 `KnownNetworks` объявлен устаревшим — используем `KnownIPNetworks`.
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
    });

    var app = builder.Build();

    // ─── Базовый путь под reverse-proxy (опц., см. doc 147) ───
    // На целевом стенде сервис публикуется под префиксом `/api/ab-fm-import`
    // (см. UI: `/api/ab-fm-import-web`). UsePathBase «отрезает» этот префикс
    // у входящего PathBase, чтобы контроллеры/хабы продолжали отдаваться по
    // относительным маршрутам (`/api/imports`, `/hubs/imports`), а LinkGenerator
    // собирал URL'ы с префиксом — нужный поведению SignalR negotiate и Swagger.
    //
    // ⚠️ Пусто (по дефолту) — поведение БЕЗ префикса, локалка не ломается.
    // На стенде задаётся `Features:PathBase=/api/ab-fm-import` через helm values.
    var pathBase = (builder.Configuration["Features:PathBase"] ?? string.Empty).Trim();
    if (!string.IsNullOrEmpty(pathBase))
    {
        if (!pathBase.StartsWith('/')) pathBase = "/" + pathBase;
        pathBase = pathBase.TrimEnd('/');
        app.UsePathBase(pathBase);
        Log.Information("PathBase активен: {PathBase} (все маршруты будут доступны под этим префиксом)", pathBase);
    }
    else
    {
        Log.Information("PathBase НЕ задан (Features:PathBase пусто) — маршруты обслуживаются от корня '/'");
    }

    app.UseForwardedHeaders();

    // ─── Auto-apply миграций для обоих DbContext'ов одной БД AbFmImport ───
    // Схемы:
    //   `import` — служебные таблицы сервиса (управляются ImportServiceDbContext)
    //   `Data`   — Visary-mirror (управляется VisaryDbContext, заменяет старые
    //              `db/visary/init/*.sql` init-скрипты; см. doc 145 / B1).
    // ⚠️ EF tools (dotnet ef migrations add) выполняют код Program.cs до app.RunAsync(),
    // чтобы построить хост и достать DbContext. Без guard EF.IsDesignTime попытка
    // подключиться к реальному Postgres сломает scaffolding, когда БД не запущена.
    if (!EF.IsDesignTime)
    {
        using var scope = app.Services.CreateScope();
        var importDb = scope.ServiceProvider.GetRequiredService<ImportServiceDbContext>();
        Log.Information("Applying ImportServiceDb migrations (schema=import)…");
        await importDb.Database.MigrateAsync();

        var visaryDb = scope.ServiceProvider.GetRequiredService<VisaryDbContext>();
        Log.Information("Applying VisaryDb migrations (schema=Data)…");
        await visaryDb.Database.MigrateAsync();
    }
    
    app.MapHealthChecks("/health/live");
    app.MapHealthChecks("/health/ready");

    // Swagger middleware подключается под тем же `Features:Swagger`, что и регистрация
    // services выше (эталон service-dev, см. doc 145).
    if (featuresSwagger)
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    // Глобальный exception-handler: ловит и логирует ВСЁ, что не поймали контроллеры.
    // Без него ASP.NET Core отдаёт 500 без тела, в логах остаётся только короткая запись
    // от UseSerilogRequestLogging — без stack-trace. На целевом стенде это критично для
    // диагностики «белого экрана»: фронтенд видит 500/CORS и молча падает.
    app.Use(async (ctx, next) =>
    {
        try
        {
            await next();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Необработанное исключение в pipeline. {Method} {Path}{Query} → 500",
                ctx.Request.Method, ctx.Request.Path, ctx.Request.QueryString);
            if (!ctx.Response.HasStarted)
            {
                ctx.Response.StatusCode = 500;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await ctx.Response.WriteAsync(
                    "{\"error\":\"Внутренняя ошибка сервера. Смотри логи backend по correlationId в заголовке X-Trace-Id.\"}");
            }
        }
    });

    // Расширенный request-log: запись на каждый HTTP-запрос с длительностью,
    // статусом, длиной ответа, схемой (после ForwardedHeaders — реальный https)
    // и PathBase. Помогает увидеть, какие именно URL-ы хитятся клиентом и где
    // ломается роутинг при включённом PathBase.
    app.UseSerilogRequestLogging(opt =>
    {
        opt.MessageTemplate =
            "HTTP {RequestMethod} {RequestPath} → {StatusCode} за {Elapsed:0} мс  (scheme={Scheme} host={Host} pathBase={PathBase})";
        opt.EnrichDiagnosticContext = (diag, http) =>
        {
            diag.Set("Scheme", http.Request.Scheme);
            diag.Set("Host", http.Request.Host.Value ?? "-");
            diag.Set("PathBase", http.Request.PathBase.HasValue ? http.Request.PathBase.Value : "-");
            diag.Set("UserAgent", (string?)http.Request.Headers.UserAgent ?? "-");
        };
    });
    app.UseRouting();
    if (corsEnabled)
        app.UseCors("ui");

    if (incomingAuthEnabled)
    {
        app.UseAuthentication();
        app.UseAuthorization();

        // Deny-by-default: все контроллеры/хабы требуют валидный JWT.
        // Что должно быть публичным (health, swagger) — пометить [AllowAnonymous].
        app.MapControllers().RequireAuthorization();
        app.MapHub<ImportProgressHub>("/hubs/imports").RequireAuthorization();
    }
    else
    {
        app.MapControllers();
        app.MapHub<ImportProgressHub>("/hubs/imports");
    }

    // Диагностический endpoint — выдаёт текущий PathBase, окружение, версию,
    // CORS-allowlist и список endpoint'ов сервиса. Полезен, чтобы убедиться:
    // на целевом стенде фронт реально достал backend по правильному URL'у, и
    // под каким именно префиксом смонтированы контроллеры/хабы.
    // Открыт публично (по эталону /health тоже без auth). Не отдаёт секреты.
    app.MapGet("/diag", (HttpContext ctx, IConfiguration cfg) =>
    {
        var endpointSource = ctx.RequestServices.GetRequiredService<EndpointDataSource>();
        var routes = endpointSource.Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => new
            {
                pattern = e.RoutePattern.RawText,
                methods = e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods,
                displayName = e.DisplayName
            })
            .OrderBy(r => r.pattern)
            .ToArray();
        return Results.Ok(new
        {
            app = "KiloImportService.Api",
            framework = Environment.Version.ToString(),
            environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
            pathBase = string.IsNullOrEmpty(pathBase) ? "/" : pathBase,
            requestPathBase = ctx.Request.PathBase.HasValue ? ctx.Request.PathBase.Value : "/",
            requestScheme = ctx.Request.Scheme,
            requestHost = ctx.Request.Host.Value,
            cors = cfg["Cors"],
            features = new
            {
                swagger = cfg.GetValue("Features:Swagger", defaultValue: false),
                requireJwt = cfg.GetValue("Features:RequireJwt", defaultValue: false),
                pathBase = cfg["Features:PathBase"]
            },
            visaryEndpoint = cfg["EndpointsConfiguration:VisaryApi:Endpoint"],
            endpointsCount = routes.Length,
            endpoints = routes
        });
    });

    // ─── Стартовый дамп конфигурации в консоль (видно сразу в `docker logs`) ───
    var urls = builder.WebHost.GetSetting("urls") ?? "default";
    Log.Information("┌── Конфигурация сервиса ─────────────────────────────────────");
    Log.Information("│ URLs        : {Urls}", urls);
    Log.Information("│ PathBase    : {PathBase}", string.IsNullOrEmpty(pathBase) ? "(не задан)" : pathBase);
    Log.Information("│ CORS        : {Cors}", corsEnabled ? string.Join(", ", allowedOrigins) : "DISABLED (same-origin)");
    Log.Information("│ Visary API  : {VisaryEndpoint}", builder.Configuration["EndpointsConfiguration:VisaryApi:Endpoint"] ?? "(пусто)");
    Log.Information("│ JWT auth    : {Auth}", incomingAuthEnabled ? "ENABLED" : "DISABLED");
    Log.Information("│ Swagger     : {Swagger}", featuresSwagger ? "ENABLED" : "DISABLED");
    Log.Information("│ DB conn-str : {Conn}", MaskConnectionString(abFmImportConn));
    Log.Information("└─────────────────────────────────────────────────────────────");

    // Перечисление endpoint'ов в логе старта — видно, какие маршруты реально подняты
    // (включая контроллеры через рефлексию). Без этого «404 на /api/imports» можно
    // искать долго: может, контроллер не подхватился из-за ошибки атрибута.
    //
    // ⚠️ EndpointDataSource через DI до старта сервера populated НЕ полностью:
    // композитный source собирается лениво, и запрос .Endpoints в момент конфигурации
    // возвращает пустой список (наблюдалось на abtest-деплое v0.0.1.50: «Зарегистрировано
    // 0 HTTP-маршрутов» при реально работающих MapControllers/MapHub). Поэтому
    // выводим список из callback'а ApplicationStarted — он срабатывает после
    // полной инициализации routing'а.
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        try
        {
            var src = app.Services.GetRequiredService<EndpointDataSource>();
            var patterns = src.Endpoints
                .OfType<RouteEndpoint>()
                .Select(e => e.RoutePattern.RawText)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .OrderBy(p => p)
                .Distinct()
                .ToArray();
            Log.Information("Зарегистрировано {Count} HTTP-маршрутов:", patterns.Length);
            foreach (var p in patterns)
                Log.Information("    {Method,-6} {Pattern}", "*", p);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Не удалось перечислить HTTP-маршруты в логе старта");
        }
    });

    Log.Information("KiloImportService.Api готов принимать запросы на {Urls}", urls);
    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Fatal error during startup");
}
finally
{
    Log.CloseAndFlush();
}

// Локальный helper для безопасного дампа connection-string в лог.
// Не печатает значение Password=...; — только структуру и имя сервера.
static string MaskConnectionString(string? cs)
{
    if (string.IsNullOrWhiteSpace(cs)) return "(пусто)";
    var parts = cs.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var safe = parts.Select(p =>
    {
        var idx = p.IndexOf('=');
        if (idx <= 0) return p;
        var key = p[..idx];
        var keyLower = key.ToLowerInvariant();
        if (keyLower is "password" or "pwd")
            return $"{key}=***";
        return p;
    });
    return string.Join(';', safe);
}
