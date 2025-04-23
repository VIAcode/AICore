namespace AiCoreApi.Common
{
    public class ExceptionHandlingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<ExceptionHandlingMiddleware> _logger;

        public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task Invoke(HttpContext context)
        {
            try
            {
                await _next(context);
            }
            catch (AiCoreUiException ex)
            {
                _logger.LogError(ex, "AiCoreUiException exception occurred.");
                await HandleUiExceptionAsync(context, ex);
            }
            catch (AiCoreAuthException ex)
            {
                await HandleAuthExceptionAsync(context, ex);
            }
            catch (OperationCanceledException ex)
            {
                // Do nothing. This is expected when the request is canceled.
            }
        }

        private static Task HandleUiExceptionAsync(HttpContext context, AiCoreUiException exception)
        {
            var response = new { message = exception.Message };
            context.Response.ContentType = "application/json";
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return context.Response.WriteAsJsonAsync(response);
        }

        private static Task HandleAuthExceptionAsync(HttpContext context, AiCoreAuthException exception)
        {
            var response = new { message = exception.Message };
            context.Response.ContentType = "application/json";
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return context.Response.WriteAsJsonAsync(response);
        }

        public class AiCoreUiException : Exception
        {
            public AiCoreUiException(string message) : base(message)
            {
            }
        }

        public class AiCoreAuthException : Exception
        {
            public AiCoreAuthException(string message) : base(message)
            {
            }
        }
    }
}
