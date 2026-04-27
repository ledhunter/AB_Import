
## Системные таблицы

### \`__EFMigrationsHistory\`

| Поле | Тип | Описание |
|------|-----|----------|
| MigrationId | varchar(150) | ID миграции |
| ProductVersion | varchar(32) | Версия продукта |

### \`__fs_link_migration_log\`

| Поле | Тип | Описание |
|------|-----|----------|
| ID | int4 | Идентификатор |
| Database | text | База данных |
| SchemaName | text | Имя схемы |
| Table | text | Имя таблицы |
| RowId | int4 | ID строки |
| Property | text | Свойство |
| OldLink | text | Старая ссылка |
| NewLink | text | Новая ссылка |
| LinkContentJson | text | JSON содержимого ссылки |
| Error | text | Ошибка |
| LinkConverted | bool | Ссылка конвертирована |

## Примечания по структуре

1. **Связи многие-ко-многим** реализованы через промежуточные таблицы
2. **Системные поля**: \`Hidden\$, \`SortOrder\$, \`Version\$, \`SysAllParents\$
3. **Финансовые расчёты** используют тип \`numeric\$ для точности
4. **Коды справочников** часто используют \`varchar(4)\$
5. **Аудит** реализован через таблицы типа \`*Audit\$ и \`*Calculation\$

## Приложения

### A. Полный список таблиц по схемам

#### Base
- ObjectPermission, ObjectPermission, ObjectValidationRule, ObjectValidationRule_Rules
- ProfileAccess, Role, Role_Permissions, Role_Permissions_PropertyPermissions
- Substitute, User, UserAndUserGroup, UserGroup, UserGroupAndRole, UserGroupModel
- UserLastActivity

#### Data (основные)
- AcceptedContract, AddAgreement, AddDictionaryName, AdditionalDictionary
- AdditionalPropertiesConfig, AdditionalPropertiesConfig_Properties
- AdditionalProperty, AdditionalPropertyType, AdditionalPropertyType_ViewComponentParameters
- AnalogBudgetWBS, AnalogCriteria, AnalogCriteriaType, AnalogCriteria_Analog*
- AnalogToRatesPrices, AnalogType, Analysis, AnalysisByStage
- AnalysisLicense, AnalysisProjectManagement, AnalysisSpecialZone
- AnalysisStatusToColor, AreaDocBrief, AreaDocument, AreaDocument_UseType
- AreaToSpecialZone, AssuranceSystem, BankPolicy, BankPolicyItem
- BankPolicyItemTemplate, BankPolicyTemplate, BaseRisk, BuildingMaterial
- CadastralArea, CadastralAreaConstructionSite, CadastralAreaHistory
- CadastralAreaToAnalysis, CadastralAreaToSection, CadastralArea_UseTypes
- CashFlow, CashFlowCalculation, CashFlowExpenses, CashFlowParameterValue
- CashFlowParameterValue_CashFlowPeriods, CashFlowPeriod, CashFlowSalesData
- CashFlowScript, CashFlowScriptToCashFlowScript, CategoryProblemsOrganization
- Chapter, CheckList, CheckListCalculation, CheckListItem
- CheckListToDocSubType, CheckList_Observers, CheckList_Recipients
- CheckPoint, CheckPointResponsible, CheckPointToCheckPointResponsible
- CheckPointToParticipantIssueCondition, CheckRequery, CheckRequery_Observers
- CheckRequery_Recipients, ClientData, ClientSegment, ClosureDocument
- ClosureDocumentToAudit, CodeControlEvent, CollateralInsuranceContract
- Comment, CommentWBS, CommentWBSSlice, CommentZoneAnalysis
- CompanyGroup, ConditionTypeCode, ConstructionCostBase
- ConstructionCostRegionalAdjustment, ConstructionCostSpecialAdjustment
- ConstructionOLAP, ConstructionProgram, ConstructionProjectCalculated
- ConstructionProjectToAddDictionary, ConstructionProjectToCheckPoint
- ConstructionSection, ConstructionSiteAnalog, ConstructionSiteAreaCalculation
- ConstructionSiteCalculation, ConstructionSiteIndicator
- ConstructionSiteIndicatorCalculation, ConstructionSiteIndicatorValue
- ConstructionSiteToAddDictionary, ConstructionSite_PledgerCollection
- ConstructionSite_Queues, ConstructionType, Contract, ContractAudit
- ContractCurrency, ControlPeriodByEvent, CopyOfSumDeclaredWBS
- CostIndex, CostItem, DDaNJournal, Deal, DocAnalisysTemplate
- DocSourceType, DocSubType, DocToEstate, DocToSite, DocumentArea
- EAConclusion, EAIndicator, EAPermissiveDocIndicator, EQCalculate
- EngineeringSurveys, EngineeringSurveysType, EscrowAccount, EscrowAccountStatus
- EscrowAccount_SlaveObject, EstateClass, EstateObject, EstateObjectToCadastralArea
- EstateObjectType, EstateObject_Materials, Experience, FeatureIssueCondition
- FileReference, FinSource, FinishingCost, FinishingMaterial, FloorCategory
- FounderEntity, FounderPerson, FrequencyMonitoring, Hub, ImportRestorePoint
- IndexingDeclareBudgetAnalogResult, IndicatorToSectionType, InflationIndex
- InfrastructureClass, InfrastructureRequest, InfrastructureSubClass
- JournalToJournal, LandCategory, License, LicenseType, LiveSuspensiveCondition
- ManagementToSite, MediaLink, MediaLinkKind, MonitoringSiteRatesPrices
- MonitoringSuspensiveCondition, MonthDataToAnalog, MonthlyData
- MultiImportPart, Notice, ODCalculate, ObjectDocumentation, OpenData
- Organization, OrganizationPerson, OrganizationToAddDictionary
- ParticipantsIssueCondition, Payment, PaymentAudit, PaymentStatus, PaymentType
- PercentBet, PercentBetType, PerformerDoneWBS, PeriodControlEvent
- PermissiveDoc, PermissiveDocBrief, PhotoReportItem, PhotoReportType
- PolicyItem, Portfolio, PredictInflation, ProjectAnalog, ProjectAnalogWBS
- ProjectAudit, ProjectAuditCalculation, ProjectAuditCashFlow
- ProjectAuditName, ProjectAuditPresence, ProjectAuditRequery
- ProjectAuditSiteRatesPrices, ProjectAuditToAnalogCriteria, ProjectCashFlow
- ProjectCashFlowCalculation, ProjectDoc, ProjectLicensing, ProjectManagement
- ProjectManagementRole, ProjectManagementRole_LicensesTypes
- ProjectParameter, ProjectParameterName, ProjectParameterTemplate
- ProjectParameterTemplateForProject, ProjectParticipant, ProjectPortfolio
- ProjectRatesPrices, ProjectRatesPricesToSiteRatesPrices, ProjectRole
- ProjectStageEnum, ProjectType, ProjectСoncept, Purpose, QuantityIndicator
- QuantityIndicator_Sources, QuantityParameter, QuarterCashFlow
- QuarterRatesPrices, Queues, RFCStatus, RFCalculate, RatingParticipantsIssueCondition
- RecurringJob, Region, Restriction, Right, RightRestrictionType
- RightToRestriction, RiskCode, RiskTemplate, RiskType, Room, RoomKind
- SalesData, SalesDataCalculate, ScriptTemplate, ScriptTemplateItem
- ScriptTemplateItem_Parameters, ScriptTemplateItem_Relative
- SectionComposition, SectionTotals, SectionType, ShareAgreement, SignOfTrouble
- SiteCashFlow, SiteCashFlowCalculation, SiteCashFlowSummary, SiteRatesPrices
- SiteRatesPricesCalculate, SourceDataTotals, SpecialZone, SpecialZoneSource
- SpecialZoneType, SpecialZone_Sources, StageToTemplate
- StatusEventsMonitoring, StatusRisk, StressTestResults, StressTestResultsV2
- StroyAnalogCriteria, StroyBalanceAudit, SubjectSuspensiveCondition
- SubmittedDocuments, SubmittedDocumentsItem, SubmittedDocumentsTemplate
- SubmittedDocumentsTemplateItem, SubmittedDocumentsToAddDictionary
- TargetGoal, TargetIndicator, TargetIndicatorValue, TargetJob, TargetProgram
- TechDataSheet, TechnicalCondition, TerritoryZone, Town, TownRegionInformation
- TownType, Tranche, TrancheCalculation, TypeSuspensiveCondition
- TypedImportJournal, TypedImportJournal_MultiImportParts, Unit, UseTypeArea
- UserCategoryToRecognizeCode, WBS, WBSContract, WBSSlice, WBSSnapshot
- WorkItem

#### Demo
- DemoCategorizedObject, DemoObjectAndWorkflowObject, DemoObjectExt
- DemoObjectItem, DemoObjectType, DemoObject_Observers, DemoObject_TreeNodes
- DemoObject_Types, DemoTreeObject, TestObject, TestObject2, WorkflowObject

#### FinModel
- CalculationMethod, CodeGroup, FMCode, FMCode_BaseRegisters, FMCode_Registers
- FMGroup, FMModel, FMModelVersion, FMRegister, FMResult, FMUnit
- FinModelPortfolio, InputData, WbsToFmCode

#### GIS
- ClientMapLayerConfig, DemoVehicle, MapConfigPreset, MapConfigPreset_WmsLayers
- MapLayerConfig, ServerMapLayerConfig, TrackerMapLayerConfig
- UserGroupAndMapConfigPreset

#### LinkingObject
- ObjectLink, ObjectLinkSetting, ObjectLinkType

#### UI
- AppPreset, AutoTextEntity, DetailViewPreset, GridPreset, MaskObject
- MenuPreset, MnemonicEntity, UiEnum, UiEnum_Values, UserFilter
- UserGroupAndMenuPreset, VisEntityInfo, VisModuleInfo, VisPropertyInfo

#### public
- __EFMigrationsHistory, __fs_link_migration_log

