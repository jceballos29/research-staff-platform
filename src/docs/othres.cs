        public List<Tuple<int, int>> GetMonths(ResourceExclusivePeriod[] periods, Service service)
        {
            periods.OrderBy(p => p.Number);
            var months = new List<Tuple<int, int>>();

            int lastStartMonth = 0, lastEndMonth = 0, lastStartYear = 0;

            for (int i = 0; i < periods.Length; i++)
            {
                if (periods[i].StartDate.Date <= DateTime.Now && periods[i].StartDate.Month != lastEndMonth)
                    months.Add(new Tuple<int, int>(periods[i].StartDate.Month, periods[i].StartDate.Year));

                if (lastEndMonth == -1) // previous period do not has end date
                {
                    var iMonth = lastStartMonth + 1 == 13 ? 1 : lastStartMonth + 1;
                    var iYear = lastStartMonth + 1 == 13 ? lastStartYear + 1 : lastStartYear;
                    var sdModified = new DateTime(periods[i].StartDate.Year, periods[i].StartDate.Month, 1);

                    while (new DateTime(iYear, iMonth, 1) < sdModified && new DateTime(iYear, iMonth, 1) <= DateTime.Now)
                    {
                        months.Add(new Tuple<int, int>(iMonth, iYear));
                        iMonth++;
                        if (iMonth == 13)
                        {
                            iMonth = 1;
                            iYear++;
                        }
                    }
                }

                if (periods[i].EndDate.HasValue)
                {
                    var iMonth = periods[i].StartDate.Month + 1 == 13 ? 1 : periods[i].StartDate.Month + 1;
                    var iYear = periods[i].StartDate.Month + 1 == 13 ? periods[i].StartDate.Year + 1 : periods[i].StartDate.Year;

                    while (new DateTime(iYear, iMonth, 1) <= periods[i].EndDate.Value.Date && new DateTime(iYear, iMonth, 1) <= DateTime.Now
                        && !(iYear == service.FiscalYearStart.Year + 1 && iMonth == service.FiscalYearStart.Month))
                    {
                        months.Add(new Tuple<int, int>(iMonth, iYear));
                        iMonth++;
                        if (iMonth == 13)
                        {
                            iMonth = 1;
                            iYear++;
                        }
                    }
                }

                if (i == periods.Length - 1 && !periods[i].EndDate.HasValue) // last period without end date
                {
                    var serviceEndDate = service.FiscalYearStart.AddYears(1).AddMonths(-1);
                    var iMonth = periods[i].StartDate.Month + 1 == 13 ? 1 : periods[i].StartDate.Month + 1;
                    var iYear = periods[i].StartDate.Month + 1 == 13 ? periods[i].StartDate.Year + 1 : periods[i].StartDate.Year;

                    while (new DateTime(iYear, iMonth, 1) <= serviceEndDate && new DateTime(iYear, iMonth, 1) <= DateTime.Now)
                    {
                        months.Add(new Tuple<int, int>(iMonth, iYear));
                        iMonth++;
                        if (iMonth == 13)
                        {
                            iMonth = 1;
                            iYear++;
                        }
                    }
                }

                lastStartMonth = periods[i].StartDate.Month;
                lastStartYear = periods[i].StartDate.Year;
                lastEndMonth = periods[i].EndDate.HasValue ? periods[i].EndDate.Value.Month : -1;
            }

            return months;
        }


        public LabelData GetPeriod(ResourceExclusivePeriod[] resourcePeriods, Service service, LabelData current)
        {
            // Month, Year
            var monsthOrdered = GetMonths(resourcePeriods, service).Select(m => new LabelData()
            {
                Number = m.Item1,
                Year = m.Item2,
                Label = $"{m.Item1}/{m.Item2}"
            }).OrderBy(m => m.Year).ThenBy(m => m.Number);
            var groups = new List<(int period, List<LabelData> months)>();

            var period = 0;
            var monthsInCurrentPeriodCount = 0;
            var previousMonth = 0;

            foreach (var month in monsthOrdered)
            {
                var isNextMonth = (previousMonth == 12 && month.Number == 1) ? true : previousMonth + 1 == month.Number;
                if (period == 0 || (previousMonth != 0 && !isNextMonth))
                {
                    period++;
                    groups.Add((period, new List<LabelData> { month }));
                    monthsInCurrentPeriodCount = 1;
                    previousMonth = month.Number;
                }
                else if (monthsInCurrentPeriodCount == 2)
                {
                    var group = groups.Find(g => g.period == period);
                    group.months.Add(month);
                    period++;
                    monthsInCurrentPeriodCount = 0;
                    previousMonth = 0;
                }
                else
                {
                    var group = groups.Find(g => g.period == period);
                    if (monthsInCurrentPeriodCount == 0) groups.Add((period, new List<LabelData> { month }));
                    else group.months.Add(month);
                    previousMonth = month.Number;
                    monthsInCurrentPeriodCount++;
                }
            }

            var currentGroup = groups.FirstOrDefault(g => g.months.Any(m => m.Number == current.Number && m.Year == current.Year));
            var currentPeriod = new LabelData()
            {
                Number = currentGroup.period,
                Year = currentGroup.months.First().Year,
                Label = $"{currentGroup.months.First().Label} - {currentGroup.months.Last().Label}"
            };

            return currentPeriod;
        }
