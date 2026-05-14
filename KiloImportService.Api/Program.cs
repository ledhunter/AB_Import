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
using Microsoft.EntityFrameworkCore;
using Serilog;
using Microsoft.Extensions.Options;
using Visary.Api;
using Visary.Api.CRUD;
using Visary.Api.Dto;
using Visary.Api.Exceptions;
using Visary.Api.ListView;


// ─────────────────────────── Serilog (раннее логирование) ───────────────────────────
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    // SSOT для секретов — корневой `.env` (gitignored). Те же переменные читают
    // docker-compose, Vite (через envDir='..'), и backend здесь.
    // В контейнере docker-compose сам инжектит env'ы — этот вызов тогда no-op.
    DotEnvLoader.LoadFromAncestors(Directory.GetCurrentDirectory());

    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();

    // Visary__BearerToken (и прочие Visary__*) приходит через стандартный
    // AddEnvironmentVariables — двойное подчёркивание мапится в Visary:BearerToken.
    // Hot-reload токена больше нет: для смены — обновить `.env` и перезапустить процесс
    // (контейнер: docker compose up -d --force-recreate backend).

    // ─── EF Core: 2 контекста на 2 PostgreSQL ───
    builder.Services.AddDbContext<ImportServiceDbContext>(opt =>
        opt.UseNpgsql(builder.Configuration.GetConnectionString("ServiceDb"),
            npg => npg.MigrationsHistoryTable("__ef_migrations_history", ImportServiceDbContext.SchemaName)));

    builder.Services.AddDbContext<VisaryDbContext>(opt =>
        opt.UseNpgsql(builder.Configuration.GetConnectionString("VisaryDb")));

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
    builder.Services.AddScoped<KiloImportService.Api.Budget.BudgetVisaryUploader>();

    // ─── Visary HTTP API клиент + кэш проектов ───
    builder.Services.AddVisaryClient(builder.Configuration.GetSection(VisaryOptions.SectionName));

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

    // ─── Web API + Swagger ───
    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(o =>
    {
        o.SwaggerDoc("v1", new() { Title = "KiloImportService API", Version = "v1" });
    });

    // ─── CORS для UI ───
    var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                         ?? ["http://localhost:5173"];
    builder.Services.AddCors(o => o.AddPolicy("ui", p => p
        .WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()));

    var app = builder.Build();

    // ─── Auto-apply миграций для service-db при старте ───
    // (Visary-БД управляется внешними init-скриптами, миграциями не трогаем.)
    // ⚠️ EF tools (dotnet ef migrations add) выполняют код Program.cs до app.RunAsync(),
    // чтобы построить хост и достать DbContext. Без guard EF.IsDesignTime попытка
    // подключиться к реальному Postgres сломает scaffolding, когда БД не запущена.
    if (!EF.IsDesignTime)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ImportServiceDbContext>();
        Log.Information("Applying ImportServiceDb migrations…");
        await db.Database.MigrateAsync();
    }

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseSerilogRequestLogging();
    app.UseCors("ui");
    app.MapControllers();
    app.MapHub<ImportProgressHub>("/hubs/imports");

    Log.Information("Starting KiloImportService.Api on {Urls}", string.Join(", ", builder.WebHost.GetSetting("urls") ?? "default"));
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
