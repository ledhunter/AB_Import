# 🎯 Dropdown «Проект»: пустой запрос возвращает кэш

## 📋 Описание

Select «Проект» в форме импорта при первом открытии вызывает `searchProjects('')` (см. [`useBackendProjects.ts`](../KiloImportService.Web/src/hooks/useBackendProjects.ts) — стратегия **probe-then-sync**, doc 18). Если probe возвращает пустой список — frontend идёт в `POST /api/projects/sync`, который ходит в Visary.

В нашей сети Visary недоступен (`https://isup-alfa-test.k8s.npc.ba` → TLS handshake fail / 502). Поэтому **probe должен сам отдавать кэш**, чтобы 502 от Visary не блокировал UI.

> 🔁 См. также: `18-projects-cache.md`, `20-select-with-search.md`, `09-lazy-loaded-select.md`.

---

## ✅ Правильная реализация

### Backend — `ProjectsCacheService.SearchLocalAsync`

```csharp
private async Task<List<CachedProject>> SearchLocalAsync(
    string query, int take, CancellationToken ct)
{
    if (string.IsNullOrEmpty(query))
    {
        // 👇 Пустой запрос = первая страница кэша по алфавиту.
        // Это нужно для probe-then-sync в useBackendProjects: фронт
        // открывает Select без ввода → должен сразу видеть проекты.
        return await _db.CachedProjects
            .OrderBy(p => p.Title)
            .Take(take)
            .ToListAsync(ct);
    }

    // Обычный поиск по подстроке (Title / IdentifierKK / IdentifierZPLM)
    var lowered = query.ToLowerInvariant();
    return await _db.CachedProjects
        .Where(p =>
            EF.Functions.Like(p.Title.ToLower(), $"%{lowered}%") ||
            (p.IdentifierKK != null && EF.Functions.Like(p.IdentifierKK.ToLower(), $"%{lowered}%")) ||
            (p.IdentifierZPLM != null && EF.Functions.Like(p.IdentifierZPLM.ToLower(), $"%{lowered}%")))
        .OrderBy(p => p.Title)
        .Take(take)
        .ToListAsync(ct);
}
```

### ⚠️ Важно

- `SearchAsync` уже корректно обрабатывает пустой query: при `local.Count > 0 || string.IsNullOrEmpty(trimmed)` возвращает локальный результат **без** fallback'а в Visary. Поэтому пустой запрос **никогда** не дёргает Visary.
- На уровне SQL: `OrderBy(Title) + Take(N)` транслируется в `ORDER BY title LIMIT N` — индекс по `Title` ускоряет (если будет нужно — добавить `CREATE INDEX idx_cached_projects_title`).
- Возвращаемый размер ограничен `take` (по умолчанию `DefaultSearchLimit`, max 200).

---

## ❌ Типичная ошибка

```csharp
// НЕПРАВИЛЬНО — пустой query → пустой ответ → frontend идёт в /sync → 502 от Visary → UI «Ошибка»
private async Task<List<CachedProject>> SearchLocalAsync(string query, int take, CancellationToken ct)
{
    if (string.IsNullOrEmpty(query))
    {
        return new List<CachedProject>(); // ❌ блокирует UI при недоступном Visary
    }
    // ...
}
```

**Симптом**: Select открывается пустым, в консоли `POST /api/projects/sync → 502`, в БД `cached_projects.Count > 0`.

---

## 📍 Применение в проекте

| Компонент | Файл | Что делает |
|-----------|------|-----------|
| `ProjectsCacheService` | [Domain/Projects/ProjectsCacheService.cs](../KiloImportService.Api/Domain/Projects/ProjectsCacheService.cs) | `SearchLocalAsync` отдаёт кэш по пустому query |
| `useBackendProjects` (probe) | [hooks/useBackendProjects.ts:122](../KiloImportService.Web/src/hooks/useBackendProjects.ts#L122) | Пробует `searchProjects('')` перед sync |
| Тест empty-query | [Projects/ProjectsCacheServiceTests.cs](../KiloImportService.Api.Tests/Projects/ProjectsCacheServiceTests.cs) | `SearchAsync_EmptyQuery_ReturnsCachedListSortedByTitle` |

---

## 🎯 Чек-лист

- [ ] Открыть Select «Проект» без ввода — список заполнен (если в `cached_projects` ≥ 1 строки).
- [ ] В Network tab: `GET /api/projects/search?limit=50` → 200, `items.length > 0`, `fromFallback=false`.
- [ ] `POST /api/projects/sync` **не вызывается**, если кэш не пуст.
- [ ] Ввод подстроки фильтрует список локально (без обращения к Visary, кроме случая «локально 0 результатов»).
- [ ] Тест `SearchAsync_EmptyQuery_ReturnsCachedListSortedByTitle` проходит.
