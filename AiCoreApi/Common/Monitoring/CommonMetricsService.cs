using AiCoreApi.Models.DbModels;

namespace AiCoreApi.Common.Monitoring;

public class CommonMetricsService: ICommonMetricsService
{
    private readonly IMetricsAccessor _metricsAccessor;

    public CommonMetricsService(IMetricsAccessor metricsAccessor)
    {
        _metricsAccessor = metricsAccessor;
    }

    public void AddIncomingTokens(long tokens, ConnectionModel connection, LoginModel login)
    {
        string modelDeploymentName = GetModelDeployment(connection);

        _metricsAccessor.SetCounterValue("tokens_incoming", tokens, unit:"Token", description:"Tokens incoming",         
            "user", login.Login,
            "model", modelDeploymentName,
            "connection", connection.Name
        );
    }

    public void AddOutgoingTokens(long tokens, ConnectionModel connection, LoginModel login)
    {
        string modelDeploymentName = GetModelDeployment(connection);

        _metricsAccessor.SetCounterValue("tokens_outgoing", tokens, unit: "Token", description: "Tokens outgoing",
            "user", login.Login,
            "model", modelDeploymentName,
            "connection", connection.Name
        );
    }

    private static string GetModelDeployment(ConnectionModel connection) => connection.Type switch
    {
        ConnectionType.AzureOpenAiLlm => connection.Content["deploymentName"],
        ConnectionType.DeepSeekLlm => connection.Content["modelName"],
        ConnectionType.OpenAiLlm => connection.Content["modelName"],
        _ => "default"
    };
}

public interface ICommonMetricsService
{
    void AddIncomingTokens(long tokens, ConnectionModel connection, LoginModel login);
 
    void AddOutgoingTokens(long tokens, ConnectionModel connection, LoginModel login);
}