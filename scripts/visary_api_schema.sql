-- Схема для метаданных Visary API: каталог мнемоник, эндпоинтов и полей.
-- Заполняется скриптом scripts/import-visary-audit.ps1 из .audit/raw/*.json.
-- Снэпшот API замораживается на момент аудита; повторный запуск перезаписывает.

CREATE SCHEMA IF NOT EXISTS visary_api;

-- ─── Сущности (мнемоники Visary) ────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS visary_api.entities (
    mnemonic        TEXT PRIMARY KEY,
    title_ru        TEXT,
    description     TEXT,
    is_in_library   BOOLEAN     NOT NULL DEFAULT false,  -- покрыта ли в Visary.Api.Client
    captured_at     TIMESTAMPTZ NOT NULL DEFAULT now()
);

COMMENT ON TABLE  visary_api.entities       IS 'Каталог сущностей Visary, известных по аудиту HTTP API';
COMMENT ON COLUMN visary_api.entities.is_in_library IS 'TRUE — мнемоника используется ICrudClient/IListViewClient';

-- ─── HTTP-эндпоинты ─────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS visary_api.endpoints (
    id              SERIAL      PRIMARY KEY,
    mnemonic        TEXT        NOT NULL REFERENCES visary_api.entities(mnemonic) ON DELETE CASCADE,
    operation       TEXT        NOT NULL,  -- 'get_by_id' | 'list' | 'patch' | 'create' | 'put' | 'link'
    http_method     TEXT        NOT NULL,  -- GET | POST | PATCH | PUT | DELETE
    url_template    TEXT        NOT NULL,  -- например '/api/visary/crud/{mnemonic}/{id}'
    description     TEXT,
    captured_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (mnemonic, operation)
);

COMMENT ON TABLE visary_api.endpoints IS 'HTTP-методы Visary, сгруппированные по сущности и логической операции';

-- ─── Поля сущностей (плоский список) ────────────────────────────────────────
-- Хранится как nested-path: 'Type.ID', 'Location.BoundMinX'.
-- Для массовых ассоциаций (A_O2M_*, A_M2M_*) — отдельный location='association'.
CREATE TABLE IF NOT EXISTS visary_api.fields (
    id              SERIAL      PRIMARY KEY,
    mnemonic        TEXT        NOT NULL REFERENCES visary_api.entities(mnemonic) ON DELETE CASCADE,
    location        TEXT        NOT NULL,  -- 'response_body' | 'request_body' | 'path' | 'query' | 'association'
    path            TEXT        NOT NULL,  -- 'Title' | 'Type.ID' | 'A_O2M_Deal'
    data_type       TEXT        NOT NULL,  -- 'string' | 'int' | 'long' | 'double' | 'boolean' | 'datetime' | 'object' | 'array' | 'ref' | 'null'
    is_nullable     BOOLEAN     NOT NULL DEFAULT true,
    sample_value    TEXT,
    notes           TEXT,
    UNIQUE (mnemonic, location, path)
);

COMMENT ON COLUMN visary_api.fields.path     IS 'Полный путь поля от корня DTO; вложенность через точку';
COMMENT ON COLUMN visary_api.fields.data_type IS 'Тип определён эвристикой по реальному ответу; null-значения помечены ''null''';

-- ─── Сырые снэпшоты ответов (для аудита и diff''ов) ─────────────────────────
CREATE TABLE IF NOT EXISTS visary_api.captures (
    id              SERIAL      PRIMARY KEY,
    mnemonic        TEXT        NOT NULL REFERENCES visary_api.entities(mnemonic) ON DELETE CASCADE,
    operation       TEXT        NOT NULL,
    sample_id       INTEGER,
    response_body   JSONB       NOT NULL,
    captured_at     TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_visary_captures_mnemonic ON visary_api.captures (mnemonic);
