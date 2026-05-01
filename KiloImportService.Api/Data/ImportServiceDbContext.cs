using System.Text.Json;
using KiloImportService.Api.Data.Entities;
using KiloImportService.Api.Domain.Importing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace KiloImportService.Api.Data;

/// <summary>
/// EF Core контекст для служебной БД <c>import_service_db</c>.
/// Управляется через миграции (<c>dotnet ef migrations add ...</c>).
///
/// Все таблицы лежат в схеме <c>import</c>, чтобы не конфликтовать с возможными
/// служебными таблицами Postgres и было видно по имени, что это наша БД.
/// </summary>
public class ImportServiceDbContext : DbContext
{
    public const string SchemaName = "import";

    public ImportServiceDbContext(DbContextOptions<ImportServiceDbContext> options) : base(options) { }

    public DbSet<ImportSession> Sessions => Set<ImportSession>();
    public DbSet<ImportSessionStage> Stages => Set<ImportSessionStage>();
    public DbSet<StagedRow> StagedRows => Set<StagedRow>();
    public DbSet<ImportError> Errors => Set<ImportError>();
    public DbSet<ImportFileSnapshot> FileSnapshots => Set<ImportFileSnapshot>();
    public DbSet<CachedProject> CachedProjects => Set<CachedProject>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(SchemaName);

        // ─── ImportSession ───
        b.Entity<ImportSession>(e =>
        {
            e.ToTable("import_sessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.ImportTypeCode).HasMaxLength(64).IsRequired();
            e.Property(x => x.FileName).HasMaxLength(500).IsRequired();
            e.Property(x => x.FileFormat).HasConversion<string>().HasMaxLength(8);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.UserId).HasMaxLength(128);
            e.Property(x => x.ErrorMessage).HasMaxLength(4000);

            // Пользователь может загружать один и тот же файл несколько раз (с разными проектами/объектами).
            // SHA256 больше не используется как уникальное ограничение.
            e.HasIndex(x => x.ImportTypeCode).HasDatabaseName("IX_ImportSession_Type");

            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.StartedAt);
        });

        // ─── ImportSessionStage ───
        b.Entity<ImportSessionStage>(e =>
        {
            e.ToTable("import_session_stages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Message).HasMaxLength(500);
            e.HasOne(x => x.Session)
             .WithMany(s => s.Stages)
             .HasForeignKey(x => x.ImportSessionId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.ImportSessionId);
        });

        // ─── StagedRow ───
        b.Entity<StagedRow>(e =>
        {
            e.ToTable("staged_rows");
            e.HasKey(x => x.Id);

            // ⚠️ Npgsql нативно поддерживает JsonDocument↔jsonb. InMemory-провайдер
            // (используется в unit-тестах) такого маппинга не имеет, поэтому
            // подключаем value-конвертер JsonDocument↔string ТОЛЬКО для не-Npgsql.
            // См. doc_project/17-backend-tests-xunit.md и 18-projects-cache.md.
            if (Database.IsNpgsql())
            {
                e.Property(x => x.RawValues).HasColumnType("jsonb").IsRequired();
                e.Property(x => x.MappedValues).HasColumnType("jsonb");
            }
            else
            {
                e.Property(x => x.RawValues).HasConversion(JsonDocConverter).IsRequired();
                e.Property(x => x.MappedValues).HasConversion(JsonDocConverter);
            }

            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasOne(x => x.Session)
             .WithMany(s => s.Rows)
             .HasForeignKey(x => x.ImportSessionId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ImportSessionId, x.SourceRowNumber }).IsUnique();
            e.HasIndex(x => new { x.ImportSessionId, x.Status });
        });

        // ─── ImportError ───
        b.Entity<ImportError>(e =>
        {
            e.ToTable("import_errors");
            e.HasKey(x => x.Id);
            e.Property(x => x.ErrorCode).HasMaxLength(64).IsRequired();
            e.Property(x => x.Message).HasMaxLength(2000).IsRequired();
            e.Property(x => x.ColumnName).HasMaxLength(255);
            e.HasOne(x => x.Session)
             .WithMany(s => s.Errors)
             .HasForeignKey(x => x.ImportSessionId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ImportSessionId, x.SourceRowNumber });
        });

        // ─── ImportFileSnapshot ───
        b.Entity<ImportFileSnapshot>(e =>
        {
            e.ToTable("import_file_snapshots");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.RelativePath).HasMaxLength(500).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(128);
            e.HasOne(x => x.Session)
             .WithOne(s => s.FileSnapshot!)
             .HasForeignKey<ImportFileSnapshot>(x => x.ImportSessionId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.ImportSessionId).IsUnique();
        });

        // ─── CachedProject ───
        // Локальный кэш проектов Visary для поиска-as-you-type.
        b.Entity<CachedProject>(e =>
        {
            e.ToTable("cached_projects");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Title).HasMaxLength(500).IsRequired();
            // ⚠️ Коды KK/ZPLM в реальных проектах бывают длинными (встречаются >64 символов)
            // — поэтому оставляем запас; 255 хватит с большим запасом.
            e.Property(x => x.IdentifierKK).HasMaxLength(255);
            e.Property(x => x.IdentifierZPLM).HasMaxLength(255);
            // Индексы под ILIKE-поиск (citext не используем — pg_trgm не обязателен).
            e.HasIndex(x => x.Title).HasDatabaseName("IX_CachedProject_Title");
            e.HasIndex(x => x.IdentifierKK).HasDatabaseName("IX_CachedProject_IdentifierKK");
        });

        base.OnModelCreating(b);
    }

    /// <summary>
    /// Запасной конвертер JsonDocument↔string для провайдеров без нативной jsonb-поддержки
    /// (InMemory в тестах). Хранение происходит как сериализованная JSON-строка;
    /// производительность нерелевантна, потому что используется только в unit-тестах.
    /// </summary>
    private static readonly ValueConverter<JsonDocument, string> JsonDocConverter =
        new(
            v => v == null ? "{}" : v.RootElement.GetRawText(),
            v => JsonDocument.Parse(string.IsNullOrWhiteSpace(v) ? "{}" : v, default));
}
