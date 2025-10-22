import { ContentType, ExclusivePeriod, PrismaClient, Service, TrackingStatus } from "./generated/prisma";

const prisma = new PrismaClient();

function getFiscalYearInformation(service: Service, currentDate: Date) {
  const startMonth = service.fiscalYearStart.getUTCMonth() + 1;
  const startYear = service.fiscalYearStart.getUTCFullYear();
  const endYear = startYear + 1;

  // Generar los 12 meses fiscales
  const fiscalMonths = [];
  for (let i = 0; i < 12; i++) {
    let month = startMonth + i;
    let year = startYear;
    if (month > 12) {
      month -= 12;
      year = endYear;
    }
    fiscalMonths.push({ month, year });
  }

  // Generar los 4 trimestres fiscales
  const fiscalQuarters = [];
  for (let q = 0; q < 4; q++) {
    let startQuarterMonth = startMonth + q * 3;
    let endQuarterMonth = startQuarterMonth + 2;
    let startQuarterYear = startYear;
    let endQuarterYear = startYear;

    if (startQuarterMonth > 12) {
      startQuarterMonth -= 12;
      startQuarterYear = endYear;
    }
    if (endQuarterMonth > 12) {
      endQuarterMonth -= 12;
      endQuarterYear = endYear;
    }

    fiscalQuarters.push({
      number: q + 1,
      start: { month: startQuarterMonth, year: startQuarterYear },
      end: { month: endQuarterMonth, year: endQuarterYear },
    });
  }

  // Determinar el mes fiscal actual
  let currentFiscalMonth = null;
  for (const fm of fiscalMonths) {
    if (
      fm.month === currentDate.getUTCMonth() + 1 &&
      fm.year === currentDate.getUTCFullYear()
    ) {
      currentFiscalMonth = fm;
      break;
    }
  }

  // Determinar el trimestre fiscal actual
  let currentFiscalQuarter = null;
  for (const fq of fiscalQuarters) {
    const startDate = new Date(Date.UTC(fq.start.year, fq.start.month - 1, 1));
    const endDate = new Date(Date.UTC(fq.end.year, fq.end.month, 0));
    if (currentDate >= startDate && currentDate <= endDate) {
      currentFiscalQuarter = fq;
      break;
    }
  }

  return {
    currentFiscalMonth,
    currentFiscalQuarter,
    fiscalMonths,
    fiscalQuarters,
  };
}

function getMonthsToResourcePeriods(periods: ExclusivePeriod[], service: Service) {
  const monthsSet = new Set<string>();
  const fiscalStartYear = service.fiscalYearStart.getUTCFullYear();
  const fiscalStartMonth = service.fiscalYearStart.getUTCMonth() + 1;
  const fiscalEndYear = fiscalStartYear + 1;
  
  // Calcular el último mes del año fiscal (mes anterior al inicio)
  const lastFiscalMonth = fiscalStartMonth === 1 ? 12 : fiscalStartMonth - 1;
  const lastFiscalYear = fiscalStartMonth === 1 ? fiscalEndYear - 1 : fiscalEndYear;

  for (const period of periods) {
    const startDate = period.startDate;
    const endDate = period.endDate || new Date(Date.UTC(lastFiscalYear, lastFiscalMonth, 0));

    let current = new Date(Date.UTC(startDate.getUTCFullYear(), startDate.getUTCMonth(), 1));

    while (current <= endDate) {
      const month = current.getUTCMonth() + 1;
      const year = current.getUTCFullYear();
      
      // Verificar si está dentro del año fiscal
      const isInFiscalYear = 
        (year === fiscalStartYear && month >= fiscalStartMonth) ||
        (year === fiscalEndYear && month < fiscalStartMonth);

      if (isInFiscalYear) {
        monthsSet.add(`${year}-${month}`);
      }

      current.setUTCMonth(current.getUTCMonth() + 1);
    }
  }

  return Array.from(monthsSet).map(key => {
    const [year, month] = key.split('-').map(Number);
    return { month, year };
  });
}

function calculateScheduleForTracking(
  tracking: { month: number; year: number },
  service: Service,
  fiscalInfo: ReturnType<typeof getFiscalYearInformation>
): string | null {
  if (service.hasTrimestralEvidences) {
    // El campo month contiene el número de trimestre (1-4)
    const quarterNumber = tracking.month;
    
    // Buscar el trimestre correspondiente
    const quarter = fiscalInfo.fiscalQuarters?.find(q => q.number === quarterNumber);
    
    if (!quarter) {
      console.log(`[FIX][WARN] - No se encontró el trimestre ${quarterNumber} para el tracking`);
      return null;
    }
    
    return `${quarter.start.month}/${quarter.start.year} - ${quarter.end.month}/${quarter.end.year}`;
  } else {
    // El campo month contiene el mes fiscal (1-12)
    return `${tracking.month}/${tracking.year}`;
  }
}

function getExpectedTrackingPeriods(
  service: Service,
  currentDate: Date
): Array<{ month: number; year: number; schedule: string }> {
  const fiscalStartYear = service.fiscalYearStart.getUTCFullYear();
  const fiscalStartMonth = service.fiscalYearStart.getUTCMonth() + 1;
  const fiscalEndYear = fiscalStartYear + 1;

  const periods: Array<{ month: number; year: number; schedule: string }> = [];

  if (service.hasTrimestralEvidences) {
    // Generar trimestres fiscales
    for (let q = 0; q < 4; q++) {
      let startQuarterMonth = fiscalStartMonth + q * 3;
      let endQuarterMonth = startQuarterMonth + 2;
      let startQuarterYear = fiscalStartYear;
      let endQuarterYear = fiscalStartYear;

      if (startQuarterMonth > 12) {
        startQuarterMonth -= 12;
        startQuarterYear = fiscalEndYear;
      }
      if (endQuarterMonth > 12) {
        endQuarterMonth -= 12;
        endQuarterYear = fiscalEndYear;
      }

      // Fecha de inicio del trimestre
      const quarterStartDate = new Date(Date.UTC(startQuarterYear, startQuarterMonth - 1, 1));
      
      // Solo incluir trimestres que ya han comenzado
      if (quarterStartDate <= currentDate) {
        periods.push({
          month: q + 1, // Número de trimestre (1-4)
          year: startQuarterYear,
          schedule: `${startQuarterMonth}/${startQuarterYear} - ${endQuarterMonth}/${endQuarterYear}`,
        });
      }
    }
  } else {
    // Generar meses fiscales
    for (let i = 0; i < 12; i++) {
      let month = fiscalStartMonth + i;
      let year = fiscalStartYear;
      
      if (month > 12) {
        month -= 12;
        year = fiscalEndYear;
      }

      // Fecha de inicio del mes
      const monthStartDate = new Date(Date.UTC(year, month - 1, 1));
      
      // Solo incluir meses que ya han comenzado
      if (monthStartDate <= currentDate) {
        periods.push({
          month: month,
          year: year,
          schedule: `${month}/${year}`,
        });
      }
    }
  }

  return periods;
}

function isPeriodInExclusivePeriods(
  periodMonth: number,
  periodYear: number,
  exclusivePeriods: ExclusivePeriod[],
  service: Service
): boolean {
  const monthsToTrack = getMonthsToResourcePeriods(exclusivePeriods, service);
  
  if (service.hasTrimestralEvidences) {
    // Para trimestres, necesitamos verificar si algún mes del trimestre está en periodo exclusivo
    const fiscalStartMonth = service.fiscalYearStart.getUTCMonth() + 1;
    const fiscalStartYear = service.fiscalYearStart.getUTCFullYear();
    const fiscalEndYear = fiscalStartYear + 1;
    
    // Calcular los meses del trimestre
    let startQuarterMonth = fiscalStartMonth + (periodMonth - 1) * 3;
    let startQuarterYear = fiscalStartYear;
    
    if (startQuarterMonth > 12) {
      startQuarterMonth -= 12;
      startQuarterYear = fiscalEndYear;
    }
    
    // Verificar si al menos un mes del trimestre está en periodo exclusivo
    for (let i = 0; i < 3; i++) {
      let month = startQuarterMonth + i;
      let year = startQuarterYear;
      
      if (month > 12) {
        month -= 12;
        year = fiscalEndYear;
      }
      
      const isInExclusive = monthsToTrack.some(
        m => m.month === month && m.year === year
      );
      
      if (isInExclusive) {
        return true;
      }
    }
    
    return false;
  } else {
    // Para meses, verificar directamente
    return monthsToTrack.some(
      m => m.month === periodMonth && m.year === periodYear
    );
  }
}

/**
 * Proceso principal para la creación automática de trackings mensuales o trimestrales para todos los clientes y sus recursos activos.
 *
 * Este proceso recorre todos los clientes, identifica el servicio fiscal vigente para la fecha actual,
 * determina los recursos activos y sus periodos exclusivos, y genera los trackings correspondientes
 * (Evidence, Training, Absence) para cada recurso en el mes o trimestre fiscal actual, evitando duplicados.
 *
 * Lógica principal:
 * - Obtiene todos los clientes.
 * - Para cada cliente, busca el servicio fiscal vigente (por fecha de inicio fiscal).
 * - Para cada recurso activo del servicio, determina los meses a trackear según sus periodos exclusivos.
 * - Si la fecha actual corresponde a un mes/trimestre a trackear, crea los trackings si no existen.
 * - Los trackings se crean en estado 'Draft' y se asocian al recurso, mes/trimestre y tipo de contenido.
 *
 * Consideraciones:
 * - Si el servicio tiene evidencias trimestrales, los trackings se agrupan por trimestre fiscal.
 * - Si el servicio no tiene evidencias trimestrales, los trackings se agrupan por mes fiscal.
 * - Se evita la creación de trackings duplicados verificando previamente su existencia.
 * - Se registran logs informativos, advertencias y errores para trazabilidad y depuración.
 *
 * @throws Error en caso de fallo en la consulta o creación de trackings en la base de datos.
 */
async function createTrackings() {
  const startTime = Date.now();
  const currentDate = new Date();
  const stats = {
    customersProcessed: 0,
    resourcesProcessed: 0,
    trackingsCreated: 0,
    trackingsSkipped: 0,
    errors: 0,
  };

  console.log("[CREATE][START] - Iniciando creación de trackings...");

  try {
    const customers = await prisma.customer.findMany();
    console.log(`[CREATE][INFO] - ${customers.length} clientes encontrados.`);

    for (const customer of customers) {
      try {
        // Buscar servicio fiscal vigente
        const service = await prisma.service.findFirst({
          where: {
            customerId: customer.id,
            fiscalYearStart: { lte: currentDate },
          },
          orderBy: { fiscalYearStart: 'desc' },
        });

        if (!service) {
          console.log(`[CREATE][SKIP] - No hay servicio válido para ${customer.name}`);
          continue;
        }

        console.log(`[CREATE][INFO] - Procesando cliente: ${customer.name}`);
        stats.customersProcessed++;

        // Calcular información fiscal
        const fiscalInfo = getFiscalYearInformation(service, currentDate);

        if (!fiscalInfo.currentFiscalMonth) {
          console.log(`[CREATE][SKIP] - Fecha actual fuera del año fiscal para ${customer.name}`);
          continue;
        }

        console.log(`[CREATE][INFO] - Mes fiscal actual: ${fiscalInfo.currentFiscalMonth.month}/${fiscalInfo.currentFiscalMonth.year}`);

        // Obtener recursos activos con sus periodos exclusivos
        const activeResources = await prisma.resource.findMany({
          where: {
            serviceId: service.id,
            proposalStatus: { notIn: ['Rejected', 'Dismissal'] },
          },
          include: {
            member: true,
            periods: true,
          },
        });

        console.log(`[CREATE][INFO] - ${activeResources.length} recursos activos encontrados`);

        // Calcular periodo de tracking una sola vez
        let trackingMonth: number;
        let trackingYear: number;
        let trackingSchedule: string;

        if (service.hasTrimestralEvidences) {
          if (!fiscalInfo.currentFiscalQuarter) {
            console.log(`[CREATE][WARN] - No se pudo determinar el trimestre fiscal actual`);
            continue;
          }
          trackingMonth = fiscalInfo.currentFiscalQuarter.number;
          trackingYear = fiscalInfo.currentFiscalQuarter.start.year;
          trackingSchedule = `${fiscalInfo.currentFiscalQuarter.start.month}/${fiscalInfo.currentFiscalQuarter.start.year} - ${fiscalInfo.currentFiscalQuarter.end.month}/${fiscalInfo.currentFiscalQuarter.end.year}`;
        } else {
          trackingMonth = fiscalInfo.currentFiscalMonth.month;
          trackingYear = fiscalInfo.currentFiscalMonth.year;
          trackingSchedule = `${fiscalInfo.currentFiscalMonth.month}/${fiscalInfo.currentFiscalMonth.year}`;
        }

        // Obtener trackings existentes en una sola query
        const existingTrackings = await prisma.tracking.findMany({
          where: {
            resourceId: { in: activeResources.map(r => r.id) },
            month: trackingMonth,
            year: trackingYear,
          },
          select: {
            resourceId: true,
            contentType: true,
          },
        });

        // Crear map para acceso O(1)
        const trackingMap = new Map<string, Set<string>>();
        existingTrackings.forEach(t => {
          if (!trackingMap.has(t.resourceId)) {
            trackingMap.set(t.resourceId, new Set());
          }
          trackingMap.get(t.resourceId)!.add(t.contentType);
        });

        // Procesar cada recurso
        for (const resource of activeResources) {
          stats.resourcesProcessed++;
          
          // Calcular meses a trackear según periodos exclusivos
          const monthsToTrack = getMonthsToResourcePeriods(resource.periods, service);

          // Verificar si el mes actual está en los periodos exclusivos
          const isCurrentMonthToTrack = monthsToTrack.some(
            (m) => m.month === fiscalInfo.currentFiscalMonth!.month && 
                   m.year === fiscalInfo.currentFiscalMonth!.year
          );

          if (!isCurrentMonthToTrack) {
            console.log(`[CREATE][SKIP] - ${resource.member.fullName} no tiene periodo exclusivo en el mes actual`);
            continue;
          }

          console.log(`[CREATE][INFO] - Creando trackings para ${resource.member.fullName}...`);

          // Determinar qué trackings crear
          const trackingContentTypes = ['Evidence', 'Training', 'Absence'] as const;
          const existingTypes = trackingMap.get(resource.id) || new Set();

          const trackingsToCreate = trackingContentTypes
            .filter(contentType => {
              if (existingTypes.has(contentType)) {
                console.log(`[CREATE][SKIP] - Tracking ${contentType} ya existe para ${resource.member.fullName}`);
                stats.trackingsSkipped++;
                return false;
              }
              return true;
            })
            .map(contentType => ({
              resourceId: resource.id,
              month: trackingMonth,
              year: trackingYear,
              contentType: contentType,
              trackingApproveStatus: TrackingStatus.Draft,
              schedule: trackingSchedule,
            }));

          // Crear trackings en batch
          if (trackingsToCreate.length > 0) {
            await prisma.tracking.createMany({
              data: trackingsToCreate,
            });
            
            stats.trackingsCreated += trackingsToCreate.length;
            console.log(`[CREATE][SUCCESS] - ${trackingsToCreate.length} trackings creados para ${resource.member.fullName}`);
          }
        }

      } catch (error) {
        stats.errors++;
        console.error(`[CREATE][ERROR] - Error procesando cliente ${customer.name}:`, error);
        // Continuar con el siguiente cliente
      }
    }

  } catch (error) {
    console.error("[CREATE][ERROR] - Error crítico en el proceso:", error);
    throw error;
  } finally {
    const duration = Date.now() - startTime;
    console.log(`[CREATE][COMPLETE] - Proceso finalizado en ${duration}ms`, {
      ...stats,
      duration: `${duration}ms`,
    });
  }
}


/**
 * Proceso para corregir los schedules de todos los trackings existentes.
 *
 * Esta función recorre todos los clientes y sus trackings, recalcula el schedule
 * correcto basándose en la configuración del servicio (hasTrimestralEvidences)
 * y actualiza aquellos trackings cuyo schedule no coincida con el esperado.
 *
 * Lógica principal:
 * - Obtiene todos los clientes y sus servicios fiscales.
 * - Para cada servicio, obtiene todos los trackings de sus recursos.
 * - Calcula el schedule correcto según si el servicio usa evidencias trimestrales o mensuales.
 * - Actualiza solo los trackings con schedules incorrectos para minimizar operaciones de BD.
 *
 * @returns Estadísticas del proceso de corrección
 */
async function fixSchedule() {
  const startTime = Date.now();
  const stats = {
    customersProcessed: 0,
    servicesProcessed: 0,
    trackingsChecked: 0,
    trackingsFixed: 0,
    trackingsSkipped: 0,
    errors: 0,
  };

  console.log("[FIX][START] - Iniciando corrección de schedules de trackings...");

  try {
    // 1. Obtener todos los clientes
    const customers = await prisma.customer.findMany();
    console.log(`[FIX][INFO] - ${customers.length} clientes encontrados.`);

    for (const customer of customers) {
      try {
        stats.customersProcessed++;
        console.log(`[FIX][INFO] - Procesando cliente: ${customer.name}`);

        // 2. Obtener todos los servicios del cliente
        const services = await prisma.service.findMany({
          where: { customerId: customer.id },
          orderBy: { fiscalYearStart: 'desc' },
        });

        if (services.length === 0) {
          console.log(`[FIX][SKIP] - No hay servicios para ${customer.name}`);
          continue;
        }

        console.log(`[FIX][INFO] - ${services.length} servicios encontrados para ${customer.name}`);

        // 3. Procesar cada servicio
        for (const service of services) {
          try {
            stats.servicesProcessed++;
            console.log(`[FIX][INFO] - Procesando servicio: ${service.description}`);

            // 4. Calcular información fiscal del servicio
            // Usamos una fecha dentro del año fiscal para obtener todos los trimestres/meses
            const fiscalStartDate = service.fiscalYearStart;
            const fiscalInfo = getFiscalYearInformation(service, fiscalStartDate);

            // 5. Obtener todos los trackings del servicio a través de sus recursos
            const trackings = await prisma.tracking.findMany({
              where: {
                resource: {
                  serviceId: service.id,
                },
              },
              include: {
                resource: {
                  include: {
                    member: true,
                  },
                },
              },
            });

            console.log(`[FIX][INFO] - ${trackings.length} trackings encontrados para el servicio`);

            if (trackings.length === 0) {
              console.log(`[FIX][SKIP] - No hay trackings para procesar en este servicio`);
              continue;
            }

            // 6. Agrupar actualizaciones para hacer batch updates
            const trackingsToUpdate: Array<{ id: string; newSchedule: string }> = [];

            for (const tracking of trackings) {
              stats.trackingsChecked++;

              // 7. Calcular el schedule correcto
              const correctSchedule = calculateScheduleForTracking(
                { month: tracking.month, year: tracking.year },
                service,
                fiscalInfo
              );

              if (!correctSchedule) {
                console.log(
                  `[FIX][WARN] - No se pudo calcular schedule para tracking ${tracking.id} ` +
                  `(${tracking.resource.member.fullName} - ${tracking.month}/${tracking.year})`
                );
                stats.errors++;
                continue;
              }

              // 8. Comparar con el schedule actual
              if (tracking.schedule !== correctSchedule) {
                console.log(
                  `[FIX][UPDATE] - Tracking ${tracking.id} (${tracking.resource.member.fullName}): ` +
                  `"${tracking.schedule}" → "${correctSchedule}"`
                );
                
                trackingsToUpdate.push({
                  id: tracking.id,
                  newSchedule: correctSchedule,
                });
              } else {
                stats.trackingsSkipped++;
              }
            }

            // 9. Realizar actualizaciones en batch
            if (trackingsToUpdate.length > 0) {
              console.log(`[FIX][INFO] - Actualizando ${trackingsToUpdate.length} trackings...`);

              // Actualizar en lotes de 100 para evitar problemas con queries muy grandes
              const BATCH_SIZE = 100;
              for (let i = 0; i < trackingsToUpdate.length; i += BATCH_SIZE) {
                const batch = trackingsToUpdate.slice(i, i + BATCH_SIZE);
                
                await prisma.$transaction(
                  batch.map(t =>
                    prisma.tracking.update({
                      where: { id: t.id },
                      data: { schedule: t.newSchedule },
                    })
                  )
                );
              }

              stats.trackingsFixed += trackingsToUpdate.length;
              console.log(`[FIX][SUCCESS] - ${trackingsToUpdate.length} trackings actualizados correctamente`);
            } else {
              console.log(`[FIX][INFO] - No hay trackings que actualizar en este servicio`);
            }

          } catch (error) {
            stats.errors++;
            console.error(`[FIX][ERROR] - Error procesando servicio ${service.description}:`, error);
            // Continuar con el siguiente servicio
          }
        }

      } catch (error) {
        stats.errors++;
        console.error(`[FIX][ERROR] - Error procesando cliente ${customer.name}:`, error);
        // Continuar con el siguiente cliente
      }
    }

  } catch (error) {
    console.error("[FIX][ERROR] - Error crítico en el proceso:", error);
    throw error;
  } finally {
    const duration = Date.now() - startTime;
    console.log(`[FIX][COMPLETE] - Proceso finalizado en ${duration}ms`, {
      ...stats,
      duration: `${duration}ms`,
      successRate: stats.trackingsChecked > 0 
        ? `${((stats.trackingsFixed / stats.trackingsChecked) * 100).toFixed(2)}%` 
        : 'N/A',
    });
  }

  return stats;
}


/**
 * Proceso para crear trackings faltantes de forma retroactiva.
 *
 * Esta función analiza todos los recursos activos y verifica que tengan
 * todos los trackings correspondientes desde el inicio del año fiscal
 * hasta la fecha actual, considerando sus periodos de exclusividad.
 *
 * Lógica principal:
 * - Calcula todos los periodos (meses/trimestres) que deberían tener tracking hasta hoy.
 * - Para cada recurso, verifica qué trackings ya existen.
 * - Identifica los periodos faltantes donde el recurso estaba en periodo exclusivo.
 * - Crea los trackings faltantes en estado 'Draft'.
 *
 * Ejemplo:
 * - Año fiscal: 01/04/2025
 * - Trackings: Trimestrales
 * - Fecha actual: 21/10/2025
 * - Trimestres esperados: Q1 (04-06/2025), Q2 (07-09/2025), Q3 (10-12/2025)
 * - Si solo existe Q3, se crearán Q1 y Q2 (si el recurso estaba en periodo exclusivo)
 *
 * @returns Estadísticas del proceso de backfill
 */
async function backfillTrackings() {
  const startTime = Date.now();
  const currentDate = new Date();
  const stats = {
    customersProcessed: 0,
    servicesProcessed: 0,
    resourcesProcessed: 0,
    periodsChecked: 0,
    trackingsCreated: 0,
    trackingsSkipped: 0,
    errors: 0,
  };

  console.log("[BACKFILL][START] - Iniciando creación de trackings faltantes...");
  console.log(`[BACKFILL][INFO] - Fecha de referencia: ${currentDate.toISOString()}`);

  try {
    // 1. Obtener todos los clientes
    const customers = await prisma.customer.findMany();
    console.log(`[BACKFILL][INFO] - ${customers.length} clientes encontrados.`);

    for (const customer of customers) {
      try {
        stats.customersProcessed++;
        console.log(`[BACKFILL][INFO] - Procesando cliente: ${customer.name}`);

        // 2. Obtener todos los servicios del cliente que hayan iniciado antes de hoy
        const services = await prisma.service.findMany({
          where: {
            customerId: customer.id,
            fiscalYearStart: { lte: currentDate },
          },
          orderBy: { fiscalYearStart: 'desc' },
        });

        if (services.length === 0) {
          console.log(`[BACKFILL][SKIP] - No hay servicios válidos para ${customer.name}`);
          continue;
        }

        console.log(`[BACKFILL][INFO] - ${services.length} servicios encontrados`);

        // 3. Procesar cada servicio
        for (const service of services) {
          try {
            stats.servicesProcessed++;
            console.log(`[BACKFILL][INFO] - Procesando servicio: ${service.description}`);

            // 4. Calcular todos los periodos esperados hasta la fecha actual
            const expectedPeriods = getExpectedTrackingPeriods(service, currentDate);
            
            console.log(
              `[BACKFILL][INFO] - ${expectedPeriods.length} periodos esperados ` +
              `(${service.hasTrimestralEvidences ? 'trimestres' : 'meses'}) hasta la fecha actual`
            );

            if (expectedPeriods.length === 0) {
              console.log(`[BACKFILL][SKIP] - No hay periodos que procesar para este servicio`);
              continue;
            }

            // 5. Obtener recursos activos con sus periodos exclusivos y trackings existentes
            const activeResources = await prisma.resource.findMany({
              where: {
                serviceId: service.id,
                proposalStatus: { notIn: ['Rejected', 'Dismissal'] },
              },
              include: {
                member: true,
                periods: true,
                trackings: {
                  select: {
                    id: true,
                    month: true,
                    year: true,
                    contentType: true,
                  },
                },
              },
            });

            console.log(`[BACKFILL][INFO] - ${activeResources.length} recursos activos encontrados`);

            // 6. Procesar cada recurso
            for (const resource of activeResources) {
              try {
                stats.resourcesProcessed++;
                console.log(`[BACKFILL][INFO] - Verificando trackings de ${resource.member.fullName}...`);

                // 7. Crear un Set de trackings existentes para búsqueda rápida
                const existingTrackingsSet = new Set<string>();
                resource.trackings.forEach(t => {
                  // Formato: "month-year-contentType"
                  existingTrackingsSet.add(`${t.month}-${t.year}-${t.contentType}`);
                });

                // 8. Para cada periodo esperado, verificar si faltan trackings
                const trackingsToCreate: Array<{
                  resourceId: string;
                  month: number;
                  year: number;
                  contentType: ContentType;
                  trackingApproveStatus: TrackingStatus;
                  schedule: string;
                }> = [];

                for (const period of expectedPeriods) {
                  stats.periodsChecked++;

                  // 9. Verificar si el recurso debería tener tracking en este periodo
                  const shouldHaveTracking = isPeriodInExclusivePeriods(
                    period.month,
                    period.year,
                    resource.periods,
                    service
                  );

                  if (!shouldHaveTracking) {
                    console.log(
                      `[BACKFILL][SKIP] - ${resource.member.fullName} no estaba en periodo exclusivo ` +
                      `en ${period.schedule}`
                    );
                    continue;
                  }

                  // 10. Verificar qué tipos de tracking faltan para este periodo
                  const trackingContentTypes = ['Evidence', 'Training', 'Absence'] as const;

                  for (const contentType of trackingContentTypes) {
                    const trackingKey = `${period.month}-${period.year}-${contentType}`;

                    if (existingTrackingsSet.has(trackingKey)) {
                      stats.trackingsSkipped++;
                      continue;
                    }

                    // 11. Agregar a la lista de trackings a crear
                    console.log(
                      `[BACKFILL][MISSING] - Falta tracking ${contentType} para ` +
                      `${resource.member.fullName} en ${period.schedule}`
                    );

                    trackingsToCreate.push({
                      resourceId: resource.id,
                      month: period.month,
                      year: period.year,
                      contentType: contentType,
                      trackingApproveStatus: TrackingStatus.Draft,
                      schedule: period.schedule,
                    });
                  }
                }

                // 12. Crear trackings faltantes en batch
                if (trackingsToCreate.length > 0) {
                  console.log(
                    `[BACKFILL][CREATE] - Creando ${trackingsToCreate.length} trackings para ` +
                    `${resource.member.fullName}...`
                  );

                  await prisma.tracking.createMany({
                    data: trackingsToCreate,
                    skipDuplicates: true, // Por seguridad, evitar duplicados
                  });

                  stats.trackingsCreated += trackingsToCreate.length;
                  console.log(
                    `[BACKFILL][SUCCESS] - ${trackingsToCreate.length} trackings creados para ` +
                    `${resource.member.fullName}`
                  );
                } else {
                  console.log(
                    `[BACKFILL][OK] - ${resource.member.fullName} tiene todos sus trackings completos`
                  );
                }

              } catch (error) {
                stats.errors++;
                console.error(
                  `[BACKFILL][ERROR] - Error procesando recurso ${resource.member.fullName}:`,
                  error
                );
                // Continuar con el siguiente recurso
              }
            }

          } catch (error) {
            stats.errors++;
            console.error(
              `[BACKFILL][ERROR] - Error procesando servicio ${service.description}:`,
              error
            );
            // Continuar con el siguiente servicio
          }
        }

      } catch (error) {
        stats.errors++;
        console.error(`[BACKFILL][ERROR] - Error procesando cliente ${customer.name}:`, error);
        // Continuar con el siguiente cliente
      }
    }

  } catch (error) {
    console.error("[BACKFILL][ERROR] - Error crítico en el proceso:", error);
    throw error;
  } finally {
    const duration = Date.now() - startTime;
    console.log(`[BACKFILL][COMPLETE] - Proceso finalizado en ${duration}ms`, {
      ...stats,
      duration: `${duration}ms`,
      averagePerResource: stats.resourcesProcessed > 0
        ? `${(stats.trackingsCreated / stats.resourcesProcessed).toFixed(2)} trackings/recurso`
        : 'N/A',
    });
  }

  return stats;
}

backfillTrackings()
  .then(async () => {
    await prisma.$disconnect();
  })
  .catch(async (e) => {
    console.error(e);
    await prisma.$disconnect();
    process.exit(1);
  });
