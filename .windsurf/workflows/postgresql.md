PostgreSQL# Философия PostgreSQL - Единое Правило

## 🎯 Золотое Правило

**Каждый элемент базы данных должен быть самодокументируемым, предсказуемым и легко поддерживаемым по умолчанию.**

---

## 📐 Основные Принципы

### 1. **Консистентность Важнее Удобства**

```sql
-- ✓ ВСЕГДА
CREATE TABLE users (
    id BIGSERIAL PRIMARY KEY,
    email VARCHAR(255) NOT NULL UNIQUE,
    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- ✗ НИКОГДА
CREATE TABLE user (  -- единственное число
    ID int primary key,  -- ЗАГЛАВНЫЕ, INT вместо BIGSERIAL
    Email varchar(100),  -- PascalCase, отсутствует NOT NULL
);
```

**Почему?**
- Все члены команды мгновенно понимают структуру
- Инструменты и ORM работают предсказуемо
- Code review фокусируется на логике, а не на стиле

---

### 2. **Явность Важнее Скрытости**

```sql
-- ✓ ЯВНО
user_id BIGINT NOT NULL REFERENCES users(id) ON DELETE CASCADE

-- ✗ СКРЫТО
user_id BIGINT REFERENCES users(id)  -- Что произойдет при удалении? Неизвестно!
```

**Почему?**
- Нет сюрпризов в production
- Легче отлаживать ошибки
- Ясная управление жизненным циклом данных

---

### 3. **Осведомленность о Производительности с Первого Дня**

```sql
-- ✓ ИНДЕКС НА FK
CREATE TABLE orders (
    id BIGSERIAL PRIMARY KEY,
    user_id BIGINT NOT NULL REFERENCES users(id) ON DELETE RESTRICT
);
CREATE INDEX idx_orders_user_id ON orders(user_id);  -- ВСЕГДА!

-- ✗ БЕЗ ИНДЕКСА
CREATE TABLE orders (
    id BIGSERIAL PRIMARY KEY,
    user_id BIGINT NOT NULL REFERENCES users(id) ON DELETE RESTRICT
);
-- Отсутствие индекса = медленные JOIN когда таблица растет
```

**Почему?**
- Добавление индексов позже требует downtime
- Проблемы производительности проявляются при масштабировании, не в разработке
- Профилактическая оптимизация дешевле реактивных исправлений

---

### 4. **Целостность Данных - Неоспоримо**

```sql
-- ✓ С ОГРАНИЧЕНИЯМИ
CREATE TABLE products (
    id BIGSERIAL PRIMARY KEY,
    price NUMERIC(10, 2) NOT NULL CHECK (price >= 0),
    stock INT NOT NULL DEFAULT 0 CHECK (stock >= 0),
    is_active BOOLEAN DEFAULT true NOT NULL
);

-- ✗ БЕЗ ОГРАНИЧЕНИЙ
CREATE TABLE products (
    id BIGSERIAL PRIMARY KEY,
    price FLOAT,  -- может быть отрицательной, NULL, неточной
    stock INT,    -- может быть отрицательной, NULL
    is_active INT -- 0, 1, 2, NULL? Кто знает!
);
```

**Почему?**
- База данных применяет бизнес-правила 24/7
- Ошибки приложения не портят данные
- Качество данных гарантировано на самом низком уровне

---

### 5. **Аудитируемость По Умолчанию**

```sql
-- ✓ ОТСЛЕЖИВАНИЕ ВРЕМЕНИ
CREATE TABLE orders (
    id BIGSERIAL PRIMARY KEY,
    -- ... бизнес-колонки ...
    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    deleted_at TIMESTAMP NULL  -- мягкое удаление
);

-- Автоматический триггер для обновления
CREATE TRIGGER update_orders_timestamp
    BEFORE UPDATE ON orders
    FOR EACH ROW
    EXECUTE FUNCTION update_timestamp();
```

**Почему?**
- Мгновенно ответить "когда это было создано/изменено?"
- Отладка production проблем требует временной шкалы
- Требования соответствия и аудита

---

### 6. **Документация как Код**

```sql
COMMENT ON TABLE orders IS 
'Основная таблица заказов. Содержит все заказы клиентов.
Обновляется: при каждой покупке
Критична для: биллинга, доставки, аналитики';

COMMENT ON COLUMN orders.user_id IS
'Ссылка на users.id. Каскадное удаление при удалении пользователя.
Используется в 90% запросов (см. idx_orders_user_id)';
```

**Почему?**
- Новые члены команды быстрее адаптируются
- Изменения схемы требуют понимания контекста
- Живая документация никогда не становится устаревшей

---

## 🔄 Фреймворк Принятия Решений

Когда принимаешь любое решение о БД, спроси себя:

1. **Будет ли это очевидно кому-то через 6 месяцев?**
   - Если нет → добавь комментарии, используй четкие имена

2. **Что произойдет, когда эта таблица будет содержать 10М строк?**
   - Если медленно → добавь индексы, рассмотри партиционирование

3. **Что сломается, если эти данные будут удалены?**
   - Определи ON DELETE CASCADE/RESTRICT явно

4. **Может ли невалидные данные войти в систему?**
   - Добавь ограничения, используй правильные типы

5. **Как я отслеживаю изменения этой записи?**
   - Добавь created_at, updated_at, deleted_at

---

## 🛠️ Практическое Применение

### При Создании Новой Таблицы

```sql
-- Шаблон (копируй и заполняй)
CREATE TABLE <schema>.<table_name> (
    -- 1. Идентификация
    id BIGSERIAL PRIMARY KEY,
    
    -- 2. Отношения
    <parent>_id BIGINT NOT NULL REFERENCES <schema>.<parent>(id) ON DELETE <CASCADE|RESTRICT>,
    
    -- 3. Бизнес-данные
    <column> <TYPE> NOT NULL CHECK (<validation>),
    
    -- 4. Флаги
    is_<state> BOOLEAN DEFAULT false NOT NULL,
    
    -- 5. Аудит
    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    deleted_at TIMESTAMP NULL
);

-- 6. Индексы (FK + бизнес-запросы)
CREATE INDEX idx_<table>_<parent>_id ON <schema>.<table_name>(<parent>_id);

-- 7. Триггер (автообновление временной метки)
CREATE TRIGGER update_<table_name>_timestamp
    BEFORE UPDATE ON <schema>.<table_name>
    FOR EACH ROW
    EXECUTE FUNCTION <schema>.update_timestamp();

-- 8. Документация
COMMENT ON TABLE <schema>.<table_name> IS '<назначение и контекст>';
```

### При Написании Миграции

```sql
-- V001__descriptive_name.sql
BEGIN;

-- Что: Создать таблицу users
-- Почему: Хранить аккаунты пользователей приложения
-- Влияние: Основная таблица для аутентификации

CREATE TABLE users (
    id BIGSERIAL PRIMARY KEY,
    email VARCHAR(255) NOT NULL UNIQUE,
    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX idx_users_email ON users(email);

COMMIT;
```

---

## 📊 Правила Выбора Типов

| Сценарий | Тип | Почему |
|----------|-----|--------|
| Primary Key | `BIGSERIAL` | Никогда не кончатся ID |
| Foreign Key | `BIGINT` | Соответствует BIGSERIAL |
| Деньги | `NUMERIC(10,2)` | Точная точность, без округлений |
| Счетчики | `INT` или `BIGINT` | Зависит от максимума |
| Да/Нет | `BOOLEAN` | Явно true/false/null |
| Временные метки | `TIMESTAMP` | Всегда отслеживай время |
| Текст (ограниченный) | `VARCHAR(n)` | Когда макс. длина известна |
| Текст (неограниченный) | `TEXT` | Когда длина варьируется |
| JSON данные | `JSONB` | Индексируемый, быстрее чем JSON |
| Бинарные данные | `BYTEA` | Зашифрованные ключи, файлы |

---

## 🎓 Менталитет

**Думай как администратор базы данных, не только как разработчик.**

- Твоя схема переживет твой код
- Другие будут поддерживать то, что ты создал
- Профилактика легче чем миграция
- Явное лучше скрытого
- Консистентность компаундируется со временем

---

## 🚀 Быстрый Чек-лист

Перед коммитом любого изменения схемы, проверь:

- [ ] Имя таблицы множественное число (users, не user)
- [ ] Имена колонок snake_case
- [ ] Primary key это BIGSERIAL
- [ ] Foreign keys имеют ON DELETE действие
- [ ] Foreign keys имеют индексы
- [ ] Деньги используют NUMERIC, не FLOAT
- [ ] Существуют временные метки (created_at, updated_at)
- [ ] Ограничения валидируют бизнес-правила
- [ ] Существуют комментарии объясняющие назначение и контекст
- [ ] Миграция в BEGIN/COMMIT блоке

---

