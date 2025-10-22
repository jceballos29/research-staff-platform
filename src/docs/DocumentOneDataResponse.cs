using Bonis.Extensions;
using Bonis.Services;
using System.Globalization;
using System.Linq;
using System.Collections;

namespace Bonis.Models.OneDataResponse
{
    public class DocumentOneDataResponse : OneDataResponse<DocuemntSyncItem>
    {
        private readonly ILogger<OneDataIntegrationsClient> _logger;

        public DocumentOneDataResponse(string continuationToken, dynamic[] rows, dynamic[] columns, ILogger<OneDataIntegrationsClient> logger)
        {
            this.ContinuationToken = continuationToken;
            Columns = columns ?? (Array.Empty<dynamic>());
            Rows = rows ?? (Array.Empty<dynamic>());
            _logger = logger;
            Deserialize();
        }
        public override void Deserialize()
        {
            Dictionary<string, int> keys = Columns.ValuesToKeys();
            Items = new List<DocuemntSyncItem>();
            foreach (var item in Rows)
            {
                try
                {
                    // Si la fila no es indexable por posición, la omitimos para evitar binder dinámico
                    bool isIndexable = item is object[] || item is IList;
                    if (!isIndexable)
                    {
                        object? o = item as object;
                        string typeName = o == null ? "null" : o.GetType().FullName ?? "(unknown)";
                        _logger.LogWarning("[WARN] Non-indexable row type detected. Skipping. Type={Type}", typeName);
                        continue;
                    }
                    var idStr = GetStringValue(TryGetValue(item, keys, "DocID"));
                    if (string.IsNullOrEmpty(idStr)) continue;
                    // Recuperamos valores uno por uno de forma segura para poder loguear cualquier fallo de mapeo
                    var docId = GetGuidValue(TryGetValue(item, keys, "DocID")) ?? Guid.Empty;
                    var title = TryGetValue(item, keys, "Title")?.ToString();
                    var status = GetIntValue(TryGetValue(item, keys, "Status")) ?? 0;
                    var statusMessage = TryGetValue(item, keys, "StatusMessage")?.ToString();
                    var modifiedOn = GetTimeValue(TryGetValue(item, keys, "ModifiedOn"));
                    var template = TryGetValue(item, keys, "Template")?.ToString();
                    var scannedData = TryGetValue(item, keys, "ScannedData")?.ToString();
                    var arguments = TryGetValue(item, keys, "Arguments")?.ToString();
                    var scannedDataLength = GetIntValue(TryGetValue(item, keys, "ScannedDataLength")) ?? (scannedData == null ? 0 : scannedData.Length);
                    var company = TryGetValue(item, keys, "Company")?.ToString();
                    var discriminator = TryGetValue(item, keys, "Discriminator")?.ToString();
                    var accountName = TryGetValue(item, keys, "AccountName")?.ToString();
                    var missionTitle = TryGetValue(item, keys, "MissionTitle")?.ToString();

                    // IssuedOn: si no viene o no se puede parsear, por defecto DateTime.Now
                    string? issuedOnKey = keys.ContainsKey("ExpeditionDate") ? "ExpeditionDate" : (keys.ContainsKey("IssuedOn") ? "IssuedOn" : null);
                    var issuedOn = issuedOnKey != null ? GetTimeValue(TryGetValue(item, keys, issuedOnKey)) : null;
                    if (!issuedOn.HasValue)
                    {
                        issuedOn = DateTime.Now;
                    }

                    var newItem = new DocuemntSyncItem()
                    {
                        DocID = docId,
                        Title = title,
                        Status = status,
                        StatusMessage = statusMessage,
                        ModifiedOn = modifiedOn ?? default,
                        Template = template,
                        ScannedData = scannedData,
                        Arguments = arguments,
                        ScannedDataLength = scannedDataLength,
                        Company = company,
                        Discriminator = discriminator,
                        AccountName = accountName,
                        MissionTitle = missionTitle,
                        IssuedOn = issuedOn.Value
                    };

                    Items.Add(newItem);
                }
                catch (Exception e)
                {
                    // Mejoramos el log para indicar la clave concreta que pudo fallar y una vista previa del item
                    try
                    {
                        string columns = string.Join(",", keys.Select(k => k.Key));
                        string docIdForLog = GetStringValue(TryGetValue(item, keys, "DocID")) ?? string.Empty;
                        string messageForLog = e.Message ?? string.Empty;
                        // Usamos _logger.LogError con argumentos tipados (no dinámicos) para evitar despacho dinámico
                        _logger.LogError(
                            e,
                            "[ERROR] Deserialize Document: DocID={DocID} Message={Message} Columns={Columns}",
                            docIdForLog,
                            messageForLog,
                            columns
                        );
                    }
                    catch
                    {
                        _logger.LogError(e, "[ERROR] Deserialize Document: unexpected structure");
                    }
                }
            }
        }

        private dynamic? TryGetValue(dynamic row, Dictionary<string, int> keys, string key)
        {
            if (row == null || keys == null || !keys.ContainsKey(key)) return null;
            int idx = keys[key];
            try
            {
                // Intentar caminos tipados para evitar binder dinámico sobre null
                if (row is object[] arr)
                {
                    return (idx >= 0 && idx < arr.Length) ? arr[idx] : null;
                }
                if (row is IList list)
                {
                    return (idx >= 0 && idx < list.Count) ? list[idx] : null;
                }
                // Si no es indexable por posición, devolvemos null para evitar binder dinámico
                return null;
            }
            catch
            {
                // ignoramos; el caller decidirá cómo proceder
                return null;
            }
        }

        public string? GetStringValue(dynamic item) => item?.ToString();
        public DateTime? GetTimeValue(dynamic item)
        {
            if (item == null) return null;
            if (item is DateTime dt) return dt;

            var s = item.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(s)) return null;

            // formatos comunes detectados en logs: dd-MM-yyyy, dd/MM/yyyy
            string[] formats = new[]
            {
                "dd-MM-yyyy", "dd/MM/yyyy", "dd.MM.yyyy",
                "yyyy-MM-dd", "yyyy/MM/dd", "yyyyMMdd",
                "MM/dd/yyyy",
                "yyyy-MM-ddTHH:mm:ss",
                "yyyy-MM-ddTHH:mm:ssZ",
                "yyyy-MM-ddTHH:mm:ss.fff",
                "yyyy-MM-ddTHH:mm:ss.fffZ",
                "o"
            };

            if (DateTime.TryParseExact(s, formats, CultureInfo.GetCultureInfo("es-ES"), DateTimeStyles.None, out DateTime parsedEs))
                return parsedEs;
            if (DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsedInv))
                return parsedInv;

            if (DateTime.TryParse(s, CultureInfo.GetCultureInfo("es-ES"), DateTimeStyles.None, out DateTime parsedAnyEs))
                return parsedAnyEs;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsedAnyInv))
                return parsedAnyInv;

            // No lanzamos: devolvemos null para que el consumidor decida cómo proceder
            return null;
        }

        public Guid? GetGuidValue(dynamic item)
        {
            if (item == null) return null;
            var s = item.ToString();
            if (Guid.TryParse(s, out Guid g)) return g;
            return null;
        }

        public int? GetIntValue(dynamic item)
        {
            if (item == null) return null;
            var s = item.ToString();
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) return v;
            return null;
        }
    }

    public class DocuemntSyncItem
    {
        public Guid DocID { get; set; }
        public string? Title { get; set; }
        public int Status { get; set; }
        public string? StatusMessage { get; set; }
        public DateTime ModifiedOn { get; set; }
        public string? Template { get; set; }
        public string? ScannedData { get; set; }
        public string? Arguments { get; set; }
        public int ScannedDataLength { get; set; }
        public string? Company { get; set; }
        public string? Discriminator { get; set; }
        public string? AccountName { get; set; }
        public string? MissionTitle { get; set; }
        public DateTime IssuedOn { get; set; }
    }
}
