
namespace HistoryWebApi;

public class Program
{
    public static async Task Main()
    {
        await Launch();
        while(true) await Task.Delay(int.MaxValue);
    }
    public static async Task Launch()
    {
        var builder = WebApplication.CreateBuilder();

        // Add services to the container.

        builder.Services.AddControllers().AddApplicationPart(typeof(Program).Assembly);//ensure controller can be found
        // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
        builder.Services.AddOpenApi();

        var app = builder.Build();

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        app.UseAuthorization();


        app.MapControllers();

        await app.StartAsync();
    }
}
