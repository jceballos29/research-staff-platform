using Bonis.Models.OneDataResponse;
using Newtonsoft.Json;

namespace Bonis.Services
{
    public interface IOneDataIntegrationsClient
    {
        Task<AccountOneDataResponse> GetAccountNum(string continuationToken = null);

        Task<DocumentOneDataResponse> GetDocumentAsync(string continuationToken = null);
        Task<MissionsOneDataResponse> GetMissionsAsync(string continuationToken = null);
        Task<MissionMembersOneDataResponse> GetMissionMembersAsync(string continuationToken = null);
    }

    public class OneDataIntegrationsClient : IOneDataIntegrationsClient
    {
        public HttpClient Client { get; }
        private readonly ILogger<OneDataIntegrationsClient> _logger;

        public OneDataIntegrationsClient(HttpClient client, ILogger<OneDataIntegrationsClient> logger)
        {
            Client = client;
            // Ajuste temporal: deshabilitar timeout de HttpClient para evitar cancelaciones a los 100s
            Client.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
            _logger = logger;
        }

        public async Task<AccountOneDataResponse> GetAccountNum(string continuationToken = null)
        {
            var url = "accounts/feed";
            if (!string.IsNullOrEmpty(continuationToken))
                url += $"?from={continuationToken}";
            _logger.LogInformation("[HTTP][GET] {Url}", url);
            _logger.LogInformation("[HTTP][GET] Getting accounts from OneData with continuation token: {Token}", continuationToken);
            //Show Headers for debug
            _logger.LogInformation("[HTTP][GET] Request Headers: {Headers}", Client.DefaultRequestHeaders.ToString());
            //Show Auth Header for debug
            _logger.LogInformation("[HTTP][GET] Authorization Header: {AuthHeader}", Client.DefaultRequestHeaders.Authorization?.ToString());

            HttpResponseMessage response = await Client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError($"Non success status code. Status code: {response.StatusCode}");
            }
            string responseJson = await response.Content.ReadAsStringAsync();
            var obj = JsonConvert.DeserializeObject<OneDataResponse>(responseJson);
            return new AccountOneDataResponse(obj.ContinuationToken, obj.Rows, obj.Columns, _logger);
        }

        public async Task<DocumentOneDataResponse> GetDocumentAsync(string continuationToken = null)
        {
            var url = "documents/feed";
            if (!string.IsNullOrEmpty(continuationToken))
            {
                var encoded = Uri.EscapeDataString(continuationToken);
                url += $"?from={encoded}";
            }
            // Log completo de la URL (con base si existe) para monitorear el continuationToken y la ruta final
            var absolute = Client?.BaseAddress != null ? new Uri(Client.BaseAddress, url).ToString() : url;
            _logger.LogInformation("[HTTP][GET] {Url}", absolute);
            HttpResponseMessage response = await Client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                // Evitar posteriormente intentar parsear HTML como JSON
                string errorBody = await response.Content.ReadAsStringAsync();
                var bodyPreview = errorBody == null ? "<no-body>" : (errorBody.Length <= 500 ? errorBody : errorBody.Substring(0, 500) + $"... (+{errorBody.Length - 500} chars)");
                _logger.LogError("Non success status code. Status code: {Status}. BodyPreview: {Body}", response.StatusCode, bodyPreview);
                throw new HttpRequestException($"GET {url} failed: {(int)response.StatusCode} {response.StatusCode}");
            }
            string responseJson = await response.Content.ReadAsStringAsync();
            var obj = JsonConvert.DeserializeObject<OneDataResponse>(responseJson);
            return new DocumentOneDataResponse(obj.ContinuationToken, obj.Rows, obj.Columns, _logger);
        }

        public async Task<MissionsOneDataResponse> GetMissionsAsync(string continuationToken = null)
        {
            var url = "missions/feed";
            if (!string.IsNullOrEmpty(continuationToken))
                url += $"?from={continuationToken}";
            HttpResponseMessage response = await Client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError($"Non success status code. Status code: {response.StatusCode}");
            }
            string responseJson = await response.Content.ReadAsStringAsync();
            var obj = JsonConvert.DeserializeObject<OneDataResponse>(responseJson);
            return new MissionsOneDataResponse(obj.ContinuationToken, obj.Rows, obj.Columns, _logger);
        }

        public async Task<MissionMembersOneDataResponse> GetMissionMembersAsync(string continuationToken = null)
        {
            var url = "mission_members/feed";
            if (!string.IsNullOrEmpty(continuationToken))
                url += $"?from={continuationToken}";
            HttpResponseMessage response = await Client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError($"Non success status code. Status code: {response.StatusCode}");
            }
            string responseJson = await response.Content.ReadAsStringAsync();
            var obj = JsonConvert.DeserializeObject<OneDataResponse>(responseJson);
            return new MissionMembersOneDataResponse(obj.ContinuationToken, obj.Rows, obj.Columns, _logger);
        }
    }
}
