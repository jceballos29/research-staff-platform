using Bonis.Data;
using Bonis.Jobs;
using Bonis.Models.OneDataResponse;

namespace Bonis.Services
{
    public interface IOneDataSyncServiceWorker
    {
        IAsyncEnumerable<AccountOneDataResponse> GetAccountsChangeSetEnumerableAsync();
        IAsyncEnumerable<DocumentOneDataResponse> GetDocumentsChangeSetEnumerableAsync();
        IAsyncEnumerable<MissionsOneDataResponse> GetMissionsChangeSetEnumerableAsync();
        IAsyncEnumerable<MissionMembersOneDataResponse> GetMissionMembersChangeSetEnumerableAsync();
    }

    public class OneDataSyncServiceWorker : IOneDataSyncServiceWorker
    {
        private readonly IOneDataIntegrationsClient _oneDataService;
        private readonly ApplicationDbContext _context;
        private readonly ILogger<OneDataSyncServiceWorker> _logger;

        public OneDataSyncServiceWorker(IOneDataIntegrationsClient oneDataIntegrationsClient, ApplicationDbContext context,
            ILogger<OneDataSyncServiceWorker> logger)
        {
            _context = context;
            _oneDataService = oneDataIntegrationsClient;
            _logger = logger;
        }

        public async IAsyncEnumerable<AccountOneDataResponse> GetAccountsChangeSetEnumerableAsync()
        {
            var lastConfig = _context.OneDataSyncSettings.FirstOrDefault(t => t.Code == OneDataDatasets.Accounts);
            string token = null;
            if (lastConfig != null)
            {
                token = lastConfig.ContinuationToken;
            }
            while (true)
            {
                var result = await _oneDataService.GetAccountNum(token);
                if (result.ContinuationToken == token) yield break;
                token = result.ContinuationToken;
                yield return result;
            }
        }

        public async IAsyncEnumerable<DocumentOneDataResponse> GetDocumentsChangeSetEnumerableAsync()
        {
            var lastConfig = _context.OneDataSyncSettings.FirstOrDefault(t => t.Code == OneDataDatasets.Documents);
            string token = null;
            if (lastConfig != null)
            {
                token = lastConfig.ContinuationToken;
            }
            while (true)
            {
                var result = await _oneDataService.GetDocumentAsync(token);
                if (result.ContinuationToken == token) yield break;
                token = result.ContinuationToken;
                yield return result;
            }
        }

        public async IAsyncEnumerable<MissionsOneDataResponse> GetMissionsChangeSetEnumerableAsync()
        {
            var lastConfig = _context.OneDataSyncSettings.FirstOrDefault(t => t.Code == OneDataDatasets.Missions);
            string token = null;
            if (lastConfig != null)
            {
                token = lastConfig.ContinuationToken;
            }
            while (true)
            {
                var result = await _oneDataService.GetMissionsAsync(token);
                if (result.ContinuationToken == token) yield break;
                token = result.ContinuationToken;
                yield return result;
            }
        }

        public async IAsyncEnumerable<MissionMembersOneDataResponse> GetMissionMembersChangeSetEnumerableAsync()
        {
            var lastConfig = _context.OneDataSyncSettings.FirstOrDefault(t => t.Code == OneDataDatasets.MissionMembers);
            string token = null;
            if (lastConfig != null)
            {
                token = lastConfig.ContinuationToken;
            }
            while (true)
            {
                var result = await _oneDataService.GetMissionMembersAsync(token);
                if (result.ContinuationToken == token) yield break;
                token = result.ContinuationToken;
                yield return result;
            }
        }
    }
}
