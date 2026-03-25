using Newtonsoft.Json.Serialization;
using TestProject.Services;

namespace TestProject
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Configure logging explicitly
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole();
            builder.Logging.AddDebug();

            // Enable Debug level for FileSystemService specifically
            builder.Logging.AddFilter("TestProject.Controllers.FileSystemController", LogLevel.Debug);
            builder.Logging.AddFilter("TestProject.Services.FileSystemService", LogLevel.Debug);

            // Add services to the container with Newtonsoft.Json
            builder.Services.AddControllers()
                .AddNewtonsoftJson(options =>
                {
                    options.SerializerSettings.ContractResolver = new DefaultContractResolver
                    {
                        NamingStrategy = new SnakeCaseNamingStrategy()
                    };
                });

            builder.Services.AddMemoryCache();
            builder.Services.AddScoped<IFileSystemService, FileSystemService>();

            // Configure file upload limits
            builder.Services.Configure<IISServerOptions>(options =>
            {
                options.MaxRequestBodySize = 100_000_000; // 100MB
            });

            var app = builder.Build();

            // Configure the HTTP request pipeline
            app.UseHttpsRedirection();
            app.UseStaticFiles();
            app.MapControllers();

            app.Run();
        }
    }
}