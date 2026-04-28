using KiloImportService.Api.Data;
using KiloImportService.Api.Data.Visary;
using KiloImportService.Api.Domain.Importing;
using KiloImportService.Api.Domain.Importing.Parsers;
using KiloImportService.Api.Domain.Mapping;
using KiloImportService.Api.Domain.Pipeline;
using KiloImportService.Api.Hubs;
using Microsoft.EntityFrameworkCore;
using Serilog;

// ─────────────────────────── Serilog (раннее логирование) ───────────────────────────
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();

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
    builder.Services.AddSingleton<IImportMapper, RoomsImportMapper>();
    // … добавлять по мере реализации новых типов импорта
    builder.Services.AddSingleton<IImportMapperRegistry, ImportMapperRegistry>();

    // ─── Pipeline + Storage ───
    builder.Services.AddScoped<ImportPipeline>();
    builder.Services.AddSingleton<IFileStorage, LocalFileStorage>();

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
    using (var scope = app.Services.CreateScope())
    {
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
