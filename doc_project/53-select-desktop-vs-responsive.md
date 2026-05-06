# 🎨 SelectDesktop vs SelectResponsive (Bottom Sheet)

## 📋 Описание

`@alfalab/core-components/select` — это **`SelectResponsive`**, который автоматически переключается между:
- **desktop** — классический dropdown-поповер ниже поля,
- **mobile** — Bottom Sheet (модалка снизу с поиском в стиле «формы»).

Решение принимается по ширине viewport / user-agent. На узком окне (< ~768px, или DevTools mobile preview, или просто ресайз браузера в шторку IDE) Select превращается в Bottom Sheet — пользователь видит «форму с поиском», а не привычный dropdown.

Для форм импорта (всегда desktop-окружение) нужен **жёстко классический dropdown** — без responsive-логики.

> 🔁 См. также: `01-alfa-core-components-api.md`, `20-select-with-search.md`.

---

## ✅ Правильная реализация

```tsx
// 👇 Явный импорт desktop-варианта — всегда поповер-дропдаун
import { SelectDesktop as Select } from '@alfalab/core-components/select/desktop';

// Дальше используем как обычно — API совместим:
<Select
  label="Проект"
  options={projectOptions}
  selected={projectId !== null ? String(projectId) : null}
  onChange={({ selected }) => onProjectChange(selected ? Number(selected.key) : null)}
  showSearch
  searchProps={{
    value: projectSearch,
    onChange: setProjectSearch,
    componentProps: { placeholder: 'Введите название или KK/ZPLM…' },
  }}
  block
/>
```

### ⚠️ Важно

- Импорт-путь — `@alfalab/core-components/select/desktop`, **не** `@alfalab/core-components/select-desktop`.
- Алиас `SelectDesktop as Select` сохраняет совместимость с существующим кодом — пропы не меняются.
- API идентичен `SelectResponsive`: `label`, `placeholder`, `options`, `selected`, `onChange`, `showSearch`, `searchProps`, `block`, `disabled`, `onOpen` работают как раньше.
- На устройствах с touch-only экраном UX чуть хуже (нет адаптации под mobile), но в проекте мы импортируем только из desktop-окружения (Альфа-Банк менеджеры).

---

## ❌ Типичная ошибка

```tsx
// НЕПРАВИЛЬНО — на узком окне Select превратится в полноэкранный Bottom Sheet,
// пользователь увидит «форму с поиском» вместо ожидаемого dropdown'а.
import { Select } from '@alfalab/core-components/select';

<Select label="Проект" options={projectOptions} showSearch ... />
```

**Симптом**: модалка снизу/сверху экрана, поверх формы импорта, с большим заголовком «Проект» и крестом закрытия.

---

## 📍 Применение в проекте

| Компонент | Файл | Заметки |
|-----------|------|---------|
| Select «Проект» | [components/ImportForm/ImportForm.tsx:2](../KiloImportService.Web/src/components/ImportForm/ImportForm.tsx#L2) | `SelectDesktop as Select` — всегда поповер |
| Select «Объект строительства» | [components/ImportForm/ImportForm.tsx:195](../KiloImportService.Web/src/components/ImportForm/ImportForm.tsx#L195) | Использует тот же `Select` импорт — тоже desktop |

---

## 🎯 Чек-лист

- [ ] Сузить окно браузера до 400px → Select всё ещё открывается как dropdown ниже поля, **не** как Bottom Sheet.
- [ ] Внутри dropdown'а сверху видно поле «Поиск», ниже — список опций с скроллбаром.
- [ ] Клик вне dropdown'а закрывает его (поведение поповера, не модалки).
- [ ] Tab-навигация: поле → поиск → опции → следующее поле формы.
- [ ] Все ранее работающие `searchProps` / `onChange` / `selected` props не сломались.
