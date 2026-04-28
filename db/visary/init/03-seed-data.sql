-- ═══════════════════════════════════════════════════════════════════
-- Seed-данные для visary_db (минимальный набор для разработки/тестов)
-- ═══════════════════════════════════════════════════════════════════

-- Системный пользователь для FK на Author/Creator/Modified*
INSERT INTO "Base"."User" ("ID", "Title", "Hidden")
VALUES (1, 'System', false)
ON CONFLICT ("ID") DO NOTHING;

-- Виды помещений (под импорт `rooms`)
INSERT INTO "Data"."RoomKind" ("ID", "Title", "Hidden")
VALUES
    (1, 'Квартира',     false),
    (2, 'Машиноместо',  false),
    (3, 'Кладовая',     false),
    (4, 'Апартамент',   false),
    (5, 'Нежилое',      false)
ON CONFLICT ("ID") DO NOTHING;

-- Один проект для smoke-тестов
INSERT INTO "Data"."ConstructionProject" ("ID", "Title", "IdentifierKK", "Hidden")
VALUES (1, 'Тестовый проект', 'TEST-001', false)
ON CONFLICT ("ID") DO NOTHING;

INSERT INTO "Data"."ConstructionSite" ("ID", "Title", "ConstructionProjectID", "Hidden")
VALUES (1, 'Тестовый объект', 1, false)
ON CONFLICT ("ID") DO NOTHING;
