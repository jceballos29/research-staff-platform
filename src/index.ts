// Tipos auxiliares para periodos y etiquetas
export type ResourceExclusivePeriod = {
  StartDate: Date;
  EndDate?: Date;
  Number: number;
};

export type LabelData = {
  Number: number;
  Year: number;
  Label: string;
};

// getMonths debe estar definida antes de getPeriod
function getMonths(periods: ResourceExclusivePeriod[], service: Service): Array<[number, number]> {
  periods = periods.slice().sort((a, b) => a.Number - b.Number);
  const months: Array<[number, number]> = [];
  let lastStartMonth = 0, lastEndMonth = 0, lastStartYear = 0;
  for (let i = 0; i < periods.length; i++) {
    const period = periods[i];
    if (!period) continue;
    const start = period.StartDate;
    const end = period.EndDate;
    if (start && start <= new Date() && start.getMonth() + 1 !== lastEndMonth) {
      months.push([start.getMonth() + 1, start.getFullYear()]);
    }
    if (lastEndMonth === -1 && start) {
      let iMonth = lastStartMonth + 1 === 13 ? 1 : lastStartMonth + 1;
      let iYear = lastStartMonth + 1 === 13 ? lastStartYear + 1 : lastStartYear;
      const sdModified = new Date(start.getFullYear(), start.getMonth(), 1);
      while (new Date(iYear, iMonth - 1, 1) < sdModified && new Date(iYear, iMonth - 1, 1) <= new Date()) {
        months.push([iMonth, iYear]);
        iMonth++;
        if (iMonth === 13) {
          iMonth = 1;
          iYear++;
        }
      }
    }
    if (end && start) {
      let iMonth = start.getMonth() + 1 === 13 ? 1 : start.getMonth() + 2;
      let iYear = start.getMonth() + 1 === 13 ? start.getFullYear() + 1 : start.getFullYear();
      while (
        new Date(iYear, iMonth - 1, 1) <= end &&
        new Date(iYear, iMonth - 1, 1) <= new Date() &&
        !(iYear === service.fiscalYearStart.getFullYear() + 1 && iMonth === service.fiscalYearStart.getMonth() + 1)
      ) {
        months.push([iMonth, iYear]);
        iMonth++;
        if (iMonth === 13) {
          iMonth = 1;
          iYear++;
        }
      }
    }
    if (i === periods.length - 1 && !end && start) {
      const serviceEndDate = new Date(service.fiscalYearStart);
      serviceEndDate.setFullYear(serviceEndDate.getFullYear() + 1);
      serviceEndDate.setMonth(serviceEndDate.getMonth() - 1);
      let iMonth = start.getMonth() + 1 === 13 ? 1 : start.getMonth() + 2;
      let iYear = start.getMonth() + 1 === 13 ? start.getFullYear() + 1 : start.getFullYear();
      while (new Date(iYear, iMonth - 1, 1) <= serviceEndDate && new Date(iYear, iMonth - 1, 1) <= new Date()) {
        months.push([iMonth, iYear]);
        iMonth++;
        if (iMonth === 13) {
          iMonth = 1;
          iYear++;
        }
      }
    }
    lastStartMonth = start ? start.getMonth() + 1 : 0;
    lastStartYear = start ? start.getFullYear() : 0;
    lastEndMonth = end ? end.getMonth() + 1 : -1;
  }
  return months;
}
// Traducción de GetPeriod desde C#
function getPeriod(resourcePeriods: ResourceExclusivePeriod[], service: Service, current: LabelData): LabelData {
  // Obtener y ordenar los meses
  const monthsOrdered: LabelData[] = getMonths(resourcePeriods, service)
    .map(([month, year]: [number, number]) => ({
      Number: month,
      Year: year,
      Label: `${month}/${year}`
    }))
    .sort((a: LabelData, b: LabelData) => a.Year - b.Year || a.Number - b.Number);

  type Group = { period: number, months: LabelData[] };
  const groups: Group[] = [];
  let period = 0;
  let monthsInCurrentPeriodCount = 0;
  let previousMonth = 0;

  for (const month of monthsOrdered) {
    const isNextMonth = (previousMonth === 12 && month.Number === 1) ? true : previousMonth + 1 === month.Number;
    if (period === 0 || (previousMonth !== 0 && !isNextMonth)) {
      period++;
      groups.push({ period, months: [month] });
      monthsInCurrentPeriodCount = 1;
      previousMonth = month.Number;
    } else if (monthsInCurrentPeriodCount === 2) {
      const group = groups.find((g: Group) => g.period === period);
      if (group) group.months.push(month);
      period++;
      monthsInCurrentPeriodCount = 0;
      previousMonth = 0;
    } else {
      const group = groups.find((g: Group) => g.period === period);
      if (monthsInCurrentPeriodCount === 0) groups.push({ period, months: [month] });
      else if (group) group.months.push(month);
      previousMonth = month.Number;
      monthsInCurrentPeriodCount++;
    }
  }

  const currentGroup = groups.find((g: Group) => g.months.some((m: LabelData) => m.Number === current.Number && m.Year === current.Year));
  if (!currentGroup || !currentGroup.months.length) {
    return { Number: 0, Year: 0, Label: "" };
  }
  const firstMonth = currentGroup.months[0];
  const lastMonth = currentGroup.months[currentGroup.months.length - 1];
  if (!firstMonth || !lastMonth) {
    return { Number: currentGroup.period, Year: 0, Label: "" };
  }
  const currentPeriod: LabelData = {
    Number: currentGroup.period,
    Year: firstMonth.Year,
    Label: `${firstMonth.Label} - ${lastMonth.Label}`
  };
  return currentPeriod;
}
import { ContentType } from "./type";
import { customers, resources, services, trackings } from './database';
import { FiscalInfo, ProposalStatus, Resource, Service, Month, Period, Tracking } from './type';

const getFiscalYearMonths = (fiscalYearStart: Date): Month[] => {
  const months: Month[] = [];
  let year = fiscalYearStart.getFullYear();
  let month = fiscalYearStart.getMonth() + 1; // JS: 0=enero, 1=Febrero...
  for (let i = 0; i < 12; i++) {
    months.push({
      month,
      year,
      label: `${month}/${year}`
    });
    month++;
    if (month > 12) {
      month = 1;
      year++;
    }
  }
  return months;
};

const splitFiscalMonthsIntoPeriods = (months: Month[]): Period[] => {
  const periods: Period[] = [];
  for (let i = 0; i < months.length; i += 3) {
    periods.push({
      number: (i / 3) + 1,
      months: months.slice(i, i + 3)
    });
  }
  return periods;
};

const getLastServiceForCustomer: (customerId: string, currentDate: Date) => Service | null = (customerId, currentDate) => {
  const customerServices = services
    .filter((service: Service) =>
      service.customerId === customerId &&
      service.fiscalYearStart <= currentDate &&
      currentDate <= new Date(service.fiscalYearStart.getTime() + 365 * 24 * 60 * 60 * 1000)
    )
    .sort((a, b) => b.fiscalYearStart.getTime() - a.fiscalYearStart.getTime());
  return customerServices[0] || null;
};

const getActiveResourcesForService: (serviceId: string) => Resource[] = (serviceId) => {
  return resources.filter(resource =>
    resource.serviceId === serviceId &&
    resource.proposalStatus !== ProposalStatus.Rejected &&
    resource.proposalStatus !== ProposalStatus.Dismissal
  );
};

const getTrackingsForResourceAndService: (resourceId: string, serviceId: string) => Tracking[] = (resourceId, serviceId) => {
  return trackings
    .filter(tracking =>
      tracking.resourceId === resourceId &&
      tracking.serviceId === serviceId
    )
    .sort((a, b) => {
      if (a.year !== b.year) return a.year - b.year;
      return a.month - b.month;
    });
}

const getCurrentFiscalInfo: (fiscalYearStart: Date, currentDate: Date) => FiscalInfo = (fiscalYearStart, currentDate) => {
  const months = getFiscalYearMonths(fiscalYearStart);
  const periods = splitFiscalMonthsIntoPeriods(months);

  // Buscar el mes actual en el año fiscal
  let currentMonth = months.find(m => m.month === (currentDate.getMonth() + 1) && m.year === currentDate.getFullYear());

  // Buscar el periodo actual
  let currentPeriod = periods.find(p => p.months.some(m => m.month === currentMonth?.month && m.year === currentMonth?.year));

  const fiscalYearEnd = new Date(fiscalYearStart.getTime());
  fiscalYearEnd.setFullYear(fiscalYearEnd.getFullYear() + 1);
  fiscalYearEnd.setDate(fiscalYearEnd.getDate() - 1);

  return {
    start: fiscalYearStart,
    end: fiscalYearEnd,
    periods,
    currentMonth,
    currentPeriod
  };
};

function main() {
  const currentDate = new Date();
  console.log("[JOB][START]", { JobName: "Tracking Creation", StartTime: currentDate.toISOString() });

  let customersProcessed = 0;
  let customersSkipped = 0;
  let servicesProcessed = 0;
  let resourcesProcessed = 0;
  let resourcesSkipped = 0;
  let trackingsCreated = 0;
  let trackingsSkipped = 0;
  let historicalPeriodsProcessed = 0;
  let historicalTrackingsCreated = 0;
  let skipReasons = new Map<string, number>();
  let createdByContentType = new Map<string, number>();

  try {
    customers.forEach(customer => {
      console.log("[CUSTOMER][BEGIN]", { CustomerName: customer.name, CustomerId: customer.id });
      const lastService = getLastServiceForCustomer(customer.id, currentDate);
      if (!lastService) {
        const reason = "NoActiveService";
        console.log("[CUSTOMER][SKIP]", { CustomerName: customer.name, CustomerId: customer.id, Reason: reason });
        skipReasons.set(reason, (skipReasons.get(reason) || 0) + 1);
        customersSkipped++;
        return;
      }
      customersProcessed++;
      servicesProcessed++;

      console.log("[SERVICE][SELECTED]", { CustomerName: customer.name, ServiceDesc: lastService.description, ServiceId: lastService.id, FiscalStart: lastService.fiscalYearStart.toISOString().split("T")[0], HasTrimestre: lastService.hasTrimestreEvidence });

      const activeResources = getActiveResourcesForService(lastService.id);
      console.log("[SERVICE][RESOURCES]", { CustomerName: customer.name, ServiceDesc: lastService.description, ServiceId: lastService.id, ActiveResourcesCount: activeResources.length });

      const fiscalInfo = getCurrentFiscalInfo(lastService.fiscalYearStart, currentDate);
      console.log("[FISCAL_PERIOD][INFO]", { startDate: fiscalInfo.start.toISOString().split("T")[0], endDate: fiscalInfo.end.toISOString().split("T")[0], currentMonth: fiscalInfo.currentMonth, currentPeriod: fiscalInfo.currentPeriod?.number });

      activeResources.forEach(resource => {
        console.log("[RESOURCE][PROCESS]", { ResourceId: resource.id });
        resourcesProcessed++;

        // Ejemplo de integración: obtener meses y período actual del recurso
        // Suponiendo que resource.periods existe y es un array de ResourceExclusivePeriod
        if (Array.isArray(resource.periods) && resource.periods.length > 0) {
          const meses = getMonths(resource.periods, lastService);
          console.log("[RESOURCE][MONTHS]", { ResourceId: resource.id, Meses: meses });

          // Tomar el mes actual fiscal para el cálculo del período
          const fiscalMonth = fiscalInfo.currentMonth;
          if (fiscalMonth) {
            const currentLabel: LabelData = {
              Number: fiscalMonth.month,
              Year: fiscalMonth.year,
              Label: fiscalMonth.label
            };
            const periodoActual = getPeriod(resource.periods, lastService, currentLabel);
            console.log("[RESOURCE][PERIOD]", { ResourceId: resource.id, PeriodoActual: periodoActual });
          }
        }

        // ...continúa la lógica original de creación de trackings...
        // ...existing code...
        console.log("[RESOURCE][FINISH]", { ResourceId: resource.id });
      });
      console.log("[CUSTOMER][FINISH]", { CustomerName: customer.name, CustomerId: customer.id });
    });

    console.log("[JOB][FINISH]", {
      customersTotal: customersProcessed + customersSkipped,
      customersSkipped,
      servicesProcessed,
      resourcesTotal: resourcesProcessed + resourcesSkipped,
      resourcesProcessed,
      resourcesSkipped,
      trackingsCreated,
      trackingsSkipped,
      historicalPeriodsProcessed,
      historicalTrackingsCreated,
      skipReasons: Object.fromEntries(skipReasons),
      createdByContentType: Object.fromEntries(createdByContentType)
    });
  } catch (error) {
    console.error(`[JOB][ERROR] An error occurred during Tracking Creation: ${error}`);
  }
}

main();