# 🔍 Alfa Select с показным поиском + динамическими опциями

## 📋 Описание

`@alfalab/core-components/select` поддерживает **встроенный showSearch** — поле ввода фильтрует выпадающий список. Когда `options` приходят с сервера и **меняются** при каждом нажатии (как в нашем случае: backend ищет проекты по подстроке), возникает важный нюанс: **выбранный пользователем элемент может выпасть из списка `options`**, и Select потеряет отображение.

Этот документ описывает паттерн «**пиннинг выбранной опции**», который мы применили в форме импорта.

> 🔁 См. также: `09-lazy-loaded-select.md`, `10-listview-library.md`, `18-projects-cache.md`.

---

## ❌ Симптом

1. Юзер открыл Select «Проект» → видит список из 50 проектов из кэша.
2. Печатает «север» → список фильтруется на сервере → возвращаются только проекты с этой подстрокой.
3. Юзер кликает «ЖК Север», выбирает.
4. UI вызывает `onProjectChange(42)`, родитель ставит `projectId = 42`.
5. Параллельно `onChange` поиска не очищает search-строку, или React триггерит ре-рендер с обновлённым `projects.data`.
6. **Поле Select становится пустым** — потому что `selected={"42"}`, а в текущем `options` элемента с `key="42"` уже нет (его отфильтровали).

---

## ✅ Правильная реализация

### 1. Храни выбранную опцию **отдельно** от данных search'a

```tsx
// Контролируемая строка поиска
const [projectSearch, setProjectSearch] = useState('');

// ⚠️ Отдельный state для выбранной опции — и id, и label.
// Без него Select потеряет визуал, как только search-фильтр исключит элемент.
const [selectedProject, setSelectedProject] = useState<ProjectOption | null>(null);

const projects = useBackendProjects({ searchString: projectSearch, debounceMs: 300 });
```

### 2. **Пинни** выбранную опцию в `options`

```tsx
const projectOptions = useMemo(() => {
  const list = projects.data.map(toProjectOption);

  // Если выбранный проект отсутствует в текущем выводе search'a — всёравно
  // включаем его в список, иначе Select не сможет показать label.
  if (selectedProject && !list.some((o) => o.key === selectedProject.key)) {
    list.unshift(selectedProject);
  }
  return list;
}, [projects.data, selectedProject]);
```

### 3. На `onChange` сохраняй опцию + чисти search

```tsx
<Select
  options={projectOptions}
  selected={projectId !== null ? String(projectId) : null}
  onChange={({ selected }) => {
    if (selected) {
      const opt: ProjectOption = {
        key: String(selected.key),
        content: String(selected.content ?? selected.key),
      };
      setSelectedProject(opt);          // 👈 пиннинг
      onProjectChange(Number(opt.key)); // обновление родительского state
      setProjectSearch('');             // 👈 очистка поиска
    } else {
      setSelectedProject(null);
      onProjectChange(null);
    }
  }}
  showSearch
  searchProps={{
    value: projectSearch,
    onChange: (value: string) => setProjectSearch(value),
    componentProps: { placeholder: 'Введите название или KK/ZPLM…' },
  }}
/>
```

### ⚠️ Важно

- **Selected key должен присутствовать в `options`**. `Select` от core-components ищет соответствие по `key`. Если нет — рисует пусто, никаких ошибок в консоли.
- **Очищай `searchProps.value` после select**. Иначе при повторном открытии Select юзер увидит остаток своего предыдущего поиска и фильтр поверх него.
- **Опция должна быть полной** (`{ key, content }`), не только `key` — Select использует `content` для отображения.

---

## ❌ Типичные ошибки

### Ошибка 1: Полагаться только на `data` от хука

```tsx
// НЕПРАВИЛЬНО — selected пропадёт после фильтрации
const options = projects.data.map(toProjectOption);

<Select
  options={options}
  selected={String(projectId)}  // 👈 если projectId не в options — поле пустое
  searchProps={{ value: search, onChange: setSearch }}
/>
```

### Ошибка 2: Не очищать search после select

```tsx
// НЕПРАВИЛЬНО — search-фильтр живёт после выбора, при повторном open
// юзер видит фильтрованный список и не понимает, почему его проекта нет.
onChange={({ selected }) => {
  onProjectChange(Number(selected.key));
  // setProjectSearch('') ← забыли
}}
```

### Ошибка 3: Хранить `selectedProject` без `content`

```tsx
// НЕПРАВИЛЬНО — Select покажет голый key вместо названия проекта.
setSelectedProject({ key: String(selected.key) }); // ❌ без content
```

---

## 🎯 Когда применять

Паттерн нужен в любом сценарии «Select + showSearch + динамический backend-поиск», где список `options` **не статичен** и зависит от `searchString`:

| Сценарий | Нужен пиннинг? |
|---|---|
| Все опции загружены сразу, фильтрация только клиентская | ❌ Нет — `options` стабилен |
| Сервер фильтрует по подстроке (наш случай) | ✅ Да |
| Bulk-выбор (multiple) | ✅ Да, для всех selected items |
| Async-load по open, без поиска | ❌ Нет — `options` не меняется после загрузки |

---

## 📍 Применение в проекте

| Компонент | Файл | Заметки |
|---|---|---|
| Select «Проект» в форме импорта | `@KiloImportService.Web/src/components/ImportForm/ImportForm.tsx:55-70,143-159` | Пиннинг + clear search on select |
| Select «Объект строительства» (sites) | `@KiloImportService.Web/src/components/ImportForm/ImportForm.tsx:174-184` | **Не нужен** пиннинг — sites без showSearch, статичный список под выбранный проект |

---

## 🧪 Чек-лист проверки

- [ ] Открой Select «Проект» — видны проекты из кэша.
- [ ] Введи в поиск подстроку — список фильтруется.
- [ ] Выбери проект → поле показывает название (не пусто, не key).
- [ ] Снова открой Select → виден полный список (search очищен).
- [ ] Введи новую подстроку, не выбирая ничего → выбранный ранее проект **всё ещё** виден в выпадающем списке (потому что unshift'нут в options).
- [ ] Закрой Select без выбора → поле сохраняет предыдущий выбор.
