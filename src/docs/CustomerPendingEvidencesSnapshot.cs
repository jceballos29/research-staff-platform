using Bonis.Data;
using Bonis.Enums;
using Hangfire;
using Microsoft.EntityFrameworkCore;

namespace Bonis.Jobs
{
  public class CustomerPendingEvidencesSnapshot
  {
    private readonly ApplicationDbContext _context;

    public CustomerPendingEvidencesSnapshot(ApplicationDbContext context)
    {
      _context = context;
    }

    [AutomaticRetry(Attempts = 0)]
    public async Task Run()
    {
      // Proyección agregada por cliente con subconsultas para servicios actuales y anteriores
      var summaries = await _context.Customers
          .Select(c => new
          {
            c.CustomerId,
            c.CustomerName,
            c.CIF,
            c.CrmAccountId,
            LatestServiceId = _context.Services
                  .Where(s => s.CustomerId == c.CustomerId)
                  .OrderByDescending(s => s.FiscalYearStart)
                  .Select(s => s.ServiceId)
                  .FirstOrDefault(),
            PrevServiceId = _context.Services
                  .Where(s => s.CustomerId == c.CustomerId)
                  .OrderByDescending(s => s.FiscalYearStart)
                  .Select(s => s.ServiceId)
                  .Skip(1)
                  .FirstOrDefault(),
          })
          .Select(x => new
          {
            x.CustomerId,
            x.CustomerName,
            x.CIF,
            x.CrmAccountId,
            PendingCurrent = _context.Trackings
                  .Where(t => t.ContentType == ContentType.Evidence
                              && t.TrackingApproveStatus == TrackingStatus.Sended
                              && t.Resource != null
                              && t.Resource.ServiceId == x.LatestServiceId)
                  .Count(),
            OldestCurrent = _context.Documents
                  .Where(d => d.ContentType == ContentType.Evidence
                              && d.Tracking != null
                              && d.Tracking.TrackingApproveStatus == TrackingStatus.Sended
                              && d.Tracking.Resource != null
                              && d.Tracking.Resource.ServiceId == x.LatestServiceId)
                  .Select(d => (DateTime?)d.UploadDate)
                  .Min(),
            PendingPrev = _context.Trackings
                  .Where(t => t.ContentType == ContentType.Evidence
                              && t.TrackingApproveStatus == TrackingStatus.Sended
                              && t.Resource != null
                              && t.Resource.ServiceId == x.PrevServiceId)
                  .Count(),
            OldestPrev = _context.Documents
                  .Where(d => d.ContentType == ContentType.Evidence
                              && d.Tracking != null
                              && d.Tracking.TrackingApproveStatus == TrackingStatus.Sended
                              && d.Tracking.Resource != null
                              && d.Tracking.Resource.ServiceId == x.PrevServiceId)
                  .Select(d => (DateTime?)d.UploadDate)
                  .Min(),
          })
          .ToListAsync();

      // Upsert por cliente y día
      foreach (var s in summaries)
      {
        var existing = await _context.CustomerPendingEvidenceSummaries
            .FirstOrDefaultAsync(e => e.CustomerId == s.CustomerId);

        if (existing == null)
        {
          existing = new CustomerPendingEvidenceSummary
          {
            Id = Guid.NewGuid(),
            CustomerId = s.CustomerId,
          };
          _context.CustomerPendingEvidenceSummaries.Add(existing);
        }

        existing.CrmAccountId = s.CrmAccountId;
        existing.CustomerName = s.CustomerName;
        existing.CIF = s.CIF;
        existing.PendingEvidencesYearCurrent = s.PendingCurrent;
        existing.OldestPendingEvidenceUploadDateYearCurrent = s.OldestCurrent;
        existing.PendingEvidencesYearBack = s.PendingPrev;
        existing.OldestPendingEvidenceUploadDateYearBack = s.OldestPrev;
      }

      await _context.SaveChangesAsync();
    }
  }
}
