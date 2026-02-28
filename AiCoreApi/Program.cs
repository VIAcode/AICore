using AiCoreApi.Common;
using AiCoreApi.Common.Data;
using AiCoreApi.Services.ProcessingServices;
using AiCoreApi.Services.UpdateServices;

namespace AiCoreApi;

public class Program
{
    public static void Main(string[] args)
    {
        // First update dependencies or wait for other service instance to update
        new UpdateService(args).Update();
        // init all services / dependencies but not run them
        CreateHostBuilder(args).Build().Run();
    }

    public static IHostBuilder CreateHostBuilder(string[] args)
    {

        var hostBuilder =
              // when we call to create a new migration we should not call 
              // adding those services in Startup Or hosted services because
              // they are expect calling settings in database just in time
              // creating configuration
              IsDesignTime()
              ? Host.CreateDefaultBuilder(args)
                  .ConfigureServices(services =>
                  {
                      var config = new Config();
                      services.AddSingleton(config);
                      services.AddTransient<IDbQuery, DbQuery>();
                      services.AddTransient<IDataSourceProvider, DataSourceProvider>();
                      services.AddTransient<Db>();
                  })
              : Host.CreateDefaultBuilder(args)
                  .ConfigureWebHostDefaults(builder => builder.UseStartup<Startup>())
                  .ConfigureServices(services =>
                  {
                      services.AddHostedService<TaskProcessingHostedService>();
                      services.AddHostedService<IngestionSchedulerHostedService>();
                      services.AddHostedService<InstanceSyncHostedService>();
                      services.AddHostedService<BackgroundWorkingHostedService>();
                  });

        return hostBuilder;
    }

    /// <summary>
    /// This solution found in internet published by community member
    /// see: https://github.com/dotnet/efcore/issues/27306  (search for: foriequal0 commented on May 25, 2022)
    /// </summary>
    public static bool IsDesignTime()
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Length < 1)
        {
            return false;
        }
        var arg = args[0];
        // win or linux (not sure about linux ef.so though)
        return Path.GetFileName(arg) == "ef.dll" || Path.GetFileName(arg) == "ef.so";
    }
}