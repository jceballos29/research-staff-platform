using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace YourNamespace.Services
{
    // DTOs y Modelos auxiliares
    public class FiscalMonthInfo
    {
        public int Month { get; set; }
        public int Year { get; set; }
    }

    public class FiscalQuarterInfo
    {
        public int Number { get; set; }
        public FiscalMonthInfo Start { get; set; }
        public FiscalMonthInfo End { get; set; }
    }

    public class FiscalYearInformation
    {
        public FiscalMonthInfo CurrentFiscalMonth { get; set; }
        public FiscalQuarterInfo CurrentFiscalQuarter { get; set; }
        public List<FiscalMonthInfo> FiscalMonths { get; set; }
        public List<FiscalQuarterInfo> FiscalQuarters { get; set; }
    }

    public class ExpectedPeriod
    {
        public int Month { get; set; }
        public int Year { get; set; }
        public string Schedule { get; set; }
    }

    public class TrackingStats
    {
        public int CustomersProcessed { get; set; }
        public int ServicesProcessed { get; set; }
        public int ResourcesProcessed { get; set; }
        public int PeriodsChecked { get; set; }
        public int TrackingsCreated { get; set; }
        public int TrackingsSkipped { get; set; }
        public int TrackingsFixed { get; set; }
        public int TrackingsChecked { get; set; }
        public int Errors { get; set; }
        public long DurationMs { get; set; }
    }

    public class TrackingService
    {
        private readonly YourDbContext _context;
        private readonly ILogger<TrackingService> _logger;

        public TrackingService(YourDbContext context, ILogger<TrackingService> logger)
        {
            _context = context;
            _logger = logger;
        }

        #region Helper Methods

        /// <summary>
        /// Obtiene la información del año fiscal incluyendo meses y trimestres
        /// </summary>
        private FiscalYearInformation GetFiscalYearInformation(Service service, DateTime currentDate)
        {
            var startMonth = service.FiscalYearStart.Month;
            var startYear = service.FiscalYearStart.Year;
            var endYear = startYear + 1;

            var fiscalMonths = new List<FiscalMonthInfo>();
            var fiscalQuarters = new List<FiscalQuarterInfo>();

            // Generar los 12 meses fiscales
            for (int i = 0; i < 12; i++)
            {
                int month = startMonth + i;
                int year = startYear;
                
                if (month > 12)
                {
                    month -= 12;
                    year = endYear;
                }
                
                fiscalMonths.Add(new FiscalMonthInfo { Month = month, Year = year });
            }

            // Generar los 4 trimestres fiscales
            for (int q = 0; q < 4; q++)
            {
                int startQuarterMonth = startMonth + q * 3;
                int endQuarterMonth = startQuarterMonth + 2;
                int startQuarterYear = startYear;
                int endQuarterYear = startYear;

                if (startQuarterMonth > 12)
                {
                    startQuarterMonth -= 12;
                    startQuarterYear = endYear;
                }
                
                if (endQuarterMonth > 12)
                {
                    endQuarterMonth -= 12;
                    endQuarterYear = endYear;
                }

                fiscalQuarters.Add(new FiscalQuarterInfo
                {
                    Number = q + 1,
                    Start = new FiscalMonthInfo { Month = startQuarterMonth, Year = startQuarterYear },
                    End = new FiscalMonthInfo { Month = endQuarterMonth, Year = endQuarterYear }
                });
            }

            // Determinar el mes fiscal actual
            FiscalMonthInfo currentFiscalMonth = null;
            foreach (var fm in fiscalMonths)
            {
                if (fm.Month == currentDate.Month && fm.Year == currentDate.Year)
                {
                    currentFiscalMonth = fm;
                    break;
                }
            }

            // Determinar el trimestre fiscal actual
            FiscalQuarterInfo currentFiscalQuarter = null;
            foreach (var fq in fiscalQuarters)
            {
                var startDate = new DateTime(fq.Start.Year, fq.Start.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                var endDate = new DateTime(fq.End.Year, fq.End.Month, DateTime.DaysInMonth(fq.End.Year, fq.End.Month), 23, 59, 59, DateTimeKind.Utc);
                
                if (currentDate >= startDate && currentDate <= endDate)
                {
                    currentFiscalQuarter = fq;
                    break;
                }
            }

            return new FiscalYearInformation
            {
                CurrentFiscalMonth = currentFiscalMonth,
                CurrentFiscalQuarter = currentFiscalQuarter,
                FiscalMonths = fiscalMonths,
                FiscalQuarters = fiscalQuarters
            };
        }

        /// <summary>
        /// Obtiene los meses cubiertos por los periodos exclusivos dentro del año fiscal
        /// </summary>
        private List<FiscalMonthInfo> GetMonthsToResourcePeriods(List<ExclusivePeriod> periods, Service service)
        {
            var monthsSet = new HashSet<string>();
            var fiscalStartYear = service.FiscalYearStart.Year;
            var fiscalStartMonth = service.FiscalYearStart.Month;
            var fiscalEndYear = fiscalStartYear + 1;

            // Calcular el último mes del año fiscal
            var lastFiscalMonth = fiscalStartMonth == 1 ? 12 : fiscalStartMonth - 1;
            var lastFiscalYear = fiscalStartMonth == 1 ? fiscalEndYear - 1 : fiscalEndYear;

            foreach (var period in periods)
            {
                var startDate = period.StartDate;
                var endDate = period.EndDate ?? new DateTime(lastFiscalYear, lastFiscalMonth, DateTime.DaysInMonth(lastFiscalYear, lastFiscalMonth), 0, 0, 0, DateTimeKind.Utc);

                var current = new DateTime(startDate.Year, startDate.Month, 1, 0, 0, 0, DateTimeKind.Utc);

                while (current <= endDate)
                {
                    var month = current.Month;
                    var year = current.Year;

                    // Verificar si está dentro del año fiscal
                    var isInFiscalYear = 
                        (year == fiscalStartYear && month >= fiscalStartMonth) ||
                        (year == fiscalEndYear && month < fiscalStartMonth);

                    if (isInFiscalYear)
                    {
                        monthsSet.Add($"{year}-{month}");
                    }

                    current = current.AddMonths(1);
                }
            }

            return monthsSet.Select(key =>
            {
                var parts = key.Split('-');
                return new FiscalMonthInfo
                {
                    Year = int.Parse(parts[0]),
                    Month = int.Parse(parts[1])
                };
            }).ToList();
        }

        /// <summary>
        /// Calcula el schedule correcto para un tracking
        /// </summary>
        private string CalculateScheduleForTracking(int month, int year, Service service, FiscalYearInformation fiscalInfo)
        {
            if (service.HasTrimestralEvidences)
            {
                var quarter = fiscalInfo.FiscalQuarters.FirstOrDefault(q => q.Number == month);
                
                if (quarter == null)
                {
                    _logger.LogWarning($"[FIX][WARN] - No se encontró el trimestre {month} para el tracking");
                    return null;
                }
                
                return $"{quarter.Start.Month}/{quarter.Start.Year} - {quarter.End.Month}/{quarter.End.Year}";
            }
            else
            {
                return $"{month}/{year}";
            }
        }

        /// <summary>
        /// Genera todos los periodos esperados hasta la fecha actual
        /// </summary>
        private List<ExpectedPeriod> GetExpectedTrackingPeriods(Service service, DateTime currentDate)
        {
            var fiscalStartYear = service.FiscalYearStart.Year;
            var fiscalStartMonth = service.FiscalYearStart.Month;
            var fiscalEndYear = fiscalStartYear + 1;
            var periods = new List<ExpectedPeriod>();

            if (service.HasTrimestralEvidences)
            {
                // Generar trimestres fiscales
                for (int q = 0; q < 4; q++)
                {
                    int startQuarterMonth = fiscalStartMonth + q * 3;
                    int endQuarterMonth = startQuarterMonth + 2;
                    int startQuarterYear = fiscalStartYear;
                    int endQuarterYear = fiscalStartYear;

                    if (startQuarterMonth > 12)
                    {
                        startQuarterMonth -= 12;
                        startQuarterYear = fiscalEndYear;
                    }
                    
                    if (endQuarterMonth > 12)
                    {
                        endQuarterMonth -= 12;
                        endQuarterYear = fiscalEndYear;
                    }

                    var quarterStartDate = new DateTime(startQuarterYear, startQuarterMonth, 1, 0, 0, 0, DateTimeKind.Utc);
                    
                    if (quarterStartDate <= currentDate)
                    {
                        periods.Add(new ExpectedPeriod
                        {
                            Month = q + 1,
                            Year = startQuarterYear,
                            Schedule = $"{startQuarterMonth}/{startQuarterYear} - {endQuarterMonth}/{endQuarterYear}"
                        });
                    }
                }
            }
            else
            {
                // Generar meses fiscales
                for (int i = 0; i < 12; i++)
                {
                    int month = fiscalStartMonth + i;
                    int year = fiscalStartYear;
                    
                    if (month > 12)
                    {
                        month -= 12;
                        year = fiscalEndYear;
                    }

                    var monthStartDate = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
                    
                    if (monthStartDate <= currentDate)
                    {
                        periods.Add(new ExpectedPeriod
                        {
                            Month = month,
                            Year = year,
                            Schedule = $"{month}/{year}"
                        });
                    }
                }
            }

            return periods;
        }

        /// <summary>
        /// Verifica si un periodo está cubierto por periodos exclusivos
        /// </summary>
        private bool IsPeriodInExclusivePeriods(int periodMonth, int periodYear, List<ExclusivePeriod> exclusivePeriods, Service service)
        {
            var monthsToTrack = GetMonthsToResourcePeriods(exclusivePeriods, service);
            
            if (service.HasTrimestralEvidences)
            {
                var fiscalStartMonth = service.FiscalYearStart.Month;
                var fiscalStartYear = service.FiscalYearStart.Year;
                var fiscalEndYear = fiscalStartYear + 1;
                
                int startQuarterMonth = fiscalStartMonth + (periodMonth - 1) * 3;
                int startQuarterYear = fiscalStartYear;
                
                if (startQuarterMonth > 12)
                {
                    startQuarterMonth -= 12;
                    startQuarterYear = fiscalEndYear;
                }
                
                // Verificar si al menos un mes del trimestre está en periodo exclusivo
                for (int i = 0; i < 3; i++)
                {
                    int month = startQuarterMonth + i;
                    int year = startQuarterYear;
                    
                    if (month > 12)
                    {
                        month -= 12;
                        year = fiscalEndYear;
                    }
                    
                    if (monthsToTrack.Any(m => m.Month == month && m.Year == year))
                    {
                        return true;
                    }
                }
                
                return false;
            }
            else
            {
                return monthsToTrack.Any(m => m.Month == periodMonth && m.Year == periodYear);
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Crea trackings para el periodo actual de todos los recursos activos
        /// </summary>
        public async Task<TrackingStats> CreateTrackingsAsync()
        {
            var stopwatch = Stopwatch.StartNew();
            var currentDate = DateTime.UtcNow;
            var stats = new TrackingStats();

            _logger.LogInformation("[CREATE][START] - Iniciando creación de trackings...");

            try
            {
                var customers = await _context.Customers.ToListAsync();
                _logger.LogInformation($"[CREATE][INFO] - {customers.Count} clientes encontrados.");

                foreach (var customer in customers)
                {
                    try
                    {
                        var service = await _context.Services
                            .Where(s => s.CustomerId == customer.Id && s.FiscalYearStart <= currentDate)
                            .OrderByDescending(s => s.FiscalYearStart)
                            .FirstOrDefaultAsync();

                        if (service == null)
                        {
                            _logger.LogInformation($"[CREATE][SKIP] - No hay servicio válido para {customer.Name}");
                            continue;
                        }

                        _logger.LogInformation($"[CREATE][INFO] - Procesando cliente: {customer.Name}");
                        stats.CustomersProcessed++;

                        var fiscalInfo = GetFiscalYearInformation(service, currentDate);

                        if (fiscalInfo.CurrentFiscalMonth == null)
                        {
                            _logger.LogInformation($"[CREATE][SKIP] - Fecha actual fuera del año fiscal para {customer.Name}");
                            continue;
                        }

                        _logger.LogInformation($"[CREATE][INFO] - Mes fiscal actual: {fiscalInfo.CurrentFiscalMonth.Month}/{fiscalInfo.CurrentFiscalMonth.Year}");

                        var activeResources = await _context.Resources
                            .Include(r => r.Member)
                            .Include(r => r.Periods)
                            .Where(r => r.ServiceId == service.Id && 
                                   r.ProposalStatus != ProposalStatus.Rejected && 
                                   r.ProposalStatus != ProposalStatus.Dismissal)
                            .ToListAsync();

                        _logger.LogInformation($"[CREATE][INFO] - {activeResources.Count} recursos activos encontrados");

                        // Calcular periodo de tracking
                        int trackingMonth;
                        int trackingYear;
                        string trackingSchedule;

                        if (service.HasTrimestralEvidences)
                        {
                            if (fiscalInfo.CurrentFiscalQuarter == null)
                            {
                                _logger.LogWarning("[CREATE][WARN] - No se pudo determinar el trimestre fiscal actual");
                                continue;
                            }
                            trackingMonth = fiscalInfo.CurrentFiscalQuarter.Number;
                            trackingYear = fiscalInfo.CurrentFiscalQuarter.Start.Year;
                            trackingSchedule = $"{fiscalInfo.CurrentFiscalQuarter.Start.Month}/{fiscalInfo.CurrentFiscalQuarter.Start.Year} - {fiscalInfo.CurrentFiscalQuarter.End.Month}/{fiscalInfo.CurrentFiscalQuarter.End.Year}";
                        }
                        else
                        {
                            trackingMonth = fiscalInfo.CurrentFiscalMonth.Month;
                            trackingYear = fiscalInfo.CurrentFiscalMonth.Year;
                            trackingSchedule = $"{fiscalInfo.CurrentFiscalMonth.Month}/{fiscalInfo.CurrentFiscalMonth.Year}";
                        }

                        // Obtener trackings existentes
                        var resourceIds = activeResources.Select(r => r.Id).ToList();
                        var existingTrackings = await _context.Trackings
                            .Where(t => resourceIds.Contains(t.ResourceId) && 
                                   t.Month == trackingMonth && 
                                   t.Year == trackingYear)
                            .Select(t => new { t.ResourceId, t.ContentType })
                            .ToListAsync();

                        var trackingMap = existingTrackings
                            .GroupBy(t => t.ResourceId)
                            .ToDictionary(
                                g => g.Key,
                                g => new HashSet<ContentType>(g.Select(t => t.ContentType))
                            );

                        // Procesar cada recurso
                        foreach (var resource in activeResources)
                        {
                            stats.ResourcesProcessed++;
                            
                            var monthsToTrack = GetMonthsToResourcePeriods(resource.Periods.ToList(), service);

                            var isCurrentMonthToTrack = monthsToTrack.Any(m => 
                                m.Month == fiscalInfo.CurrentFiscalMonth.Month && 
                                m.Year == fiscalInfo.CurrentFiscalMonth.Year
                            );

                            if (!isCurrentMonthToTrack)
                            {
                                _logger.LogInformation($"[CREATE][SKIP] - {resource.Member.FullName} no tiene periodo exclusivo en el mes actual");
                                continue;
                            }

                            _logger.LogInformation($"[CREATE][INFO] - Creando trackings para {resource.Member.FullName}...");

                            var trackingContentTypes = new[] { ContentType.Evidence, ContentType.Training, ContentType.Absence };
                            var existingTypes = trackingMap.ContainsKey(resource.Id) ? trackingMap[resource.Id] : new HashSet<ContentType>();

                            var trackingsToCreate = new List<Tracking>();

                            foreach (var contentType in trackingContentTypes)
                            {
                                if (existingTypes.Contains(contentType))
                                {
                                    _logger.LogInformation($"[CREATE][SKIP] - Tracking {contentType} ya existe para {resource.Member.FullName}");
                                    stats.TrackingsSkipped++;
                                    continue;
                                }

                                trackingsToCreate.Add(new Tracking
                                {
                                    ResourceId = resource.Id,
                                    Month = trackingMonth,
                                    Year = trackingYear,
                                    ContentType = contentType,
                                    TrackingApproveStatus = TrackingStatus.Draft,
                                    Schedule = trackingSchedule
                                });
                            }

                            if (trackingsToCreate.Any())
                            {
                                await _context.Trackings.AddRangeAsync(trackingsToCreate);
                                await _context.SaveChangesAsync();
                                
                                stats.TrackingsCreated += trackingsToCreate.Count;
                                _logger.LogInformation($"[CREATE][SUCCESS] - {trackingsToCreate.Count} trackings creados para {resource.Member.FullName}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        stats.Errors++;
                        _logger.LogError(ex, $"[CREATE][ERROR] - Error procesando cliente {customer.Name}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CREATE][ERROR] - Error crítico en el proceso");
                throw;
            }
            finally
            {
                stopwatch.Stop();
                stats.DurationMs = stopwatch.ElapsedMilliseconds;
                _logger.LogInformation($"[CREATE][COMPLETE] - Proceso finalizado en {stats.DurationMs}ms", stats);
            }

            return stats;
        }

        /// <summary>
        /// Corrige los schedules de todos los trackings existentes
        /// </summary>
        public async Task<TrackingStats> FixScheduleAsync()
        {
            var stopwatch = Stopwatch.StartNew();
            var stats = new TrackingStats();

            _logger.LogInformation("[FIX][START] - Iniciando corrección de schedules de trackings...");

            try
            {
                var customers = await _context.Customers.ToListAsync();
                _logger.LogInformation($"[FIX][INFO] - {customers.Count} clientes encontrados.");

                foreach (var customer in customers)
                {
                    try
                    {
                        stats.CustomersProcessed++;
                        _logger.LogInformation($"[FIX][INFO] - Procesando cliente: {customer.Name}");

                        var services = await _context.Services
                            .Where(s => s.CustomerId == customer.Id)
                            .OrderByDescending(s => s.FiscalYearStart)
                            .ToListAsync();

                        if (!services.Any())
                        {
                            _logger.LogInformation($"[FIX][SKIP] - No hay servicios para {customer.Name}");
                            continue;
                        }

                        _logger.LogInformation($"[FIX][INFO] - {services.Count} servicios encontrados para {customer.Name}");

                        foreach (var service in services)
                        {
                            try
                            {
                                stats.ServicesProcessed++;
                                _logger.LogInformation($"[FIX][INFO] - Procesando servicio: {service.Description}");

                                var fiscalInfo = GetFiscalYearInformation(service, service.FiscalYearStart);

                                var trackings = await _context.Trackings
                                    .Include(t => t.Resource)
                                        .ThenInclude(r => r.Member)
                                    .Where(t => t.Resource.ServiceId == service.Id)
                                    .ToListAsync();

                                _logger.LogInformation($"[FIX][INFO] - {trackings.Count} trackings encontrados para el servicio");

                                if (!trackings.Any())
                                {
                                    _logger.LogInformation("[FIX][SKIP] - No hay trackings para procesar en este servicio");
                                    continue;
                                }

                                var trackingsToUpdate = new List<Tracking>();

                                foreach (var tracking in trackings)
                                {
                                    stats.TrackingsChecked++;

                                    var correctSchedule = CalculateScheduleForTracking(tracking.Month, tracking.Year, service, fiscalInfo);

                                    if (correctSchedule == null)
                                    {
                                        _logger.LogWarning($"[FIX][WARN] - No se pudo calcular schedule para tracking {tracking.Id}");
                                        stats.Errors++;
                                        continue;
                                    }

                                    if (tracking.Schedule != correctSchedule)
                                    {
                                        _logger.LogInformation($"[FIX][UPDATE] - Tracking {tracking.Id} ({tracking.Resource.Member.FullName}): \"{tracking.Schedule}\" → \"{correctSchedule}\"");
                                        tracking.Schedule = correctSchedule;
                                        trackingsToUpdate.Add(tracking);
                                    }
                                    else
                                    {
                                        stats.TrackingsSkipped++;
                                    }
                                }

                                if (trackingsToUpdate.Any())
                                {
                                    _logger.LogInformation($"[FIX][INFO] - Actualizando {trackingsToUpdate.Count} trackings...");

                                    const int batchSize = 100;
                                    for (int i = 0; i < trackingsToUpdate.Count; i += batchSize)
                                    {
                                        var batch = trackingsToUpdate.Skip(i).Take(batchSize);
                                        _context.Trackings.UpdateRange(batch);
                                        await _context.SaveChangesAsync();
                                    }

                                    stats.TrackingsFixed += trackingsToUpdate.Count;
                                    _logger.LogInformation($"[FIX][SUCCESS] - {trackingsToUpdate.Count} trackings actualizados correctamente");
                                }
                                else
                                {
                                    _logger.LogInformation("[FIX][INFO] - No hay trackings que actualizar en este servicio");
                                }
                            }
                            catch (Exception ex)
                            {
                                stats.Errors++;
                                _logger.LogError(ex, $"[FIX][ERROR] - Error procesando servicio {service.Description}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        stats.Errors++;
                        _logger.LogError(ex, $"[FIX][ERROR] - Error procesando cliente {customer.Name}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[FIX][ERROR] - Error crítico en el proceso");
                throw;
            }
            finally
            {
                stopwatch.Stop();
                stats.DurationMs = stopwatch.ElapsedMilliseconds;
                var successRate = stats.TrackingsChecked > 0 
                    ? $"{((double)stats.TrackingsFixed / stats.TrackingsChecked * 100):F2}%" 
                    : "N/A";
                _logger.LogInformation($"[FIX][COMPLETE] - Proceso finalizado en {stats.DurationMs}ms. Success Rate: {successRate}", stats);
            }

            return stats;
        }

        /// <summary>
        /// Crea trackings faltantes de forma retroactiva
        /// </summary>
        public async Task<TrackingStats> BackfillTrackingsAsync()
        {
            var stopwatch = Stopwatch.StartNew();
            var currentDate = DateTime.UtcNow;
            var stats = new TrackingStats();

            _logger.LogInformation("[BACKFILL][START] - Iniciando creación de trackings faltantes...");
            _logger.LogInformation($"[BACKFILL][INFO] - Fecha de referencia: {currentDate:O}");

            try
            {
                var customers = await _context.Customers.ToListAsync();
                _logger.LogInformation($"[BACKFILL][INFO] - {customers.Count} clientes encontrados.");

                foreach (var customer in customers)
                {
                    try
                    {
                        stats.CustomersProcessed++;
                        _logger.LogInformation($"[BACKFILL][INFO] - Procesando cliente: {customer.Name}");

                        var services = await _context.Services
                            .Where(s => s.CustomerId == customer.Id && s.FiscalYearStart <= currentDate)
                            .OrderByDescending(s => s.FiscalYearStart)
                            .ToListAsync();

                        if (!services.Any())
                        {
                            _logger.LogInformation($"[BACKFILL][SKIP] - No hay servicios válidos para {customer.Name}");
                            continue;
                        }

                        _logger.LogInformation($"[BACKFILL][INFO] - {services.Count} servicios encontrados");

                        foreach (var service in services)
                        {
                            try
                            {
                                stats.ServicesProcessed++;
                                _logger.LogInformation($"[BACKFILL][INFO] - Procesando servicio: {service.Description}");

                                var expectedPeriods = GetExpectedTrackingPeriods(service, currentDate);
                                
                                _logger.LogInformation($"[BACKFILL][INFO] - {expectedPeriods.Count} periodos esperados ({(service.HasTrimestralEvidences ? "trimestres" : "meses")}) hasta la fecha actual");

                                if (!expectedPeriods.Any())
                                {
                                    _logger.LogInformation("[BACKFILL][SKIP] - No hay periodos que procesar para este servicio");
                                    continue;
                                }

                                var activeResources = await _context.Resources
                                    .Include(r => r.Member)
                                    .Include(r => r.Periods)
                                    .Include(r => r.Trackings)
                                    .Where(r => r.ServiceId == service.Id && 
                                           r.ProposalStatus != ProposalStatus.Rejected && 
                                           r.ProposalStatus != ProposalStatus.Dismissal)
                                    .ToListAsync();

                                _logger.LogInformation($"[BACKFILL][INFO] - {activeResources.Count} recursos activos encontrados");

                                foreach (var resource in activeResources)
                                {
                                    try
                                    {
                                        stats.ResourcesProcessed++;
                                        _logger.LogInformation($"[BACKFILL][INFO] - Verificando trackings de {resource.Member.FullName}...");

                                        var existingTrackingsSet = new HashSet<string>();
                                        foreach (var t in resource.Trackings)
                                        {
                                            existingTrackingsSet.Add($"{t.Month}-{t.Year}-{t.ContentType}");
                                        }

                                        var trackingsToCreate = new List<Tracking>();

                                        foreach (var period in expectedPeriods)
                                        {
                                            stats.PeriodsChecked++;

                                            var shouldHaveTracking = IsPeriodInExclusivePeriods(
                                                period.Month,
                                                period.Year,
                                                resource.Periods.ToList(),
                                                service
                                            );

                                            if (!shouldHaveTracking)
                                            {
                                                _logger.LogInformation($"[BACKFILL][SKIP] - {resource.Member.FullName} no estaba en periodo exclusivo en {period.Schedule}");
                                                continue;
                                            }

                                            var trackingContentTypes = new[] { ContentType.Evidence, ContentType.Training, ContentType.Absence };

                                            foreach (var contentType in trackingContentTypes)
                                            {
                                                var trackingKey = $"{period.Month}-{period.Year}-{contentType}";

                                                if (existingTrackingsSet.Contains(trackingKey))
                                                {
                                                    stats.TrackingsSkipped++;
                                                    continue;
                                                }

                                                _logger.LogInformation($"[BACKFILL][MISSING] - Falta tracking {contentType} para {resource.Member.FullName} en {period.Schedule}");

                                                trackingsToCreate.Add(new Tracking
                                                {
                                                    ResourceId = resource.Id,
                                                    Month = period.Month,
                                                    Year = period.Year,
                                                    ContentType = contentType,
                                                    TrackingApproveStatus = TrackingStatus.Draft,
                                                    Schedule = period.Schedule
                                                });
                                            }
                                        }

                                        if (trackingsToCreate.Any())
                                        {
                                            _logger.LogInformation($"[BACKFILL][CREATE] - Creando {trackingsToCreate.Count} trackings para {resource.Member.FullName}...");

                                            await _context.Trackings.AddRangeAsync(trackingsToCreate);
                                            await _context.SaveChangesAsync();

                                            stats.TrackingsCreated += trackingsToCreate.Count;
                                            _logger.LogInformation($"[BACKFILL][SUCCESS] - {trackingsToCreate.Count} trackings creados para {resource.Member.FullName}");
                                        }
                                        else
                                        {
                                            _logger.LogInformation($"[BACKFILL][OK] - {resource.Member.FullName} tiene todos sus trackings completos");
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        stats.Errors++;
                                        _logger.LogError(ex, $"[BACKFILL][ERROR] - Error procesando recurso {resource.Member.FullName}");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                stats.Errors++;
                                _logger.LogError(ex, $"[BACKFILL][ERROR] - Error procesando servicio {service.Description}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        stats.Errors++;
                        _logger.LogError(ex, $"[BACKFILL][ERROR] - Error procesando cliente {customer.Name}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BACKFILL][ERROR] - Error crítico en el proceso");
                throw;
            }
            finally
            {
                stopwatch.Stop();
                stats.DurationMs = stopwatch.ElapsedMilliseconds;
                var avgPerResource = stats.ResourcesProcessed > 0 
                    ? $"{((double)stats.TrackingsCreated / stats.ResourcesProcessed):F2} trackings/recurso" 
                    : "N/A";
                _logger.LogInformation($"[BACKFILL][COMPLETE] - Proceso finalizado en {stats.DurationMs}ms. Avg: {avgPerResource}", stats);
            }

            return stats;
        }

        #endregion
    }
}