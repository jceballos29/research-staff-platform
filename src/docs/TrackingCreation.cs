using Bonis.Data;
using Bonis.Models.Tracking;
using Bonis.Services;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Bonis.Jobs.Logging;

namespace Bonis.Jobs
{
    public class TrackingCreation
    {
        private readonly ApplicationDbContext _context;
        private readonly ITrackingService _trackingService;
        private readonly ILogger<TrackingCreation> _logger;

        public TrackingCreation(ApplicationDbContext context, ITrackingService trackingService, ILogger<TrackingCreation> logger)
        {
            _context = context;
            _trackingService = trackingService;
            _logger = logger;
        }

        [AutomaticRetry(Attempts = 0)]
        public void CreateTrackings()
        {
            var jobRunId = JobLogging.NewRunId();
            using var jobScope = JobLogging.BeginJobScope(_logger, jobRunId);

            var currentDate = DateTime.Now;
            JobLogging.Info(_logger, "[JOB][START]",
                "Initializing Tracking Creation... Time={Time} JobRunId={JobRunId}",
                currentDate, jobRunId);

            // Contadores para métricas
            int customersProcessed = 0;
            int customersSkipped = 0;
            int servicesProcessed = 0;
            int resourcesProcessed = 0;
            int resourcesSkipped = 0;
            int trackingsCreated = 0;
            int trackingsSkipped = 0;
            int historicalPeriodsProcessed = 0;
            int historicalTrackingsCreated = 0;
            var skipReasons = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var createdByContentType = new Dictionary<string, int>();

            try
            {
                var customers = _context.Customers.ToList();
                JobLogging.Info(_logger, "[JOB][CUSTOMERS]",
                    "Found {CustomerCount} customers to process", customers.Count);

                foreach (var customer in customers)
                {
                    using var customerScope = JobLogging.BeginJobScope(_logger, jobRunId);

                    JobLogging.Info(_logger, "[CUSTOMER][BEGIN]",
                        "Processing Customer={CustomerName} Id={CustomerId}",
                        customer.CustomerName, customer.CustomerId);

                    var lastService = _context.Services
                        .Where(s => s.CustomerId == customer.CustomerId &&
                                   s.FiscalYearStart <= currentDate &&
                                   currentDate <= s.FiscalYearStart.AddYears(1))
                        .OrderByDescending(s => s.FiscalYearStart)
                        .FirstOrDefault();

                    if (lastService == null)
                    {
                        var reason = "NoActiveService";
                        JobLogging.Warn(_logger, "[CUSTOMER][SKIP]",
                            "Customer={CustomerName} Id={CustomerId} Reason={Reason}",
                            customer.CustomerName, customer.CustomerId, reason);
                        customersSkipped++;
                        if (!skipReasons.ContainsKey(reason)) skipReasons[reason] = 0;
                        skipReasons[reason]++;
                        continue;
                    }

                    customersProcessed++;
                    servicesProcessed++;

                    JobLogging.Info(_logger, "[SERVICE][SELECTED]",
                        "Customer={CustomerName} Service={ServiceDesc} Id={ServiceId} FiscalYearStart={FiscalStart:yyyy-MM-dd} HasTrimestre={HasTrimestre}",
                        customer.CustomerName, lastService.Description, lastService.ServiceId,
                        lastService.FiscalYearStart, lastService.HasTrimestreEvidence);

                    var activeResources = _context.Resources
                        .Where(r => r.ServiceId == lastService.ServiceId &&
                                   r.ProposalStatus != Enums.ProposalStatus.Rejected &&
                                   r.ProposalStatus != Enums.ProposalStatus.Dismissal)
                        .Include(r => r.Periods)
                        .ToList();

                    JobLogging.Info(_logger, "[SERVICE][RESOURCES]",
                        "Service={ServiceId} Found {ResourceCount} active resources",
                        lastService.ServiceId, activeResources.Count);

                    foreach (var resource in activeResources)
                    {
                        using var resourceScope = JobLogging.BeginJobScope(_logger, jobRunId);

                        JobLogging.Info(_logger, "[RESOURCE][BEGIN]",
                            "Processing Resource={EmployeeNumber} Id={ResourceId} NAF={NAF}",
                            resource.EmployeeNumber ?? "Unknown", resource.ResourceId, resource.NAF ?? "Unknown");

                        // ✅ NUEVA LÓGICA: Calcular período fiscal actual
                        var currentFiscalInfo = GetCurrentFiscalPeriodInfo(lastService, currentDate);

                        JobLogging.Info(_logger, "[RESOURCE][FISCAL]",
                            "Resource={EmployeeNumber} CalendarMonth={CalendarMonth} FiscalYear={FiscalYear} FiscalMonth={FiscalMonth} Period={Period}",
                            resource.EmployeeNumber ?? "Unknown", currentFiscalInfo.CalendarMonth,
                            currentFiscalInfo.FiscalYear, currentFiscalInfo.FiscalMonthNumber, currentFiscalInfo.PeriodNumber);

                        if (!IsInAnyPeriod(currentFiscalInfo.FiscalYear, currentFiscalInfo.CalendarMonth, resource.Periods))
                        {
                            var reason = "NotInActivePeriod";
                            JobLogging.Warn(_logger, "[RESOURCE][SKIP]",
                                "Resource={EmployeeNumber} Id={ResourceId} Reason={Reason} FiscalYear={FiscalYear} CalendarMonth={CalendarMonth}",
                                resource.EmployeeNumber ?? "Unknown", resource.ResourceId, reason,
                                currentFiscalInfo.FiscalYear, currentFiscalInfo.CalendarMonth);
                            resourcesSkipped++;
                            if (!skipReasons.ContainsKey(reason)) skipReasons[reason] = 0;
                            skipReasons[reason]++;
                            continue;
                        }

                        resourcesProcessed++;

                        // ✅ NUEVA FUNCIONALIDAD: Obtener todos los períodos faltantes hasta la fecha actual
                        var allMissingPeriods = GetAllMissingPeriodsUntilNow(lastService, currentDate, resource);

                        if (allMissingPeriods.Any())
                        {
                            JobLogging.Info(_logger, "[RESOURCE][HISTORICAL]",
                                "Resource={EmployeeNumber} FoundMissingPeriods={Count} CreatingHistoricalTrackings=true",
                                resource.EmployeeNumber ?? "Unknown", allMissingPeriods.Count);

                            // Crear trackings para cada tipo de contenido en cada período faltante
                            var contentTypes = new[] {
                                Enums.ContentType.Evidence,
                                Enums.ContentType.Training,
                                Enums.ContentType.Absence
                            };

                            int resourceTrackingsCreated = 0;
                            int resourceTrackingsSkipped = 0;
                            int resourceHistoricalCreated = 0;

                            foreach (var missingPeriod in allMissingPeriods)
                            {
                                historicalPeriodsProcessed++;

                                JobLogging.Info(_logger, "[PERIOD][PROCESSING]",
                                    "Resource={EmployeeNumber} ProcessingPeriod={Period} Year={Year} Type={PeriodType}",
                                    resource.EmployeeNumber ?? "Unknown", missingPeriod.Period, missingPeriod.Year, missingPeriod.PeriodType);

                                foreach (var contentType in contentTypes)
                                {
                                    var result = CreateTrackingIfNotExists(resource.ResourceId, missingPeriod.Year, missingPeriod.Period,
                                                                         contentType, currentDate, resource.EmployeeNumber);

                                    if (result.Created)
                                    {
                                        trackingsCreated++;
                                        resourceTrackingsCreated++;
                                        resourceHistoricalCreated++;
                                        historicalTrackingsCreated++;
                                        var contentTypeName = contentType.ToString();
                                        if (!createdByContentType.ContainsKey(contentTypeName))
                                            createdByContentType[contentTypeName] = 0;
                                        createdByContentType[contentTypeName]++;

                                        JobLogging.Info(_logger, "[TRACKING][CREATED][HISTORICAL]",
                                            "Resource={EmployeeNumber} ContentType={ContentType} Period={Period} Year={Year} PeriodType={PeriodType} TrackingId={TrackingId}",
                                            resource.EmployeeNumber ?? "Unknown", contentType.ToString(), missingPeriod.Period, missingPeriod.Year, missingPeriod.PeriodType, result.TrackingId?.ToString() ?? "None");
                                    }
                                    else
                                    {
                                        trackingsSkipped++;
                                        resourceTrackingsSkipped++;
                                        var reason = "AlreadyExists";
                                        if (!skipReasons.ContainsKey(reason)) skipReasons[reason] = 0;
                                        skipReasons[reason]++;

                                        JobLogging.Info(_logger, "[TRACKING][SKIP][HISTORICAL]",
                                            "Resource={EmployeeNumber} ContentType={ContentType} Period={Period} Year={Year} PeriodType={PeriodType} Reason={Reason} ExistingId={ExistingId}",
                                            resource.EmployeeNumber ?? "Unknown", contentType.ToString(), missingPeriod.Period, missingPeriod.Year, missingPeriod.PeriodType, reason, result.ExistingTrackingId?.ToString() ?? "None");
                                    }
                                }
                            }

                            JobLogging.Info(_logger, "[RESOURCE][HISTORICAL_SUMMARY]",
                                "Resource={EmployeeNumber} HistoricalTrackingsCreated={HistoricalCreated} TotalCreated={TotalCreated} TotalSkipped={TotalSkipped}",
                                resource.EmployeeNumber ?? "Unknown", resourceHistoricalCreated, resourceTrackingsCreated, resourceTrackingsSkipped);
                        }
                        else
                        {
                            JobLogging.Info(_logger, "[RESOURCE][HISTORICAL]",
                                "Resource={EmployeeNumber} NoMissingPeriodsFound=true AllPeriodsUpToDate=true",
                                resource.EmployeeNumber ?? "Unknown");
                        }

                        // ✅ MANTENER LÓGICA ORIGINAL: También verificar período actual por separado para logging diferenciado
                        var trackingMonth = lastService.HasTrimestreEvidence
                            ? currentFiscalInfo.PeriodNumber
                            : currentFiscalInfo.CalendarMonth;
                        var trackingYear = currentFiscalInfo.CalendarYear;

                        JobLogging.Info(_logger, "[RESOURCE][CURRENT]",
                            "Resource={EmployeeNumber} CurrentTrackingMonth={TrackingMonth} CurrentTrackingYear={TrackingYear} HasTrimestre={HasTrimestre}",
                            resource.EmployeeNumber ?? "Unknown", trackingMonth, trackingYear, lastService.HasTrimestreEvidence);

                        // Verificar período actual específicamente (puede ser redundante pero útil para logging)
                        var contentTypesForCurrent = new[] {
                            Enums.ContentType.Evidence,
                            Enums.ContentType.Training,
                            Enums.ContentType.Absence
                        };

                        int currentPeriodCreated = 0;
                        int currentPeriodSkipped = 0;

                        foreach (var contentType in contentTypesForCurrent)
                        {
                            var result = CreateTrackingIfNotExists(resource.ResourceId, trackingYear, trackingMonth,
                                                                 contentType, currentDate, resource.EmployeeNumber);

                            if (result.Created)
                            {
                                currentPeriodCreated++;
                                // No incrementar contadores globales aquí para evitar duplicados

                                JobLogging.Info(_logger, "[TRACKING][CREATED][CURRENT]",
                                    "Resource={EmployeeNumber} ContentType={ContentType} Month={Month} Year={Year} TrackingId={TrackingId}",
                                    resource.EmployeeNumber ?? "Unknown", contentType.ToString(), trackingMonth, trackingYear, result.TrackingId?.ToString() ?? "None");
                            }
                            else
                            {
                                currentPeriodSkipped++;

                                JobLogging.Info(_logger, "[TRACKING][SKIP][CURRENT]",
                                    "Resource={EmployeeNumber} ContentType={ContentType} Month={Month} Year={Year} Reason=AlreadyExists ExistingId={ExistingId}",
                                    resource.EmployeeNumber ?? "Unknown", contentType.ToString(), trackingMonth, trackingYear, result.ExistingTrackingId?.ToString() ?? "None");
                            }
                        }

                        JobLogging.Info(_logger, "[RESOURCE][SUMMARY]",
                            "Resource={EmployeeNumber} CurrentPeriodCreated={CurrentCreated} CurrentPeriodSkipped={CurrentSkipped}",
                            resource.EmployeeNumber ?? "Unknown", currentPeriodCreated, currentPeriodSkipped);
                    }

                    // Calcular resumen por customer de manera más eficiente
                    var customerFiscalInfo = GetCurrentFiscalPeriodInfo(lastService, currentDate);
                    var resourcesInPeriod = activeResources.Count(r => IsInAnyPeriod(customerFiscalInfo.FiscalYear, customerFiscalInfo.CalendarMonth, r.Periods));
                    var resourcesNotInPeriod = activeResources.Count - resourcesInPeriod;

                    JobLogging.Info(_logger, "[CUSTOMER][SUMMARY]",
                        "Customer={CustomerName} Resources={ResourceCount} Processed={ResourcesProcessed} Skipped={ResourcesSkipped}",
                        customer.CustomerName, activeResources.Count, resourcesInPeriod, resourcesNotInPeriod);
                }
            }
            catch (Exception ex)
            {
                JobLogging.Error(_logger, "[JOB][ERROR]",
                    "Error while creating trackings: {Message} StackTrace={StackTrace}",
                    ex.Message, ex.StackTrace ?? "No stack trace available");
                throw;
            }
            finally
            {
                // Resumen final
                var contentTypeSummary = string.Join(", ", createdByContentType.Select(kv => $"{kv.Key}:{kv.Value}"));
                var reasonsTop = string.Join(", ", skipReasons.OrderByDescending(kv => kv.Value).Take(5).Select(kv => $"{kv.Key}:{kv.Value}"));

                JobLogging.Info(_logger, "[JOB][FINISH]",
                    "CustomersTotal={CustomersTotal} Processed={CustomersProcessed} Skipped={CustomersSkipped} " +
                    "ServicesProcessed={ServicesProcessed} ResourcesTotal={ResourcesTotal} ResourcesProcessed={ResourcesProcessed} ResourcesSkipped={ResourcesSkipped} " +
                    "TrackingsCreated={TrackingsCreated} TrackingsSkipped={TrackingsSkipped} " +
                    "HistoricalPeriodsProcessed={HistoricalPeriodsProcessed} HistoricalTrackingsCreated={HistoricalTrackingsCreated} " +
                    "ContentTypes={ContentTypes} SkipReasonsTop={Reasons}",
                    customersProcessed + customersSkipped, customersProcessed, customersSkipped,
                    servicesProcessed, resourcesProcessed + resourcesSkipped, resourcesProcessed, resourcesSkipped,
                    trackingsCreated, trackingsSkipped,
                    historicalPeriodsProcessed, historicalTrackingsCreated,
                    contentTypeSummary, reasonsTop);

                JobLogging.Info(_logger, "[JOB]", "Tracking Creation Finished");
            }
        }

        /// <summary>
        /// Obtiene información del período fiscal actual para un servicio en una fecha específica
        /// </summary>
        /// <param name="service">Servicio con configuración fiscal</param>
        /// <param name="currentDate">Fecha actual para calcular el período</param>
        /// <returns>Información del período fiscal actual</returns>
        private FiscalPeriodInfo GetCurrentFiscalPeriodInfo(Service service, DateTime currentDate)
        {
            var fiscalStart = service.FiscalYearStart;
            var monthsDiff = (currentDate.Year - fiscalStart.Year) * 12 + (currentDate.Month - fiscalStart.Month);

            var fiscalYear = fiscalStart.Year + (monthsDiff / 12);
            var fiscalMonthNumber = (monthsDiff % 12) + 1;

            // Para cálculo de períodos trimestrales
            var periodNumber = (fiscalMonthNumber - 1) / 3 + 1;

            return new FiscalPeriodInfo
            {
                FiscalYear = fiscalYear,
                FiscalMonthNumber = fiscalMonthNumber,
                CalendarYear = currentDate.Year,
                CalendarMonth = currentDate.Month,
                PeriodNumber = periodNumber
            };
        }

        /// <summary>
        /// Verifica si el año y mes dados están dentro de algún período activo del recurso
        /// Maneja correctamente períodos abiertos (EndDate = null) hasta el final del año fiscal
        /// </summary>
        private bool IsInAnyPeriod(int fiscalYear, int calendarMonth, ICollection<ResourceExclusivePeriod>? periods)
        {
            if (periods == null || !periods.Any())
            {
                JobLogging.Warn(_logger, "[PERIODS][VALIDATION]",
                    "No periods found for resource FiscalYear={FiscalYear} Month={Month}",
                    fiscalYear, calendarMonth);
                return false;
            }

            var checkDate = new DateTime(fiscalYear, calendarMonth, 1);

            foreach (var period in periods)
            {
                var isInPeriod = IsDateInPeriod(fiscalYear, calendarMonth, period);

                JobLogging.Info(_logger, "[PERIODS][CHECK]",
                    "FiscalYear={FiscalYear} Month={Month} PeriodStart={PeriodStart:yyyy-MM-dd} PeriodEnd={PeriodEnd} IsInPeriod={IsInPeriod}",
                    fiscalYear, calendarMonth, period.StartDate,
                    period.EndDate?.ToString("yyyy-MM-dd") ?? "OPEN", isInPeriod);

                if (isInPeriod) return true;
            }

            return false;
        }

        /// <summary>
        /// Verifica si una fecha específica está dentro de un período de recurso
        /// Maneja períodos abiertos (EndDate = null) como activos hasta el final del año fiscal
        /// </summary>
        private bool IsDateInPeriod(int year, int month, ResourceExclusivePeriod period)
        {
            var checkDate = new DateTime(year, month, 1);
            var periodStart = new DateTime(period.StartDate.Year, period.StartDate.Month, 1);

            // La fecha debe ser >= inicio del período
            if (checkDate < periodStart)
            {
                JobLogging.Info(_logger, "[PERIODS][CHECK_DETAIL]",
                    "Date {CheckDate:yyyy-MM-dd} is before period start {PeriodStart:yyyy-MM-dd}",
                    checkDate, periodStart);
                return false;
            }

            // Si no hay fecha de fin, el período está activo hasta el final del año fiscal
            if (period.EndDate == null)
            {
                // Calcular el final del año fiscal (último día del año que contiene la fecha de verificación)
                var fiscalYearEnd = new DateTime(year, 12, 31);
                var isActive = checkDate <= fiscalYearEnd;

                JobLogging.Info(_logger, "[PERIODS][OPEN_PERIOD]",
                    "Open period from {PeriodStart:yyyy-MM-dd} to end of fiscal year {FiscalYearEnd:yyyy-MM-dd}. Date {CheckDate:yyyy-MM-dd} IsActive={IsActive}",
                    periodStart, fiscalYearEnd, checkDate, isActive);

                return isActive;
            }

            // Si hay fecha de fin, verificar que esté dentro del rango
            var periodEnd = new DateTime(period.EndDate.Value.Year, period.EndDate.Value.Month,
                                        DateTime.DaysInMonth(period.EndDate.Value.Year, period.EndDate.Value.Month));

            var isInRange = checkDate <= periodEnd;

            JobLogging.Info(_logger, "[PERIODS][CLOSED_PERIOD]",
                "Closed period from {PeriodStart:yyyy-MM-dd} to {PeriodEnd:yyyy-MM-dd}. Date {CheckDate:yyyy-MM-dd} IsInRange={IsInRange}",
                periodStart, periodEnd, checkDate, isInRange);

            return isInRange;
        }

        /// <summary>
        /// Crea un tracking si no existe uno para los parámetros dados
        /// </summary>
        /// <param name="resourceId">ID del recurso</param>
        /// <param name="year">Año fiscal</param>
        /// <param name="month">Mes o período (dependiendo del servicio)</param>
        /// <param name="contentType">Tipo de contenido</param>
        /// <param name="currentDate">Fecha de creación</param>
        /// <param name="employeeNumber">Número de empleado para logging</param>
        /// <returns>Resultado de la creación</returns>
        private TrackingCreationResult CreateTrackingIfNotExists(Guid resourceId, int year, int month,
                                                                 Enums.ContentType contentType, DateTime currentDate,
                                                                 string? employeeNumber = null)
        {
            var existingTracking = _context.Trackings
                .FirstOrDefault(t => t.ResourceId == resourceId &&
                                   t.Year == year &&
                                   t.Month == month &&
                                   t.ContentType == contentType);

            if (existingTracking != null)
            {
                return new TrackingCreationResult
                {
                    Created = false,
                    ExistingTrackingId = existingTracking.TrackingId,
                    EmployeeNumber = employeeNumber
                };
            }

            var newTracking = new Tracking
            {
                ResourceId = resourceId,
                Year = year,
                Month = month,
                ContentType = contentType,
                Created = currentDate,
                Modified = currentDate,
                TrackingApproveStatus = Enums.TrackingStatus.Draft
            };

            _context.Trackings.Add(newTracking);
            _context.SaveChanges();

            return new TrackingCreationResult
            {
                Created = true,
                TrackingId = newTracking.TrackingId,
                EmployeeNumber = employeeNumber
            };
        }

        /// <summary>
        /// Obtiene todos los períodos faltantes desde el inicio del año fiscal hasta la fecha actual
        /// </summary>
        /// <param name="service">Servicio con configuración fiscal</param>
        /// <param name="currentDate">Fecha actual</param>
        /// <param name="resource">Recurso para validar períodos activos</param>
        /// <returns>Lista de períodos que necesitan trackings</returns>
        private List<PeriodInfo> GetAllMissingPeriodsUntilNow(Service service, DateTime currentDate, Resource resource)
        {
            var missingPeriods = new List<PeriodInfo>();
            var currentFiscalInfo = GetCurrentFiscalPeriodInfo(service, currentDate);

            JobLogging.Info(_logger, "[PERIODS][ANALYSIS]",
                "Resource={EmployeeNumber} AnalyzingPeriods FiscalYear={FiscalYear} CurrentPeriod={CurrentPeriod} HasTrimestre={HasTrimestre}",
                resource.EmployeeNumber ?? "Unknown", currentFiscalInfo.FiscalYear,
                service.HasTrimestreEvidence ? currentFiscalInfo.PeriodNumber : currentFiscalInfo.CalendarMonth,
                service.HasTrimestreEvidence);

            if (service.HasTrimestreEvidence)
            {
                // Para servicios trimestrales: verificar Q1, Q2, Q3... hasta el actual
                for (int quarter = 1; quarter <= currentFiscalInfo.PeriodNumber; quarter++)
                {
                    // Usar el mes medio del trimestre para validación con ResourceExclusivePeriod
                    var quarterMiddleMonth = ((quarter - 1) * 3) + 2; // Q1=feb, Q2=may, Q3=ago, Q4=nov

                    // ✅ VALIDACIÓN CRÍTICA: Verificar que el trimestre esté en períodos del recurso
                    if (IsInAnyPeriod(currentFiscalInfo.FiscalYear, quarterMiddleMonth, resource.Periods))
                    {
                        // Verificar si faltan trackings para este trimestre
                        if (!AllContentTypesExistForPeriod(resource.ResourceId, currentFiscalInfo.CalendarYear, quarter))
                        {
                            missingPeriods.Add(new PeriodInfo
                            {
                                Year = currentFiscalInfo.CalendarYear,
                                Period = quarter,
                                PeriodType = "Quarter",
                                ValidatedByResourcePeriods = true
                            });

                            JobLogging.Info(_logger, "[PERIODS][MISSING]",
                                "Resource={EmployeeNumber} MissingQuarter={Quarter} Year={Year} MiddleMonth={MiddleMonth}",
                                resource.EmployeeNumber ?? "Unknown", quarter, currentFiscalInfo.CalendarYear, quarterMiddleMonth);
                        }
                        else
                        {
                            JobLogging.Info(_logger, "[PERIODS][EXISTS]",
                                "Resource={EmployeeNumber} QuarterExists={Quarter} Year={Year}",
                                resource.EmployeeNumber ?? "Unknown", quarter, currentFiscalInfo.CalendarYear);
                        }
                    }
                    else
                    {
                        JobLogging.Warn(_logger, "[PERIODS][SKIP]",
                            "Resource={EmployeeNumber} Quarter={Quarter} Year={Year} Reason=NotInResourcePeriods MiddleMonth={MiddleMonth}",
                            resource.EmployeeNumber ?? "Unknown", quarter, currentFiscalInfo.FiscalYear, quarterMiddleMonth);
                    }
                }
            }
            else
            {
                // Para servicios mensuales: verificar desde inicio fiscal hasta mes actual
                var fiscalStart = service.FiscalYearStart;

                // Calcular todos los meses desde el inicio fiscal hasta el mes actual
                for (int monthOffset = 0; monthOffset < currentFiscalInfo.FiscalMonthNumber; monthOffset++)
                {
                    var targetFiscalMonth = monthOffset + 1;
                    var targetCalendarMonth = ((fiscalStart.Month - 1 + monthOffset) % 12) + 1;
                    var targetYear = currentFiscalInfo.CalendarYear;

                    // Ajustar año si el mes fiscal cruza años calendario
                    if (fiscalStart.Month > 1 && targetCalendarMonth < fiscalStart.Month)
                    {
                        targetYear = currentFiscalInfo.CalendarYear;
                    }
                    else if (fiscalStart.Month > 1 && targetCalendarMonth >= fiscalStart.Month)
                    {
                        targetYear = currentFiscalInfo.CalendarYear - 1;
                    }

                    // ✅ VALIDACIÓN CRÍTICA: Verificar que el mes esté en períodos del recurso
                    if (IsInAnyPeriod(currentFiscalInfo.FiscalYear, targetCalendarMonth, resource.Periods))
                    {
                        // Verificar si faltan trackings para este mes (usando mes calendario como en lógica actual)
                        if (!AllContentTypesExistForPeriod(resource.ResourceId, targetYear, targetCalendarMonth))
                        {
                            missingPeriods.Add(new PeriodInfo
                            {
                                Year = targetYear,
                                Period = targetCalendarMonth,
                                PeriodType = "Month",
                                ValidatedByResourcePeriods = true
                            });

                            JobLogging.Info(_logger, "[PERIODS][MISSING]",
                                "Resource={EmployeeNumber} MissingMonth={Month} Year={Year} FiscalMonth={FiscalMonth}",
                                resource.EmployeeNumber ?? "Unknown", targetCalendarMonth, targetYear, targetFiscalMonth);
                        }
                        else
                        {
                            JobLogging.Info(_logger, "[PERIODS][EXISTS]",
                                "Resource={EmployeeNumber} MonthExists={Month} Year={Year}",
                                resource.EmployeeNumber ?? "Unknown", targetCalendarMonth, targetYear);
                        }
                    }
                    else
                    {
                        JobLogging.Warn(_logger, "[PERIODS][SKIP]",
                            "Resource={EmployeeNumber} Month={Month} Year={Year} Reason=NotInResourcePeriods FiscalYear={FiscalYear}",
                            resource.EmployeeNumber ?? "Unknown", targetCalendarMonth, targetYear, currentFiscalInfo.FiscalYear);
                    }
                }
            }

            JobLogging.Info(_logger, "[PERIODS][SUMMARY]",
                "Resource={EmployeeNumber} TotalMissingPeriods={Count} PeriodType={PeriodType}",
                resource.EmployeeNumber ?? "Unknown", missingPeriods.Count, service.HasTrimestreEvidence ? "Quarter" : "Month");

            return missingPeriods;
        }

        /// <summary>
        /// Verifica si existen trackings para todos los content types en un período específico
        /// </summary>
        /// <param name="resourceId">ID del recurso</param>
        /// <param name="year">Año</param>
        /// <param name="period">Período (mes o trimestre)</param>
        /// <returns>True si existen todos los content types</returns>
        private bool AllContentTypesExistForPeriod(Guid resourceId, int year, int period)
        {
            var contentTypes = new[] {
                Enums.ContentType.Evidence,
                Enums.ContentType.Training,
                Enums.ContentType.Absence
            };

            return contentTypes.All(contentType =>
                _context.Trackings.Any(t =>
                    t.ResourceId == resourceId &&
                    t.Year == year &&
                    t.Month == period &&
                    t.ContentType == contentType));
        }
    }

    /// <summary>
    /// Información del período fiscal actual
    /// </summary>
    public class FiscalPeriodInfo
    {
        public int FiscalYear { get; set; }
        public int FiscalMonthNumber { get; set; }
        public int CalendarYear { get; set; }
        public int CalendarMonth { get; set; }
        public int PeriodNumber { get; set; }
    }

    /// <summary>
    /// Información de un período específico que necesita trackings
    /// </summary>
    public class PeriodInfo
    {
        public int Year { get; set; }
        public int Period { get; set; }
        public string PeriodType { get; set; } = string.Empty; // "Month" o "Quarter"
        public bool ValidatedByResourcePeriods { get; set; }
    }

    /// <summary>
    /// Resultado de la creación de tracking
    /// </summary>
    public class TrackingCreationResult
    {
        public bool Created { get; set; }
        public Guid? TrackingId { get; set; }
        public Guid? ExistingTrackingId { get; set; }
        public string? EmployeeNumber { get; set; }
    }
}