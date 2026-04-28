# 🎨 CSS-паттерны прототипа

## 📋 Описание

CSS-паттерны, использованные в `KiloImportService.Web/src/App.css`. Документ фиксирует ключевые приёмы оформления, чтобы при изменениях не сломать визуал.

---

## 🎨 Цветовая палитра

```css
/* Фоны */
--bg-page:      #f3f4f5;   /* светло-серый фон страницы */
--bg-card:      #fff;      /* карточки, таблицы */
--bg-subtle:    #f7f8f9;   /* приглушённый фон для шапок таблиц, summary-card */
--bg-hover:     #fafbfc;   /* hover на строках таблицы */

/* Бренд */
--alfa-red:     #ef3124;   /* фирменный красный Альфа (filter-tag--active) */

/* Семантика */
--row-error-bg:    #fef4f4;
--row-warning-bg:  #fffaf0;
--alert-error-bg:  #fde9e7;
--alert-error-bd:  #fab9b3;
--alert-warn-bg:   #fff5dc;
--alert-warn-bd:   #ffe187;

/* Действия (Created/Updated/Skipped) */
--act-created-bg:  #d8f1d6;  /* зелёный */
--act-created-fg:  #1d7e2a;
--act-updated-bg:  #d0e7fe;  /* синий */
--act-updated-fg:  #1066c4;
--act-skipped-bg:  #ececec;  /* серый */
--act-skipped-fg:  #6b7280;

/* Тексты */
--text-primary:   #2c2d2e;
--text-secondary: #6b7280;
--text-muted:     #98989a;

/* Границы */
--border:         #ececec;
--border-soft:    #f3f4f5;
```

> ⚠️ В прототипе цвета захардкожены. При интеграции в Visary стоит вынести в CSS-переменные или взять из `@alfalab/core-components-vars`.

---

## 📐 Layout страницы

### ✅ Правильная реализация

```css
.app { min-height: 100vh; }

.app-header {
  background: #fff;
  border-bottom: 1px solid #e7e8ea;
  padding: 24px 0;
}

.app-main {
  padding: 32px 0 64px;  /* 👈 больший bottom для воздуха внизу */
}

.container {
  max-width: 1280px;     /* 👈 фиксированная макс. ширина */
  margin: 0 auto;
  padding: 0 24px;       /* 👈 отступы по бокам не дают прижаться к краям */
}
```

### ⚠️ Важно
- `.container` — общий класс для шапки и main; **не дублируем** padding-логику в каждом блоке
- `1280px` подходит для desktop-only сценария (UI Visary). Для мобильного — добавить медиа-запросы

---

## 🃏 Карточка (.card)

### ✅ Правильная реализация

```css
.card {
  background: #fff;
  border-radius: 16px;        /* 👈 крупный радиус — фирменная "пышность" Альфа */
  padding: 32px;               /* 👈 щедрые внутренние отступы */
  box-shadow: 0 1px 3px rgba(0, 0, 0, 0.04);  /* 👈 еле заметная тень */
  border: 1px solid #ececec;   /* 👈 граница для контраста на сером фоне */
}
```

### ⚠️ Важно
- **И тень, И граница** — на сером фоне `#f3f4f5` без границы карточка "плавает"
- `border-radius: 16px` — большой; для вложенных элементов (table, message) используем `12px` или `8px`

---

## 📊 Таблица отчёта

### ✅ Правильная реализация

```css
.table-wrapper {
  border: 1px solid #ececec;
  border-radius: 12px;
  overflow: hidden;        /* 👈 скрывает квадратные углы внутренних элементов */
}

.data-table {
  width: 100%;
  border-collapse: collapse;
  font-size: 14px;
}

.data-table thead th {
  background: #f7f8f9;
  text-align: left;
  padding: 12px 16px;
  font-weight: 600;
  font-size: 13px;        /* 👈 заголовки чуть мельче ячеек */
  color: #6b7280;          /* 👈 secondary text */
  border-bottom: 1px solid #ececec;
}

.data-table tbody td {
  padding: 12px 16px;
  border-bottom: 1px solid #f3f4f5;  /* 👈 более бледная граница, чем у шапки */
  vertical-align: top;     /* 👈 для строк с разной высотой содержимого */
}
```

### ⚠️ Важно — Collapse внутри таблицы

```css
.row-details td {
  padding: 0 !important;       /* 👈 ВАЖНО: убираем стандартный паддинг ячейки */
}

.row-details__content {
  padding: 24px;               /* 👈 паддинг переносим внутрь раскрываемого контента */
  background: #fafbfc;
  border-bottom: 1px solid #ececec;
}
```

### ❌ Типичная ошибка

```css
/* НЕПРАВИЛЬНО — паддинг ячейки + паддинг контента = двойной отступ */
.row-details td {
  padding: 24px;
}

/* НЕПРАВИЛЬНО — без overflow:hidden border-radius wrapper'а не работает */
.table-wrapper {
  border-radius: 12px;
  /* нет overflow: hidden — углы будут квадратные */
}
```

---

## 🏷️ Фильтр-теги

### ✅ Правильная реализация

```css
.filter-tags {
  display: flex;
  gap: 8px;
  margin-bottom: 16px;
  flex-wrap: wrap;     /* 👈 переносить на новую строку при нехватке места */
}

.filter-tag {
  background: #f3f4f5;
  border: 1px solid transparent;   /* 👈 transparent для предотвращения прыжка размера на hover */
  border-radius: 100px;            /* 👈 полностью скруглённый = "pill" */
  padding: 8px 16px;
  cursor: pointer;
  font-size: 14px;
  font-weight: 500;
  transition: all 0.15s;            /* 👈 плавность всех изменений */
  color: #2c2d2e;
  display: inline-flex;
  align-items: center;
  gap: 8px;                         /* 👈 расстояние до .filter-tag__count */
}

.filter-tag--active {
  background: #ef3124;              /* 👈 фирменный красный Альфа */
  color: #fff;
}

.filter-tag__count {
  background: #fff;
  color: #6b7280;
  border-radius: 100px;
  padding: 2px 8px;
  font-size: 12px;
  font-weight: 600;
  min-width: 24px;                 /* 👈 предотвращает "сплющивание" одной цифры */
  text-align: center;
}

.filter-tag--active .filter-tag__count {
  background: rgba(255, 255, 255, 0.25);  /* 👈 полупрозрачный белый на красном фоне */
  color: #fff;
}
```

### ⚠️ Важно
- `min-width: 24px` на счётчике — без него "1" и "100" будут разной ширины → прыжки лейаута
- `border: 1px solid transparent` в неактивном состоянии — резерв места под границу при hover/active
- Фирменный красный `#ef3124` — **не Bootstrap-красный**, а конкретный hex Альфа

---

## 📦 Карточки сводки (.summary-card)

### ✅ Правильная реализация

```css
.report-summary__cards {
  display: grid;
  grid-template-columns: repeat(4, 1fr);   /* 👈 4 в ряд на desktop */
  gap: 12px;
}

@media (max-width: 900px) {
  .report-summary__cards {
    grid-template-columns: repeat(2, 1fr); /* 👈 2 в ряд на planshet */
  }
}

.summary-card {
  background: #f7f8f9;        /* 👈 приглушённый фон */
  border-radius: 12px;
  padding: 16px;
  border: 1px solid #ececec;
}
```

### ⚠️ Важно
- **8 карточек** в макете → `repeat(4, 1fr)` = 2 ряда по 4
- Если карточек станет 6 или 9 — пересмотреть сетку (может быть `repeat(3, 1fr)`)

---

## 🎯 Action-бейджи (Created/Updated/Skipped)

### ✅ Правильная реализация

```css
.dest-action {
  font-weight: 600;
  font-size: 11px;
  padding: 2px 6px;
  border-radius: 4px;
  text-transform: uppercase;
  letter-spacing: 0.5px;       /* 👈 делает UPPERCASE читаемым */
}

.dest-line--created .dest-action { background: #d8f1d6; color: #1d7e2a; }
.dest-line--updated .dest-action { background: #d0e7fe; color: #1066c4; }
.dest-line--skipped .dest-action { background: #ececec; color: #6b7280; }
```

### ⚠️ Важно
- **Не используем** `Status` от Alfa — Status слишком крупный для inline-использования в ячейке таблицы
- Цвета **синхронизированы** с `summary-card` (зелёный = positive, синий = link)

---

## 💬 Сообщения (warnings/errors)

### ✅ Правильная реализация

```css
.messages {
  border-radius: 8px;
  padding: 12px 16px;
  margin-top: 12px;
}

.messages--warning {
  background: #fff5dc;       /* 👈 светло-жёлтый */
  border: 1px solid #ffe187;
}

.messages--error {
  background: #fde9e7;       /* 👈 светло-красный */
  border: 1px solid #fab9b3;
}

.message-row {
  display: flex;
  gap: 12px;
  padding: 6px 0;
  font-size: 13px;
  align-items: flex-start;   /* 👈 ВАЖНО: длинные сообщения не центрируются по vertical center */
}

.message-field {
  font-weight: 600;
  font-family: ui-monospace, "SF Mono", Consolas, monospace;  /* 👈 monospace для имени поля */
  background: rgba(255, 255, 255, 0.6);
  padding: 2px 6px;
  border-radius: 4px;
  font-size: 12px;
  flex-shrink: 0;            /* 👈 поле не сжимается при длинном сообщении */
  white-space: nowrap;
}
```

### ⚠️ Важно
- `flex-shrink: 0` на `.message-field` — иначе при длинном `message` имя поля сжимается в нечитаемое
- `align-items: flex-start` (а не `center`) — длинные сообщения переносятся вниз без сдвига имени поля

---

## 🔧 Inline-стили: когда можно

### ✅ Допустимо

```tsx
{/* Одноразовый отступ для конкретного instance компонента */}
<Typography.Text view="primary-small" style={{ marginTop: 4 }}>...</Typography.Text>
```

### ❌ Нельзя

```tsx
{/* Стиль на компоненте без поддержки style */}
<Divider style={{ margin: '24px 0' }} />     {/* ❌ */}
<Alert style={{ display: 'none' }}>...</Alert> {/* ❌ */}

{/* Стилизация структуры через inline */}
<div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 24 }}>...</div>
{/* ✅ Лучше: <div className="details-grid">...</div> */}
```

### Правило
- Inline `style` — только для **компонентов Typography** или одноразовых отступов на `<div>`
- Структурные стили (grid, flex layout) — всегда **классы** в `App.css`

---

## 📍 Применение в проекте

| Паттерн | Файл | Классы |
|---------|------|--------|
| Карточка | `KiloImportService.Web/src/App.css` | `.card`, `.section-gap` |
| Таблица отчёта | `KiloImportService.Web/src/App.css` | `.data-table`, `.table-wrapper`, `.row-details` |
| Фильтр-теги | `KiloImportService.Web/src/App.css` | `.filter-tag`, `.filter-tag--active`, `.filter-tag__count` |
| Карточки сводки | `KiloImportService.Web/src/App.css` | `.report-summary__cards`, `.summary-card` |
| Action-бейджи | `KiloImportService.Web/src/App.css` | `.dest-line`, `.dest-action`, `.dest-line--created` |
| Сообщения | `KiloImportService.Web/src/App.css` | `.messages`, `.message-row`, `.message-field` |

---

## 🎯 Чек-лист при изменении CSS

- [ ] Изменение цвета — проверил, что не задевает другие компоненты с тем же hex
- [ ] Новый класс — добавлен в **`App.css`** (не создаём отдельные CSS-файлы пока не нужно)
- [ ] BEM-нотация: `.block__element--modifier` (не camelCase, не PascalCase)
- [ ] Медиа-запрос — для всех новых grid-сеток с 3+ колонками
- [ ] Inline-стили — только для компонентов с поддержкой `style` (Typography, нативные `<div>`)
- [ ] Никаких `!important` кроме объяснённых исключений (см. `.row-details td`)
