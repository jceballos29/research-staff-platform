using Bonis.Data;

namespace Bonis.Jobs
{
    public class OneDataSyncClearTokens
    {
        private readonly ApplicationDbContext _context;
        public OneDataSyncClearTokens(ApplicationDbContext context)
        {
            _context = context;
        }

        public void Run()
        {
            var listToClear = _context.OneDataSyncSettings.ToList();
            _context.OneDataSyncSettings.RemoveRange(listToClear);
            _context.SaveChanges();
        }
    }
}
