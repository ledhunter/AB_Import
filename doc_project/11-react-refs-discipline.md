# 🪝 React 19: дисциплина работы с `useRef`

## 📋 Описание

В React 19 ESLint-правило `react-hooks/refs` запрещает читать или **писать**
`ref.current` во время фазы рендера. Нарушение — это не «warning ради warning»:
такой код может рассинхронизировать состояние при concurrent rendering, double
invoke в StrictMode, и SSR/hydration.

В нашем проекте на это правило мы наступили один раз — в `useListView`. Этот
документ фиксирует **правильный** и **неправильный** способы хранения
«latest value» в ref'ах, чтобы повторно не наступать.

> 💡 Контекст: ошибка была поймана `npm run lint` после `npm install`;
> исправлена в `KiloImportService.Web/src/hooks/useListView.ts` в рамках
> `doc_project/plan-listview-library.md`, этап 7.

---

## ✅ Правильная реализация

### Паттерн: «храним последнее значение в ref'е, чтобы коллбэки не пересоздавались»

```ts
// hooks/useListView.ts
import { useCallback, useEffect, useRef } from 'react';

export function useListView<TItem>(service, options) {
  const { query } = options;

  // 1) Инициализируем ref ОДИН раз — значение query на первом рендере.
  const queryRef = useRef<ListViewQuery | undefined>(query);

  // 2) Сравниваем «изменилось ли значение» В EFFECT'е, не в рендере.
  useEffect(() => {
    if (queryRef.current === query) {
      return;                          // 👈 первый коммит: ничего не делаем
    }
    queryRef.current = query;          // 👈 запись ТОЛЬКО внутри useEffect
    // ... побочные эффекты на смену query (abort, сброс кэша, ...)
  }, [query, /* другие зависимости */]);

  // 3) Коллбэки читают актуальный query из ref'а — стабильны по ссылке.
  const run = useCallback(() => {
    service.fetch({ ...queryRef.current, signal: ... });   // 👈 чтение в обработчике, не в рендере
  }, [service]);

  return { run, ... };
}
```

### ⚠️ Важно

- **Запись `ref.current = ...` — только внутри `useEffect` / `useLayoutEffect` /
  обработчиков событий.** В рендере — никогда.
- **Чтение `ref.current` в рендере допустимо**, но обычно плохая идея: это
  значит, что у тебя есть «скрытое состояние», которое не вызывает re-render.
  Подумай, нужно ли тебе вместо этого `useState` или `useMemo`.
- Используй ref как «передачу latest-значения в стабильный коллбэк»: коллбэк
  не пересоздаётся, но всегда видит свежий `query` через `queryRef.current`.
- Сравнение «изменилось ли значение» в effect'е делается через тот же ref:
  `if (queryRef.current === query) return;` — отрабатывает первый коммит без
  лишних побочек.

---

## ❌ Типичная ошибка

### Ошибка 1: запись в ref прямо в теле компонента/хука

```ts
// НЕПРАВИЛЬНО — eslint поймает: react-hooks/refs «Cannot update ref during render»
export function useListView(service, options) {
  const { query } = options;
  const queryRef = useRef(query);
  queryRef.current = query;            // ❌ запись в фазе рендера
  // ...
}
```

**Почему плохо:**
- В StrictMode компонент рендерится дважды → запись произойдёт дважды
- В concurrent rendering React может **отбросить** рендер (например, из-за более
  приоритетного обновления) — но запись в ref уже произошла, и `current` будет
  указывать на «недокоммиченное» значение
- При SSR ref может содержать значение клиента, что ломает hydration

**Правильно:** запись внутри `useEffect`, как в примере выше.

### Ошибка 2: «двойной ref» вместо сравнения с current

```ts
// ИЗБЫТОЧНО — но это и был наш первоначальный код
const queryRef = useRef(query);
const prevQueryRef = useRef(query);

useEffect(() => {
  if (prevQueryRef.current === query) return;
  prevQueryRef.current = query;
  // ... побочка
}, [query]);
```

**Почему так не делать:** `queryRef.current` уже хранит «предыдущее» значение
(до записи в effect'е). Лишний ref `prevQueryRef` дублирует информацию, и легко
случайно рассинхронить два ref'а правкой одного из них.

**Правильно:** один `queryRef`, сравнение `queryRef.current === query` в начале
effect'а — то же самое, но без второго ref'а.

### Ошибка 3: чтение ref'а в JSX/выводе компонента

```tsx
// ⚠️ Плохо: render не перезапустится, когда ref изменится
return <div>{counterRef.current}</div>;
```

**Почему плохо:** изменение `ref.current` не триггерит re-render. На экране
останется старое значение, пока не произойдёт ререндер по другой причине.

**Правильно:** для UI-значений используй `useState` (или `useSyncExternalStore`
для внешних подписок).

### Ошибка 4: запись в ref в условном рендере

```tsx
// НЕПРАВИЛЬНО — даже если выглядит «безопасно»
if (someCondition) {
  myRef.current = value;               // ❌ всё равно фаза рендера
}
return <div>...</div>;
```

**Правильно:** перенеси в `useEffect(() => { if (...) myRef.current = ... })`.

---

## 🧠 Когда `useRef` нужен, а когда — нет

| Задача | Решение |
|--------|---------|
| Сохранить значение между рендерами **без** триггера ре-рендера | `useRef` (запись в effect/обработчике) |
| Сохранить значение **с** триггером ре-рендера | `useState` |
| Закэшировать вычисление пока зависимости не изменились | `useMemo` |
| «Зафризить» коллбэк и иметь доступ к latest props/state внутри него | `useRef` + запись в effect, чтение в коллбэке |
| Дать parent'у императивно вызвать метод child'a | `useImperativeHandle` поверх `forwardRef` |
| Получить DOM-узел | `useRef<HTMLDivElement>(null)` + `<div ref={ref} />` |

---

## 📍 Применение в проекте

| Компонент / хук | Файл | Где `ref.current` пишется |
|-----------------|------|---------------------------|
| `useListView` | `KiloImportService.Web/src/hooks/useListView.ts` | `inFlightRef.current` — в `run()` (обработчик) и cleanup `useEffect`'е; `queryRef.current` — в `useEffect([query, logTag])` |

> 📝 Если в проекте появится ещё один хук с паттерном «latest value в ref'е» —
> добавь его в эту таблицу.

---

## 🎯 Чек-лист при добавлении нового `useRef`

- [ ] Запись `ref.current = ...` происходит **только** в:
  - [ ] `useEffect` / `useLayoutEffect`
  - [ ] обработчике события (`onClick`, `onOpen`, ...)
  - [ ] коллбэке внутри `useCallback`, который сам вызывается в обработчиках
- [ ] Запись **не** происходит в:
  - [ ] теле функции компонента/хука (фаза рендера)
  - [ ] условиях/циклах верхнего уровня
  - [ ] `useMemo` (это тоже фаза рендера)
- [ ] Если ref содержит «latest value» — есть `useEffect` с этой зависимостью,
      обновляющий `ref.current`
- [ ] Если ref читается в коллбэке — коллбэк стабилен (`useCallback`) и видит
      актуальное значение через `ref.current`
- [ ] Если значение должно отрендериться в JSX — это **не** ref, а `useState`
- [ ] `npm run lint` проходит без ошибок `react-hooks/refs`
- [ ] При `npm run build` (StrictMode) поведение корректное (ничего не «дёргается»
      дважды и не теряется)
