# 53. Аудит схемы Visary API (snapshot)

**Дата снэпшота:** 2026-05-06
**Среда:** test (`isup-alfa-test.k8s.npc.ba`)
**Метод сбора:** `GET /api/visary/crud/{mnemonic}/{id}` для одной существующей записи каждой сущности.
**Хранилище:** Postgres `import_service_db`, схема `visary_api` (таблицы `entities`, `endpoints`, `fields`, `captures`).

## Как обновить

1. Получить свежий Bearer-токен из DevTools Visary, положить в `.audit/.token` (один файл, без переноса строки).
2. `pwsh scripts/audit-visary-api.ps1` — обходит API и сохраняет JSON в `.audit/raw/`.
3. `pwsh scripts/import-visary-audit.ps1` — применяет DDL и заливает данные в Postgres.

Папка `.audit/` в `.gitignore` — токен и сырые JSON в репо не попадают.

## Сводка покрытия библиотекой

| Мнемоника | Полей в API | Полей в DTO | Покрыто |
|-----------|-------------|-------------|---------|
| `constructionproject` | 53 | 21 | 20/21 |
| `constructionsite` | 81 | 12 | 7/12 |
| `constructionsection` | 27 | 6 | 6/6 |
| `constructionsiteindicator` | 19 | 18 | 18/18 |
| `constructionsiteindicatorvalue` | 17 | 14 | 14/14 |
| `room` | 35 | 34 | 34/34 |
| `cadastralarea` | 49 | 8 | 8/8 |
| `percentbet` | 21 | 16 | 16/16 |
| `shareagreement` | 42 | 11 | 11/11 |
| `deal` | 11 | 10 | 10/10 |
| `organization` | 45 | 10 | 9/10 |

> «Покрыто» — поля DTO, реально присутствующие в ответе API. Если меньше DTO-полей — DTO содержит названия, которых в snapshot нет (либо сущность их не имеет, либо имена расходятся).

## `constructionproject`

**В DTO есть, в API snapshot нет:**

- `Hidden`

**В API есть, в DTO нет (33 полей):**

| Поле | Тип | Пример |
|------|-----|--------|
| `AdditionalProperties` | null |  |
| `AdditionalPropertiesConfig` | null |  |
| `Beneficiaries` | null |  |
| `CompetitionLevel` | int | 0 |
| `CreditRequest` | null |  |
| `CreditTerm` | null |  |
| `DiscountRate` | null |  |
| `Escrow` | boolean | False |
| `Files` | null |  |
| `FinalDecision` | null |  |
| `Guarantee` | boolean | False |
| `IdDealState` | null |  |
| `InflationCalcMethod` | null |  |
| `Insurance` | null |  |
| `Location` | object |  |
| `LocationDescription` | null |  |
| `NegativeFactor` | null |  |
| `Phase` | int | 10 |
| `PhotoCadastralMap` | null |  |
| `PhotoLocation` | null |  |
| `PositiveFactors` | null |  |
| `Program` | null |  |
| `ProjectFolder` | string | 32,40439 |
| `ProjectLiquidity` | null |  |
| `ProjectQuality` | null |  |
| `RowVersion` | int | 4631332 |
| `SelfParticipation` | null |  |
| `SignificantLimits` | null |  |
| `SourceUrl` | string | https://translate.google.com/????? |
| `StateCondition` | null |  |
| `UseTypeConclusion` | null |  |
| `VideoSurveillance` | null |  |
| `WholesaleRate` | null |  |

**Ассоциации (нужно запрашивать отдельно через `/listview/.../onetomany|manytomany/...`):**

- `A_M2M_CheckPoint`
- `A_O2M_Deal`
- `A_O2M_ProjectParameter`
- `A_O2M_SiteRatesPrices`
- `A_O2O_CheckList`
- `A_O2O_ConstructionProjectCalculated`

## `constructionsite`

**В DTO есть, в API snapshot нет:**

- `ConstructionProjectId`
- `RegionId`
- `TownId`
- `Hidden`
- `FinishingMaterialId`

**В API есть, в DTO нет (74 полей):**

| Поле | Тип | Пример |
|------|-----|--------|
| `AdditionalParameter` | null |  |
| `AdditionalProperties` | null |  |
| `AdditionalPropertiesConfig` | null |  |
| `AnalogFileName` | null |  |
| `AnalogType` | null |  |
| `ApprovedReserveSum` | null |  |
| `AreaCost` | null |  |
| `Author` | ref |  |
| `Borrower` | null |  |
| `BuildingMaterial` | ref |  |
| `ClaimedCost` | null |  |
| `Comment` | null |  |
| `CommissioningDate` | null |  |
| `CommissioningNumber` | null |  |
| `CompanyGroupTitle` | null |  |
| `CompetitionLevel` | null |  |
| `ComplexID` | null |  |
| `ConfirmationDate` | null |  |
| `ConfirmedDateRNV` | null |  |
| `ConstructOptional` | null |  |
| `CostIndex` | null |  |
| `CostPerUnit` | null |  |
| `CostPlan` | null |  |
| `Date` | datetime | 2026-05-05T13:57:24.989943Z |
| `Description` | string | ????????? |
| `Developer` | null |  |
| `ERZSource` | null |  |
| `EstateClass` | ref |  |
| `FascadeView` | null |  |
| `FinishDate` | null |  |
| `FinishingMaterial` | ref |  |
| `Hub` | null |  |
| `IndexDate` | null |  |
| `InfoSource` | null |  |
| `IsContract` | boolean | False |
| `IsExceptions` | boolean | False |
| `IsMaterial` | boolean | False |
| `Location` | object |  |
| `LocationDescription` | null |  |
| `MonthDuration` | null |  |
| `NeighbourhoodMap` | null |  |
| `Note` | null |  |
| `OwnParticipation` | null |  |
| `OwnParticipationPercent` | null |  |
| `PercentFromPRKK` | null |  |
| `PledgerCollection` | array |  |
| `Project` | ref |  |
| `ProjectFolder` | string | 32,40110 |
| `Queues` | array |  |
| `Radius` | int | 2001 |
| `RatingERZ` | null |  |
| `Region` | ref |  |
| `Reserve` | null |  |
| `RiskFund` | null |  |
| `RowVersion` | int | 4631153 |
| `SalesStart` | null |  |
| `ScoreERZ` | null |  |
| `ShareAgrDateTransfer` | null |  |
| `SituationPlan` | null |  |
| `SquareOtherNonRes` | null |  |
| `StartDate` | null |  |
| `StatusReadiness` | int | 0 |
| `TempOfConstruction` | null |  |
| `TitleFooterReport` | null |  |
| `TitleObjectReport` | null |  |
| `TopRegion` | null |  |
| `TopRF` | null |  |
| `TotalArea` | double | 0.0 |
| `TotalCost` | null |  |
| `Town` | ref |  |
| `TransportAccessibility` | null |  |
| `Type` | ref |  |
| `UndergroundParking` | boolean | False |
| `VariableCostPlan` | null |  |

**Ассоциации (нужно запрашивать отдельно через `/listview/.../onetomany|manytomany/...`):**

- `A_M2M_CadastralArea`
- `A_M2M_ProjectDoc`
- `A_M2M_ProjectManagement`
- `A_O2M_SiteRatesPrices`
- `A_O2O_ConstructionSiteCalculation`
- `A_O2O_SalesDataCalculate`
- `A_O2O_StroyAnalogCriteria`

## `constructionsection`

**В API есть, в DTO нет (21 полей):**

| Поле | Тип | Пример |
|------|-----|--------|
| `AvgResArea` | null |  |
| `AvgResAreaWithoutSummerRoom` | null |  |
| `ClaimedCost` | null |  |
| `CostPerUnit` | null |  |
| `Description` | null |  |
| `HasLift` | boolean | False |
| `HasUndergroundStage` | boolean | False |
| `NonresArea` | null |  |
| `NonresQuantity` | null |  |
| `OtherNonresArea` | null |  |
| `OtherNonresQuantity` | null |  |
| `ParkingArea` | null |  |
| `ParkingQuantity` | null |  |
| `ResAreaWithoutSummerRoom` | null |  |
| `ResPercentage` | null |  |
| `ResProjectArea` | null |  |
| `ResQuantity` | null |  |
| `RowVersion` | int | 4663618 |
| `SectionID` | null |  |
| `TotalCost` | null |  |
| `Version` | datetime | 2026-05-06T06:48:27.298187Z |

**Ассоциации (нужно запрашивать отдельно через `/listview/.../onetomany|manytomany/...`):**

- `A_O2M_SectionTotals`

## `constructionsiteindicator`

**В API есть, в DTO нет (1 полей):**

| Поле | Тип | Пример |
|------|-----|--------|
| `RowVersion` | int | 4631794 |

**Ассоциации (нужно запрашивать отдельно через `/listview/.../onetomany|manytomany/...`):**

- `A_O2M_ConstructionSiteIndicatorValue`
- `A_O2O_ConstructionSiteIndicatorCalculation`

## `constructionsiteindicatorvalue`

**В API есть, в DTO нет (3 полей):**

| Поле | Тип | Пример |
|------|-----|--------|
| `ProjectDoc` | null |  |
| `RowVersion` | int | 4630973 |
| `Section` | null |  |

## `room`

**В API есть, в DTO нет (1 полей):**

| Поле | Тип | Пример |
|------|-----|--------|
| `RowVersion` | int | 4633504 |

**Ассоциации (нужно запрашивать отдельно через `/listview/.../onetomany|manytomany/...`):**

- `A_O2M_ShareAgreement`

## `cadastralarea`

**В API есть, в DTO нет (41 полей):**

| Поле | Тип | Пример |
|------|-----|--------|
| `Address` | null |  |
| `AdjustmentToCost` | null |  |
| `AreaDocument` | null |  |
| `CadastralCost` | null |  |
| `CadastralEngineer` | null |  |
| `CollateralValue` | null |  |
| `CourtRequirements` | null |  |
| `DecisionToWithdraw` | null |  |
| `Description` | null |  |
| `Discount` | null |  |
| `EGRNDate` | null |  |
| `Encumbrance` | null |  |
| `ExternalVendor` | boolean | False |
| `FactUseTypeArea` | null |  |
| `FailureToRegister` | null |  |
| `FullyPaid` | boolean | False |
| `InCount` | boolean | False |
| `InitRightRestrictionType` | null |  |
| `IsVendorAffiliate` | boolean | False |
| `LandWork` | null |  |
| `LandWorkDate` | null |  |
| `MarketCost` | null |  |
| `MarketFinalCost` | null |  |
| `NoConsentThirdParty` | null |  |
| `NotReady` | boolean | False |
| `ObjectionsToRight` | null |  |
| `OldRegistrationNumber` | null |  |
| `Owner` | null |  |
| `PersonDataAvail` | null |  |
| `PersonInvolvement` | null |  |
| `PLanArea` | null |  |
| `Queues` | string |  |
| `RelevanceStatus` | null |  |
| `RentalPrice` | null |  |
| `RowVersion` | int | 4632426 |
| `SpecialMarks` | null |  |
| `StatementBacklog` | null |  |
| `Surveying` | boolean | False |
| `Title` | string | 05:05:2025 |
| `Vendor` | null |  |
| `Version` | datetime | 2026-05-05T14:18:30.958604Z |

**Ассоциации (нужно запрашивать отдельно через `/listview/.../onetomany|manytomany/...`):**

- `A_M2M_AreaDocument`
- `A_M2M_ConstructionSite`
- `A_M2M_EstateObject`
- `A_O2M_Right`

## `percentbet`

**В API есть, в DTO нет (5 полей):**

| Поле | Тип | Пример |
|------|-----|--------|
| `Advance` | boolean | False |
| `DateCreate` | datetime | 2026-05-05T14:24:30.315682Z |
| `ModifiedAt` | null |  |
| `RowVersion` | int | 4632804 |
| `SpecialRateCalc` | boolean | False |

**Ассоциации (нужно запрашивать отдельно через `/listview/.../onetomany|manytomany/...`):**

- `A_O2M_PercentBetType`

## `shareagreement`

**В API есть, в DTO нет (31 полей):**

| Поле | Тип | Пример |
|------|-----|--------|
| `BudgetFundsAmount` | null |  |
| `CadastralNumber` | null |  |
| `ConstructionPermitDate` | null |  |
| `ConstructionPermitNumber` | null |  |
| `Cost` | null |  |
| `Deadline` | null |  |
| `DepositedAmount` | null |  |
| `DepositorFullName` | null |  |
| `DeveloperINN` | null |  |
| `DeveloperPIN` | null |  |
| `FilingDate` | null |  |
| `HouseNumber` | null |  |
| `HouseNumberPermit` | null |  |
| `IsBorrowedFunds` | boolean | False |
| `IsPreferentialRate` | boolean | False |
| `IsRegisteredProvided` | boolean | False |
| `MonthlyData` | null |  |
| `MotherFundAmount` | null |  |
| `ProjectTitle` | null |  |
| `RegistrationDate` | null |  |
| `RoomKind` | string | ??????????? |
| `RoomNumber` | null |  |
| `RowVersion` | int | 4633503 |
| `SectionNumber` | null |  |
| `SerialNumber` | null |  |
| `StateRegistrationNumber` | null |  |
| `StateRegistrationStatus` | null |  |
| `Street` | null |  |
| `TotalArea` | null |  |
| `TotalLivingArea` | null |  |
| `ValidityStatus` | null |  |

## `deal`

**В API есть, в DTO нет (1 полей):**

| Поле | Тип | Пример |
|------|-----|--------|
| `RowVersion` | int | 3647977 |

**Ассоциации (нужно запрашивать отдельно через `/listview/.../onetomany|manytomany/...`):**

- `A_O2M_PercentBet`

## `organization`

**В DTO есть, в API snapshot нет:**

- `Hidden`

**В API есть, в DTO нет (36 полей):**

| Поле | Тип | Пример |
|------|-----|--------|
| `ActivityInfo` | null |  |
| `AddInfo` | null |  |
| `AdditionalProperties` | null |  |
| `AdditionalPropertiesConfig` | null |  |
| `Beneficiaries` | null |  |
| `Category` | null |  |
| `CEO` | null |  |
| `Criteria1Result` | boolean | False |
| `Criteria2Result` | boolean | False |
| `Criteria3Result` | boolean | False |
| `Criteria4Result` | boolean | False |
| `Criteria5Result` | boolean | False |
| `Criteria6Result` | boolean | False |
| `Criteria7Result` | boolean | False |
| `CurrentUser` | ref |  |
| `Email` | null |  |
| `FoundersInfo` | null |  |
| `Group` | ref |  |
| `Insurance?ompanyAccreditation` | boolean | False |
| `NegativeInfo` | null |  |
| `Phone` | null |  |
| `RatingERZ` | null |  |
| `Region` | null |  |
| `RowVersion` | int | 3610093 |
| `SPV` | boolean | False |
| `SRO` | null |  |
| `StabilityCriteria1` | null |  |
| `StabilityCriteria2` | null |  |
| `StabilityCriteria3` | double | 0.0 |
| `StabilityCriteria4` | null |  |
| `StabilityCriteria5` | null |  |
| `StabilityCriteria6` | null |  |
| `StabilityCriteria7` | null |  |
| `Town` | null |  |
| `Version` | datetime | 2026-04-16T09:36:58.018094Z |
| `WebSite` | null |  |

**Ассоциации (нужно запрашивать отдельно через `/listview/.../onetomany|manytomany/...`):**

- `A_O2M_Experience`

## Справочники (не входят в библиотеку)

Покрыты только GET-запросом для понимания формы. В `Visary.Api.Client` пока ходить за ними нечем — при необходимости добавлять отдельные методы (либо использовать общий `IListViewClient` с произвольной мнемоникой).

### `town` (9 полей)

| Поле | Тип |
|------|-----|
| `Code` | null |
| `CurrentUser` | ref |
| `ID` | int |
| `Region` | ref |
| `RowVersion` | int |
| `Status` | int |
| `Title` | string |
| `Type` | ref |
| `Version` | datetime |

### `region` (8 полей)

| Поле | Тип |
|------|-----|
| `Code` | string |
| `CurrentUser` | null |
| `File` | null |
| `ID` | int |
| `RowVersion` | int |
| `Status` | int |
| `Title` | string |
| `Version` | datetime |

### `projecttype` (7 полей)

| Поле | Тип |
|------|-----|
| `Code` | null |
| `CurrentUser` | null |
| `ID` | int |
| `RowVersion` | int |
| `Status` | int |
| `Title` | string |
| `Version` | null |

### `inflationcalcmethod` (7 полей)

| Поле | Тип |
|------|-----|
| `Code` | string |
| `DeveloperCategory` | int |
| `ID` | int |
| `ModifiedAt` | datetime |
| `ModifiedBy` | ref |
| `RowVersion` | int |
| `Title` | string |

### `estateclass` (9 полей)

| Поле | Тип |
|------|-----|
| `BaseFinishingCost` | double |
| `Code` | null |
| `CurrentUser` | ref |
| `HasLift` | boolean |
| `ID` | int |
| `RowVersion` | int |
| `Status` | int |
| `Title` | string |
| `Version` | datetime |

### `buildingmaterial` (7 полей)

| Поле | Тип |
|------|-----|
| `Code` | null |
| `CurrentUser` | ref |
| `ID` | int |
| `RowVersion` | int |
| `Status` | int |
| `Title` | string |
| `Version` | datetime |

### `finishingmaterial` (8 полей)

| Поле | Тип |
|------|-----|
| `Code` | string |
| `CurrentUser` | null |
| `ID` | int |
| `Ration` | double |
| `RowVersion` | int |
| `Status` | int |
| `Title` | string |
| `Version` | datetime |

### `roomkind` (6 полей)

| Поле | Тип |
|------|-----|
| `Code` | null |
| `ID` | int |
| `RoomCategory` | int |
| `RowVersion` | int |
| `ShortTitle` | string |
| `Title` | string |

## Каталог эндпоинтов (в Postgres `visary_api.endpoints`)

Для каждой мнемоники зарегистрированы 4 стандартных эндпоинта:

| Операция | Метод | URL |
|----------|-------|-----|
| `get_by_id` | GET | `/api/visary/crud/{mnemonic}/{id}` |
| `list` | POST | `/api/visary/listview/{mnemonic}` |
| `create` | POST | `/api/visary/crud/{mnemonic}` |
| `patch` | PATCH | `/api/visary/crud/{mnemonic}/{id}?forceUpdate=false` |

Дополнительно встречаются:

- `POST /api/visary/listview/{mnemonic}/onetomany/{Relation}?associationId={id}` — список связанных по 1:N.
- `POST /api/visary/listview/{mnemonic}/manytomany/{Relation}/link?associationId={id}&ids={otherId}` — линкование M:N.
- `PUT /api/visary/listview/{mnemonic}` — массовое обновление через listview (используется в legacy-методе `UpdateSiteFinishingMaterialAsync`).

## Заметки и ограничения снэпшота

- Тип поля определён эвристикой по реальному ответу. Поля, имевшие `null` на момент снимка, помечены как `null` — реальный тип не выведен. Чтобы уточнить — нужен второй снапшот по записи, у которой эти поля заполнены.
- Поля для **CREATE/PATCH** не покрыты автоматически: API не публикует Swagger, а POST/PATCH — деструктивные операции. Источник правды — примеры в `52-visary-api-method-examples.txt`.
- Snapshot отражает один экземпляр каждой сущности. Для редко заполняемых полей (например, `A_O2M_Deal` у проектов без сделок) тип в snapshot может быть `object{}` — пустой объект.
- Из ~30 мнемоник, проверенных «вслепую», подтверждено только 19. Остальные либо имеют другое имя, либо не существуют как самостоятельные сущности (например, `roompurpose`, `parkingplacetype`, `roomcategory` → 404).

