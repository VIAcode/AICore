using System.Text;
using System.Web;
using Microsoft.SemanticKernel;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Common;
using AiCoreApi.Common.Extensions;
using AiCoreApi.Data.Processors;
using Azure.Core.Pipeline;
using Azure;
using Azure.AI.DocumentIntelligence;
using Newtonsoft.Json;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class OcrAgent : BaseAgent, IOcrAgent
    {
        private const string DebugMessageSenderName = "OcrAgent";
        public static class AgentPromptPlaceholders
        {
            public const string FileDataPlaceholder = "firstFileData";
        }

        private static class AgentContentParameters
        {
            public const string Output = "outputOptions";
            public const string OutputFormat = "outputFormat";
            public const string Options = "options";
            public const string DocumentIntelligenceConnection = "documentIntelligenceConnection";
            public const string Base64Image = "base64Image";
        }

        private static class AgentOptions
        {
            public const string Barcodes = "barcodes";
            public const string Formulas = "formulas";
            public const string KeyValuePairs = "keyvaluepairs";
            public const string HighResolution = "highresolution";
            public const string Markdown = "markdown";
            public const string FigureImages = "figureimages";
        }

        private static class AgentOutputOptions
        {
            public const string WordMiddle = "wordmiddle";
            public const string WordArea = "wordarea";
            public const string TextMiddle = "textmiddle";
            public const string TextArea = "textarea";
            public const string Content = "content";
            public const string BarCodes = "barcodes";
            public const string Formulas = "formulas";
            public const string Images = "images";
            public const string Fields = "fields";
            public const string Confidence = "confidence";
        }

        private static class AgentOutputFormat
        {
            public const string Text = "text";
            public const string Markdown = "markdown";
        }

        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConnectionProcessor _connectionProcessor;
        private readonly ILogger<OcrAgent> _logger;

        public OcrAgent(
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            IHttpClientFactory httpClientFactory,
            IConnectionProcessor connectionProcessor,
            ILogger<OcrAgent> logger)
        {
            _requestAccessor = requestAccessor;
            _responseAccessor = responseAccessor;
            _httpClientFactory = httpClientFactory;
            _connectionProcessor = connectionProcessor;
            _logger = logger;
        }

        public override async Task<string> DoCall(
            AgentModel agent, 
            Dictionary<string, string> parameters)
        {
            parameters.ToList().ForEach(p => parameters[p.Key] = HttpUtility.HtmlDecode(p.Value));

            var base64Image = ApplyParameters(agent.Content[AgentContentParameters.Base64Image].Value, parameters);
            if (_requestAccessor.MessageDialog != null && _requestAccessor.MessageDialog.Messages!.Last().HasFiles() && base64Image.Contains(AgentPromptPlaceholders.FileDataPlaceholder))
            {
                base64Image = ApplyParameters(base64Image, new Dictionary<string, string>
                {
                    {AgentPromptPlaceholders.FileDataPlaceholder, _requestAccessor.MessageDialog.Messages!.Last().Files!.First().Base64Data},
                });
            }

            var documentIntelligenceConnection = agent.Content[AgentContentParameters.DocumentIntelligenceConnection].Value;
            var connections = await _connectionProcessor.List();
            var connection = GetConnection(_requestAccessor, _responseAccessor, connections, ConnectionType.DocumentIntelligence, DebugMessageSenderName, connectionName: documentIntelligenceConnection);

            var optionsList = !agent.Content.ContainsKey(AgentContentParameters.Options)
                ? new List<string>()
                : agent.Content[AgentContentParameters.Options].Value.Split('|').Select(item => item.ToLower()).ToList();
            var outputList = !agent.Content.ContainsKey(AgentContentParameters.Output)
                ? new List<string>()
                : agent.Content[AgentContentParameters.Output].Value.Split('|').Select(item => item.ToLower()).ToList();
            var outputFormat = !agent.Content.ContainsKey(AgentContentParameters.OutputFormat)
                ? "text"
                : agent.Content[AgentContentParameters.OutputFormat].Value.ToLower();

            var endpoint = connection.Content["endpoint"];
            var modelName = connection.Content["modelName"];
            var apiKey = connection.Content["apiKey"];
            _responseAccessor.AddDebugMessage(DebugMessageSenderName, "OCR Processing", $"{endpoint}, {modelName}, {apiKey} \r\nFormat: {outputFormat} Options:{string.Join(", ", optionsList)} Output:{string.Join(", ", outputList)}");
            var result = await ProcessFile(endpoint, modelName, apiKey, optionsList, outputList, outputFormat, base64Image.StripBase64());
            _responseAccessor.AddDebugMessage(DebugMessageSenderName, "OCR Processing Result", result);

            _logger.LogInformation("{Login}, Action:{Action}, ConnectionName: {ConnectionName}, Options: {Options}, Output: {Output}",
                _requestAccessor.Login, "Ocr", connection.Name, string.Join(",", optionsList), string.Join(",", outputList));
            return result;
        }


        private async Task<string?> ProcessFile(string modelUrl, string modelName, string apiKey, List<string> optionsList, List<string> outputList, string outputFormat, string base64Data)
        {
            var file = Convert.FromBase64String(base64Data);

            var clientOptions = new DocumentIntelligenceClientOptions
            {
                Transport = new HttpClientTransport(_httpClientFactory.CreateClient("RetryClient"))
            };
            var client = new DocumentIntelligenceClient(new Uri(modelUrl), new AzureKeyCredential(apiKey), clientOptions);
            var content = new AnalyzeDocumentContent
            {
                Base64Source = BinaryData.FromBytes(file),
            };

            var features = new List<DocumentAnalysisFeature>();
            if (optionsList.Contains(AgentOptions.Barcodes))
                features.Add(DocumentAnalysisFeature.Barcodes);
            if (optionsList.Contains(AgentOptions.Formulas))
                features.Add(DocumentAnalysisFeature.Formulas);
            if (optionsList.Contains(AgentOptions.KeyValuePairs))
                features.Add(DocumentAnalysisFeature.QueryFields);
            if (optionsList.Contains(AgentOptions.HighResolution))
                features.Add(DocumentAnalysisFeature.OcrHighResolution);

            var outputContentFormat = "text";
            if (optionsList.Contains(AgentOptions.Markdown))
                outputContentFormat = "markdown";

            var useConfidence = outputList.Contains(AgentOutputOptions.Confidence);

            var output = new List<AnalyzeOutputOption>();
            if (optionsList.Contains(AgentOptions.FigureImages))
                output.Add(AnalyzeOutputOption.Figures);

            var operation = await client.AnalyzeDocumentAsync(WaitUntil.Completed, modelName, content, features: features, outputContentFormat: outputContentFormat, output: output);
            var result = operation.Value;

            var ocrResult = new OcrResult();
            // Content
            if (outputList.Contains(AgentOutputOptions.Content))
            {
                ocrResult.Content = result.Content;
            }
            if (outputList.Contains(AgentOutputOptions.WordArea)
                || outputList.Contains(AgentOutputOptions.WordMiddle)
                || outputList.Contains(AgentOutputOptions.TextArea)
                || outputList.Contains(AgentOutputOptions.TextMiddle)
                || outputList.Contains(AgentOutputOptions.BarCodes)
                || outputList.Contains(AgentOutputOptions.Formulas)
                || outputList.Contains(AgentOutputOptions.Images)
                || outputList.Contains(AgentOutputOptions.Fields))
            {
                ocrResult.Pages = new List<Page>();
                foreach (var ocrPage in result.Pages)
                {
                    var page = new Page();
                    ocrResult.Pages.Add(page);
                    // WordArea
                    if (outputList.Contains(AgentOutputOptions.WordArea))
                    {
                        page.WordArea = GetWordArea(ocrPage, useConfidence);
                    }
                    // WordMiddle
                    if (outputList.Contains(AgentOutputOptions.WordMiddle))
                    {
                        page.WordMiddle = GetWordMiddle(ocrPage, useConfidence);
                    }
                    // TextArea
                    if (outputList.Contains(AgentOutputOptions.TextArea))
                    {
                        page.TextArea = GetTextArea(ocrPage);
                    }
                    // TextMiddle
                    if (outputList.Contains(AgentOutputOptions.TextMiddle))
                    {
                        page.TextMiddle = GetTextMiddle(ocrPage);
                    }
                    // BarCodes
                    if (outputList.Contains(AgentOutputOptions.BarCodes))
                    {
                        page.BarCodes = GetBarCodes(ocrPage, useConfidence);
                    }
                    // Formulas
                    if (outputList.Contains(AgentOutputOptions.Formulas))
                    {
                        page.Formulas = GetFormulas(ocrPage, useConfidence);
                    }
                }
                // Images
                if (outputList.Contains(AgentOutputOptions.Images))
                {
                    foreach (var figure in result.Figures)
                    {
                        var pageId = figure.BoundingRegions[0].PageNumber - 1;
                        var ocrPage = result.Pages[pageId];
                        var page = ocrResult.Pages[pageId];
                        var location = PolygonToPointList(ocrPage, figure.BoundingRegions[0].Polygon, (int)ocrPage.Angle);
                        if (page.Images == null)
                            page.Images = new List<ImageItem>();
                        var image = client.GetAnalyzeResultFigure(result.ModelId, new Guid(operation.Id), figure.Id);
                        var base64Image = Convert.ToBase64String(image.Value.ToArray());
                        page.Images.Add(new ImageItem
                        {
                            Base64 = base64Image,
                            Location = location,
                        });
                    }
                }
                // Fields
                if (outputList.Contains(AgentOutputOptions.Fields))
                {
                    foreach (var keyValuePair in result.KeyValuePairs)
                    {
                        var pageId = keyValuePair.Key.BoundingRegions[0].PageNumber - 1;
                        var ocrPage = result.Pages[pageId];
                        var page = ocrResult.Pages[pageId];
                        var location = PolygonToPointList(ocrPage, keyValuePair.Key.BoundingRegions[0].Polygon, (int)ocrPage.Angle);
                        if (page.Fields == null)
                            page.Fields = new List<Field>();
                        page.Fields.Add(new Field
                        {
                            Key = keyValuePair.Key.Content,
                            Value = keyValuePair.Value.Content,
                            Location = location,
                            Confidence = useConfidence ? keyValuePair.Confidence : null,
                        });
                    }
                }
            }

            if (outputFormat == AgentOutputFormat.Markdown)
            {
                return GenerateMarkdown(ocrResult);
            }
            return ocrResult.ToJson();
        }

        private string GenerateMarkdown(OcrResult ocrResult)
        {
            var markdown = new StringBuilder();
            if (!string.IsNullOrEmpty(ocrResult.Content))
            {
                markdown.AppendLine("## Content");
                markdown.AppendLine(ocrResult.Content);
                markdown.AppendLine("");
            }
            if (ocrResult.Pages != null)
            {
                var pageId = 1; 
                foreach (var page in ocrResult.Pages)
                {
                    markdown.AppendLine($"## Page {pageId++}");
                    if (page.TextArea != null && page.TextArea.Count > 0)
                    {
                        foreach (var textArea in page.TextArea)
                        {
                            markdown.Append($"(");
                            foreach (var point in textArea.Location)
                            {
                                markdown.Append($"{point.X},{point.Y}; ");
                            }
                            markdown.Append($"): ");
                            markdown.AppendLine(textArea.Content);
                        }
                    }
                    if (page.TextMiddle != null && page.TextMiddle.Count > 0)
                    {
                        foreach (var textMiddle in page.TextMiddle)
                        {
                            markdown.Append($"({textMiddle.Location.X}, {textMiddle.Location.Y}): ");
                            markdown.AppendLine(textMiddle.Content);
                        }
                    }
                    if (page.BarCodes != null && page.BarCodes.Count > 0)
                    {
                        markdown.AppendLine($"### BarCodes");
                        foreach (var barCode in page.BarCodes)
                        {
                            markdown.Append($"(");
                            foreach (var point in barCode.Location)
                            {
                                markdown.Append($"{point.X},{point.Y}; ");
                            }
                            markdown.AppendLine($"): ");
                            markdown.AppendLine($"{barCode.Kind}: {barCode.Value}");
                        }
                    }
                    if (page.Formulas != null && page.Formulas.Count > 0)
                    {
                        markdown.AppendLine($"### Formulas");
                        foreach (var formula in page.Formulas)
                        {
                            markdown.Append($"(");
                            foreach (var point in formula.Location)
                            {
                                markdown.Append($"{point.X},{point.Y}; ");
                            }
                            markdown.AppendLine($"): ");
                            markdown.AppendLine(formula.Value);
                        }
                    }
                    if (page.Images != null && page.Images.Count > 0)
                    {
                        markdown.AppendLine($"### Images");
                        foreach (var image in page.Images)
                        {
                            markdown.Append($"(");
                            foreach (var point in image.Location)
                            {
                                markdown.Append($"{point.X},{point.Y}; ");
                            }
                            markdown.AppendLine($"): ");
                            markdown.AppendLine(image.Base64);
                        }
                    }
                    if (page.Fields != null && page.Fields.Count > 0)
                    {
                        markdown.AppendLine($"### Fields");
                        foreach (var field in page.Fields)
                        {
                            markdown.Append($"(");
                            foreach (var point in field.Location)
                            {
                                markdown.Append($"{point.X},{point.Y}; ");
                            }
                            markdown.AppendLine($"): ");
                            markdown.AppendLine($"[{field.Key}]={field.Value}");
                        }
                    }
                    markdown.AppendLine("");
                }
            }
            return markdown.ToString();
        }

        private List<ContentPointItem> GetWordMiddle(DocumentPage ocrPage, bool useConfidence)
        {
            var textMiddle = new List<ContentPointItem>();
            foreach (var word in ocrPage.Words)
            {
                var points = PolygonToPointList(ocrPage, word.Polygon, (int)ocrPage.Angle);
                textMiddle.Add(new ContentPointItem
                {
                    Content = word.Content,
                    Location = GetMiddlePoint(points),
                    Confidence = useConfidence ? word.Confidence : null,
                });
            }
            textMiddle = textMiddle.OrderByDescending(item => item.Location.Y).ToList();
            return textMiddle;
        }

        private List<ContentAreaItem> GetWordArea(DocumentPage ocrPage, bool useConfidence)
        {
            var textAreas = new List<ContentAreaItem>();
            foreach (var word in ocrPage.Words)
            {
                var points = PolygonToPointList(ocrPage, word.Polygon, (int)ocrPage.Angle);
                textAreas.Add(new ContentAreaItem
                {
                    Content = word.Content,
                    Location = points,
                    Confidence = useConfidence ? word.Confidence : null,
                });
            }
            textAreas = textAreas.OrderByDescending(item => item.Location.Sum(l => l.Y)).ToList();
            return textAreas;
        }

        private List<ContentPointItem> GetTextMiddle(DocumentPage ocrPage)
        {
            var textMiddle = new List<ContentPointItem>();
            foreach (var line in ocrPage.Lines)
            {
                var points = PolygonToPointList(ocrPage, line.Polygon, (int)ocrPage.Angle);
                textMiddle.Add(new ContentPointItem
                {
                    Content = line.Content,
                    Location = GetMiddlePoint(points),
                });
            }
            textMiddle = textMiddle.OrderByDescending(item => item.Location.Y).ToList();
            return textMiddle;
        }

        private List<ContentAreaItem> GetTextArea(DocumentPage ocrPage)
        {
            var textAreas = new List<ContentAreaItem>();
            foreach (var line in ocrPage.Lines)
            {
                var points = PolygonToPointList(ocrPage, line.Polygon, (int)ocrPage.Angle);
                textAreas.Add(new ContentAreaItem
                {
                    Content = line.Content,
                    Location = points,
                });
            }
            textAreas = textAreas.OrderByDescending(item => item.Location.Sum(l => l.Y)).ToList();
            return textAreas;
        }

        private List<BarCodeItem> GetBarCodes(DocumentPage ocrPage, bool useConfidence)
        {
            var barCodes = new List<BarCodeItem>();
            foreach (var barcode in ocrPage.Barcodes)
            {
                var points = PolygonToPointList(ocrPage, barcode.Polygon, (int)ocrPage.Angle);
                barCodes.Add(new BarCodeItem
                {
                    Kind = barcode.Kind.ToString(),
                    Value = barcode.Value,
                    Location = points,
                    Confidence = useConfidence ? barcode.Confidence : null,
                });
            }
            return barCodes;
        }

        private List<FormulaItem> GetFormulas(DocumentPage ocrPage, bool useConfidence)
        {
            var formulas = new List<FormulaItem>();
            foreach (var formula in ocrPage.Formulas)
            {
                var points = PolygonToPointList(ocrPage, formula.Polygon, (int)ocrPage.Angle);
                formulas.Add(new FormulaItem
                {
                    Value = formula.Value,
                    Location = points,
                    Confidence = useConfidence ? formula.Confidence : null,
                });
            }
            return formulas;
        }

        private PointItem GetMiddlePoint(List<PointItem> points)
        {
            var x = points.Sum(point => point.X) / points.Count;
            var y = points.Sum(point => point.Y) / points.Count;
            return new PointItem
            {
                X = x,
                Y = y
            };
        }

        private PointItem RotatePoint(PointItem contentPointItem, int angle, int centerX, int centerY)
        {
            if (angle < 0)
                angle = 360 + angle;
            var x = contentPointItem.X - centerX;
            var y = contentPointItem.Y - centerY;
            while (angle >= 45)
            {
                var temp = x;
                x = -y;
                y = temp;
                angle -= 90;
            }
            contentPointItem.X = x + centerX;
            contentPointItem.Y = y + centerY;
            return contentPointItem;
        }

        private List<PointItem> PolygonToPointList(DocumentPage documentPage, IReadOnlyList<float> polygon, int angle)
        {
            var centerX = (int)documentPage.Width / 2;
            var centerY = (int)documentPage.Height / 2;
            var points = new List<PointItem>();
            for (var j = 0; j < polygon.Count; j += 2)
            {
                points.Add(RotatePoint(new PointItem
                {
                    X = Convert.ToInt32(polygon[j]),
                    Y = Convert.ToInt32(polygon[j + 1]),
                }, angle, centerX, centerY));
            }
            return points;
        }

        public class OcrResult
        {
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string? Content { get; set; } = string.Empty;
            public List<Page>? Pages { get; set; } = new();
        }
        public class Page
        {
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public List<ContentPointItem>? WordMiddle { get; set; }
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public List<ContentAreaItem>? WordArea { get; set; }
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public List<ContentPointItem>? TextMiddle { get; set; }
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public List<ContentAreaItem>? TextArea { get; set; }
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string? Markdown { get; set; }
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public List<BarCodeItem>? BarCodes { get; set; }
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public List<FormulaItem>? Formulas { get; set; }
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public List<ImageItem>? Images { get; set; }
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public List<Field>? Fields { get; set; }
        }
        public class Field
        {
            public string Key { get; set; }
            public string Value { get; set; }
            public List<PointItem> Location { get; set; } = new();
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public double? Confidence { get; set; }
        }
        public class ImageItem
        {
            public string Base64 { get; set; } = string.Empty;
            public List<PointItem> Location { get; set; } = new();
        }
        public class FormulaItem
        {
            public string Value { get; set; } = string.Empty;
            public List<PointItem> Location { get; set; } = new();
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public double? Confidence { get; set; }
        }
        public class BarCodeItem
        {
            public string Kind { get; set; } = string.Empty;
            public string Value { get; set; } = string.Empty;
            public List<PointItem> Location { get; set; } = new();
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public double? Confidence { get; set; }
        }
        public class ContentAreaItem
        {
            public string Content { get; set; } = string.Empty;
            public List<PointItem> Location { get; set; } = new();
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public double? Confidence { get; set; }
        }
        public class ContentPointItem
        {
            public string Content { get; set; } = string.Empty;
            public PointItem Location { get; set; } = new();
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public double? Confidence { get; set; }
        }
        public class PointItem
        {
            public int X { get; set; }
            public int Y { get; set; }
        }
    }

    public interface IOcrAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
    }
}