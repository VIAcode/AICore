using Microsoft.SemanticKernel;
using AiCoreApi.Models.DbModels;
using System.Net.Http.Headers;
using AiCoreApi.Common;
using System.Web;
using AiCoreApi.Data.Processors;
using AiCoreApi.Common.Extensions;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class StabilityAiImagesAgent : BaseAgent, IStabilityAiImagesAgent
    {
        private string _debugMessageSenderName = "StabilityAiImagesAgent";

        private static class AgentContentParameters
        {
            public const string StabilityAiConnectionName = "stabilityAiConnectionName";
            public const string ImageProcessingType = "imageProcessingType";
            public const string GenerationService = "generationService";
            public const string UpscaleService = "upscaleService";
            public const string EditService = "editService";
            public const string ControlService = "controlService";

            // StableImageUltra, StableImageCore, StableDiffusion35
            public const string GeneratePrompt = "generatePrompt";
            // StableImageUltra, StableDiffusion35
            public const string GenerateInputImageBase64 = "generateInputImageBase64";
            // StableImageUltra, StableImageCore, StableDiffusion35
            public const string GeneratePromptNegative = "generatePromptNegative";
            // StableImageUltra, StableImageCore, StableDiffusion35
            public const string GenerateAspectRatio = "generateAspectRatio";
            // StableImageUltra, StableImageCore, StableDiffusion35
            public const string GenerateStylePreset = "generateStylePreset";
            // StableImageUltra, StableImageCore, StableDiffusion35
            public const string GenerateSeed = "generateSeed";
            // StableImageUltra, StableDiffusion35
            public const string GenerateStrength = "generateStrength";
            // StableDiffusion35
            public const string GenerateMode = "generateMode";
            // StableDiffusion35
            public const string GenerateModel = "generateModel";
            
            // Conservative, Fast
            public const string UpscaleInputImageBase64 = "upscaleInputImageBase64";
            // Conservative
            public const string UpscalePrompt = "upscalePrompt";
            // Conservative
            public const string UpscalePromptNegative = "upscalePromptNegative";
            // Conservative
            public const string UpscaleCreativity = "upscaleCreativity";
            

            // RemoveBackground, Outpaint, SearchAndReplace, SearchAndRecolor, 
            public const string EditInputImageBase64 = "editInputImageBase64";
            // Outpaint
            public const string EditOutpaintDirection = "editOutpaintDirection";
            // SearchAndReplace, SearchAndRecolor
            public const string EditPromptToReplace = "editPromptToReplace";
            // SearchAndReplace, SearchAndRecolor
            public const string EditPromptToAdd = "editPromptToAdd";
            // SearchAndReplace, SearchAndRecolor
            public const string EditPromptNegative = "editPromptNegative";

            // Structure, Sketch, Style
            public const string ControlInputImageBase64 = "controlInputImageBase64";
            // Structure, Sketch, Style
            public const string ControlPrompt = "controlPrompt";
            // Structure, Sketch
            public const string ControlStrength = "controlStrength";
            // Structure, Sketch, Style
            public const string ControlPromptNegative = "controlPromptNegative";
            // Structure, Sketch, Style
            public const string ControlStylePreset = "controlStylePreset";
            // Style
            public const string ControlAspectRatio = "controlAspectRatio";
            // Style
            public const string ControlFidelity = "controlFidelity"; 
        }

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ResponseAccessor _responseAccessor;
        private readonly RequestAccessor _requestAccessor;
        private readonly IConnectionProcessor _connectionProcessor;

        public StabilityAiImagesAgent(
            IConnectionProcessor connectionProcessor,
            ILogger<ApiCallAgent> logger,
            ExtendedConfig extendedConfig,
            IHttpClientFactory httpClientFactory,
            ResponseAccessor responseAccessor,
            RequestAccessor requestAccessor) : base(responseAccessor, requestAccessor, extendedConfig, logger)
        {
            _connectionProcessor = connectionProcessor;
            _httpClientFactory = httpClientFactory;
            _responseAccessor = responseAccessor;
            _requestAccessor = requestAccessor;
        }

        public override async Task<string> DoCall(AgentModel agent, Dictionary<string, string> parameters)
        {
            parameters.ToList().ForEach(p => parameters[p.Key] = HttpUtility.HtmlDecode(p.Value));
            _debugMessageSenderName = $"{agent.Name} ({agent.Type})";

            var connectionName = agent.Content[AgentContentParameters.StabilityAiConnectionName].Value;
            var connections = await _connectionProcessor.List(_requestAccessor.WorkspaceId);
            var connection = GetConnection(_requestAccessor, _responseAccessor, connections, ConnectionType.StabilityAi, _debugMessageSenderName, connectionName: connectionName);
            if (connection == null)
            {
                throw new Exception("Connection not found");
            }
            var apiKey = connection.Content["apiKey"];

            var imageProcessingType = agent.Content[AgentContentParameters.ImageProcessingType].Value;

            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Request",
                $"Image Processing Type: \r\n{imageProcessingType}");
            var result = "";
            switch (imageProcessingType)
            {
                case "generate":
                    result = await GenerateImage(apiKey, agent, parameters);
                    break;
                case "upscale":
                    result = await UpscaleImage(apiKey, agent, parameters);
                    break;
                case "edit":
                    result = await EditImage(apiKey, agent, parameters);
                    break;
                case "control":
                    result = await ControlImage(apiKey, agent, parameters);
                    break;
                default:
                    throw new Exception("Invalid image processing type");
            }
            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Response", result);
            return result;
        }

        public async Task<string> GenerateImage(string apiKey, AgentModel agent, Dictionary<string, string> parameters)
        {
            var generationService = agent.Content[AgentContentParameters.GenerationService].Value;
            var result = "";
            switch (generationService)
            {
                case "stableImageUltra":
                    result = await StableImageUltra(apiKey, agent, parameters);
                    break;
                case "stableDiffusion35":
                    result = await StableDiffusion35(apiKey, agent, parameters);
                    break;
                case "stableImageCore":
                    result = await StableImageCore(apiKey, agent, parameters);
                    break;
                default:
                    throw new Exception("Invalid upscale service");
            }
            return result;
        }

        public async Task<string> UpscaleImage(string apiKey, AgentModel agent, Dictionary<string, string> parameters)
        {
            var upscaleService = agent.Content[AgentContentParameters.UpscaleService].Value;
            var result = "";
            switch (upscaleService)
            {
                case "conservativeUpscaler":
                    result = await Conservative(apiKey, agent, parameters);
                    break;
                case "fastUpscaler":
                    result = await Fast(apiKey, agent, parameters);
                    break;
                default:
                    throw new Exception("Invalid upscale service");
            }
            return result;
        }

        public async Task<string> EditImage(string apiKey, AgentModel agent, Dictionary<string, string> parameters)
        {
            var editService = agent.Content[AgentContentParameters.EditService].Value;
            var result = "";
            switch (editService)
            {
                case "outpaint":
                    result = await Outpaint(apiKey, agent, parameters);
                    break;
                case "removeBackground":
                    result = await RemoveBackground(apiKey, agent, parameters);
                    break;
                case "searchAndRecolor":
                    result = await SearchAndRecolor(apiKey, agent, parameters);
                    break;
                case "searchAndReplace":
                    result = await SearchAndReplace(apiKey, agent, parameters);
                    break;
                default:
                    throw new Exception("Invalid edit service");
            }
            return result;
        }

        public async Task<string> ControlImage(string apiKey, AgentModel agent, Dictionary<string, string> parameters)
        {
            var controlService = agent.Content[AgentContentParameters.ControlService].Value;
            var result = "";
            switch (controlService)
            {
                case "structure":
                    result = await Structure(apiKey, agent, parameters);
                    break;
                case "sketch":
                    result = await Sketch(apiKey, agent, parameters);
                    break;
                case "style":
                    result = await Style(apiKey, agent, parameters);
                    break;
                default:
                    throw new Exception("Invalid control service");
            }
            return result;
        }

        // Generate APIs

        public async Task<string> StableImageUltra(string apiKey, AgentModel agent, Dictionary<string, string> parameters)
        {
            var image = GetImageBytes(parameters, agent, AgentContentParameters.GenerateInputImageBase64);
            var generatePrompt = ApplyParameters(agent, parameters, AgentContentParameters.GeneratePrompt);
            var promptNegative = ApplyParameters(agent, parameters, AgentContentParameters.GeneratePromptNegative);
            var aspectRatio = ApplyParameters(agent, parameters, AgentContentParameters.GenerateAspectRatio);
            var seed = ApplyParameters(agent, parameters, AgentContentParameters.GenerateSeed);
            var stylePreset = ApplyParameters(agent, parameters, AgentContentParameters.GenerateStylePreset);
            var strength = ApplyParameters(agent, parameters, AgentContentParameters.GenerateStrength);
            using var formData = new MultipartFormDataContent
            {
                ImageFormField("image", image, "image.png", "image/png"),
                StringFormField("prompt", generatePrompt),
                StringFormField("output_format", "png"),
            };
            if (!string.IsNullOrWhiteSpace(seed))
                formData.Add(StringFormField("seed", seed));
            if (!string.IsNullOrWhiteSpace(strength))
                formData.Add(StringFormField("strength", strength));
            if (!string.IsNullOrWhiteSpace(aspectRatio) && aspectRatio != "none")
                formData.Add(StringFormField("aspect_ratio", aspectRatio));
            if (!string.IsNullOrWhiteSpace(promptNegative))
                formData.Add(StringFormField("negative_prompt", promptNegative));
            if (!string.IsNullOrEmpty(stylePreset) && stylePreset != "none")
                formData.Add(StringFormField("style_preset", stylePreset));
            return await CallStabilityAi("https://api.stability.ai/v2beta/stable-image/generate/ultra", apiKey, formData);
        }

        public async Task<string> StableDiffusion35(string apiKey, AgentModel agent, Dictionary<string, string> parameters)
        {
            var image = GetImageBytes(parameters, agent, AgentContentParameters.GenerateInputImageBase64);
            var generatePrompt = ApplyParameters(agent, parameters, AgentContentParameters.GeneratePrompt);
            var mode = ApplyParameters(agent, parameters, AgentContentParameters.GenerateMode);
            var promptNegative = ApplyParameters(agent, parameters, AgentContentParameters.GeneratePromptNegative);
            var aspectRatio = ApplyParameters(agent, parameters, AgentContentParameters.GenerateAspectRatio);
            var seed = ApplyParameters(agent, parameters, AgentContentParameters.GenerateSeed);
            var stylePreset = ApplyParameters(agent, parameters, AgentContentParameters.GenerateStylePreset);
            var strength = ApplyParameters(agent, parameters, AgentContentParameters.GenerateStrength);
            var model = ApplyParameters(agent, parameters, AgentContentParameters.GenerateModel);
            using var formData = new MultipartFormDataContent
            {
                ImageFormField("image", image, "image.png", "image/png"),
                StringFormField("prompt", generatePrompt),
                StringFormField("output_format", "png"),
            };
            if (!string.IsNullOrWhiteSpace(model))
                formData.Add(StringFormField("model", model));
            if (!string.IsNullOrWhiteSpace(mode))
                formData.Add(StringFormField("mode", mode));
            if (!string.IsNullOrWhiteSpace(seed))
                formData.Add(StringFormField("seed", seed));
            if (!string.IsNullOrWhiteSpace(strength))
                formData.Add(StringFormField("strength", strength));
            if (!string.IsNullOrWhiteSpace(aspectRatio) && aspectRatio != "none")
                formData.Add(StringFormField("aspect_ratio", aspectRatio));
            if (!string.IsNullOrWhiteSpace(promptNegative))
                formData.Add(StringFormField("negative_prompt", promptNegative));
            if (!string.IsNullOrEmpty(stylePreset) && stylePreset != "none")
                formData.Add(StringFormField("style_preset", stylePreset));
            return await CallStabilityAi("https://api.stability.ai/v2beta/stable-image/generate/sd3", apiKey, formData);
        }
        
        public async Task<string> StableImageCore(string apiKey, AgentModel agent, Dictionary<string, string> parameters)
        {
            var generatePrompt = ApplyParameters(agent, parameters, AgentContentParameters.GeneratePrompt);
            var promptNegative = ApplyParameters(agent, parameters, AgentContentParameters.GeneratePromptNegative);
            var aspectRatio = ApplyParameters(agent, parameters, AgentContentParameters.GenerateAspectRatio);
            var seed = ApplyParameters(agent, parameters, AgentContentParameters.GenerateSeed);
            var stylePreset = ApplyParameters(agent, parameters, AgentContentParameters.GenerateStylePreset);
            using var formData = new MultipartFormDataContent
            {
                StringFormField("prompt", generatePrompt),
                StringFormField("output_format", "png"),
            };
            if (!string.IsNullOrWhiteSpace(seed))
                formData.Add(StringFormField("seed", seed));
            if (!string.IsNullOrWhiteSpace(aspectRatio) && aspectRatio != "none")
                formData.Add(StringFormField("aspect_ratio", aspectRatio));
            if (!string.IsNullOrWhiteSpace(promptNegative))
                formData.Add(StringFormField("negative_prompt", promptNegative));
            if (!string.IsNullOrEmpty(stylePreset) && stylePreset != "none")
                formData.Add(StringFormField("style_preset", stylePreset));
            return await CallStabilityAi("https://api.stability.ai/v2beta/stable-image/generate/core", apiKey, formData);
        }
        
        // Upscale APIs

        public async Task<string> Conservative(string apiKey, AgentModel agent, Dictionary<string, string> parameters)
        {
            var image = GetImageBytes(parameters, agent, AgentContentParameters.UpscaleInputImageBase64);
            var upscalePrompt = ApplyParameters(agent, parameters, AgentContentParameters.UpscalePrompt);
            var promptNegative = ApplyParameters(agent, parameters, AgentContentParameters.UpscalePromptNegative);
            var upscaleCreativity = ApplyParameters(agent, parameters, AgentContentParameters.UpscaleCreativity);
            using var formData = new MultipartFormDataContent
            {
                ImageFormField("image", image, "image.png", "image/png"),
                StringFormField("prompt", upscalePrompt),
                StringFormField("output_format", "png")
            };
            if (!string.IsNullOrWhiteSpace(promptNegative))
                formData.Add(StringFormField("negative_prompt", promptNegative));
            if (!string.IsNullOrWhiteSpace(upscaleCreativity))
                formData.Add(StringFormField("creativity", upscaleCreativity));
            return await CallStabilityAi("https://api.stability.ai/v2beta/stable-image/upscale/conservative", apiKey, formData);
        }


        public async Task<string> Fast(string apiKey, AgentModel agent, Dictionary<string, string> parameters)
        {
            var image = GetImageBytes(parameters, agent, AgentContentParameters.UpscaleInputImageBase64);
            using var formData = new MultipartFormDataContent
            {
                ImageFormField("image", image, "image.png", "image/png"),
                StringFormField("output_format", "png")
            };
            return await CallStabilityAi("https://api.stability.ai/v2beta/stable-image/upscale/fast", apiKey, formData);
        }

        // Edit APIs

        public async Task<string> Outpaint(string apiKey, AgentModel agent, Dictionary<string, string> parameters)
        {
            var image = GetImageBytes(parameters, agent, AgentContentParameters.EditInputImageBase64);
            var outpaintDirection = ApplyParameters(agent, parameters, AgentContentParameters.EditOutpaintDirection);
            var outpaintDirectionParts = outpaintDirection.Split(';', ',');
            using var formData = new MultipartFormDataContent
            {
                ImageFormField("image", image, "image.png", "image/png"),
                StringFormField("output_format", "png")
            };
            foreach (var outpaintDirectionPart in outpaintDirectionParts)
            {
                var parts = outpaintDirectionPart.Split('=', ':');
                formData.Add(StringFormField(parts[0].Trim(), parts[1].Trim()));
            }
            return await CallStabilityAi("https://api.stability.ai/v2beta/stable-image/edit/outpaint", apiKey, formData);
        }

        public async Task<string> RemoveBackground(string apiKey, AgentModel agent, Dictionary<string, string> parameters)
        {
            var image = GetImageBytes(parameters, agent, AgentContentParameters.EditInputImageBase64);
            using var formData = new MultipartFormDataContent
            {
                ImageFormField("image", image, "image.png", "image/png"),
                StringFormField("output_format", "png")
            };
            return await CallStabilityAi("https://api.stability.ai/v2beta/stable-image/edit/remove-background", apiKey, formData);
        }
        
        public async Task<string> SearchAndRecolor(string apiKey, AgentModel agent, Dictionary<string, string> parameters)
        {
            var image = GetImageBytes(parameters, agent, AgentContentParameters.EditInputImageBase64);
            var promptToAdd = ApplyParameters(agent, parameters, AgentContentParameters.EditPromptToAdd);
            var promptToReplace = ApplyParameters(agent, parameters, AgentContentParameters.EditPromptToReplace);
            var promptNegative = ApplyParameters(agent, parameters, AgentContentParameters.EditPromptNegative);
            using var formData = new MultipartFormDataContent
            {
                ImageFormField("image", image, "image.png", "image/png"),
                StringFormField("output_format", "png"),
                StringFormField("select_prompt", promptToReplace),
                StringFormField("prompt", promptToAdd),
            };
            if (!string.IsNullOrWhiteSpace(promptNegative))
                formData.Add(StringFormField("negative_prompt", promptNegative));
            return await CallStabilityAi("https://api.stability.ai/v2beta/stable-image/edit/search-and-recolor", apiKey, formData);
        }

        public async Task<string> SearchAndReplace(string apiKey, AgentModel agent, Dictionary<string, string> parameters)
        {
            var image = GetImageBytes(parameters, agent, AgentContentParameters.EditInputImageBase64);
            var promptToAdd = ApplyParameters(agent, parameters, AgentContentParameters.EditPromptToAdd);
            var promptToReplace = ApplyParameters(agent, parameters, AgentContentParameters.EditPromptToReplace);
            var promptNegative = ApplyParameters(agent, parameters, AgentContentParameters.EditPromptNegative);
            using var formData = new MultipartFormDataContent
            {
                ImageFormField("image", image, "image.png", "image/png"),
                StringFormField("output_format", "png"),
                StringFormField("search_prompt", promptToReplace),
                StringFormField("prompt", promptToAdd),
            };
            if (!string.IsNullOrWhiteSpace(promptNegative))
                formData.Add(StringFormField("negative_prompt", promptNegative));
            return await CallStabilityAi("https://api.stability.ai/v2beta/stable-image/edit/search-and-replace", apiKey, formData);
        }

        // Control APIs

        public async Task<string> Structure(string apiKey, AgentModel agent, Dictionary<string, string> parameters)
        {
            var image = GetImageBytes(parameters, agent, AgentContentParameters.ControlInputImageBase64);
            var controlPrompt = ApplyParameters(agent, parameters, AgentContentParameters.ControlPrompt);
            var controlStrength = ApplyParameters(agent, parameters, AgentContentParameters.ControlStrength);
            var promptNegative = ApplyParameters(agent, parameters, AgentContentParameters.ControlPromptNegative);
            var controlStylePreset = ApplyParameters(agent, parameters, AgentContentParameters.ControlStylePreset);
            using var formData = new MultipartFormDataContent
            {
                ImageFormField("image", image, "image.png", "image/png"),
                StringFormField("output_format", "png"),
                StringFormField("control_strength", controlStrength),
                StringFormField("prompt", controlPrompt),
            };
            if (!string.IsNullOrWhiteSpace(promptNegative))
                formData.Add(StringFormField("negative_prompt", promptNegative));
            if (!string.IsNullOrEmpty(controlStylePreset) && controlStylePreset != "none")
                formData.Add(StringFormField("style_preset", controlStylePreset));
            return await CallStabilityAi("https://api.stability.ai/v2beta/stable-image/control/structure", apiKey, formData);
        }
        
        public async Task<string> Sketch(string apiKey, AgentModel agent, Dictionary<string, string> parameters)
        {
            var image = GetImageBytes(parameters, agent, AgentContentParameters.ControlInputImageBase64);
            var controlPrompt = ApplyParameters(agent, parameters, AgentContentParameters.ControlPrompt);
            var controlStrength = ApplyParameters(agent, parameters, AgentContentParameters.ControlStrength);
            var promptNegative = ApplyParameters(agent, parameters, AgentContentParameters.ControlPromptNegative);
            var controlStylePreset = ApplyParameters(agent, parameters, AgentContentParameters.ControlStylePreset);
            using var formData = new MultipartFormDataContent
            {
                ImageFormField("image", image, "image.png", "image/png"),
                StringFormField("output_format", "png"),
                StringFormField("prompt", controlPrompt),
                StringFormField("control_strength", controlStrength),
            };
            if (!string.IsNullOrWhiteSpace(promptNegative))
                formData.Add(StringFormField("negative_prompt", promptNegative));
            if(!string.IsNullOrEmpty(controlStylePreset) && controlStylePreset != "none")
                formData.Add(StringFormField("style_preset", controlStylePreset));
            return await CallStabilityAi("https://api.stability.ai/v2beta/stable-image/control/sketch", apiKey, formData);
        }

        public async Task<string> Style(string apiKey, AgentModel agent, Dictionary<string, string> parameters)
        {
            var image = GetImageBytes(parameters, agent, AgentContentParameters.ControlInputImageBase64);
            var controlPrompt = ApplyParameters(agent, parameters, AgentContentParameters.ControlPrompt);
            var promptNegative = ApplyParameters(agent, parameters, AgentContentParameters.ControlPromptNegative);
            var controlStylePreset = ApplyParameters(agent, parameters, AgentContentParameters.ControlStylePreset);
            var controlAspectRatio = ApplyParameters(agent, parameters, AgentContentParameters.ControlAspectRatio);
            var controlFidelity = ApplyParameters(agent, parameters, AgentContentParameters.ControlFidelity);
            using var formData = new MultipartFormDataContent
            {
                ImageFormField("image", image, "image.png", "image/png"),
                StringFormField("output_format", "png"),
                StringFormField("prompt", controlPrompt),
            };
            if (!string.IsNullOrWhiteSpace(promptNegative))
                formData.Add(StringFormField("negative_prompt", promptNegative));
            if (!string.IsNullOrEmpty(controlStylePreset) && controlStylePreset != "none")
                formData.Add(StringFormField("style_preset", controlStylePreset));
            if (!string.IsNullOrEmpty(controlAspectRatio) && controlAspectRatio != "none")
                formData.Add(StringFormField("aspect_ratio", controlAspectRatio));
            if (!string.IsNullOrEmpty(controlFidelity))
                formData.Add(StringFormField("fidelity", controlFidelity));
            return await CallStabilityAi("https://api.stability.ai/v2beta/stable-image/control/style", apiKey, formData);
        }

        // API end

        private string ApplyParameters(AgentModel agent, Dictionary<string, string> parameters, string key) => 
            !agent.Content.ContainsKey(key) ? "" : ApplyParameters(agent.Content[key].Value, parameters);

        private async Task<string> CallStabilityAi(string url, string apiKey, MultipartFormDataContent formData)
        {
            using var httpRequestMessage = new HttpRequestMessage(HttpMethod.Post, new Uri(url));
            httpRequestMessage.Headers.Add("Authorization", $"Bearer {apiKey}");
            httpRequestMessage.Headers.Add("Accept", "image/*");
            httpRequestMessage.Content = formData;
            using var httpClient = _httpClientFactory.CreateClient("NoRetryClient");
            using var response = await httpClient.SendAsync(httpRequestMessage);
            if (!response.IsSuccessStatusCode)
            {
                var errorText = await response.Content.ReadAsStringAsync();
                throw new BadHttpRequestException($"Request failed with status code {response.StatusCode}: {errorText}");
            }
            var responseBody = Convert.ToBase64String(await response.Content.ReadAsByteArrayAsync());
            return responseBody;
        }

        private static StringContent StringFormField(string name, string value)
        {
            var formField = new StringContent(value);
            formField.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
            {
                Name = $"\"{name}\""
            };
            return formField;
        }

        private static ByteArrayContent ImageFormField(string name, byte[] imageBytes, string filename, string mimeType)
        {
            var formField = new ByteArrayContent(imageBytes);
            formField.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
            formField.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
            {
                Name = $"\"{name}\"",
                FileName = $"\"{filename}\""
            };
            return formField;
        }

        private byte[] GetImageBytes(Dictionary<string, string> parameters, AgentModel agent, string? parameterName)
        {
            var imageString = ApplyParameters(agent, parameters, parameterName);
            if (string.IsNullOrEmpty(imageString) && !_requestAccessor.MessageDialog!.Messages!.Last().HasFiles())
                throw new Exception("Image not found");
            var image = string.IsNullOrEmpty(imageString)
                ? Convert.FromBase64String(_requestAccessor.MessageDialog.Messages!.Last().Files!.First().Base64Data.StripBase64())
                : Convert.FromBase64String(imageString);
            return image;
        }
    }

    public interface IStabilityAiImagesAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
    }
}
