namespace KiloImportService.Api.Domain.Importing;

/// <summary>
/// Статус сессии импорта (жизненный цикл).
/// Сериализуется как строка (lowercase) — такой же формат используется в UI.
/// </summary>
public enum ImportStatus
{
    /// <summary>Только что создана, файл загружен — ждёт парсинга.</summary>
    Pending,
    /// <summary>Идёт парсинг файла → StagedRow.</summary>
    Parsing,
    /// <summary>Идёт валидация распарсенных строк (правила, справочники).</summary>
    Validating,
    /// <summary>Парсинг + валидация завершены, можно делать <c>POST /imports/{id}/apply</c>.</summary>
    Validated,
    /// <summary>Идёт запись StagedRow → visary_db.</summary>
    Applying,
    /// <summary>Всё применено в visary_db, сессия успешно закрыта.</summary>
    Applied,
    /// <summary>Сессия закрыта с ошибками (парсинг/валидация/apply).</summary>
    Failed,
    /// <summary>Сессия отменена пользователем до apply.</summary>
    Cancelled
}

/// <summary>Этапы прогресса (для SignalR-нотификаций UI).</summary>
public enum ImportStageKind
{
    Upload,
    Parse,
    Validate,
    Apply
}

/// <summary>Статус строки в <c>StagedRow</c>.</summary>
public enum StagedRowStatus
{
    /// <summary>Строка распарсена и проходит валидацию.</summary>
    Pending,
    /// <summary>Прошла все валидации, готова к apply.</summary>
    Valid,
    /// <summary>Не прошла валидацию (см. <c>ImportError</c> с тем же RowNumber).</summary>
    Invalid,
    /// <summary>Уже записана в visary_db.</summary>
    Applied,
    /// <summary>Запись в visary_db не удалась (см. ImportError).</summary>
    Failed
}
