        [AutomaticRetry(Attempts = 0)]
        public async Task Run()
        {
            var jobRunId = JobLogging.NewRunId();
            using var jobScope = JobLogging.BeginJobScope(_logger, jobRunId));

            JobLogging.Info(_logger, "[SNAPSHOT][START]", "Starting Customer Pending Evidences Snapshot job.");
            var swTotal = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var totalCustomers = await _context.Customers.CountAsync();
                JobLogging.Info(_logger, "[SNAPSHOT]", "Total customers to process: {TotalCustomers}", totalCustomers);
                for (int skip = 0; skip < totalCustomers; skip += BatchSize)
                {
                    var swBatch = System.Diagnostics.Stopwatch.StartNew();

                    // Obtener lote de clientes
                    var customersBatch = await _context.Customers
                        .AsNoTracking()
                        .OrderBy(c => c.CustomerId)
                        .Skip(skip)
                        .Take(BatchSize)
                        .Select(c => new { c.CustomerId, c.CustomerName, c.CIF, c.CrmAccountId })
                        .ToListAsync();

                    if (customersBatch.Count == 0) break;

                    var customerIds = customersBatch.Select(c => c.CustomerId).ToArray();

                    JobLogging.Info(_logger, "[SNAPSHOT]", "Processing batch starting at {Skip}, size {BatchSize}", skip, customersBatch.Count);

                    // Ontener servicios más recientes y anteriores por cliente
                    var servicesByCustomer = await _context.Services
                        .Where(s => customerIds.Contains(s.CustomerId))
                        .GroupBy(s => s.CustomerId)
                        .Select(g => new
                        {
                            CustomerId = g.Key,
                            LatestServiceId = g.OrderByDescending(s => s.FiscalYearStart).Select(s => s.ServiceId).FirstOrDefault(),
                            PrevServiceId = g.OrderByDescending(s => s.FiscalYearStart).Select(s => s.ServiceId).Skip(1).FirstOrDefault()
                        })
                        .ToDictionaryAsync(x => x.CustomerId);

                    JobLogging.Info(_logger, "[SNAPSHOT]", "Fetched service info for {Count} customers in batch.", servicesByCustomer.Count);

                    // Obtener resúmenes de evidencias pendientes por cliente
                    var summaries = new List<CustomerPendingEvidenceSummary>();

                    foreach (var customer in customersBatch)
                    {
                        servicesByCustomer.TryGetValue(customer.CustomerId, out var serviceInfo);
                        int pendingCurrent = 0;
                        DateTime? oldestCurrent = null;
                        int pendingPrev = 0;
                        DateTime? oldestPrev = null;
                        if (serviceInfo != null)
                        {
                            pendingCurrent = await _context.Trackings
                                .Where(t => t.ContentType == ContentType.Evidence
                                            && t.TrackingApproveStatus == TrackingStatus.Sended
                                            && t.Resource != null
                                            && t.Resource.ServiceId == serviceInfo.LatestServiceId)
                                .CountAsync();
                            oldestCurrent = await _context.Documents
                                .Where(d => d.ContentType == ContentType.Evidence
                                            && d.Tracking != null
                                            && d.Tracking.TrackingApproveStatus == TrackingStatus.Sended
                                            && d.Tracking.Resource != null
                                            && d.Tracking.Resource.ServiceId == serviceInfo.LatestServiceId)
                                .Select(d => (DateTime?)d.UploadDate)
                                .MinAsync();
                            pendingPrev = await _context.Trackings
                                .Where(t => t.ContentType == ContentType.Evidence
                                            && t.TrackingApproveStatus == TrackingStatus.Sended
                                            && t.Resource != null
                                            && t.Resource.ServiceId == serviceInfo.PrevServiceId)
                                .CountAsync();
                            oldestPrev = await _context.Documents
                                .Where(d => d.ContentType == ContentType.Evidence
                                            && d.Tracking != null
                                            && d.Tracking.TrackingApproveStatus == TrackingStatus.Sended
                                            && d.Tracking.Resource != null
                                            && d.Tracking.Resource.ServiceId == serviceInfo.PrevServiceId)
                                .Select(d => (DateTime?)d.UploadDate)
                                .MinAsync();
                        }
                        summaries.Add(new CustomerPendingEvidenceSummary
                        {
                            Id = Guid.NewGuid(),
                            CustomerId = customer.CustomerId,
                            CrmAccountId = customer.CrmAccountId,
                            CustomerName = customer.CustomerName,
                            CIF = customer.CIF,
                            PendingEvidencesYearCurrent = pendingCurrent,
                            OldestPendingEvidenceUploadDateYearCurrent = oldestCurrent,
                            PendingEvidencesYearBack = pendingPrev,
                            OldestPendingEvidenceUploadDateYearBack = oldestPrev
                        });
                    }

                    JobLogging.Info(_logger, "[SNAPSHOT]", "Computed summaries for {Count} customers in batch.", summaries.Count);

                    // Upsert de resúmenes
                    foreach (var summary in summaries)
                    {
                        var existing = await _context.CustomerPendingEvidenceSummaries
                            .FirstOrDefaultAsync(e => e.CustomerId == summary.CustomerId);
                        JobLogging.Info(_logger, "[SNAPSHOT]", "Upserting summary for CustomerId {CustomerId}.", summary.CustomerId);
                        if (existing == null)
                        {
                            JobLogging.Info(_logger, "[SNAPSHOT]", "Inserting new summary for CustomerId {CustomerId}.", summary.CustomerId);
                            _context.CustomerPendingEvidenceSummaries.Add(summary);
                        }
                        else
                        {
                            JobLogging.Info(_logger, "[SNAPSHOT]", "Updating existing summary for CustomerId {CustomerId}.", summary.CustomerId);
                            existing.CrmAccountId = summary.CrmAccountId;
                            existing.CustomerName = summary.CustomerName;
                            existing.CIF = summary.CIF;
                            existing.PendingEvidencesYearCurrent = summary.PendingEvidencesYearCurrent;
                            existing.OldestPendingEvidenceUploadDateYearCurrent = summary.OldestPendingEvidenceUploadDateYearCurrent;
                            existing.PendingEvidencesYearBack = summary.PendingEvidencesYearBack;
                            existing.OldestPendingEvidenceUploadDateYearBack = summary.OldestPendingEvidenceUploadDateYearBack;
                        }
                    }

                    await _context.SaveChangesAsync();

                    swBatch.Stop();

                    JobLogging.Info(_logger, "[SNAPSHOT]", "Finished processing batch starting at {Skip} in {ElapsedMilliseconds} ms.", skip, swBatch.ElapsedMilliseconds);
                }

                JobLogging.Info(_logger, "[SNAPSHOT]", "All batches processed successfully.");
            }
            catch (Exception ex) { }
            finally
            {
                swTotal.Stop();
                JobLogging.Info(_logger, "[SNAPSHOT][END]", "Finished Customer Pending Evidences Snapshot job in {ElapsedMilliseconds} ms.", swTotal.ElapsedMilliseconds);
            }
        }


