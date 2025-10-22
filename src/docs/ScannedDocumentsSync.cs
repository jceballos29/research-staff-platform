using Bonis.Data;
using Bonis.Enums;
using Bonis.Models.OneDataResponse;
using Bonis.Services;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Math;
using DocumentFormat.OpenXml.Spreadsheet;
using Hangfire;
using IdentityModel;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using static System.Net.WebRequestMethods;
using Bonis.Jobs.Logging;
using System.Diagnostics;

namespace Bonis.Jobs
{
    /// <summary>
    /// Servicio encargado de sincronizar documentos escaneados desde OneData y procesar los periodos RNT e IDC asociados.
    /// </summary>
    public class ScannedDocumentsSync
    {
        private readonly ApplicationDbContext _context;
        private readonly IOneDataSyncServiceWorker _serviceWorker;
        private readonly ILogger<ScannedDocumentsSync> _logger;
        private readonly IFilesService _filesService;
        private readonly ITranslationService _t;

        // Logging simplificado: eliminado modo verbose específico RNT / NO-RNT
        private const int MAX_JSON_LOG_CHARS = 8000; // límite de caracteres al loguear JSON
        private const int MAX_FEED_PAGES_PER_RUN = 5; // límite de páginas del feed por ejecución para evitar bucles largos
                                                      // Eliminado IsRntTemplate: ya no condicionamos detalle por tipo
        private static string Truncate(string? v, int max) => string.IsNullOrEmpty(v) ? string.Empty : (v.Length <= max ? v : v.Substring(0, max) + $"... (+{v.Length - max} chars)");

        /// <summary>
        /// Inicializa el servicio de sincronización de documentos escaneados.
        /// </summary>
        public ScannedDocumentsSync(ApplicationDbContext context, IOneDataSyncServiceWorker serviceWorker, ILogger<ScannedDocumentsSync> logger, IFilesService filesService, ITranslationService t)
        {
            _context = context;
            _serviceWorker = serviceWorker;
            _logger = logger;
            _filesService = filesService;
            _t = t;
        }

        // Listas de valores permitidos y plantillas soportadas
        private static readonly List<string> DOCUMENT_TEMPLATES = ["rntglobal", "rntindividual", "idcindividual", "idcpl", "idcrl", "vilem"];

        private static readonly List<string> ALLOWED_CODES = ["3171", "3172", "3173", "3174", "3175"];

        private static readonly List<string> LIQUIDATION_QUALIFIERS = ["L00-NORMAL", "L13-VACACIONES RETRIB", "L03-COMP SALARIOS RETROA"];
        private static readonly List<string> ALLOWED_DESCRIPTION = ["BASE DE CONTINGENCIAS COMUNES", "BASE C.COMUNES MAT./ PAT.PARCIAL"];

        /// <summary>
        /// Método principal que ejecuta la sincronización de documentos escaneados.
        /// Procesa los documentos recibidos, los valida y actualiza/agrega los periodos RNT e IDC en la base de datos.
        /// </summary>
        [AutomaticRetry(Attempts = 1, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
        public async Task Run()
        {
            var jobRunId = JobLogging.NewRunId();
            using var jobScope = JobLogging.BeginJobScope(_logger, jobRunId);
            JobLogging.Info(_logger, "[JOB][START]",
                "Initializing Scanned Documents Synchronization... Time={Time} MAX_FEED_PAGES_PER_RUN={MaxPages} JobRunId={JobRunId}",
                DateTime.Now, MAX_FEED_PAGES_PER_RUN, jobRunId);

            int scannedDocumentsAdded = 0;
            int scannedDocumentsUpdated = 0;
            int scannedDocumentsSkipped = 0;
            int reportsOk = 0;
            int reportsSkipped = 0;
            var skipReasons = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<RNTType, int> rntPeriodsAdded = new();
            Dictionary<IDCType, int> idcPeriodsAdded = new();

            try
            {
                // Optimización de operaciones de base de datos para evitar timeouts con cargas grandes
                // - Aumenta el timeout de comandos (por defecto ~30s)
                // - Desactiva AutoDetectChanges para mejorar rendimiento, lo reactivamos al final
                // - Guardamos en lotes pequeños para evitar INSERT gigantes
                _context.Database.SetCommandTimeout(180); // 3 minutos
                _context.ChangeTracker.AutoDetectChangesEnabled = false;

                const int SAVE_BATCH_SIZE = 25; // lote pequeño para particionar INSERT/UPDATE
                int pendingDbOps = 0;

                JobLogging.Info(_logger, "[JOB]", "Begin Synchronization");
                int pagesProcessed = 0;
                // Itera sobre los lotes de documentos recibidos desde OneData
                await foreach (var results in _serviceWorker.GetDocumentsChangeSetEnumerableAsync())
                {
                    using var pageTimer = JobLogging.TimeOperation(out var swPage);
                    pagesProcessed++;
                    using var pageScope = JobLogging.BeginJobScope(_logger, jobRunId, pagesProcessed, results?.ContinuationToken);
                    if (results == null)
                    {
                        JobLogging.Warn(_logger, "[PAGE][SKIP]", "Page={Page} Reason=Results null", pagesProcessed);
                        break;
                    }
                    if (results.Items == null || results.Items.Count == 0)
                    {
                        JobLogging.Info(_logger, "[PAGE][SKIP]", "Page={Page} Reason=NoItems", pagesProcessed);
                        await UpdateContinuationToken(results.ContinuationToken);
                        break;
                    }
                    JobLogging.Info(_logger, "[PAGE][RECV]", "Page={Page} Items={Items} ContinuationToken={Token}", pagesProcessed, results.Items.Count, results.ContinuationToken);
                    int pageProcessed = 0, pageSkipped = 0;
                    foreach (var item in results.Items)
                    {
                        if (item == null) continue;

                        ScannedDocument? documentToProcess = null;
                        bool isEffectivelyNewLogicalDocument = false;
                        DateTime? issuedOn = item.IssuedOn != default(DateTime) ? item.IssuedOn : null;

                        // Normaliza la plantilla y otros campos clave
                        string normalizedTemplate = item.Template?.Replace(" ", "").Replace("/", "").ToLower(CultureInfo.CurrentCulture) ?? string.Empty;
                        string currentAccountName = item.AccountName ?? string.Empty;
                        string currentMissionTitle = item.MissionTitle ?? string.Empty;

                        // Header log (unificado)
                        JobLogging.Info(_logger, "[DOC][BEGIN]", "DocID={DocID} File={Title} Template={Template} Status={Status} IssuedOn={IssuedOn:O} Account={Account} Mission={Mission} ModifiedOn={ModifiedOn:O}",
                            item.DocID, item.Title, normalizedTemplate, item.Status, issuedOn, currentAccountName, currentMissionTitle, item.ModifiedOn);

                        // Valida que los campos clave estén presentes
                        if (string.IsNullOrEmpty(normalizedTemplate) || string.IsNullOrEmpty(currentAccountName) || issuedOn == null)
                        {
                            var missing = new List<string>();
                            if (string.IsNullOrEmpty(normalizedTemplate)) missing.Add("Template");
                            if (string.IsNullOrEmpty(currentAccountName)) missing.Add("AccountName");
                            if (issuedOn == null) missing.Add("IssuedOn");
                            var reason = $"MissingFields:{string.Join("_", missing)}";
                            JobLogging.Warn(_logger, "[DOC][SKIP]", "DocID={DocID} File={Title} Reason={Reason}", item.DocID, item.Title, reason);
                            scannedDocumentsSkipped++;
                            if (!skipReasons.ContainsKey(reason)) skipReasons[reason] = 0; skipReasons[reason]++;
                            pageSkipped++;
                            continue;
                        }

                        // Busca el documento en la base de datos por plantilla, cuenta y fecha de emisión
                        // Consulta liviana: sin Includes (no son necesarios para esta decisión)
                        documentToProcess = await _context.ScannedDocuments
                            .FirstOrDefaultAsync(d => d.Template == normalizedTemplate && d.AccountName == currentAccountName && d.IssuedOn == issuedOn);

                        if (documentToProcess != null)
                        {
                            // Si ya existe, actualiza los datos
                            isEffectivelyNewLogicalDocument = false;
                            UpdateScannedDocument(documentToProcess, item);
                            scannedDocumentsUpdated++;
                            pendingDbOps++;
                        }
                        else
                        {
                            // Si no existe, busca por DocId (consulta liviana) o crea uno nuevo
                            documentToProcess = await _context.ScannedDocuments
                                .FirstOrDefaultAsync(d => d.DocId == item.DocID);

                            if (documentToProcess != null)
                            {
                                isEffectivelyNewLogicalDocument = true;
                                UpdateScannedDocument(documentToProcess, item, issuedOn, currentAccountName, currentMissionTitle);
                                scannedDocumentsUpdated++;
                                pendingDbOps++;
                            }
                            else
                            {
                                isEffectivelyNewLogicalDocument = true;
                                documentToProcess = CreateScannedDocument(item);
                                _context.ScannedDocuments.Add(documentToProcess);
                                scannedDocumentsAdded++;
                                pendingDbOps++;
                            }
                        }

                        // Procesa los periodos si la plantilla es soportada y el documento está finalizado (Status == 2)
                        if (!DOCUMENT_TEMPLATES.Contains(normalizedTemplate))
                        {
                            var reason = $"UnsupportedTemplate:{normalizedTemplate}";
                            JobLogging.Info(_logger, "[DOC][SKIP]", "DocID={DocID} File={Title} Reason={Reason}", item.DocID, item.Title, reason);
                            scannedDocumentsSkipped++;
                            if (!skipReasons.ContainsKey(reason)) skipReasons[reason] = 0; skipReasons[reason]++;
                            pageSkipped++;
                        }
                        else if (documentToProcess.Status != 2)
                        {
                            var reason = $"StatusNotFinished:{documentToProcess.Status}";
                            JobLogging.Info(_logger, "[DOC][SKIP]", "DocID={DocID} File={Title} Reason={Reason}", item.DocID, item.Title, reason);
                            scannedDocumentsSkipped++;
                            if (!skipReasons.ContainsKey(reason)) skipReasons[reason] = 0; skipReasons[reason]++;
                            pageSkipped++;
                        }
                        else
                        {
                            await ProcessDocumentPeriods(item, documentToProcess, normalizedTemplate, isEffectivelyNewLogicalDocument, rntPeriodsAdded, idcPeriodsAdded);
                            pageProcessed++;
                        }

                        // Guardar inmediatamente tras procesar cada documento
                        await _context.SaveChangesAsync();

                        // Generar y adjuntar el reporte Excel
                        try
                        {
                            var reportUrl = await GenerateAndAttachScanReportAsync(documentToProcess);
                            if (!string.IsNullOrWhiteSpace(reportUrl))
                            {
                                documentToProcess.ScanReportUrl = reportUrl;
                                _context.ScannedDocuments.Update(documentToProcess);
                                await _context.SaveChangesAsync();
                                reportsOk++;
                            }
                            else { reportsSkipped++; }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"[DOC][REPORT][ERROR] DocID={documentToProcess.DocId} File={documentToProcess.Title} Reason=Excel generation failed");
                            reportsSkipped++;
                        }

                        _context.ChangeTracker.Clear();
                        pendingDbOps = 0;
                    }

                    // Guarda los cambios y actualiza el token de continuación
                    await _context.SaveChangesAsync();
                    _context.ChangeTracker.Clear();
                    pendingDbOps = 0;
                    await UpdateContinuationToken(results.ContinuationToken);
                    JobLogging.Info(_logger, "[PAGE][SUMMARY]", "Page={Page} Items={Items} Processed={Processed} Skipped={Skipped} DurationMs={Duration}",
                        pagesProcessed, results.Items.Count, pageProcessed, pageSkipped, swPage.ElapsedMilliseconds);
                    JobLogging.Info(_logger, "[JOB][AGG]", "SoFar DocsNew={New} DocsUpdated={Updated} DocsSkipped={Skipped} ReportsOk={ReportsOk} ReportsSkipped={ReportsSkipped}",
                        scannedDocumentsAdded, scannedDocumentsUpdated, scannedDocumentsSkipped, reportsOk, reportsSkipped);
                    _logger.LogInformation($"ScannedDocuments agregados: {scannedDocumentsAdded}");
                    foreach (var kv in rntPeriodsAdded)
                        _logger.LogInformation($"RNT agregados de tipo {kv.Key}: {kv.Value}");
                    foreach (var kv in idcPeriodsAdded)
                        _logger.LogInformation($"IDC agregados de tipo {kv.Key}: {kv.Value}");

                    pagesProcessed++;
                    if (pagesProcessed >= MAX_FEED_PAGES_PER_RUN)
                    {
                        JobLogging.Info(_logger, "[PAGE][LIMIT]", "Reached MAX_FEED_PAGES_PER_RUN={MaxPages} at Page={Page}", MAX_FEED_PAGES_PER_RUN, pagesProcessed);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                JobLogging.Error(_logger, "[JOB][ERROR]", "Error while synchronizing Scanned Documents: {Message}", ex.Message);
            }
            finally
            {
                var rntSummary = string.Join(", ", rntPeriodsAdded.Select(kv => $"{kv.Key}:{kv.Value}"));
                var idcSummary = string.Join(", ", idcPeriodsAdded.Select(kv => $"{kv.Key}:{kv.Value}"));
                var reasonsTop = string.Join(", ", skipReasons.OrderByDescending(kv => kv.Value).Take(5).Select(kv => $"{kv.Key}:{kv.Value}"));
                JobLogging.Info(_logger, "[JOB][FINISH]",
                    "DocsTotal={Total} New={New} Updated={Updated} Skipped={Skipped} ReportsOk={ReportsOk} ReportsSkipped={ReportsSkipped} RNT={Rnt} IDC={Idc} SkipReasonsTop={Reasons}",
                    scannedDocumentsAdded + scannedDocumentsUpdated + scannedDocumentsSkipped,
                    scannedDocumentsAdded, scannedDocumentsUpdated, scannedDocumentsSkipped,
                    reportsOk, reportsSkipped, rntSummary, idcSummary, reasonsTop);
                JobLogging.Info(_logger, "[JOB]", "Scanned Documents Synchronization Finished");
                // Restaurar configuración del ChangeTracker
                _context.ChangeTracker.AutoDetectChangesEnabled = true;
            }

        }

        /// <summary>
        /// Genera un Excel con dos tablas por hoja (Escaneados primero y Procesados debajo) y lo sube a Azure Blob.
        /// Devuelve la URL pública del blob.
        /// </summary>
        private async Task<string?> GenerateAndAttachScanReportAsync(ScannedDocument doc)
        {
            if (doc == null) return null;
            // Discriminadores previos a la generación

            if (doc.Status != 2)
            {
                _logger.LogInformation($"[DOC][REPORT][SKIP] DocID={doc.DocId} File={doc.Title} Reason=Status {doc.Status} != 2");
                return null;
            }
            if (string.IsNullOrWhiteSpace(doc.ScannedData))
            {
                _logger.LogInformation($"[DOC][REPORT][SKIP] DocID={doc.DocId} File={doc.Title} Reason=ScannedData is null/empty");
                return null;
            }
            if (string.IsNullOrWhiteSpace(doc.MissionTitle))
            {
                _logger.LogInformation($"[DOC][REPORT][SKIP] DocID={doc.DocId} File={doc.Title} Reason=MissionTitle is null/empty");
                return null;
            }
            if (string.IsNullOrWhiteSpace(doc.Template))
            {
                _logger.LogInformation($"[DOC][REPORT][SKIP] DocID={doc.DocId} File={doc.Title} Reason=Template is null/empty");
                return null;
            }

            Service? service = await _context.Services.FirstOrDefaultAsync(s => s.MissionCode == doc.MissionTitle);
            if (service == null)
            {
                _logger.LogWarning($"[DOC][REPORT][SKIP] DocID={doc.DocId} File={doc.Title} Reason=Mission {doc.MissionTitle} not found in DB");
                return null;
            }

            Customer? customer = await _context.Customers.FirstOrDefaultAsync(c => c.CustomerId == service.CustomerId);
            if (customer == null)
            {
                _logger.LogWarning($"[DOC][REPORT][SKIP] DocID={doc.DocId} File={doc.Title} Reason=Customer for Mission {doc.MissionTitle} not found in DB");
                return null;
            }

            if (string.IsNullOrWhiteSpace(customer.StorageName))
            {
                _logger.LogWarning($"[DOC][REPORT][SKIP] DocID={doc.DocId} File={doc.Title} Reason=Customer {customer.CustomerName} has no StorageName configured");
                return null;
            }

            _logger.LogInformation($"[DOC][REPORT][BEGIN] DocID={doc.DocId} File={doc.Title} Template={doc.Template}");

            var ms = new MemoryStream();
            var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("Escaneados");

            var templateStr = doc.Template;
            var normalizedTemplate = templateStr.Replace(" ", string.Empty).Replace("/", string.Empty).ToLower(CultureInfo.CurrentCulture);

            if (templateStr == null)
            {
                _logger.LogWarning($"[DOC][REPORT][SKIP] DocID={doc.DocId} File={doc.Title} Reason=Template is null");
                return null;
            }

            if (!DOCUMENT_TEMPLATES.Contains(normalizedTemplate))
            {
                _logger.LogInformation($"[DOC][REPORT][SKIP] DocID={doc.DocId} File={doc.Title} Reason=Unsupported template Template={normalizedTemplate}");
                return null;
            }

            int headerColumn = 1;
            if (normalizedTemplate.Equals("rntglobal"))
            {
                ws.Cell(1, headerColumn++).Value = "Razón social";
                ws.Cell(1, headerColumn++).Value = "Código cuenta cotización";
                ws.Cell(1, headerColumn++).Value = "Período de Liquidación";
                ws.Cell(1, headerColumn++).Value = "Calificador de la Liquidación";
                ws.Cell(1, headerColumn++).Value = "NAF";
                ws.Cell(1, headerColumn++).Value = "N.I.F";
                ws.Cell(1, headerColumn++).Value = "Acrónimo";
                ws.Cell(1, headerColumn++).Value = "Fecha Inicio";
                ws.Cell(1, headerColumn++).Value = "Fecha Fin";
                ws.Cell(1, headerColumn++).Value = "Días Coti.";
                ws.Cell(1, headerColumn++).Value = "Horas Coti.";
                ws.Cell(1, headerColumn++).Value = "Descripción";
                ws.Cell(1, headerColumn++).Value = "Importe";
            }
            else if (normalizedTemplate.Equals("rntindividual"))
            {
                ws.Cell(1, headerColumn++).Value = "Razón social";
                ws.Cell(1, headerColumn++).Value = "Código cuenta cotización";
                ws.Cell(1, headerColumn++).Value = "Código de Empresario";
                ws.Cell(1, headerColumn++).Value = "Período de Liquidación";
                ws.Cell(1, headerColumn++).Value = "Calificador de la Liquidación";
                ws.Cell(1, headerColumn++).Value = "NAF";
                ws.Cell(1, headerColumn++).Value = "I.P.F (DNI)";
                ws.Cell(1, headerColumn++).Value = "C.A.F. (ACRÓNIMO)";
                ws.Cell(1, headerColumn++).Value = "Fechas Tramo Desde";
                ws.Cell(1, headerColumn++).Value = "Fechas Tramo Hasta";
                ws.Cell(1, headerColumn++).Value = "Días Cotiz.";
                ws.Cell(1, headerColumn++).Value = "Horas Cotiz.";
                ws.Cell(1, headerColumn++).Value = "Descripción";
                ws.Cell(1, headerColumn++).Value = "Importe";
            }
            else if (normalizedTemplate.Equals("idcindividual"))
            {
                ws.Cell(1, headerColumn).Value = "NOMBRE Y APELLIDOS";
                ws.Cell(1, headerColumn).Value = "NSS";
                ws.Cell(1, headerColumn).Value = "DOC.IDENTIFICATIVO";
                ws.Cell(1, headerColumn).Value = "NUM";
                ws.Cell(1, headerColumn).Value = "RAZÓN SOCIAL";
                ws.Cell(1, headerColumn).Value = "CCC";
                ws.Cell(1, headerColumn).Value = "CNAE";
                ws.Cell(1, headerColumn).Value = "PERIODO";
                ws.Cell(1, headerColumn).Value = "TIPO CONTRATO";
                ws.Cell(1, headerColumn).Value = "ALTA";
                ws.Cell(1, headerColumn).Value = "BAJA";
                ws.Cell(1, headerColumn).Value = "RLCE";
                ws.Cell(1, headerColumn).Value = "COEF.TIEMPO PARCIAL";
                ws.Cell(1, headerColumn).Value = "REDUCCIÓN JORNADA/ COEFIC";
                ws.Cell(1, headerColumn).Value = "GC / M *";
                ws.Cell(1, headerColumn).Value = "FECHA INICIO CONTRATO DE TRABAJO";
                ws.Cell(1, headerColumn).Value = "FIN CONTRATO DE TRABAJO";
                ws.Cell(1, headerColumn).Value = "OCUPACIÓN";
                ws.Cell(1, headerColumn).Value = "TIPO DE PECULIARIDAD";
                ws.Cell(1, headerColumn).Value = "PORCENTAJE / TIPO";
                ws.Cell(1, headerColumn).Value = "CUANTÍA / MES";
                ws.Cell(1, headerColumn).Value = "FRACCIÓN DE CUOTA";
                ws.Cell(1, headerColumn).Value = "DESDE";
                ws.Cell(1, headerColumn).Value = "HASTA";
                ws.Cell(1, headerColumn).Value = "FECHA EXTRACCIÓN ID";
            }
            else if (normalizedTemplate.Equals("idcpl"))
            {
                ws.Cell(1, headerColumn).Value = "Razón social";
                ws.Cell(1, headerColumn).Value = "CCC";
                ws.Cell(1, headerColumn).Value = "Período de liquidación";
                ws.Cell(1, headerColumn).Value = "NSS";
                ws.Cell(1, headerColumn).Value = "Nombre y apellidos";
                ws.Cell(1, headerColumn).Value = "Caso Especial";
                ws.Cell(1, headerColumn).Value = "Fecha desde";
                ws.Cell(1, headerColumn).Value = "Fecha hasta";
                ws.Cell(1, headerColumn).Value = "GC/M";
                ws.Cell(1, headerColumn).Value = "% Pluriempleo";
                ws.Cell(1, headerColumn).Value = "Tipo de peculiaridad";
                ws.Cell(1, headerColumn).Value = "Por/tipo";
                ws.Cell(1, headerColumn).Value = "Colectivo incentivado";
            }
            else if (normalizedTemplate.Equals("idcrl"))
            {
                ws.Cell(1, headerColumn).Value = "RAZÓN SOCIAL";
                ws.Cell(1, headerColumn).Value = "CCC";
                ws.Cell(1, headerColumn).Value = "PERIODO SOLICITADO";
                ws.Cell(1, headerColumn).Value = "NOMBRE Y APELLIDOS";
                ws.Cell(1, headerColumn).Value = "NÚMERO SEGURIDAD SOCIAL";
                ws.Cell(1, headerColumn).Value = "DOC.IDENTIFICATIVO";
                ws.Cell(1, headerColumn).Value = "FECHA DESDE";
                ws.Cell(1, headerColumn).Value = "FECHA HASTA";
                ws.Cell(1, headerColumn).Value = "TIPO CONTRATO";
                ws.Cell(1, headerColumn).Value = "GC/M";
                ws.Cell(1, headerColumn).Value = "FECHA DESDE P.";
                ws.Cell(1, headerColumn).Value = "FECHA HASTA P.";
                ws.Cell(1, headerColumn).Value = "TIPO DE PECULIARIDAD";
                ws.Cell(1, headerColumn).Value = "POR/TIPO";
                ws.Cell(1, headerColumn).Value = "FRACCIÓN DE CUOTA";
                ws.Cell(1, headerColumn).Value = "COLECTIVO INCENTIVADO";
                ws.Cell(1, headerColumn).Value = "LEGISLACIÓN";
            }
            else if (normalizedTemplate.Equals("vilem"))
            {

                ws.Cell(1, headerColumn).Value = "RAZÓN SOCIAL";
                ws.Cell(1, headerColumn).Value = "C.C.C.";
                ws.Cell(1, headerColumn).Value = "PERIODO SOLICITADO";
                ws.Cell(1, headerColumn).Value = "NÚMERO AFILICACIÓN";
                ws.Cell(1, headerColumn).Value = "DOCUMENTO IDENTIFICATIVO";
                ws.Cell(1, headerColumn).Value = "APELLIDOS Y NOMBRE";
                ws.Cell(1, headerColumn).Value = "SITUACIÓN";
                ws.Cell(1, headerColumn).Value = "F.REAL ALTA";
                ws.Cell(1, headerColumn).Value = "F.EFECTO ALTA";
                ws.Cell(1, headerColumn).Value = "F.REAL SIT.";
                ws.Cell(1, headerColumn).Value = "F.EFECTO SIT.";
                ws.Cell(1, headerColumn).Value = "G. C/M";
                ws.Cell(1, headerColumn).Value = "T.C.";
                ws.Cell(1, headerColumn).Value = "C.T.P.";
                ws.Cell(1, headerColumn).Value = "EP/OC";
                ws.Cell(1, headerColumn).Value = "DÍAS COT.";
            }
            else
            {
                _logger.LogWarning($"[DOC][REPORT][SKIP] DocID={doc.DocId} File={doc.Title} Reason=Template {normalizedTemplate} not specifically handled");
                return null;
            }
            _logger.LogInformation($"[DOC][REPORT][HEADERS] DocID={doc.DocId} File={doc.Title} Template={normalizedTemplate} Headers={headerColumn - 1} columns");


            JArray scannedDataArray;
            try
            {
                scannedDataArray = JArray.Parse(doc.ScannedData);
                _logger.LogInformation($"[DOC][REPORT][JSON][OK] DocID={doc.DocId} Items={scannedDataArray.Count}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[DOC][REPORT][JSON][ERROR] DocID={doc.DocId}");
                return null;
            }

            int row = 2;
            int column = 1;
            if (normalizedTemplate.Equals("rntglobal"))
            {
                foreach (JObject scannedDataItem in scannedDataArray.Cast<JObject>())
                {
                    column = 1;
                    ws.Cell(row, column++).Value = scannedDataItem["Razón social"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column++).Value = scannedDataItem["Código cuenta cotización"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column++).Value = scannedDataItem["Período de liquidación"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column++).Value = scannedDataItem["Calificador de la liquidación"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column++).Value = scannedDataItem["NAF"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column++).Value = scannedDataItem["N.I.F"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column++).Value = scannedDataItem["Acrónimo"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column++).Value = scannedDataItem["Fecha Inicio"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column++).Value = scannedDataItem["Fecha Fin"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column++).Value = scannedDataItem["Días Coti."]?.ToString() ?? string.Empty;
                    ws.Cell(row, column++).Value = scannedDataItem["Horas Coti."]?.ToString() ?? string.Empty;
                    ws.Cell(row, column++).Value = scannedDataItem["Descripción"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column++).Value = scannedDataItem["Importe"]?.ToString() ?? string.Empty;
                    row++;
                }
            }
            else if (normalizedTemplate.Equals("rntindividual"))
            {
                foreach (JObject scannedDataItem in scannedDataArray.Cast<JObject>())
                {
                    column = 1;
                    ws.Cell(row, column++).Value = scannedDataItem["Razón social"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column++).Value = scannedDataItem["Código cuenta cotización"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column++).Value = scannedDataItem["Código de Empresario"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column++).Value = scannedDataItem["Período de Liquidación"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column++).Value = scannedDataItem["Calificador de la Liquidación"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column++).Value = scannedDataItem["NAF"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column++).Value = scannedDataItem["I.P.F (DNI)"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column++).Value = scannedDataItem["C.A.F. (ACRÓNIMO)"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column++).Value = scannedDataItem["Fechas Tramo Desde"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column++).Value = scannedDataItem["Fechas Tramo Hasta"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column++).Value = scannedDataItem["Días Cotiz."]?.ToString() ?? string.Empty;
                    ws.Cell(row, column++).Value = scannedDataItem["Horas Cotiz."]?.ToString() ?? string.Empty;
                    ws.Cell(row, column++).Value = scannedDataItem["Descripción"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column++).Value = scannedDataItem["Importe"]?.ToString() ?? string.Empty;
                    row++;
                }
            }
            else if (normalizedTemplate.Equals("idcindividual"))
            {
                foreach (JObject scannedDataItem in scannedDataArray.Cast<JObject>())
                {
                    column = 1;
                    ws.Cell(row, column).Value = scannedDataItem["NOMBRE Y APELLIDOS"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["NSS"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["DOC.IDENTIFICATIVO"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["NUM"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["RAZÓN SOCIAL"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["CCC"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["CNAE"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["PERIODO"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["TIPO CONTRATO"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["ALTA"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["BAJA"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["RLCE"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["COEF.TIEMPO PARCIAL"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["REDUCCIÓN JORNADA/COEFIC"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["GC/M*"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["FECHA INICIO CONTRATO DE TRABAJO"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["FIN CONTRATO DE TRABAJO"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["OCUPACIÓN"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["TIPO DE PECULIARIDAD"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["PORCENTAJE/TIPO"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["CUANTÍA/MES"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["FRACCIÓN DE CUOTA"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["DESDE"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["HASTA"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["FECHA EXTRACCIÓN IDC"]?.ToString() ?? string.Empty;
                    row++;
                }
            }
            else if (normalizedTemplate.Equals("idcpl"))
            {
                foreach (JObject scannedDataItem in scannedDataArray.Cast<JObject>())
                {
                    column = 1;
                    ws.Cell(row, column).Value = scannedDataItem["Razón social"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["CCC"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["Período de liquidación"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["NSS"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["Nombre y apellidos"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["Caso Especial"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["Fecha desde"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["Fecha hasta"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["GC/M"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["% Pluriempleo"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["Tipo de peculiaridad"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["Por/tipo"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["Colectivo incentivado"]?.ToString() ?? string.Empty;
                    row++;
                }
            }
            else if (normalizedTemplate.Equals("idcrl"))
            {
                foreach (JObject scannedDataItem in scannedDataArray.Cast<JObject>())
                {
                    column = 1;
                    ws.Cell(row, column).Value = scannedDataItem["RAZ�N SOCIAL"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["CCC"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["PERIODO SOLICITADO"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["NOMBRE Y APELLIDOS"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["N�MERO SEGURIDAD SOCIAL"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["DOC.IDENTIFICATIVO"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["FECHA DESDE"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["FECHA HASTA"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["TIPO CONTRATO"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["GC/M"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["FECHA DESDE P."]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["FECHA HASTA P."]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["TIPO DE PECULIARIDAD"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["POR/TIPO"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["FRACCI�N DE CUOTA"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["COLECTIVO INCENTIVADO"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["LEGISLACI�N"]?.ToString() ?? string.Empty;
                    row++;
                }
            }
            else if (normalizedTemplate.Equals("vilem"))
            {
                foreach (JObject scannedDataItem in scannedDataArray.Cast<JObject>())
                {
                    column = 1;
                    ws.Cell(row, column).Value = scannedDataItem["RAZÓN SOCIAL"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["C.C.C."]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["PERIODO SOLICITADO"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["NÚMERO AFILICACIÓN"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["DOCUMENTO IDENTIFICATIVO"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["APELLIDOS Y NOMBRE"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["SITUACIÓN"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["F.REAL ALTA"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["F.EFECTO ALTA"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["F.REAL SIT."]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["F.EFECTO SIT."]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["G. C/M"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["T.C."]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["C.T.P."]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["EP/OC"]?.ToString() ?? string.Empty;
                    ws.Cell(row, column).Value = scannedDataItem["DÍAS COT."]?.ToString() ?? string.Empty;
                    row++;
                }
            }
            else
            {
                _logger.LogWarning($"[DOC][REPORT][SKIP] DocID={doc.DocId} File={doc.Title} Reason=Template {normalizedTemplate} not specifically handled");
                return null;
            }

            wb.SaveAs(ms);
            ms.Seek(0, SeekOrigin.Begin);

            var filename = $"ScanReport_{doc.DocId}_{doc.MissionTitle}.xlsx";
            var targetDirectory = "Documents/ScannedReports";
            // Si ya existe un reporte previo con el mismo nombre lo eliminamos antes de subir
            try
            {
                if (_filesService.ExistFile(customer.StorageName, targetDirectory, filename))
                {
                    var deleted = await _filesService.DeleteFile(customer.StorageName, targetDirectory, filename);
                    _logger.LogInformation($"[DOC][REPORT][DEL-OLD] DocID={doc.DocId} File={doc.Title} Deleted={deleted}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"[DOC][REPORT][DEL-OLD][WARN] DocID={doc.DocId} File={doc.Title} Reason=Pre-delete failed (continuing)");
            }

            var url = _filesService.UploadFile(customer.StorageName, targetDirectory, ms, filename);

            if (string.IsNullOrWhiteSpace(url))
            {
                _logger.LogError($"[DOC][REPORT][ERROR] DocID={doc.DocId} File={doc.Title} Reason=Upload returned null/empty URL");
                return null;
            }

            _logger.LogInformation($"[DOC][REPORT][OK] DocID={doc.DocId} File={doc.Title} URL={url}");
            return url;
        }

        /// <summary>
        /// Actualiza los datos de un documento escaneado existente con los valores de un nuevo item.
        /// Solo actualiza si la fecha de modificación es más reciente.
        /// </summary>
        /// <param name="document">Documento a actualizar.</param>
        /// <param name="item">Datos nuevos.</param>
        /// <param name="newIssuedOn">Nueva fecha de emisión (opcional).</param>
        /// <param name="newAccountName">Nuevo nombre de cuenta (opcional).</param>
        /// <param name="newMissionTitle">Nuevo título de misión (opcional).</param>
        private void UpdateScannedDocument(ScannedDocument document, DocuemntSyncItem item, DateTime? newIssuedOn = null, string? newAccountName = null, string? newMissionTitle = null)
        {
            // Solo actualiza si el nuevo documento es más reciente
            if (item.ModifiedOn > document.ModifiedOn)
            {
                document.Status = item.Status;
                document.StatusMessage = item.StatusMessage;
                document.ModifiedOn = item.ModifiedOn;
                document.Template = item.Template;
                document.ScannedData = item.ScannedData;
                document.Arguments = item.Arguments;
                document.ScannedDataLength = item.ScannedData?.Length ?? 0;
            }
            if (newIssuedOn.HasValue) document.IssuedOn = newIssuedOn.Value;
            if (!string.IsNullOrEmpty(newAccountName)) document.AccountName = newAccountName;
            if (!string.IsNullOrEmpty(newMissionTitle)) document.MissionTitle = newMissionTitle;
            _logger.LogInformation($"[DOC][UPDATE] DocId={document.DocId} File={item.Title} ModifiedOn={item.ModifiedOn:O} Status={item.Status}");
            _context.ScannedDocuments.Update(document);
        }

        /// <summary>
        /// Crea una nueva entidad ScannedDocument a partir de un DocuemntSyncItem.
        /// </summary>
        /// <param name="item">Item con los datos del documento.</param>
        /// <returns>Instancia de ScannedDocument lista para agregar a la base de datos.</returns>
        private static ScannedDocument CreateScannedDocument(DocuemntSyncItem item)
        {
            return new ScannedDocument
            {
                DocId = item.DocID,
                Title = item.Title,
                Status = item.Status,
                StatusMessage = item.StatusMessage,
                ModifiedOn = item.ModifiedOn,
                Template = item.Template,
                ScannedData = item.ScannedData,
                Arguments = item.Arguments,
                ScannedDataLength = item.ScannedData?.Length ?? 0,
                Company = item.Company,
                Discriminator = item.Discriminator,
                AccountName = item.AccountName,
                MissionTitle = item.MissionTitle,
                IssuedOn = item.IssuedOn
            };
        }

        /// <summary>
        /// Procesa los periodos (RNT o IDC) de un documento escaneado y los guarda en la base de datos.
        /// </summary>
        public async Task ProcessDocumentPeriods(DocuemntSyncItem item, ScannedDocument document, string normalizedTemplate, bool isNewDocument, Dictionary<RNTType, int> rntPeriodsAdded, Dictionary<IDCType, int> idcPeriodsAdded)
        {
            // Busca el servicio y el cliente asociados al documento
            Service? service = !string.IsNullOrEmpty(document.MissionTitle) ? await _context.Services.FirstOrDefaultAsync(s => s.MissionCode == document.MissionTitle) : null;
            if (service == null)
            {
                _logger.LogWarning($"[PROC][SKIP] DocId={document.DocId} File={document.Title} Reason=Service not found MissionTitle={document.MissionTitle}");
                return;
            }

            Customer? customer = await _context.Customers.FirstOrDefaultAsync(c => c.CustomerId == service.CustomerId);
            if (customer == null)
            {
                _logger.LogWarning($"[PROC][SKIP] DocId={document.DocId} File={document.Title} Reason=Customer not found ServiceId={service.ServiceId}");
                return;
            }

            if (string.IsNullOrEmpty(document.ScannedData))
            {
                _logger.LogWarning($"[PROC][SKIP] DocId={document.DocId} File={document.Title} Reason=ScannedData empty");
                return;
            }

            // Parsea el JSON de los datos escaneados
            JArray scannedDataArray;
            try
            {
                // Se omite log de JSON bruto para reducir ruido.
                scannedDataArray = JArray.Parse(document.ScannedData);
            }
            catch (JsonReaderException ex)
            {
                _logger.LogError($"[PROC][ERROR] DocId={document.DocId} File={document.Title} Reason=Invalid JSON Error={ex.Message}");
                return;
            }

            // Procesa cada item de datos escaneados
            foreach (JObject scannedDataItem in scannedDataArray.Cast<JObject>())
            {
                var scannedData = ParseAndAdaptScannedDataItem(scannedDataItem, normalizedTemplate);
                if (scannedData == null)
                {
                    _logger.LogWarning($"[PROC][SKIP] DocId={document.DocId} File={document.Title} Reason=Parsed scanned data is null Template={normalizedTemplate}");
                    continue;
                }

                // Busca el investigador asociado por NAF y servicio
                Resource? resource = await _context.Resources
                    .Include(r => r.Company)
                    .FirstOrDefaultAsync(r => r.NAF.Replace(" ", "") == scannedData.NAF && r.ServiceId == service.ServiceId);

                // tipo determinado por pattern matching
                if (resource == null || resource.Company == null)
                {
                    _logger.LogWarning($"[PROC][SKIP] DocId={document.DocId} File={document.Title} Reason=Resource or Company not found NAF={scannedData.NAF} ServiceId={service.ServiceId}");
                    continue;
                }

                // Valida que el nombre de la empresa coincida
                if (scannedData.CompanyName != resource.Company.Name)
                {
                    _logger.LogWarning($"[PROC][SKIP] DocId={document.DocId} File={document.Title} Reason=Company mismatch NAF={scannedData.NAF} Expected={resource.Company.Name} Found={scannedData.CompanyName}");
                    continue;
                }

                // Asigna los identificadores al objeto escaneado
                scannedData.DocId = document.DocId;
                scannedData.ResourceId = resource.ResourceId;

                // Procesa según el tipo de period
                if (scannedData is Rnt rnt)
                {
                    rnt.FiscalYearStart = service.FiscalYearStart;
                    rnt.FiscalYearEnd = service.FiscalYearStart.AddYears(1).AddDays(-1);
                    await ProcessRNT(rnt, isNewDocument, rntPeriodsAdded);
                }
                else if (scannedData is Idc idc)
                {
                    await ProcessIDC(idc, isNewDocument, idcPeriodsAdded);
                }
                else if (scannedData is Vilem vilem)
                {
                    await ProccessVilem(vilem, isNewDocument); //rntTypee, rntPeriodsAdded
                }
                else
                {
                    _logger.LogWarning($"[PROC][SKIP] DocId={document.DocId} File={document.Title} Reason=Unknown scanned data type Template={normalizedTemplate}");
                }
            }
        }

        /// <summary>
        /// Procesa y guarda un periodo RNT en la base de datos, validando los datos y evitando duplicados.
        /// </summary>
        /// <param name="rnt">Datos del periodo RNT.</param>
        /// <param name="isNewDocumnet">Indica si el documento es nuevo.</param>
        /// <param name="rntPeriodsAdded">Diccionario para contar los periodos agregados por tipo.</param>
        private async Task ProcessRNT(Rnt rnt, bool isNewDocumnet, Dictionary<RNTType, int> rntPeriodsAdded)
        {
            _logger.LogInformation($"[RNT][CHECK] DocId={rnt.DocId} ResourceId={rnt.ResourceId} Period={rnt.Period} Qualifier={rnt.Qualifier}");

            // Validación por rango de fechas reales (Start/End)
            if (rnt.StartDate == null)
            {
                _logger.LogWarning($"[RNT][SKIP] DocId={rnt.DocId} ResourceId={rnt.ResourceId} Reason=Missing start date NAF={rnt.NAF}");
                return;
            }
            var startDate = rnt.StartDate.Value;
            var endDate = rnt.EndDate ?? startDate;
            if (endDate < startDate)
            {
                _logger.LogWarning($"[RNT][SKIP] DocId={rnt.DocId} ResourceId={rnt.ResourceId} Reason=Invalid date range NAF={rnt.NAF} Start={startDate:dd/MM/yyyy} End={endDate:dd/MM/yyyy}");
                return;
            }

            bool overlaps = startDate <= rnt.FiscalYearEnd && endDate >= rnt.FiscalYearStart;
            if (!overlaps)
            {
                _logger.LogInformation($"[RNT][SKIP] DocId={rnt.DocId} ResourceId={rnt.ResourceId} Reason=Outside FY Range={startDate:dd/MM/yyyy}-{endDate:dd/MM/yyyy} FY={rnt.FiscalYearStart:dd/MM/yyyy}-{rnt.FiscalYearEnd:dd/MM/yyyy}");
                return;
            }

            if (!LIQUIDATION_QUALIFIERS.Contains(rnt.Qualifier))
            {
                _logger.LogInformation($"[RNT][SKIP] DocId={rnt.DocId} ResourceId={rnt.ResourceId} Reason=Invalid qualifier Qualifier={rnt.Qualifier} NAF={rnt.NAF}");
                return;
            }

            if (!ALLOWED_DESCRIPTION.Contains(rnt.Description))
            {
                _logger.LogInformation($"[RNT][SKIP] DocId={rnt.DocId} ResourceId={rnt.ResourceId} Reason=Invalid description Desc={rnt.Description} NAF={rnt.NAF}");
                return;
            }

            if (rnt.Amount == null)
            {
                _logger.LogWarning($"[RNT][SKIP] DocId={rnt.DocId} ResourceId={rnt.ResourceId} Reason=Missing amount NAF={rnt.NAF}");
                return;
            }

            RntPeriod? rntPeriod = null;
            bool createdNew = false;

            // Dedupe SIEMPRE por documento para evitar duplicados del mismo DocId
            var matchEnd = endDate;
            rntPeriod = await _context.RntPeriods.FirstOrDefaultAsync(r =>
                r.ScannedDocumentId == rnt.DocId &&
                r.ResourceId == rnt.ResourceId &&
                r.StartDate == startDate &&
                r.EndDate == matchEnd &&
                r.Type == rnt.Type);

            if (rntPeriod == null)
            {
                // Fallback: intentar localizar por Resource+Rango+Tipo ignorando DocId
                rntPeriod = await _context.RntPeriods.FirstOrDefaultAsync(r =>
                    r.ResourceId == rnt.ResourceId &&
                    r.StartDate == startDate &&
                    r.EndDate == matchEnd &&
                    r.Type == rnt.Type);
            }

            if (rntPeriod == null)
            {
                rntPeriod = new RntPeriod
                {
                    Id = Guid.NewGuid(),
                    ResourceId = rnt.ResourceId,
                    ScannedDocumentId = rnt.DocId,
                    Type = rnt.Type,
                    Description = rnt.Description
                };
                await _context.RntPeriods.AddAsync(rntPeriod);
                createdNew = true;
                if (!rntPeriodsAdded.ContainsKey(rnt.Type))
                {
                    rntPeriodsAdded[rnt.Type] = 0;
                }
                rntPeriodsAdded[rnt.Type]++;
            }
            else
            {
                _logger.LogInformation($"[RNT][DUP] PeriodId={rntPeriod.Id} DocId={rntPeriod.ScannedDocumentId} ResourceId={rntPeriod.ResourceId}");
            }

            // Calcular Period si no viene o para rangos multi-mes
            string? computedPeriod = rnt.Period;
            if (string.IsNullOrWhiteSpace(computedPeriod))
            {
                computedPeriod = (startDate.Month == endDate.Month && startDate.Year == endDate.Year)
                    ? $"{startDate:MM/yyyy}"
                    : $"{startDate:MM/yyyy}-{endDate:MM/yyyy}";
            }

            rntPeriod.Amount = rnt.Amount;
            rntPeriod.Period = computedPeriod;
            rntPeriod.Qualifier = rnt.Qualifier ?? string.Empty;
            rntPeriod.StartDate = startDate;
            rntPeriod.EndDate = endDate;

            if (!createdNew)
            {
                _context.RntPeriods.Update(rntPeriod);
            }

            _logger.LogInformation($"[RNT][UPSERT] {(createdNew ? "Created" : "Updated")} PeriodId={rntPeriod.Id} Type={rntPeriod.Type} Period={rntPeriod.Period} Qualifier={rntPeriod.Qualifier} Start={rntPeriod.StartDate:dd/MM/yyyy} End={rntPeriod.EndDate:dd/MM/yyyy} Amount={rntPeriod.Amount} Desc={rntPeriod.Description}");
        }

        /// Procesa y guarda un periodo IDC en la base de datos, validando los datos y evitando duplicados.
        /// </summary>
        /// <param name="idc">Datos del periodo IDC.</param>
        /// <param name="isNewDocument">Indica si el documento es nuevo.</param>
        /// <param name="idcPeriodsAdded">Diccionario para contar los periodos agregados por tipo.</param>
        private async Task ProcessIDC(Idc idc, bool isNewDocument, Dictionary<IDCType, int> idcPeriodsAdded)
        {
            if (idc.StartDate == null || idc.EndDate == null)
            {
                _logger.LogWarning($"[IDC][SKIP] DocId={idc.DocId} ResourceId={idc.ResourceId} Reason=Missing start/end date NAF={idc.NAF}");
                return;
            }

            if (!ALLOWED_CODES.Contains((idc.IncentivizedCollective ?? string.Empty).Split(' ')[0]))
            {
                _logger.LogInformation($"[IDC][SKIP] DocId={idc.DocId} ResourceId={idc.ResourceId} Reason=Invalid incentivized collective Collective={idc.IncentivizedCollective} NAF={idc.NAF}");
                return;
            }

            IdcPeriod? idcPeriod = null;
            bool createdNew = false;

            var s = idc.StartDate.Value;
            var e = idc.EndDate.Value;
            // Intento 1: match por DocId+Resource+Fechas+Tipo
            idcPeriod = await _context.IdcPeriods.FirstOrDefaultAsync(r =>
                r.ScannedDocumentId == idc.DocId &&
                r.ResourceId == idc.ResourceId &&
                r.StartDate == s &&
                r.EndDate == e &&
                r.Type == idc.Type);

            // Intento 2 (fallback): match por Resource+Fechas+Tipo (ignorando DocId)
            if (idcPeriod == null)
            {
                idcPeriod = await _context.IdcPeriods.FirstOrDefaultAsync(r =>
                    r.ResourceId == idc.ResourceId &&
                    r.StartDate == s &&
                    r.EndDate == e &&
                    r.Type == idc.Type);
            }

            if (idcPeriod == null)
            {
                idcPeriod = new IdcPeriod
                {
                    Id = Guid.NewGuid(),
                    ResourceId = idc.ResourceId,
                    ScannedDocumentId = idc.DocId,
                    Type = idc.Type
                };
                await _context.IdcPeriods.AddAsync(idcPeriod);
                createdNew = true;
                if (!idcPeriodsAdded.ContainsKey(idc.Type))
                {
                    idcPeriodsAdded[idc.Type] = 0;
                }
                idcPeriodsAdded[idc.Type]++;
            }

            idcPeriod.StartDate = idc.StartDate;
            idcPeriod.EndDate = idc.EndDate;
            idcPeriod.IncentivizedCollective = idc.IncentivizedCollective ?? string.Empty;
            // Asegurar columnas NOT NULL con valores por defecto vacíos
            idcPeriod.GCM = idc.GCM ?? string.Empty;
            idcPeriod.MultipleEmploymentPercentage = idc.MultipleEmploymentPercentage ?? string.Empty;
            idcPeriod.PeculiarityType = idc.PeculiarityType ?? string.Empty;
            idcPeriod.ByType = idc.ByType ?? idc.Type.ToString();
            // Campos informativos opcionales
            idcPeriod.CompanyName = idc.CompanyName ?? idcPeriod.CompanyName;
            idcPeriod.NAF = idc.NAF ?? idcPeriod.NAF;
            idcPeriod.NSS = idc.NSS ?? idcPeriod.NSS;
            idcPeriod.Name = idc.Name ?? idcPeriod.Name;
            idcPeriod.CCC = idc.CCC ?? idcPeriod.CCC;
            idcPeriod.NIF = idc.NIF ?? idcPeriod.NIF;

            if (!createdNew)
            {
                _context.IdcPeriods.Update(idcPeriod);
            }
        }

        private async Task ProccessVilem(Vilem vilem, bool isNewDocument) //RNTType type, Dictionary<RNTType, int> rntPeriodsAdded
        {
            Affiliation? affiliation = null;
            bool createdNew = false;
            if (!isNewDocument)
            {
                affiliation = await _context.Affiliations.FirstOrDefaultAsync(a => a.ScannedDocumentId == vilem.DocId);
            }

            if (affiliation == null)
            {
                affiliation = new Affiliation
                {
                    Id = Guid.NewGuid(),
                    ResourceId = vilem.ResourceId,
                    ScannedDocumentId = vilem.DocId
                };
                await _context.Affiliations.AddAsync(affiliation);
                createdNew = true;
            }

            affiliation.NAF = vilem.NAF;
            affiliation.CompanyName = vilem.CompanyName;
            affiliation.Number = GenerateSequentialContractNumber(vilem.ResourceId);
            affiliation.ContractTypeId = vilem.ContractTypeId;
            affiliation.StartDate = vilem.StartDate;
            affiliation.EndDate = vilem.EndDate;
            affiliation.WorkingDayType = vilem.WorkingDayType;
            affiliation.CTP = vilem.CTP;

            if (!createdNew)
            {
                _context.Affiliations.Update(affiliation);
            }
            _logger.LogInformation($"[VILEM][UPSERT] DocId={vilem.DocId} ResourceId={vilem.ResourceId} Number={affiliation.Number} TypeId={affiliation.ContractTypeId} Start={affiliation.StartDate:dd/MM/yyyy} End={affiliation.EndDate:dd/MM/yyyy} WorkingDay={affiliation.WorkingDayType} CTP={affiliation.CTP}");
        }

        /// <summary>
        /// Parsea y adapta un item de datos escaneados según la plantilla.'
        /// </summary>
        public IScannedData? ParseAndAdaptScannedDataItem(JObject scannedDataItem, string normalizedTemplate)
        {
            if (scannedDataItem == null || string.IsNullOrEmpty(normalizedTemplate))
            {
                _logger.LogWarning("Scanned data item or template is null or empty.");
                return null;
            }

            // Según la plantilla, extrae los campos relevantes y crea el objeto correspondiente
            switch (normalizedTemplate)
            {
                case "idcpl":
                    {
                        var NAF = scannedDataItem["NSS"]?.ToString()?.Replace(" ", "") ?? string.Empty;
                        var CompanyName = scannedDataItem["Razón social"]?.ToString() ?? string.Empty;
                        var FechaDesde = GetDateTimeValue(scannedDataItem["Fecha desde"]?.ToString());
                        var FechaHasta = GetDateTimeValue(scannedDataItem["Fecha hasta"]?.ToString());
                        var ColectivoIncentivado = scannedDataItem["Colectivo incentivado"]?.ToString() ?? string.Empty;
                        var GCM = scannedDataItem["GC/M"]?.ToString() ?? string.Empty;
                        var MultipleEmploymentPercentage = scannedDataItem["% Pluriempleo"]?.ToString() ?? string.Empty;
                        var PeculiarityType = scannedDataItem["Tipo de peculiaridad"]?.ToString() ?? string.Empty;
                        var ByType = scannedDataItem["Caso Especial"]?.ToString() ?? string.Empty;
                        return new Idc
                        {
                            NAF = NAF,
                            CompanyName = CompanyName,
                            StartDate = FechaDesde,
                            EndDate = FechaHasta,
                            IncentivizedCollective = ColectivoIncentivado,
                            GCM = GCM,
                            MultipleEmploymentPercentage = MultipleEmploymentPercentage,
                            PeculiarityType = PeculiarityType,
                            ByType = ByType,
                            Type = IDCType.PL,
                        };
                    }
                case "idcrl":
                    {
                        var NAF = scannedDataItem["NÚMERO SEGURIDAD SOCIAL"]?.ToString()?.Replace(" ", "") ?? string.Empty;
                        var CompanyName = scannedDataItem["RAZÓN SOCIAL"]?.ToString() ?? string.Empty;
                        var FechaDesde = GetDateTimeValue(scannedDataItem["FECHA DESDE P."]?.ToString());
                        var FechaHasta = GetDateTimeValue(scannedDataItem["FECHA HASTA P."]?.ToString());
                        var ColectivoIncentivado = scannedDataItem["COLECTIVO INCENTIVADO"]?.ToString() ?? string.Empty;
                        var GCM = scannedDataItem["GC/M"]?.ToString() ?? string.Empty;
                        var MultipleEmploymentPercentage = scannedDataItem["% Pluriempleo"]?.ToString() ?? string.Empty;
                        var PeculiarityType = scannedDataItem["TIPO DE PECULIARIDAD"]?.ToString() ?? string.Empty;
                        return new Idc
                        {
                            NAF = NAF,
                            CompanyName = CompanyName,
                            StartDate = FechaDesde,
                            EndDate = FechaHasta,
                            IncentivizedCollective = ColectivoIncentivado,
                            GCM = GCM,
                            MultipleEmploymentPercentage = MultipleEmploymentPercentage,
                            PeculiarityType = PeculiarityType,
                            Type = IDCType.RL,
                        };
                    }
                case "rntglobal":
                    {
                        var NAF = scannedDataItem["NAF"]?.ToString()?.Replace(" ", "") ?? string.Empty;
                        var CompanyName = scannedDataItem["Razón social"]?.ToString() ?? string.Empty;
                        var PeriodoLiquidacion = scannedDataItem["Período de liquidación"]?.ToString() ?? string.Empty;
                        var CalificadorLiquidacion = scannedDataItem["Calificador de la liquidación"]?.ToString() ?? string.Empty;
                        var FechaInicio = GetDateTimeValue(scannedDataItem["Fecha Inicio"]?.ToString());
                        var FechaFin = GetDateTimeValue(scannedDataItem["Fecha Fin"]?.ToString());
                        var Importe = GetDecimalValue(scannedDataItem["Importe"]?.ToString());
                        var Descripcion = scannedDataItem["Descripción"]?.ToString() ?? string.Empty;
                        return new Rnt
                        {
                            NAF = NAF,
                            CompanyName = CompanyName,
                            Period = PeriodoLiquidacion,
                            Qualifier = CalificadorLiquidacion,
                            StartDate = FechaInicio,
                            EndDate = FechaFin,
                            Amount = Importe,
                            Description = Descripcion,
                            Type = RNTType.Global
                        };
                    }
                case "rntindividual":
                    {
                        var NAF = scannedDataItem["NAF"]?.ToString()?.Replace(" ", "") ?? string.Empty;
                        var CompanyName = scannedDataItem["Razón social"]?.ToString() ?? string.Empty;
                        var PeriodoLiquidacion = scannedDataItem["Período de Liquidación"]?.ToString() ?? string.Empty;
                        var CalificadorLiquidacion = scannedDataItem["Calificador de la Liquidación"]?.ToString() ?? string.Empty;
                        var FechasTramoDesde = GetDateTimeValue(scannedDataItem["Fechas Tramo Desde"]?.ToString());
                        var FechasTramoHasta = GetDateTimeValue(scannedDataItem["Fechas Tramo Hasta"]?.ToString());
                        var Importe = GetDecimalValue(scannedDataItem["Importe"]?.ToString());
                        var Descripcion = scannedDataItem["Descripción"]?.ToString() ?? string.Empty;
                        return new Rnt
                        {
                            NAF = NAF,
                            CompanyName = CompanyName,
                            Period = PeriodoLiquidacion,
                            Qualifier = CalificadorLiquidacion,
                            StartDate = FechasTramoDesde,
                            EndDate = FechasTramoHasta,
                            Amount = Importe,
                            Description = Descripcion,
                            Type = RNTType.Individual
                        };
                    }
                case "vilem":
                    {
                        var naf = scannedDataItem["N�MERO AFILICACI�N"]?.ToString().Replace(" ", "") ?? string.Empty;
                        string companyName = scannedDataItem["RAZ�N SOCIAL"]?.ToString() ?? string.Empty;
                        string situation = scannedDataItem["SITUACI�N"]?.ToString() ?? string.Empty;

                        DateTime? startDate = null, endDate = null;
                        if (situation == "ALTA") startDate = GetDateTimeValue(scannedDataItem["F.EFECTO ALTA"]?.ToString());
                        else if (situation == "BAJA") endDate = GetDateTimeValue(scannedDataItem["F.EFECTO ALTA"]?.ToString());

                        int? contractType = GetIntValue(scannedDataItem["T.C"]?.ToString());
                        string workinDay = scannedDataItem["G. C/M"]?.ToString() ?? string.Empty;
                        WorkingDayType workingDayType;

                        if (string.IsNullOrEmpty(workinDay))
                            workingDayType = WorkingDayType.FullTime;
                        else
                            workingDayType = WorkingDayType.Partial;

                        decimal? ctp = GetDecimalValue(scannedDataItem["C.T.P."]?.ToString());

                        return new Vilem
                        {
                            NAF = naf,
                            CompanyName = companyName,
                            Number = 1,
                            ContractTypeId = contractType ?? 100,
                            StartDate = startDate,
                            EndDate = endDate,
                            WorkingDayType = workingDayType,
                            CTP = ctp
                        };
                    }
                default:
                    {
                        _logger.LogWarning($"[PARSE][SKIP] Reason=Unsupported template Template={normalizedTemplate}");
                        return null;
                    }
            }
        }

        /// <summary>
        /// Actualiza el token de continuación para la sincronización incremental de documentos.
        /// </summary>
        /// <param name="token">Token de continuación recibido de OneData.</param>
        private async Task UpdateContinuationToken(string? token)
        {
            if (string.IsNullOrEmpty(token)) return;
            var lastConfig = await _context.OneDataSyncSettings.FirstOrDefaultAsync(t => t.Code == OneDataDatasets.Documents);
            if (lastConfig == null)
            {
                lastConfig = new OneDataSyncSettings()
                {
                    Code = OneDataDatasets.Documents,
                    ContinuationToken = token
                };
                await _context.OneDataSyncSettings.AddAsync(lastConfig);
            }
            else
            {
                lastConfig.ContinuationToken = token;
                // Con AutoDetectChanges desactivado, marcamos explícitamente como modificado
                _context.Entry(lastConfig).State = EntityState.Modified;
            }
            await _context.SaveChangesAsync();
        }

        /// <summary>
        /// Convierte un string a decimal, eliminando símbolos de moneda y adaptando el formato.
        /// </summary>
        /// <param name="value">Cadena con el valor decimal (puede incluir símbolo de moneda, puntos o comas).</param>
        /// <returns>El valor decimal si es válido, o null si no se puede convertir.</returns>
        private static decimal? GetDecimalValue(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            var symbol = @"[\p{Sc}]"; //@"[\p{Sc}\s] // Expresión regular para símbolos de moneda

            // Elimina símbolos, puntos, cambia comas por puntos y toma el primer fragmento
            var aux = value.Replace(symbol, "").Replace(".", "").Replace(",", ".").Trim().Split(' ')[0];

            if (decimal.TryParse(aux, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out decimal result))
            {
                return result;
            }
            else
            {
                // Nota: no lanzar; el caller decidirá si es bloqueo o no
                return null;
            }
        }

        /// <summary>
        /// Convierte un string a DateTime usando el formato "dd-MM-yyyy".
        /// </summary>
        /// <param name="value">Cadena con la fecha.</param>
        /// <returns>El valor DateTime si es válido, o null si no se puede convertir.</returns>
        private static DateTime? GetDateTimeValue(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;

            string format = "dd-MM-yyyy";
            if (DateTime.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime result))
            {
                return result;
            }
            else
            {
                // Intento adicional con separador '/'
                if (DateTime.TryParseExact(value, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out result))
                    return result;
                return null;
            }
        }

        /// <summary>  
        /// Generates a sequential contract number based on the existing contracts in the database.  
        /// </summary>  
        private int GenerateSequentialContractNumber(Guid resourceId)
        {
            int maxContractNumber = 0;
            // Fetch the highest existing contract number from the database  
            maxContractNumber = _context.Affiliations
                .Where(r => r.ResourceId == resourceId)
                .OrderByDescending(rc => rc.Number)
                .Select(rc => rc.Number)
                .FirstOrDefault();

            // Increment the highest contract number to generate the next one  
            return maxContractNumber + 1;
        }

        /// <summary>  
        /// Convierte un string a int.  
        /// </summary>    
        private static int? GetIntValue(string? value)
        {
            if (string.IsNullOrEmpty(value)) return null;

            if (int.TryParse(value, out int result))
            {
                return result;
            }
            else
            {
                return null;
            }
        }

        /// <summary>
        /// Interfaz para los datos escaneados genéricos (RNT o IDC).
        /// </summary>
        public interface IScannedData
        {
            Guid DocId { get; set; }
            Guid ResourceId { get; set; }
            string? NAF { get; }
            string? CompanyName { get; }
        }

        /// <summary>
        /// Modelo de datos para un periodo IDC extraído de un documento escaneado.
        /// </summary>
        public class Idc : IScannedData
        {
            public Guid DocId { get; set; }
            public Guid ResourceId { get; set; }
            public string? NAF { get; set; }
            public string? CompanyName { get; set; }
            public DateTime? StartDate { get; set; }
            public DateTime? EndDate { get; set; }
            public string? IncentivizedCollective { get; set; }
            // Campos adicionales mapeados a IdcPeriod
            public string? GCM { get; set; }
            public string? MultipleEmploymentPercentage { get; set; }
            public string? PeculiarityType { get; set; }
            public string? ByType { get; set; }
            public string? NSS { get; set; }
            public string? Name { get; set; }
            public string? CCC { get; set; }
            public string? NIF { get; set; }
            public IDCType Type { get; set; }
        }

        /// <summary>
        /// Modelo de datos para un periodo RNT extraído de un documento escaneado.
        /// </summary>
        public class Rnt : IScannedData
        {
            public Guid DocId { get; set; }
            public Guid ResourceId { get; set; }
            public string? NAF { get; set; }
            public string? CompanyName { get; set; }
            public string? Period { get; set; }
            public string? Qualifier { get; set; }
            public DateTime? StartDate { get; set; }
            public DateTime? EndDate { get; set; }
            public decimal? Amount { get; set; }
            public string? Description { get; set; }
            public RNTType Type { get; set; }
            public DateTime FiscalYearStart { get; set; }
            public DateTime FiscalYearEnd { get; set; }
        }
        /// <summary>
        /// Modelo de datos para un VILEM extraído de un documento escaneado.
        /// </summary>
        public class Vilem : IScannedData
        {
            public Guid DocId { get; set; }
            public Guid ResourceId { get; set; }
            public string? NAF { get; set; }
            public string? CompanyName { get; set; }
            public int Number { get; set; }
            public int ContractTypeId { get; set; }
            public DateTime? StartDate { get; set; }
            public DateTime? EndDate { get; set; }
            public WorkingDayType WorkingDayType { get; set; }
            public decimal? CTP { get; set; }

        }
    }
}
