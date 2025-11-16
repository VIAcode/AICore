using System.Text;
using AiCoreApi.Models.ViewModels;

namespace AiCoreApi.SemanticKernel.Agents
{
    public abstract class BaseCompositeAgent : BaseEnabledAgentsAgent
    {

        public BaseCompositeAgent(
            IBaseAgentHelper baseAgentHelper,
            ILogger<BaseEnabledAgentsAgent> logger)
            : base(baseAgentHelper, logger)
        {
        }
        protected string GetParametersDefinition(List<ParameterRecordModel> parameterRecordModels)
        {
            var result = new StringBuilder();
            result.AppendLine();
            for (var i = 0; i < parameterRecordModels.Count; i++)
            {
                var param = parameterRecordModels[i];
                result.AppendLine($@"  - Param{i + 1}");
                result.AppendLine($@"    Name: {param.Name}");
                if (!string.IsNullOrEmpty(param.Description))
                    result.AppendLine($@"    Description: {param.Description}");
                if (!string.IsNullOrEmpty(param.Type))
                    result.Append($@"    DataType: {param.Type}");
                if (param.EnumValues != null && param.EnumValues.Any())
                    result.Append($@" [{string.Join(", ", param.EnumValues)}]");
                if (param.CanBeNull.HasValue && param.CanBeNull.Value)
                    result.Append($@" can be null");
                result.AppendLine();
            }
            return result.ToString();
        }
    }
}
