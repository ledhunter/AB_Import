---
trigger: always_on
---

# Философия React 19 - Единое Правило

## 🎯 Золотое Правило

**Каждый компонент React должен быть переиспользуемым, тестируемым и понятным при первом прочтении.**

---

## 📐 Основные Принципы

### 1. **Компоненты Как Функции, Не Классы**

```tsx
// ✓ ВСЕГДА - функциональный компонент
const UserCard: React.FC<{ userId: string }> = ({ userId }) => {
  const [user, setUser] = React.useState<User | null>(null);
  
  return (
    <div className="user-card">
      <h2>{user?.name}</h2>
    </div>
  );
};

// ✗ НИКОГДА - классовый компонент (React 19 устаревает)
class UserCard extends React.Component {
  state = { user: null };
  render() {
    return <div>{this.state.user?.name}</div>;
  }
}
```

**Почему?**
- Hooks делают функциональные компоненты мощнее
- Легче отлаживать и тестировать
- Меньше boilerplate кода
- React 19 оптимизирован для функций

---

### 2. **Явность Типов - TypeScript Везде**

```tsx
// ✓ ВСЕГДА - полная типизация
interface UserProps {
  userId: string;
  onUserLoaded?: (user: User) => void;
  className?: string;
}

const UserCard: React.FC<UserProps> = ({ userId, onUserLoaded, className = "" }) => {
  const [user, setUser] = React.useState<User | null>(null);
  const [error, setError] = React.useState<Error | null>(null);
  
  return <div className={`user-card ${className}`}>{user?.name}</div>;
};

// ✗ НИКОГДА - без типов
const UserCard = ({ userId, onUserLoaded, className }) => {
  const [user, setUser] = useState(null);
  return <div className={`user-card ${className}`}>{user?.name}</div>;
};
```

**Почему?**
- TypeScript ловит ошибки на compile time
- Автодополнение IDE работает лучше
- Самодокументирующийся код
- Refactoring становится безопаснее

---

### 3. **Кастомные Hooks Для Переиспользования Логики**

```tsx
// ✓ КАСТОМНЫЙ HOOK
const useUser = (userId: string) => {
  const [user, setUser] = React.useState<User | null>(null);
  const [loading, setLoading] = React.useState(true);
  const [error, setError] = React.useState<Error | null>(null);

  React.useEffect(() => {
    fetchUser(userId)
      .then(setUser)
      .catch(setError)
      .finally(() => setLoading(false));
  }, [userId]);

  return { user, loading, error };
};

// Использование
const UserCard: React.FC<{ userId: string }> = ({ userId }) => {
  const { user, loading, error } = useUser(userId);
  
  if (loading) return <LoadingSpinner />;
  if (error) return <ErrorMessage error={error} />;
  
  return <div>{user?.name}</div>;
};

// ✗ НЕ КОПИРУЙ логику между компонентами
const UserCard = ({ userId }) => {
  const [user, setUser] = useState(null);
  const [loading, setLoading] = useState(true);
  // ... повторяющаяся логика загрузки
};
```

**Почему?**
- DRY принцип - не повторяй себя
- Легче поддерживать и обновлять
- Логика тестируется отдельно
- Компоненты остаются простыми

---

### 4. **Uncontrolled Components Когда Возможно**

```tsx
// ✓ UNCONTROLLED (React 19 улучшение)
const SearchForm: React.FC<{ onSearch: (term: string) => void }> = ({ onSearch }) => {
  const inputRef = React.useRef<HTMLInputElement>(null);
  
  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    if (inputRef.current) {
      onSearch(inputRef.current.value);
    }
  };
  
  return (
    <form onSubmit={handleSubmit}>
      <input ref={inputRef} type="text" defaultValue="" />
      <button type="submit">Search</button>
    </form>
  );
};

// ✗ ИЗБЕГАЙ UNNECESSARY CONTROLLED COMPONENTS
const SearchForm = ({ onSearch }) => {
  const [searchTerm, setSearchTerm] = useState("");
  
  // Лишнее состояние если оно не обновляется извне
  return (
    <input 
      value={searchTerm} 
      onChange={(e) => setSearchTerm(e.target.value)}
    />
  );
};
```

**Почему?**
- Меньше состояния = меньше ошибок
- Форма становится проще
- Синхронизация с DOM автоматическая
- React 19 лучше работает с refs

---

### 5. **Дефолтные Props и Деструктурирование**

```tsx
// ✓ ЯВНЫЕ ДЕФОЛТЫ
interface ButtonProps {
  children: React.ReactNode;
  variant?: "primary" | "secondary" | "danger";
  disabled?: boolean;
  onClick?: () => void;
  className?: string;
}

const Button: React.FC<ButtonProps> = ({
  children,
  variant = "primary",
  disabled = false,
  onClick,
  className = "",
}) => {
  const variantClass = {
    primary: "btn-primary",
    secondary: "btn-secondary",
    danger: "btn-danger",
  }[variant];

  return (
    <button
      className={`btn ${variantClass} ${className}`}
      disabled={disabled}
      onClick={onClick}
    >
      {children}
    </button>
  );
};

// ✗ НИКОГДА - неполные дефолты
const Button = ({ children, variant, disabled, onClick }) => {
  // Какие дефолты? Непонятно!
  return <button disabled={disabled}>{children}</button>;
};
```

**Почему?**
- Ясные значения по умолчанию
- Легко понять как использовать компонент
- Меньше ошибок при использовании
- Самодокументируется

---

### 6. **React.FC Vs Стрелочные Функции**

```tsx
// ✓ React.FC (лучше для типизации)
const UserCard: React.FC<UserCardProps> = ({ userId }) => {
  return <div>User: {userId}</div>;
};

// ✓ Альтернатива со явными типами
type UserCardProps = { userId: string };
const UserCard = ({ userId }: UserCardProps) => {
  return <div>User: {userId}</div>;
};

// ✗ Без типов
const UserCard = (props) => {
  return <div>User: {props.userId}</div>;
};
```

**Почему?**
- React.FC явно определяет компонент React
- TypeScript лучше проверяет типы
- IDE подсказывает правильно

---

## 🔄 Фреймворк Архитектуры

Когда создаешь новый компонент, спроси себя:

1. **Сколько компонентов будет переиспользовать эту логику?**
   - Если 2+ → вынеси в кастомный hook

2. **Может ли этот компонент существовать отдельно?**
   - Если нет → это не компонент, это часть компонента

3. **Содержит ли компонент более 200 строк?**
   - Если да → раздели на меньшие компоненты

4. **Могу ли я описать компонент одним предложением?**
   - Если нет → компонент делает слишком много

5. **Сможет ли другой разработчик использовать это правильно?**
   - Если нет → добавь типы, дефолты, JSDoc комментарии

---

## 🛠️ Практическое Применение

### Структура Компонента

```tsx
// ✓ ПРАВИЛЬНАЯ СТРУКТУРА

import React from "react";

// 1. Типы и интерфейсы в начале
interface UserCardProps {
  userId: string;
  onSelect?: (userId: string) => void;
  className?: string;
}

// 2. Кастомные hooks если есть
const useUserData = (userId: string) => {
  const [data, setData] = React.useState(null);
  // ...
  return data;
};

// 3. Основной компонент
const UserCard: React.FC<UserCardProps> = ({
  userId,
  onSelect,
  className = "",
}) => {
  const user = useUserData(userId);

  const handleClick = () => {
    onSelect?.(userId);
  };

  return (
    <div className={`user-card ${className}`} onClick={handleClick}>
      <h3>{user?.name}</h3>
    </div>
  );
};

// 4. Экспорт
export default UserCard;
```

### Folder Structure

```
src/
├── components/
│   ├── common/              # переиспользуемые компоненты
│   │   ├── Button.tsx
│   │   ├── Card.tsx
│   │   └── Modal.tsx
│   ├── layout/              # макеты страниц
│   │   ├── Header.tsx
│   │   ├── Sidebar.tsx
│   │   └── Footer.tsx
│   └── features/            # специфичные компоненты фич
│       ├── UserProfile.tsx
│       └── UserList.tsx
│
├── hooks/                   # кастомные hooks
│   ├── useUser.ts
│   ├── useFetch.ts
│   └── useLocalStorage.ts
│
├── types/                   # глобальные типы
│   ├── User.ts
│   ├── Post.ts
│   └── index.ts
│
├── utils/                   # вспомогательные функции
│   ├── api.ts
│   ├── formatters.ts
│   └── validators.ts
│
├── styles/                  # глобальные стили
│   └── globals.css
│
└── App.tsx                  # главный компонент
```

---

## 📊 React 19 Новые Фичи

```tsx
// 1. Автоматическое управление batch-обновлениями
const handleClick = async () => {
  const data = await fetchData();
  setData(data);        // batch обновление
  setLoading(false);     // batch обновление
};

// 2. useFormStatus (новый hook)
const SearchForm = () => {
  const { pending } = useFormStatus();
  
  return (
    <form>
      <input type="search" />
      <button disabled={pending}>
        {pending ? "Searching..." : "Search"}
      </button>
    </form>
  );
};

// 3. useFormState (для действий сервера)
const Form = () => {
  const [state, formAction] = useFormState(submitForm, null);
  
  return <form action={formAction}>{/* ... */}</form>;
};

// 4. useOptimistic (оптимистичные обновления)
const TodoList = ({ todos }: { todos: Todo[] }) => {
  const [optimisticTodos, addOptimisticTodo] = useOptimistic(
    todos,
    (state, newTodo) => [...state, newTodo]
  );
  
  const handleAdd = async (newTodo: Todo) => {
    addOptimisticTodo(newTodo);
    await addTodoToServer(newTodo);
  };
  
  return <div>{optimisticTodos.map(t => <div key={t.id}>{t.name}</div>)}</div>;
};
```

---

## 🎓 Менталитет

**Думай о компонентах как о LEGO блоках.**

- Каждый блок работает отдельно
- Блоки комбинируются в более крупные структуры
- Блоки имеют четкие interfaces (props)
- Блоки можно переиспользовать везде
- Изменение одного блока не ломает другие

---

## 🚀 Быстрый Чек-лист

Перед коммитом компонента, проверь:

- [ ] Компонент использует React.FC или типизированную функцию
- [ ] Все props имеют типы (TypeScript)
- [ ] Props имеют дефолтные значения где нужно
- [ ] Логика извлечена в кастомные hooks (если есть)
- [ ] Компонент менее 200 строк
- [ ] Компонент имеет JSDoc комментарии (если сложный)
- [ ] Компонент тестируется (хотя бы snapshot)
- [ ] Имена переменных ясные и описательные
- [ ] Нет console.log в production коде
- [ ] Используются latest React 19 фичи (useFormStatus, useOptimistic)

---



